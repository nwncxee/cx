using CX.Framework;
using CX.Services.Camera;
using CX.Services.Dialog;

namespace CX.Services.Interact;

/// <summary>The Talk rule's approved examples, each pressed through <see cref="InteractInput.KeyDown"/> on a world
/// double. Oracle: the object ids, distances and lines written here.</summary>
internal static class TalkApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TalkApprovedExamples.SelfCheck");
        try
        {
            // Given a friendly or neutral creature with a conversation 5m away
            // When the player presses Interact
            // Then its conversation opens
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(5f, Attitude.Friend);
                w.Press('F');
                check.Expect(w.Did(friend), $"E35 the friendly creature's conversation opens: {w.Seen}");
                InteractInput.Use(w = new InteractWorldDouble());
                uint neutral = w.Creature(5f, Attitude.Neutral);
                w.Press('F');
                check.Expect(w.Did(neutral), $"E35 the neutral creature's conversation opens: {w.Seen}");
            }

            // Given the player's own henchman or a party member 2m away, and nothing else
            // When the player presses Interact
            // Then the character stays put, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                w.Creature(2f, party: true);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E36 a party member is passed over and Interact says nothing qualifies: {w.Seen}");
            }

            // Given a hostile creature with a conversation 3m away, and nothing else
            // When the player presses Interact
            // Then the creature is not attacked, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                w.Creature(3f, Attitude.Hostile);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E37 the hostile creature gets no action and Interact says nothing qualifies: {w.Seen}");
            }

            // Given a friendly creature 3m away that Interact has not tried, and nothing else within reach
            // When the player presses Interact
            // Then the character walks up to it and asks to talk, as clicking it would
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(3f, talks: false);
                w.Press('F');
                check.Expect(w.Did(friend), $"E38 the character asks the untried creature to talk, as a click would: {w.Seen}");
            }

            // Given the Talk key is bound, a body bag 1m away and a friendly creature with a conversation 5m away
            // When the player presses the Talk key
            // Then the conversation opens
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TalkKey.Set("K");
                w.Bag(1f); uint friend = w.Creature(5f);
                w.Press('K');
                check.Expect(w.Did(friend), $"E39 the Talk key opens the conversation and leaves the body bag: {w.Seen}");
            }

            // Given Interact has tried a friendly creature 3m away and no conversation opened, and a friendly creature with a conversation is 6m away
            // When the player presses Interact again
            // Then the conversation with the creature 6m away opens
            {
                var w = InteractWorldDouble.Install();
                uint mute = w.Creature(3f, talks: false); uint friend = w.Creature(6f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(mute, friend), $"E63 the creature that opened nothing is passed over: {w.Seen}");
            }

            // Given Interact has tried a friendly creature 3m away and it only spoke a line over its head, and a friendly creature with a conversation is 6m away
            // When the player presses Interact again
            // Then the conversation with the creature 6m away opens
            {
                var w = InteractWorldDouble.Install();
                uint barker = w.Creature(3f, talks: false, barks: true); uint friend = w.Creature(6f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(barker, friend) && w.Conversations.Count == 1 && w.Conversations[0].Id == friend,
                    $"E64 a line over its head is no conversation: the creature 6m away talks: {w.Seen}, {w.Conversations.Count} conversation(s)");
            }

            // Given Interact opened a conversation with a creature 3m away, and the player ended it
            // When the player presses Interact again
            // Then that creature's conversation opens again
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(3f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(friend, friend), $"E65 a creature that talked is asked again: {w.Seen}");
            }

            // Given the CX dialog UI is switched off, Interact has tried a creature 3m away that opened no conversation, and a creature with a conversation is 6m away
            // When the player presses Interact
            // Then the conversation with the creature 6m away opens, in the game's own window
            {
                var w = InteractWorldDouble.Install();
                DialogKnobs.Enabled.Set(false);
                w.GameDialog = true;
                uint mute = w.Creature(3f, talks: false); uint friend = w.Creature(6f);
                w.Press('F');
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(mute, friend, friend) && w.Conversations.Count == 2 && w.Conversations[0] == (friend, true),
                    $"E66 with CX's dialog off the creature 6m away talks in the game's window and counts as talked: {w.Seen}, {w.Conversations.Count} conversation(s)");
            }

            // Given Interact has tried the only creature within reach and no conversation opened
            // When the player presses Interact
            // Then the character stays put, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                uint mute = w.Creature(3f, talks: false);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(mute) && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E67 the tried creature is passed over and Interact says nothing qualifies: {w.Seen}");
            }

            // Given two friendly creatures with a conversation, 4m and 8m away, neither tried
            // When the player presses Interact
            // Then the conversation with the creature 4m away opens
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Creature(4f); w.Creature(8f);
                w.Press('F');
                check.Expect(w.Did(near), $"E110 the creature 4m away talks: {w.Seen}");
            }

            // Given a friendly creature with a conversation 11m away, and nothing else
            // When the player presses Interact
            // Then the character stays put, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                w.Creature(11f);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E111 a creature 11m away is out of reach: {w.Seen}");
            }

            // Given a friendly creature with a conversation 2m away and an unlocked closed door 6m away
            // When the player presses Interact
            // Then the door opens and no conversation opens
            {
                var w = InteractWorldDouble.Install();
                w.Creature(2f); uint d = w.Door(6f);
                w.Press('F');
                check.Expect(w.Did(d), $"E112 Door comes before Talk: the door opens: {w.Seen}");
            }

            // Given an untried friendly creature 3m away that has no conversation, and a friendly creature with a conversation 6m away
            // When the player presses Interact once
            // Then the character asks the creature 3m away and stops there
            {
                var w = InteractWorldDouble.Install();
                uint mute = w.Creature(3f, talks: false); w.Creature(6f);
                w.Press('F');
                check.Expect(w.Did(mute), $"E113 one press asks the creature 3m away and stops: {w.Seen}");
            }

            // Given a friendly creature with a conversation 5m away behind an area wall, and nothing else
            // When the player presses Interact
            // Then the character stays put, and Interact says nothing qualifies
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(5f); w.WallBefore(friend);
                w.Press('F');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Nothing, $"E114 the creature beyond an area wall is out of sight: {w.Seen}");
            }

            // Given another player's character 2m away, not in the party, and a friendly creature with a conversation 6m away
            // When the player presses Interact
            // Then the conversation with the creature 6m away opens
            {
                var w = InteractWorldDouble.Install();
                w.Creature(2f, otherPlayer: true); uint friend = w.Creature(6f);
                w.Press('F');
                check.Expect(w.Did(friend), $"E115 another player's character is passed over: {w.Seen}");
            }

            // Given tab-target is locked on a hostile creature 4m away and a friendly creature with a conversation is 7m away
            // When the player presses the Talk key
            // Then the hostile creature is not attacked, no conversation opens, and the feedback line shows
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TalkKey.Set("K");
                uint foe = w.Creature(4f, Attitude.Hostile); w.Creature(7f);
                TabTarget.Enabled.Set(true); w.Lock = foe;
                w.Press('K');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused, $"E116 the locked hostile is refused and nothing else is asked: {w.Seen}");
            }
        }
        finally
        {
            TabTarget.Enabled.Reset();
            DialogKnobs.Enabled.Reset();
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "TalkApprovedExamples.SelfCheck: passed" : "TalkApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
