using CX.Framework;

namespace CX.Services.Interact;

/// <summary>Edges of the Pick up rule no approved example reaches, pressed through <see cref="InteractInput.KeyDown"/> on a
/// world double. Oracle: the object ids and distances written here.</summary>
internal static class PickUpEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PickUpEdges.SelfCheck");
        try
        {
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Item(2f); uint far = w.Item(5f);
                w.Press('F');
                w.Press('F');
                check.Expect(w.Did(near, far), $"an item still on the ground after its try is passed over for the next one: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint near = w.Item(2f); w.Item(5f);
                w.Press('F');
                w.Change(near, t => { t.Pending = true; return t; });
                w.Press('F');
                check.Expect(w.Did(near, near), $"an item whose pickup the server has not answered is acted on again: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint item = w.Item(2f);
                w.Press('F');
                Thing held = w.Of(item);
                w.Remove(item);
                w.Press('F');
                w.Put(held);
                w.Press('F');
                check.Expect(w.Did(item, item), $"an item that left the ground and came back is tried afresh: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.PickUp.Set(false); InteractKnobs.PickUpKey.Set("P");
                uint item = w.Item(5f);
                bool took = w.Press('F');
                check.Expect(took && w.Acts.Count == 0, $"Interact without the Pick up rule leaves the item: took {took}, {w.Seen}");
                w.Press('P');
                check.Expect(w.Did(item), $"the Pick up key still picks it up: {w.Seen}");
            }
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "PickUpEdges.SelfCheck: passed" : "PickUpEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
