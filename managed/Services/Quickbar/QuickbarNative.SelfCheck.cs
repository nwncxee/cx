using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Quickbar;

internal static unsafe partial class QuickbarNative
{
    /// <summary>A CX slot shows what its engine button holds now: the icon and the count are read off the button, never a copy
    /// a hook kept from an earlier character. Regression tests, not approved examples. Oracle: the resrefs and counts typed
    /// here, written into a zeroed stand-in panel through the button's own fields.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        if (!EngineLayout.KeyOk) { check.Skip("no measured client build, so no button layout to lay a stand-in out by"); return; }
        QuickbarNativeState keep = _s;
        _s = new QuickbarNativeState();
        var mem = default(NativeBuffer<byte>);
        try
        {
            check.Expect(mem.Take(EngineLayout.QuickbarButtons + Buttons * EngineLayout.QuickbarButtonStride), "the stand-in panel allocates");
            Icons(check, (IntPtr)mem.Pointer);
            Counts(check, (IntPtr)mem.Pointer);
        }
        finally { mem.Free(); _s = keep; }
        log.Info(check.Ok ? "QuickbarNative.SelfCheck: passed" : "QuickbarNative.SelfCheck: FAILED");
    }

    /// <summary>Oracle: the resrefs typed here, upper-case as the engine's setters write them.</summary>
    private static void Icons(Checker check, IntPtr panel)
    {
        const int b = 4;
        var qb = new CGuiQuickButton(Button(panel, b));
        qb.Type = 2;
        Put(qb, "is_magmisl");
        check.Expect(Read(panel, b).Icon == "is_magmisl", $"a spell button that no hook saw shows its spell's icon ({Read(panel, b).Icon})");
        check.Expect(ReferenceEquals(Read(panel, b).Icon, Read(panel, b).Icon), "an unchanged button reads the same string each frame, not a new one");

        Put(qb, "is_darknes");
        check.Expect(Read(panel, b).Icon == "is_darknes", $"another spell whose name is just as long shows its own icon ({Read(panel, b).Icon})");

        qb.Type = 7;
        Put(qb, "IR_ATTACK");
        check.Expect(Read(panel, b).Icon == "ir_attack", $"the same button set to Attack shows Attack's icon, lower-cased ({Read(panel, b).Icon})");

        qb.Type = 0x12;
        Put(qb, "");
        check.Expect(Read(panel, b).Icon == "", $"a button cleared for a command line shows no icon from what it held ({Read(panel, b).Icon})");

        qb.Type = 4;
        Put(qb, "ife_x2_sixteench");
        check.Expect(Read(panel, b).Icon == "ife_x2_sixteench", $"a resref of the full 16 characters reads whole ({Read(panel, b).Icon})");
    }

    /// <summary>Oracle: the counts typed here, and a label pointer that is null or not.</summary>
    private static void Counts(Checker check, IntPtr panel)
    {
        const int b = 30;
        var qb = new CGuiQuickButton(Button(panel, b));
        qb.Type = 2;
        _s.Count[b] = "5";
        qb.CountLabel = default;
        check.Expect(Read(panel, b).Count == "", $"a button with no count label shows no count, whatever count was last copied ({Read(panel, b).Count})");
        qb.CountLabel = new CAurString(panel);
        check.Expect(Read(panel, b).Count == "5", $"a button with a count label shows the last count set ({Read(panel, b).Count})");
    }

    private static void Put(CGuiQuickButton qb, string resref)
    {
        Span<byte> f = qb.Icon;
        f.Clear();
        for (int i = 0; i < resref.Length; i++) f[i] = (byte)resref[i];
    }
}
