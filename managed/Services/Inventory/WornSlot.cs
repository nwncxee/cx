using System;
using CX.Engine;

namespace CX.Services.Inventory;

/// <summary>Keeps a worn item's own slot field naming the slot it is worn in. The engine's unequip finds the slot to empty
/// through that field, and the server's equip result fills the slot without setting it.</summary>
internal static class WornSlot
{
    /// <summary>Record on <paramref name="item"/> that it is now worn in <paramref name="slot"/> (a slot mask); a null item,
    /// which empties a slot, records nothing.</summary>
    public static void Stamp(IntPtr item, uint slot)
    {
        if (item != IntPtr.Zero) new CNWCItem(item).EquipSlot = slot;
    }
}
