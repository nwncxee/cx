using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>The consent prompt's key labels off the client, where SDL names no key: a modifier KeyNames names itself,
/// a chord's prefix and a code nothing names. Oracle: the labels written here.</summary>
internal static class HotkeyConsentLabels
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "HotkeyConsentLabels.SelfCheck");
        string shift = HotkeyConsent.KeyName(0x400000E1);  // not-an-offset: SDL key code
        string chord = HotkeyConsent.KeyName(HotkeyProtocol.ModCtrl | 'f');
        string unknown = HotkeyConsent.KeyName(0x40000000);  // not-an-offset: SDL key code
        check.Expect(shift == "Left Shift", $"Left Shift reads as its name: '{shift}'");
        check.Expect(chord == "Ctrl+F", $"Ctrl with F reads Ctrl+F: '{chord}'");
        check.Expect(unknown == "key 1073741824", $"a code no table names reads as its number, never blank: '{unknown}'");
        log.Info(check.Ok ? "HotkeyConsentLabels.SelfCheck: passed" : "HotkeyConsentLabels.SelfCheck: FAILED");
        return check.Ok;
    }
}
