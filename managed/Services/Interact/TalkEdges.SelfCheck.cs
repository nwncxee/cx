using CX.Framework;

namespace CX.Services.Interact;

/// <summary>Edges of the Talk rule no approved example reaches, pressed through <see cref="InteractInput.KeyDown"/> on a
/// world double. Oracle: the object ids and distances written here.</summary>
internal static class TalkEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TalkEdges.SelfCheck");
        try
        {
            {
                var w = InteractWorldDouble.Install();
                uint mute = w.Creature(3f, talks: false);
                w.Press('F');
                w.Change(mute, t => { t.Dead = true; t.Lootable = true; return t; });
                w.Press('F');
                check.Expect(w.Did(mute, mute), $"a creature tried for talk that died lootable is looted: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Talk.Set(false); InteractKnobs.TalkKey.Set("K");
                uint friend = w.Creature(5f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"Interact without the Talk rule leaves the creature: took {took}, {w.Seen}");
                w.Press('K');
                check.Expect(w.Did(friend), $"the Talk key still talks to it: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TalkKey.Set("K");
                w.Corpse(2f, lootable: false, attitude: Attitude.Friend);
                w.Press('F');
                w.Press('K');
                check.Expect(w.Acts.Count == 0 && w.Lines.Count == 2 && w.Lines[0] == InteractWorldDouble.Nothing,
                    $"a dead friendly creature that cannot be looted is never talked to, by Interact or the Talk key: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "TalkEdges.SelfCheck: passed" : "TalkEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
