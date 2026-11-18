using System;
using System.Collections.Generic;
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
        List<string> sent = Heard(log, () => { Node(42, 7, Array.Empty<string>(), 0); HandleKey(EscapeKey, 0); }, out bool fading);
        check.Expect(sent.Count == 0 && fading, $"E3 Escape on End Dialog sends nothing and fades: sent [{string.Join(" | ", sent)}] fading={fading}");

        // Given the player picked a reply and the next node has not arrived
        // When the player presses Escape
        // Then the server receives nothing more and the window fades out
        Node(42, 7, new[] { "Yes.", "No." }, 0);
        Select(0);
        sent = Heard(log, () => HandleKey(EscapeKey, 0), out fading);
        check.Expect(sent.Count == 0 && fading, $"E4 Escape after a pick sends nothing more and fades: sent [{string.Join(" | ", sent)}] fading={fading}");

        // Given Escape has just closed the conversation window
        // When the player presses Escape again
        // Then the server receives nothing more
        Node(42, 7, new[] { "Yes.", "No." }, 0);
        HandleKey(EscapeKey, 0);
        bool consumed = true;
        sent = Heard(log, () => consumed = HandleKey(EscapeKey, 0), out _);
        check.Expect(sent.Count == 0 && !consumed, $"E6 a second Escape sends nothing and goes back to the game: sent [{string.Join(" | ", sent)}] consumed={consumed}");

        // Given the CX conversation window is switched off and the stock panel shows the conversation
        // When the player presses Escape
        // Then CX sends nothing, and the stock panel ends the conversation as it always has
        Close();
        _engineOwns = true;
        consumed = true;
        sent = Heard(log, () => consumed = HandleKey(EscapeKey, 0), out _);
        check.Expect(sent.Count == 0 && !consumed, $"E7 the stock panel's conversation keeps its own Escape: sent [{string.Join(" | ", sent)}] consumed={consumed}");

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
        var recs = new ExoStringRec[Math.Max(n, 1)];
        var idx = new uint[Math.Max(n, 1)];
        var bytes = new byte[n][];
        for (int i = 0; i < n; i++) { bytes[i] = Encoding.UTF8.GetBytes(replies[i] + "\0"); idx[i] = (uint)(i + 1) * 10; }
        var pins = new System.Runtime.InteropServices.GCHandle[n];
        try
        {
            for (int i = 0; i < n; i++)
            {
                pins[i] = System.Runtime.InteropServices.GCHandle.Alloc(bytes[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                recs[i].Buf = (byte*)pins[i].AddrOfPinnedObject();
                recs[i].Len = (uint)(bytes[i].Length - 1);
            }
            fixed (ExoStringRec* rp = recs)
            fixed (uint* ip = idx)
            {
                delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, int, uint, int, long> show = &OnShowReplies;
                show(IntPtr.Zero, n, (IntPtr)rp, (IntPtr)ip, 0, pageId, continueFlag);
            }
        }
        finally { for (int i = 0; i < n; i++) if (pins[i].IsAllocated) pins[i].Free(); }
    }

    /// <summary>The reply lines <paramref name="act"/> logs, and whether the window is fading out after it.</summary>
    private static List<string> Heard(ExtenderLog log, Action act, out bool fading)
    {
        var heard = new List<string>();
        Action<string>? tap = log.Tap;
        log.Tap = line => { if (line.Contains("reply dlg=")) heard.Add(line); };
        try { act(); }
        finally { log.Tap = tap; }
        fading = !_visible && _winAlphaTarget == 0f;
        return heard;
    }
}
