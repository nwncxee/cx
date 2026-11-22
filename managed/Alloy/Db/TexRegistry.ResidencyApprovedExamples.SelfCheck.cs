using System;
using CX.Framework;

namespace CX.Alloy.Db;

internal static unsafe partial class TexRegistry
{
    private const long ResidencyMiB = 1L << 20;

    /// <summary>Approved examples of the one texture budget: its figure from injected inputs, the refusal edge and
    /// the owned cache reading the same figure. Oracles: hand-written heap, allocation and knob figures and the MiB
    /// they work out to, and hand-landed textures of fixed sizes.</summary>
    internal static bool ResidencyApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.ResidencyApprovedExamplesSelfCheck");
        TexBudgetInputs had = TexBudget.Inputs;
        try
        {
            // E1 Given alloy.texcache -1, a heap budget of 5169 MiB and 2600 MiB of Alloy's allocations that are not textures
            // When the period's budget is worked out
            // Then the texture budget is 90% of 5169 less 2600: 2052 MiB
            check.Expect(PeriodMib(-1, 5169, 2600) == 2052, $"E1: auto, 5169MiB heap budget, 2600MiB not textures: budget {PeriodMib(-1, 5169, 2600)}MiB, not 2052MiB");

            // E2 Given alloy.texcache 2048
            // When the period's budget is worked out
            // Then the texture budget is 2048 MiB, whatever the live figure says
            check.Expect(PeriodMib(2048, 5169, 2600) == 2048 && PeriodMib(2048, 900, 2600) == 2048,
                         $"E2: alloy.texcache 2048 gives {PeriodMib(2048, 5169, 2600)}MiB on a 5169MiB heap and {PeriodMib(2048, 900, 2600)}MiB on a 900MiB one, not 2048MiB");

            // E11 Given a budget smaller than what the area itself draws
            // When it is worked out
            // Then it stops at 0, never below
            check.Expect(PeriodMib(-1, 2000, 2600) == 0, $"E11: 90% of a 2000MiB heap less 2600MiB gives {PeriodMib(-1, 2000, 2600)}MiB, not 0");

            // E12 Given a device that answers no memory query
            // When the budget is worked out
            // Then it uses the fixed fallback figure
            check.Expect(PeriodMib(-1, 0, 300) == 1024, $"E12: with no memory figure the budget is {PeriodMib(-1, 0, 300)}MiB, not the 1024MiB fallback");

            // E13 Given a device without VK_EXT_memory_budget
            // When the budget is worked out
            // Then it uses the allocator's estimate
            check.Expect(PeriodMib(-1, 6400, 1000) == 4760, $"E13: VMA's 6400MiB estimate for an 8000MiB heap, 1000MiB not textures: budget {PeriodMib(-1, 6400, 1000)}MiB, not 4760MiB");

            // E7 Given world and owned textures
            // When the budget is worked out
            // Then both caches share one budget
            TexBudget.BeginPeriod(new TexBudgetInputs(-1, 5169 * ResidencyMiB, 2600 * ResidencyMiB));
            long world = BudgetBytes() >> 20, owned = OwnedTextures.Budget() >> 20;
            check.Expect(world == 2052 && owned == 2052, $"E7: the world cache's budget is {world}MiB and the owned cache's {owned}MiB, not both 2052MiB");

            check.Expect(BudgetFalls(), "E8: the budget did not follow the driver's falling figure into the next period, or eviction did not follow it");
            check.Expect(RefusalEvicts(), "E6: a texture refused for room did not evict the two oldest idle textures and only those");
        }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally { TexBudget.BeginPeriod(had); }
        log.Info(check.Ok ? "TexRegistry.ResidencyApprovedExamplesSelfCheck: passed" : "TexRegistry.ResidencyApprovedExamplesSelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The world cache's budget, MiB, for a period started from these figures (MiB).</summary>
    private static long PeriodMib(int texCacheMib, long heapMib, long otherMib)
    {
        TexBudget.BeginPeriod(new TexBudgetInputs(texCacheMib, heapMib * ResidencyMiB, otherMib * ResidencyMiB));
        return BudgetBytes() >> 20;
    }

    /// <summary>A texture landed at <paramref name="mib"/> and held once; the stub host cannot load it, so the
    /// failure line is claimed first.</summary>
    private static TexId Held(string resref, long mib, uint name)
    {
        LoudOnce($"tex:{resref}");
        TexId id = Acquire(resref);
        Land(id, name, mib * ResidencyMiB);
        return id;
    }

    /// <summary>E8. Oracle: three idle 500 MiB textures, a 2052 MiB period that keeps them and a 1000 MiB one
    /// (90% of 4000 less 2600) that keeps at most two.</summary>
    private static bool BudgetFalls()
    {
        string[] names = ["cx_rsd_fall_a", "cx_rsd_fall_b", "cx_rsd_fall_c"];
        var ids = new TexId[names.Length];
        try
        {
            for (int k = 0; k < names.Length; k++) { ids[k] = Held(names[k], 500, 0xE8000001u + (uint)k); Release(ids[k]); }

            // E8 Given the driver's budget falls mid-session
            // When the next period starts
            // Then the texture budget follows it with no restart
            TexBudget.BeginPeriod(new TexBudgetInputs(-1, 5169 * ResidencyMiB, 2600 * ResidencyMiB));
            Tick();
            bool kept = StateOf(ids[0]) == TexState.Idle && StateOf(ids[1]) == TexState.Idle && StateOf(ids[2]) == TexState.Idle;
            TexBudget.BeginPeriod(new TexBudgetInputs(-1, 4000 * ResidencyMiB, 2600 * ResidencyMiB));
            Tick();
            long world = BudgetBytes() >> 20, owned = OwnedTextures.Budget() >> 20;
            return kept && world == 1000 && owned == 1000
                && StateOf(ids[0]) == TexState.Free && StateOf(ids[2]) == TexState.Idle;
        }
        finally { foreach (string n in names) Forget(n); }
    }

    /// <summary>E6. Oracle: a 300 MiB budget holding 100 MiB in use and three 50 MiB idle textures, released
    /// oldest first; room for 120 MiB more leaves 80 MiB for idle, so the two oldest go.</summary>
    private static bool RefusalEvicts()
    {
        string[] names = ["cx_rsd_room_a", "cx_rsd_room_b", "cx_rsd_room_c"];
        var ids = new TexId[names.Length];
        TexId held = default;
        try
        {
            TexBudget.BeginPeriod(new TexBudgetInputs(300, 5169 * ResidencyMiB, 0));
            held = Held("cx_rsd_room_held", 100, 0xE6000010u);
            for (int k = 0; k < names.Length; k++) { ids[k] = Held(names[k], 50, 0xE6000001u + (uint)k); Release(ids[k]); }

            // E6 Given a texture refused for room
            // When the refusal is counted
            // Then idle textures are evicted until it fits
            RefusedForRoom(120 * ResidencyMiB);
            return StateOf(ids[0]) == TexState.Free && StateOf(ids[1]) == TexState.Free
                && StateOf(ids[2]) == TexState.Idle && StateOf(held) == TexState.Resident;
        }
        finally
        {
            Release(held);
            Forget("cx_rsd_room_held");
            foreach (string n in names) Forget(n);
        }
    }
}
