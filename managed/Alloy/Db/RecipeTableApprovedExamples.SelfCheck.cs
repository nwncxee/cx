using System;
using System.Collections.Generic;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

internal static partial class DbSelfCheck
{
    /// <summary>Frames a part keeps asking for its recipe before an example gives up on it.</summary>
    private const int RoomFrames = 12;

    /// <summary>Approved examples of a full recipe table, driven through the real model cache and recipe table on a
    /// table capped small. Oracles: hand-built models of a known node count, the order they were released in, and
    /// the model cache's own eviction count.</summary>
    internal static bool RecipeTableApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbSelfCheck.RecipeTableApprovedExamples.SelfCheck");
        ExtenderLog? savedTreeLog = PartTree.Log, savedArenaLog = MeshArena.Log;
        IModelSource? hadSource = ModelCache.Source;
        long? hadBudget = ModelCache.BudgetOverride;
        Action<ModelDef>? hadEvict = ModelCache.Evicting;
        TexBudgetInputs hadInputs = TexBudget.Inputs;
        PartTree.Log = null;
        MeshArena.Log = null;
        ModelCache.SetSource(null);
        ModelCache.Evicting = DrawRecipe.Forget;
        ModelCache.BudgetOverride = long.MaxValue;
        try
        {
            RoomE1(check);
            RoomE2(check);
            RoomE3(check);
            RoomE5(check);
            RoomE6(check);
            RoomE7(check);
        }
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
        }
        log.Info(check.Ok ? "DbSelfCheck.RecipeTableApprovedExamples.SelfCheck: passed"
                          : "DbSelfCheck.RecipeTableApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>File <paramref name="count"/> models named <paramref name="stem"/>0.. in <paramref name="src"/>, each
    /// with <paramref name="nodes"/> mesh nodes and a texture of its own per node.</summary>
    private static string[] Models(ResidencySource src, string stem, int count, int nodes)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = stem + i;
            var tex = new string[nodes];
            for (int k = 0; k < nodes; k++) tex[k] = $"{names[i]}_t{k}";
            Textured(src, names[i], tex);
        }
        return names;
    }

    /// <summary>Every texture name <see cref="Models"/> gave the models named.</summary>
    private static string[] TexturesOf(string[] names, int nodes)
    {
        var all = new List<string>();
        foreach (string n in names)
            for (int k = 0; k < nodes; k++) all.Add($"{n}_t{k}");
        return all.ToArray();
    }

    /// <summary>Ask a recipe for every mesh node of <paramref name="m"/>, as its parts do each frame: true when all
    /// of them have one.</summary>
    private static bool Draws(ModelDef m)
    {
        bool all = true;
        for (int n = 1; n < m.Nodes.Length; n++)
            if (DrawRecipe.Intern(m, n, plan: false) < 0) all = false;
        return all;
    }

    /// <summary>Run frames, each asking for <paramref name="m"/>'s recipes then pumping the table, until it draws.
    /// The frames it took, or -1 when it never drew.</summary>
    private static int FramesToDraw(ModelDef m)
    {
        for (int f = 0; f < RoomFrames; f++)
        {
            if (Draws(m)) return f;
            DrawRecipe.Pump();
        }
        return Draws(m) ? RoomFrames : -1;
    }

    /// <summary>Hold each model and let it go again, oldest first: idle models the cache keeps, least recently
    /// used first.</summary>
    private static void HoldThenIdle(string[] names, long mib = 1)
    {
        foreach (string n in names) ModelCache.Release(Hold(n, mib).Model);
    }

    /// <summary>Recipes in use once the cache is clear, which other checks of the run may have left.</summary>
    private static int RecipeFloor()
    {
        ModelCache.SetSource(null);
        DrawRecipe.Pump();
        DrawRecipe.Pump();
        return DrawRecipe.Count;
    }

    // E1 Given a long session whose idle models' recipes have filled the recipe table
    // When the player walks into an area with a tileset not drawn before
    // Then its tiles draw, and the least recently used idle models are evicted to make room
    private static void RoomE1(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtb1_m", 16, 4);
        string[] tiles = Models(src, "cx_rtb1_tile", 1, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor + 64;
        try
        {
            HoldThenIdle(idle);
            ModelDef tile = ModelCache.Acquire(tiles[0])!;
            int frames = FramesToDraw(tile);
            check.Expect(frames >= 0, $"E1: a table full of idle models' recipes ({DrawRecipe.Count} of {floor + 64}) never made room for a new tileset");
            check.Expect(ModelCache.FindModel(idle[0]) == null && ModelCache.FindModel(idle[^1]) != null,
                         $"E1: making room did not evict the least recently used idle model and keep the most recent "
                       + $"(oldest {(ModelCache.FindModel(idle[0]) == null ? "evicted" : "kept")}, newest {(ModelCache.FindModel(idle[^1]) == null ? "evicted" : "kept")}, floor {floor})");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. idle, .. tiles], 4)); }
    }

    // E2 Given a recipe table full of recipes that the current area's objects all draw, with no idle model left
    // When a part not drawn before asks for its recipe
    // Then no model the area draws is evicted, the frame line says RECIPES SPILLED, and the part draws as soon as a recipe is freed
    private static void RoomE2(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] area = Models(src, "cx_rtb2_a", 2, 4);
        string[] fresh = Models(src, "cx_rtb2_new", 1, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor + 8;
        try
        {
            ModelDef a0 = Hold(area[0], 1).Model;
            Hold(area[1], 1);
            ModelDef part = ModelCache.Acquire(fresh[0])!;
            long evicted = ModelCache.Evictions;
            check.Expect(FramesToDraw(part) < 0, "E2: a part drew from a table the area's own recipes fill");
            check.Expect(ModelCache.Evictions == evicted && ModelCache.FindModel(area[0]) != null && ModelCache.FindModel(area[1]) != null,
                         $"E2: a model the area draws was evicted to make room ({ModelCache.Evictions - evicted} evicted)");
            string line = DbFeed.Stats();
            check.Expect(line.Contains("RECIPES SPILLED", StringComparison.Ordinal), $"E2: the frame line does not say RECIPES SPILLED: \"{line}\"");
            ModelCache.Release(a0);
            int frames = FramesToDraw(part);
            check.Expect(frames >= 0 && frames <= 4, $"E2: the part did not draw within four frames of its area letting a model go ({frames})");
            check.Expect(ModelCache.FindModel(area[1]) != null, "E2: the model the area still draws went with the one it let go");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. area, .. fresh], 4)); }
    }

    // E3 Given a session whose idle models hold more than a quarter of the recipe table
    // When the player changes area
    // Then the least recently used idle models are evicted until three quarters of the table is free, and the models the new area draws stay
    private static void RoomE3(Checker check)
    {
        int floor = RecipeFloor();
        int cap = floor * 4 + 64;
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtb3_m", 8, 4);
        string[] area = Models(src, "cx_rtb3_area", 1, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = cap;
        try
        {
            Hold(area[0], 1);
            HoldThenIdle(idle);
            check.Expect(DrawRecipe.Count > cap / 4, $"E3: the idle models hold {DrawRecipe.Count} of {cap}, not more than a quarter");
            DrawRecipe.AreaEdge();
            check.Expect(DrawRecipe.Count <= cap - cap * 3 / 4, $"E3: after the area change {DrawRecipe.Count} of {cap} recipes are in use, so three quarters is not free");
            check.Expect(ModelCache.FindModel(area[0]) != null, "E3: the model the new area draws was evicted");
            check.Expect(ModelCache.FindModel(idle[0]) == null && ModelCache.FindModel(idle[^1]) != null,
                         $"E3: the trim did not take the least recently used idle model first "
                       + $"(oldest {(ModelCache.FindModel(idle[0]) == null ? "evicted" : "kept")}, newest {(ModelCache.FindModel(idle[^1]) == null ? "evicted" : "kept")})");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. idle, .. area], 4)); }
    }

    // E5 Given a session already holding 16,384 recipes, the whole table of v0.13.11 as in the reporter's 11-23 session
    // When one more model node not drawn before is drawn
    // Then it gets its recipe and draws, and no model is evicted
    private static void RoomE5(Checker check)
    {
        int whole = 1 << 14;
        RecipeFloor();
        var src = new ResidencySource();
        string[] one = Models(src, "cx_rtb5_one", 1, 1);
        ModelCache.SetSource(src);
        var held = new List<int>();
        try
        {
            while (DrawRecipe.Count < whole)
            {
                int r = DrawRecipe.FlagsForCheck(0);
                if (r < 0) break;
                held.Add(r);
            }
            check.Expect(DrawRecipe.Count == whole, $"E5: the session could not hold {whole} recipes ({DrawRecipe.Count})");
            long evicted = ModelCache.Evictions;
            ModelDef m = ModelCache.Acquire(one[0])!;
            check.Expect(Draws(m), $"E5: one more node past {whole} recipes got no recipe");
            check.Expect(ModelCache.Evictions == evicted, $"E5: {ModelCache.Evictions - evicted} model(s) were evicted for the one more node");
        }
        finally
        {
            foreach (int r in held) DrawRecipe.ReleaseForCheck(r);
            EndEviction(TexturesOf(one, 1));
        }
    }

    // E6 Given a model evicted to make room in the recipe table
    // When the player returns to an area that draws it
    // Then it loads again and draws as it did before
    private static void RoomE6(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] back = Models(src, "cx_rtb6_back", 1, 4);
        string[] other = Models(src, "cx_rtb6_other", 3, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor + 16;
        try
        {
            (ModelDef first, _) = Hold(back[0], 1);
            var before = new string[first.Nodes.Length];
            for (int n = 1; n < first.Nodes.Length; n++) before[n] = DrawRecipe.TexName(DrawRecipe.Intern(first, n, plan: false), 0);
            ModelCache.Release(first);
            var others = new List<ModelDef>();
            foreach (string o in other) others.Add(Hold(o, 1).Model);
            for (int f = 0; f < 4 && ModelCache.FindModel(back[0]) != null; f++) DrawRecipe.Pump();
            check.Expect(ModelCache.FindModel(back[0]) == null, "E6: the idle model was not evicted to make room in a full table");
            foreach (ModelDef o in others) ModelCache.Release(o);
            ModelDef again = ModelCache.Acquire(back[0])!;
            check.Expect(src.Parses.GetValueOrDefault(back[0]) == 2, $"E6: the returning area's model was not loaded again ({src.Parses.GetValueOrDefault(back[0])} parses)");
            check.Expect(FramesToDraw(again) >= 0, "E6: the model loaded again but never drew");
            for (int n = 1; n < again.Nodes.Length; n++)
            {
                int r = DrawRecipe.Intern(again, n, plan: false);
                check.Expect(r >= 0 && DrawRecipe.TexName(r, 0) == before[n],
                             $"E6: node {n} draws {(r < 0 ? "nothing" : DrawRecipe.TexName(r, 0))}, not {before[n]} as before");
            }
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. back, .. other], 4)); }
    }

    // E7 Given a period in which idle models were evicted to make room for recipes
    // When the frame line is written
    // Then it says how many models the recipe table evicted, apart from those the memory and texture budgets evicted
    private static void RoomE7(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtb7_m", 8, 2);
        string[] area = Models(src, "cx_rtb7_area", 1, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor + 16;
        try
        {
            HoldThenIdle(idle, 100);
            DrawRecipe.ResetPeriod();
            check.Expect(AlloyRenderer.ClauseCount(DbFeed.Stats(), DrawRecipe.EvictedKey) == null,
                         $"E7: a period with no model evicted for recipes says one: \"{DbFeed.Stats()}\"");
            long evicted = ModelCache.Evictions;
            TexBudget.BeginPeriod(new TexBudgetInputs(1500, 5169 * EvictMiB, 0));
            TexRegistry.Tick();
            check.Expect(TexBudget.ModelsEvicted >= 1, "E7: the texture budget evicted no model, so the line has nothing to tell apart");
            FramesToDraw(ModelCache.Acquire(area[0])!);
            long forRecipes = ModelCache.Evictions - evicted - TexBudget.ModelsEvicted;
            check.Expect(forRecipes > 0, "E7: the recipe table evicted no model to make room");
            string line = DbFeed.Stats();
            check.Expect(AlloyRenderer.ClauseCount(line, DrawRecipe.EvictedKey) == forRecipes,
                         $"E7: {forRecipes} model(s) evicted for recipes beside {TexBudget.ModelsEvicted} for the texture budget, and the frame line does not say {forRecipes}: \"{line}\"");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction([.. TexturesOf(idle, 2), .. TexturesOf(area, 4)]); }
    }
}
