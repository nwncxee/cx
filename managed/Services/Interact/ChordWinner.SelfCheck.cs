using CX.Framework;
using CX.Ui;

namespace CX.Services.Interact;

/// <summary>A key or mouse button two chord consumers want runs only the higher-ranked one: Interact (priority 2) over a
/// priority-1 consumer on the same press. Oracle: the fire counts and the double's acts written here.</summary>
internal static class ChordWinner
{
    /// <summary>A priority-1 chord consumer on one press, counting what reaches it.</summary>
    private sealed class Below : IChordConsumer
    {
        private readonly int _key;
        public int Fired;
        public Below(int key) => _key = key;
        public int Priority => 1;
        public bool WantsKey(int key) => key == _key;
        public void Fire(uint key, int sdlMods) => Fired++;
        public void FireMouse(int button, int sdlMods) => Fired++;
    }

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ChordWinner.SelfCheck");
        var thumb = new Below(-4);
        var key = new Below('f');
        UiHost.AddChordConsumer(thumb);
        UiHost.AddChordConsumer(key);
        try
        {
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Key.Set("Mouse4");
                uint door = w.Door(1.5f);
                UiHost.FireMouseChords(4, 0);
                check.Expect(w.Did(door) && thumb.Fired == 0, $"Mouse4 bound to Interact and wanted by a priority-1 consumer runs only Interact: below fired {thumb.Fired}, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Key.Set("F");
                uint door = w.Door(1.5f);
                UiHost.FireChords('f', 0);
                check.Expect(w.Did(door) && key.Fired == 0, $"F bound to Interact and wanted by a priority-1 consumer runs only Interact: below fired {key.Fired}, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Key.Set("G");
                w.Door(1.5f);
                thumb.Fired = key.Fired = 0;
                UiHost.FireMouseChords(4, 0);
                UiHost.FireChords('f', 0);
                check.Expect(w.Acts.Count == 0 && thumb.Fired == 1 && key.Fired == 1, $"control: with Interact on G, Mouse4 and F reach the priority-1 consumer once each: {thumb.Fired}, {key.Fired}, {w.Seen}");
            }
        }
        finally
        {
            UiHost.RemoveChordConsumer(thumb);
            UiHost.RemoveChordConsumer(key);
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "ChordWinner.SelfCheck: passed" : "ChordWinner.SelfCheck: FAILED");
        return check.Ok;
    }
}
