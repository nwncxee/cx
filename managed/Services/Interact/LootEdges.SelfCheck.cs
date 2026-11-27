using System.Numerics;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Services.Interact;

/// <summary>Edges of the Loot rule no approved example reaches, pressed through <see cref="InteractInput.KeyDown"/> on a
/// world double. Oracle: the object ids and distances written here.</summary>
internal static class LootEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LootEdges.SelfCheck");
        try
        {
            {
                var w = InteractWorldDouble.Install();
                w.Bag(6f); uint near = w.Bag(3f);
                w.Press('F');
                check.Expect(w.Did(near), $"the nearer bag wins though the world lists the farther first: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint shut = w.Bag(3f); uint bag = w.Bag(6f);
                w.Change(shut, t => { t.Lootable = false; return t; });
                w.Press('F');
                check.Expect(w.Did(bag), $"a body bag that cannot be used is passed over: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f);
                w.Press('F');
                bool took = w.Press('F', repeat: true);
                check.Expect(took && w.Did(bag), $"a held key's repeat is taken too, so the game's binding never sees it: took {took}, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint living = w.Creature(2f, Attitude.Hostile, talks: false); uint bag = w.Bag(5f);
                w.Change(living, t => { t.Lootable = true; return t; });
                w.Press('F');
                check.Expect(w.Did(bag), $"a living creature flagged lootable is not looted: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, locked: true); w.Corpse(6f);
                w.Press('F');
                w.Change(bag, t => { t.Locked = false; return t; });
                w.Press('F');
                w.Change(bag, t => { t.Locked = true; return t; });
                w.Press('F');
                check.Expect(w.Did(bag, bag, bag), $"a bag read unlocked leaves the tried memory, so relocked it is tried afresh: {w.Seen}");            }
            {
                var w = InteractWorldDouble.Install();
                bool tab = TabTarget.Enabled.V;
                TabTarget.Enabled.Set(true);
                try
                {
                    uint bag = w.Bag(3f); uint other = w.Bag(6f);
                    w.Press('F');
                    check.Expect(w.Did(bag) && w.LockChanges.Count == 0, $"Interact's own default action leaves the tab-target lock alone: {w.LockChanges.Count} lock change(s), {w.Seen}");
                    w.DefaultAction(other);
                    check.Expect(w.LockChanges.Count == 1 && w.Lock == other, $"control: a click's default action still locks: {w.LockChanges.Count} lock change(s)");
                }
                finally { TabTarget.Enabled.Set(tab); }
            }
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(10f);
                w.Change(bag, t => { t.Pos = w.Player + new Vector3(10f, 0f, 0f); return t; });
                w.Press('F');
                check.Expect(w.Did(bag), $"a body bag exactly 10m away is in reach and opens: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(10.1f);
                w.Change(bag, t => { t.Pos = w.Player + new Vector3(10.1f, 0f, 0f); return t; });
                w.Press('F');
                check.Expect(w.Acts.Count == 0, $"a body bag 10.1m away is out of reach and stays shut: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                w.Bag(3f);
                bool took = w.PressCode(0);
                check.Expect(!took && w.Acts.Count == 0, $"a key event with no key code is not caught by an unbound Loot key: took {took}, {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "LootEdges.SelfCheck: passed" : "LootEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
