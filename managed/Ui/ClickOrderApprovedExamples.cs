using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Nameplate;

namespace CX.Ui;

/// <summary>CX windows, the game's panels and NUI draw and take presses in one order. Oracle: fixture windows at
/// hand-written rects, the engine probes' answers each Given states, and hand-written letter orders; the engine draws
/// its panels before nuklear's flush and its overlay scenes after it (<c>CGuiMan::UpdateAndRender</c>).</summary>
internal static unsafe class ClickOrderApprovedExamples
{
    private const byte Left = 1;  // not-an-offset: SDL_BUTTON_LEFT
    private const uint KeyA = 97;  // not-an-offset: SDL key code

    /// <summary>The CX inventory stands at (100,100)-(300,300); a NUI window over (200,200)-(400,400); the character
    /// sheet under all of it. <see cref="Overlap"/> is inside the inventory and the NUI window, <see cref="Bare"/>
    /// inside the inventory alone.</summary>
    private const int Overlap = 250, Bare = 150;

    /// <summary>A letter per thing drawn: S for the character sheet (a panel), N for NUI, O then M for the overlay scene
    /// and the cursor, and whatever each CX window appends.</summary>
    private readonly struct Letters(StringBuilder drawn) : IGuiFrameSink, UiHostService.INkSiteSink
    {
        public bool Windows(UiLayer layer) { UiHost.DrawFrame(layer); return true; }
        public bool Panels() { drawn.Append('S'); return true; }
        public bool Nui() { drawn.Append('N'); return true; }
        public bool Overlays() { drawn.Append("OM"); return true; }
        public void Windows(UiLayer from, UiLayer to) { for (UiLayer l = from; l <= to; l++) UiHost.DrawFrame(l); }
        void UiHostService.INkSiteSink.Nui() => drawn.Append('N');
    }

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

    /// <summary>The frame with Alloy on: <see cref="GuiFrame.Draw{T}"/> over the registered windows.</summary>
    private static string AlloyFrame(StringBuilder drawn)
    {
        drawn.Clear();
        GuiFrame.Draw(new Letters(drawn), true);
        return drawn.ToString();
    }

    /// <summary>The frame with Alloy off: the engine's panels, the GL path at nuklear's flush, then the engine's overlay scenes.</summary>
    private static string GlFrame(StringBuilder drawn)
    {
        drawn.Clear();
        drawn.Append('S');
        UiHostService.AtNuklear(new Letters(drawn));
        drawn.Append("OM");
        return drawn.ToString();
    }

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
        string alloy = AlloyFrame(drawn), gl = GlFrame(drawn);
        asked.Clear();
        var top = UiInputService.RoutePress(Left, Overlap, Overlap, new UiInputService.PointerUnder(true, true, false));
        string askedTop = asked.ToString();
        asked.Clear();
        var mid = UiInputService.RoutePress(Left, Bare, Bare, new UiInputService.PointerUnder(false, true, false));
        check.Expect(alloy == "ScNOM" && gl == "ScNOM",
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
        check.Expect(alloy.EndsWith('M') && alloy.IndexOf('M') > alloy.IndexOf('c') && alloy.IndexOf('M') > alloy.IndexOf('N'),
            $"E5 the cursor draws last, over the inventory and NUI: '{alloy}'");
    }

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

    /// <summary>A stand-in chat window that takes focus on hover and keys while it holds focus, as a focusable CX window does.</summary>
    private static void HoverFocus(Checker check)
    {
        bool saved = UiHost.HoverToFocus;
        var line = new StringBuilder();
        Window? chat = null;
        chat = new Window(100, 100, 200, 200) { OnKey = (key, _) => { if (UiHost.Focused != chat) return false; line.Append((char)key); return true; } };
        try
        {
            UiHost.HoverToFocus = true;

            // Given hover-to-focus switched on and a NUI window from the server over part of the CX chat window
            // When the player moves the pointer onto the overlap and types
            // Then the chat window does not take focus and the keys do not go into its input line
            UiInputService.HoverMoved(Overlap, Overlap, true);
            UiHost.DispatchKey(KeyA, 0);
            check.Expect(UiHost.Focused != chat && line.Length == 0,
                $"E25 hovering where NUI covers the chat window leaves it unfocused: focused {UiHost.Focused == chat}, its line '{line}'");
        }
        finally { UiHost.HoverToFocus = saved; }
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
            string gl = GlFrame(drawn);
            check.Expect(over && e27 == UiInputService.PointerOutcome.Engine && asked.ToString() == "",
                $"E27 with no hover call NUI's window list takes the press over the inventory: NUI over {over}, outcome {e27}, inventory asked '{asked}'");
            check.Expect(gl.IndexOf('N') > gl.IndexOf('c') && gl.IndexOf('c') >= 0,
                $"E27 NUI draws over the inventory with Alloy off: '{gl}'");
        }
        finally { NativeMemory.Free(ctx); NativeMemory.Free(win); }
    }
}
