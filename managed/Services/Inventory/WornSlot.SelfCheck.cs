using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>Proof that a worn item names its slot. Oracle: a hand-built item at the layout row's offset, every byte
/// set to 0xff (none) by the fixture, and slot masks written as literals (0x40 the cloak, 0x2000 the bolts slot).</summary>
internal static unsafe class WornSlotCheck
{
    /// <summary>Bytes past the slot field the fixture watches for a stray write.</summary>
    private const int Tail = 8;

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "WornSlot.SelfCheck");
        int at = EngineLayout.ItemEquipSlot, size = at + sizeof(uint) + Tail;
        byte[] bytes = new byte[size];
        Array.Fill(bytes, (byte)0xff);
        fixed (byte* obj = bytes) Run(check, obj, at, size);
        log.Info(check.Ok ? "WornSlot.SelfCheck: passed" : "WornSlot.SelfCheck: FAILED");
    }

    private static void Run(Checker check, byte* obj, int at, int size)
    {
        var item = new CNWCItem((IntPtr)obj);

        WornSlot.Stamp((IntPtr)obj, 0x40);
        check.Expect(item.EquipSlot == 0x40, $"an item worn in the cloak slot names it (0x{item.EquipSlot:x})");

        WornSlot.Stamp((IntPtr)obj, 0x2000);
        check.Expect(item.EquipSlot == 0x2000, $"an item moved to another slot names the new one (0x{item.EquipSlot:x})");

        bool tailKept = true;
        for (int i = at + sizeof(uint); i < size; i++) tailKept &= obj[i] == 0xff;
        check.Expect(tailKept, "...and nothing past the slot field is written");

        WornSlot.Stamp(IntPtr.Zero, 0x40);
        check.Expect(item.EquipSlot == 0x2000, "emptying a slot (no item) records nothing");
    }
}
