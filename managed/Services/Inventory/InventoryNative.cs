using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Radial;

namespace CX.Services.Inventory;

/// <summary>What a world pick hit: nothing, the ground, or a game object.</summary>
internal enum WorldKind { None, Ground, Object }

/// <summary>A world pick: the kind, the object's id and <c>GetGameobjectHit</c> type (0 for ground), and the hit point.</summary>
internal readonly record struct WorldHit(WorldKind Kind, uint Oid, int Type, System.Numerics.Vector3 Point);

/// <summary>
/// The inventory window's engine side: the <c>SetInventoryVisible</c> funnel, the key, toggle, level-up and possess hooks, the backpack set learned from
/// <c>CGuiMultiPageMultiInvbox::AddItem</c>, the per-frame copy of items / equip slots / gold / weight into the
/// <see cref="InventoryModel"/>, the vanilla panel's own labels (filled by <c>SetCharacterData</c>, side-effect free on
/// the player's own panel), and the stock senders. Nothing is held across a frame: every object is re-resolved by id.
/// </summary>
internal static unsafe class InventoryNative
{
    private static ExtenderLog? _log;
    /// <summary>The engine functions the hooks replace, installed once at attach and never reset, and whether the key or a
    /// level-up is being handled, which tells their <c>ToggleDisplay</c> calls from a store's show.</summary>
    private sealed class HookState
    {
        public InvSetInventoryVisibleFn VisibleOrig;
        public InvToggleDisplayFn ToggleOrig;
        public InvHandleInvButtonFn KeyOrig;
        public InvCloseForLevelupFn LevelupOrig;
        public SendPossessFamiliarFn PossessOrig;
        public InvboxAddItemFn* AddSlot;
        public InvContainerPagenumFn PagenumOrig;
        public NwcCreatureEquipFn* EquipSlot;
        public bool KeyDown, LevelingUp;
    }

    private static readonly HookState _hooks = new();
    private static bool _ready, _seenFull, _modelFull;
    /// <summary>Backpack ids remembered at most: a hostile or broken stream cannot grow the set without bound.</summary>
    private const int MaxSeen = InventoryModel.MaxItems * 4;

    /// <summary>Every item id the engine's repository handler was handed this session, all pages, with the
    /// <see cref="BagSeq"/> of its last add into the backpack grid (0 for another box's); pruned each frame to what the
    /// player still carries unequipped, and per page by <see cref="PruneBagPage"/>.</summary>
    private static readonly Dictionary<uint, int> _seen = new();
    private static readonly uint[] _drop = new uint[InventoryModel.MaxItems];
    /// <summary><see cref="Fill"/>'s per-frame slot box ids and the ids the paper doll shows, in <see cref="InventoryModel.Slots"/> order.</summary>
    private static readonly uint[] _boxIds = new uint[InventoryModel.Slots.Length], _wornIds = new uint[InventoryModel.Slots.Length];
    /// <summary>What <see cref="Fill"/> last saw of gold / weight / worn items: a change re-runs the stock label fill.</summary>
    private static long _statSig = long.MinValue;
    /// <summary>Order-independent digest of the backpack (ids, cells, stacks): the set's enumeration order moves on every
    /// add, so a slot-by-slot compare would bump <see cref="InventoryModel.Epoch"/> on every frame.</summary>
    private static ulong _bagSig;
    private static bool _labelsStale = true;
    private const byte TypeCreature = 5, TypeItem = 6;   // not-an-offset: CGameObject type enum
    /// <summary><c>CNWCItem::ActivateRadialMenu</c> flags the stock widgets pass (§10): possessed, and possessed + equipped;
    /// a container's cell passes possessed for a bag, none (the ground menu) for a placeable.</summary>
    internal const int RadialGround = 0, RadialPossessed = 1, RadialEquipped = 3;
    /// <summary>A label is at most this many wrapped lines.</summary>
    private const int MaxLabelLines = 4;

    private static readonly string[] Needed =
    {
        SymbolMap.InvSetInventoryVisible, SymbolMap.InvToggleDisplay, SymbolMap.InvHandleInvButton, SymbolMap.InvCloseForLevelup,
        SymbolMap.SendPossessFamiliar, SymbolMap.RulesetIntEntry, SymbolMap.InvSetCharacterData, SymbolMap.GetInventoryPanel, SymbolMap.InvboxAddItem,
        SymbolMap.InvGetEquipSlot, SymbolMap.PlayGuiSound, SymbolMap.InvboxHandleDoubleClick,
        SymbolMap.GetGameObject, SymbolMap.AppManager, SymbolMap.Rules, SymbolMap.GetBaseItem,
        SymbolMap.SendInvEquip, SymbolMap.SendInvUnequip, SymbolMap.SendInvRepositoryMove, SymbolMap.SendInvDrop,
        SymbolMap.SendGuiInvStatus, SymbolMap.SendGuiInvSelectPanel, SymbolMap.SendInputUseObject, SymbolMap.SendInputMergeItem,
        SymbolMap.SendInputSplitItem, SymbolMap.ItemGetName, SymbolMap.ExoStringDtor, SymbolMap.ItemActivateRadialMenu,
        SymbolMap.GuiMan, SymbolMap.GuiObjectHitCheck, SymbolMap.GameCameraHitCheck, SymbolMap.GetGameobjectHit, SymbolMap.HitInfoReset,
        SymbolMap.SendBarterStart, SymbolMap.SendInputUseItem, SymbolMap.SendGoldTransfer,
        SymbolMap.QuickButtonSetItem, SymbolMap.ItemPlayDropSound,
        SymbolMap.InvSetContainerGuiVisible, SymbolMap.InvContainerPageUp, SymbolMap.InvContainerPageDown, SymbolMap.InvContainerPagenum,
        SymbolMap.NwcCreatureEquip,
    };

    /// <summary>Resolve, then install the funnel hook, the key and toggle hooks, the level-up and possess hooks, the repository and slot-fill chains and the container page-count hook. False =
    /// every piece stands down together: a half-live inventory is not a state this ships in.</summary>
    public static bool Attach(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "InventoryService", Needed)) return false;
        if (!EngineLayout.Require(log, "InventoryService", "CPanelInventory", "CPanelInvEquip", "CGuiLabel", "CNWCItem",
                                  "CNWCCreature", "CNWBaseItem", "CNWRules", "CGameObject", "CNWCObject", "CGuiMan", "CHitInfo", "CPanelQuickBar", "CGuiInGame",
                                  "CGuiContainer", "CGuiPanel", "CGuiInvbox")) return false;
        _hooks.VisibleOrig = hooks.InstallInvSetInventoryVisible(&OnSetInventoryVisible);
        _hooks.ToggleOrig = hooks.InstallInvToggleDisplay(&OnToggleDisplay);
        _hooks.KeyOrig = hooks.InstallInvHandleInvButton(&OnInvButton);
        _hooks.LevelupOrig = hooks.InstallInvCloseForLevelup(&OnCloseForLevelup);
        _hooks.PossessOrig = hooks.InstallSendPossessFamiliar(&OnPossessFamiliar);
        _hooks.AddSlot = hooks.ChainInvboxAddItem(&OnAddItem, 50);
        _hooks.PagenumOrig = hooks.InstallInvContainerPagenum(&OnContainerPagenum);
        _hooks.EquipSlot = hooks.ChainNwcCreatureEquip(&OnEquipToSlot, 50);
        _ready = _hooks.VisibleOrig != null && _hooks.ToggleOrig != null && _hooks.KeyOrig != null && _hooks.LevelupOrig != null
              && _hooks.PossessOrig != null && _hooks.AddSlot != null && _hooks.PagenumOrig != null && _hooks.EquipSlot != null;
        if (!_ready) log.Info("InventoryService: a hook did not install");
        return _ready;
    }

    // ---- hooks ----

    /// <summary>Every slot fill, the server's equip result among them: with the CX inventory on, the item learns its slot
    /// before the engine wears it. Off, the engine runs as stock.</summary>
    [UnmanagedCallersOnly]
    private static void OnEquipToSlot(IntPtr creature, uint slot, IntPtr item)
    {
        if (InventoryKnobs.Enabled.V) WornSlot.Stamp(item, slot);
        (*_hooks.EquipSlot)(creature, slot, item);
    }

    [UnmanagedCallersOnly]
    private static void OnSetInventoryVisible(IntPtr panel, int show, int notify, uint oid)
    {
        if (InventoryService.Ours(panel, show, oid))
        {
            InventoryService.OnSetVisible(show != 0, notify != 0);
            return;
        }
        if (show != 0)
        {
            _log?.Info($"[inv] stock panel shown (0x{oid:x})");
            InventoryService.Yield();
        }
        _hooks.VisibleOrig(panel, show, notify, oid);
    }

    /// <summary>The inventory key: its <c>ToggleDisplay</c> toggles the CX window, where every other caller's shows it.</summary>
    [UnmanagedCallersOnly]
    private static void OnInvButton(IntPtr gui)
    {
        _hooks.KeyDown = true;
        _hooks.KeyOrig(gui);
        _hooks.KeyDown = false;
    }

    /// <summary>A show of the player's own hidden panel reaches the CX window, as the Linux body's show call does, since
    /// Windows inlines that call: the key toggles it, store setup opens it, a level-up (which closed it) leaves it shut.
    /// A shown panel, or a possessed familiar the ruleset refuses, keeps the stock toggle.</summary>
    [UnmanagedCallersOnly]
    private static void OnToggleDisplay(IntPtr panel)
    {
        if (InventoryService.Ours(panel, 1, InventoryModel.Invalid) && !new CGuiPanel(panel).Shown && !FamiliarRefused())
        {
            if (_hooks.LevelingUp) return;
            _log?.Info(_hooks.KeyDown ? "[inv] key toggles the CX window" : "[inv] stock show call reaches the CX window");
            InventoryService.OnSetVisible(show: true, notify: _hooks.KeyDown);
            return;
        }
        _hooks.ToggleOrig(panel);
    }

    /// <summary>A level-up shuts the CX window as it shuts a shown stock panel, which the CX window never registers as.</summary>
    [UnmanagedCallersOnly]
    private static void OnCloseForLevelup(IntPtr gui)
    {
        CloseFor("[inv] level-up begins");
        _hooks.LevelingUp = true;
        _hooks.LevelupOrig(gui);
        _hooks.LevelingUp = false;
    }

    /// <summary>Possessing a familiar shuts the CX window first, as it shuts a shown stock panel.</summary>
    [UnmanagedCallersOnly]
    private static void OnPossessFamiliar(IntPtr msg)
    {
        CloseFor("[inv] possessing a familiar");
        _hooks.PossessOrig(msg);
    }

    /// <summary>Shut the CX window, telling the server, as a stock caller shuts a shown stock panel.</summary>
    private static void CloseFor(string why)
    {
        _log?.Info(why);
        InventoryService.OnSetVisible(show: false, notify: true);
    }

    /// <summary><c>ALL_ASSOCIATES_CAN_INTERACT</c>'s ruleset key, the FNV-1a-64 hash of its name, which the stock key reads.</summary>
    private const ulong AllAssociatesCanInteract = 0x65450e9db61e0246; // not-an-offset: a hash of a ruleset entry's name

    /// <summary>The player controls a possessed familiar and the ruleset leaves associates' inventories shut: the stock key's refusal.</summary>
    private static bool FamiliarRefused()
    {
        IntPtr cre = Creature(PlayerId());
        if (cre == IntPtr.Zero || !new CNWCCreature(cre).IsFamiliar) return false;
        IntPtr rules = Rules();
        return rules == IntPtr.Zero || new CNWRules(rules).RulesetIntEntry(AllAssociatesCanInteract, 0) == 0;
    }

    /// <summary>Make the hidden stock panel the player's, as its own open does (<c>SetInventoryVisible</c> stores the shown
    /// creature at <c>+0x31a0</c>): <c>Update_GuiRepository</c>'s <c>'A'</c> stamps every arriving item's possessor from that
    /// field, so a panel that never opened hands the backpack to nobody and <see cref="Fill"/> discards it.</summary>
    public static void Adopt()
    {
        IntPtr panel = MyPanel();
        uint pid = PlayerId();
        if (panel != IntPtr.Zero && pid != InventoryModel.Invalid) new CPanelInventory(panel).ShownOid = pid;
    }

    /// <summary>Numbers every add into the player's backpack grid; <see cref="_bagPageSeq"/> holds each page's last.</summary>
    private static int _bagSeq;
    private static readonly int[] _bagPageSeq = new int[InventoryModel.Pages];

    [UnmanagedCallersOnly]
    private static void OnAddItem(IntPtr box, uint oid, int x, int y, int ghost)
    {
        if (oid != InventoryModel.Invalid)
        {
            _log?.Info($"[inv] add oid=0x{oid:x} x={x} y={y} ghost={ghost} stack={StackOf(oid)}");
            IntPtr panel = MyPanel();
            bool bag = panel != IntPtr.Zero && box == new CPanelInventory(panel).BackpackGrid.Ptr;
            if (bag && (uint)(y / InventoryModel.Rows) < InventoryModel.Pages) _bagPageSeq[y / InventoryModel.Rows] = ++_bagSeq;
            if (_seen.Count < MaxSeen || _seen.ContainsKey(oid))
            {
                if (bag) _seen[oid] = _bagSeq;
                else _seen.TryAdd(oid, 0);
            }
            else if (!_seenFull) { _seenFull = true; Notice.Show(_log, $"inventory: more than {MaxSeen} item ids seen this session -- new items may not show in the CX window"); }
        }
        (*_hooks.AddSlot)(box, oid, x, y, ghost);
    }

    /// <summary>The backpack add counter: taken when a page is asked for, it tells what came after.</summary>
    public static int BagSeq => _bagSeq;

    /// <summary>Has the server sent an item on backpack page <paramref name="page"/> since <paramref name="since"/>? It
    /// streams only the selected page, whole in one update pass, so one add after a select means that page arrived. An
    /// empty page sends nothing -- the caller times out.</summary>
    public static bool BagArrived(int page, int since) => (uint)page < InventoryModel.Pages && _bagPageSeq[page] > since;

    /// <summary>Backpack page <paramref name="page"/>, asked for at <paramref name="since"/>, has arrived: the server resent
    /// it whole (a select clears what it believes it sent), so an item remembered on it that was not added again has
    /// left -- taken by a script or a bank while the window was shut, when nothing streams and no delete is sent, its
    /// client object lingering with the player as possessor. Cold: once per page asked.</summary>
    public static void PruneBagPage(int page, int since)
    {
        IntPtr app = App();
        if (app == IntPtr.Zero) return;
        int n = 0;
        foreach ((uint oid, int stamp) in _seen)
        {
            if (stamp > since) continue;
            IntPtr it = Item(app, oid);
            if (it != IntPtr.Zero && new CNWCItem(it).RepoY / InventoryModel.Rows == page && n < _drop.Length) _drop[n++] = oid;
        }
        for (int i = 0; i < n; i++) _seen.Remove(_drop[i]);
        if (n > 0) _log?.Info($"[inv] bag page {page}: {n} item(s) the server no longer sends, dropped");
    }

    private static int StackOf(uint oid)
    {
        IntPtr app = App();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, oid);
        return it == IntPtr.Zero ? 0 : new CNWCItem(it).StackSize;
    }

    /// <summary>The open's own <c>(1, count)</c> follows its <c>(0, 1)</c> (<see cref="SymbolMap.InvContainerPagenum"/>): it is
    /// not an arrival.</summary>
    private static bool _pagenumOpening;
    /// <summary>The server's <c>'P'</c>s on the player's panel: how many, and the container and grid page of the last.</summary>
    private static int _arrivals, _arrivalsSeen;
    private static uint _arrivalOid = InventoryModel.Invalid;
    private static int _arrivalPage;

    [UnmanagedCallersOnly]
    private static void OnContainerPagenum(IntPtr panel, int show, byte count)
    {
        _hooks.PagenumOrig(panel, show, count);
        if (!IsMyPanel(panel)) return;
        if (show == 0) { _pagenumOpening = true; return; }
        if (_pagenumOpening) { _pagenumOpening = false; return; }
        var p = new CPanelInventory(panel);
        _arrivals++; _arrivalOid = p.ContainerOid; _arrivalPage = p.ContainerGrid.Page;
        _log?.Info($"[inv] loot page {_arrivalPage} arrived (0x{_arrivalOid:x}, {count} pages)");
    }

    // ---- lookups ----

    private static IntPtr App() => PlayerIdentity.App();

    public static uint PlayerId()
    {
        IntPtr app = App();
        return app == IntPtr.Zero ? InventoryModel.Invalid : new CClientExoApp(app).Internal.PlayerCreatureId;
    }

    public static IntPtr MyPanel()
    {
        IntPtr app = App();
        return app == IntPtr.Zero ? IntPtr.Zero : new CClientExoApp(app).GetInventoryPanel().Ptr;
    }

    public static bool IsMyPanel(IntPtr panel) => panel != IntPtr.Zero && panel == MyPanel();

    public static bool ContainerMode(IntPtr panel) => panel != IntPtr.Zero && new CPanelInventory(panel).ContainerMode != 0;

    private static IntPtr Creature(uint id)
    {
        IntPtr app = App();
        if (app == IntPtr.Zero || id == InventoryModel.Invalid) return IntPtr.Zero;
        IntPtr go = EngineFn.GetGameObject(app, id);
        return go != IntPtr.Zero && new CNWCObject(go).Type == TypeCreature ? go : IntPtr.Zero;
    }

    private static IntPtr Item(IntPtr app, uint id)
    {
        IntPtr go = EngineFn.GetGameObject(app, id);
        if (go == IntPtr.Zero || new CGameObject(go).Type != TypeItem) return IntPtr.Zero;
        return new CGameObject(go).AsNWCItem();
    }

    /// <summary>The CX window covers the pointer while the target cursor grades: <paramref name="obj"/> is the CNWCObject* of
    /// the item under it, Zero over an empty cell or the frame. Game thread, per frame; allocates nothing.</summary>
    internal static bool Cover(InventoryWindow w, out IntPtr obj)
    {
        obj = IntPtr.Zero;
        if (!w.Aim(out uint oid)) return false;
        IntPtr app = App();
        if (oid == InventoryModel.Invalid || app == IntPtr.Zero || Item(app, oid) == IntPtr.Zero) return true;
        CNWCObject o = new CGameObject(EngineFn.GetGameObject(app, oid)).AsNWCObject();
        obj = o.IsNull ? IntPtr.Zero : o.Ptr;
        return true;
    }

    /// <summary>Resolve the engine's live target cursor at item <paramref name="oid"/>, as a click on the stock panel's cell
    /// would: the mode's own allowed types and validation decide, so a spell that cannot take an item stays up. Returns
    /// what became of the click, for the log.</summary>
    public static string FireTarget(uint oid)
    {
        IntPtr app = App(), mode = TargetFire.Live();
        if (app == IntPtr.Zero) return "no app";
        if (mode == IntPtr.Zero) return "no target mode is up";
        if (Item(app, oid) == IntPtr.Zero) return "not an item";
        CNWCObject obj = new CGameObject(EngineFn.GetGameObject(app, oid)).AsNWCObject();
        if (obj.IsNull || !TargetFire.Fire(mode, obj.Ptr)) return "the fire hooks are not up";
        return TargetFire.Live() == IntPtr.Zero ? "resolved" : "the mode refused it and stays up";
    }

    public static int Mods() => EngineFn.SdlGetModState != null ? EngineFn.SdlGetModState() : 0;

    // ---- the per-frame copy ----

    /// <summary>Copy the player's inventory into <paramref name="m"/>. Render thread; allocates nothing (the label
    /// re-read is cold, on a change of gold, weight or worn items only). The paper doll shows what <see cref="WornIds"/>
    /// picks. A container's items reach the same <c>AddItem</c>; <see cref="EngineLayout.ItemInContainer"/> sends them away.</summary>
    public static void Fill(InventoryModel m)
    {
        IntPtr app = App();
        uint pid = PlayerId();
        IntPtr cre = Creature(pid);
        if (app == IntPtr.Zero || cre == IntPtr.Zero) { m.Count = 0; return; }
        var c = new CNWCCreature(cre);
        IntPtr baseItems = BaseItems();

        long sig = c.Gold * 1_000_003L + c.CarriedWeight;
        Span<uint> worn = c.EquippedItems;
        Boxes(_boxIds);
        WornIds(_boxIds, worn, _wornIds);
        for (int i = 0; i < InventoryModel.Slots.Length; i++)
        {
            uint oid = _wornIds[i];
            sig = sig * 31 + oid;
            IntPtr it = oid == InventoryModel.Invalid ? IntPtr.Zero : Item(app, oid);
            if (it == IntPtr.Zero) { m.Equipped[i] = new InvItem { Oid = InventoryModel.Invalid, X = -1, Y = -1 }; continue; }
            m.Equipped[i] = Read(oid, it, baseItems, -1, -1);
        }
        m.Gold = (int)c.Gold;
        m.WeightTenths = c.CarriedWeight;
        if (sig != _statSig)
        {
            _statSig = sig; _labelsStale = true;
            _log?.Info($"[inv] worn/stat change gold={c.Gold} weight={c.CarriedWeight} chest=0x{worn[1]:x}");
        }

        int n = 0, dropped = 0;
        ulong bag = 0;
        foreach (uint oid in _seen.Keys)
        {
            IntPtr it = Item(app, oid);
            if (it == IntPtr.Zero)
            {
                if (dropped < _drop.Length) _drop[dropped++] = oid;
                continue;
            }
            var item = new CNWCItem(it);
            if (item.InContainer != 0)
            {
                if (dropped < _drop.Length) _drop[dropped++] = oid;
                continue;
            }
            if (item.Possessor != pid || ((ReadOnlySpan<uint>)_wornIds).Contains(oid)) continue;
            if (n >= InventoryModel.MaxItems)
            {
                if (!_modelFull) { _modelFull = true; Notice.Show(_log, $"inventory: more than {InventoryModel.MaxItems} items carried -- the rest are not shown"); }
                continue;
            }
            InvItem next = Read(oid, it, baseItems, item.RepoX, item.RepoY);
            next.Y = InventoryModel.Flip(next.Y, next.H);
            m.Items[n++] = next;
            bag += Mix(oid, next.X, next.Y, next.Stack);
        }
        for (int i = 0; i < dropped; i++) _seen.Remove(_drop[i]);
        m.Count = n;
        if (bag != _bagSig) { _bagSig = bag; m.Epoch++; }
    }

    /// <summary>Each paper-doll box's item id (<see cref="InventoryModel.Slots"/> order), <c>Invalid</c> for an empty box or no panel.</summary>
    private static void Boxes(Span<uint> ids)
    {
        IntPtr panel = MyPanel();
        for (int i = 0; i < ids.Length; i++)
        {
            CGuiInvbox box = panel == IntPtr.Zero ? default : new CPanelInventory(panel).InvGetEquipSlot((int)InventoryModel.Slots[i].Mask);
            ids[i] = box.IsNull ? InventoryModel.Invalid : box.ItemId;
        }
    }

    /// <summary>The id each paper-doll slot shows (<see cref="InventoryModel.Slots"/> order): the stock panel's slot box, which an
    /// appearance change leaves filled, else the creature's own slot (<paramref name="worn"/>, by the mask's bit) for a panel never built.</summary>
    internal static void WornIds(ReadOnlySpan<uint> boxes, ReadOnlySpan<uint> worn, Span<uint> ids)
    {
        for (int i = 0; i < ids.Length; i++)
            ids[i] = boxes[i] != InventoryModel.Invalid ? boxes[i] : worn[System.Numerics.BitOperations.Log2(InventoryModel.Slots[i].Mask)];
    }

    /// <summary>One item's contribution to the order-independent backpack digest (a sum of well-mixed terms).</summary>
    private static ulong Mix(uint oid, int x, int y, int stack)
    {
        ulong h = ((ulong)oid << 32) ^ (uint)(x | (y << 8) | (stack << 16));
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;   // not-an-offset: murmur3 fmix64 constant
        return h;
    }

    // ---- the open container ----

    /// <summary>Every item id seen in the stock container grid since the container opened, all pages (the grid holds one).</summary>
    private static readonly HashSet<uint> _loot = new();
    private static readonly uint[] _lootDrop = new uint[LootModel.MaxItems];
    private static ulong _lootSig;
    private static bool _lootGridOdd, _lootFull;

    /// <summary>Mirror the player's open container into <paramref name="m"/>: the page on the stock grid is read live
    /// and every page seen before is kept (the server streams one). A multi-page container's page is fresh once the
    /// server's <c>'P'</c> for it arrived (<see cref="OnContainerPagenum"/>), a single page or a bag once an item shows
    /// or <paramref name="grace"/> ticks pass; an item the live grid no longer holds is gone. Render thread, no allocation.</summary>
    public static void FillLoot(LootModel m, IntPtr panel, long now, long grace)
    {
        var p = new CPanelInventory(panel);
        uint oid = panel != IntPtr.Zero && p.ContainerMode != 0 ? p.ContainerOid : InventoryModel.Invalid;
        if (oid != m.Oid)
        {
            _loot.Clear(); _lootSig = 0;
            m.Reset(oid, now);
            if (!m.Open) return;
            m.IsItem = p.ContainerIsItem != 0;
            m.Title = Label(p.ContainerTitle);
            m.Pages = LootPages(m, p);
            if (m.Pages > 1) m.Ask(0, now);
            _log?.Info($"[inv] loot open 0x{oid:x} '{m.Title}' pages={p.ContainerPages} bag={m.IsItem}");
        }
        if (!m.Open) return;
        CGuiContainer g = p.ContainerGrid;
        if (g.Cols != LootModel.Cols || g.Rows != LootModel.Rows || g.ItemIds == IntPtr.Zero)
        {
            if (!_lootGridOdd) { _lootGridOdd = true; Notice.Show(_log, $"inventory: the container grid is {g.Cols}x{g.Rows}, not {LootModel.Cols}x{LootModel.Rows} -- loot is not shown"); }
            m.Count = 0;
            return;
        }
        m.Pages = LootPages(m, p);
        m.EnginePage = g.Page;
        if (_arrivals != _arrivalsSeen)
        {
            _arrivalsSeen = _arrivals;
            if (_arrivalOid == m.Oid) { m.Arrived(_arrivalPage); m.Epoch++; }
        }
        uint* ids = (uint*)g.ItemIds;
        const int cells = LootModel.Cols * LootModel.Rows;
        bool any = false;
        for (int i = 0; i < cells; i++)
        {
            if (ids[i] == InventoryModel.Invalid) continue;
            any = true;
            if (_loot.Count < LootModel.MaxItems) _loot.Add(ids[i]);
            else if (!_lootFull) { _lootFull = true; Notice.Show(_log, $"inventory: more than {LootModel.MaxItems} items in the container -- the rest are not shown"); }
        }
        if (m.Pages == 1 && !m.Fresh[0] && (any || now - m.OpenedAt >= grace)) { m.Arrived(0); m.Epoch++; }
        int page = m.EnginePage;
        bool live = m.Awaiting < 0 && (uint)page < LootModel.MaxPages && m.Fresh[page];

        IntPtr app = App(), baseItems = BaseItems();
        int n = 0, dropped = 0;
        ulong bag = 0;
        foreach (uint id in _loot)
        {
            IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, id);
            var item = new CNWCItem(it);
            bool gone = it == IntPtr.Zero || item.InContainer == 0
                        || live && item.RepoY / LootModel.Rows == page && !OnGrid(ids, cells, id);
            if (gone)
            {
                if (dropped < _lootDrop.Length) _lootDrop[dropped++] = id;
                continue;
            }
            InvItem next = Read(id, it, baseItems, item.RepoX, item.RepoY);
            next.Y = ItemGrid.Flip(next.Y, next.H, LootModel.Rows);
            m.Items[n++] = next;
            bag += Mix(id, next.X, next.Y, next.Stack);
        }
        for (int i = 0; i < dropped; i++) _loot.Remove(_lootDrop[i]);
        m.Count = n;
        if (bag != _lootSig) { _lootSig = bag; m.Epoch++; }
    }

    /// <summary>The container's page count. ponytail: a bag shows its first page only -- the server's <c>SetNextPage</c> turns
    /// placeables alone, so a bag's later pages never stream; until a bag has more than one.</summary>
    private static int LootPages(LootModel m, CPanelInventory p) => m.IsItem ? 1 : Math.Clamp((int)p.ContainerPages, 1, LootModel.MaxPages);

    private static bool OnGrid(uint* ids, int cells, uint oid)
    {
        for (int i = 0; i < cells; i++) if (ids[i] == oid) return true;
        return false;
    }

    /// <summary>Turn the stock container view -- and with it the server's page -- one page on or back, as its arrows do.</summary>
    public static void LootStep(IntPtr panel, bool forward)
    {
        if (panel == IntPtr.Zero) return;
        var p = new CPanelInventory(panel);
        if (forward) p.InvContainerPageUp();
        else p.InvContainerPageDown();
    }

    /// <summary>Close the open container as the stock panel does when the inventory closes over it
    /// (<c>SetContainerGUIVisible(0, 1, 0, INVALID)</c>: the local teardown, then <c>GuiContainer_Status(0)</c>).</summary>
    public static void CloseContainer(IntPtr panel)
    {
        if (!ContainerMode(panel)) return;
        _log?.Info("[inv] loot close (inventory closed over it)");
        new CPanelInventory(panel).InvSetContainerGuiVisible(0, 1, 0, InventoryModel.Invalid);
    }

    private static IntPtr Rules()
    {
        IntPtr rules = Native.Sym(SymbolMap.Rules);
        return rules == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)rules;
    }

    private static IntPtr BaseItems()
    {
        IntPtr r = Rules();
        return r == IntPtr.Zero ? IntPtr.Zero : new CNWRules(r).BaseItems;
    }

    private static InvItem Read(uint oid, IntPtr itemPtr, IntPtr baseItems, int x, int y)
    {
        var item = new CNWCItem(itemPtr);
        var r = new InvItem { Oid = oid, X = x, Y = y, W = 1, H = 1, Stack = item.StackSize, BaseId = item.BaseItem, Ghost = item.Ghost != 0 };
        if (baseItems != IntPtr.Zero)
        {
            var b = new CNWBaseItem(EngineFn.GetBaseItem(baseItems, item.BaseItem));
            if (!b.IsNull)
            {
                r.W = Math.Max(1, (int)b.InvSlotWidth); r.H = Math.Max(1, (int)b.InvSlotHeight);
                r.SlotMask = b.EquipSlots; r.Stackable = b.Stacking > 1; r.Container = b.Container != 0;
            }
        }
        r.CastSpell = HasProperty(item, CastSpellProperty);
        return r;
    }

    /// <summary>The item property type of a cast-spell property (<c>itempropdef.2da</c> row 15), the one the stock
    /// double-click opens a target cursor for.</summary>
    private const ushort CastSpellProperty = 15;

    /// <summary>True when one of the item's active properties is of <paramref name="type"/>.</summary>
    private static bool HasProperty(CNWCItem item, ushort type)
    {
        CNWItemProperty props = item.ActiveProperties;
        if (props.IsNull) return false;
        for (int i = 0; i < item.ActivePropertyCount; i++) if (props[i].PropertyName == type) return true;
        return false;
    }

    /// <summary>Run the stock <c>SetCharacterData</c> on the (never shown) player panel and read back its AC and
    /// max-weight labels -- the engine's own text, TLK and <c>encumbrance.2da</c> included. Cold: on open, and when
    /// <see cref="Fill"/> saw gold, weight or worn items change.</summary>
    public static void RefreshLabels(InventoryModel m, bool force = false)
    {
        if (!force && !_labelsStale) return;
        _labelsStale = false;
        IntPtr panel = MyPanel();
        uint pid = PlayerId();
        if (panel == IntPtr.Zero || pid == InventoryModel.Invalid || Creature(pid) == IntPtr.Zero) return;
        var p = new CPanelInventory(panel);
        p.InvSetCharacterData(pid);
        m.ArmorClass = Label(p.EquipPanel.AcLabel);
        m.MaxWeight = Label(p.MaxWeightLabel);
    }

    /// <summary>The item's display name as the stock panel builds it (<c>CNWCItem::GetName(1)</c>, §3.4: the custom name
    /// once identified, else the base item's name behind the "Unidentified" prefix). Cold: tooltip and search only.</summary>
    public static string Name(uint oid)
    {
        IntPtr app = App();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, oid);
        if (it == IntPtr.Zero) return "";
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;
        new CNWCItem(it).ItemGetName(exo, 1);
        string name = Native.ReadExo((IntPtr)exo);
        EngineFn.ExoStringDtor((IntPtr)exo);
        return name.Trim();
    }

    private static string Label(CGuiLabel label)
    {
        if (label.IsNull) return "";
        return label.String.ReadText(MaxLabelLines);
    }

    // ---- senders (this = the live CNWCMessage) ----

    /// <summary>The live <c>CNWCMessage</c>; none means the action cannot reach the server, and the player is told.</summary>
    private static IntPtr Msg()
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) Feedback("inventory: no connection to the server -- the action was not sent");
        return msg;
    }

    /// <summary>The <c>GuiInventory_Status</c> the stock panel owes the server, sent from the CX window in its place.</summary>
    public static void Status(bool visible)
    {
        _log?.Info($"[inv] status open={visible}");
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendGuiInvStatus(msg, visible ? 1 : 0, PlayerId());
    }

    public static void SelectPanel(int page)
    {
        _log?.Info($"[inv] select page={page}");
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendGuiInvSelectPanel(msg, (byte)page, 1);
    }

    public static void Equip(uint oid, uint mask)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendInvEquip(msg, oid, mask, PlayerId());
    }

    public static void Unequip(uint oid, int x, int y, uint target)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendInvUnequip(msg, oid, (byte)x, (byte)y, target);
    }

    /// <summary><c>RepositoryMove</c> to cell (<paramref name="x"/>, wire <paramref name="y"/>) of <paramref name="target"/>'s
    /// repository: the player (the backpack), a container, or INVALID with x = y = 0xff -- the server picks a free cell in
    /// the player's inventory, as a stock container double-click sends (<c>CGuiContainer::HandleDoubleClick</c> @0x68c1f0).</summary>
    public static void Move(uint oid, int x, int y, uint target)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendInvRepositoryMove(msg, oid, (byte)x, (byte)y, target);
    }

    /// <summary><c>Input_UseObject</c>: on the open container it closes it, as the stock container X and Close do
    /// (<c>HandleContainerClose</c> @0x68be50).</summary>
    public static void UseObject(uint oid)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendInputUseObject(msg, oid, 0, 0);
    }

    public static void Split(uint oid, int count)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendInputSplitItem(msg, oid, count);
    }

    public static void Merge(uint dst, uint src)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero) EngineFn.SendInputMergeItem(msg, dst, src);
    }

    /// <summary>The stock double-click, the backpack grid's own <c>HandleDoubleClick</c>: a container is <c>UseObject</c>ed,
    /// and an item with a cast-spell property opens that spell's target cursor.</summary>
    public static void DoubleClick(uint oid)
    {
        IntPtr app = App(), panel = MyPanel();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, oid);
        if (it == IntPtr.Zero || panel == IntPtr.Zero) return;
        new CPanelInventory(panel).BackpackGrid.InvboxHandleDoubleClick(new CNWCItem(it));
    }

    /// <summary>The item's own radial (<c>CNWCItem::ActivateRadialMenu</c>, which CX's radial takes over): split, use,
    /// identify, equip, examine. It opens at the click (<paramref name="x"/>, <paramref name="y"/>, screen px): the engine
    /// would place it at its own last mouse position, stale while the pointer is over a CX window. <paramref name="flags"/>
    /// overrides the backpack / worn choice (a container's item: possessed in a bag, else the ground menu).</summary>
    public static void Radial(uint oid, int x, int y, int flags = -1)
    {
        IntPtr app = App();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, oid);
        if (it == IntPtr.Zero) return;
        var item = new CNWCItem(it);
        IntPtr cre = Creature(PlayerId());
        bool worn = cre != IntPtr.Zero && new CNWCCreature(cre).EquippedItems.Contains(oid);
        if (flags < 0) flags = worn ? RadialEquipped : RadialPossessed;
        RadialService.OpenAt = (x, y);
        try { item.ItemActivateRadialMenu(flags); }
        finally { RadialService.OpenAt = null; }
    }

    // ---- the world under the pointer ----

    /// <summary>The <c>CHitInfo</c> every pick reuses (allocated once; <see cref="PickWorld"/> unlinks its gob before returning).</summary>
    private static byte* _hit;
    /// <summary><c>CHitInfo::Reset</c>'s mask, as <c>CGuiDragSource</c>'s constructor passes it.</summary>
    private const long HitMask = -0x40000001L;   // not-an-offset: CHitInfo pick mask
    /// <summary><c>GetGameobjectHit</c> types (the gob's object type) the drops act on.</summary>
    internal const int HitCreature = 5, HitItem = 6, HitPlaceable = 9;
    /// <summary>Base items a drop on the player drinks (<c>CGuiInvbox::HandleDropAsSource</c>): potions, crafted potions.</summary>
    private const int BasePotion = 49, BaseCraftedPotion = 104;

    /// <summary>What is in the world under screen point (<paramref name="sx"/>, <paramref name="sy"/>), the way a stock drag
    /// ends: nothing when an engine GUI object is there, else <c>GameCameraHitCheck</c> at the GUI-space point (y flipped
    /// through <c>GuiManVpH</c>, no scale) and <c>GetGameobjectHit</c> for the object. Input thread; cold.</summary>
    public static WorldHit PickWorld(int sx, int sy)
    {
        IntPtr gmp = Native.Sym(SymbolMap.GuiMan);
        IntPtr gm = gmp == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)gmp;
        if (gm == IntPtr.Zero) return default;
        var man = new CGuiMan(gm);
        int gx = sx, gy = man.VpHeight - sy;
        if (man.GuiObjectHitCheck(gx, gy, 1, 1) != IntPtr.Zero) return default;
        if (_hit == null) _hit = (byte*)NativeMemory.AllocZeroed(EngineLayout.SizeOfHitInfo);
        EngineFn.HitInfoReset((IntPtr)_hit, 0, HitMask, 0);
        EngineFn.GameCameraHitCheck(gx, gy, IntPtr.Zero, (IntPtr)_hit);
        var info = new CHitInfo((IntPtr)_hit);
        IntPtr obj = IntPtr.Zero;
        int type = EngineFn.GetGameobjectHit((IntPtr)_hit, &obj);
        WorldHit r = info.Kind == 1 ? new WorldHit(WorldKind.Ground, InventoryModel.Invalid, 0, info.Point)
                   : obj != IntPtr.Zero && type > 2 ? new WorldHit(WorldKind.Object, new CNWCObject(obj).Id, type, info.Point)
                   : default;
        // A gob hit links the record into the gob's SafePointer list: unlink it now, not when the gob dies.
        EngineFn.HitInfoReset((IntPtr)_hit, 0, HitMask, 0);
        _log?.Info($"[inv] pick ({sx},{sy}) -> {r.Kind} oid=0x{r.Oid:x} type={r.Type} at {r.Point}");
        return r;
    }

    /// <summary>What a backpack, worn or looted item dropped on <paramref name="w"/> does, as the stock
    /// <c>CGuiInvbox::HandleDropAsSource</c> decides it: the ground drops it there; a potion on the player or the
    /// player's own associate is used on it; any other creature, from the backpack only (not
    /// <paramref name="notBackpack"/>), opens a barter with it. Null: nothing happens.</summary>
    public static string? DropVerb(uint oid, bool notBackpack, in WorldHit w)
    {
        if (w.Kind == WorldKind.Ground) return "drop";
        if (w.Kind != WorldKind.Object || w.Type != HitCreature) return null;
        uint pid = PlayerId();
        IntPtr cre = Creature(w.Oid);
        bool mine = w.Oid == pid || (cre != IntPtr.Zero && new CNWCCreature(cre).IsAnyAssociate && new CNWCCreature(cre).MasterId == pid);
        IntPtr app = App();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, oid);
        int b = it == IntPtr.Zero ? -1 : new CNWCItem(it).BaseItem;
        if (mine && b is BasePotion or BaseCraftedPotion) return "use";
        return w.Oid != pid && !notBackpack ? "barter" : null;
    }

    /// <summary>The player's quickbar button under screen point (<paramref name="sx"/>, <paramref name="sy"/>), zero when the
    /// engine GUI object there is not one of the quickbar's 36 inline buttons (<see cref="EngineLayout.QuickbarButtons"/>);
    /// <paramref name="slot"/> is its index (0-35: three rows of 12).</summary>
    public static IntPtr QuickButtonAt(int sx, int sy, out int slot)
    {
        slot = -1;
        IntPtr gmp = Native.Sym(SymbolMap.GuiMan), app = App();
        IntPtr gm = gmp == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)gmp;
        if (gm == IntPtr.Zero || app == IntPtr.Zero) return IntPtr.Zero;
        var man = new CGuiMan(gm);
        IntPtr obj = man.GuiObjectHitCheck(sx, man.VpHeight - sy, 1, 1);
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        IntPtr qb = obj == IntPtr.Zero || intern.IsNull || intern.InGameGui.IsNull ? IntPtr.Zero : intern.InGameGui.Quickbar;
        if (qb == IntPtr.Zero) return IntPtr.Zero;
        long off = (long)obj - (long)qb - EngineLayout.QuickbarButtons;
        int stride = EngineLayout.QuickbarButtonStride;
        if (off < 0 || off % stride != 0 || off / stride >= EngineLayout.QuickbarButtonCount) return IntPtr.Zero;
        slot = (int)(off / stride);
        return obj;
    }

    /// <summary>The quick button's own item drop (<c>CGuiQuickButton::HandleDragDropAsDest</c>): <c>SetButtonItem(item,
    /// 0xff00ff, 0)</c>, which tells the server, then the item's drop sound.</summary>
    public static void AssignQuickButton(IntPtr button, uint oid)
    {
        IntPtr app = App();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, oid);
        if (button == IntPtr.Zero || it == IntPtr.Zero) return;
        EngineFn.QuickButtonSetItem(button, oid, QuickItemProp, 0);
        new CNWCItem(it).ItemPlayDropSound();
    }

    /// <summary><c>SetButtonItem</c>'s property argument as the stock item drop passes it.</summary>
    private const int QuickItemProp = 0xff00ff;   // not-an-offset: the stock drop's "no property" word

    /// <summary><c>CGuiStoreInvBox::GetGuiObjType</c>'s tag, an open store's item box, where the stock drag sells.</summary>
    public const int StoreBoxTag = 0x53746f72;   // not-an-offset: 'Stor'

    /// <summary>The engine GUI object under screen point (<paramref name="sx"/>, <paramref name="sy"/>) and its
    /// <c>GetGuiObjType</c> tag (0 when none); the hit check leaves the point on it in <c>CGuiMan</c>'s hit point.</summary>
    public static IntPtr GuiObjectAt(int sx, int sy, out int tag)
    {
        tag = 0;
        IntPtr gmp = Native.Sym(SymbolMap.GuiMan);
        IntPtr gm = gmp == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)gmp;
        if (gm == IntPtr.Zero) return IntPtr.Zero;
        var man = new CGuiMan(gm);
        IntPtr obj = man.GuiObjectHitCheck(sx, man.VpHeight - sy, 1, 1);
        if (obj != IntPtr.Zero) tag = new CGuiObject(obj).GetGuiObjType();
        return obj;
    }

    /// <summary>Offer backpack item <paramref name="oid"/> on the player's side of a trade, <paramref name="box"/>, as
    /// <c>CGuiBarterInvBox::ProcessItemDrop</c> does: <c>BarterAddItem</c> at the cell under the last hit check (0xff, 0xff
    /// when it is taken, the server picking), then the item ghosted at that cell in the box.</summary>
    public static void Offer(IntPtr box, uint oid)
    {
        IntPtr gmp = Native.Sym(SymbolMap.GuiMan);
        IntPtr gm = gmp == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)gmp;
        IntPtr it = Item(App(), oid), bases = BaseItems(), model = box == IntPtr.Zero ? IntPtr.Zero : new CGuiObject(box).Model;
        if (gm == IntPtr.Zero || it == IntPtr.Zero || bases == IntPtr.Zero || model == IntPtr.Zero
            || EngineFn.MultiInvboxCellFree == null || EngineFn.MultiInvboxAddItem == null) return;
        var item = new CNWCItem(it);
        if (!Cells(it, bases, out int w, out int h)) return;
        var grid = new CGuiMultiInvbox(box);
        Span<float> hit = new CGuiMan(gm).HitPoint;
        System.Numerics.Vector3 at = new Gob(model).Position;
        int x = InventoryWindow.DropCell(hit[0], at.X, w, grid.Cols), y = InventoryWindow.DropCell(hit[1], at.Y, h, grid.Rows);
        if (EngineFn.MultiInvboxCellFree(box, w, h, x, y, oid) == 0) x = y = -1;
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero && EngineFn.SendBarterAddItem != null) EngineFn.SendBarterAddItem(msg, oid, (byte)x, (byte)y);
        item.Ghost = 1;
        item.PendingX = (byte)x;
        item.PendingY = (byte)y;
        EngineFn.MultiInvboxAddItem(box, oid, x, y, 1);
    }

    /// <summary>Item <paramref name="it"/>'s size in inventory cells, from its base item as the stock occupancy check reads it.</summary>
    private static bool Cells(IntPtr it, IntPtr bases, out int w, out int h)
    {
        var b = new CNWBaseItem(EngineFn.GetBaseItem(bases, new CNWCItem(it).BaseItem));
        w = b.IsNull ? 0 : b.InvSlotWidth;
        h = b.IsNull ? 0 : b.InvSlotHeight;
        return !b.IsNull;
    }

    /// <summary>The store item the engine is dragging and its size in cells, <c>W</c> 0 when none: a live drag whose source,
    /// the mouse captor, is a box tagged 'Stor', as the stock repository drop tells a buy from a move.</summary>
    public static (uint Oid, int W, int H) StoreDrag()
    {
        IntPtr gmp = Native.Sym(SymbolMap.GuiMan);
        IntPtr gm = gmp == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)gmp;
        if (gm == IntPtr.Zero || !Quickbar.QuickbarNative.EngineDragging()) return default;
        IntPtr src = new CGuiMan(gm).MouseCapture;
        if (new CGuiObject(src).GetGuiObjType() != StoreBoxTag) return default;
        uint oid = new CGuiInvbox(src).DraggedId;
        IntPtr it = Item(App(), oid), bases = BaseItems();
        if (it == IntPtr.Zero || bases == IntPtr.Zero || !Cells(it, bases, out int w, out int h)) return default;
        return (oid, w, h);
    }

    /// <summary><c>Store_RequestBuy</c> for store item <paramref name="oid"/> at a backpack cell in the wire's form (0xff, 0xff
    /// for the server to place), owned by the player, as the stock repository drop sends it: no confirm.</summary>
    public static void Buy(uint oid, int x, int globalY)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero && EngineFn.SendStoreRequestBuy != null) EngineFn.SendStoreRequestBuy(msg, oid, (byte)x, (byte)globalY, PlayerId());
    }

    /// <summary>The open store's price line for <paramref name="oid"/>, cleared for <see cref="InventoryModel.Invalid"/>, as the
    /// stock inventory's hover writes it; nothing when no store is open.</summary>
    public static void PriceInStore(uint oid)
    {
        IntPtr slot = Native.Sym(SymbolMap.ActiveStorePanel);
        IntPtr panel = slot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)slot;
        if (panel != IntPtr.Zero && EngineFn.StoreMouseoverInventoryItem != null) EngineFn.StoreMouseoverInventoryItem(panel, oid);
    }

    /// <summary><c>Store_RequestSell</c>, as the stock store box's drop sends it: no confirm.</summary>
    public static void Sell(uint oid)
    {
        IntPtr msg = Msg();
        if (msg != IntPtr.Zero && EngineFn.SendStoreRequestSell != null) EngineFn.SendStoreRequestSell(msg, oid);
    }

    /// <summary>Send <see cref="DropVerb"/>'s action: <c>Inventory_Drop</c> at the point, <c>Input_UseItem</c> on the player, or
    /// <c>BarterStartBarter</c>.</summary>
    public static void DropOn(string verb, uint oid, in WorldHit w)
    {
        IntPtr msg = Msg();
        if (msg == IntPtr.Zero) return;
        Vec3 p = new() { X = w.Point.X, Y = w.Point.Y, Z = w.Point.Z }, zero = default;
        switch (verb)
        {
            case "drop": EngineFn.SendInvDrop(msg, oid, p.X, p.Y, p.Z); break;
            case "use": EngineFn.SendInputUseItem(msg, oid, 0, NoProperty, w.Oid, VectorArg.Of(&zero)); break;
            case "barter": EngineFn.SendBarterStart(msg, w.Oid, oid); break;
        }
    }

    /// <summary><c>Input_UseItem</c>'s "no item property" byte, as the stock potion drop passes it.</summary>
    private const byte NoProperty = 0xff;   // not-an-offset: a wire sentinel

    /// <summary>Where gold dropped on <paramref name="w"/> goes, as <c>CGuiPlayerGoldSource::HandleDropAsSource</c> decides
    /// it: the ground (INVALID + the point), a creature or placeable, or a ground item whose base item is usable (a
    /// container). False: nowhere.</summary>
    public static bool GoldTarget(in WorldHit w, out uint target)
    {
        target = InventoryModel.Invalid;
        if (w.Kind == WorldKind.Ground) return true;
        if (w.Kind != WorldKind.Object) return false;
        if (w.Type is HitCreature or HitPlaceable) { target = w.Oid; return true; }
        if (w.Type != HitItem) return false;
        IntPtr app = App(), baseItems = BaseItems();
        IntPtr it = app == IntPtr.Zero ? IntPtr.Zero : Item(app, w.Oid);
        if (it == IntPtr.Zero || baseItems == IntPtr.Zero) return false;
        var b = new CNWBaseItem(EngineFn.GetBaseItem(baseItems, new CNWCItem(it).BaseItem));
        if (b.IsNull || b.Container == 0) return false;
        target = w.Oid;
        return true;
    }

    /// <summary><c>Gold_TransferGold</c>: <paramref name="amount"/> to <paramref name="target"/>, or onto the ground at the
    /// point when it is INVALID.</summary>
    public static void SendGold(uint target, int amount, in WorldHit w)
    {
        IntPtr msg = Msg();
        if (msg == IntPtr.Zero) return;
        Vec3 p = target == InventoryModel.Invalid ? new Vec3 { X = w.Point.X, Y = w.Point.Y, Z = w.Point.Z } : default;
        EngineFn.SendGoldTransfer(msg, target, VectorArg.Of(&p), amount, 0, 0, 0);
    }

    /// <summary>The player's gold now, 0 without a creature.</summary>
    public static int Gold()
    {
        IntPtr cre = Creature(PlayerId());
        return cre == IntPtr.Zero ? 0 : (int)new CNWCCreature(cre).Gold;
    }

    /// <summary>A refusal the player must see.</summary>
    public static void Feedback(string text) => Notice.Show(_log, text);

    /// <summary>Plays the interface sound <paramref name="resref"/> once, as the stock panel plays its refusals.</summary>
    public static unsafe void GuiSound(string resref)
    {
        ResRef r = ResRef.Of(resref, lower: true);
        EngineFn.PlayGuiSound(ResRefArg.Of(&r), 0);
    }
}
