using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

/// <summary>Which layer of the fixed input precedence consumes a key.</summary>
public enum InputLayer
{
    /// <summary>The focused window owns the keyboard (typing): it eats every key; chords and keybinds withheld.</summary>
    FocusedContent,
    /// <summary>The gate is clear and a registered chord consumer claimed the key.</summary>
    ChordConsumer,
    /// <summary>Nobody claimed it -- the engine's gameplay keybinds get it.</summary>
    GameplayKeybind,
}

/// <summary>A global hotkey owner that consumes chords without owning a Window. Registered with a priority; the
/// highest-priority consumer wanting a key wins, and only while no window owns the keyboard. <see cref="WantsKey"/>
/// is the off-device arbiter query; <see cref="Fire"/> / <see cref="FireMouse"/> are the live entry points, called
/// once a key or button fell past every window with the chord gate clear -- the consumer self-gates and sends.</summary>
public interface IChordConsumer
{
    int Priority { get; }
    bool WantsKey(int key);
    /// <summary>A key press fell through to the chord layer. <paramref name="sdlMods"/> is the raw SDL modifier
    /// state (the consumer maps it to its own wire encoding).</summary>
    void Fire(uint key, int sdlMods);
    /// <summary>A mouse button press fell through to the chord layer (mouse buttons are chords too).</summary>
    void FireMouse(int button, int sdlMods);
}

/// <summary>The pure-C# UI arbiter: a static registry of <see cref="Window"/>s plus the value-in/decision-out
/// logic every window needs -- z-order, top-down hit-test, cursor pick, one global keyboard owner, and the fixed
/// input-precedence chain. No GL, no native refs, nothing hooked; <c>UiHostService</c> (the frame host) and
/// <c>UiInputService</c> (the input layer) supply the raw events.</summary>
public static partial class UiHost
{
    /// <summary>Populated at service init and immutable thereafter -- no Register/Unregister after the first frame --
    /// so <see cref="DrawFrame"/>'s render-thread read needs no lock. Focus and keyboard writes land live from the
    /// input thread, but are word-sized: the worst case is one frame of stale z-order. ponytail: a window that ever
    /// mutates <see cref="Window.Regions"/> live needs snapshot-or-swap, a mid-growth read being a real crash.</summary>
    private static readonly List<Window> _windows = new();
    private static readonly List<IChordConsumer> _chords = new();
    private static int _zCounter;

    /// <summary>Focus-mode preference: false = click-to-focus (default), true = hover-to-focus. The
    /// <c>ui.hoverfocus</c> knob is the storage; the setter is the SelfCheck's (a plain value write, no set).</summary>
    public static bool HoverToFocus { get => UiKnobs.HoverFocus.V; set => UiKnobs.HoverFocus.V = value; }

    /// <summary>The engine keyboard-capture gate, owned by the framework input layer. It drives
    /// <c>CExoInput::KeyboardMode{On,Off}</c> plus the drive-latch reset (<c>TurnOffKeyboardModal</c>) on the
    /// off→on edge, once for every consumer. Null off-device / under LOCKCHAT (no input layer installed).</summary>
    public static Action<bool>? KeyboardGate;

    /// <summary>The single window that owns focus -- the candidate keyboard owner. Null when nothing is focused.</summary>
    public static Window? Focused { get; private set; }

    public static IReadOnlyList<Window> Windows => _windows;

    internal static void Register(Window w)
    {
        w.Z = ++_zCounter;
        _windows.Add(w);
    }

    /// <summary>Z handed out below every registration: <see cref="Register"/> only ever assigns positive
    /// values, so a decreasing counter starting at 0 keeps a sent-back window under all of them.</summary>
    private static int _backZ;

    /// <summary>Push a window under every other one, permanently. Registration order is discovery order,
    /// which is type-name sorted, so a window that must paint BELOW the chat box and the dialog panel
    /// (world-anchored nameplates) cannot get there by naming. <see cref="Focus"/> would raise it again,
    /// but only for a focusable window -- a back window sets <c>Focusable = false</c>.</summary>
    public static void SendToBack(Window w) => w.Z = --_backZ;

    /// <summary>Remove a window; if it held focus, clear the owner so the keyboard-owned gate drops
    /// (stale-owner safety -- a destroyed typing window must not keep gameplay keys suppressed).</summary>
    public static void Unregister(Window w)
    {
        _windows.Remove(w);
        if (Focused == w) SetFocused(null);
    }

    /// <summary>Focus a registered focusable window and raise it to the top of the z-order. No-op for a
    /// non-focusable or already-unregistered window (no dangling focus owner).</summary>
    public static void Focus(Window w)
    {
        if (!w.Focusable || !_windows.Contains(w)) return;
        SetFocused(w);
        w.Z = ++_zCounter;
    }

    /// <summary>Move focus, ending the outgoing window's keyboard-ownership. A window that loses focus is no
    /// longer typing, so its <see cref="Window.WantsKeyboard"/> is cleared -- otherwise re-focusing it would
    /// silently re-raise the gate and suppress gameplay keys -- the stuck-input class of bug.</summary>
    private static void SetFocused(Window? w)
    {
        if (Focused == w) return;
        if (Focused != null) Focused.WantsKeyboard = false;
        Focused = w;
    }

    /// <summary>Release focus, as a click on empty space does. For a click that lands somewhere the host
    /// cannot hit-test -- over a NUI window, which draws on top of a below-NUI window and takes the press itself.</summary>
    public static void Blur() => SetFocused(null);

    /// <summary>Topmost window (any) whose live UI covers the point, or null. Drives the cursor -- overlays
    /// count. Uses <see cref="Window.Hit"/> so a window's chrome outside its rect (popped-out strip, dropdown)
    /// still registers. Zero-alloc, like every per-frame read here: plain loops, no LINQ, no sorting.</summary>
    public static Window? HitTest(float px, float py)
    {
        Window? best = null;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Hit(px, py) && (best == null || w.Order > best.Order)) best = w;
        }
        return best;
    }

    /// <summary>Topmost FOCUSABLE window under the point: non-focusable windows are skipped for focus, so a
    /// click passes through an overlay to the focusable window beneath it.</summary>
    public static Window? HitTestFocusable(float px, float py)
    {
        Window? best = null;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Focusable && w.Hit(px, py) && (best == null || w.Order > best.Order)) best = w;
        }
        return best;
    }

    /// <summary>Click-to-focus resolution: focus the topmost focusable window under the point and return it;
    /// a click on empty space (no window at all) releases focus; a click on a non-focusable overlay leaves
    /// focus unchanged.</summary>
    public static Window? FocusAt(float px, float py)
    {
        var w = HitTestFocusable(px, py);
        if (w != null) Focus(w);
        else if (HitTest(px, py) == null) SetFocused(null);
        return w;
    }

    /// <summary>Hover-to-focus resolution: when <see cref="HoverToFocus"/> is set, focus the topmost
    /// focusable window under the point as the pointer moves over it. No-op (returns null) in click-to-focus
    /// mode; empty space never releases focus on hover (only a click does).</summary>
    public static Window? HoverFocusAt(float px, float py)
    {
        if (!HoverToFocus) return null;
        var w = HitTestFocusable(px, py);
        if (w != null) Focus(w);
        return w;
    }

    /// <summary>The cursor under the pointer: the topmost window's pick, or <see cref="CursorKind.None"/> when
    /// no window is there (the engine cursor stands).</summary>
    public static CursorKind PickCursor(float px, float py)
    {
        var w = HitTest(px, py);
        return w == null ? CursorKind.None : w.CursorAt(px, py);
    }

    /// <summary>Route the keyboard capture on/off for a window: flip its <see cref="Window.WantsKeyboard"/> and drive
    /// the centralized engine gate through <see cref="KeyboardGate"/>. A release while another window still owns the
    /// keyboard leaves the gate up. Contract: a window that takes the keyboard MUST wire <see cref="Window.OnKey"/> --
    /// the owner consumes every key, which is how it withholds gameplay keybinds, so a handler-less owner eats all.</summary>
    public static void RequestKeyboard(Window w, bool on)
    {
        w.WantsKeyboard = on;
        if (!on && AnyKeyboardOwner) return;
        KeyboardGate?.Invoke(on);
    }

    /// <summary>True while any registered window is withholding chord consumers (a modal window is up). The
    /// live input layer reads it to decide whether an unconsumed key/button may fall to the hotkey chords.</summary>
    public static bool AnySuppressChords
    {
        get
        {
            for (int i = 0; i < _windows.Count; i++) if (_windows[i].SuppressChords) return true;
            return false;
        }
    }

    /// <summary>True while a window is the full keyboard owner (chat typing). The pump-level swallow is gated on
    /// its negation -- an owner already suppresses engine keybinds via the keyboard-mode gate, so no swallow.</summary>
    public static bool AnyKeyboardOwner
    {
        get
        {
            for (int i = 0; i < _windows.Count; i++) if (_windows[i].WantsKeyboard) return true;
            return false;
        }
    }

    /// <summary>Pump-level swallow: a modal <see cref="Window.SuppressChords"/> window that consumes a key at the
    /// SDL pump, before the engine queues its scancode keybind (<c>CExoInput::AddEvent</c> runs pre-HandleWMKeyDown
    /// with no modifier check, so Ctrl+C would still fire the plain-C keybind). Only SuppressChords windows swallow
    /// here; the keyboard owner suppresses keybinds via the keyboard-mode gate. Topmost-first.</summary>
    public static bool DispatchKeyPumpSwallow(uint key, int sdlMods)
    {
        foreach (Window next in TopmostFirst())
        {
            if (next.SuppressChords && next.OnKey != null && next.OnKey(key, sdlMods)) return true;
        }
        return false;
    }

    /// <summary>Route a mouse button (press/release) to window content, true at the first window that consumes it.
    /// <paramref name="captureOnly"/> visits only <see cref="Window.CapturesPointer"/> windows: the pump's extra pass BEFORE the engine-GUI yield.</summary>
    public static bool DispatchMouseButton(bool down, int button, int x, int y, bool captureOnly = false)
        => Route(w => w.OnMouseButton != null && w.OnMouseButton(down, button, x, y), captureOnly, x, y);

    /// <summary>The modal routing both pointer events take: topmost-first, and when not
    /// <paramref name="captureOnly"/> two passes -- modal (<see cref="Window.SuppressChords"/>) windows win the
    /// event, then the rest -- each window visited exactly once, so no double-invoke. Cold: input, not a frame.</summary>
    private static bool Route(Func<Window, bool> offer, bool captureOnly, float x, float y)
    {
        if (captureOnly)
        {
            foreach (Window next in TopmostFirst())
                if (TakesBeforeNui(next, x, y) && offer(next)) return true;
            return false;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            bool modal = pass == 0;
            foreach (Window next in TopmostFirst())
                if (next.SuppressChords == modal && offer(next)) return true;
        }
        return false;
    }

    /// <summary>Whether a window is asked ahead of the engine-GUI yield: a modal pointer-capturing window always,
    /// and an above-NUI window while the pointer is over it -- it is drawn over NUI, so it gets the press first.</summary>
    private static bool TakesBeforeNui(Window w, float x, float y)
        => (w.CapturesPointer && w.SuppressChords) || (w.Layer == UiLayer.AboveNui && w.Hit(x, y));

    /// <summary>Route pointer motion to window content, topmost-first; true at the first consumer (a drag owns
    /// motion until release, so handlers self-gate on their own grab state).</summary>
    public static bool DispatchMouseMotion(int x, int y)
    {
        foreach (Window next in TopmostFirst())
        {
            if (next.OnMouseMotion != null && next.OnMouseMotion(x, y)) return true;
        }
        return false;
    }

    /// <summary>Route a wheel notch to window content, as <see cref="DispatchMouseButton"/> routes a press; the
    /// wheel event has no position, so the caller supplies the pointer's.</summary>
    public static bool DispatchMouseWheel(int dy, bool captureOnly = false, float px = float.NaN, float py = float.NaN)
        => Route(w => w.OnMouseWheel != null && w.OnMouseWheel(dy), captureOnly, px, py);

    /// <summary>Every registered window, topmost-first. Zero-alloc: a struct enumerator over the O(n^2)
    /// descending-z scan (z values are unique, from the monotonic counter, so the walk terminates even when a
    /// handler unregisters a window mid-walk); no buffer, no sort, safe on the render thread.
    /// ponytail: O(n^2) is free at this n; swap for a kept-sorted list only if the window count ever grows big.</summary>
    private static ZWalk TopmostFirst() => new() { _lastZ = int.MaxValue };

    /// <summary>The <see cref="TopmostFirst"/> enumerator. Only the factory seeds it: <c>default</c> starts at z 0
    /// and silently visits nothing.</summary>
    private struct ZWalk
    {
        internal int _lastZ;
        public Window Current { get; private set; }
        public ZWalk GetEnumerator() => this;
        public bool MoveNext()
        {
            var next = NextBelow(_lastZ);
            if (next == null) return false;
            Current = next;
            _lastZ = next.Order;
            return true;
        }
    }

    /// <summary>The registered window with the highest z strictly below <paramref name="belowZ"/>, or null.</summary>
    private static Window? NextBelow(int belowZ)
    {
        Window? next = null;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Order < belowZ && (next == null || w.Order > next.Order)) next = w;
        }
        return next;
    }

    /// <summary>The fixed key precedence, live. First a full keyboard owner (chat typing) eats every key;
    /// then selective consumers (a modal dialog) top-down eat what they handle. Returns true when a window
    /// consumed the key -- the caller then does NOT run the engine's original handler. An unconsumed key falls
    /// to the chord layer (subject to <see cref="AnySuppressChords"/>) and then gameplay keybinds.</summary>
    public static bool DispatchKey(uint key, int sdlMods)
    {
        // Pass 1: the single full keyboard owner eats everything, regardless of z (matches chat-first today).
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.WantsKeyboard) { w.OnKey?.Invoke(key, sdlMods); return true; }
        }
        // Pass 2: selective consumers, topmost-first.
        foreach (Window next in TopmostFirst())
        {
            if (next.OnKey != null && next.OnKey(key, sdlMods)) return true;
        }
        return false;
    }

    /// <summary>Deliver finished text (SDL_TEXTINPUT payload) to the keyboard owner's <see cref="Window.OnText"/>.
    /// Returns true when an owner took it. No owner -> false (the text is dropped, as gameplay has no text sink).</summary>
    public static bool DispatchText(IntPtr payload)
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.WantsKeyboard && w.OnText != null) { w.OnText(payload); return true; }
        }
        return false;
    }

    /// <summary>Fire a fallen-through key at every registered chord consumer; each self-gates on its own
    /// arm/consent/debounce state. ponytail: fire-all (one consumer today); switch to a priority resolve
    /// (<see cref="ResolveChord"/>) only if two consumers ever contend for the same key.</summary>
    public static void FireChords(uint key, int sdlMods)
    {
        for (int i = 0; i < _chords.Count; i++) _chords[i].Fire(key, sdlMods);
    }

    /// <summary>Fire a fallen-through mouse button at every registered chord consumer (self-gating).</summary>
    public static void FireMouseChords(int button, int sdlMods)
    {
        for (int i = 0; i < _chords.Count; i++) _chords[i].FireMouse(button, sdlMods);
    }

    /// <summary>The per-frame draw roll-call over both layers, below NUI then above. The two sites that run it
    /// in one go are the present-time fallback and the render-thread frame; the NUI-aware sites call
    /// <see cref="DrawFrame(UiLayer)"/> on either side of NUI's flush.</summary>
    public static void DrawFrame()
    {
        DrawFrame(UiLayer.BelowNui);
        DrawFrame(UiLayer.AboveNui);
    }

    /// <summary>One layer's roll-call: every window's <see cref="Window.DrawContent"/> on that layer in ascending z, so the
    /// focused window paints last, then that layer's <see cref="Window.DrawOver"/>. Zero-alloc: it runs on the render
    /// thread inside SwapBuffers.
    /// ponytail: O(n^2) is free at a handful of windows; a kept-sorted list is the upgrade if the count grows.</summary>
    public static void DrawFrame(UiLayer layer)
    {
        int lastZ = int.MinValue;
        for (int drawn = 0; drawn < _windows.Count; drawn++)
        {
            Window? next = null;
            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                if (w.Layer == layer && w.Z > lastZ && (next == null || w.Z < next.Z)) next = w;
            }
            if (next == null) break;   // no window on this layer with a higher z -> done
            lastZ = next.Z;
            next.DrawContent?.Invoke();
        }
        for (int i = 0; i < _windows.Count; i++) if (_windows[i].Layer == layer) _windows[i].DrawOver?.Invoke();
    }

    public static void AddChordConsumer(IChordConsumer c) { if (!_chords.Contains(c)) _chords.Add(c); }
    public static void RemoveChordConsumer(IChordConsumer c) => _chords.Remove(c);

    /// <summary>The highest-priority chord consumer wanting this key, or null. (The gate is not applied here;
    /// callers use <see cref="ResolveKey"/> for the full precedence, and this to reach the winner.)</summary>
    public static IChordConsumer? ResolveChord(int key)
    {
        IChordConsumer? pick = null;
        for (int i = 0; i < _chords.Count; i++)
        {
            var c = _chords[i];
            if (c.WantsKey(key) && (pick == null || c.Priority > pick.Priority)) pick = c;
        }
        return pick;
    }

    /// <summary>The fixed input precedence: keyboard-owned gate → chord consumers → gameplay keybinds. When a
    /// window owns the keyboard, its content eats every key and chords/keybinds are withheld. The gate is
    /// <see cref="AnyKeyboardOwner"/>, so the owner need not be the focused window (chat self-manages focus).</summary>
    public static InputLayer ResolveKey(int key)
    {
        if (AnyKeyboardOwner) return InputLayer.FocusedContent;
        return ResolveChord(key) != null ? InputLayer.ChordConsumer : InputLayer.GameplayKeybind;
    }
}
