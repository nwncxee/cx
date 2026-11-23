using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy.Db;

internal static unsafe partial class TexRegistry
{
    /// <summary>Found test: a table whose every free slot is taken by idle textures gives a new texture the least
    /// recently used idle slot instead of the white stand-in. Oracle: hand-landed 1 MiB textures, released in order.</summary>
    internal static bool FullTableSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.FullTableSelfCheck");
        TexBudgetInputs had = TexBudget.Inputs;
        var names = new List<string>();
        var ids = new List<TexId>();
        TexId fresh = default;
        long hadFull = _full;
        try
        {
            TexBudget.BeginPeriod(new TexBudgetInputs(1 << 20, 5169 * ResidencyMiB, 0));
            while (_freeHead >= 0 || _high < MaxSlots)
            {
                string n = $"cx_tfull_{names.Count}";
                names.Add(n);
                TexId id = Held(n, 1, 0xF1000000u + (uint)names.Count);
                ids.Add(id);
                Release(id);
            }
            LoudOnce("tex:cx_tfull_new");
            fresh = Acquire("cx_tfull_new");
            check.Expect(Valid(fresh) && _full == hadFull,
                         $"a full table of {names.Count} idle textures refused a new one ({_full - hadFull} refused)");
            check.Expect(StateOf(ids[0]) == TexState.Free && StateOf(ids[^1]) == TexState.Idle,
                         $"the new texture did not take the least recently used idle slot (oldest {StateOf(ids[0])}, newest {StateOf(ids[^1])})");
        }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            if (Valid(fresh)) Release(fresh);
            Forget("cx_tfull_new");
            foreach (string n in names) Forget(n);
            _full = hadFull;
            TexBudget.BeginPeriod(had);
        }
        log.Info(check.Ok ? "TexRegistry.FullTableSelfCheck: passed" : "TexRegistry.FullTableSelfCheck: FAILED");
        return check.Ok;
    }
}
