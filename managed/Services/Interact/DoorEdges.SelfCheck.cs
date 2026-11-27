using CX.Framework;

namespace CX.Services.Interact;

/// <summary>Edges of the Door rule no approved example reaches: the engine's door state byte, and the Door key and the
/// Doors toggle pressed through <see cref="InteractInput.KeyDown"/> on a world double. Oracle: the state values
/// GetState returns on both builds, and the object ids and distances written here.</summary>
internal static class DoorEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DoorEdges.SelfCheck");
        try
        {
            foreach ((byte state, bool open, bool destroyed) in new (byte, bool, bool)[] { (0, false, false), (1, true, false), (2, true, false), (3, false, true) })
            {
                var t = new Thing { Kind = ThingKind.Door };
                InteractEngineWorld.ReadDoorState(ref t, state);
                check.Expect(t.Open == open && t.Destroyed == destroyed,
                    $"door state {state} reads open {open}, destroyed {destroyed}: read open {t.Open}, destroyed {t.Destroyed}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint at = w.Door(10f);
                w.Press('F');
                check.Expect(w.Did(at), $"a closed door exactly 10m away opens: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                w.Door(10.1f);
                w.Press('F');
                check.Expect(w.Acts.Count == 0, $"a closed door 10.1m away is out of reach: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Doors.Set(false); InteractKnobs.DoorKey.Set("O");
                uint d = w.Door(4f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"Interact without the Doors rule leaves the door shut: took {took}, {w.Seen}");
                w.Press('O');
                check.Expect(w.Did(d), $"the Door key still opens it: {w.Seen}");
            }
            foreach ((string what, bool open, bool destroyed, bool leadsAway) in new (string, bool, bool, bool)[]
                     { ("an open door", true, false, false), ("a destroyed door", false, true, false), ("a transition door", false, false, true) })
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.DoorKey.Set("O");
                w.Door(4f, open: open, destroyed: destroyed, leadsAway: leadsAway);
                w.Press('O');
                check.Expect(w.Acts.Count == 0, $"the Door key passes over {what} 4m away: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.DoorKey.Set("O");
                w.Door(4f, trap: true);
                w.Press('O');
                check.Expect(w.Acts.Count == 0, $"the Door key passes over a door 4m away whose trap the player found: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.DoorKey.Set("O");
                w.Item(2f);
                w.Press('O');
                check.Expect(w.Acts.Count == 0, $"the Door key passes over an item 2m away: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "DoorEdges.SelfCheck: passed" : "DoorEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
