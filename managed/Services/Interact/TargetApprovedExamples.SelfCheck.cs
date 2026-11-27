using CX.Framework;
using CX.Services.Camera;

namespace CX.Services.Interact;

/// <summary>The pointed-target approved examples (the action camera's crosshair and the tab-target lock), each pressed
/// through <see cref="InteractInput.KeyDown"/> on a world double. Oracle: the object ids and lines written here.</summary>
internal static class TargetApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TargetApprovedExamples.SelfCheck");
        try
        {
            // Given the action camera is on, its crosshair is on a body bag 6m away, and another body bag is 2m away
            // When the player presses Interact
            // Then the body bag under the crosshair opens
            {
                var w = InteractWorldDouble.Install();
                uint aimed = w.Bag(6f); w.Bag(2f);
                w.Crosshair = aimed;
                w.Press('F');
                check.Expect(w.Did(aimed), $"E40 the body bag under the crosshair opens: {w.Seen}");
            }

            // Given the action camera is on and its crosshair is on a hostile creature, with a body bag 2m away
            // When the player presses Interact
            // Then nothing opens, the creature is not attacked, and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                uint foe = w.Creature(4f, Attitude.Hostile, talks: false); w.Bag(2f);
                w.Crosshair = foe;
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E41 the hostile target is refused: {w.Seen}");
            }

            // Given the action camera is on, nothing is under the crosshair, and a body bag is 3m away
            // When the player presses Interact
            // Then the body bag opens
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E42 no crosshair target: the nearest rule runs: {w.Seen}");
            }

            // Given tab-target is locked on a friendly creature with a conversation 7m away, and a body bag is 2m away
            // When the player presses Interact
            // Then the conversation opens
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(7f); w.Bag(2f);
                TabTarget.Enabled.Set(true); w.Lock = friend;
                w.Press('F');
                check.Expect(w.Did(friend), $"E43 the locked creature's conversation opens: {w.Seen}");
            }

            // Given the action camera is on and its crosshair is on a body bag whose trap the player found
            // When the player presses Interact
            // Then the body bag stays shut and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f, trap: true);
                w.Crosshair = bag;
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E44 the trapped target is refused: {w.Seen}");
            }

            // Given the action camera is on and its crosshair is on a body bag 15m away
            // When the player presses Interact
            // Then the character goes to the body bag and opens it, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(15f);
                w.Crosshair = bag;
                w.Press('F');
                check.Expect(w.Did(bag), $"E45 a pointed bag beyond 10m is acted on: {w.Seen}");
            }

            // Given the action camera is on, its crosshair is on a friendly creature with a conversation, and a body bag is 3m away
            // When the player presses the Loot key
            // Then no body bag opens and no conversation opens, and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.LootKey.Set("L");
                uint friend = w.Creature(4f); w.Bag(3f);
                w.Crosshair = friend;
                w.Press('L');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E46 the Loot key refuses a creature target: {w.Seen}");
            }

            // Given the action camera is on, its crosshair is on a chest, and a body bag is 2m away
            // When the player presses Interact
            // Then neither opens, and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                uint chest = w.Chest(4f); w.Bag(2f);
                w.Crosshair = chest;
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E117 a chest target is refused: {w.Seen}");
            }

            // Given tab-target is locked on a hostile creature 5m away, and a body bag is 2m away
            // When the player presses Interact
            // Then the creature is not attacked, the body bag does not open, and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                uint foe = w.Creature(5f, Attitude.Hostile, talks: false); w.Bag(2f);
                TabTarget.Enabled.Set(true); w.Lock = foe;
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E118 a locked hostile is refused: {w.Seen}");
            }

            // Given the action camera is on and its crosshair is on the lootable corpse of a hostile creature
            // When the player presses Interact
            // Then the corpse's loot window opens and nothing is attacked
            {
                var w = InteractWorldDouble.Install();
                uint corpse = w.Corpse(4f, attitude: Attitude.Hostile);
                w.Crosshair = corpse;
                w.Press('F');
                check.Expect(w.Did(corpse), $"E119 the hostile's lootable corpse opens: {w.Seen}");
            }

            // Given the action camera is on, its crosshair is on a locked body bag Interact has not tried, and another body bag is 3m away
            // When the player presses Interact
            // Then the character tries the locked bag, as a click would, and the other bag does not open
            {
                var w = InteractWorldDouble.Install();
                uint locked = w.Bag(5f, locked: true); w.Bag(3f);
                w.Crosshair = locked;
                w.Press('F');
                check.Expect(w.Did(locked), $"E120 the untried locked target is tried: {w.Seen}");
            }

            // Given the action camera is on, its crosshair is on a locked body bag Interact already tried, and another body bag is 3m away
            // When the player presses Interact
            // Then the locked bag is not tried again, the other bag does not open, and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                uint locked = w.Bag(5f, locked: true); w.Bag(3f);
                w.Crosshair = locked;
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(locked) && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E121 the tried locked target is refused: {w.Seen}");
            }

            // Given the action camera is on and its crosshair is on a closed area-transition door 10m away
            // When the player presses Interact
            // Then the character stays in the area and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Door(10f, leadsAway: true);
                w.Crosshair = exit;
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E122 a pointed transition 10m away is refused: {w.Seen}");
            }

            // Given tab-target mode is on, the player pressed Interact and opened a body bag now 6m away, and the player stands 1m from an area transition
            // When the player presses Interact
            // Then the player takes the transition
            {
                var w = InteractWorldDouble.Install();
                TabTarget.Enabled.Set(true);
                uint bag = w.Bag(1.5f);
                w.Press('F');
                w.StepBack(bag, 6f);
                uint exit = w.Transition(1f);
                w.Press('F');
                check.Expect(w.LockChanges.Count == 0 && w.Did(bag, exit), $"E123 the press asked for no lock and the transition is taken: {w.LockChanges.Count} lock change(s), {w.Seen}");
            }

            // Given the action camera is on with its crosshair on a body bag, and tab-target is locked on a friendly creature with a conversation
            // When the player presses Interact
            // Then the body bag opens and no conversation opens
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f); uint friend = w.Creature(5f);
                TabTarget.Enabled.Set(true); w.Lock = friend; w.Crosshair = bag;
                w.Press('F');
                check.Expect(w.Did(bag), $"E124 the crosshair beats the lock: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "TargetApprovedExamples.SelfCheck: passed" : "TargetApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
