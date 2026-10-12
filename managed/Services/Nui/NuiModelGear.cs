using System;
using System.Globalization;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// What the model view's doll holds in its hands. A hand item is not part of the appearance record: the engine wields it
/// from a registered item object. So each hand slot of the doll gets a throwaway item built from the real one's look,
/// registered like any object and equipped through the engine's own equip call; the real items are only read.
/// A <c>wear</c> preview takes the same route with an item the target does not hold.
/// </summary>
internal static unsafe class NuiModelGear
{
    internal const uint RightHand = 0x10, LeftHand = 0x20;   // not-an-offset: INVENTORY_SLOT_RIGHTHAND / LEFTHAND
    private const uint InvalidId = 0x7f000000;   // not-an-offset: INVALID_OBJECT_ID

    /// <summary>One hand of the doll: what it was built from (a source item id, or the preview text) and the throwaway item.</summary>
    private sealed class Worn
    {
        public readonly uint Slot;
        public ulong From;
        public IntPtr Item;
        public uint ItemId = InvalidId;
        public Worn(uint slot) => Slot = slot;
    }

    private sealed class GearState
    {
        public ExtenderLog? Log;
        public readonly Worn[] Hands = { new(RightHand), new(LeftHand) };
        public string PreviewText = "";
        public uint PreviewSlot;
    }

    private static readonly GearState _g = new();

    internal static void Init(ExtenderLog log) => _g.Log = log;

    /// <summary>The hand slot a base item's slot mask names: the right hand first, else the left. Zero when it is neither.</summary>
    internal static uint HandOf(uint equipMask) => (equipMask & RightHand) != 0 ? RightHand : (equipMask & LeftHand) != 0 ? LeftHand : 0;

    /// <summary>The item id a creature holds in a hand slot, or the invalid id.</summary>
    internal static uint Held(IntPtr creature, uint slot)
        => creature == IntPtr.Zero ? InvalidId : *(uint*)(creature + (slot == RightHand ? EngineLayout.CreatureRightHandItem : EngineLayout.CreatureLeftHandItem));

    /// <summary>The item a <c>wear</c> value names: an <c>@look:</c> string mints a fresh item (<paramref name="minted"/> = true,
    /// the caller ends it), a hex object id is an item the client knows (never ended). Zero when it names none.</summary>
    internal static IntPtr WearItem(string wear, out bool minted)
    {
        minted = false;
        if (wear.StartsWith("@look:", StringComparison.Ordinal))
        {
            minted = true;
            return NuiItemIcons.MintLook(wear);
        }
        return uint.TryParse(wear, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)
            ? Known(id | NuiItemIcons.ServerIdBit) : IntPtr.Zero;
    }

    private static IntPtr Known(uint id) => id == InvalidId ? IntPtr.Zero : CX.Services.Radial.RadialIcons.Item((IntPtr)id);

    /// <summary>Bring the doll's hands to what the target holds, with the preview in its slot. Cheap when nothing changed:
    /// two id reads and a string compare each; an <c>@look:</c> preview is minted once per value, not per frame.</summary>
    internal static void Sync(IntPtr doll, IntPtr target, string wear)
    {
        if (wear != _g.PreviewText)
        {
            _g.PreviewText = wear;
            _g.PreviewSlot = 0;
            if (wear.Length > 0)
            {
                IntPtr it = WearItem(wear, out bool minted);
                _g.PreviewSlot = HandOf(NuiItemIcons.EquipMask(it));
                if (minted && it != IntPtr.Zero) NuiItemIcons.Destroy(it);
                if (_g.PreviewSlot == 0) _g.Log?.Always($"NuiModelGear: '{wear}' names no item that sits in a hand -- nothing is previewed");
            }
        }
        foreach (Worn w in _g.Hands)
        {
            ulong from;
            if (w.Slot == _g.PreviewSlot) from = FromPreview;
            else
            {
                uint id = Held(target, w.Slot);
                from = id == InvalidId ? 0 : FromItem | id;
            }
            if (from == w.From) continue;
            Remove(doll, w);
            w.From = from;
            if (from != 0) Put(doll, w, target, wear);
        }
    }

    /// <summary>What a doll hand was built from: nothing (0), the preview, or a held item (this bit plus its id).</summary>
    private const ulong FromPreview = 1ul << 33, FromItem = 1ul << 32;

    private static void Put(IntPtr doll, Worn w, IntPtr target, string wear)
    {
        IntPtr model;
        bool minted = false;
        if (w.From == FromPreview) { model = WearItem(wear, out minted); }
        else model = Known(Held(target, w.Slot));
        IntPtr copy = NuiItemIcons.MintCopy(model);
        if (minted && model != IntPtr.Zero) NuiItemIcons.Destroy(model);
        if (copy == IntPtr.Zero) { _g.Log?.Always($"NuiModelGear: no item to copy for slot 0x{w.Slot:x} (from 0x{w.From:x})"); return; }
        IntPtr array = EngineFn.GetGameObjectArray(NuiModelDoll.App());
        uint id = InvalidId;
        if (array == IntPtr.Zero || EngineFn.GameObjectArrayAdd(array, &id, copy + ItemObject, 0) != 0)
        {
            _g.Log?.Always("NuiModelGear: the engine would not register the doll's item");
            NuiItemIcons.Destroy(copy);
            return;
        }
        new CNWCObject(copy + ItemObject).Id = id;
        w.Item = copy;
        w.ItemId = id;
        EngineFn.NwcCreatureEquip(doll, w.Slot, copy);
        _g.Log?.Always($"NuiModelGear: slot 0x{w.Slot:x} wears a copy of source 0x{w.From:x} (item id 0x{id:x})");
    }

    /// <summary>The CNWCObject sub-object inside a CNWCItem, the part the game-object array registers.</summary>
    private const int ItemObject = 0x48;   // not-an-offset: CNWCItem's CNWCObject base, from the engine's AddExternalObject(item + 0x48)

    private static void Remove(IntPtr doll, Worn w, bool taken = false)
    {
        if (w.Item == IntPtr.Zero) return;
        if (taken || !NuiModelDoll.Holds(w.ItemId, w.Item + ItemObject))
        {
            w.Item = IntPtr.Zero;
            w.ItemId = InvalidId;
            return;
        }
        _g.Log?.Always($"NuiModelGear: slot 0x{w.Slot:x} emptied, item id 0x{w.ItemId:x} freed");
        EngineFn.NwcCreatureEquip(doll, w.Slot, IntPtr.Zero);
        IntPtr app = NuiModelDoll.App();
        if (app != IntPtr.Zero)
        {
            IntPtr gone;
            EngineFn.GameObjectArrayDelete(EngineFn.GetGameObjectArray(app), w.ItemId, &gone);
        }
        NuiItemIcons.Destroy(w.Item);
        w.Item = IntPtr.Zero;
        w.ItemId = InvalidId;
    }

    /// <summary>The doll is going: empty both hands, then end every throwaway item, before the creature is deleted. When the
    /// engine already freed the registered objects (<paramref name="taken"/>), only the bookkeeping is dropped.</summary>
    internal static void Release(IntPtr doll, bool taken)
    {
        foreach (Worn w in _g.Hands)
        {
            Remove(doll, w, taken);
            w.From = 0;
        }
    }

    /// <summary>How many throwaway items the doll holds now.</summary>
    internal static int Count()
    {
        int n = 0;
        foreach (Worn w in _g.Hands) if (w.Item != IntPtr.Zero) n++;
        return n;
    }

    /// <summary>Oracle: hand-written <c>INVENTORY_SLOT_*</c> bits (right hand 0x10, left hand 0x20, cloak 0x40) and strings no item
    /// can come from. Nothing here reaches the engine.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(HandOf(0x10) == 0x10 && HandOf(0x20) == 0x20, "the right and the left hand bits name their own hand");
        check.Expect(HandOf(0x30) == 0x10, "a base item that fits either hand goes in the right");
        check.Expect(HandOf(0x40) == 0 && HandOf(0x01) == 0 && HandOf(0) == 0, "a cloak, a helmet and nothing are no hand item");
        IntPtr none = WearItem("not an item", out bool minted);
        check.Expect(none == IntPtr.Zero && !minted, "text that is neither an @look: string nor hex names no item");
        none = WearItem("@look:12", out minted);
        check.Expect(none == IntPtr.Zero && minted, "a malformed @look: string mints nothing, and says it tried to");
    }
}
