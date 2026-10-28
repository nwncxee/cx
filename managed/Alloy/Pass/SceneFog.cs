using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The scene fog's TARGET COLOUR, and the terms that shape it. The area author still owns the ramp, read
/// untouched off <c>Scene+0x140..0x14c</c>, so every knob here changes only what a fully-fogged fragment
/// converges TO: the SKY (the dome texture's mean, role-weighted, or a native stage's cx_fog texel), which removes the
/// horizon seam vanilla paints over. ponytail: one colour for the whole dome, not a gradient by elevation.
/// </summary>
internal static unsafe class SceneFog
{
    /// <summary>The dials are <see cref="Fog"/>'s, read through these so <c>fog.enabled</c> off makes every
    /// one read as 0 -- vanilla's flat colour, view-depth metric and no cull -- without a branch at each site.
    /// <c>sky</c> 0 is vanilla, which is what every knob here reduces to; <c>floor</c> is an offset from the
    /// player's feet, upward only, because an area has no ground plane to read.</summary>
    internal static float SkyMix => Fog.Enabled.V ? Fog.Sky.V : 0f;
    internal static float SunAmount => Fog.Enabled.V ? Fog.Sun.V : 0f;
    internal static float SunExp => Fog.SunExp.V;
    internal static float HeightK => Fog.Enabled.V ? Fog.Height.V : 0f;
    internal static float FloorOffset => Fog.Floor.V;
    internal static float CullZ => Fog.CullZ.V;
    /// <summary>The volumetric fog's wall is radial from the player by construction, so it measures from there whatever
    /// <c>fog.origin</c> says; its sky is the volume's, so the band is only what a capture's ramp falls back to.</summary>
    internal static bool OriginPlayer => FogVolume.On || (Fog.Enabled.V && Fog.Origin.V);
    internal static float SkyBand => FogVolume.On ? FogVolume.CaptureBand : Fog.Enabled.V ? Fog.SkyBand.V : 0f;
    /// <summary>The cull's fog gate (<see cref="CullPass.FogCull"/>): <c>fog.cull</c> while either fog draws, off with
    /// both masters. The volume hides the far plane past <see cref="OpaqueDistance()"/> as the ramp does.</summary>
    internal static bool Cull => (FogVolume.On || Fog.Enabled.V) && Fog.Cull.V;

    /// <summary>The sky colour the fog converges to, LINEAR. Zero until a sky texture has been read, which
    /// is what <see cref="_haveSky"/> reports and what makes the term stand down to the author's colour.
    /// </summary>
    // _haveSky FIRST: ilc 10.0.11 fails codegen (recordRelocation InvalidCastException) on a Vector3 static at offset 0.
    private static bool _haveSky;
    private static Vector3 _sky;
    private static Vector3 _acc; private static float _accW;

    /// <summary>The sky average, LINEAR, once one has been read: <see cref="EnvProbe"/>'s clear where the capture
    /// draws no dome -- a hosted content sky (a cloud stage) is never in it.</summary>
    internal static bool TrySky(out Vector3 sky) { sky = _sky; return _haveSky; }

    /// <summary>A new area, or a wiped part table: the sky colour belongs to a dome that may not be in the
    /// next one. The GL names are the engine's, so this is a Revoke and not a ContextLost.</summary>
    internal static void Revoke()
    {
        _haveSky = false; _sky = default;
    }

    /// <summary>Fold one staged sky part into this frame's average: the dome part's texture is a registry
    /// slot, whose mean the loader took off the decoded image. Called for every part that is NOT the
    /// fog-tinted fade band -- the band is painted in the fog colour, so counting it would feed the fog its
    /// own output, and without the rest the sky term never arms at all.</summary>
    internal static void NoteSkyDb(Db.TexId id, float alpha)
    {
        if (alpha <= 0f || !Db.TexRegistry.TryMean(id, out Vector3 m)) return;
        _acc += new Vector3(Lin(m.X), Lin(m.Y), Lin(m.Z)) * alpha; _accW += alpha;
    }

    /// <summary>Close the frame's sky accounting. A frame that staged no sky at all (an interior, or a sky
    /// pass standing down) leaves the last known colour in place rather than snapping the fog: the roles are
    /// re-staged every pass and one refused frame is not a new sky.</summary>
    internal static void EndSky()
    {
        // A native stage's cx_fog texel, read back, is the colour outright while its stage runs: a hosted stage's
        // textures say nothing about its colour, and a texel a shader names for the purpose says exactly that.
        if (StageResources.FogColour(out Vector3 named)) { _sky = named; _haveSky = true; _acc = default; _accW = 0f; return; }
        if (_accW <= 0f) { _acc = default; return; }
        _sky = _acc / _accW; _haveSky = true;
        _acc = default; _accW = 0f;
    }

    private static float Lin(float v) => MathF.Pow(MathF.Max(v, 0f), AlloyLights.SrgbGamma);
    private static float Disp(float v) => MathF.Pow(MathF.Max(v, 0f), 1f / AlloyLights.SrgbGamma);

    /// <summary>Push this pass's fog terms at one program. <paramref name="playerZ"/> anchors the height
    /// layer, and <paramref name="capture"/> stands the sky term down for an <see cref="EnvProbe"/> face:
    /// the capture is a scene draw like any other, so fogging it toward the sky and then reading that cube
    /// back as the sky would be the renderer sampling its own output.</summary>
    internal static void Upload(in AlloyRenderer.FeatLoc loc, float playerZ, bool capture)
    {
        // NO SKY, NO SKY TERM. An interior has a fog colour its author picked and nothing overhead to
        // match it against -- converging a cave's fog on some stand-in would be this feature overruling the
        // author exactly where it has no evidence. The sun lobe goes with it, because AreaDiffuse is the
        // directional light indoors too and a cave does not have a sun in its fog.
        bool useSky = _haveSky && !capture && !FrameInputs.Blind;
        float* v = stackalloc float[4];
        v[0] = useSky ? SkyMix : 0f; v[1] = HeightK; v[2] = playerZ + FloorOffset;
        LooseBlock.Set(loc.FogSky, new ReadOnlySpan<float>(v, 3));
        Vector3 sky = _sky;
        v[0] = sky.X; v[1] = sky.Y; v[2] = sky.Z;
        LooseBlock.Set(loc.FogSkyColor, new ReadOnlySpan<float>(v, 3));

        Vector3 dir = FrameInputs.AreaDiffuseDir;
        float len = dir.Length();
        v[0] = len > 0f ? dir.X / len : 0f; v[1] = len > 0f ? dir.Y / len : 0f;
        v[2] = len > 0f ? dir.Z / len : 1f; v[3] = MathF.Max(SunExp, 1f);
        LooseBlock.Set(loc.FogSun, new ReadOnlySpan<float>(v, 4));
        Vector3 sun = FrameInputs.HasAreaDiffuse && len > 0f && useSky
            ? FrameInputs.AreaDiffuse * (AlloyLights.SunScale * SunAmount) : default;
        v[0] = sun.X; v[1] = sun.Y; v[2] = sun.Z;
        LooseBlock.Set(loc.FogSunColor, new ReadOnlySpan<float>(v, 3));
    }

    /// <summary>The display-space colour the skyfade gob is painted in: what fully fogged geometry converges to at the
    /// HORIZON -- the volume's direction-averaged light while it draws, else the ramp's target (vanilla's raw fog colour
    /// at sky 0), since a band in any other colour is the seam it was invented to hide. ponytail: elevation only, so
    /// at a low sun the band is a little flat on the side away from it.</summary>
    internal static void SkyfadeTint(float* rgb)
    {
        Vector3 c = FrameInputs.FogColor;   // display space, as the Scene block stores it
        if (FogVolume.Horizon(out Vector3 v)) c = new Vector3(Disp(v.X), Disp(v.Y), Disp(v.Z));
        else if (SkyMix > 0f && _haveSky && !FrameInputs.Blind)
        {
            Vector3 lin = new Vector3(Lin(c.X), Lin(c.Y), Lin(c.Z));
            // The shader's own horizon weight at dir.z == 0 is 1.0 (smoothstep(-0.35, 0.0, 0.0)), and the
            // in-scatter lobe is dropped with the azimuth it needs.
            lin = Vector3.Lerp(lin, _sky, SkyMix);
            c = new Vector3(Disp(lin.X), Disp(lin.Y), Disp(lin.Z));
        }
        rgb[0] = c.X; rgb[1] = c.Y; rgb[2] = c.Z;
    }

    /// <summary>The distance past which a fragment is FULLY fogged, and therefore the distance the cull may
    /// throw geometry away at (<see cref="CullPass.FogCull"/>); infinity stands the fog cull down. With the
    /// plain ramp that distance IS <c>FogEnd</c>, measured the way the fog is; the height falloff breaks that,
    /// so the cut is placed for geometry <see cref="CullZ"/> above the layer floor and higher is traded off.</summary>
    internal static float OpaqueDistance() => OpaqueDistance(FrameInputs.FogOn, FrameInputs.FogStart, FrameInputs.FogEnd);

    private static float OpaqueDistance(bool on, float start, float end)
    {
        if (FogVolume.On) return FogVolume.OpaqueDistance(on, start, end, VolumetricFog.Haze.V);
        if (!on || end <= 0f) return float.PositiveInfinity;
        if (HeightK <= 0f) return end;
        float span = end - start;
        if (span <= 0f) return end;
        return start + span * MathF.Exp(HeightK * MathF.Max(CullZ, 0f));
    }

    /// <summary>A world view's far plane, pushed out to where its fog is opaque. The engine puts the far plane at the
    /// fog's end, which hides the cut under full fog in its own metric, view depth; measured from the player, a point
    /// on that plane is up to <paramref name="eyeToPlayer"/> nearer and would be cut while still showing. Negative
    /// <paramref name="eyeToPlayer"/> = no player, and the fog is measured from the eye like the engine's.</summary>
    internal static float FarForFog(float far, bool on, float start, float end, float eyeToPlayer)
    {
        if (!OriginPlayer || eyeToPlayer < 0f) return far;
        float opaque = OpaqueDistance(on, start, end);
        return float.IsInfinity(opaque) ? far : MathF.Max(far, opaque + eyeToPlayer);
    }

    /// <summary>Where the fog is measured from. <paramref name="radial"/> is true when it is the player's
    /// feet (a sphere); false means the engine's view depth and the point returned is the pass eye, which is
    /// what a radial test against it degrades to.</summary>
    internal static Vector3 Origin(out bool radial)
    {
        Vector3 p = AlloyRenderer.PlayerPos;
        radial = OriginPlayer && p != default;
        return radial ? p : AlloyRenderer.EyePos;
    }

    internal static string Describe()
    {
        float opaque = OpaqueDistance();
        return (FogVolume.On ? "SUPERSEDED: the volumetric fog draws (vfog.enabled)  " : "")
             + (Fog.Enabled.V ? "" : "OFF (author's flat fog; every dial reads 0)  ") + $"sky {SkyMix:F2} ({(_haveSky ? "sky colour" : "NO SKY COLOUR -- author colour only")} " +
               $"{_sky.X:F2},{_sky.Y:F2},{_sky.Z:F2})  sun {SunAmount:F2} exp {SunExp:F1}  " +
               $"height {HeightK:F3}/unit floor {FloorOffset:F1}  " +
               $"[opaque {(float.IsInfinity(opaque) ? "--" : opaque.ToString("F0"))} " +
               $"cullz {CullZ:F0} camfar {FrameInputs.CamFar:F0}]  origin {(OriginPlayer ? "player" : "camera")} skyband {SkyBand:F2}";
    }

    /// <summary>Off-device: the two pieces that are arithmetic rather than GL. The cull distance is the one
    /// that can silently delete a world -- it is a plane the cull shader trusts -- and the tint is the one
    /// that can silently paint a band the wrong colour.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        bool onWas = FrameInputs.FogOn; float sWas = FrameInputs.FogStart, eWas = FrameInputs.FogEnd;
        Vector3 cWas = FrameInputs.FogColor;
        float kWas = Fog.Height.V, mixWas = Fog.Sky.V, zWas = Fog.CullZ.V, sunWas = Fog.Sun.V, bandWas = Fog.SkyBand.V;
        bool haveWas = _haveSky, enWas = Fog.Enabled.V, cullWas = Fog.Cull.V, volWas = VolumetricFog.Enabled.V; Vector3 skyWas = _sky;
        Fog.Enabled.V = true; VolumetricFog.Enabled.V = false;

        FrameInputs.FogOn = false;
        check.Expect(float.IsPositiveInfinity(OpaqueDistance()), "fog off means no cull distance at all");
        FrameInputs.FogOn = true; FrameInputs.FogStart = 25f; FrameInputs.FogEnd = 125f;
        Fog.Height.V = 0f;
        check.Expect(MathF.Abs(OpaqueDistance() - 125f) < 1e-3f, "the plain ramp is opaque at FogEnd, as #336 culled");
        Fog.Height.V = 0.01f; Fog.CullZ.V = 30f;
        // 25 + 100*exp(0.3) = 159.9: further out than FogEnd, never nearer.
        check.Expect(OpaqueDistance() > 125f, "height falloff pushes the opaque distance OUT, never in");

        Fog.Sky.V = 0f; _haveSky = true; _sky = new Vector3(1f, 1f, 1f);
        FrameInputs.FogColor = new Vector3(0.5f, 0.25f, 0.125f);
        float* rgb = stackalloc float[3];
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[0] - 0.5f) < 1e-4f && MathF.Abs(rgb[2] - 0.125f) < 1e-4f,
          "sky 0 leaves the skyfade band on the author's own fog colour, byte for byte");
        Fog.Sky.V = 1f; _sky = new Vector3(Lin(0.25f), Lin(0.5f), Lin(1f));
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[2] - 1f) < 1e-3f && MathF.Abs(rgb[0] - 0.25f) < 1e-3f,
          "sky 1 lands the band exactly on the sky colour, round-tripped through the encode");

        Fog.Sky.V = 1f; _haveSky = false;
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[0] - 0.5f) < 1e-4f,
          "no sky staged leaves the band on the author's colour whatever the mix says");

        // The far plane: a 45 m engine clip on a 10..45 ramp, the camera 15 m from the player, is cut at 60 m.
        bool originWas = Fog.Origin.V;
        Fog.Height.V = 0f; Fog.Origin.V = true;
        check.Expect(MathF.Abs(FarForFog(45f, true, 10f, 45f, 15f) - 60f) < 1e-3f,
          "fog measured from the player pushes the far plane out to the fog's end plus the camera's distance");
        check.Expect(FarForFog(135f, true, 10f, 45f, 15f) == 135f, "...and never pulls a farther engine plane in");
        check.Expect(FarForFog(45f, false, 10f, 45f, 15f) == 45f && FarForFog(45f, true, 10f, 45f, -1f) == 45f,
          "...and leaves it alone with the fog off or no player");
        Fog.Origin.V = false;
        check.Expect(FarForFog(45f, true, 10f, 45f, 15f) == 45f, "...and with the fog measured from the eye, as the engine's");
        Fog.Origin.V = originWas;

        // /fog origin 0 is the engine's metric: view depth, measured from the pass eye -- not a sphere
        // around wherever the player happens to stand.
        // Both arms of the gate: off-device the pass eye and the player position are both default, so an
        // assertion on the POINT would hold whichever arm ran. The flag is what says which.
        bool origWas = Fog.Origin.V;
        Fog.Origin.V = false;
        Origin(out bool radial);
        check.Expect(!radial, "origin 0 is view depth from the pass eye, never a sphere round the player");
        Fog.Origin.V = true;
        Origin(out radial);
        check.Expect(!radial, "...and origin 1 with no player position staged degrades to view depth, not to the world origin");
        Fog.Origin.V = origWas;

        // The master: off, every dial reads as 0 -- the author's flat colour, view depth, no cull -- whatever
        // the knobs hold, and the skyfade band is the author's colour byte for byte.
        _haveSky = true; Fog.Sky.V = 1f; Fog.Sun.V = 1f; Fog.Height.V = 0.01f; Fog.SkyBand.V = 0.5f; Fog.Cull.V = true;
        Fog.Enabled.V = false;
        check.Expect(SkyMix == 0f && SunAmount == 0f && HeightK == 0f && SkyBand == 0f && !OriginPlayer && !Cull && !CullPass.FogCull,
          "fog.enabled off: every dial reads 0 and the cull stands down");
        check.Expect(MathF.Abs(OpaqueDistance() - 125f) < 1e-3f, "...and the opaque distance is the plain ramp's");
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[0] - 0.5f) < 1e-4f && MathF.Abs(rgb[2] - 0.125f) < 1e-4f, "...and the skyfade band is the author's colour");
        Fog.Enabled.V = true;
        check.Expect(SkyMix == 1f && SunAmount == 1f && HeightK == 0.01f && SkyBand == 0.5f && OriginPlayer && Cull, "fog.enabled on restores the dials");
        Fog.Origin.V = origWas; Fog.Sun.V = sunWas; Fog.SkyBand.V = bandWas; Fog.Cull.V = cullWas;

        FrameInputs.FogOn = onWas; FrameInputs.FogStart = sWas; FrameInputs.FogEnd = eWas;
        FrameInputs.FogColor = cWas;
        Fog.Height.V = kWas; Fog.Sky.V = mixWas; Fog.CullZ.V = zWas; Fog.Enabled.V = enWas; _haveSky = haveWas; _sky = skyWas;
        VolumetricFog.Enabled.V = volWas;
        log.Info(check.Ok ? "SceneFog.SelfCheck: passed" : "SceneFog.SelfCheck: FAILED");
    }
}
