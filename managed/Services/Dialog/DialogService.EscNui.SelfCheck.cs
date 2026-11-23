using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Services.Nui;
using CX.Ui;

namespace CX.Services.Dialog;

public sealed unsafe partial class DialogService
{
    /// <summary>Esc with a server NUI window over the conversation window closes NUI's window and leaves the
    /// conversation up, as it does over the inventory. Oracle: the panel rect and NUI's close bit set by hand.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void EscNuiSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DialogService.EscNui.SelfCheck");
        if (ClientApi.Message() != IntPtr.Zero) { check.Skip("live session"); return; }
        using var nk = new NuiScratch();
        UiHost.Alone(() =>
        {
            // Given a conversation up and a closable server NUI window over its panel
            // When the player presses Esc
            // Then NUI's window closes and the conversation stays up, with no reply sent
            Window w = NewWindow();
            IntPtr ctx = EscOrderApprovedExamples.NuiContext(nk);
            IntPtr over = EscOrderApprovedExamples.NuiWindow(nk, ctx, EscOrderApprovedExamples.Json + "quest",
                EscOrderApprovedExamples.Closable, 100f, 100f, 300f, 200f);
            bool taken = false;
            List<string> sent = Heard(() =>
            {
                Node(42, 7, new[] { "Yes.", "No." }, 0);
                _frame = new DialogFrame { PanelX = 50f, PanelY = 50f, PanelW = 500f, TitleH = 400f };
                w.SuppressChords = true;
                taken = EscOrderApprovedExamples.Press(ctx);
            }, out bool closed);
            check.Expect(taken && EscOrderApprovedExamples.Closed(over) && !closed && sent.Count == 0,
                $"Esc with a closable server NUI window over the conversation closes NUI's window and keeps the conversation: taken={taken} nui=0x{EscOrderApprovedExamples.Flags(over):x} conversationClosed={closed} sent={sent.Count}");
        });
        _engineOwns = false;
        Close();
        using var nk2 = new NuiScratch();
        UiHost.Alone(() =>
        {
            // Given a NUI edit box holding the keyboard and a closable server NUI window over the conversation
            // When the player presses Esc
            // Then the pump leaves the key to the engine and NUI's window stays open
            Window w = NewWindow();
            IntPtr ctx = EscOrderApprovedExamples.NuiContext(nk2);
            IntPtr over = EscOrderApprovedExamples.NuiWindow(nk2, ctx, EscOrderApprovedExamples.Json + "quest",
                EscOrderApprovedExamples.Closable, 100f, 100f, 300f, 200f);
            Node(42, 7, new[] { "Yes." }, 0);
            _frame = new DialogFrame { PanelX = 50f, PanelY = 50f, PanelW = 500f, TitleH = 400f };
            bool taken = UiInputService.PumpKeyDown(0x1B, 0, new UiInputService.EscUnder(false, false, ctx, NuiEdit: true));  // not-an-offset: SDL Esc
            check.Expect(!taken && !EscOrderApprovedExamples.Closed(over),
                $"a NUI edit box keeps Esc from closing NUI's window at the pump: taken={taken} nui=0x{EscOrderApprovedExamples.Flags(over):x}");
        });
        _engineOwns = false;
        Close();
        if (check.Ok) log.Info("DialogService.EscNui.SelfCheck: passed");
    }
}
