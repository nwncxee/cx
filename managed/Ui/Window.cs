using System;
using System.Collections.Generic;

namespace CX.Ui;

/// <summary>A first-class UI window: rect, z-order, focus/keyboard state, capability flags, and the cursor regions
/// under it. Constructing one registers it with <see cref="UiHost"/> -- there is no discovery interface. The
/// framework owns the chrome (hit-test, cursor, focus) and routes captured input to the content delegates; the
/// service supplies content. Drag/resize mechanics stay in the content; the framework only picks the cursor.</summary>
public sealed class Window
{
    /// <summary>Design px for the built-in chrome regions; multiplied by <see cref="Metrics.Scale"/> so cursor
    /// zones track the UI scale exactly like the chat chrome does.</summary>
    private const float GripPx = 12f;
    private const float TitlePx = 20f;

    public float X, Y, W, H;

    /// <summary>Higher is nearer the top; assigned on register and bumped by <see cref="UiHost.Focus"/>.</summary>
    public int Z { get; internal set; }

    /// <summary>Capability flags. A non-focusable window is skipped by focus hit-testing but still drives the
    /// cursor; draggable/resizable expose the move/resize cursor zones.
    /// ponytail: promote the drag/resize state machine here when a second consumer needs it.</summary>
    public bool Focusable = true;
    public bool Draggable;
    public bool Resizable;

    /// <summary>Content asserts this while it owns the keyboard (typing) -- the lift of ChatService.IsTyping.
    /// The arbiter's keyboard-owned gate is set only when this window is ALSO the focused one, so focus alone
    /// never suppresses gameplay keys.</summary>
    public bool WantsKeyboard;

    /// <summary>Content asserts this while it wants global chord consumers (hotkeys) withheld even for keys it
    /// does NOT consume -- a modal window (Dialog) that selectively eats digits/ESC but must not let the rest
    /// fire a hotkey. Distinct from <see cref="WantsKeyboard"/> (a full owner eats everything and drives the
    /// engine keyboard gate); a chord-suppressing window still lets unconsumed keys reach gameplay.</summary>
    public bool SuppressChords;

    /// <summary>A modal window that owns EVERY press and wheel notch while up, ahead of the engine's own GUI -- the
    /// radial cascade opens over HUD chrome and its click-away must reach it. The pump runs a capture-only dispatch
    /// before the NUI/vanilla-GUI yield for these windows and nothing else.</summary>
    public bool CapturesPointer;

    /// <summary>Content input slots. The framework's raw-input layer routes captured events here in z-order; each
    /// returns true when it consumed the event, which stops routing, and null means the window ignores that channel.
    /// Cold (input thread), so allocation is fine. <see cref="OnKey"/> takes the SDL keycode + raw modifier state;
    /// <see cref="OnText"/> takes the SDL_TEXTINPUT payload pointer (finished UTF-8) for the keyboard owner.</summary>
    public Func<bool, int, int, int, bool>? OnMouseButton;   // (down, button, x, y) -> consumed
    public Func<int, int, bool>? OnMouseMotion;              // (x, y) -> consumed
    public Func<int, bool>? OnMouseWheel;                    // (dy) -> consumed
    public Func<uint, int, bool>? OnKey;                     // (sdlKey, sdlMods) -> consumed
    public Action<IntPtr>? OnText;                           // SDL_TEXTINPUT payload

    /// <summary>Overrides the rect hit-test for cursor pick + click routing: true when the point is over this
    /// window's live UI (a service whose chrome extends outside its rect -- a popped-out input strip, an open
    /// dropdown -- supplies this). Null falls back to <see cref="Contains"/>. Zero-alloc: read on the render
    /// thread by the cursor pick and on the input thread by focus routing.</summary>
    public Func<float, float, bool>? HitTestOverride;

    /// <summary>Content-supplied cursor regions (absolute px). Mutated cold when content relayouts, read hot
    /// during cursor pick. Built-in grip/drag zones are derived from the rect, not stored here.</summary>
    public readonly List<HitRegion> Regions = new();

    /// <summary>The service's per-frame content draw, invoked by <see cref="UiHost.DrawFrame"/> on the render
    /// thread once per frame in z-order. The framework owns the roll-call; the service owns what happens inside
    /// (its own GL draw, gate-branching, cold->hot frame hand-off). Null for an inert window. Hot: a cached
    /// delegate invoke, zero-alloc.</summary>
    public Action? DrawContent;
    /// <summary>Drawn after every window's content, over all of them: a tooltip that must not hide under a neighbour.</summary>
    public Action? DrawOver;

    public Window(float x, float y, float w, float h)
    {
        X = x; Y = y; W = w; H = h;
        UiHost.Register(this);
    }

    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    /// <summary>Whether the point is over this window's live UI: <see cref="HitTestOverride"/> if the content
    /// supplied one (chrome outside the rect), else the plain rect <see cref="Contains"/>.</summary>
    public bool Hit(float px, float py) => HitTestOverride?.Invoke(px, py) ?? Contains(px, py);

    /// <summary>Clamp one axis of a window so its <paramref name="size"/>-wide span stays fully inside a
    /// <paramref name="viewport"/>-wide screen: pinned to 0 off the near edge, to viewport-size off the far edge,
    /// and to 0 when the span is larger than the viewport. Screen-space, so the on-screen guarantee is
    /// scale-invariant; shared here so every consumer clamps alike.</summary>
    public static float ClampAxis(float pos, float size, float viewport)
        => System.Math.Clamp(pos, 0f, System.Math.Max(0f, viewport - size));

    /// <summary>The cursor requested at an absolute point already known to be inside this window. Built-in
    /// chrome (resize grip, then drag handle) wins by capability flag; then content regions; else Default.
    /// Zero-alloc: a plain scan, no LINQ or closures -- this runs on the render thread.</summary>
    public CursorKind CursorAt(float px, float py)
    {
        float g = GripPx * Metrics.Scale;
        if (Resizable && px >= X + W - g && py >= Y + H - g) return CursorKind.Resize;
        if (Draggable && py < Y + TitlePx * Metrics.Scale) return CursorKind.Move;
        for (int i = 0; i < Regions.Count; i++)
            if (Regions[i].Contains(px, py)) return Regions[i].Cursor;
        return CursorKind.Default;
    }
}
