using System;
using System.Collections.Generic;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Dialog;

public sealed unsafe partial class DialogService
{
    /// <summary>Escape ends the server's conversation (approved examples E1, E5, E23). Oracle: the reply
    /// lines the log tap hears, against hand-written literals of the stock panel's reply.</summary>
    private static void EscapeApprovedExamples(ExtenderLog log)
    {
        var check = new Checker(log, "DialogApprovedExamples.SelfCheck");
        if (ClientApi.Message() != IntPtr.Zero) { check.Skip("a live session would put the checks' replies on the wire"); return; }

        // Given an open CX conversation window on a node with replies to pick
        // When the player presses Escape
        // Then the server receives one abort reply for that conversation and page, and the window fades out
        List<string> sent = Heard(() => { Node(42, 7, new[] { "Yes.", "No." }, 0); HandleKey(EscapeKey, 0); }, out bool fading);
        check.Expect(sent.Count == 1 && sent[0].Contains("reply dlg=42 idx=4294967295 abort=1 page=7") && fading,
          $"E1 Escape sends one abort reply and fades: sent [{string.Join(" | ", sent)}] fading={fading}");

        // Given the server marked the open node as a continue node
        // When the player presses Escape
        // Then the server receives the first reply as a normal pick, not an abort
        sent = Heard(() => { Node(42, 7, new[] { "Continue" }, 1); HandleKey(EscapeKey, 0); }, out _);
        check.Expect(sent.Count == 1 && sent[0].Contains("reply dlg=42 idx=10 abort=0 page=7"),
          $"E5 Escape on a continue node picks the first reply: sent [{string.Join(" | ", sent)}]");

        // Given a conversation open in the CX conversation window
        // When the player presses Esc
        // Then the conversation window closes, the NPC stops talking, and the game menu does not open
        bool taken = false;
        UiHost.Alone(() =>
        {
            _ = NewWindow();
            sent = Heard(() => { Node(42, 7, new[] { "Yes.", "No." }, 0); taken = EscOrderApprovedExamples.Press(); }, out bool closed);
            check.Expect(taken && closed && sent.Count == 1 && sent[0].Contains("reply dlg=42 idx=4294967295 abort=1 page=7"),
              $"E23 the first Esc is kept from the engine and ends the conversation with the abort reply the stock close button sends: taken={taken} closed={closed} sent [{string.Join(" | ", sent)}]");
        });

        _engineOwns = false;
        Close();
        if (check.Ok) log.Info("DialogApprovedExamples.SelfCheck: passed");
    }
}
