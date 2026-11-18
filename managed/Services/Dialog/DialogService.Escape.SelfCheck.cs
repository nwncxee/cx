using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Dialog;

public sealed unsafe partial class DialogService
{
    /// <summary>Escape where it already sends nothing: End Dialog, the gap after a pick, a second press and the
    /// stock panel's conversation. Oracle: the reply lines the log tap hears, against hand-written literals.</summary>
    private static void EscapeSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DialogService.EscapeSelfCheck");
        if (ClientApi.Message() != IntPtr.Zero) { check.Skip("a live session would put the checks' replies on the wire"); return; }

        // Given the conversation window shows only End Dialog
        // When the player presses Escape
        // Then the server receives nothing and the window fades out
        List<string> sent = Heard(() => { Node(42, 7, Array.Empty<string>(), 0); HandleKey(EscapeKey, 0); }, out bool fading);
        check.Expect(sent.Count == 0 && fading, $"E3 Escape on End Dialog sends nothing and fades: sent [{string.Join(" | ", sent)}] fading={fading}");

        // Given the player picked a reply and the next node has not arrived
        // When the player presses Escape
        // Then the server receives nothing more and the window fades out
        Node(42, 7, new[] { "Yes.", "No." }, 0);
        Select(0);
        sent = Heard(() => HandleKey(EscapeKey, 0), out fading);
        check.Expect(sent.Count == 0 && fading, $"E4 Escape after a pick sends nothing more and fades: sent [{string.Join(" | ", sent)}] fading={fading}");

        // Given Escape has just closed the conversation window
        // When the player presses Escape again
        // Then the server receives nothing more
        Node(42, 7, new[] { "Yes.", "No." }, 0);
        HandleKey(EscapeKey, 0);
        bool consumed = true;
        sent = Heard(() => consumed = HandleKey(EscapeKey, 0), out _);
        check.Expect(sent.Count == 0 && !consumed, $"E6 a second Escape sends nothing and goes back to the game: sent [{string.Join(" | ", sent)}] consumed={consumed}");

        // Given the CX conversation window is switched off and the stock panel shows the conversation
        // When the player presses Escape
        // Then CX sends nothing, and the stock panel ends the conversation as it always has
        Node(42, 7, new[] { "Yes.", "No." }, 0);
        ShowDialogEntryFn entry = &OnShowEntry;
        entry(IntPtr.Zero, IntPtr.Zero, 43, 1);   // no in-game GUI: the stock panel takes the node
        consumed = true;
        sent = Heard(() => consumed = HandleKey(EscapeKey, 0), out _);
        check.Expect(_engineOwns && sent.Count == 0 && !consumed, $"E7 the stock panel's conversation keeps its own Escape: sent [{string.Join(" | ", sent)}] consumed={consumed}");

        _engineOwns = false;
        Close();
        if (check.Ok) log.Info("DialogService.EscapeSelfCheck: passed");
    }

    /// <summary>A CX-owned node of dialog <paramref name="dialogId"/> on page <paramref name="pageId"/>, put up
    /// through the replies hook as the message layer would: reply i carries server index <c>(i + 1) * 10</c>.</summary>
    private static void Node(uint dialogId, uint pageId, string[] replies, int continueFlag)
    {
        Close();
        _engineOwns = false;
        _dialogId = dialogId;
        _visible = true;
        int n = replies.Length;
        var recs = new ExoStringRec[n];
        var idx = new uint[n];
        var pins = new GCHandle[n];
        try
        {
            for (int i = 0; i < n; i++)
            {
                byte[] text = Encoding.UTF8.GetBytes(replies[i]);
                pins[i] = GCHandle.Alloc(text, GCHandleType.Pinned);
                recs[i].Buf = (byte*)pins[i].AddrOfPinnedObject();
                recs[i].Len = (uint)text.Length;
                idx[i] = (uint)(i + 1) * 10;
            }
            fixed (ExoStringRec* rp = recs)
            fixed (uint* ip = idx)
            {
                ShowDialogRepliesFn show = &OnShowReplies;
                show(IntPtr.Zero, n, (IntPtr)rp, (IntPtr)ip, 0, pageId, continueFlag);
            }
        }
        finally { foreach (GCHandle pin in pins) if (pin.IsAllocated) pin.Free(); }
    }

    /// <summary>The reply lines <paramref name="act"/> logs, heard on an ear of the check's own, and whether the
    /// window is fading out after it.</summary>
    private static List<string> Heard(Action act, out bool fading)
    {
        var heard = new List<string>();
        var ear = new ExtenderLog { Tap = line => { if (line.Contains("reply dlg=")) heard.Add(line); } };
        ExtenderLog? was = _log;
        _log = ear;
        try { act(); }
        finally { _log = was; }
        fading = !_visible && _winAlphaTarget == 0f;
        return heard;
    }
}
