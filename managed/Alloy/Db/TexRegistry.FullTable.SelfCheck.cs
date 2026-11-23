using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy.Db;

internal static unsafe partial class TexRegistry
{
    /// <summary>The first slot no texture has taken yet, for a check to hand back with <see cref="RewindTo"/>.</summary>
    internal static int Watermark => System.Threading.Volatile.Read(ref _high);

    /// <summary>How many once-per-cause lines the registry has said this session.</summary>
    internal static int LoudKeys { get { lock (_loudOnce) return _loudOnce.Count; } }

    /// <summary>Hands back every slot from <paramref name="high"/> up as never used, so a check that filled the table
    /// leaves the session the slots <see cref="SlotsShort"/> answers from without a walk. False, and nothing changed,
    /// while any of those slots still holds a texture.</summary>
    internal static bool RewindTo(int high)
    {
        lock (_lock)
        {
            if (high < 1 || high > _high) return false;
            for (int i = high; i < _high; i++)
                if (_state[i] != TexState.Free) return false;
            int head = -1, tail = -1;
            for (int k = _freeHead; k >= 0; k = _freeNext[k])
            {
                if (k >= high) continue;
                if (tail < 0) head = k; else _freeNext[tail] = k;
                tail = k;
            }
            if (tail >= 0) _freeNext[tail] = -1;
            _freeHead = head;
            _high = high;
            return true;
        }
    }

    /// <summary>The registry's log off until disposed: a fixture of thousands of textures writes none of its lines
    /// to the player's log and claims none of their once-per-name keys.</summary>
    internal static Quieted Quiet()
    {
        var q = new Quieted(_log);
        _log = null;
        return q;
    }

    /// <summary>Hands the registry back the log <see cref="Quiet"/> took.</summary>
    internal readonly struct Quieted(ExtenderLog? had) : IDisposable
    {
        public void Dispose() => _log = had;
    }

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
        int mark = Watermark;
        using Quieted quiet = Quiet();
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
            check.Expect(RewindTo(mark), "the full table's textures still hold slots past the session's never-used mark");
        }
        log.Info(check.Ok ? "TexRegistry.FullTableSelfCheck: passed" : "TexRegistry.FullTableSelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Found test: a full table of held textures is short of one open slot, and one released texture is
    /// enough for one and short of two. Oracle: hand-landed textures the check holds, one released.</summary>
    internal static bool SlotsShortSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.SlotsShortSelfCheck");
        TexBudgetInputs had = TexBudget.Inputs;
        var names = new List<string>();
        var ids = new List<TexId>();
        long hadFull = _full;
        int mark = Watermark;
        using Quieted quiet = Quiet();
        bool lastHeld = false;
        try
        {
            TexBudget.BeginPeriod(new TexBudgetInputs(1 << 20, 5169 * ResidencyMiB, 0));
            while (_freeHead >= 0 || _high < MaxSlots || _idleHead >= 0)
            {
                string n = $"cx_tshort_{names.Count}";
                names.Add(n);
                ids.Add(Held(n, 1, 0xF2000000u + (uint)names.Count));
            }
            lastHeld = true;
            check.Expect(SlotsShort(1), $"a table of {names.Count} held textures with no slot open is not short of one");
            Release(ids[^1]);
            lastHeld = false;
            check.Expect(!SlotsShort(1) && SlotsShort(2),
                         $"one released texture in a full table: short of one {SlotsShort(1)}, short of two {SlotsShort(2)}");
        }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            for (int k = 0; k < ids.Count - (lastHeld ? 0 : 1); k++) Release(ids[k]);
            foreach (string n in names) Forget(n);
            _full = hadFull;
            TexBudget.BeginPeriod(had);
            check.Expect(RewindTo(mark), "the held textures still hold slots past the session's never-used mark");
        }
        log.Info(check.Ok ? "TexRegistry.SlotsShortSelfCheck: passed" : "TexRegistry.SlotsShortSelfCheck: FAILED");
        return check.Ok;
    }
}
