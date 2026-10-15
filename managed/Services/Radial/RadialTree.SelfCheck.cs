using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

internal static unsafe partial class RadialTree
{
    private static IntPtr _fakeSelf;
    private static long _fakeParam;
    private static int _fakeCalls;
    private static IntPtr _fakeGob;

    [UnmanagedCallersOnly]
    private static void FakeHandler(IntPtr self, long param) { _fakeSelf = self; _fakeParam = param; _fakeCalls++; }

    /// <summary>The count-label string (one line) beside the two-line label string.</summary>
    private static IntPtr _fakeCountGob;

    [UnmanagedCallersOnly]
    private static int FakeLineCount(IntPtr gob) => gob == _fakeGob ? 2 : gob == _fakeCountGob ? 1 : 0;

    private static IntPtr _line0, _line1, _countLine;
    [UnmanagedCallersOnly]
    private static IntPtr FakeLine(IntPtr gob, int i) =>
        gob == _fakeCountGob ? (i == 0 ? _countLine : IntPtr.Zero) : gob != _fakeGob ? IntPtr.Zero : i == 0 ? _line0 : _line1;

    /// <summary>A stand-in <c>CreateSubMenu</c>: stores <see cref="_fakeChild"/> at <c>m_pSubMenu[i]</c> for slot 1 only.</summary>
    private static IntPtr _fakeChild;
    [UnmanagedCallersOnly]
    private static int FakeCreate(IntPtr menu, int i)
    {
        if (i != 1) return 0;
        var m = new CRadialMenu(menu);
        m.SubMenu[i] = new CRadialMenu(_fakeChild);
        return 1;
    }

    /// <summary>A fake root laid out per the resolved <see cref="EngineLayout"/> rows (so the same check runs on either
    /// platform's offsets), with a planted vptr, a text bubble + CAurString pair whose vtable carries managed fake line
    /// readers, a CGuiIcon blob with a count label, sentinel and set callbacks, the event-object global redirected at a
    /// stack cell so the bracket's restore can be asserted, and a faked <c>CreateSubMenu</c>. Teardown has no fake.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        const int BlobSize = 0x900, VtSize = 0x200, ObjAdj = 0x10;  // not-an-offset: sizes of the self-check's fake objects
        byte* menu = (byte*)NativeMemory.AllocZeroed(BlobSize);
        byte* fakeVt = (byte*)NativeMemory.AllocZeroed(VtSize);
        byte* handlerVt = (byte*)NativeMemory.AllocZeroed(VtSize);
        byte* handlerObj = (byte*)NativeMemory.AllocZeroed(0x40);
        byte* bubble = (byte*)NativeMemory.AllocZeroed(0x200);
        byte* gob = (byte*)NativeMemory.AllocZeroed(0x600);
        byte* icon = (byte*)NativeMemory.AllocZeroed(0x48);
        byte* textVt = (byte*)NativeMemory.AllocZeroed(VtSize);
        byte* line0 = (byte*)NativeMemory.AllocZeroed(32);
        byte* line1 = (byte*)NativeMemory.AllocZeroed(32);
        byte* countGob = (byte*)NativeMemory.AllocZeroed(0x600);
        byte* countLine = (byte*)NativeMemory.AllocZeroed(32);
        byte* child = (byte*)NativeMemory.AllocZeroed(BlobSize);
        IntPtr eventCell = IntPtr.Zero;

        var savedVptr = _vptr[(int)RootKind.Door];
        var savedEvent = _eventObject;
        var savedCreate = _createSubMenu;
        try
        {
            bool win = OperatingSystem.IsWindows();
            // Left raw: the address of our own handler in the engine's member-fn shape (see RadialTree.Call).
            IntPtr fake = (IntPtr)(delegate* unmanaged<IntPtr, long, void>)&FakeHandler;
            var root = new CRadialMenu((IntPtr)menu);
            for (int i = 0; i < SlotCount; i++)
            {
                CGuiCallback e = root.Entry[i], c = root.Creator[i];
                e.Obj = e.Ptr;
                c.Obj = c.Ptr;
            }
            // vptr: Itanium points 0x10 into the _ZTV blob, MSVC at the vftable label -- plant the resolved form.
            *(IntPtr*)menu = EngineAbi.Vtbl((IntPtr)fakeVt);
            _vptr[(int)RootKind.Door] = EngineAbi.Vtbl((IntPtr)fakeVt);
            root.MenuX = 640;
            root.MenuY = 360;
            root.TargetId = 0x7f01;

            // slot 0: a direct entry leaf, labelled + iconed, param 0x123456789.
            CGuiCallback e0 = root.Entry[0];
            e0.Obj = (IntPtr)handlerObj;
            e0.Fn = fake;
            root.Param[0] = 0x123456789L;
            root.Text[0] = new CGuiTextBubble((IntPtr)bubble);
            root.Icon[0] = new CGuiIcon((IntPtr)icon);
            "IR_CAST\0"u8.CopyTo(new CGuiIcon((IntPtr)icon).Resref);
            "Cast "u8.CopyTo(new Span<byte>(line0, 32));
            "Spell"u8.CopyTo(new Span<byte>(line1, 32));
            _line0 = (IntPtr)line0; _line1 = (IntPtr)line1; _fakeGob = (IntPtr)gob;
            ((IntPtr*)textVt)[EngineLayout.AurStringGetTextLineCountSlot / sizeof(IntPtr)] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&FakeLineCount;
            ((IntPtr*)textVt)[EngineLayout.AurStringGetTextLineSlot / sizeof(IntPtr)] = (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr>)&FakeLine;
            *(IntPtr*)gob = *(IntPtr*)countGob = (IntPtr)textVt;
            new CGuiTextBubble((IntPtr)bubble).AurString = new CAurString((IntPtr)gob);
            "3"u8.CopyTo(new Span<byte>(countLine, 32));
            _countLine = (IntPtr)countLine; _fakeCountGob = (IntPtr)countGob;
            new CGuiIcon((IntPtr)icon).CountLabel = new CAurString((IntPtr)countGob);
            _createSubMenu = &FakeCreate;
            _fakeChild = (IntPtr)child;

            // slot 1: a creator (submenu), disabled; slot 2: a materialized child with no creator.
            CGuiCallback c1 = root.Creator[1];
            c1.Obj = (IntPtr)handlerObj;
            root.Flags[1] = 1;
            root.SubMenu[2] = new CRadialMenu((IntPtr)fakeVt);

            // slot 3 (Itanium only): a virtual entry -- odd fn = vtable byte offset 0x28 + 1, adj 0x10, so this =
            // obj + 0x10 and the vtable is read from there.
            CGuiCallback e3 = root.Entry[3];
            e3.Obj = (IntPtr)handlerObj;
            if (win)
            {
                e3.Fn = fake;
            }
            else
            {
                e3.Fn = (IntPtr)(0x28 + 1);
                e3.Adj = (IntPtr)ObjAdj;
                ((IntPtr*)handlerObj)[ObjAdj / sizeof(IntPtr)] = (IntPtr)handlerVt;
                ((IntPtr*)handlerVt)[0x28 / sizeof(IntPtr)] = fake;
            }
            root.Param[3] = -7;

            check.Expect(Classify((IntPtr)menu) == RootKind.Door, "a planted vptr classifies as the kind it was planted for");
            *(IntPtr*)menu = (IntPtr)handlerVt;
            check.Expect(Classify((IntPtr)menu) == RootKind.Unknown, "an unknown vptr classifies as Unknown");
            *(IntPtr*)menu = EngineAbi.Vtbl((IntPtr)fakeVt);
            check.Expect(Position((IntPtr)menu) == (640, 360), "Position reads menu XY");
            check.Expect(TargetId((IntPtr)menu, RootKind.Door) == 0x7f01 && TargetId((IntPtr)menu, RootKind.Quickbar) == 0,
                "TargetId reads the subclass word for kinds that have one, 0 otherwise");

            RadialSlot s0 = ReadSlot((IntPtr)menu, 0);
            check.Expect(s0.Kind == SlotKind.Entry && !s0.IsSubMenu, $"slot 0 is an entry ({s0.Kind})");
            check.Expect(s0.Label == "Cast Spell", $"slot 0 label rejoins the wrapped lines (\"{s0.Label}\")");
            check.Expect(s0.Icon == "ir_cast", $"slot 0 icon is the lowercased resref (\"{s0.Icon}\")");
            check.Expect(s0.Count == "3", $"slot 0 count reads the icon's label gob (\"{s0.Count}\")");
            check.Expect(!s0.Disabled && s0.Param == 0x123456789L, "slot 0 enabled, param read");
            check.Expect(s0.Obj == (IntPtr)handlerObj && s0.Fn == fake
                && s0.Adj == IntPtr.Zero && s0.Child == IntPtr.Zero, "slot 0 triple read back");
            RadialSlot s1 = ReadSlot((IntPtr)menu, 1);
            check.Expect(s1.Kind == SlotKind.Creator && s1.IsSubMenu && s1.Disabled && s1.Label == "" && s1.Icon == "",
                $"slot 1 is a disabled creator with no label/icon ({s1.Kind} {s1.Disabled} \"{s1.Label}\")");
            RadialSlot s2 = ReadSlot((IntPtr)menu, 2);
            check.Expect(s2.Kind == SlotKind.Creator && s2.Child == (IntPtr)fakeVt, "slot 2: a materialized child alone makes a submenu");
            RadialSlot s4 = ReadSlot((IntPtr)menu, 4);
            check.Expect(s4.Kind == SlotKind.Empty && !s4.IsSubMenu && s4.Obj == IntPtr.Zero, "slot 4: both sentinels = empty");

            _eventObject = &eventCell;
            eventCell = (IntPtr)0x5150;
            _fakeCalls = 0;
            check.Expect(Invoke((IntPtr)menu, 0) && _fakeCalls == 1 && _fakeSelf == (IntPtr)handlerObj && _fakeParam == 0x123456789L,
                $"Invoke slot 0 calls fn(obj, param) ({_fakeCalls} {_fakeParam:x})");
            check.Expect(eventCell == (IntPtr)0x5150, "Invoke restores s_pCurrentEventObject");
            check.Expect(!Invoke((IntPtr)menu, 4) && _fakeCalls == 1, "Invoke on an unset entry does nothing");
            check.Expect(Invoke((IntPtr)menu, 3) && _fakeCalls == 2 && _fakeParam == -7
                && (long)_fakeSelf - (long)handlerObj == (win ? 0 : ObjAdj),
                win ? "Invoke slot 3 (Win64 pair)" : $"Invoke slot 3 decodes the virtual thunk: this = obj + adj ({(long)_fakeSelf - (long)handlerObj:x})");
            check.Expect(Materialize((IntPtr)menu, 9) == IntPtr.Zero && !Invoke((IntPtr)menu, -1), "out-of-range slots are refused");

            // BuildItems: slots 0 (entry), 1 (disabled creator), 2 (child), 3 (entry) -- empties 4..7 dropped, tags = slot.
            CascadeItem[] items = BuildItems((IntPtr)menu);
            check.Expect(items.Length == 4, $"BuildItems keeps the four filled slots ({items.Length})");
            check.Expect(items.Length == 4 && (int)items[0].Tag! == 0 && (int)items[1].Tag! == 1 && (int)items[2].Tag! == 2 && (int)items[3].Tag! == 3,
                "row tags are the vanilla slot indices, in order");
            check.Expect(items.Length == 4 && items[0].Label == "Cast Spell" && items[0].Icon == "ir_cast" && items[0].Count == "3" && !items[0].HasChildren && !items[0].Disabled,
                "row 0 carries label, icon, count, no mark");
            check.Expect(items.Length == 4 && items[1].HasChildren && items[1].Disabled && items[2].HasChildren && !items[3].HasChildren && items[3].Count == "",
                "creator + child rows carry the mark, the disabled flag follows the slot, no icon = no count");

            // Materialize through the faked CreateSubMenu: slot 1 yields the child with its parent linked; slot 3 declines.
            check.Expect(Materialize((IntPtr)menu, 1) == (IntPtr)child && new CRadialMenu((IntPtr)child).Parent == (IntPtr)menu,
                "Materialize returns m_pSubMenu[i] and links the child's m_pParent");
            check.Expect(Materialize((IntPtr)menu, 3) == IntPtr.Zero, "a declined creator yields null");
        }
        finally
        {
            _vptr[(int)RootKind.Door] = savedVptr;
            _eventObject = savedEvent; _createSubMenu = savedCreate;
            _fakeGob = _fakeCountGob = _fakeChild = IntPtr.Zero; _line0 = _line1 = _countLine = IntPtr.Zero;
            NativeMemory.Free(menu); NativeMemory.Free(fakeVt); NativeMemory.Free(handlerVt); NativeMemory.Free(handlerObj);
            NativeMemory.Free(bubble); NativeMemory.Free(gob); NativeMemory.Free(icon); NativeMemory.Free(textVt); NativeMemory.Free(line0); NativeMemory.Free(line1);
            NativeMemory.Free(countGob); NativeMemory.Free(countLine); NativeMemory.Free(child);
        }
        log.Info(check.Ok ? "RadialTree.SelfCheck: passed" : "RadialTree.SelfCheck: FAILED");
        RadialIcons.SelfCheck(log);
    }
}
