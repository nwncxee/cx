using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>lod</c> group: automatic mesh detail. Touched from <see cref="RenderCommands.Register"/>
/// so the group declares at Initialize on the main thread, as every other Alloy group does.</summary>
internal static class Lod
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Px1, Px2, Bias;

    static Lod()
    {
        G = KnobGroup.Declare("lod", "Graphics", "Quality", master: "Automatic mesh detail",
            masterHelp: "Distant walls and floors are drawn from a simplified copy of their own mesh. "
                      + "Off draws every model at full detail at every distance, which is what the game does.",
            masterTier: Tier.ChatOnly, order: 304, title: "Model detail",
            masterChanged: CasterPass.ShadowInvalidate);
        G.Status = MeshLod.Describe;
        Px1 = G.Slider("px1", "Full detail above", 50f, 2000f, 10f, 500f, "px", Tier.Advanced,
                       help: "A mesh wider than this on screen is drawn exactly as authored.",
                       changed: CasterPass.ShadowInvalidate)
               .Ladder(Quality.Detail, 800f, 650f, 500f, 300f);
        Px2 = G.Slider("px2", "Lowest detail below", 10f, 1000f, 10f, 150f, "px", Tier.Advanced,
                       help: "Below this a mesh is drawn from its smallest copy. Between the two it takes the middle one.",
                       changed: CasterPass.ShadowInvalidate)
               .Ladder(Quality.Detail, 300f, 220f, 150f, 80f);
        // every row here feeds the SHADOW views' thresholds too, and no static bake is keyed on them
        Bias = G.Slider("bias", "Detail bias", 0.25f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                        help: "Scales both thresholds' effect: above 1 keeps detail further away, below 1 drops it sooner.",
                        changed: CasterPass.ShadowInvalidate);
    }
}

/// <summary>
/// Automatic mesh LOD: the thresholds and the level picker. NWN models carry no LOD chain, so a mesh draws its
/// full triangle count at every distance; <c>MeshArena</c> generates two simplified INDEX RUNS per eligible mesh
/// -- roughly half and a fifth of its triangles -- beside the base run, and <see cref="Level"/> picks one by the
/// part's projected screen radius. Reindex only: an absent level falls back to the finer run.
/// </summary>
internal static class MeshLod
{
    /// <summary>Detail levels: the engine's own run, then the two generated ones.</summary>
    internal const int Levels = 3;
    /// <summary>Triangle fraction each generated level aims at.</summary>
    internal const float Target1 = 0.5f, Target2 = 0.2f;
    /// <summary>Error budget per level as a fraction of the mesh's squared bounding-box diagonal. A collapse
    /// past it is refused, so a mesh whose shape cannot survive thinning keeps the triangles it has.</summary>
    internal const float Error1 = 0.0004f, Error2 = 0.004f;
    /// <summary>Triangles under which a mesh is not worth a level of its own: two extra runs and a batch cut
    /// cost more than the triangles they save.</summary>
    internal const int MinTris = 200;
    /// <summary>A generated run has to retire this fraction of the triangles to be kept at all; otherwise the
    /// level is left absent and the draw stays on the engine's range.</summary>
    internal const float MinSaving = 0.15f;

    private static readonly long[] _drawnTris = new long[Levels];

    /// <summary>The master knob alone: the staging walk picks a level whenever this is on, because
    /// <see cref="MeshChart"/>'s levels live in its own arena and never arm this class's buffer.</summary>
    internal static bool On => Lod.G.Master is { V: true };

    /// <summary>Declare the group and say which way the detail pick is set. Once, from the pass roll-call.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _ = Lod.G;
        log.Info(On ? "MeshLod: armed -- eligible static meshes draw the mesh arena's two generated index runs by distance"
                    : "MeshLod: lod.enabled=0 -- every mesh draws as authored at every distance");
    }

    /// <summary>The detail level a sphere of world radius <paramref name="r"/> at <paramref name="dist"/> from
    /// the eye earns. <paramref name="scale"/> is the cull pass's pixels-per-unit-radius-at-unit-depth; the
    /// distance stands in for the clip w the GPU test uses, which is exact on the view axis and conservative
    /// (one level sooner at most) off it.</summary>
    internal static int Level(float r, float dist, float scale)
    {
        if (scale <= 0f || dist <= 0f || r <= 0f) return 0;
        float px = 2f * r * scale / dist * MathF.Max(Lod.Bias.V, 0.01f);
        if (px >= Lod.Px1.V) return 0;
        return px >= MathF.Min(Lod.Px2.V, Lod.Px1.V) ? 1 : 2;
    }

    /// <summary>The two bound thresholds a SHADOW view's detail pick compares a caster against:
    /// <see cref="Level"/>'s curve with <paramref name="texel"/>, the view's world units per shadow texel, in
    /// place of the screen projection. 0/0 = every caster bakes the base run (detail off, or a view with no
    /// texel size of its own).</summary>
    internal static void ShadowThresholds(float texel, out float r1, out float r2)
    {
        r1 = 0f; r2 = 0f;
        // a non-finite texel (a view with no map size yet) must fall to the BASE run, not past both
        // thresholds into the coarsest one, which is what an infinite texel divides out to
        if (!On || !(texel > 0f) || !float.IsFinite(texel)) return;
        // px = bound * k, so the two pixel thresholds divide straight through into bounds
        float k = 2f * MathF.Max(Lod.Bias.V, 0.01f) / texel;
        r1 = Lod.Px1.V / k;
        r2 = MathF.Min(Lod.Px2.V, Lod.Px1.V) / k;
    }

    /// <summary>The level a bound of <paramref name="q"/> earns against them -- a radius for a cascade, a
    /// radius over the distance from the light for a cube face, whose texels grow with it. The expression
    /// both gather dispatches spell, so a check can run the ladder the GPU runs.</summary>
    internal static int ShadowLevel(float q, float r1, float r2) => q >= r1 ? 0 : q >= r2 ? 1 : 2;

    /// <summary>Period-line tally: triangles submitted at each level. Called from the batch walk, which is
    /// single-threaded by the time it runs.</summary>
    internal static void Drew(int lod, int indices) => _drawnTris[lod] += indices / 3;

    /// <summary>The world edge: a new area's triangles are its own, so the per-level tally restarts.</summary>
    internal static void Revoke() => Array.Clear(_drawnTris);

    /// <summary>The settings panel's status block.</summary>
    internal static string Describe()
        => !On ? "off -- every mesh draws as authored"
               : (MeshChart.LodNote() is { Length: > 0 } c ? c : "no mesh simplified yet") + DrawnNote();

    /// <summary>The last period's triangles drawn per level, kept for the panel after <see cref="Stats"/> clears the tally.</summary>
    private static string DrawnNote()
        => _shown[0] + _shown[1] + _shown[2] == 0 ? ""
         : $"; drawn per frame at lod 0/1/2: {_shown[0]}/{_shown[1]}/{_shown[2]} triangles";

    private static readonly long[] _shown = new long[Levels];

    /// <summary>One clause of the Alloy period line: what the frame actually drew at each level.</summary>
    internal static string Stats(int frames)
    {
        long t0 = _drawnTris[0], t1 = _drawnTris[1], t2 = _drawnTris[2];
        Array.Clear(_drawnTris);
        if (frames <= 0) frames = 1;
        _shown[0] = t0 / frames; _shown[1] = t1 / frames; _shown[2] = t2 / frames;
        if (t0 + t1 + t2 == 0) return "";
        return $"lod {t0 / frames}/{t1 / frames}/{t2 / frames} tris per frame at lod 0/1/2";
    }

    /// <summary>Off-device: the level a mesh arena range serves at each requested detail, and the two
    /// thresholds a shadow view's texel size divides into. Oracles: the hand-built arena fixture's own index
    /// counts, and arithmetic worked out by hand for the thresholds.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        if (!MeshSimplify.SelfCheck(log)) return false;
        var check = new Checker(log);

        // CX-50: the colour draw's level substitution, over two REAL arena ranges. The oracle is the fixture:
        // index runs of 12/6/3 shorts, so a hit at a level is that level's own hand-written count, and the
        // range that was given no coarse run must serve its base run at ANY requested level -- never nothing.
        ushort[] full = { 0, 1, 2, 0, 2, 3, 1, 2, 3, 0, 1, 3 }, half = { 0, 1, 2, 1, 2, 3 }, fifth = { 0, 1, 3 };
        var verts = new float[4 * MeshChart.Stride];
        int three = Db.MeshArena.AcquireRaw(0xC50_0001, Db.MeshArena.Fmt.Static, 4, verts,
                                            full, 12, half, 6, fifth, 3, default, default);
        int one = Db.MeshArena.AcquireRaw(0xC50_0002, Db.MeshArena.Fmt.Static, 4, verts,
                                          full, 12, null, 0, null, 0, default, default);
        nint ro = 0; int rb = 0, rc = 0, rl = 0;
        check.Expect(AlloyRenderer.ArenaLevelRuns(three, 2, ref ro, ref rb, ref rc, ref rl) && rc == 3 && rl == 2,
          "a level-2 draw does not take the level-2 run");
        nint roTwo = ro;
        rc = 0; rl = 0;
        check.Expect(AlloyRenderer.ArenaLevelRuns(three, 1, ref ro, ref rb, ref rc, ref rl) && rc == 6 && rl == 1,
          "a level-1 draw does not take the level-1 run");
        check.Expect(ro != roTwo, "two levels of one mesh draw from the same byte offset");
        rc = 0; rl = 0;
        check.Expect(AlloyRenderer.ArenaLevelRuns(one, 2, ref ro, ref rb, ref rc, ref rl) && rc == 12 && rl == 0,
          "a mesh with no coarse run does not fall back to its base run");
        rc = 0; rl = 0;
        check.Expect(!AlloyRenderer.ArenaLevelRuns(-1, 2, ref ro, ref rb, ref rc, ref rl) && rc == 0,
          "a slot with no arena range substituted a run anyway");
        // CX-50: ...and the ORDER the colour draw applies them in. A chart that has not landed must still
        // fall through to the level run: a bakeable part (most of a scene) whose chart is absent or refused
        // took the chart branch, missed, and kept full detail. Slot 0 holds no DB part here, so DbBakeable
        // refuses and the fall-through is the path taken -- the same path a chart MISS takes, by
        // construction: DbChart returning false is the only other way into it.
        check.Expect(!AlloyRenderer.DbSlot(0), "the order check's stand-in slot holds a DB part");
        ro = 0; rb = 0; rc = 0; rl = 0;
        AlloyRenderer.DbRuns(0, three, 2, chartArmed: true, ref ro, ref rb, ref rc, ref rl);
        check.Expect(rc == 3 && rl == 2, "a chart that has not landed does not fall through to the level run");
        rc = 0; rl = 0;
        AlloyRenderer.DbRuns(0, three, 0, chartArmed: false, ref ro, ref rb, ref rc, ref rl);
        check.Expect(rc == 0, "a draw at level 0 substituted a run");
        rc = 0; rl = 0;
        AlloyRenderer.DbRuns(0, -1, 2, chartArmed: false, ref ro, ref rb, ref rc, ref rl);
        check.Expect(rc == 0, "a slot with no recipe substituted a run");

        // CX-50: ...and the runs a shadow view picks among, in INDICES (the indirect command takes elements,
        // where the arena stores bytes). The fixture is the oracle twice over: the two counts are its own,
        // and its runs were appended back to back, so level 2 starts exactly 6 INDICES after level 1 -- 12
        // if these were still byte offsets.
        check.Expect(AlloyRenderer.ArenaLevels(three, out uint sc1, out uint sf1, out uint sc2, out uint sf2)
                     && sc1 == 6 && sc2 == 3 && sf2 - sf1 == 6,
          "the staged coarse runs are not the fixture's two generated runs in index form");
        check.Expect(AlloyRenderer.ArenaLevels(one, out uint oc1, out _, out uint oc2, out _) && oc1 == 0 && oc2 == 0,
          "a mesh the simplifier declined twice reports a coarse run anyway");
        Db.MeshArena.Release(three); Db.MeshArena.Release(one);

        // CX-50: the shadow view's level curve. The oracle is arithmetic done by hand: at a 40 u cascade fit
        // over a 2048 map a texel is 2*40/2048 = 0,0390625 u, so a bound of r spans 2r/0,0390625 px -- 614 px
        // at 12 u (full detail above 500), 256 px at 5 u, 51 px at 1 u (lowest detail below 150). A cube
        // face's texel is 2d/size, so the same 256 px falls to a 20 u bound 40 u from a 512 light.
        float sPx1 = Lod.Px1.V, sPx2 = Lod.Px2.V, sBias = Lod.Bias.V;
        bool sMaster = Lod.G.Master is { V: true };
        if (Lod.G.Master != null) Lod.G.Master.V = true;
        Lod.Px1.V = 500f; Lod.Px2.V = 150f; Lod.Bias.V = 1f;
        ShadowThresholds(2f * 40f / 2048f, out float c1t, out float c2t);
        check.Expect(ShadowLevel(12f, c1t, c2t) == 0 && ShadowLevel(5f, c1t, c2t) == 1 && ShadowLevel(1f, c1t, c2t) == 2,
          "a cascade's detail levels do not follow its texel size");
        check.Expect(ShadowLevel(1e30f, c1t, c2t) == 0, "a bound exempt from the cull does not bake full detail");
        ShadowThresholds(2f / 512f, out float f1t, out float f2t);
        check.Expect(ShadowLevel(20f / 10f, f1t, f2t) == 0 && ShadowLevel(20f / 40f, f1t, f2t) == 1
                     && ShadowLevel(2f / 40f, f1t, f2t) == 2,
          "a cube face's detail levels do not follow its distance");
        ShadowThresholds(0f, out float z1, out float z2);
        check.Expect(z1 == 0f && z2 == 0f && ShadowLevel(0f, z1, z2) == 0, "a view with no texel size does not bake full detail");
        ShadowThresholds(float.PositiveInfinity, out float i1, out float i2);
        check.Expect(ShadowLevel(0.001f, i1, i2) == 0, "a view with no map size bakes the coarsest run instead of the base one");
        if (Lod.G.Master != null) Lod.G.Master.V = sMaster;
        Lod.Px1.V = sPx1; Lod.Px2.V = sPx2; Lod.Bias.V = sBias;

        log.Info(check.Ok ? "MeshLod.SelfCheck: passed" : "MeshLod.SelfCheck: FAILED");
        return check.Ok;
    }
}
