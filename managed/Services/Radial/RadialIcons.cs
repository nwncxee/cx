using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Nameplate;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>What a <c>CGuiIcon</c> is at runtime, by vptr.
/// <see cref="Plain"/> is <c>CGuiIcon</c> itself and every subclass not listed (<c>CSpellIcon</c>): one texture
/// by resref. The other three are what the item root's centre icon can be.</summary>
public enum IconKind { Plain, Layered, Composite, Armor }

/// <summary>
/// The item root's composite icon: the engine draws an item's <c>CGuiIcon</c> as one to six stacked textured quads
/// (<c>GUI_ICON_%dX%dL</c> / <c>..LC</c> / <c>GUI_ICON_ARMR%dL</c>), so this draws the same layers, bottom to top, as
/// <see cref="TextRig.DrawIconTid"/> quads. The handle is the item's OBJECT ID, not its <c>CGuiIcon*</c>, which
/// <c>CNWCItem::UpdateIcons</c> @0x7cbc50 rebuilds on every item update; a <c>CAurTexture*</c> is borrowed, never cached.
/// </summary>
internal static unsafe class RadialIcons
{
    /// <summary>The armour model's ceiling, and the widest scheme the engine keeps (<c>CAurTextureBasic::SetScheme</c>
    /// @0x485f90 holds 15 slots; items pass 10). A scheme longer than that is treated as no layers, not truncated.</summary>
    public const int MaxLayers = EngineLayout.ArmorIconLayers;
    internal const int MaxScheme = 15;
    internal const int ResRefLen = 16;
    /// <summary>CGameObject type byte for an item (<c>CClientExoAppInternal::GetItemByGameObjectID</c> @0x63f3a0).</summary>
    private const byte GameObjTypeItem = 6;

    /// <summary>Per layer of the LAST <see cref="Layers"/> walk, the master-palette rows CX resolved
    /// the layer with, and whether it did. Scratch, not state: <see cref="Layers"/> clears it on entry and
    /// <see cref="Draw"/> reads it immediately after, both on the UI thread, which is the same contract the
    /// stack-allocated tid span already has.</summary>
    private static readonly int[] _cxRows = new int[MaxLayers * PltRows];
    private static readonly bool[] _cxPlt = new bool[MaxLayers];
    /// <summary>Rows a PLT icon carries -- the ten the game layer can ever pass (<c>PltImage.Layers</c>).</summary>
    internal const int PltRows = 10;

    private static AurTextureFindFn _find;
    private static AurTextureGetFn _get;
    private static GuiIconGetIconModelFn _getIconModel;
    private static GetGameObjectFn _getGameObject;
    private static GetObjectNameFn _getObjectName;
    private static ExoStringDtorFn _exoDtor;
    private static IntPtr _appMgr, _vtLayered, _vtComposite, _vtArmor;
    private static bool _ready, _initTried, _firstDrawLogged;
    private static ExtenderLog? _log;

    /// <summary>Resolve once; false stands the composite icon down (the dropdown then shows no header icon), and the
    /// stand-down line prints once, not per right-click. Called lazily by <see cref="HeaderItem"/> so the owner needs
    /// no init call.</summary>
    public static bool Init(ExtenderLog log)
    {
        if (_initTried) return _ready;
        _initTried = true;
        _log = log;
        IconFrames.Init(log);
        RadialIconsPlt.Install(log);
        if (!SymbolMap.Require(log, "RadialIcons", SymbolMap.AurTextureFind, SymbolMap.AurTextureGet, SymbolMap.GetGameObject,
                               SymbolMap.AppManager, SymbolMap.VtCLayeredIcon, SymbolMap.VtCCompositeIcon, SymbolMap.VtCArmorIcon,
                               SymbolMap.GuiIconGetIconModel)) return false;
        if (!EngineLayout.Require(log, "RadialIcons", "CGuiIcon", "CLayeredIcon", "CCompositeIcon", "CArmorIcon", "CNWCItem", "CGameObject")) return false;
        _find = (AurTextureFindFn)SymbolMap.Resolve(SymbolMap.AurTextureFind, log);
        _get = (AurTextureGetFn)SymbolMap.Resolve(SymbolMap.AurTextureGet, log);
        _getIconModel = (GuiIconGetIconModelFn)SymbolMap.Resolve(SymbolMap.GuiIconGetIconModel, log);
        _getGameObject = (GetGameObjectFn)SymbolMap.Resolve(SymbolMap.GetGameObject, log);
        _getObjectName = (GetObjectNameFn)SymbolMap.Resolve(SymbolMap.GetObjectName, log);
        _exoDtor = (ExoStringDtorFn)SymbolMap.Resolve(SymbolMap.ExoStringDtor, log);
        _appMgr = SymbolMap.Resolve(SymbolMap.AppManager, log);
        _vtLayered = EngineAbi.Vtbl(SymbolMap.Resolve(SymbolMap.VtCLayeredIcon, log));
        _vtComposite = EngineAbi.Vtbl(SymbolMap.Resolve(SymbolMap.VtCCompositeIcon, log));
        _vtArmor = EngineAbi.Vtbl(SymbolMap.Resolve(SymbolMap.VtCArmorIcon, log));
        _ready = _find != null && _get != null && _getIconModel != null && _getGameObject != null && _appMgr != IntPtr.Zero;
        return _ready;
    }

    public static IconKind Classify(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return IconKind.Plain;
        IntPtr vptr = *(IntPtr*)icon;
        if (vptr == _vtLayered) return IconKind.Layered;
        if (vptr == _vtComposite) return IconKind.Composite;
        if (vptr == _vtArmor) return IconKind.Armor;
        return IconKind.Plain;
    }

    /// <summary>The dropdown's header row for an item root: the item's name and its own icon by handle, or
    /// <c>default</c> (null label) when the root is not an item, the item is gone, or the unit is stood down. Cold
    /// (once per open); logs the <c>[radial] icon</c> line under <c>logs=1</c>. The owner inserts it at row 0 of the
    /// item root and treats <c>Activate</c> on it as a no-op.</summary>
    public static CascadeItem HeaderItem(IntPtr menu, RootKind kind, ExtenderLog log)
    {
        if (kind != RootKind.Item || !Init(log)) return default;
        uint id = RadialTree.TargetId(menu, kind);
        IntPtr icon = ItemIcon((IntPtr)id);
        if (icon == IntPtr.Zero) return default;
        _firstDrawLogged = false;
        if (ExtenderLog.Enabled)
        {
            Span<int> tids = stackalloc int[MaxLayers];
            int n = Layers(icon, tids);
            log.Info($"[radial] icon slot=c kind={Classify(icon)} tid={(n > 0 ? tids[0] : 0)} layers={n} icon=0x{(long)icon:x} (0 = built on the next frame)");
        }
        return new CascadeItem(Native.EngineText(ObjectName(id))) { IconHandle = (IntPtr)id };
    }

    /// <summary>Have the engine build the item's own icon model when the icon is PLT-tinted (layered or armour), so its
    /// textures exist whatever page the stock grid holds (<see cref="SymbolMap.GuiIconGetIconModel"/>); a no-op once built.
    /// Outside any draw: the model load is engine work, not ours to interleave with a GL batch.</summary>
    public static void BuildModel(uint oid)
    {
        if (!_ready) return;
        IntPtr icon = ItemIcon((IntPtr)oid);
        if (Classify(icon) is IconKind.Layered or IconKind.Armor) _getIconModel(icon);
    }

    /// <summary>Per frame, from the owner's icon pass: every layer of the item's icon as it stands now, bottom to top.
    /// Zero-alloc; a vanished item, a stood-down unit or a not-yet-built texture draws nothing.</summary>
    public static void Draw(IntPtr handle, float x, float y, float size, float alpha) => Draw(handle, x, y, size, size, alpha);

    /// <summary><see cref="Draw(IntPtr, float, float, float, float)"/> of a <paramref name="cellsW"/>×<paramref name="cellsH"/>
    /// inventory item into a <paramref name="w"/>×<paramref name="h"/> rect: the art's own frame (<see cref="IconFrames"/>) at
    /// its proportions, centred. A sheet whose frame is unknown is cropped to the cells as the engine's <c>GUI_ICON_%dX%dL</c>
    /// model crops it: u to <c>W*32/pow2(W*32)</c>, v from <c>1 - H*32/pow2(H*32)</c>.</summary>
    public static void Draw(IntPtr handle, float x, float y, float w, float h, float alpha, int cellsW, int cellsH,
                            bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (!_ready || handle == IntPtr.Zero || !AurTex.Ready) return;
        DrawGuiIcon(ItemIcon(handle), x, y, w, h, alpha, cellsW, cellsH, clip, cx, cy, cw, ch);
    }

    /// <summary>As the cell-cropped <c>Draw</c>, from an engine <c>CGuiIcon*</c> rather than an item: a dragged icon.</summary>
    public static void DrawGuiIcon(IntPtr icon, float x, float y, float w, float h, float alpha, int cellsW, int cellsH,
                                   bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (!_ready || icon == IntPtr.Zero || !AurTex.Ready) return;
        Span<int> tids = stackalloc int[MaxLayers];
        int n = Layers(icon, tids);
        IconFrames.Frame f = IconFrames.Of(icon);
        float u1 = ContentFrac(cellsW), v0 = 1f - ContentFrac(cellsH);
        if (f.Known) IconFrames.Fit(f, ref x, ref y, ref w, ref h, out u1, out v0);
        for (int i = 0; i < n; i++) DrawLayer(i, tids[i], x, y, w, h, alpha, u1, v0, clip, cx, cy, cw, ch);
    }

    /// <summary>The art's own frame for an item's icon, or unknown when the item or its icon is gone.</summary>
    internal static IconFrames.Frame FrameOf(IntPtr handle) => _ready && handle != IntPtr.Zero ? IconFrames.Of(ItemIcon(handle)) : default;

    /// <summary>Texels of one inventory cell on a stock icon sheet.</summary>
    private const int IconCellPx = 32;

    /// <summary>The share of a power-of-two padded sheet <paramref name="cells"/> cells fill.</summary>
    internal static float ContentFrac(int cells)
    {
        int px = Math.Max(1, cells) * IconCellPx;
        return px / (float)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)px);
    }

    /// <summary><see cref="Draw(IntPtr, float, float, float, float)"/> into a <paramref name="w"/>×<paramref name="h"/> rect: the
    /// art's own frame at its proportions, centred, or the whole sheet when the frame is unknown.</summary>
    public static void Draw(IntPtr handle, float x, float y, float w, float h, float alpha)
    {
        if (!_ready || handle == IntPtr.Zero || !AurTex.Ready) return;
        IntPtr icon = ItemIcon(handle);
        if (icon == IntPtr.Zero) return;
        Span<int> tids = stackalloc int[MaxLayers];
        int n = Layers(icon, tids);
        IconFrames.Fit(IconFrames.Of(icon), ref x, ref y, ref w, ref h, out float u1, out float v0);
        for (int i = 0; i < n; i++) DrawLayer(i, tids[i], x, y, w, h, alpha, u1, v0);
        if (!_firstDrawLogged && n > 0 && tids[0] != 0 && ExtenderLog.Enabled)
        {
            _firstDrawLogged = true;
            _log?.Info($"[radial] icon first draw kind={Classify(icon)} layers={n} tids={tids[0]},{(n > 1 ? tids[1] : 0)},{(n > 2 ? tids[2] : 0)}");
        }
    }

    /// <summary>The icon's layers as live GL texture ids, bottom to top; returns the layer count (an existing layer
    /// whose texture is not built yet reports 0 and still counts). Plain: the resref. Layered: the resref as a PLT under
    /// the icon's scheme. Composite: three plain resrefs. Armour: up to six PLT resrefs, an empty slot skipped and the
    /// scheme table indexed by the COMPACTED layer, as <c>CArmorIcon::LoadArmorLayeredIconModel</c> @0x58d7c0 does.</summary>
    internal static int Layers(IntPtr icon, Span<int> tids)
    {
        Span<byte> resref = new CGuiIcon(icon).Resref;
        Array.Clear(_cxPlt);
        switch (Classify(icon))
        {
            case IconKind.Layered:
            {
                var layered = new CLayeredIcon(icon);
                tids[0] = TidPlt(resref, (ushort*)layered.Scheme, layered.SchemeCount);
                if (tids[0] == 0) tids[0] = CxPlt(resref, (ushort*)layered.Scheme, layered.SchemeCount, 0);
                return 1;
            }
            case IconKind.Composite:
            {
                var composite = new CCompositeIcon(icon);
                tids[0] = TidPlain(resref);
                tids[1] = TidPlain(composite.Resref2);
                tids[2] = TidPlain(composite.Resref3);
                return 3;
            }
            case IconKind.Armor:
            {
                var armor = new CArmorIcon(icon);
                int count = armor.SchemeCount;
                ushort* shared = (ushort*)armor.Scheme;
                ushort* perLayer = (ushort*)armor.LayerSchemes;
                Span<byte> layers = armor.Resrefs;
                int n = 0;
                for (int slot = 0; slot < EngineLayout.ArmorIconLayers; slot++)
                {
                    Span<byte> name = layers.Slice(slot * EngineLayout.ArmorIconResRefStride, EngineLayout.ArmorIconResRefStride);
                    if (name[0] == 0) continue;
                    ushort* scheme = perLayer != null
                        ? (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(new Span<ushort>(perLayer, (n + 1) * count).Slice(n * count)))
                        : shared;
                    IntPtr tex = FindPlt(name, scheme, count);
                    int tid = tex == IntPtr.Zero ? 0 : AurTex.Tid(tex);
                    // A find MISS is the engine's own skip for a layer it never made a texture for -- except
                    // in DB mode, where it made none at all, so CX's own resolve is asked before skipping.
                    if (tid == 0) tid = CxPlt(name, scheme, count, n);
                    if (tid == 0 && tex == IntPtr.Zero) continue;
                    tids[n++] = tid;
                }
                return n;
            }
            default:
                tids[0] = TidPlain(resref);
                return 1;
        }
    }

    /// <summary>Handle (object id) -&gt; the item's <c>CNWCItem*</c>, or zero: <c>GetGameObject</c>, the type byte, the
    /// <c>AsNWCItem</c> virtual. Every step re-read; nothing held.</summary>
    internal static IntPtr Item(IntPtr handle)
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return IntPtr.Zero;
        var obj = new CGameObject(_getGameObject(app, (uint)(long)handle));
        if (obj.IsNull || obj.Type != GameObjTypeItem) return IntPtr.Zero;
        return obj.AsNWCItem();
    }

    /// <summary>Handle -&gt; the item's current <c>CGuiIcon*</c> (<see cref="Item"/>, then the field).</summary>
    private static IntPtr ItemIcon(IntPtr handle)
    {
        IntPtr item = Item(handle);
        return item == IntPtr.Zero ? IntPtr.Zero : new CNWCItem(item).GuiIcon;
    }

    /// <summary>A plain layer: <c>AurTextureGet(name, 0, 0, 0, 0, false)</c>, the resman's find-or-create by resref
    /// (what the engine's own <c>ReplaceTexture</c> and <see cref="TextRig.ResolveTid"/> do).</summary>
    private static int TidPlain(ReadOnlySpan<byte> resref)
    {
        byte* name = stackalloc byte[ResRefLen + 1];
        if (CName(resref, name) == 0) return 0;
        return AurTex.Tid(_get(name, IntPtr.Zero, null, null, 0, 0));
    }

    private static int TidPlt(ReadOnlySpan<byte> resref, ushort* scheme, int count) => AurTex.Tid(FindPlt(resref, scheme, count));

    /// <summary>The layer CX resolves itself, since the engine has no composited texture for it -- no
    /// <c>CAurTextureBasic</c> is ever built. Returns the PAIR texture's GL name and records this item's scheme as the
    /// rows the fragment stage looks up with; 0 leaves the layer skipped, as a still-loading texture does.</summary>
    private static int CxPlt(ReadOnlySpan<byte> resref, ushort* scheme, int count, int layer)
    {
        if (scheme == null || (uint)count > MaxScheme || (uint)layer >= MaxLayers) return 0;
        if (RadialIconsPlt.Palette == 0) return 0;
        byte* name = stackalloc byte[ResRefLen + 1];
        int n = CName(resref, name);
        if (n == 0) return 0;
        uint tex = RadialIconsPlt.Pair(System.Text.Encoding.ASCII.GetString(name, n));
        if (tex == 0) return 0;
        int at = layer * PltRows;
        for (int i = 0; i < PltRows; i++) _cxRows[at + i] = i < count ? scheme[i] : 0;
        _cxPlt[layer] = true;
        return (int)tex;
    }

    /// <summary>One layer of a drawn icon, through CX's own PLT resolve when <see cref="CxPlt"/> supplied it.</summary>
    private static void DrawLayer(int i, int tid, float x, float y, float w, float h, float alpha,
                                  float u1 = 1f, float v0 = 0f,
                                  bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (i < MaxLayers && _cxPlt[i])
            TextRig.DrawIconPlt(tid, RadialIconsPlt.Palette, new ReadOnlySpan<int>(_cxRows, i * PltRows, PltRows),
                                x, y, w, h, alpha, u1, v0, clip, cx, cy, cw, ch);
        else
            TextRig.DrawIconTid(tid, x, y, w, h, alpha, u1, v0, clip, cx, cy, cw, ch);
    }

    /// <summary>A PLT layer: <c>AurTextureFind(name, name, scheme, count, false)</c> -- the hashed (pltName, scheme)
    /// lookup, scheme widened <c>ushort -&gt; int</c> as <c>Gob::ReplaceTexture</c> @0x43eb30 widens it. Find, never get:
    /// a get would mint a scheme-less duplicate every frame.</summary>
    private static IntPtr FindPlt(ReadOnlySpan<byte> resref, ushort* scheme, int count)
    {
        if (scheme == null || (uint)count > MaxScheme) return IntPtr.Zero;
        byte* name = stackalloc byte[ResRefLen + 1];
        if (CName(resref, name) == 0) return IntPtr.Zero;
        int* ints = stackalloc int[MaxScheme];
        for (int i = 0; i < count; i++) ints[i] = scheme[i];
        return _find(name, name, ints, count, 0);
    }

    /// <summary>Copy a 16-byte <c>CResRef</c> into a NUL-terminated stack buffer; returns its length.</summary>
    internal static int CName(ReadOnlySpan<byte> resref, byte* dst)
    {
        int n = 0;
        while (n < ResRefLen && resref[n] != 0) { dst[n] = resref[n]; n++; }
        dst[n] = 0;
        return n;
    }

    /// <summary>The item's display name through <c>GetObjectName</c> (cold; the DialogService contract: zeroed
    /// CExoString in, its buffer freed by the CExoString dtor after the copy).</summary>
    internal static string ObjectName(uint id)
    {
        if (_getObjectName == null || _exoDtor == null) return "";
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return "";
        IntPtr app = *(IntPtr*)mgr; if (app == IntPtr.Zero) return "";
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;
        _getObjectName(app, id, (IntPtr)exo);
        string name = Native.ReadExo((IntPtr)exo);
        _exoDtor((IntPtr)exo);
        return name.Trim();
    }

    // ---- self-check ----------------------------------------------------------------------------

    private static int _fakeFinds, _fakeGets, _fakeLastCount;
    private static readonly int[] _fakeLastScheme = new int[MaxScheme];
    private static readonly byte[] _fakeLastName = new byte[ResRefLen + 1];
    private static readonly byte[] _fakeMissName = new byte[ResRefLen + 1];
    private static IntPtr _fakeTex;

    private static bool SameName(byte* a, byte[] b) { for (int i = 0; i <= ResRefLen; i++) { if (a[i] != b[i]) return false; if (a[i] == 0) return true; } return true; }

    [UnmanagedCallersOnly]
    private static IntPtr FakeFind(byte* name, byte* plt, int* scheme, int count, byte byName)
    {
        _fakeFinds++;
        for (int i = 0; i <= ResRefLen; i++) { _fakeLastName[i] = name[i]; if (name[i] == 0) break; }
        _fakeLastCount = count;
        for (int i = 0; i < count && i < MaxScheme; i++) _fakeLastScheme[i] = scheme[i];
        return byName != 0 || plt != name || SameName(name, _fakeMissName) ? IntPtr.Zero : _fakeTex;
    }

    [UnmanagedCallersOnly]
    private static IntPtr FakeGet(byte* name, IntPtr tex, byte* plt, int* scheme, int count, byte byName)
    {
        _fakeGets++;
        for (int i = 0; i <= ResRefLen; i++) { _fakeLastName[i] = name[i]; if (name[i] == 0) break; }
        return plt == null && scheme == null && count == 0 ? _fakeTex : IntPtr.Zero;
    }

    private static void Put(Span<byte> dst, string s) { for (int i = 0; i < s.Length; i++) dst[i] = (byte)s[i]; dst[s.Length] = 0; }
    private static string LastName() { int n = 0; while (n < ResRefLen && _fakeLastName[n] != 0) n++; return System.Text.Encoding.ASCII.GetString(_fakeLastName, 0, n); }

    /// <summary>Off-device: the vptr classification and the layer walk over fake icon blobs laid out per the resolved
    /// <see cref="EngineLayout"/> rows, with the two resman calls swapped for managed fakes that record (name, scheme)
    /// -- plain = one get; layered = one hashed find; composite = three gets in 01/02/03 order; armour = an empty slot
    /// skipped and a find miss NOT advancing the scheme index. Skipped when <see cref="AurTex"/> is already armed.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        IconFrames.SelfCheck(log);
        if (AurTex.Ready) { log.Info("RadialIcons.SelfCheck: skipped (AurTex armed -- the fake walk would count refusals)"); return; }
        var check = new Checker(log);

        var savedFind = _find; var savedGet = _get; bool savedReady = _ready;
        IntPtr sl = _vtLayered, sc = _vtComposite, sa = _vtArmor;
        var vt = (FakeVtables*)NativeMemory.AllocZeroed((nuint)sizeof(FakeVtables));
        var icon = (FakeIcon*)NativeMemory.AllocZeroed((nuint)sizeof(FakeIcon));
        ushort* scheme = (ushort*)NativeMemory.AllocZeroed(2 * MaxScheme * MaxLayers);
        ushort* perLayer = (ushort*)NativeMemory.AllocZeroed(2 * MaxScheme * MaxLayers);
        Span<int> tids = stackalloc int[MaxLayers];
        try
        {
            _find = &FakeFind; _get = &FakeGet; _ready = true;
            _fakeTex = (IntPtr)vt;   // any non-null: AurTex is unarmed headless, so Tid() answers 0 and only the walk is checked
            _vtLayered = (IntPtr)vt->Layered; _vtComposite = (IntPtr)vt->Composite; _vtArmor = (IntPtr)vt->Armor;
            Put(_fakeMissName, "no_such_plt");

            icon->Vptr = (IntPtr)vt->Plain;
            Put(new CGuiIcon((IntPtr)icon).Resref, "isk_pocket");
            check.Expect(Classify((IntPtr)icon) == IconKind.Plain, "an unlisted vptr is Plain");
            _fakeGets = _fakeFinds = 0;
            check.Expect(Layers((IntPtr)icon, tids) == 1 && _fakeGets == 1 && _fakeFinds == 0 && LastName() == "isk_pocket", $"plain = one get by resref ({LastName()})");

            icon->Vptr = _vtLayered;
            for (int i = 0; i < 10; i++) scheme[i] = (ushort)(i * 17);
            var layered = new CLayeredIcon((IntPtr)icon);
            layered.SchemeCount = 10;
            layered.Scheme = (IntPtr)scheme;
            _fakeGets = _fakeFinds = 0;
            check.Expect(Classify((IntPtr)icon) == IconKind.Layered && Layers((IntPtr)icon, tids) == 1 && _fakeFinds == 1 && _fakeGets == 0
              && _fakeLastCount == 10 && _fakeLastScheme[9] == 153 && LastName() == "isk_pocket", "layered = one hashed find, scheme widened");
            layered.SchemeCount = MaxScheme + 1;
            _fakeFinds = 0;
            check.Expect(Layers((IntPtr)icon, tids) == 1 && _fakeFinds == 0 && tids[0] == 0, "an over-long scheme is refused, not truncated");

            icon->Vptr = _vtComposite;
            var composite = new CCompositeIcon((IntPtr)icon);
            Put(composite.Resref2, "iwaxe_m_1");
            Put(composite.Resref3, "iwaxe_t_1");
            _fakeGets = _fakeFinds = 0;
            check.Expect(Classify((IntPtr)icon) == IconKind.Composite && Layers((IntPtr)icon, tids) == 3 && _fakeGets == 3 && _fakeFinds == 0
              && LastName() == "iwaxe_t_1", "composite = three gets, 01 -> 03");

            NativeMemory.Clear(icon->Body, 0xf8);
            icon->Vptr = _vtArmor;
            var armor = new CArmorIcon((IntPtr)icon);
            Span<byte> layers = armor.Resrefs;
            Put(layers[(0 * EngineLayout.ArmorIconResRefStride)..], "arm_a");
            Put(layers[(2 * EngineLayout.ArmorIconResRefStride)..], "no_such_plt");
            Put(layers[(3 * EngineLayout.ArmorIconResRefStride)..], "arm_d");
            armor.SchemeCount = 10;
            armor.Scheme = (IntPtr)scheme;
            for (int i = 0; i < 30; i++) perLayer[i] = (ushort)(100 + i);
            armor.LayerSchemes = (IntPtr)perLayer;
            _fakeGets = _fakeFinds = 0;
            int n = Layers((IntPtr)icon, tids);
            check.Expect(Classify((IntPtr)icon) == IconKind.Armor && n == 2 && _fakeFinds == 3 && _fakeGets == 0,
              $"armour: empty slot skipped, three finds, two layers ({n}, finds={_fakeFinds})");
            check.Expect(LastName() == "arm_d" && _fakeLastScheme[0] == 110, $"the miss did not advance the per-layer scheme index (row {_fakeLastScheme[0] - 100})");
            armor.LayerSchemes = IntPtr.Zero;
            Layers((IntPtr)icon, tids);
            check.Expect(_fakeLastScheme[9] == 153, "no per-layer table: every layer takes the shared scheme");
        }
        finally
        {
            _find = savedFind; _get = savedGet; _ready = savedReady;
            _vtLayered = sl; _vtComposite = sc; _vtArmor = sa; _fakeTex = IntPtr.Zero;
            NativeMemory.Free(vt); NativeMemory.Free(icon); NativeMemory.Free(scheme); NativeMemory.Free(perLayer);
        }
        log.Info(check.Ok ? "RadialIcons.SelfCheck: passed" : "RadialIcons.SelfCheck: FAILED");
    }

    private unsafe struct FakeVtables
    {
        public fixed byte Header[0x10];
        public fixed byte Layered[0x10];
        public fixed byte Composite[0x10];
        public fixed byte Armor[0x10];
        public fixed byte Plain[0x10];
        public fixed byte Rest[0xb0];
    }

    private unsafe struct FakeIcon
    {
        public IntPtr Vptr;
        public fixed byte Body[0xf8];
    }
}
