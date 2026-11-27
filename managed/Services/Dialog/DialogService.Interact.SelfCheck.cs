using System;
using CX.Framework;
using CX.Services.Interact;

namespace CX.Services.Dialog;

public sealed unsafe partial class DialogService
{
    /// <summary>A conversation the dialog hook opens, in either window, frees its creature from Interact's passed-over
    /// memory. Oracle: the object ids the world double hands out.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool InteractSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DialogService.InteractSelfCheck");
        if (ClientApi.Message() != IntPtr.Zero) return check.Skip("a live session would show the checks' nodes");
        bool was = DialogKnobs.Enabled.V;
        ShowDialogEntryFn entryOrig = _entryOrig;
        CloseDialogFn closeOrig = _closeOrig;
        // In the real client these are the engine's own ShowDialogEntry and CloseDialog, which a null GUI crashes.
        _entryOrig = null;
        _closeOrig = null;
        try
        {
            ShowDialogEntryFn entry = &OnShowEntry;
            foreach (bool cx in new[] { true, false })
            {
                DialogKnobs.Enabled.Set(cx);
                var w = InteractWorldDouble.Install();
                uint mute = w.Creature(3f, talks: false); w.Creature(6f);
                w.Press('F');
                entry(IntPtr.Zero, IntPtr.Zero, mute, 1);
                w.Press('F');
                check.Expect(w.Did(mute, mute), $"a conversation the dialog hook opened (CX dialog {(cx ? "on" : "off")}) makes the creature tried afresh: {w.Seen}");
            }
        }
        finally
        {
            _entryOrig = entryOrig;
            _closeOrig = closeOrig;
            InteractWorldDouble.Restore();
            DialogKnobs.Enabled.Set(was);
            _engineOwns = false;
            Close();
        }
        log.Info(check.Ok ? "DialogService.InteractSelfCheck: passed" : "DialogService.InteractSelfCheck: FAILED");
        return check.Ok;
    }
}
