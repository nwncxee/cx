using System.Collections.Generic;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

internal static partial class DbSelfCheck
{
    /// <summary>Regression and found tests of the recipe table's room: approved example E4, the cap, the per-frame
    /// headroom, the area edge's trims through the model cache's pass, a table twice the old size, and texture slots.</summary>
    internal static bool RecipeTableRoomSelfCheck(ExtenderLog log)
        => EvictionSuite(log, "DbSelfCheck.RecipeTableRoom.SelfCheck", RoomE4, RoomCapHolds, RoomOneFrame, RoomEdgeBytes,
                         RoomEdgeByPass, RoomTwiceOld, RoomTexSlots);

    /// <summary>The eviction suites, run so the frame line's recipe counts read the same before and after: a live
    /// session runs them at boot, and must never report their spills, retirements or evictions as its own.</summary>
    internal static bool EvictionSuitesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbSelfCheck.EvictionSuites.SelfCheck");
        string before = RecipeCounts();
        bool suites = ModelEvictionApprovedExamplesSelfCheck(log) & RecipeTableApprovedExamplesSelfCheck(log)
                    & RecipeTableRoomSelfCheck(log);
        string after = RecipeCounts();
        check.Expect(after == before, $"the frame line's recipe counts moved: {before} -> {after}");
        log.Info(check.Ok ? "DbSelfCheck.EvictionSuites.SelfCheck: passed" : "DbSelfCheck.EvictionSuites.SelfCheck: FAILED");
        return suites && check.Ok;
    }

    private static string RecipeCounts()
        => $"spilled {DrawRecipe.Spilled} evicted {DrawRecipe.RoomEvicted} retired {DrawRecipe.Retired} "
         + $"box-grown {DrawRecipe.BoxGrown} twin {DrawRecipe.TwinRefused} reclassified {DrawRecipe.Reclassified} "
         + $"in-use {DrawRecipe.RetiredInUse} twice {DrawRecipe.DoubleRetire}";

    // E4 Given a session whose recipes fill less than a quarter of the recipe table, with its model memory under budget
    // When the player changes area
    // Then no model is evicted, and an area visited before comes back without loading its models again
    private static void RoomE4(Checker check)
    {
        int floor = RecipeFloor();
        int cap = floor * 4 + 64;
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtb4_m", 2, 4);
        string[] area = Models(src, "cx_rtb4_area", 1, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = cap;
        try
        {
            Hold(area[0], 1);
            HoldThenIdle(idle);
            check.Expect(DrawRecipe.Count < cap / 4, $"E4: the session holds {DrawRecipe.Count} of {cap} recipes, not less than a quarter");
            long evicted = ModelCache.Evictions;
            DrawRecipe.AreaEdge();
            check.Expect(ModelCache.Evictions == evicted, $"E4: the area change evicted {ModelCache.Evictions - evicted} model(s) from a table with room");
            foreach (string n in idle) ModelCache.Acquire(n);
            check.Expect(src.Parses.GetValueOrDefault(idle[0]) == 1 && src.Parses.GetValueOrDefault(idle[1]) == 1,
                         $"E4: the area visited before loaded its models again ({src.Parses.GetValueOrDefault(idle[0])}, {src.Parses.GetValueOrDefault(idle[1])} parses)");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. idle, .. area], 4)); }
    }

    /// <summary>A table its held models fill to the cap refuses one more recipe and never grows past the cap.</summary>
    private static void RoomCapHolds(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] held = Models(src, "cx_rtc_h", 4, 1);
        string[] fresh = Models(src, "cx_rtc_new", 1, 1);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor + held.Length;
        try
        {
            foreach (string n in held) Hold(n, 1);
            ModelDef part = ModelCache.Acquire(fresh[0])!;
            check.Expect(!Draws(part) && DrawRecipe.Count == floor + held.Length,
                         $"a table full at {floor + held.Length} took one more recipe ({DrawRecipe.Count} in use)");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. held, .. fresh], 1)); }
    }

    /// <summary>One frame of a table full of idle models frees a sixteenth of it, so a new area asking for that many
    /// recipes at once draws on the next frame.</summary>
    private static void RoomOneFrame(Checker check)
    {
        int floor = RecipeFloor();
        int cap = floor + 256;
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtf_m", 64, 4);
        string[] area = Models(src, "cx_rtf_area", cap / 16 / 4, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = cap;
        try
        {
            HoldThenIdle(idle);
            var parts = new ModelDef[area.Length];
            for (int i = 0; i < area.Length; i++) parts[i] = ModelCache.Acquire(area[i])!;
            int frames = 0;
            while (frames < 4 && !System.Array.TrueForAll(parts, Draws)) { DrawRecipe.Pump(); frames++; }
            check.Expect(frames == 1, $"a full table of {cap} took {frames} frame(s) to seat {area.Length * 4} new recipes, not one");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf([.. idle, .. area], 4)); }
    }

    /// <summary>The area change still trims idle models by bytes when their recipes leave the table room.</summary>
    private static void RoomEdgeBytes(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rte_m", 2, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor * 4 + 64;
        try
        {
            HoldThenIdle(idle);
            ModelCache.BudgetOverride = 0;
            long evicted = ModelCache.Evictions;
            DrawRecipe.AreaEdge();
            check.Expect(ModelCache.Evictions - evicted == idle.Length,
                         $"the area change evicted {ModelCache.Evictions - evicted} of {idle.Length} idle models over a zero byte budget");
        }
        finally { ModelCache.BudgetOverride = long.MaxValue; DrawRecipe.CapOverride = null; EndEviction(TexturesOf(idle, 4)); }
    }

    /// <summary>The model cache pass's area edge trims idle models down to a quarter of the table, and the frame
    /// line counts each of those evictions as one for the recipe table.</summary>
    private static void RoomEdgeByPass(Checker check)
    {
        int floor = RecipeFloor();
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtp_m", 16, 4);
        ModelCache.SetSource(src);
        DrawRecipe.CapOverride = floor + 64;
        try
        {
            HoldThenIdle(idle);
            long evicted = ModelCache.Evictions;
            int counted = DrawRecipe.RoomEvicted;
            PassLifecycle? pass = PassRegistry.Global.Find("modelcache");
            pass?.Revoke?.Invoke();
            long gone = ModelCache.Evictions - evicted;
            check.Expect(gone > 0 && DrawRecipe.Count <= (floor + 64) / 4,
                         $"the model cache's area edge left {DrawRecipe.Count} of {floor + 64} recipes in use after {gone} eviction(s)");
            check.Expect(DrawRecipe.RoomEvicted - counted == gone,
                         $"the frame line counted {DrawRecipe.RoomEvicted - counted} of the area edge's {gone} evictions for the recipe table");
        }
        finally { DrawRecipe.CapOverride = null; EndEviction(TexturesOf(idle, 4)); }
    }

    /// <summary>A session holding twice the recipes the 16,384-slot table held keeps every idle model a frame later.</summary>
    private static void RoomTwiceOld(Checker check)
    {
        int twice = 1 << 15;
        RecipeFloor();
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rtw_m", 1, 1);
        ModelCache.SetSource(src);
        var held = new List<int>();
        try
        {
            HoldThenIdle(idle);
            while (DrawRecipe.Count < twice)
            {
                int r = DrawRecipe.FlagsForCheck(0);
                if (r < 0) break;
                held.Add(r);
            }
            long evicted = ModelCache.Evictions;
            DrawRecipe.Pump();
            check.Expect(DrawRecipe.Count >= twice && ModelCache.Evictions == evicted,
                         $"a session at {DrawRecipe.Count} of {twice} recipes evicted {ModelCache.Evictions - evicted} idle model(s) in a frame");
        }
        finally
        {
            foreach (int r in held) DrawRecipe.ReleaseForCheck(r);
            EndEviction(TexturesOf(idle, 1));
        }
    }

    /// <summary>Idle models whose recipes hold textures past the texture table's slots are evicted, frame by frame,
    /// so a new model still gets a slot for each of its textures.</summary>
    private static void RoomTexSlots(Checker check)
    {
        const int nodes = 32;
        const int batch = 16;
        RecipeFloor();
        var src = new ResidencySource();
        string[] idle = Models(src, "cx_rts_m", (TexRegistry.MaxSlots + 2048) / nodes, nodes);
        string[] fresh = Models(src, "cx_rts_new", 1, 4);
        ModelCache.SetSource(src);
        try
        {
            for (int i = 0; i < idle.Length; i++)
            {
                ModelDef m = ModelCache.Acquire(idle[i])!;
                Draws(m);
                ModelCache.Release(m);
                if (i % batch == batch - 1) DrawRecipe.Pump();
            }
            DrawRecipe.Pump();
            ModelDef part = ModelCache.Acquire(fresh[0])!;
            int white = 0;
            for (int n = 1; n < part.Nodes.Length; n++)
            {
                int r = DrawRecipe.Intern(part, n, plan: false);
                if (r < 0 || DrawRecipe.Tex(r, 0).IsNull) white++;
            }
            check.Expect(white == 0, $"a new model drew {white} of {part.Nodes.Length - 1} node(s) white after idle models' recipes filled the texture table");
        }
        finally { EndEviction([.. TexturesOf(idle, nodes), .. TexturesOf(fresh, 4)]); }
    }
}
