using CX.Framework;

namespace CX.Services.Interact;

/// <summary>The Pick up rule's approved examples, each pressed through <see cref="InteractInput.KeyDown"/> on a world
/// double. Oracle: the object ids, distances and lines written here.</summary>
internal static class PickUpApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PickUpApprovedExamples.SelfCheck");
        try
        {
            // Given an item on the ground 3m away and nothing else
            // When the player presses Interact
            // Then the character picks it up
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(3f);
                w.Press('F');
                check.Expect(w.Did(item), $"E25 the character picks up the item 3m away: {w.Seen}");
            }

            // Given a body bag 8m away and an item on the ground 2m away
            // When the player presses Interact
            // Then the body bag opens and the item stays on the ground
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(8f); w.Item(2f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E26 Loot comes before Pick up: the body bag opens: {w.Seen}");
            }

            // Given an item on the ground 11m away and nothing nearer
            // When the player presses Interact
            // Then nothing is picked up
            {
                var w = InteractWorldDouble.Install();
                w.Item(11f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E27 Interact takes the press and nothing is picked up: took {took}, {w.Seen}");
            }

            // Given the Pick up key is bound, a body bag 1m away and an item 3m away
            // When the player presses the Pick up key
            // Then the character picks up the item and the body bag stays shut
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.PickUpKey.Set("P");
                w.Bag(1f); uint item = w.Item(3f);
                w.Press('P');
                check.Expect(w.Did(item), $"E28 the Pick up key picks up the item and leaves the body bag: {w.Seen}");
            }

            // Given a chest 1m away and an item on the ground 4m away
            // When the player presses Interact
            // Then the character picks up the item and the chest stays shut
            {
                var w = InteractWorldDouble.Install();
                w.Chest(1f); uint item = w.Item(4f);
                w.Press('F');
                check.Expect(w.Did(item), $"E94 the item is picked up and the chest stays shut: {w.Seen}");
            }

            // Given an item on the ground 2m away and another 6m away
            // When the player presses Interact
            // Then the character picks up the item 2m away
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Item(2f); w.Item(6f);
                w.Press('F');
                check.Expect(w.Did(near), $"E95 the item 2m away is picked up: {w.Seen}");
            }

            // Given a closed unlocked door 1m away, a friendly creature 1m away and an item on the ground 5m away
            // When the player presses Interact
            // Then the character picks up the item, the door stays shut and no conversation opens
            {
                var w = InteractWorldDouble.Install();
                w.Door(1f); w.Creature(1f); uint item = w.Item(5f);
                w.Press('F');
                check.Expect(w.Did(item), $"E96 Pick up comes before Door and Talk: {w.Seen}");
            }

            // Given an item on the ground 4m away on the far side of an area wall, and nothing else within reach
            // When the player presses Interact
            // Then nothing is picked up
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(4f); w.WallBefore(item);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E97 the item beyond an area wall is not picked up: took {took}, {w.Seen}");
            }

            // Given the player holds the Interact key down, with two items on the ground within reach
            // When the key repeats
            // Then one item is picked up, once
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Item(2f); w.Item(4f);
                w.Press('F');
                for (int i = 0; i < 4; i++) w.Press('F', repeat: true);
                check.Expect(w.Did(near), $"E98 one item is picked up, once, through the repeats: {w.Seen}");
            }

            // Given a hostile creature 2m away wearing items, and nothing on the ground within reach
            // When the player presses Interact
            // Then nothing is picked up and the creature is not attacked
            {
                var w = InteractWorldDouble.Install();
                w.Creature(2f, Attitude.Hostile, talks: false);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E99 Interact takes the press, picks up nothing and attacks nothing: took {took}, {w.Seen}");
            }

            // Given the player possesses a familiar on a server that does not let associates interact, and an item lies 2m away
            // When the player presses Interact
            // Then the item stays on the ground, as clicking it would leave it
            {
                var w = InteractWorldDouble.Install();
                w.PossessedBarred = true;
                uint item = w.Item(2f);
                w.Press('F');
                check.Expect(w.Did(item) && w.ServerRefused.Count == 1 && w.Lines.Count == 0,
                    $"E100 Interact runs the item's default action once, as a click would, and the server's refusal leaves it on the ground: {w.Seen}, refused {w.ServerRefused.Count}");
            }

            // Given the character's inventory is full, Interact has tried an item 2m away once, and a closed unlocked door is 5m away
            // When the player presses Interact again
            // Then the door opens
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(2f); uint door = w.Door(5f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(item, door), $"E101 the refused item is passed over and the door opens: {w.Seen}");
            }

            // Given an item on the ground 9.5m away and nothing nearer
            // When the player presses Interact
            // Then the character picks it up
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(9.5f);
                w.Press('F');
                check.Expect(w.Did(item), $"E102 the item 9.5m away is picked up: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Defaults();
            InteractInput.Use(null);
        }
        log.Info(check.Ok ? "PickUpApprovedExamples.SelfCheck: passed" : "PickUpApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
