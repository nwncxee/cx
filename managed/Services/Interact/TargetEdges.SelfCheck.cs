using CX.Framework;

namespace CX.Services.Interact;

/// <summary>Edges of the pointed target no approved example reaches, pressed through <see cref="InteractInput.KeyDown"/>
/// on a world double. Oracle: the object ids and lines written here.</summary>
internal static class TargetEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TargetEdges.SelfCheck");
        try
        {
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(15f); w.Bag(2f);
                w.Crosshair = item;
                w.Press('F');
                check.Expect(w.Did(item), $"an item under the crosshair is picked up, beyond reach too: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint door = w.Door(6f); w.Bag(2f);
                w.Crosshair = door;
                w.Press('F');
                check.Expect(w.Did(door), $"a closed door under the crosshair opens: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(4f); w.Bag(2f);
                w.Crosshair = item;
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(item) && w.Lines.Count == 1 && w.Lines[0] == InteractWorldDouble.Refused,
                             $"an item under the crosshair still on the ground after its try is refused: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint foe = w.Creature(4f, Attitude.Hostile, talks: false);
                w.Crosshair = foe;
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"a refused press is taken, so the game's own binding never sees it: took {took}, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f);
                w.Press('F');
                check.Expect(w.Did(bag) && w.Lines.Count == 0, $"with nothing under the crosshair the nearest bag opens and no line shows: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "TargetEdges.SelfCheck: passed" : "TargetEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
