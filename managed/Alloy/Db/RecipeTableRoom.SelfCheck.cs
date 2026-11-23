using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

internal static partial class DbSelfCheck
{
    /// <summary>Regression tests of the recipe table's room: approved example E4, which held before the trim existed,
    /// and the cap, the per-frame headroom and the area edge's byte trim the mutation pass found unpinned.</summary>
    internal static bool RecipeTableRoomSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbSelfCheck.RecipeTableRoom.SelfCheck");
        IModelSource? hadSource = ModelCache.Source;
        long? hadBudget = ModelCache.BudgetOverride;
        System.Action<ModelDef>? hadEvict = ModelCache.Evicting;
        ModelCache.SetSource(null);
        ModelCache.Evicting = DrawRecipe.Forget;
        ModelCache.BudgetOverride = long.MaxValue;
        try { RoomE4(check); RoomCapHolds(check); RoomOneFrame(check); RoomEdgeBytes(check); }
        catch (System.Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            DrawRecipe.CapOverride = null;
            ModelCache.SetSource(null);
            ModelCache.Evicting = hadEvict;
            ModelCache.BudgetOverride = hadBudget;
            ModelCache.SetSource(hadSource);
            Drain();
        }
        log.Info(check.Ok ? "DbSelfCheck.RecipeTableRoom.SelfCheck: passed" : "DbSelfCheck.RecipeTableRoom.SelfCheck: FAILED");
        return check.Ok;
    }

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
}
