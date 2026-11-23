using System;
using System.Collections.Generic;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

internal static partial class DbSelfCheck
{
    private const long EvictMiB = 1L << 20;

    /// <summary>Hand-built models served to the real model cache, with the parses each one took.</summary>
    private sealed class ResidencySource : IModelSource
    {
        internal readonly Dictionary<string, ModelDef> Models = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, int> Parses = new(StringComparer.OrdinalIgnoreCase);

        public object? Locate(string resref, out string? why)
        {
            bool have = Models.ContainsKey(resref);
            why = have ? null : "not in the fixture";
            return have ? resref : null;
        }

        public ModelDef? Parse(object token, out string? why)
        {
            why = null;
            lock (Parses) Parses[(string)token] = Parses.GetValueOrDefault((string)token) + 1;
            return Models[(string)token];
        }
    }

    /// <summary>Approved examples of eviction under texture pressure, driven through the real model cache, recipe
    /// table and texture registry. Oracles: hand-built models whose textures are landed at fixed MiB, budgets
    /// typed here, and which models and textures each budget must keep.</summary>
    internal static bool ModelEvictionApprovedExamplesSelfCheck(ExtenderLog log)
        => EvictionSuite(log, "DbSelfCheck.ModelEvictionApprovedExamples.SelfCheck", EvictE3, EvictE4, OverClears, EvictE5, EvictE9, EvictE10);

    /// <summary>Run <paramref name="examples"/> through the real model cache, recipe table and texture registry, with
    /// recipes retired by eviction and no byte budget, then give back every seam and table they borrowed.</summary>
    private static bool EvictionSuite(ExtenderLog log, string name, params Action<Checker>[] examples)
    {
        var check = new Checker(log, name);
        ExtenderLog? savedTreeLog = PartTree.Log, savedArenaLog = MeshArena.Log;
        IModelSource? hadSource = ModelCache.Source;
        long? hadBudget = ModelCache.BudgetOverride;
        Action<ModelDef>? hadEvict = ModelCache.Evicting;
        TexBudgetInputs hadInputs = TexBudget.Inputs;
        int texMark = TexRegistry.Watermark;
        PartTree.Log = null;
        MeshArena.Log = null;
        ModelCache.SetSource(null);
        ModelCache.Evicting = DrawRecipe.Forget;
        ModelCache.BudgetOverride = long.MaxValue;
        try { foreach (Action<Checker> example in examples) example(check); }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            DrawRecipe.CapOverride = null;
            ModelCache.SetSource(null);
            ModelCache.Evicting = hadEvict;
            ModelCache.BudgetOverride = hadBudget;
            ModelCache.SetSource(hadSource);
            TexBudget.BeginPeriod(hadInputs);
            PartTree.Log = savedTreeLog;
            Drain();
            PartTree.ResetForCheck();
            MeshArena.ResetForCheck();
            MeshArena.Log = savedArenaLog;
            check.Expect(TexRegistry.RewindTo(texMark), "the examples' textures still hold slots past the session's never-used mark");
        }
        log.Info(check.Ok ? $"{name}: passed" : $"{name}: FAILED");
        return check.Ok;
    }

    /// <summary>A model with one mesh node per texture named, each at its own depth so no node is a twin of
    /// another, filed in <paramref name="src"/>.</summary>
    private static ModelDef Textured(ResidencySource src, string name, params string[] textures)
    {
        var parents = new int[textures.Length + 1];
        var kinds = new NodeKind[textures.Length + 1];
        var meshes = new MeshDef?[textures.Length + 1];
        parents[0] = -1;
        kinds[0] = NodeKind.Dummy;
        for (int k = 0; k < textures.Length; k++) { parents[k + 1] = 0; kinds[k + 1] = NodeKind.TriMesh; meshes[k + 1] = Quad(textures[k], k * 0.25f); }
        ModelDef m = Model(name, parents, kinds, meshes);
        src.Models[name] = m;
        return m;
    }

    /// <summary>Acquire <paramref name="name"/> from the cache and intern a recipe per mesh node, landing each
    /// texture not yet resident at <paramref name="mib"/>. The node's recipe and texture, by node.</summary>
    private static (ModelDef Model, TexId[] Tex) Hold(string name, long mib)
    {
        ModelDef? m = ModelCache.Acquire(name);
        if (m == null) throw new InvalidOperationException($"the fixture model {name} did not load");
        var tex = new TexId[m.Nodes.Length];
        for (int n = 1; n < m.Nodes.Length; n++)
        {
            int r = DrawRecipe.Intern(m, n, plan: false);
            if (r < 0) throw new InvalidOperationException($"{name} node {n} interned no recipe");
            tex[n] = DrawRecipe.Tex(r, 0);
            if (!TexRegistry.Resident(tex[n])) TexRegistry.Land(tex[n], 0xE0000000u | (uint)(r * 4 + n), mib * EvictMiB);
        }
        return (m, tex);
    }

    /// <summary>Clear the cache (each model's recipes retire with it) and drop every texture the example named.</summary>
    private static void EndEviction(params string[] textures)
    {
        ModelCache.SetSource(null);
        DrawRecipe.Pump();
        DrawRecipe.Pump();
        foreach (string t in textures) TexRegistry.Forget(t);
    }

    // E3 Given textures held only by cached models the current area does not use
    // When texture memory passes its budget
    // Then those models are evicted, least recently used first, until it fits, and a later visit to their area loads them again
    private static void EvictE3(Checker check)
    {
        var src = new ResidencySource();
        Textured(src, "cx_rsd3_area", "cx_rsd3_area_t");
        Textured(src, "cx_rsd3_m1", "cx_rsd3_m1_t");
        Textured(src, "cx_rsd3_m2", "cx_rsd3_m2_t");
        ModelCache.SetSource(src);
        try
        {
            (_, TexId[] areaTex) = Hold("cx_rsd3_area", 100);
            (ModelDef m1, TexId[] m1Tex) = Hold("cx_rsd3_m1", 100);
            (ModelDef m2, TexId[] m2Tex) = Hold("cx_rsd3_m2", 100);
            ModelCache.Release(m1);
            ModelCache.Release(m2);
            TexBudget.BeginPeriod(new TexBudgetInputs(250, 5169 * EvictMiB, 0));
            TexRegistry.Tick();
            check.Expect(ModelCache.FindModel("cx_rsd3_m1") == null && TexRegistry.StateOf(m1Tex[1]) == TexState.Free,
                         $"E3: 300MiB held against a 250MiB budget, the least recently used idle model is not evicted with its texture (model {(ModelCache.FindModel("cx_rsd3_m1") == null ? "gone" : "cached")}, texture {TexRegistry.StateOf(m1Tex[1])})");
            check.Expect(ModelCache.FindModel("cx_rsd3_m2") != null && TexRegistry.StateOf(m2Tex[1]) == TexState.Resident,
                         $"E3: eviction went past the fit and took the second idle model (texture {TexRegistry.StateOf(m2Tex[1])})");
            check.Expect(TexRegistry.StateOf(areaTex[1]) == TexState.Resident, $"E3: the area's own texture was evicted ({TexRegistry.StateOf(areaTex[1])})");
            ModelDef? again = ModelCache.Acquire("cx_rsd3_m1");
            int r = again == null ? -1 : DrawRecipe.Intern(again, 1, plan: false);
            check.Expect(src.Parses.GetValueOrDefault("cx_rsd3_m1") == 2 && r >= 0 && !DrawRecipe.Tex(r, 0).IsNull
                         && TexRegistry.Valid(DrawRecipe.Tex(r, 0)) && !TexRegistry.Valid(m1Tex[1]),
                         $"E3: the later visit did not parse the evicted model again and ask for its texture anew (parses {src.Parses.GetValueOrDefault("cx_rsd3_m1")})");
        }
        finally { EndEviction("cx_rsd3_area_t", "cx_rsd3_m1_t", "cx_rsd3_m2_t"); }
    }

    // E4 Given the current area's own textures alone pass the budget
    // When the period's budget is worked out
    // Then nothing the current area draws is evicted, and the frame line says the texture budget is exceeded by how many MiB
    private static void EvictE4(Checker check)
    {
        var src = new ResidencySource();
        Textured(src, "cx_rsd4_area", "cx_rsd4_a", "cx_rsd4_b", "cx_rsd4_c");
        ModelCache.SetSource(src);
        try
        {
            (_, TexId[] tex) = Hold("cx_rsd4_area", 100);
            TexBudget.BeginPeriod(new TexBudgetInputs(200, 5169 * EvictMiB, 0));
            TexRegistry.Tick();
            check.Expect(ModelCache.FindModel("cx_rsd4_area") != null && TexRegistry.StateOf(tex[1]) == TexState.Resident
                         && TexRegistry.StateOf(tex[2]) == TexState.Resident && TexRegistry.StateOf(tex[3]) == TexState.Resident,
                         "E4: a texture the area draws was evicted to meet the budget");
            string line = TexRegistry.BudgetLine();
            check.Expect(AlloyRenderer.ClauseMiB(line, TexRegistry.OverKey) == 100,
                         $"E4: 300MiB the area draws against a 200MiB budget, the frame line does not say exceeded by 100MiB: \"{line}\"");
        }
        finally { EndEviction("cx_rsd4_a", "cx_rsd4_b", "cx_rsd4_c"); }
    }

    // Found: Given the area's own textures passed the budget, and the frame line said by how many MiB
    // When the player leaves that area within the period, and its model is evicted
    // Then the frame line no longer says the budget is exceeded
    private static void OverClears(Checker check)
    {
        var src = new ResidencySource();
        Textured(src, "cx_rsdo_area", "cx_rsdo_a", "cx_rsdo_b", "cx_rsdo_c");
        ModelCache.SetSource(src);
        try
        {
            (ModelDef area, _) = Hold("cx_rsdo_area", 100);
            TexBudget.BeginPeriod(new TexBudgetInputs(200, 5169 * EvictMiB, 0));
            TexRegistry.Tick();
            long? before = AlloyRenderer.ClauseMiB(TexRegistry.BudgetLine(), TexRegistry.OverKey);
            ModelCache.Release(area);
            TexRegistry.Tick();
            string line = TexRegistry.BudgetLine();
            check.Expect(before == 100 && AlloyRenderer.ClauseMiB(line, TexRegistry.OverKey) == null,
                         $"found: the area left within the period, and the frame line still says the budget is exceeded (was {before} MiB): \"{line}\"");
        }
        finally { EndEviction("cx_rsdo_a", "cx_rsdo_b", "cx_rsdo_c"); }
    }

    // E5 Given texture memory is well under its budget
    // When the player changes area
    // Then nothing is evicted, and a texture seen before is handed back, not rebuilt
    private static void EvictE5(Checker check)
    {
        var src = new ResidencySource();
        Textured(src, "cx_rsd5_m", "cx_rsd5_m_t");
        ModelCache.SetSource(src);
        TexId seen = default;
        try
        {
            (ModelDef m, TexId[] mTex) = Hold("cx_rsd5_m", 100);
            seen = TexRegistry.Acquire("cx_rsd5_seen");
            TexRegistry.Land(seen, 0xE5000001u, 600 * EvictMiB);
            TexRegistry.Release(seen);
            TexBudget.BeginPeriod(new TexBudgetInputs(-1, 5169 * EvictMiB, 2600 * EvictMiB));
            long evicted = ModelCache.Evictions;
            ModelCache.Release(m);
            ModelCache.AreaEdge();
            TexRegistry.Tick();
            check.Expect(ModelCache.Evictions == evicted && ModelCache.FindModel("cx_rsd5_m") != null && TexRegistry.StateOf(mTex[1]) == TexState.Resident,
                         "E5: 700MiB against a 2052MiB budget, the area change evicted a cached model or its texture");
            TexId back = TexRegistry.Acquire("cx_rsd5_seen");
            check.Expect(back.Index == seen.Index && TexRegistry.Valid(seen) && TexRegistry.StateOf(back) == TexState.Resident,
                         $"E5: 700MiB against a 2052MiB budget, the texture seen before was not handed back warm (state {TexRegistry.StateOf(back)})");
            TexRegistry.Release(back);
        }
        finally { EndEviction("cx_rsd5_m_t", "cx_rsd5_seen"); }
    }

    // E9 Given a period that evicted models
    // When the frame line is written
    // Then it says how many, and the texture budget
    private static void EvictE9(Checker check)
    {
        var src = new ResidencySource();
        Textured(src, "cx_rsd9_area", "cx_rsd9_area_t");
        string[] idle = ["cx_rsd9_m1", "cx_rsd9_m2", "cx_rsd9_m3"];
        foreach (string n in idle) Textured(src, n, n + "_t");
        ModelCache.SetSource(src);
        try
        {
            Hold("cx_rsd9_area", 100);
            foreach (string n in idle) ModelCache.Release(Hold(n, 100).Model);
            TexBudget.BeginPeriod(new TexBudgetInputs(250, 5169 * EvictMiB, 0));
            TexRegistry.Tick();
            string line = TexRegistry.BudgetLine();
            check.Expect(AlloyRenderer.ClauseCount(line, TexRegistry.EvictedKey) == 2 && AlloyRenderer.ClauseMiB(line, TexRegistry.BudgetKey) == 250,
                         $"E9: 400MiB against a 250MiB budget evicts two models, and the frame line does not say 2 models evicted and texture budget 250MiB: \"{line}\"");
        }
        finally { EndEviction("cx_rsd9_area_t", "cx_rsd9_m1_t", "cx_rsd9_m2_t", "cx_rsd9_m3_t"); }
    }

    // E10 Given a texture used by an idle model and by one the area draws
    // When eviction runs
    // Then that texture stays
    private static void EvictE10(Checker check)
    {
        var src = new ResidencySource();
        Textured(src, "cx_rsd10_area", "cx_rsd10_shared");
        Textured(src, "cx_rsd10_idle", "cx_rsd10_own", "cx_rsd10_shared");
        ModelCache.SetSource(src);
        try
        {
            (_, TexId[] areaTex) = Hold("cx_rsd10_area", 100);
            (ModelDef idle, TexId[] idleTex) = Hold("cx_rsd10_idle", 100);
            ModelCache.Release(idle);
            TexBudget.BeginPeriod(new TexBudgetInputs(150, 5169 * EvictMiB, 0));
            TexRegistry.Tick();
            check.Expect(ModelCache.FindModel("cx_rsd10_idle") == null && TexRegistry.StateOf(idleTex[1]) == TexState.Free,
                         $"E10: 200MiB against a 150MiB budget, the idle model and its own texture were not evicted (texture {TexRegistry.StateOf(idleTex[1])})");
            check.Expect(areaTex[1].Index == idleTex[2].Index && TexRegistry.StateOf(areaTex[1]) == TexState.Resident,
                         $"E10: the texture the area draws went with the idle model that shared it ({TexRegistry.StateOf(areaTex[1])})");
        }
        finally { EndEviction("cx_rsd10_shared", "cx_rsd10_own"); }
    }
}
