using CX.Framework;

namespace CX.Services.Interact;

/// <summary>The Loot rule's approved examples, each pressed through <see cref="InteractInput.KeyDown"/> on a world
/// double. Oracle: the object ids, distances and lines written here.</summary>
internal static class LootApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LootApprovedExamples.SelfCheck");
        try
        {
            // Given a body bag 4m away, left by a creature that died, with no trap the player found
            // When the player presses Interact
            // Then its loot window opens, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(4f);
                bool took = w.Press('F');
                check.Expect(took && w.Did(bag), $"E8 the body bag 4m away opens: {w.Seen}");
            }

            // Given one body bag 3m away and another 6m away
            // When the player presses Interact
            // Then the body bag 3m away opens
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Bag(3f); w.Bag(6f);
                w.Press('F');
                check.Expect(w.Did(near), $"E9 the body bag 3m away opens: {w.Seen}");
            }

            // Given a body bag 3m away whose trap the player found, and a safe body bag 6m away
            // When the player presses Interact
            // Then the body bag 6m away opens
            {
                var w = InteractWorldDouble.Install();
                w.Bag(3f, trap: true); uint safe = w.Bag(6f);
                w.Press('F');
                check.Expect(w.Did(safe), $"E10 the safe body bag 6m away opens, not the trapped one: {w.Seen}");
            }

            // Given a body bag 11m away and nothing nearer
            // When the player presses Interact
            // Then nothing opens
            {
                var w = InteractWorldDouble.Install();
                w.Bag(11f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E12 Interact takes the press and nothing opens: took {took}, {w.Seen}");
            }

            // Given a body bag 4m away behind an area wall
            // When the player presses Interact
            // Then it does not open
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(4f); w.WallBefore(bag);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E13 Interact takes the press and the bag behind the wall does not open: took {took}, {w.Seen}");
            }

            // Given a lootable corpse 5m away
            // When the player presses Interact
            // Then its loot window opens
            {
                var w = InteractWorldDouble.Install();
                uint corpse = w.Corpse(5f);
                w.Press('F');
                check.Expect(w.Did(corpse), $"E14 the lootable corpse's loot window opens: {w.Seen}");
            }

            // Given a hostile creature 1m away and a body bag 5m away
            // When the player presses Interact
            // Then the body bag opens and the creature is not attacked
            {
                var w = InteractWorldDouble.Install();
                w.Creature(1f, Attitude.Hostile, talks: false); uint bag = w.Bag(5f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E15 the body bag opens and the hostile creature gets no action: {w.Seen}");
            }

            // Given the player holds the Interact key down, with two body bags within reach
            // When the key repeats
            // Then one loot window opens, once
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Bag(3f); w.Bag(5f);
                w.Press('F');
                for (int i = 0; i < 4; i++) w.Press('F', repeat: true);
                check.Expect(w.Did(near), $"E16 one loot window opens, once, through the repeats: {w.Seen}");
            }

            // Given the Interact master toggle is off and F is bound to Interact
            // When the player presses F
            // Then nothing Interact does happens, and F reaches the game
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f);
                bool tookOn = w.Press('F');
                check.Expect(tookOn && w.Did(bag), $"E17 control: with the master on F opens the bag: took {tookOn}, {w.Seen}");
                w.Acts.Clear(); w.Lines.Clear(); w.Sounds.Clear();
                InteractKnobs.Enabled.Set(false);
                bool took = w.Press('F');
                check.Expect(!took && w.Acts.Count == 0 && w.Lines.Count == 0 && w.Sounds.Count == 0, $"E17 master off: F reaches the game and Interact does nothing: took {took}, {w.Seen}");
            }

            // Given the Loot rule is left out of Interact, the Loot key is bound, and a body bag is 5m away
            // When the player presses Interact, then the Loot key
            // Then Interact leaves the body bag shut, and the Loot key opens it
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Loot.Set(false); InteractKnobs.LootKey.Set("L");
                uint bag = w.Bag(5f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E18 Interact without the Loot rule leaves the body bag shut: took {took}, {w.Seen}");
                w.Press('L');
                check.Expect(w.Did(bag), $"E18 the Loot key opens it: {w.Seen}");
            }

            // Given a locked body bag 3m away that Interact has not tried, and nothing else within reach
            // When the player presses Interact
            // Then the character tries to open it, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, locked: true);
                w.Press('F');
                check.Expect(w.Did(bag), $"E59 the character tries the locked bag, as a click would: {w.Seen}");
            }

            // Given Interact has tried a locked body bag 3m away, and a lootable corpse lies 6m away
            // When the player presses Interact
            // Then the corpse's loot window opens
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, locked: true); uint corpse = w.Corpse(6f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(bag, corpse), $"E60 after the tried locked bag, the corpse's loot window opens: {w.Seen}");
            }

            // Given a useable lever 2m away and nothing else within reach
            // When the player presses Interact
            // Then the lever is not pulled
            {
                var w = InteractWorldDouble.Install();
                w.Lever(2f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E61 Interact takes the press and the lever is not pulled: took {took}, {w.Seen}");
            }

            // Given a useable chest 3m away and a body bag 6m away
            // When the player presses Interact
            // Then the body bag opens, and the chest stays shut
            {
                var w = InteractWorldDouble.Install();
                w.Chest(3f); uint bag = w.Bag(6f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E62 the body bag opens and the chest stays shut: {w.Seen}");
            }

            // Given Interact tried a locked body bag 3m away, the bag has since been unlocked, and a lootable corpse lies 6m away
            // When the player presses Interact
            // Then the body bag's loot window opens
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, locked: true); uint corpse = w.Corpse(6f);
                w.Press('F');
                w.Change(bag, t => { t.Locked = false; return t; });
                w.Press('F');
                check.Expect(w.Did(bag, bag), $"E76 the bag, unlocked since its try, opens before the corpse: {w.Seen}");
            }

            // Given the player clicked a locked body bag 3m away and it stayed shut, and Interact has never tried it
            // When the player presses Interact
            // Then the character tries to open it, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, locked: true);
                w.Click(bag);
                w.Press('F');
                check.Expect(w.Did(bag), $"E77 a mouse click is not Interact's try: the character tries the bag: {w.Seen}");
            }

            // Given a body bag 3m away with a trap the player has not found, and an untrapped body bag 6m away
            // When the player presses Interact
            // Then the character goes to the body bag 3m away, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Bag(3f, unfoundTrap: true); w.Bag(6f);
                w.Press('F');
                check.Expect(w.Did(near), $"E78 an unfound trap is unknown to the player: the bag 3m away is acted on: {w.Seen}");
            }

            // Given a dead creature 2m away that cannot be looted, and a lootable corpse 5m away
            // When the player presses Interact
            // Then the lootable corpse's loot window opens
            {
                var w = InteractWorldDouble.Install();
                w.Corpse(2f, lootable: false); uint corpse = w.Corpse(5f);
                w.Press('F');
                check.Expect(w.Did(corpse), $"E79 the lootable corpse opens, not the dead creature that cannot be looted: {w.Seen}");
            }

            // Given an item on the ground 1m away and a body bag 5m away
            // When the player presses the Loot key
            // Then the body bag opens, and the item stays where it is
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.LootKey.Set("L");
                w.Item(1f); uint bag = w.Bag(5f);
                w.Press('L');
                check.Expect(w.Did(bag), $"E80 the Loot key opens the body bag and leaves the item: {w.Seen}");
            }

            // Given an item on the ground 1m away and a body bag 6m away
            // When the player presses Interact
            // Then the body bag opens
            {
                var w = InteractWorldDouble.Install();
                w.Item(1f); uint bag = w.Bag(6f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E81 Loot comes before Pick up: the body bag opens: {w.Seen}");
            }

            // Given a body bag 4m away behind a closed door, with no area wall between
            // When the player presses Interact
            // Then the character goes to it, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(4f); w.DoorBefore(bag, 2f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E82 a closed door does not block sight: the character goes to the bag: {w.Seen}");
            }

            // Given the player possesses a familiar, the server does not let associates interact, and a body bag is 3m away
            // When the player presses Interact
            // Then the body bag stays shut, as a click would leave it
            {
                var w = InteractWorldDouble.Install();
                w.PossessedBarred = true;
                uint bag = w.Bag(3f);
                w.Press('F');
                check.Expect(w.Did(bag) && w.ServerRefused.Count == 1 && w.Lines.Count == 0,
                    $"E83 Interact runs the bag's default action once, as a click would, and the server's refusal leaves it shut: {w.Seen}, refused {w.ServerRefused.Count}");
            }

            // Given a body bag 9.5m away and nothing nearer
            // When the player presses Interact
            // Then it opens
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(9.5f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E84 the body bag 9.5m away opens: {w.Seen}");
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
