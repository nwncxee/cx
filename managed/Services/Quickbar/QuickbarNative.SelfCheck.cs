using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Quickbar;

internal static unsafe partial class QuickbarNative
{
    /// <summary>A CX slot shows what its engine button holds now: the icon and the count are read off the button, never a copy
    /// a hook kept from an earlier character. Regression tests, not approved examples. Oracle: the resrefs and counts typed
    /// here, written into a zeroed stand-in panel through the button's own fields and a stand-in count label.</summary>
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

    /// <summary>Oracle: the resrefs typed here, upper-case as the engine's setters write them, and their lower-case readings.</summary>
    private static void Icons(Checker check, IntPtr panel)
    {
        const int b = 4;
        var qb = new CGuiQuickButton(Button(panel, b));
        qb.Type = 2;
        Put(qb, "IS_MAGMISL");
        check.Expect(Read(panel, b).Icon == "is_magmisl", $"a spell button that no hook saw shows its spell's icon ({Read(panel, b).Icon})");
        check.Expect(ReferenceEquals(Read(panel, b).Icon, Read(panel, b).Icon), "an unchanged button reads the same string each frame, not a new one");

        Put(qb, "IS_DARKNES");
        check.Expect(Read(panel, b).Icon == "is_darknes", $"another spell whose name is just as long shows its own icon ({Read(panel, b).Icon})");

        qb.Type = 7;
        Put(qb, "IR_ATTACK");
        check.Expect(Read(panel, b).Icon == "ir_attack", $"the same button set to Attack shows Attack's icon ({Read(panel, b).Icon})");

        qb.Type = 0x12;
        Put(qb, "");
        check.Expect(Read(panel, b).Icon == "", $"a button cleared for a command line shows no icon from what it held ({Read(panel, b).Icon})");

        qb.Type = 4;
        Put(qb, "IFE_X2_SIXTEENCH");
        check.Expect(Read(panel, b).Icon == "ife_x2_sixteench", $"a resref of the full 16 characters reads whole ({Read(panel, b).Icon})");
    }

    /// <summary>A stand-in count label: its vtable, then the lines the two text slots answer with.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FakeLabel
    {
        public IntPtr Vtbl;
        public int Lines;
        public IntPtr Line0, Line1;
    }

    [UnmanagedCallersOnly]
    private static int FakeLineCount(IntPtr label) => ((FakeLabel*)label)->Lines;

    [UnmanagedCallersOnly]
    private static IntPtr FakeLine(IntPtr label, int i) => i == 0 ? ((FakeLabel*)label)->Line0 : i == 1 ? ((FakeLabel*)label)->Line1 : IntPtr.Zero;

    /// <summary>Oracle: the counts typed here, as the label's lines and as the bar should show them.</summary>
    private static void Counts(Checker check, IntPtr panel)
    {
        const int b = 30;
        var qb = new CGuiQuickButton(Button(panel, b));
        qb.Type = 2;
        var label = default(NativeBuffer<FakeLabel>);
        var vt = default(NativeBuffer<IntPtr>);
        var line0 = default(NativeBuffer<byte>);
        var line1 = default(NativeBuffer<byte>);
        try
        {
            int slots = Math.Max(EngineLayout.AurStringGetTextLineCountSlot, EngineLayout.AurStringGetTextLineSlot) / IntPtr.Size + 1;
            check.Expect(label.Take(1) && vt.Take(slots) && line0.Take(LineBytes) && line1.Take(LineBytes), "the stand-in label allocates");
            var table = new FakeVtable((IntPtr)vt.Pointer);
            table.Set(EngineLayout.AurStringGetTextLineCountSlot, (IntPtr)(delegate* unmanaged<IntPtr, int>)&FakeLineCount);
            table.Set(EngineLayout.AurStringGetTextLineSlot, (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr>)&FakeLine);
            ref FakeLabel l = ref label[0];
            l.Vtbl = (IntPtr)vt.Pointer; l.Line0 = (IntPtr)line0.Pointer; l.Line1 = (IntPtr)line1.Pointer;

            qb.CountLabel = default;
            check.Expect(Read(panel, b).Count == "", $"a button with no count label shows no count ({Read(panel, b).Count})");

            qb.CountLabel = new CAurString((IntPtr)label.Pointer);
            Line(line0.Span, "5"); l.Lines = 1;
            check.Expect(Read(panel, b).Count == "5", $"a button's label reading 5 shows 5 ({Read(panel, b).Count})");
            check.Expect(ReferenceEquals(Read(panel, b).Count, Read(panel, b).Count), "an unchanged label reads the same string each frame, not a new one");
            Line(line0.Span, "4");
            check.Expect(Read(panel, b).Count == "4", $"the label set to 4 shows 4, not the 5 it showed ({Read(panel, b).Count})");
            Line(line0.Span, "Use "); Line(line1.Span, "Item"); l.Lines = 2;
            check.Expect(Read(panel, b).Count == "Use Item", $"a label wrapped over two lines reads as one ({Read(panel, b).Count})");
            l.Lines = 0;
            check.Expect(Read(panel, b).Count == "", $"a label with no lines shows no count ({Read(panel, b).Count})");
        }
        finally { qb.CountLabel = default; label.Free(); vt.Free(); line0.Free(); line1.Free(); }
    }

    /// <summary>A stand-in label line's room, NUL included.</summary>
    private const int LineBytes = 32;

    private static void Line(Span<byte> line, string text)
    {
        line.Clear();
        System.Text.Encoding.ASCII.GetBytes(text, line);
    }

    private static void Put(CGuiQuickButton qb, string resref)
    {
        Span<byte> f = qb.Icon;
        f.Clear();
        System.Text.Encoding.ASCII.GetBytes(resref, f);
    }
}
