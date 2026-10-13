using System;
using System.Globalization;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// What the model view's doll wears besides the target's body. Hand items and a helmet are throwaway items, registered and (for
/// hands) equipped through the engine's own call; armour and a cloak are written over the appearance record the doll is built from.
/// The real items and the target are only read. A <c>wear</c> value is any number of items, split on spaces, commas or semicolons;
/// the last one for a slot wins.
/// </summary>
internal static unsafe class NuiModelGear
{
    internal const uint RightHand = 0x10, LeftHand = 0x20;   // not-an-offset: INVENTORY_SLOT_RIGHTHAND / LEFTHAND
    private const uint HeadSlot = 0x1, ChestSlot = 0x2, CloakSlot = 0x40;   // not-an-offset: INVENTORY_SLOT_HEAD / CHEST / CLOAK
    private const int Parts = 19, Colors = 6, PartColors = Parts * Colors;
    /// <summary>How long a wear item the client does not know yet waits before it is looked up again, and how long it is looked for in all.</summary>
    private const int RetryMs = 500, GiveUpMs = 10_000;
    /// <summary>The most items one <c>wear</c> value may name; the rest are recorded and left out.</summary>
    internal const int MaxItems = 8;

    /// <summary>One hand of the doll: what it was built from (a source item id, or the preview), the preview text it was built
    /// from, and the throwaway item.</summary>
    internal sealed class Worn
    {
        public readonly uint Slot;
        public ulong From;
        public string Text = "";
        public IntPtr Item;
        public uint ItemId = NuiModelDoll.InvalidId;
        public Worn(uint slot) => Slot = slot;
    }

    /// <summary>The armour and cloak a <c>wear</c> value asks the body to show; each flag says which fields it overwrites.</summary>
    internal sealed class Dressing
    {
        public bool Armor, Cloak;
        public readonly ushort[] ArmorParts = new ushort[Parts];
        public readonly byte[] ArmorColors = new byte[Colors];
        public readonly byte[] ArmorPartColors = new byte[PartColors];
        public ushort CloakModel;
        public readonly ushort[] CloakColors = new ushort[Colors];
    }

    /// <summary>A throwaway item the doll keeps registered: the helmet its record names, or a retired one waiting to be freed.</summary>
    internal struct Kept
    {
        public IntPtr Item;
        public uint Id;
    }

    /// <summary>The hands one doll holds, the body preview it was last told and the helmet it keeps: each view has its own.</summary>
    internal sealed class GearState
    {
        public readonly Worn[] Hands = { new(RightHand), new(LeftHand) };
        public string PreviewText = "";
        public readonly string[] HandText = { "", "" };
        public string BodyKey = "";
        public bool Dirty;
        public bool Waiting;
        /// <summary>The first item of the last <c>wear</c> text the client could not use, and why; <see cref="RefusedNew"/> until the view has recorded it.</summary>
        public string RefusedItem = "", RefusedWhy = "";
        public bool RefusedNew;
        public uint HelmId = NuiModelDoll.InvalidId;
        public long NextTry;
        /// <summary>The first id <see cref="Plan"/> is waiting for the client to learn, and since when (-1 = not yet timed).</summary>
        public string Unknown = "";
        public long WaitSince = -1;
        public int RetryLogs;
        internal Dressing Dress = new();
        internal Kept HelmSrc = new() { Id = NuiModelDoll.InvalidId };
        internal Kept Retired = new() { Id = NuiModelDoll.InvalidId };
        /// <summary>The doll's own helmet, see <see cref="NuiModelHelm"/>.</summary>
        public readonly NuiModelHelm.HelmState Helm = new();
    }

    private sealed class GearLog { public ExtenderLog? Log; }

    private static readonly GearLog _g = new();

    internal static void Init(ExtenderLog log) => _g.Log = log;

    /// <summary>One line in the log, for the helmet path that has no log of its own.</summary>
    internal static void Say(string line) => _g.Log?.Always(line);

    /// <summary>The hand slot a base item's slot mask names: the right hand first, else the left. Zero when it is neither.</summary>
    internal static uint HandOf(uint equipMask) => (equipMask & RightHand) != 0 ? RightHand : (equipMask & LeftHand) != 0 ? LeftHand : 0;

    /// <summary>The body slot a base item's slot mask names, armour before cloak before helmet. Zero when it is none of them.</summary>
    internal static uint BodyOf(uint equipMask) => (equipMask & ChestSlot) != 0 ? ChestSlot : (equipMask & CloakSlot) != 0 ? CloakSlot : (equipMask & HeadSlot) != 0 ? HeadSlot : 0;

    /// <summary>The items a <c>wear</c> value names, in order.</summary>
    internal static string[] Items(string wear) => wear.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The item id a creature holds in a hand slot, or the invalid id.</summary>
    internal static uint Held(IntPtr creature, uint slot)
        => creature == IntPtr.Zero ? NuiModelDoll.InvalidId : *(uint*)(creature + (slot == RightHand ? EngineLayout.CreatureRightHandItem : EngineLayout.CreatureLeftHandItem));

    /// <summary>The item a <c>wear</c> value names: an <c>@look:</c> string mints a fresh item (<paramref name="minted"/> = true,
    /// the caller ends it), a hex object id is an item the client knows (never ended). Zero when it names none, with the reason in
    /// <paramref name="why"/>; an empty reason for an id is one the client does not know yet.</summary>
    internal static IntPtr WearItem(string wear, out bool minted, out string why)
    {
        minted = false;
        why = "";
        if (wear.StartsWith("@look:", StringComparison.Ordinal))
        {
            minted = true;
            return NuiItemIcons.MintLookForModel(wear, out why);
        }
        if (uint.TryParse(wear, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)) return Known(id | ImageNames.ServerIdBit);
        why = "neither an @look: string nor a hex object id";
        return IntPtr.Zero;
    }

    internal static IntPtr Known(uint id) => id == NuiModelDoll.InvalidId ? IntPtr.Zero : CX.Services.Radial.RadialIcons.Item((IntPtr)id);

    /// <summary>Read a <c>wear</c> value: sort its items into the hands, the armour, the cloak and the helmet. Cheap when the text is
    /// the one already read. Run before the doll is built, so the first build already wears it.</summary>
    internal static void Plan(GearState g, string wear, long now)
    {
        bool again = wear == g.PreviewText;
        if (again && (!g.Waiting || now < g.NextTry || GiveUp(g, now))) return;
        g.NextTry = now + RetryMs;
        if (!again) g.WaitSince = -1;
        g.PreviewText = wear;
        g.Waiting = false;
        g.HandText[0] = g.HandText[1] = "";
        g.RefusedItem = g.RefusedWhy = "";
        var dress = new Dressing();
        string armor = "", cloak = "", helm = "";
        string[] items = Items(wear);
        if (items.Length > MaxItems) Refuse(g, items[MaxItems], $"past the {MaxItems} items a wear value may name");
        foreach (string tok in items.AsSpan(0, Math.Min(items.Length, MaxItems)))
        {
            IntPtr it = WearItem(tok, out bool minted, out string why);
            if (it == IntPtr.Zero)
            {
                if (minted || why.Length > 0) Refuse(g, tok, why);
                else { if (!g.Waiting) g.Unknown = tok; g.Waiting = true; }
                continue;
            }
            uint mask = NuiItemIcons.EquipMask(it);
            if (!again || g.RetryLogs++ < 8) _g.Log?.Always($"NuiModelGear: a wear item is base item {new CNWCItem(it).BaseItem}, slots 0x{mask:x}");
            uint hand = HandOf(mask);
            uint body = BodyOf(mask);
            if (hand != 0) g.HandText[hand == RightHand ? 0 : 1] = tok;
            else if (body == ChestSlot)
            {
                armor = tok;
                dress.Armor = true;
                var v = new CNWCItem(it);
                v.Models.CopyTo(dress.ArmorParts);
                v.Colors.CopyTo(dress.ArmorColors);
                if (!NuiItemIcons.ReadLookPartColors(tok, dress.ArmorPartColors)) Array.Fill(dress.ArmorPartColors, (byte)0xff);
            }
            else if (body == CloakSlot)
            {
                cloak = tok;
                dress.Cloak = true;
                var v = new CNWCItem(it);
                dress.CloakModel = v.Models[0];
                for (int i = 0; i < Colors; i++) dress.CloakColors[i] = v.Colors[i];
            }
            else if (body == HeadSlot) helm = tok;
            else
            {
                Refuse(g, tok, "names no item that fits a hand, the body, the head or the cloak slot");
                if (!again) _g.Log?.Always($"NuiModelGear: '{tok}' names no item that fits a hand, the body, the head or the cloak slot (item 0x{it:x}, slots 0x{mask:x}) -- nothing is previewed");
            }
            if (minted) NuiItemIcons.Destroy(it);
        }
        string key = $"A:{armor}|C:{cloak}|H:{helm}";
        g.Dress = dress;
        if (key == g.BodyKey) return;
        g.BodyKey = key;
        g.Dirty = true;
        Retire(g);
        if (helm.Length > 0) SetHelm(g, helm);
    }

    /// <summary>Has <see cref="GearState.Unknown"/> stayed unknown for <see cref="GiveUpMs"/>? Then it is recorded and no longer waited for.</summary>
    internal static bool GiveUp(GearState g, long now)
    {
        if (!g.Waiting) return false;
        if (g.WaitSince < 0) g.WaitSince = now;
        if (now - g.WaitSince < GiveUpMs) return false;
        g.Waiting = false;
        Refuse(g, g.Unknown, $"names no item this client knows ({GiveUpMs / 1000} s waited)");
        return true;
    }

    private static void Refuse(GearState g, string item, string why)
    {
        if (g.RefusedWhy.Length > 0) return;
        g.RefusedItem = item;
        g.RefusedWhy = why.Length > 0 ? why : "the client cannot build that item";
        g.RefusedNew = true;
    }

    /// <summary>The item <see cref="Plan"/> could not use, once, for the view to record; false when there is none to say.</summary>
    internal static bool TakeRefusal(GearState g, out string item, out string why)
    {
        item = g.RefusedItem;
        why = g.RefusedWhy;
        bool fresh = g.RefusedNew;
        g.RefusedNew = false;
        return fresh;
    }

    /// <summary>The previous helmet moves to the retired place, to be freed once the doll no longer names it.</summary>
    private static void Retire(GearState g)
    {
        if (g.Retired.Item != IntPtr.Zero) Unregister(g.Retired, false);
        g.Retired = g.HelmSrc;
        g.HelmSrc = new Kept { Id = NuiModelDoll.InvalidId };
        g.HelmId = NuiModelDoll.InvalidId;
    }

    /// <summary>The helmet the record will name: a known item's own id, or the id of a registered source minted from an <c>@look:</c>
    /// string. The doll's helmet path copies whichever it is.</summary>
    private static void SetHelm(GearState g, string text)
    {
        if (!text.StartsWith("@look:", StringComparison.Ordinal))
        {
            if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)) g.HelmId = id | ImageNames.ServerIdBit;
            return;
        }
        g.HelmSrc = MintKept(text);
        g.HelmId = g.HelmSrc.Id;
    }

    private static Kept MintKept(string text)
    {
        IntPtr model = WearItem(text, out bool minted, out _);
        IntPtr copy = NuiItemIcons.MintCopy(model);
        if (minted && model != IntPtr.Zero) NuiItemIcons.Destroy(model);
        var k = new Kept { Id = NuiModelDoll.InvalidId };
        if (copy == IntPtr.Zero) { _g.Log?.Always($"NuiModelGear: no helmet to copy for '{text}'"); return k; }
        uint id = Register(copy);
        if (id == NuiModelDoll.InvalidId) return k;
        k.Item = copy;
        k.Id = id;
        _g.Log?.Always($"NuiModelGear: the head wears a copy of '{text}' (item id 0x{id:x})");
        return k;
    }

    /// <summary>Put a throwaway item in the game-object array. The invalid id, with the item ended, when the engine refuses.</summary>
    internal static uint Register(IntPtr copy)
    {
        IntPtr array = EngineFn.GetGameObjectArray(NuiModelDoll.App());
        uint id = NuiModelDoll.InvalidId;
        if (array == IntPtr.Zero || EngineFn.GameObjectArrayAdd(array, &id, copy + NuiModelDoll.ItemObject, 0) != 0)
        {
            _g.Log?.Always("NuiModelGear: the engine would not register the doll's item");
            NuiItemIcons.Destroy(copy);
            return NuiModelDoll.InvalidId;
        }
        new CNWCObject(copy + NuiModelDoll.ItemObject).Id = id;
        return id;
    }

    /// <summary>Take an item out of the array and end it. Only an item the array still holds: once the module has unloaded, the
    /// engine has freed it already.</summary>
    private static void Unregister(Kept k, bool taken)
    {
        if (taken) return;
        Unregister(k.Item, k.Id);
    }

    /// <summary>The same for an item and its id given apart.</summary>
    internal static void Unregister(IntPtr item, uint id)
    {
        if (item == IntPtr.Zero || !NuiModelDoll.Holds(id, item + NuiModelDoll.ItemObject)) return;
        IntPtr gone;
        EngineFn.GameObjectArrayDelete(EngineFn.GetGameObjectArray(NuiModelDoll.App()), id, &gone);
        NuiItemIcons.Destroy(item);
    }

    /// <summary>Write the body preview over a copy of the appearance record the doll is built from. Armour goes in as the body's parts
    /// with the worn flag clear, which is the set the engine builds from then; then its colours, the cloak and the helmet's id. A
    /// record with nothing previewed is left as it is.</summary>
    internal static void Dress(GearState g, byte* info)
    {
        var a = new CCAppearanceInfo((IntPtr)info);
        Dressing d = g.Dress;
        if (d.Armor)
        {
            d.ArmorParts.CopyTo(a.BodyParts);
            d.ArmorColors.CopyTo(a.Colors);
            d.ArmorPartColors.CopyTo(a.PartColors);
            a.ArmorWorn = 0;
        }
        if (d.Cloak)
        {
            a.CloakModel = d.CloakModel;
            d.CloakColors.CopyTo(a.CloakColors);
        }
        if (g.HelmId != NuiModelDoll.InvalidId) a.HelmId = g.HelmId;
    }

    /// <summary>Did the body preview change since the doll was last dressed? Reading it clears the flag.</summary>
    internal static bool TakeDirty(GearState g)
    {
        bool d = g.Dirty;
        g.Dirty = false;
        return d;
    }

    /// <summary>The doll has been dressed again and no longer names the retired helmet: free it.</summary>
    internal static void Settled(GearState g)
    {
        if (g.Retired.Item == IntPtr.Zero) return;
        Unregister(g.Retired, false);
        g.Retired = new Kept { Id = NuiModelDoll.InvalidId };
    }

    /// <summary>Bring the doll's hands to what the target holds, with the previewed hand items in their slots. Cheap when nothing
    /// changed: two id reads and a string compare each; an <c>@look:</c> preview is minted once per value, not per frame.</summary>
    internal static void Sync(GearState g, IntPtr doll, IntPtr target)
    {
        foreach (Worn w in g.Hands)
        {
            string text = g.HandText[w.Slot == RightHand ? 0 : 1];
            ulong from;
            if (text.Length > 0) from = FromPreview;
            else
            {
                uint id = Held(target, w.Slot);
                from = id == NuiModelDoll.InvalidId ? 0 : FromItem | id;
            }
            if (from == w.From && (from != FromPreview || text == w.Text)) continue;
            Remove(doll, w);
            w.From = from;
            w.Text = text;
            if (from != 0) Put(doll, w, target, text);
        }
    }

    /// <summary>What a doll hand was built from: nothing (0), the preview, or a held item (this bit plus its id).</summary>
    private const ulong FromPreview = 1ul << 33, FromItem = 1ul << 32;

    private static void Put(IntPtr doll, Worn w, IntPtr target, string wear)
    {
        IntPtr model;
        bool minted = false;
        if (w.From == FromPreview) { model = WearItem(wear, out minted, out _); }
        else model = Known(Held(target, w.Slot));
        IntPtr copy = NuiItemIcons.MintCopy(model);
        if (minted && model != IntPtr.Zero) NuiItemIcons.Destroy(model);
        if (copy == IntPtr.Zero) { _g.Log?.Always($"NuiModelGear: no item to copy for slot 0x{w.Slot:x} (from 0x{w.From:x})"); return; }
        uint id = Register(copy);
        if (id == NuiModelDoll.InvalidId) return;
        w.Item = copy;
        w.ItemId = id;
        EngineFn.NwcCreatureEquip(doll, w.Slot, copy);
        _g.Log?.Always($"NuiModelGear: slot 0x{w.Slot:x} wears a copy of source 0x{w.From:x} (item id 0x{id:x})");
    }


    private static void Remove(IntPtr doll, Worn w, bool taken = false)
    {
        if (w.Item == IntPtr.Zero) return;
        if (taken || !NuiModelDoll.Holds(w.ItemId, w.Item + NuiModelDoll.ItemObject))
        {
            w.Item = IntPtr.Zero;
            w.ItemId = NuiModelDoll.InvalidId;
            return;
        }
        _g.Log?.Always($"NuiModelGear: slot 0x{w.Slot:x} emptied, item id 0x{w.ItemId:x} freed");
        EngineFn.NwcCreatureEquip(doll, w.Slot, IntPtr.Zero);
        Unregister(w.Item, w.ItemId);
        w.Item = IntPtr.Zero;
        w.ItemId = NuiModelDoll.InvalidId;
    }

    /// <summary>The doll is going: empty both hands, take the helmet off, then end every throwaway item, before the creature is
    /// deleted. When the engine already freed the registered objects (<paramref name="taken"/>), only the bookkeeping is dropped.</summary>
    internal static void Release(GearState g, IntPtr doll, bool taken)
    {
        NuiModelHelm.Release(g.Helm, doll, taken);
        foreach (Worn w in g.Hands)
        {
            Remove(doll, w, taken);
            w.From = 0;
        }
        Unregister(g.HelmSrc, taken);
        Unregister(g.Retired, taken);
        g.HelmSrc = g.Retired = new Kept { Id = NuiModelDoll.InvalidId };
        g.HelmId = NuiModelDoll.InvalidId;
        g.PreviewText = g.BodyKey = "";
    }

    /// <summary>Oracle: hand-written <c>INVENTORY_SLOT_*</c> bits (right hand 0x10, left hand 0x20, helmet 0x1, armour 0x2, cloak 0x40),
    /// strings no item can come from, and the byte offsets of the appearance record read off the engine's body, head and cloak
    /// builders (colours at 0x4c, the helmet id at 0xc4, the armour flag at 0xc8, the cloak model at 0xf8). Nothing here reaches the engine.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(HandOf(0x10) == 0x10 && HandOf(0x20) == 0x20, "the right and the left hand bits name their own hand");
        check.Expect(HandOf(0x30) == 0x10, "a base item that fits either hand goes in the right");
        check.Expect(HandOf(0x40) == 0 && HandOf(0x01) == 0 && HandOf(0) == 0, "a cloak, a helmet and nothing are no hand item");
        check.Expect(BodyOf(0x02) == 0x02 && BodyOf(0x40) == 0x40 && BodyOf(0x01) == 0x01, "armour, a cloak and a helmet are each their own body slot");
        check.Expect(BodyOf(0x10) == 0 && BodyOf(0x20) == 0 && BodyOf(0) == 0, "a hand item and nothing are no body slot");
        string[] items = Items("12ab, @look:00 ;;  7f");
        check.Expect(items.Length == 3 && items[0] == "12ab" && items[1] == "@look:00" && items[2] == "7f", "spaces, commas and semicolons separate wear items");
        check.Expect(Items("").Length == 0 && Items(" , ;").Length == 0, "an empty wear value names no item");
        IntPtr none = WearItem("not an item", out bool minted, out _);
        check.Expect(none == IntPtr.Zero && !minted, "text that is neither an @look: string nor hex names no item");
        none = WearItem("@look:12", out minted, out string whyNot);
        check.Expect(none == IntPtr.Zero && minted && whyNot == "not an @look: string of hex fields", "a malformed @look: string mints nothing, says it tried to, and says why");
        var refused = new GearState();
        Refuse(refused, "@look:12", whyNot);
        Refuse(refused, "second", "later");
        check.Expect(TakeRefusal(refused, out string refItem, out string refWhy) && refItem == "@look:12" && refWhy == whyNot && !TakeRefusal(refused, out _, out _),
                     "the first item a wear text cannot use is handed over once");
        var plain = new GearState();
        Plan(plain, "zz", 1000);
        check.Expect(TakeRefusal(plain, out refItem, out refWhy) && refItem == "zz" && refWhy == "neither an @look: string nor a hex object id" && !plain.Waiting,
                     "a wear item that is not hex is recorded at once and not waited for");
        var waits = new GearState { Waiting = true, Unknown = "7f" };
        bool gaveUp = false;
        for (long t = 0; t < 20_000 && !gaveUp; t += 500) gaveUp = GiveUp(waits, t);
        check.Expect(gaveUp && !waits.Waiting && TakeRefusal(waits, out refItem, out refWhy) && refItem == "7f" && refWhy.StartsWith("names no item"),
                     "an id the client never learns is recorded and no longer waited for");
        var soon = new GearState { Waiting = true, Unknown = "7f" };
        check.Expect(!GiveUp(soon, 0) && !GiveUp(soon, 9_999) && soon.Waiting, "an id is waited for ten seconds first");
        var many = new GearState();
        Plan(many, string.Join(',', System.Linq.Enumerable.Repeat("zz", 40)), 0);
        check.Expect(TakeRefusal(many, out _, out refWhy) && refWhy == "past the 8 items a wear value may name", "a wear value past eight items is recorded");

        byte* rec = stackalloc byte[256];
        new Span<byte>(rec, 256).Clear();
        *(int*)(rec + 0xc8) = 1;
        *(ushort*)(rec + 0) = 9;
        var g = new GearState();
        g.Dress.Armor = true;
        g.Dress.Cloak = true;
        for (int i = 0; i < Parts; i++) g.Dress.ArmorParts[i] = (ushort)(100 + i);
        for (int i = 0; i < Colors; i++) { g.Dress.ArmorColors[i] = (byte)(10 + i); g.Dress.CloakColors[i] = (ushort)(40 + i); }
        Array.Fill(g.Dress.ArmorPartColors, (byte)0xff);
        g.Dress.ArmorPartColors[113] = 7;
        g.Dress.CloakModel = 0x2b;
        Dress(g, rec);
        check.Expect(*(ushort*)(rec + 0x26) == 100 && *(ushort*)(rec + 0x26 + 36) == 118 && *(ushort*)(rec + 0) == 9, "armour parts fill the 19 words at 0x26 and leave the set at 0 alone");
        check.Expect(rec[0x4c] == 10 && rec[0x51] == 15, "the six armour colours sit at 0x4c");
        check.Expect(rec[0x52] == 0xff && rec[0xc3] == 7, "the 114 part colours end where the helmet id begins");
        check.Expect(*(int*)(rec + 0xc8) == 0, "armour clears the worn flag, so the body is built from the set at 0x26");
        check.Expect(*(ushort*)(rec + 0xec) == 40 && *(ushort*)(rec + 0xf6) == 45 && *(ushort*)(rec + 0xf8) == 0x2b, "the cloak colours are six words before the cloak model");
        check.Expect(*(uint*)(rec + 0xc4) == 0, "no helmet previewed leaves the helmet id as it was");
        new Span<byte>(rec, 256).Clear();
        *(int*)(rec + 0xc8) = 1;
        g.Dress = new Dressing();
        Dress(g, rec);
        bool clear = *(int*)(rec + 0xc8) == 1;
        for (int i = 0; i < 256; i++) clear &= i is >= 0xc8 and < 0xcc || rec[i] == 0;
        check.Expect(clear, "a record with nothing previewed is left untouched");
    }
}
