using CX.Framework;

namespace CX.Services.Interact;

/// <summary>The nothing-qualifies feedback approved examples (the line and the sound), each pressed through
/// <see cref="InteractInput.KeyDown"/> on a world double. Oracle: the lines and resrefs written here.</summary>
internal static class FeedbackApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FeedbackApprovedExamples.SelfCheck");
        try
        {
            // Given nothing Interact can act on within reach
            // When the player presses Interact
            // Then a feedback line shows and a quiet sound plays
            {
                var w = InteractWorldDouble.Install();
                bool took = w.Press('F');
                check.Expect(took && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing && w.Sounds.Count == 1, $"E47 a line shows and a quiet sound plays: took {took}, {w.Seen}");
            }

            // Given the feedback text setting is off and nothing within reach
            // When the player presses Interact
            // Then no line shows and the sound still plays
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.FeedbackText.Set(false);
                bool took = w.Press('F');
                check.Expect(took && w.Lines.Count == 0 && w.Sounds.Count == 1 && w.Sounds[0] == "gui_nowalk", $"E48 no line, the sound still plays: took {took}, {w.Seen}");
            }

            // Given the feedback sound setting is off and nothing within reach
            // When the player presses Interact
            // Then the line shows and no sound plays
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.FeedbackSound.Set(false);
                bool took = w.Press('F');
                check.Expect(took && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing && w.Sounds.Count == 0, $"E49 the line shows, no sound plays: took {took}, {w.Seen}");
            }

            // Given the feedback sound resref is set to another base-game sound
            // When the player presses Interact with nothing within reach
            // Then that sound plays
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Sound.Set("gui_select");
                w.Press('F');
                check.Expect(w.Sounds.Count == 1 && w.Sounds[0] == "gui_select", $"E50 the chosen sound plays: {w.Seen}");
            }

            // Given the feedback sound resref names no sound the game has
            // When the player looks at the feedback settings
            // Then the resref row says the sound was not found, and a press shows the line and plays nothing
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Sound.Set("cx_nosuchsound");
                string? note = InteractKnobs.Sound.Note?.Invoke();
                check.Expect(note != null && note.Contains("not found", System.StringComparison.OrdinalIgnoreCase), $"E51 the resref row says the sound was not found: '{note}'");
                bool took = w.Press('F');
                check.Expect(took && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing && w.Sounds.Count == 0, $"E51 a press shows the line and plays nothing: took {took}, {w.Seen}");
            }

            // Given nothing within reach and the player holds the Interact key down
            // When the key repeats
            // Then one line shows and one sound plays
            {
                var w = InteractWorldDouble.Install();
                w.Press('F');
                for (int i = 0; i < 4; i++) w.Press('F', repeat: true);
                check.Expect(w.Lines.Count == 1 && w.Sounds.Count == 1, $"E52 one line and one sound through the repeats: {w.Seen}");
            }

            // Given the Loot key is bound and nothing lootable within reach
            // When the player presses the Loot key
            // Then the line says there is nothing to loot
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.LootKey.Set("L");
                w.Item(2f);
                w.Press('L');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.NoLoot, $"E53 the Loot key's own line: {w.Seen}");
            }

            // Given the feedback settings are untouched and nothing is within reach
            // When the player presses Interact
            // Then the line shows and the sound the stock client plays when it refuses a move plays
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.FeedbackText.Reset(); InteractKnobs.FeedbackSound.Reset(); InteractKnobs.Sound.Reset();
                w.Press('F');
                check.Expect(w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing && w.Sounds.Count == 1 && w.Sounds[0] == "gui_nowalk", $"E125 the line and the stock refused-move sound: {w.Seen}");
            }

            // Given a body bag 4m away
            // When the player presses Interact
            // Then its loot window opens, and no line shows and no sound plays
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(4f);
                w.Press('F');
                check.Expect(w.Did(bag) && w.Lines.Count == 0 && w.Sounds.Count == 0, $"E126 the bag opens with no line and no sound: {w.Seen}");
            }

            // Given Interact has tried a locked body bag 3m away and nothing else is within reach
            // When the player presses Interact
            // Then the line shows and the sound plays
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, locked: true);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(bag) && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing && w.Sounds.Count == 1, $"E127 the tried bag is passed over and the line and sound fire: {w.Seen}");
            }

            // Given a chest 3m away, a lever 2m away and nothing else within reach
            // When the player presses the Loot key
            // Then the line says there is nothing to loot, and neither the chest nor the lever is used
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.LootKey.Set("L");
                w.Chest(3f); w.Lever(2f);
                w.Press('L');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.NoLoot, $"E128 the Loot key uses neither placeable and says there is nothing to loot: {w.Seen}");
            }

            // Given only a hostile creature 2m away
            // When the player presses Interact
            // Then the line shows and the creature is not attacked
            {
                var w = InteractWorldDouble.Install();
                w.Creature(2f, Attitude.Hostile, talks: false);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E129 the line shows and the creature gets no action: {w.Seen}");
            }

            // Given only a body bag 3m away whose trap the player found
            // When the player presses Interact
            // Then the line shows and the body bag stays shut
            {
                var w = InteractWorldDouble.Install();
                w.Bag(3f, trap: true);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E130 the line shows and the trapped bag stays shut: {w.Seen}");
            }

            // Given the Transition, Pick up, Door and Talk keys are bound and nothing for any of them is within reach
            // When the player presses each key
            // Then each shows its own rule's line, as the probe words it
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TransitionKey.Set("T"); InteractKnobs.PickUpKey.Set("P"); InteractKnobs.DoorKey.Set("O"); InteractKnobs.TalkKey.Set("K");
                w.Press('T'); w.Press('P'); w.Press('O'); w.Press('K');
                check.Expect(w.Lines.Count == 4 && w.Lines[0] == InteractWorldDouble.NoWayOut && w.Lines[1] == InteractWorldDouble.NoPickUp
                             && w.Lines[2] == InteractWorldDouble.NoDoor && w.Lines[3] == InteractWorldDouble.NoTalk, $"E131 each key's own line: {w.Seen}");
            }

            // Given the Interact master toggle is off, F is bound to Interact and nothing is within reach
            // When the player presses F
            // Then no line shows, no sound plays, and F reaches the game
            {
                var w = InteractWorldDouble.Install();
                bool tookOn = w.Press('F');
                check.Expect(tookOn && w.Lines.Count == 1, $"E132 control: with the master on the press says nothing qualifies: took {tookOn}, {w.Seen}");
                w.Lines.Clear(); w.Sounds.Clear();
                InteractKnobs.Enabled.Set(false);
                bool took = w.Press('F');
                check.Expect(!took && w.Lines.Count == 0 && w.Sounds.Count == 0, $"E132 master off: no line, no sound, F reaches the game: took {took}, {w.Seen}");
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
