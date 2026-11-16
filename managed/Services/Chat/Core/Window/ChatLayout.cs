using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// The ordered window list ChatService drives for draw/input/persistence. Index 0 is the always-present,
/// undeletable <b>Main</b>; <see cref="FocusedIndex"/> tracks which window owns the keyboard and input strip.
/// The backing array is swapped atomically (volatile) so ingest and render never see a torn list — the same
/// pattern <see cref="ChatWindow"/> uses for its tabs. Pure Core — no native refs — so it self-checks off-device.
/// </summary>
public sealed class ChatLayout
{
    /// <summary>Hard cap on simultaneous windows. ponytail: 4 covers any real use; raise if players ask.</summary>
    public const int MaxWindows = 4;

    private volatile ChatWindow[] _windows;
    private volatile int _focusedIndex;

    /// <summary>Snapshot of the window array. Readers should capture this once and iterate the snapshot.</summary>
    public ChatWindow[] Windows => _windows;

    public ChatLayout(ChatWindow main) { _windows = new[] { main }; }

    /// <summary>The undeletable Main window (index 0). Always present; always &gt;=1 tab.</summary>
    public ChatWindow Main => _windows[0];

    /// <summary>Window count (snapshot-safe).</summary>
    public int Count => _windows.Length;

    /// <summary>Index of the focused window (owns the keyboard + input strip). Default 0 (Main).
    /// Setter silently refuses out-of-range values.</summary>
    public int FocusedIndex
    {
        get => _focusedIndex;
        set
        {
            if (value < 0 || value >= _windows.Length) return;
            ChatWindow[] w = _windows;
            int old = _focusedIndex;
            if (old != value)
            {
                if ((uint)old < (uint)w.Length) w[old].Dirty = true;
                w[value].Dirty = true;
            }
            _focusedIndex = value;
        }
    }

    /// <summary>The focused window. Bounds-checks against the snapshot so a stale index (visible between
    /// two volatile writes on a mutation) falls back to Main instead of throwing.</summary>
    public ChatWindow Focused
    {
        get { ChatWindow[] w = _windows; int i = _focusedIndex; return w[(uint)i < (uint)w.Length ? i : 0]; }
    }

    /// <summary>Index of the window Enter opens the typing bar in, or -1 when none can send: Main, or -- when
    /// Main's tab can't send -- the focused window, unless it is click-through.</summary>
    public int TypingWindow()
    {
        ChatWindow[] w = _windows;
        if (w[0].HasSendable) return 0;
        int fi = _focusedIndex;
        return (uint)fi < (uint)w.Length && w[fi].HasSendable && !w[fi].ClickThrough ? fi : -1;
    }

    /// <summary>Index of the topmost window that takes the pointer at the point, or -1: the focused window (drawn
    /// last) first, then the rest in reverse draw order.</summary>
    public int WindowAt(float x, float y)
    {
        ChatWindow[] w = _windows;
        int fi = _focusedIndex;
        if ((uint)fi < (uint)w.Length && w[fi].TakesPointer(x, y)) return fi;
        for (int i = w.Length - 1; i >= 0; i--)
            if (i != fi && w[i].TakesPointer(x, y)) return i;
        return -1;
    }

    /// <summary>Append a window and return it, or null when the <see cref="MaxWindows"/> cap is reached.
    /// Atomic array swap; <see cref="FocusedIndex"/> is unaffected.</summary>
    public ChatWindow? AddWindow(ChatWindow w)
    {
        ChatWindow[] old = _windows;
        if (old.Length >= MaxWindows) return null;
        w.Closeable = true;
        var next = new ChatWindow[old.Length + 1];
        old.CopyTo(next, 0);
        next[old.Length] = w;
        _windows = next;
        return w;
    }

    /// <summary>Close a window by index, refusing Main (0) and out-of-range. Writes
    /// <see cref="FocusedIndex"/> before the array swap so a concurrent reader on the old (longer) array
    /// never sees the new (shorter) array with a stale index. Returns whether it removed anything.</summary>
    public bool CloseWindow(int index)
    {
        ChatWindow[] old = _windows;
        if (index <= 0 || index >= old.Length) return false;
        int fi = _focusedIndex;
        if (fi == index) fi = 0;
        else if (fi > index) fi--;
        _focusedIndex = fi;
        var next = new ChatWindow[old.Length - 1];
        System.Array.Copy(old, 0, next, 0, index);
        System.Array.Copy(old, index + 1, next, index, old.Length - index - 1);
        _windows = next;
        return true;
    }

    /// <summary>Move a tab from one window to another. Guards the &gt;=1-tab invariant on the source
    /// window: if the tab is the source's last, the transfer is refused. Returns whether it succeeded.</summary>
    public bool TransferTab(int srcWinIdx, int srcTabIdx, int dstWinIdx, int dstTabIdx)
    {
        ChatWindow[] w = _windows;
        if ((uint)srcWinIdx >= (uint)w.Length || (uint)dstWinIdx >= (uint)w.Length) return false;
        if (srcWinIdx == dstWinIdx) return false;
        ChatWindow src = w[srcWinIdx], dst = w[dstWinIdx];
        if (dst.Locked) return false;
        if ((uint)srcTabIdx >= (uint)src.Tabs.Count || src.Tabs.Count <= 1) return false;
        Tab t = src.Tabs[srcTabIdx];
        src.RemoveTab(srcTabIdx);
        dst.InsertTab(t, dstTabIdx);
        return true;
    }

    /// <summary>Off-device proof of the Main-undeletable / add-close-window / focus-tracking invariants.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var main = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        var layout = new ChatLayout(main);

        check.Expect(layout.Count == 1 && layout.Main == main, "starts with just the Main window");
        check.Expect(layout.Focused == main && layout.FocusedIndex == 0, "Main focused by default");
        check.Expect(!layout.CloseWindow(0), "Main (index 0) is undeletable");
        check.Expect(layout.Count == 1, "refused close left the list untouched");

        var extra = layout.AddWindow(new ChatWindow(50, 50, 400, 300, new[] { new Tab("All", 0xffffffff) }));
        check.Expect(layout.Count == 2 && layout.Windows[1] == extra, "AddWindow appends");

        // Focus extra, close it -> focus returns to Main
        layout.FocusedIndex = 1;
        check.Expect(layout.Focused == extra, "focus set to extra window");
        check.Expect(layout.CloseWindow(1) && layout.Count == 1, "non-Main window closes");
        check.Expect(layout.FocusedIndex == 0 && layout.Focused == main, "closing focused returns focus to Main");
        check.Expect(layout.Main == main, "Main survives a close");
        check.Expect(!layout.CloseWindow(5), "out-of-range close is a no-op");

        // Close a window below the focused -> focused index decrements to track
        var w1 = layout.AddWindow(new ChatWindow(50, 50, 400, 300, new[] { new Tab("All", 0xffffffff) }))!;
        var w2 = layout.AddWindow(new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) }))!;
        layout.FocusedIndex = 2;
        check.Expect(layout.Focused == w2, "focused on w2");
        layout.CloseWindow(1);
        check.Expect(layout.FocusedIndex == 1 && layout.Focused == w2, "closing below focused decrements index");
        layout.CloseWindow(1);   // clean up

        // Window cap. Bounded by the cap itself: an AddWindow that refused early used to spin here forever
        // instead of failing the check below.
        for (int i = layout.Count; i < ChatLayout.MaxWindows; i++)
            layout.AddWindow(new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) }));
        check.Expect(layout.Count == MaxWindows, $"adding windows one at a time reaches the cap ({layout.Count})");
        check.Expect(layout.AddWindow(new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) })) == null,
            "AddWindow at cap returns null");
        check.Expect(layout.Count == MaxWindows, "count unchanged at cap");

        // FocusedIndex setter rejects out-of-range
        layout.FocusedIndex = 99;
        check.Expect(layout.FocusedIndex != 99, "out-of-range focus rejected");
        layout.FocusedIndex = -1;
        check.Expect(layout.FocusedIndex >= 0, "negative focus rejected");

        // Cross-window tab transfer: move + last-tab guard.
        while (layout.CloseWindow(1)) { }   // clean slate: Main only
        var tm = layout.Main;
        tm.SetTabs(new[] { new Tab("A", 1u), new Tab("B", 2u) });
        var tw = layout.AddWindow(new ChatWindow(50, 50, 400, 300, new[] { new Tab("X", 4u) }))!;
        check.Expect(layout.TransferTab(0, 1, 1, 0), "transfer B from win0 to win1 at pos 0");
        check.Expect(tm.Tabs.Count == 1 && tm.Tabs[0].Label == "A", "source lost the transferred tab");
        check.Expect(tw.Tabs.Count == 2 && tw.Tabs[0].Label == "B" && tw.Tabs[1].Label == "X", "target gained tab at position");
        check.Expect(!layout.TransferTab(0, 0, 1, 0), "last-tab transfer refused");
        check.Expect(tm.Tabs.Count == 1, "source unchanged after refusal");
        check.Expect(!layout.TransferTab(0, 0, 0, 0), "same-window transfer refused");
        check.Expect(!layout.TransferTab(0, 0, 99, 0), "out-of-range dst refused");

        // Locked destination refuses tab drop.
        tw.Locked = true;
        tm.SetTabs(new[] { new Tab("A", 1u), new Tab("C", 8u) });
        check.Expect(!layout.TransferTab(0, 0, 1, 0), "transfer to locked window refused");
        check.Expect(tm.Tabs.Count == 2, "source unchanged after locked refusal");
        tw.Locked = false;

        // Enter's window when Main's tab can't send: the focused window, never a click-through one, else none.
        while (layout.CloseWindow(1)) { }
        layout.Main.SetTabs(new[] { new Tab("Combat", 0x080) });
        var talker = layout.AddWindow(new ChatWindow(50, 50, 400, 300, new[] { new Tab("Talk", 0x001) }))!;
        layout.FocusedIndex = 1;
        check.Expect(layout.TypingWindow() == 1, "Main unable to send: Enter opens in the focused window that can");
        talker.ClickThrough = true;
        check.Expect(layout.TypingWindow() == -1, "...but never in a click-through one");
        layout.FocusedIndex = 0;
        check.Expect(layout.TypingWindow() == -1, "...and nowhere when the focused window can't send either");

        log.Info(check.Ok ? "ChatLayout.SelfCheck: passed" : "ChatLayout.SelfCheck: FAILED");
    }
}
