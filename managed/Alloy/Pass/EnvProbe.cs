using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The per-area image-based lighting probe: a small prefiltered colour cubemap captured from the
/// Alloy scene draw, the ambient-specular source of every Alloy lit part, in LINEAR RGBA16F. It replaces the
/// engine's pseudo-spheremap, which squashes the AREA's authored env map over a view-space normal -- which is
/// why indoor metal reflects an outdoor sky -- so the scene env map is never bound by the Alloy pass.
/// </summary>
internal static class EnvProbe
{
    /// <summary>Face resolution. Small on purpose -- this is an irradiance-ish source, not a mirror, and the
    /// whole cube is one 6 x 128^2 RGBA16F allocation (~0.8MB with mips). <c>light.probesize</c> sets it: it
    /// sizes an allocation, so it is a Restart knob read through Boot.</summary>
    internal static int Size { get; private set; } = 128;
    /// <summary>Levels in the mip chain: roughness 1 maps onto the last one.</summary>
    internal static int Mips { get; private set; }
    /// <summary>A capture has COMPLETED. Until then the Alloy pass feeds no ambient specular at all, never
    /// a half-written cube.</summary>
    internal static bool Ready { get; private set; }
    /// <summary>World point the live cube was captured from: the parallax correction's centre, and the one the
    /// "has the player walked out of this probe" test measures from.</summary>
    internal static Vector3 Origin { get; private set; }
    /// <summary>Captures this session -- the [alloy] line prints it, and a number that climbs with the frame
    /// count is the failure this ticket's third acceptance criterion is about.</summary>
    internal static int Captures { get; private set; }

    /// <summary>Texture unit the probe binds on: above the material slots, which are above the two shadow maps
    /// and the diffuse. It must stay above them -- a samplerCube and a sampler2D on ONE unit is
    /// INVALID_OPERATION and the draw is dropped, silently as far as the frame is concerned. See
    /// <see cref="TextureSlots"/> for the whole ladder, which asserts that ordering.</summary>
    internal const uint ProbeUnit = TextureSlots.Probe;

    /// <summary>Near plane of a face frustum. Small: the probe sits at the camera, and the geometry that
    /// matters (the room around it) starts close.</summary>
    private const float FaceNear = 0.25f;

    /// <summary>Frames between captures. Not a quality dial -- a floor that keeps a pathological dirty
    /// signal (a scene pointer that churns, a player running a boundary) from turning the probe into a
    /// per-frame pass.</summary>
    private const int MinFrames = 120;
    /// <summary>World distance from <see cref="Origin"/> that re-captures. Areas are large and one probe
    /// cannot serve all of one, so the probe follows the player in coarse steps.</summary>
    private const float MoveDist = 30f;

    /// <summary>UBO binding of the SH9 block (<c>cx_material</c>'s <c>CxProbeSh</c>).</summary>
    internal const uint ShBinding = UboSlots.ProbeSh;
    /// <summary>SSBO binding <c>cx_probe_sh</c> writes the same buffer through; the dispatch unbinds it after
    /// (<see cref="SsboSlots.ProbeSh"/> says why that is safe).</summary>
    private const uint ShOutBinding = SsboSlots.ProbeSh;
    /// <summary>The light-settle test's memory: the area's point-light count and how many frames it has stood.</summary>
    private sealed class SettleState { public int Lights = -1, Frames; }
    private static SettleState _settle = new();
    /// <summary>Frames the point-light count must stand before a capture: a server streams the area's lights
    /// after it places the player, and a cube captured before they land mirrors a lamp-less world.</summary>
    private const int LightSettleFrames = 60;
    /// <summary>Have the area's point lights stopped arriving? <paramref name="count"/> is this frame's
    /// upload; a change restarts the count.</summary>
    internal static bool LightsSettled(int count)
    {
        if (count != _settle.Lights) { _settle.Lights = count; _settle.Frames = 0; return false; }
        return ++_settle.Frames >= LightSettleFrames;
    }
    /// <summary><c>light.probe</c>: gates <see cref="Wanted"/>, which is re-asked every frame, so the probe
    /// idles rather than un-arming and a re-enable captures at the next edge.</summary>
    private static bool _on => Lighting.Probe.V;
    private static int _lastFrame = -MinFrames;
    private static ExtenderLog? _log;
    private static float _far = 500f;

    /// <summary><c>/light probegain</c>: how much presence the ambient-specular term gets over the engine's own
    /// weighting. 1.0 IS the engine's weighting, and higher is a deliberate departure -- the term multiplies
    /// the light already arriving at the fragment, so raising it far past 1 puts the wet-stone look back.</summary>
    internal static float Gain => Lighting.ProbeGain.V;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        Size = Lighting.ProbeSize.Boot;
    }

    /// <summary>Off-device (run-demo.sh): the world edge drops what it must and keeps what it must.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        var check = new Checker(log);

        try
        {
            // #209: the world edge, which the probe had no slot for at all. Both halves matter -- dropping
            // Ready without the key would re-bake once and then trust the stale key again, and dropping the
            // key without Ready leaves the cube readable until the player walks MoveDist.
            // CX-46: every probe fetch maps roughness the way cx_probe_ggx baked it -- level i is perceptual
            // roughness i / (levels - 1), so a site on any other curve samples the wrong lobe.
            string ggx = ShaderSource.Get("cx_probe_ggx.glsl");
            check.Expect(ggx.Contains("float perceptual = float(uLevel) / float(max(levels - 1, 1));")
                         && ggx.Contains("float a = perceptual * perceptual"), "the prefilter bakes perceptual roughness per level");
            foreach (string stage in new[] { "cx_ownlit_tail.glsl", "cx_ownlit_light.glsl", "cx_grass_fmain.glsl", "cx_gblade_fmain.glsl" })
            {
                string src = ShaderSource.Get(stage);
                // the one exemption: the legacy env pattern's MEAN, read at the top mip on purpose
                int fetches = Count(src, "uProbe,"), mapped = Count(src, "CxProbeFetch(uProbe, vWorld, R, sqrt(")
                              + Count(src, "CxProbeFetch(uProbe, vWorld, reflect(-V, N), sqrt("),
                    mean = Count(src, "CxProbeFetch(uProbe, vWorld, R, uProbeMips - 1.0)");
                check.Expect(fetches > 0 && fetches == mapped + mean && Count(src, "* (uProbeMips - 1.0))") == mapped,
                             $"{stage}: every probe fetch is parallax-corrected on the prefilter's sqrt(rough) * (uProbeMips - 1) mapping");
            }
            string mat = ShaderSource.Get("cx_material.glsl"), sh = ShaderSource.Get("cx_probe_sh.glsl"),
                   ssr = ShaderSource.Get("cx_ssr.glsl");
            check.Expect(!mat.Contains("textureLod(uProbe"), "the ambient shape reads the SH9 block, not the cube");
            check.Expect(ssr.Contains($"layout(std140, binding = {ShBinding}) uniform CxProbeSh {{ vec4 uProbeSh[9]; vec4 uProbeAt; }}")
                         && sh.Contains("buffer CxProbeShOut { vec4 uShOut[9]; vec4 uShAt; }")
                         && sh.Contains($"layout(std430, binding = {ShOutBinding}) writeonly buffer"),
                         "the SH9 block's two bindings match the C# side");

            Ready = true;
            Revoke();
            check.Expect(!Ready, "the world edge drops the cube's freshness, so a new area recaptures");
            _settle = new();
            bool early = LightsSettled(5);
            for (int i = 0; i < 58; i++) LightsSettled(5);
            check.Expect(!early && !LightsSettled(6) && !LightsSettled(6), "a light count that just changed is not settled");
            for (int i = 0; i < 58; i++) LightsSettled(6);
            check.Expect(LightsSettled(6), "...and one that stood sixty frames is");
            _settle = new();

            string sky = ShaderSource.Get("cx_sky_f.glsl");
            check.Expect(sky.Contains("mix(clamp(FragmentColor, 0.0, 1.0), vec4(0.0), isnan(FragmentColor))")
                         && sky.Contains("CxOitOut(vec4(rgb, c.a))"),
                         "a hosted sky stage is clamped as the engine's UNORM target clamps it, NaN to 0");
        }
        finally { _log = quiet; }
        log.Info(check.Ok ? "EnvProbe.SelfCheck: passed" : "EnvProbe.SelfCheck: FAILED");
    }

    /// <summary>Is a capture due? True only on the edges that change what the probe should contain: never
    /// captured (a new area: the world edge's <see cref="Revoke"/> drops <see cref="Ready"/>), or the player
    /// has walked <see cref="MoveDist"/> out of the last one. The frame floor sits in front of both.</summary>
    internal static bool Wanted(Vector3 eye, int frame)
    {
        if (!_on || LightmapBake.Held) return false;
        if (frame - _lastFrame < MinFrames) return false;
        return !Ready || Vector3.DistanceSquared(eye, Origin) > MoveDist * MoveDist;
    }

    /// <summary>Levels in a <paramref name="size"/>-square chain down to one texel.</summary>
    internal static int MipsOf(int size)
    {
        int n = 1;
        for (int s = size; s > 1; s >>= 1) n++;
        return n;
    }

    /// <summary>Open a capture: latch what this one is of, with the cube and the faces the caller's.</summary>
    internal static void BeginVk(Vector3 eye, int frame, float far)
    {
        Origin = eye; _lastFrame = frame;
        _far = far > FaceNear * 4f ? far : FaceNear * 4f;
        Ready = false;
        Mips = MipsOf(Size);
    }

    /// <summary>Face <paramref name="face"/>'s exactly-90&deg; view-projection: the same face basis the shadow
    /// cubes render and sample with, so the reflection vector round-trips.</summary>
    internal static Matrix4x4 FaceVp(int face) => ShadowMath.PointLightFaceViewProj(Origin, face, FaceNear, _far);

    /// <summary>Close a capture whose prefilter and SH9 the caller recorded: published. No readback -- the
    /// capture's stages clamp a non-finite colour to zero before it reaches the chain.</summary>
    internal static void EndVk()
    {
        Captures++;
        Ready = true;
        _log?.Info($"EnvProbe: captured area probe #{Captures} at ({Origin.X:F0},{Origin.Y:F0},{Origin.Z:F0}) " +
                   $"-- {Size}px x 6 faces, {Mips} mips (Vulkan)");
    }

    /// <summary>A device edge: the cube went with the device, so the next capture builds a fresh one.</summary>
    internal static void Forget() => Ready = false;

    /// <summary>The world edge. The cube describes an area that has gone away, and this is the only thing
    /// that asks for a new one; the device's targets survive and the next capture reuses them.</summary>
    internal static void Revoke() => Ready = false;

    internal static (float near, float far) FacePlanes => (FaceNear, _far);

    /// <summary>Occurrences of <paramref name="what"/> in <paramref name="s"/>, for SelfCheck's source contracts.</summary>
    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
