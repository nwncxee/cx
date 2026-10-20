using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

public static partial class UiHost
{
    private sealed class TestChord : IChordConsumer
    {
        private readonly int _key;
        public int Priority { get; }
        public int Fired = -1, FiredMouse = -1;
        public TestChord(int priority, int key) { Priority = priority; _key = key; }
        public bool WantsKey(int key) => key == _key;
        public void Fire(uint key, int sdlMods) => Fired = (int)key;
        public void FireMouse(int button, int sdlMods) => FiredMouse = button;
    }

    /// <summary>Layer order against hand-written literals: the draw roll-call per layer (a string of the letters
    /// each fixture window appends), and the order input and the hit-test visit overlapping windows, which must
    /// put an above-NUI window first even when a below-NUI one was focused last.</summary>
    private static void LayerCheck(Checker check, List<Window> created)
    {
        bool upConsumes = false;
        var drawn = new System.Text.StringBuilder();
        var asked = new System.Text.StringBuilder();
        var a = new Window(0, 0, 100, 100) { DrawContent = () => drawn.Append('a'), OnMouseButton = (_, _, _, _) => { asked.Append('a'); return false; } };
        var up = new Window(0, 0, 50, 50) { Layer = UiLayer.AboveNui, DrawContent = () => drawn.Append('U'), OnMouseButton = (_, _, _, _) => { asked.Append('U'); return upConsumes; } };
        var c = new Window(0, 0, 100, 100) { DrawContent = () => drawn.Append('c'), DrawOver = () => drawn.Append('o') };
        created.Add(a); created.Add(up); created.Add(c);

        DrawFrame(UiLayer.BelowNui);
        check.Expect(drawn.ToString() == "aco", "the below layer draws its windows by z, then its overlays");
        drawn.Clear(); DrawFrame(UiLayer.AboveNui);
        check.Expect(drawn.ToString() == "U", "the above layer draws only its own windows");
        drawn.Clear(); DrawFrame();
        check.Expect(drawn.ToString() == "acoU", "a full roll-call is below, then above");

        Focus(a);
        drawn.Clear(); DrawFrame(UiLayer.BelowNui);
        check.Expect(drawn.ToString() == "cao", "focus reorders a layer");
        check.Expect(HitTest(10, 10) == up, "an above window wins the overlap over a freshly focused below one");
        check.Expect(HitTest(80, 80) == a, "outside the above window the focused below one wins");

        DispatchMouseButton(true, 1, 10, 10);
        check.Expect(asked.ToString() == "Ua", "input visits the above window first");
        asked.Clear(); upConsumes = true;
        check.Expect(DispatchMouseButton(true, 1, 10, 10, captureOnly: true), "an above window under the pointer takes the press ahead of the NUI yield");
        check.Expect(asked.ToString() == "U", "...and no other window is asked");
        check.Expect(!DispatchMouseButton(true, 1, 80, 80, captureOnly: true), "an above window not under the pointer leaves the press to NUI");
        foreach (Window w in new[] { a, up, c }) { Unregister(w); created.Remove(w); }
    }

    /// <summary>Off-device proof of the arbiter: layout/z-order, focus, hit-test/cursor pick, and
    /// precedence, at two scales, leaving the static registry empty. No GL, no device.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        float savedScale = Metrics.Scale;
        var created = new List<Window>();
        var chords = new List<IChordConsumer>();

        // Hermetic isolation: the arbiter is a live static singleton and other services Initialize first
        // (discovery is sorted by name). Snapshot and clear the registry so the self-test neither sees nor
        // mutates production windows, then restore in finally.
        var savedWindows = new List<Window>(_windows);
        var savedChords = new List<IChordConsumer>(_chords);
        var savedFocused = Focused;
        int savedZ = _zCounter;
        bool savedHover = HoverToFocus;
        Action<bool>? savedGate = KeyboardGate;
        _windows.Clear(); _chords.Clear(); Focused = null; HoverToFocus = false; KeyboardGate = null;
        try
        {
            // Geometry checks run at 1.0 and 2.0 -- built-in cursor zones must track Metrics.Scale.
            foreach (float scale in new[] { 1f, 2f })
            {
                Metrics.Scale = scale;

                var a = new Window(100, 100, 200, 150); created.Add(a);
                var b = new Window(150, 120, 200, 150); created.Add(b);   // overlaps a, created later -> on top

                check.Expect(b.Z > a.Z, "later window gets the higher z");
                check.Expect(HitTest(160, 130) == b, "overlap hit-test returns the topmost window");
                check.Expect(HitTest(110, 110) == a, "non-overlap hit-test returns the only window there");
                check.Expect(HitTest(9999, 9999) == null, "hit-test miss returns null");

                Focus(a);
                check.Expect(Focused == a, "focus sets the single owner");
                check.Expect(a.Z > b.Z, "focus raises the window to the top");
                check.Expect(HitTest(160, 130) == a, "the raised window now wins the overlap");
                Focus(b);
                check.Expect(Focused == b, "focusing another replaces the single owner");

                // non-focusable overlay: skipped by focus hit-test, still drives the cursor
                var overlay = new Window(150, 120, 60, 60) { Focusable = false }; created.Add(overlay);
                overlay.Regions.Add(new HitRegion(150, 120, 60, 60, CursorKind.Link));
                check.Expect(HitTest(160, 130) == overlay, "topmost overlay wins the cursor hit-test");
                check.Expect(PickCursor(160, 130) == CursorKind.Link, "cursor follows the overlay's region");
                check.Expect(HitTestFocusable(160, 130) == b, "focus hit-test skips the non-focusable overlay");
                check.Expect(FocusAt(160, 130) == b, "click through the overlay focuses the window beneath");

                // cursor from capability flags, scaled: grip and drag handle track Metrics.Scale
                var chrome = new Window(0, 0, 300, 200) { Draggable = true, Resizable = true }; created.Add(chrome);
                Focus(chrome);   // raise it above the others so its cursor wins the interior points
                check.Expect(PickCursor(1, 1) == CursorKind.Move, "draggable title strip picks Move");
                check.Expect(PickCursor(299, 199) == CursorKind.Resize, "resizable grip corner picks Resize");
                check.Expect(PickCursor(150, 150) == CursorKind.Default, "window interior picks Default");
                check.Expect(PickCursor(9999, 9999) == CursorKind.None, "no window under pointer picks None");

                foreach (var w in created) Unregister(w);
                created.Clear();
                check.Expect(Windows.Count == 0, "registry empty between scales");
            }

            // draw roll-call: DrawFrame invokes DrawContent bottom-up in z-order (focused/topmost last).
            var order = new List<int>();
            var d0 = new Window(0, 0, 10, 10); created.Add(d0); d0.DrawContent = () => order.Add(0);
            var d1 = new Window(0, 0, 10, 10); created.Add(d1); d1.DrawContent = () => order.Add(1);
            var d2 = new Window(0, 0, 10, 10); created.Add(d2); d2.DrawContent = () => order.Add(2);
            DrawFrame();
            check.Expect(order.Count == 3 && order[0] == 0 && order[1] == 1 && order[2] == 2,
              "draw roll-call runs every window's content once, in ascending z-order");
            order.Clear();
            Focus(d0);   // raising d0 to the top must make it paint last
            DrawFrame();
            check.Expect(order.Count == 3 && order[2] == 0, "focus raises a window to paint last");
            var inert = new Window(0, 0, 10, 10); created.Add(inert);   // null DrawContent -> skipped, no throw
            order.Clear(); DrawFrame();
            check.Expect(order.Count == 3, "a window with no content draw is skipped without faulting");
            foreach (var w in created) Unregister(w);
            created.Clear();

            // input precedence: gate vs chord consumers vs gameplay keybinds
            var win = new Window(0, 0, 100, 100); created.Add(win);
            var chord = new TestChord(priority: 10, key: 42); chords.Add(chord); AddChordConsumer(chord);

            Focus(win);
            check.Expect(!AnyKeyboardOwner, "a focused window that does not want the keyboard leaves the gate clear");
            check.Expect(ResolveKey(42) == InputLayer.ChordConsumer, "gate clear: a chord consumer takes its key");
            check.Expect(ResolveKey(7) == InputLayer.GameplayKeybind, "gate clear: an unclaimed key falls to gameplay keybinds");

            var hi = new TestChord(priority: 20, key: 42); chords.Add(hi); AddChordConsumer(hi);
            check.Expect(ResolveChord(42) == hi, "the higher-priority consumer wins the shared key");

            win.WantsKeyboard = true;
            check.Expect(AnyKeyboardOwner, "a window asserting WantsKeyboard sets the gate");
            check.Expect(ResolveKey(42) == InputLayer.FocusedContent, "gate set: focused content eats even a chord's key");
            check.Expect(ResolveKey(7) == InputLayer.FocusedContent, "gate set: focused content eats gameplay keys too");

            // stale owner: unregistering the focused, keyboard-owning window drops the gate
            Unregister(win); created.Remove(win);
            check.Expect(Focused == null && !AnyKeyboardOwner, "unregistering the owner clears focus and drops the gate");

            // ---- live routing: mouse/key/text dispatch, keyboard gate, chord fall-through, focus mode ----
            {
                // Two stacked windows; each records the events it consumes. Overlay (higher z) is topmost.
                var hits = new List<string>();
                var lo = new Window(0, 0, 100, 100); created.Add(lo);
                var upper = new Window(0, 0, 100, 100); created.Add(upper);   // registered later -> higher z -> topmost
                lo.OnMouseButton = (d, b, x, y) => { hits.Add("lo.btn"); return false; };   // never consumes
                upper.OnMouseButton = (d, b, x, y) => { hits.Add("up.btn"); return true; }; // consumes
                check.Expect(DispatchMouseButton(true, 1, 5, 5), "mouse button consumed by the topmost window");
                check.Expect(hits.Count == 1 && hits[0] == "up.btn", "topmost-first: the lower window never sees a consumed press");
                hits.Clear();
                upper.OnMouseButton = (d, b, x, y) => { hits.Add("up.btn"); return false; };   // now passes through
                check.Expect(!DispatchMouseButton(true, 1, 5, 5), "no window consumes -> falls through");
                check.Expect(hits.Count == 2 && hits[0] == "up.btn" && hits[1] == "lo.btn", "unconsumed press walks top-down to the lower window");

                // A modal (SuppressChords) window wins the press ahead of a higher-z non-modal one.
                hits.Clear();
                lo.SuppressChords = true;
                lo.OnMouseButton = (d, b, x, y) => { hits.Add("lo.btn"); return true; };
                check.Expect(DispatchMouseButton(true, 1, 5, 5) && hits.Count == 1 && hits[0] == "lo.btn",
                  "modal window wins the press before a higher-z non-modal window");
                hits.Clear();
                check.Expect(!DispatchMouseButton(true, 1, 5, 5, captureOnly: true) && hits.Count == 0,
                  "the capture pass skips a modal window that does not capture the pointer");
                lo.CapturesPointer = true;
                check.Expect(DispatchMouseButton(true, 1, 5, 5, captureOnly: true) && hits.Count == 1 && hits[0] == "lo.btn",
                  "the capture pass reaches only the capturing modal window");
                lo.OnMouseWheel = dy => { hits.Add("lo.wheel"); return true; };
                hits.Clear();
                check.Expect(DispatchMouseWheel(1, captureOnly: true) && hits.Count == 1 && hits[0] == "lo.wheel",
                  "the wheel's capture pass reaches the capturing modal window");
                lo.CapturesPointer = false;
                hits.Clear();
                check.Expect(!DispatchMouseWheel(1, captureOnly: true) && hits.Count == 0, "no capturing window: the capture pass finds no consumer");
                lo.OnMouseWheel = null;
                lo.SuppressChords = false;
                lo.OnMouseButton = (d, b, x, y) => { hits.Add("lo.btn"); return false; };
                hits.Clear();

                // Motion + wheel route the same top-down way.
                bool motion = false, wheel = false;
                upper.OnMouseMotion = (x, y) => { motion = true; return true; };
                upper.OnMouseWheel = dy => { wheel = true; return true; };
                check.Expect(DispatchMouseMotion(5, 5) && motion, "motion routes to a window");
                check.Expect(DispatchMouseWheel(1) && wheel, "wheel routes to a window");

                // Keys: a full owner eats everything; a selective consumer eats only what it handles.
                var keys = new List<uint>();
                lo.OnKey = (k, m) => { keys.Add(k); return k == 9; };   // selective: eats key 9 only
                check.Expect(!DispatchKey(7, 0), "no owner + selective consumer rejects -> key falls through");
                check.Expect(DispatchKey(9, 0), "selective consumer eats its key");
                keys.Clear();
                upper.OnKey = (k, m) => { keys.Add(k); return false; };
                RequestKeyboard(upper, true);   // upper becomes the full keyboard owner
                check.Expect(DispatchKey(7, 0) && keys.Count == 1 && keys[0] == 7, "the keyboard owner eats every key, first");
                check.Expect(!lo.WantsKeyboard, "the lower window is not the owner");

                // Text goes to the owner's OnText.
                bool gotText = false;
                upper.OnText = _ => gotText = true;
                check.Expect(DispatchText(IntPtr.Zero) && gotText, "text routes to the keyboard owner");
                RequestKeyboard(upper, false);
                check.Expect(!DispatchText(IntPtr.Zero), "no owner -> text is dropped");

                // Keyboard gate: RequestKeyboard drives the framework gate callback on each call.
                int gateCalls = 0; bool gateLast = false;
                KeyboardGate = on => { gateCalls++; gateLast = on; };
                RequestKeyboard(upper, true);
                check.Expect(gateCalls == 1 && gateLast && upper.WantsKeyboard, "RequestKeyboard(on) sets WantsKeyboard and drives the gate");
                RequestKeyboard(upper, false);
                check.Expect(gateCalls == 2 && !gateLast && !upper.WantsKeyboard, "RequestKeyboard(off) clears WantsKeyboard and drives the gate");
                RequestKeyboard(lo, true); RequestKeyboard(upper, true);
                RequestKeyboard(lo, false);
                check.Expect(gateCalls == 4 && gateLast && upper.WantsKeyboard, "a release while another window owns the keyboard leaves the gate up");
                RequestKeyboard(upper, false);
                check.Expect(gateCalls == 5 && !gateLast, "the last owner's release drops the gate");
                KeyboardGate = null;

                // Chord suppression + fall-through fire.
                var tc = new TestChord(priority: 5, key: 3); chords.Add(tc); AddChordConsumer(tc);
                check.Expect(!AnySuppressChords, "no window suppresses chords by default");
                upper.SuppressChords = true;
                check.Expect(AnySuppressChords, "a window can withhold chords");
                upper.SuppressChords = false;
                FireChords(3, 0); FireMouseChords(2, 0);
                check.Expect(tc.Fired == 3 && tc.FiredMouse == 2, "fall-through key/mouse fire reaches the chord consumer");
                RemoveChordConsumer(tc); chords.Remove(tc);

                // HitTestOverride widens the hit-test past the rect (chrome outside the frame).
                lo.HitTestOverride = (px, py) => px >= 0 && px < 500;   // wider than the 100px rect
                check.Expect(HitTest(300, 5) == lo, "hit-test honours a window's override region outside its rect");
                lo.HitTestOverride = null;

                // Focus mode: hover-to-focus focuses on motion only when enabled; click-to-focus is the default.
                check.Expect(HoverFocusAt(5, 5) == null, "click-to-focus (default): hover does not focus");
                HoverToFocus = true;
                check.Expect(HoverFocusAt(5, 5) == upper && Focused == upper, "hover-to-focus: the topmost focusable window under the pointer takes focus");
                HoverToFocus = false;

                Unregister(lo); created.Remove(lo);
                Unregister(upper); created.Remove(upper);
                SetFocused(null);
            }

            LayerCheck(check, created);

            // hygiene: every fake the test created is gone (checked against the cleared baseline, pre-restore)
            check.Expect(Windows.Count == 0, "all self-check windows unregistered");
        }
        finally
        {
            foreach (var w in created) Unregister(w);
            foreach (var c in chords) RemoveChordConsumer(c);
            _windows.Clear(); _windows.AddRange(savedWindows);
            _chords.Clear(); _chords.AddRange(savedChords);
            Focused = savedFocused;
            _zCounter = savedZ;
            HoverToFocus = savedHover;
            KeyboardGate = savedGate;
            Metrics.Scale = savedScale;
        }

        log.Info(check.Ok ? "UiHost.SelfCheck: passed" : "UiHost.SelfCheck: FAILED");
    }
}
