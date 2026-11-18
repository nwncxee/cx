using System;
using System.Buffers.Binary;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>Proof that a worn item names its slot. Oracle: a hand-built item at the layout row's offset, every byte
/// set to 0xff (none) by the fixture, and slot masks written as literals (0x40 the cloak, 0x2000 the bolts slot).</summary>
internal static unsafe class WornSlotCheck
{
    /// <summary>Bytes past the slot field the fixture also watches for a stray write.</summary>
    private const int Tail = 8;

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int at = EngineLayout.ItemEquipSlot, size = at + sizeof(uint) + Tail;
        byte[] bytes = new byte[size];
        Array.Fill(bytes, (byte)0xff);
        fixed (byte* obj = bytes) Run(check, bytes, obj, at);
        log.Info(check.Ok ? "WornSlot.SelfCheck: passed" : "WornSlot.SelfCheck: FAILED");
    }

    private static void Run(Checker check, byte[] bytes, byte* obj, int at)
    {
        WornSlot.Stamp((IntPtr)obj, 0x40);
        check.Expect(Field(bytes, at) == 0x40, $"an item worn in the cloak slot names it (0x{Field(bytes, at):x})");

        WornSlot.Stamp((IntPtr)obj, 0x2000);
        check.Expect(Field(bytes, at) == 0x2000, $"an item moved to another slot names the new one (0x{Field(bytes, at):x})");

        bool restKept = true;
        for (int i = 0; i < bytes.Length; i++)
            if (i < at || i >= at + sizeof(uint)) restKept &= bytes[i] == 0xff;
        check.Expect(restKept, "...and nothing outside the slot field is written");

        WornSlot.Stamp(IntPtr.Zero, 0x40);
        check.Expect(Field(bytes, at) == 0x2000, "emptying a slot (no item) records nothing");
    }

    /// <summary>The slot field's four bytes as the engine stores them, read without the view under test.</summary>
    private static uint Field(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, sizeof(uint)));
}
