using CX.Framework;

namespace CX.Services.Interact;

/// <summary>The Door rule's approved examples, each pressed through <see cref="InteractInput.KeyDown"/> on a world
/// double. Oracle: the object ids, distances and lines written here.</summary>
internal static class DoorApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DoorApprovedExamples.SelfCheck");
        try
        {
            // Given an unlocked closed door 4m away with no trap the player found
            // When the player presses Interact
            // Then the door opens
            {
                var w = InteractWorldDouble.Install();
                uint d = w.Door(4f);
                w.Press('F');
                check.Expect(w.Did(d), $"E29 the door opens: {w.Seen}");
            }

            // Given a closed door 4m away whose trap the player found
            // When the player presses Interact
            // Then the door stays shut
            {
                var w = InteractWorldDouble.Install();
                w.Door(4f, trap: true);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E31 Interact takes the press and the trapped door stays shut: took {took}, {w.Seen}");
            }

            // Given a door 5m away that leads to another area
            // When the player presses Interact
            // Then the player stays in the area
            {
                var w = InteractWorldDouble.Install();
                w.Door(5f, leadsAway: true);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E33 Interact takes the press and the transition door 5m away is not taken: took {took}, {w.Seen}");
            }

            // Given the Door key is bound, a body bag 1m away and an unlocked closed door 4m away
            // When the player presses the Door key
            // Then the door opens and the body bag stays shut
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.DoorKey.Set("O");
                w.Bag(1f); uint d = w.Door(4f);
                w.Press('O');
                check.Expect(w.Did(d), $"E34 the Door key opens the door and leaves the body bag: {w.Seen}");
            }

            // Given a locked closed door 4m away that Interact has not tried, and nothing else within reach
            // When the player presses Interact
            // Then the character tries the door, as clicking it would, and the door stays shut
            {
                var w = InteractWorldDouble.Install();
                uint d = w.Door(4f, locked: true);
                w.Press('F');
                check.Expect(w.Did(d), $"E54 the character tries the locked door, as a click would: {w.Seen}");
            }

            // Given the player has pressed Interact on a locked door 4m away, and a safe closed door is 6m away
            // When the player presses Interact again
            // Then the door 6m away opens
            {
                var w = InteractWorldDouble.Install();
                uint locked = w.Door(4f, locked: true); uint safe = w.Door(6f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(locked, safe), $"E55 the tried locked door is passed over and the door 6m away opens: {w.Seen}");
            }

            // Given the player clicked a locked door 4m away with the mouse, and a safe closed door is 6m away
            // When the player presses Interact
            // Then the character tries the locked door
            {
                var w = InteractWorldDouble.Install();
                uint locked = w.Door(4f, locked: true); w.Door(6f);
                w.Click(locked);
                w.Press('F');
                check.Expect(w.Did(locked), $"E56 a click is not Interact's try: the character tries the locked door: {w.Seen}");
            }

            // Given Interact has tried a locked door 4m away, and someone has since unlocked it
            // When the player presses Interact
            // Then the door opens
            {
                var w = InteractWorldDouble.Install();
                uint d = w.Door(4f, locked: true);
                w.Press('F');
                w.Change(d, t => { t.Locked = false; return t; });
                w.Press('F');
                check.Expect(w.Did(d, d), $"E57 the door, unlocked since its try, opens: {w.Seen}");
            }

            // Given Interact has tried the only door within reach and it is still locked
            // When the player presses Interact
            // Then the character stays put, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                uint d = w.Door(4f, locked: true);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(d) && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E58 the second press acts on nothing and says so: {w.Seen}");
            }

            // Given an open door 2m away and an unlocked closed door 6m away
            // When the player presses Interact
            // Then the door 6m away opens and the open door stays open
            {
                var w = InteractWorldDouble.Install();
                w.Door(2f, open: true); uint d = w.Door(6f);
                w.Press('F');
                check.Expect(w.Did(d), $"E103 the open door is passed over and the door 6m away opens: {w.Seen}");
            }

            // Given a chest 1m away and an unlocked closed door 4m away
            // When the player presses Interact
            // Then the door opens and the chest stays shut
            {
                var w = InteractWorldDouble.Install();
                w.Chest(1f); uint d = w.Door(4f);
                w.Press('F');
                check.Expect(w.Did(d), $"E104 the door opens and the chest stays shut: {w.Seen}");
            }

            // Given an unlocked closed door 11m away and nothing else within reach
            // When the player presses Interact
            // Then the character stays put, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                w.Door(11f);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E105 nothing within reach: Interact says so: {w.Seen}");
            }

            // Given unlocked closed doors 4m and 7m away
            // When the player presses Interact
            // Then the door 4m away opens and the door 7m away stays shut
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Door(4f); w.Door(7f);
                w.Press('F');
                check.Expect(w.Did(near), $"E106 the door 4m away opens: {w.Seen}");
            }

            // Given an unlocked closed door 4m away behind an area wall, and another 8m away in plain view
            // When the player presses Interact
            // Then the door 8m away opens
            {
                var w = InteractWorldDouble.Install();
                uint walled = w.Door(4f); w.WallBefore(walled); uint d = w.Door(8f);
                w.Press('F');
                check.Expect(w.Did(d), $"E107 the door in plain view opens: {w.Seen}");
            }

            // Given a destroyed door 3m away and an unlocked closed door 6m away
            // When the player presses Interact
            // Then the door 6m away opens
            {
                var w = InteractWorldDouble.Install();
                w.Door(3f, destroyed: true); uint d = w.Door(6f);
                w.Press('F');
                check.Expect(w.Did(d), $"E108 the destroyed door is passed over: {w.Seen}");
            }

            // Given the player pressed the Door key on a locked door 4m away, and an unlocked closed door is 6m away
            // When the player presses Interact
            // Then the door 6m away opens
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.DoorKey.Set("O");
                uint locked = w.Door(4f, locked: true); uint d = w.Door(6f);
                w.Press('O');
                w.Press('F');
                check.Expect(w.Did(locked, d), $"E109 the Door key's try counts for Interact: the door 6m away opens: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Defaults();
            InteractInput.Use(null);
        }
        return check.Ok;
    }
}
