using CX.Framework;
using CX.Services.Camera;

namespace CX.Services.Interact;

/// <summary>The Transition rule's approved examples, each pressed through <see cref="InteractInput.KeyDown"/> on a world
/// double. Oracle: the object ids, distances and lines written here.</summary>
internal static class TransitionApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TransitionApprovedExamples.SelfCheck");
        try
        {
            // Given an area transition 1.5m away
            // When the player presses Interact
            // Then the player takes it, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f);
                w.Press('F');
                check.Expect(w.Did(exit), $"E20 the player takes the transition 1.5m away: {w.Seen}");
            }

            // Given an area transition 3m away and nothing else
            // When the player presses Interact
            // Then the player stays in the area
            {
                var w = InteractWorldDouble.Install();
                w.Transition(3f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E21 Interact takes the press and the player stays: took {took}, {w.Seen}");
            }

            // Given an area transition 1.5m away and a body bag 1m away
            // When the player presses Interact
            // Then the player takes the transition
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f); w.Bag(1f);
                w.Press('F');
                check.Expect(w.Did(exit), $"E22 the transition comes before the nearer body bag: {w.Seen}");
            }

            // Given the Transition rule is switched off in the Interact settings, a transition 1.5m away and a body bag 1m away
            // When the player presses Interact
            // Then the body bag opens
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Transitions.Set(false);
                w.Transition(1.5f); uint bag = w.Bag(1f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E23 with the Transition rule off the body bag opens: {w.Seen}");
            }

            // Given the Transition key is bound, a transition 1.5m away and a lootable corpse 1m away
            // When the player presses the Transition key
            // Then the player takes the transition
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TransitionKey.Set("T");
                uint exit = w.Transition(1.5f); w.Corpse(1f);
                w.Press('T');
                check.Expect(w.Did(exit), $"E24 the Transition key takes the transition: {w.Seen}");
            }

            // Given the Transition key is bound, no transition within 2m and a body bag 1m away
            // When the player presses the Transition key
            // Then the body bag stays shut and the player stays in the area
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TransitionKey.Set("T");
                w.Bag(1f);
                bool took = w.Press('T');
                check.Expect(took && w.Acts.Count == 0, $"E85 the Transition key takes the press and acts on nothing: took {took}, {w.Seen}");
            }

            // Given one transition 1.8m away and another 1.2m away
            // When the player presses Interact
            // Then the player takes the one 1.2m away
            {
                var w = InteractWorldDouble.Install();
                w.Transition(1.8f); uint near = w.Transition(1.2f);
                w.Press('F');
                check.Expect(w.Did(near), $"E86 the transition 1.2m away is taken: {w.Seen}");
            }

            // Given a transition trigger whose nearest edge is 1.5m away and whose centre is 8m away
            // When the player presses Interact
            // Then the player walks into it and takes it
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f, 8f);
                w.Press('F');
                check.Expect(w.Did(exit), $"E87 a trigger is measured by its nearest edge: {w.Seen}");
            }

            // Given a transition trigger whose nearest edge is 3m away
            // When the player presses Interact
            // Then the player stays in the area
            {
                var w = InteractWorldDouble.Install();
                w.Transition(3f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E88 a trigger whose nearest edge is 3m away is out of reach: took {took}, {w.Seen}");
            }

            // Given an open transition door 1.5m away
            // When the player presses Interact
            // Then the player takes the transition and the door stays open
            {
                var w = InteractWorldDouble.Install();
                uint door = w.Door(1.5f, open: true, leadsAway: true);
                w.Press('F');
                check.Expect(w.Did(door), $"E89 the open transition door is walked into: {w.Seen}");
            }

            // Given a transition door 1.5m away whose trap the player found, and a body bag 1m away
            // When the player presses Interact
            // Then the body bag opens
            {
                var w = InteractWorldDouble.Install();
                w.Door(1.5f, trap: true, leadsAway: true); uint bag = w.Bag(1f);
                w.Press('F');
                check.Expect(w.Did(bag), $"E90 the trapped transition door is passed over and the body bag opens: {w.Seen}");
            }

            // Given a transition 1.5m away on the far side of an area wall
            // When the player presses Interact
            // Then the player stays in the area
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f); w.WallBefore(exit);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"E91 the transition beyond an area wall is not taken: took {took}, {w.Seen}");
            }

            // Given the player holds Interact down and arrives 1m from the new area's return transition
            // When the area finishes loading
            // Then the player stays in the new area until they press again
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f);
                w.Press('F');
                check.Expect(w.Did(exit), $"E92 the first press takes the transition: {w.Seen}");
                w.Clear();
                uint back = w.Transition(1f);
                InteractInput.AreaLoaded();
                for (int i = 0; i < 3; i++) w.Press('F', repeat: true);
                check.Expect(w.Did(exit), $"E92 the held key does not take the return transition: {w.Seen}");
                w.Press('F');
                check.Expect(w.Did(exit, back), $"E92 a fresh press takes it: {w.Seen}");
            }

            // Given tab-target is locked on a friendly creature with a conversation 1.8m away, and a transition is 1.5m away
            // When the player presses Interact
            // Then the conversation opens and the player stays in the area
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(1.8f); w.Transition(1.5f);
                TabTarget.Enabled.Set(true); w.Lock = friend;
                w.Press('F');
                check.Expect(w.Did(friend), $"E93 the locked target comes first: its conversation opens: {w.Seen}");
            }
        }
        finally
        {
            TabTarget.Enabled.Reset();
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "TransitionApprovedExamples.SelfCheck: passed" : "TransitionApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
