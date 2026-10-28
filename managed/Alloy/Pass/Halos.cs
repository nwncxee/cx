using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Light flares: one view-aligned billboard per authored flare element, the chain vanilla lays along
/// the line from the screen centre through the light. Population and layout are vanilla's, re-decided each frame
/// at the enrol slot (<see cref="Flares"/>); an element draws its own texture, and where none is resident the
/// procedural halo profile a hak can override. Vanilla's near-plane quad becomes a billboard of world size.</summary>
internal static unsafe partial class Halos
{
    internal const string ShdHaloVs = "cx_halo_vs", ShdHaloFs = "cx_halo_fs";
    /// <summary>The SSBO binding point the draw reads this frame's records from.</summary>
    private const uint BindHalos = SsboSlots.Halos;
    private const int InitialHalos = 64;
    /// <summary><c>ManageSceneBSP</c> @0x47fa80 gathers the frame's light candidates around the scene focus out to
    /// this or the fog end, whichever is further, past each light's own adjusted radius.</summary>
    private const float MinFocusReach = 45f;
    /// <summary>The soft range, in metres, of the per-pixel cut and the probe: a surface this far in front of the
    /// light starts to hide it.</summary>
    private const float OcclusionSoftness = 0.6f;

    private static ExtenderLog? _log;

    /// <summary>What staging knows about an element beside its GPU record: the light's rig id, the texture it
    /// draws with and whether it leads its light. One record, so the sort moves them together.</summary>
    private struct Staged
    {
        /// <summary>The flare's light, as <c>DbLight.Id</c> names it -- the Lit row or part index, which
        /// unlike the engine's <c>PartLight*</c> is never recycled and is never zero. Elements of one light
        /// share it, so they share one fade; a ring sample is matched back through it.</summary>
        public IntPtr Key;
        /// <summary>The GL name this element draws with, 0 for the procedural profile. Also the batch key:
        /// the list is sorted on it, so one draw serves every element sharing a texture.</summary>
        public uint Tex;
        /// <summary>Is this the light's first staged element -- the one whose record carries the probe flag?</summary>
        public bool Lead;
    }

    /// <summary>This frame's flare elements, grouped by texture, and the area's resolved flare textures.
    /// One owner: the area edge replaces it, which is what releases those textures.</summary>
    private sealed class HaloStageState
    {
        public HaloRec[] Rec = new HaloRec[InitialHalos];
        public Staged[] At = new Staged[InitialHalos];
        public int Count;
        /// <summary>Elements an authored chain named a texture for but no size, position or colour shift --
        /// vanilla reads past the end of the short array; nothing is drawn here, and this is the count.</summary>
        public int Short;
        /// <summary>One <c>TexRegistry</c> reference per flare resref the area named, so a frame costs a
        /// dictionary probe rather than an acquire.</summary>
        public readonly Dictionary<string, Db.TexId> Textures = new();
    }

    private static HaloStageState _st = new();

    private static bool _off;
    private static int _drawn, _groups, _bare;

    /// <summary>The dials are <see cref="Effects"/>' <c>halos</c> (the brightness, and 0 is the pass off) /
    /// <c>halosize</c> / <c>haloviz</c>: one live knob each, so a chat set is what the next launch reads.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        log.Info($"Halos: armed -- flare lights from the scene database drawn as their authored element chain (/effects; size {Effects.HaloSize.V:F2} gain {Effects.Halos.V:F2})");
    }

    /// <summary><c>RenderLensFlares</c> @0x471860 draws a flare, and integrates its fade, only while the camera
    /// stands within its <c>flareradius</c>.</summary>
    private static bool Reaches(Vector3 pos, float flareRadius)
        => !FrameInputs.HasCamera || (pos - FrameInputs.CameraPos).LengthSquared() <= flareRadius * flareRadius;

    /// <summary>The halo population: every light of <paramref name="scene"/> that <see cref="Flares"/> admits, read
    /// off the database the way <c>DbLights</c> reads the rig; <paramref name="radiusMult"/> is the rig's own.</summary>
    internal static void EnrollDb(Db.DbId scene, float radiusMult)
    {
        _st.Count = 0;
        if (_off || !Effects.HalosOn) return;
        int lits = Db.SceneDb.Lights.High, parts = Db.DbInputs.LightCount;
        Vector3 focus = Db.SceneDb.SceneFocus[scene.Index];
        float fogEnd = Db.SceneDb.SceneFogFar[scene.Index];
        for (int lit = 1; lit < lits; lit++)
            if (Db.DbInputs.LitLight(lit, out Db.DbLight l)) StageDb(in l, scene, radiusMult, focus, fogEnd);
        for (int k = 0; k < parts; k++)
            if (Db.DbInputs.Light(k, out Db.DbLight l) && l.Lit < 0) StageDb(in l, scene, radiusMult, focus, fogEnd);
        Batch();
    }

    /// <summary>How many elements this light node actually authored: <c>RenderLensFlares</c> @0x471860 loops
    /// the resolved texture array and indexes the other three with the same subscript, reading past the end of
    /// a short one. Alloy draws what the model named -- the shortest of the four.</summary>
    internal static int Elements(LightDef def)
        => Math.Min(Math.Min(def.FlareTextures.Length, def.FlareSizes.Length),
                    Math.Min(def.FlarePositions.Length, def.FlareColors.Length));

    private static void StageDb(in Db.DbLight l, Db.DbId scene, float radiusMult, Vector3 focus, float fogEnd)
    {
        if (!l.Enabled || !Db.SceneDb.Objects.Valid(l.Obj) || Db.SceneDb.ObjScene[l.Obj.Index].Value != scene.Value) return;
        LightDef? def = Db.DbInputs.LightDefOf(in l);
        if (def == null || !Flares(def, l.Position, l.Colour, l.Radius, l.Multiplier, radiusMult, focus, fogEnd)) return;
        // RenderLensFlares @0x471860 skips a light standing in a tile still under fog of war
        int tile = Db.TileBuild.At(scene, l.Position);
        if (tile >= 0 && Db.SceneDb.TileFogged[tile]) return;
        if (!Reaches(l.Position, def.FlareRadius)) return;
        int n = Elements(def);
        _st.Short += def.FlareTextures.Length - n;
        bool lead = true;
        for (int i = 0; i < n; i++)
        {
            float size = def.FlareSizes[i];
            if (size <= 0f) continue;
            Reserve(_st.Count + 1);
            ref HaloRec d = ref _st.Rec[_st.Count];
            d.Pos = l.Position;
            d.Softness = OcclusionSoftness;
            d.Tint = Tint(l.Colour, def.FlareColors[i]);
            d.FlareRadius = def.FlareRadius;
            d.Size = size * Effects.HaloSize.V;
            d.FlarePosition = def.FlarePositions[i];
            d.Probe = new Vector2(lead ? 1f : 0f, 0f);
            _st.At[_st.Count] = new Staged { Key = l.Id, Tex = TexName(def.FlareTextures[i]), Lead = lead };
            lead = false;
            _st.Count++;
        }
    }

    /// <summary>Room for <paramref name="want"/> staged elements. Growth doubles, so an area settles on its
    /// own size within a frame or two and the staging loop allocates nothing after that.</summary>
    private static void Reserve(int want)
    {
        if (want <= _st.Rec.Length) return;
        int cap = Math.Max(want, _st.Rec.Length * 2);
        Array.Resize(ref _st.Rec, cap);
        Array.Resize(ref _st.At, cap);
    }

    /// <summary>This element's flare texture, 0 until one is resident: a preview would draw the white
    /// stand-in as a flare, so an element waits on its own artwork behind the procedural profile.</summary>
    private static uint TexName(string resref)
    {
        if (resref.Length == 0) return 0;
        if (!_st.Textures.TryGetValue(resref, out Db.TexId id)) _st.Textures[resref] = id = Db.TexRegistry.Acquire(resref);
        return Db.TexRegistry.TryResident(id, out uint name, out _) ? name : 0u;
    }

    /// <summary>Sort the staged elements by texture so each GL name is bound once and drawn as one instanced
    /// range. Insertion sort: tens of elements, already nearly grouped, and it moves no memory on a frame
    /// whose lights have not changed.</summary>
    private static void Batch()
    {
        for (int i = 1; i < _st.Count; i++)
        {
            HaloRec r = _st.Rec[i];
            Staged a = _st.At[i];
            int j = i - 1;
            while (j >= 0 && _st.At[j].Tex > a.Tex) { _st.Rec[j + 1] = _st.Rec[j]; _st.At[j + 1] = _st.At[j]; j--; }
            _st.Rec[j + 1] = r; _st.At[j + 1] = a;
        }
    }

    /// <summary>Does vanilla flare this light? <c>GetLensFlareLights</c> @0x4c1d80 walks only the frame's candidates,
    /// which <c>SetupLightsForThisPositionAndRadius</c> @0x4c1df0 admits when isdynamic or affectdynamic, radius in
    /// (0, 1000), colour not black, multiplier above 0 and the scene focus within the adjusted radius plus
    /// max(45, fog end); of those it keeps lights with flare colour shifts, and the draw loops their textures.</summary>
    internal static bool Flares(LightDef def, Vector3 pos, Vector3 colour, float radius, float multiplier,
                                float radiusMult, Vector3 focus, float fogEnd)
    {
        if (def.FlareColors.Length <= 0 || def.FlareTextures.Length <= 0) return false;
        if (!def.Dynamic && !def.AffectDynamic) return false;
        if (!(radius > 0f && radius < EngineLayout.DirectionalRange) || !(multiplier > 0f)) return false;
        if (colour == Vector3.Zero) return false;
        Db.DbLights.Adjust(radius, multiplier, radiusMult, ref colour, out float adjR);
        float reach = adjR + MathF.Max(MinFocusReach, fogEnd);
        return (pos - focus).LengthSquared() < reach * reach;
    }

    /// <summary>A staged halo's colour: the RAW light colour plus the node's shift, as <c>RenderLensFlares</c>
    /// @0x471860 sums them -- display space and unclamped above, because vanilla clamps each channel only after
    /// the view weights the vertex stage applies. NaN stages as 0.</summary>
    internal static Vector3 Tint(Vector3 colour, Vector3 shift)
    {
        Vector3 v = colour + shift;
        return new Vector3(Finite(v.X), Finite(v.Y), Finite(v.Z));
    }

    /// <summary>A channel floored at 0 (no weight lifts a negative one past vanilla's clamp) and capped finite: NaN
    /// reads 0, and an Inf cannot reach the vertex stage's weights as Inf x 0.</summary>
    private static float Finite(float c) => c > 0f ? MathF.Min(c, 1e6f) : 0f;

    /// <summary>World edge: this area's staged elements go, and the flare textures they held go with them.</summary>
    internal static void Revoke()
    {
        foreach (Db.TexId id in _st.Textures.Values) Db.TexRegistry.Release(id);
        _st = new HaloStageState();
    }

    internal static void ResetPeriod() { _drawn = 0; _groups = 0; _bare = 0; _st.Short = 0; }
    internal static string Stats() => _off ? "halos unavailable" : !Effects.HalosOn ? "halos off"
        : $"halos {_st.Count} flare elements this frame; this period {_drawn} element draws in {_groups} texture "
          + $"groups ({_bare} on the procedural profile, their texture not yet resident)"
          + (_st.Short > 0 ? $", {_st.Short} element draws skipped for a texture with no size/position/shift" : "");
    internal static string Describe() => $"{(_off ? "UNAVAILABLE" : Effects.HalosOn ? "on" : "off")} size {Effects.HaloSize.V:F2} gain {Effects.Halos.V:F2} viz {Effects.HaloViz.V}";

    /// <summary>The occlusion integrator, the tint, the population gate and the chain length, headless: too slow
    /// a fade smears flares through walls, too loose a gate lights flares vanilla never draws. Oracle: hand-written
    /// values read off <c>RenderLensFlares</c> and <c>SetupLightsForThisPositionAndRadius</c>, a stock tile lamp's
    /// authored flare block, and the embedded GLSL text.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // The staged colour is vanilla's sum, raw: the clamp waits for the vertex stage's view weights.
        check.Expect(Tint(new Vector3(255f, 0f, 0f), Vector3.Zero) == new Vector3(255f, 0f, 0f), "an overbright channel stages raw, not clamped");
        Vector3 summed = Tint(new Vector3(0.5f, 0f, -2f), new Vector3(0.6f, 0f, 0f));
        check.Expect(MathF.Abs(summed.X - 1.1f) < 1e-5f && summed.Y == 0f && summed.Z == 0f, "colour + shift is summed and floored at 0");
        check.Expect(Tint(new Vector3(float.NaN, 1f, 1f), Vector3.Zero).X == 0f, "a NaN colour stages as 0");
        Vector3 inf = Tint(new Vector3(float.PositiveInfinity, 0.5f, 0f), Vector3.Zero);
        check.Expect(float.IsFinite(inf.X) && inf.Y == 0.5f, "an infinite colour stages finite");
        Vector3 torch = Tint(new Vector3(1.75f, 1.5f, 1f), new Vector3(0f, 0f, 0.5f));
        check.Expect(torch == new Vector3(1.75f, 1.5f, 1.5f), $"the shift joins the raw colour ({torch.X:F3}, {torch.Y:F3}, {torch.Z:F3})");
        // The population gate, against hand-written cases of SetupLightsForThisPositionAndRadius's predicate. The
        // flare node is the stock tile lamp's shape: isdynamic 0, affectdynamic 1, a blue first colour shift.
        var lamp = new LightDef { Dynamic = false, AffectDynamic = true, FlareTextures = new[] { "fxpa_lensflare" },
                                  FlareColors = new[] { new Vector3(0f, 0f, 1f) } };
        Vector3 at = new(10f, 0f, 0f), warm = new(1f, 0.8f, 0.5f);
        check.Expect(Flares(lamp, at, warm, 5f, 1f, 1f, Vector3.Zero, 40f), "a lit flare lamp beside the focus flares");
        check.Expect(!Flares(lamp, at, warm, 0f, 1f, 1f, Vector3.Zero, 40f), "a lamp at radius 0 (a window before Night) does not");
        check.Expect(!Flares(lamp, at, warm, 5f, 0f, 1f, Vector3.Zero, 40f), "nor one at multiplier 0");
        check.Expect(!Flares(lamp, at, Vector3.Zero, 5f, 1f, 1f, Vector3.Zero, 40f), "nor a black one, whatever its colour shift");
        check.Expect(!Flares(lamp, at, warm, 1000f, 1f, 1f, Vector3.Zero, 40f), "nor one reaching 1000, the area pair's range");
        var bakedOnly = new LightDef { Dynamic = false, AffectDynamic = false, FlareTextures = lamp.FlareTextures, FlareColors = lamp.FlareColors };
        check.Expect(!Flares(bakedOnly, at, warm, 5f, 1f, 1f, Vector3.Zero, 40f), "nor one neither dynamic nor affecting dynamics");
        var noShift = new LightDef { AffectDynamic = true, FlareTextures = lamp.FlareTextures };
        check.Expect(!Flares(noShift, at, warm, 5f, 1f, 1f, Vector3.Zero, 40f), "nor one with textures but no colour shifts");
        // reach to the focus: radius 5 x multiplier 1 + max(45, fog end)
        Vector3 far = new(49.9f, 0f, 0f), farther = new(50.1f, 0f, 0f);
        check.Expect(Flares(lamp, far, warm, 5f, 1f, 1f, Vector3.Zero, 40f) && !Flares(lamp, farther, warm, 5f, 1f, 1f, Vector3.Zero, 40f),
                     "the focus reach is the adjusted radius plus 45 under a nearer fog end");
        check.Expect(Flares(lamp, new Vector3(64.9f, 0f, 0f), warm, 5f, 1f, 1f, Vector3.Zero, 60f), "...and plus the fog end past 45");
        // The chain length, against a stock outdoor lamp's authored block: texturenames 4 (lensflare, lensring,
        // lensring, lensflare), flaresizes 5 5 7 2, flarepositions 1 0.5 1.5 0.4, four colour shifts.
        var chain = new LightDef { AffectDynamic = true, FlareRadius = 50f,
                                   FlareTextures = new[] { "fxpa_lensflare", "fxpa_lensring", "fxpa_lensring", "fxpa_lensflare" },
                                   FlareSizes = new[] { 5f, 5f, 7f, 2f },
                                   FlarePositions = new[] { 1f, 0.5f, 1.5f, 0.4f },
                                   FlareColors = new[] { new Vector3(0f, 0f, 1f), new Vector3(-0.2f), new Vector3(-0.1f), new Vector3(-0.7f) } };
        check.Expect(Elements(chain) == 4, $"a four-element lamp stages four quads, not one ({Elements(chain)})");
        var short1 = new LightDef { FlareTextures = chain.FlareTextures, FlareSizes = chain.FlareSizes,
                                    FlarePositions = new[] { 1f }, FlareColors = chain.FlareColors };
        check.Expect(Elements(short1) == 1, "an element no flarepositions entry names is not drawn");
        check.Expect(Elements(noShift) == 0, "nor is one with textures and nothing else");
        // The draw is one instanced range per texture group, so the stage indexes from the group's first
        // element; the fragment stage samples the element's own texture and shifts its depth read to the light.
        string vs = ShaderStages.Source(ShdHaloVs), fs = ShaderStages.Source(ShdHaloFs);
        check.Expect(vs.Contains("(uFirst + gl_InstanceID) * 3"), "the vertex stage indexes from the group's first element");
        check.Expect(fs.Contains("uniform sampler2D uFlare;") && fs.Contains("texture(uFlare, vUV * 0.5 + 0.5)"),
                     "the fragment stage samples the element's authored texture");
        check.Expect(vs.Contains("out vec2 vOcc;") && fs.Contains("+ vOcc * 0.5"),
                     "and reads the scene depth at the light rather than under the element");
        log.Info(check.Ok ? "Halos.SelfCheck: passed" : "Halos.SelfCheck: FAILED -- see above");
    }
}
