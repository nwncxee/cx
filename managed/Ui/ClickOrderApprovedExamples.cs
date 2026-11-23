using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Nameplate;

namespace CX.Ui;

/// <summary>CX windows, the game's panels and NUI draw and take presses in one order. Oracle: fixture windows at
/// hand-written rects, the engine probes' answers each Given states, and hand-written letter orders; the engine draws
/// its panels before nuklear's flush and its overlay scenes after it (<c>CGuiMan::UpdateAndRender</c>).</summary>
internal static unsafe class ClickOrderApprovedExamples
{
    private const byte Left = UiInputService.SdlButtonLeft;
    private const uint KeyA = 97;  // not-an-offset: SDL key code

    /// <summary>The CX inventory stands at (100,100)-(300,300); a NUI window over (200,200)-(400,400); the character
    /// sheet under all of it. <see cref="Overlap"/> is inside the inventory and the NUI window, <see cref="Bare"/>
    /// inside the inventory alone.</summary>
    private const int Overlap = 250, Bare = 150;

    /// <summary>The character sheet's letter, the one game panel these frames draw.</summary>
    private const string Sheet = "S";

    /// <summary>O then M for the overlay scene and the cursor.</summary>
    private const string Cursor = "OM";

    /// <summary>A stand-in for the CX inventory window with its flags: not focusable, capturing the pointer, taking every
    /// press (i) and wheel notch (w) inside its rect, and drawing c.</summary>
    private static Window Inventory(StringBuilder asked, StringBuilder drawn) => new(100, 100, 200, 200)
    {
        Focusable = false,
        CapturesPointer = true,
        OnMouseButton = (down, _, _, _) => { if (down) asked.Append('i'); return true; },
        OnMouseWheel = _ => { asked.Append('w'); return true; },
        DrawContent = () => drawn.Append('c'),
    };

    /// <summary>The frame with Alloy on, the overlays drawn as <paramref name="overlays"/>.</summary>
    private static string AlloyFrame(StringBuilder drawn, string overlays = Cursor) => FrameLetters.Alloy(drawn, Sheet, overlays);

    /// <summary>The frame with Alloy off, the overlays drawn as <paramref name="overlays"/>.</summary>
    private static string GlFrame(StringBuilder drawn, string overlays = Cursor) => FrameLetters.Gl(drawn, Sheet, overlays);

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ClickOrderApprovedExamples.SelfCheck");
        var asked = new StringBuilder();
        var drawn = new StringBuilder();
        UiHost.Alone(() => Presses(check, asked, drawn));
        UiHost.Alone(() => Plates(check, drawn));
        UiHost.Alone(() => HoverFocus(check));
        UiHost.Alone(() => WindowsClient(check, asked, drawn));
        log.Info(check.Ok ? "ClickOrderApprovedExamples.SelfCheck: passed" : "ClickOrderApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>Presses, wheel notches and the draw order around the inventory, NUI and the character sheet.</summary>
    private static void Presses(Checker check, StringBuilder asked, StringBuilder drawn)
    {
        Inventory(asked, drawn);

        // Given a NUI window from the server drawn over part of the CX inventory
        // When the player clicks where the two overlap
        // Then the NUI window takes the click and the inventory does not
        asked.Clear();
        var e1 = UiInputService.RoutePress(Left, Overlap, Overlap, new UiInputService.PointerUnder(true, false, false));
        check.Expect(e1 == UiInputService.PointerOutcome.Engine && asked.ToString() == "",
            $"E1 a press where NUI covers the inventory goes to NUI: outcome {e1}, inventory asked '{asked}'");

        // Given the same NUI window over part of the CX inventory
        // When the player clicks a part of the inventory the NUI window does not cover
        // Then the inventory takes the click
        asked.Clear();
        var e2 = UiInputService.RoutePress(Left, Bare, Bare, new UiInputService.PointerUnder(false, false, false));
        check.Expect(e2 == UiInputService.PointerOutcome.Taken && asked.ToString() == "i",
            $"E2 a press on the uncovered inventory goes to it: outcome {e2}, inventory asked '{asked}'");

        // Given the CX inventory over part of the stock character sheet
        // When the player clicks where the two overlap
        // Then the inventory takes the click and the character sheet does not
        asked.Clear();
        var e3 = UiInputService.RoutePress(Left, Bare, Bare, new UiInputService.PointerUnder(false, true, false));
        check.Expect(e3 == UiInputService.PointerOutcome.Taken && asked.ToString() == "i",
            $"E3 a press where the inventory covers the character sheet goes to the inventory: outcome {e3}, inventory asked '{asked}'");

        // Given Alloy switched off
        // When the CX inventory, a NUI window and the stock character sheet overlap
        // Then they draw and take clicks in the same order as with Alloy on
        string alloy = AlloyFrame(drawn, ""), gl = GlFrame(drawn, "");
        asked.Clear();
        var top = UiInputService.RoutePress(Left, Overlap, Overlap, new UiInputService.PointerUnder(true, true, false));
        string askedTop = asked.ToString();
        asked.Clear();
        var mid = UiInputService.RoutePress(Left, Bare, Bare, new UiInputService.PointerUnder(false, true, false));
        check.Expect(alloy == "ScN" && gl == "ScN",
            $"E4 the sheet, the inventory and NUI draw bottom to top on both paths: Alloy '{alloy}', GL '{gl}'");
        check.Expect(top == UiInputService.PointerOutcome.Engine && askedTop == "" && mid == UiInputService.PointerOutcome.Taken && asked.ToString() == "i",
            $"E4 NUI takes the triple overlap and the inventory the sheet's: {top} asked '{askedTop}', {mid} asked '{asked}'");

        // Given the CX inventory open over part of the stock character sheet
        // When the player turns the mouse wheel where the two overlap
        // Then the inventory scrolls and the character sheet does not
        asked.Clear();
        var e24 = UiInputService.RouteWheel(1, Bare, Bare, new UiInputService.PointerUnder(false, true, false));
        check.Expect(e24 == UiInputService.PointerOutcome.Taken && asked.ToString() == "w",
            $"E24 a wheel notch where the inventory covers the character sheet scrolls the inventory: outcome {e24}, inventory asked '{asked}'");

        // Given the CX inventory and a NUI window open, with the software cursor
        // When the player moves the cursor over either one
        // Then the cursor draws over it
        string alloyCursor = AlloyFrame(drawn), glCursor = GlFrame(drawn);
        check.Expect(CursorLast(alloyCursor) && CursorLast(glCursor),
            $"E5 the cursor draws last, over the inventory and NUI, on both paths: Alloy '{alloyCursor}', GL '{glCursor}'");
    }

    /// <summary>True when the cursor draws last and both the inventory and NUI draw before it.</summary>
    private static bool CursorLast(string f)
        => f.EndsWith('M') && f.IndexOf('c', StringComparison.Ordinal) >= 0 && f.IndexOf('N', StringComparison.Ordinal) >= 0
           && f.IndexOf('M', StringComparison.Ordinal) > f.IndexOf('c', StringComparison.Ordinal)
           && f.IndexOf('M', StringComparison.Ordinal) > f.IndexOf('N', StringComparison.Ordinal);

    /// <summary>A nameplate under the character sheet, on both render paths.</summary>
    private static void Plates(Checker check, StringBuilder drawn)
    {
        NameplateService.NewWindow().DrawContent = () => drawn.Append('p');

        // Given Alloy switched off and a creature's nameplate behind the stock character sheet
        // When the player looks at where they overlap
        // Then the character sheet draws over the nameplate, as it does with Alloy on
        string alloy = AlloyFrame(drawn), gl = GlFrame(drawn);
        check.Expect(alloy == "pSNOM" && gl == "pSNOM",
            $"E26 the nameplate draws under the character sheet on both paths: Alloy '{alloy}', GL '{gl}'");
    }

    /// <summary>The chatbox's own window over a fixture layout of one chat window at the inventory's rect, a module up.</summary>
    private static void HoverFocus(Checker check)
    {
        bool saved = UiHost.HoverToFocus, inGame = GameGate.InGame, typing = ChatService.IsTyping;
        ChatLayout? was = ChatService.UseLayout(new ChatLayout(ChatExamplesKit.Window(100, 100, 200, 200)));
        try
        {
            GameGate.InGame = true;
            Window chat = ChatService.NewWindow();
            UiHost.HoverToFocus = true;

            // Given hover-to-focus switched on and a NUI window from the server over part of the CX chat window
            // When the player moves the pointer onto the overlap and types
            // Then the chat window does not take focus and the keys do not go into its input line
            UiInputService.HoverMoved(Overlap, Overlap, new UiInputService.PointerUnder(true, false, false));
            bool keyed = UiHost.DispatchKey(KeyA, 0);
            check.Expect(UiHost.HitTest(Overlap, Overlap) == chat && UiHost.Focused != chat && !keyed && !ChatService.IsTyping,
                $"E25 hovering where NUI covers the chat window leaves it unfocused: focused {UiHost.Focused == chat}, key taken {keyed}, typing {ChatService.IsTyping}");
        }
        finally
        {
            UiHost.HoverToFocus = saved;
            ChatService.IsTyping = typing;
            GameGate.InGame = inGame;
            ChatService.UseLayout(was);
        }
    }

    /// <summary>The Windows press path, where nuklear's hover call is absent: a NUI window list built here at hand-written bounds.</summary>
    private static void WindowsClient(Checker check, StringBuilder asked, StringBuilder drawn)
    {
        Inventory(asked, drawn);
        int ctxBytes = EngineLayout.NkCtxWindowBegin + IntPtr.Size;
        int winBytes = Math.Max(EngineLayout.NkWinNext, Math.Max(EngineLayout.NkWinFlags, EngineLayout.NkWinBounds + 4 * sizeof(float))) + IntPtr.Size;
        void* ctx = NativeMemory.AllocZeroed((nuint)ctxBytes), win = NativeMemory.AllocZeroed((nuint)winBytes);
        try
        {
            var nui = new NkWindow((IntPtr)win);
            nui.Bounds[0] = 200; nui.Bounds[1] = 200; nui.Bounds[2] = 200; nui.Bounds[3] = 200;
            new NkContext((IntPtr)ctx).WindowBegin = (IntPtr)win;

            // Given the Windows client with Alloy switched off
            // When a NUI window overlaps the CX inventory and the player clicks the overlap
            // Then the NUI window takes the click and draws over the inventory, as on Linux
            bool over = UiInputService.NuiOver((IntPtr)ctx, null, Overlap, Overlap);
            asked.Clear();
            var e27 = UiInputService.RoutePress(Left, Overlap, Overlap, new UiInputService.PointerUnder(over, false, false));
            check.Expect(over && e27 == UiInputService.PointerOutcome.Engine && asked.ToString() == "",
                $"E27 with no hover call NUI's window list takes the press over the inventory: NUI over {over}, outcome {e27}, inventory asked '{asked}'");
        }
        finally { NativeMemory.Free(ctx); NativeMemory.Free(win); }
    }
}
