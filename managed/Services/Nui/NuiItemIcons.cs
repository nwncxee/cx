using System;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Radial;

namespace CX.Services.Nui;

/// <summary>
/// <c>"@item:&lt;hex&gt;"</c> as any NUI image draws that item's own inventory icon, every layer in the item's real
/// colours. The hex is NWScript's <c>ObjectToString</c> (lowercase, 1-8 digits); anything after it is ignored, so a
/// suffix bump gets past the per-window image cache. <see cref="SymbolMap.NkLoadImg"/> composites the layers into a
/// TGA under a synthetic resref the <see cref="SymbolMap.ExoResManGet"/> hook answers once. Cold, so it may allocate.
/// </summary>
internal static unsafe class NuiItemIcons
{
    private const ushort ResTga = 3, ResAny = ushort.MaxValue;
    private const int RefLen = RadialIcons.ResRefLen, HexDigits = 8, LogCap = 20;
    private const int TgaHeader = 18;
    /// <summary><c>CGameObjectArray::LISTTYPE_MASK</c> (rodata 0x13edefc): <c>CNWSMessage::WriteOBJECTIDServer</c> @0xb50fc0 ORs it
    /// into every valid id it sends, and the client keeps ids as read, so the server's <c>ObjectToString</c> value
    /// <c>e04</c> is client object <c>0x80000e04</c>. The wire format, so the same on every build.</summary>
    internal const uint ServerIdBit = 0x80000000;  // not-an-offset: object id bit
    /// <summary>A master-palette row is 256 RGBA entries: <c>ImageDecode</c> addresses row r at <c>r &lt;&lt; 10</c>.</summary>
    private const int PaletteRowShift = 10;
    /// <summary>std::string {data, size, capacity} as <c>DataBlock::FromBase64</c> reads it: libstdc++ size at +8;
    /// MSVC size at +0x10 and capacity at +0x18, whose data field is a pointer only when capacity >= 16.</summary>
    private const int StrLenGnu = 8, StrLenMsvc = 0x10, StrCapMsvc = 0x18, StrBytes = 32;  // not-an-offset: std::string ABI, not an engine class

    private static ExtenderLog? _log;
    private static Knob<bool>? _enabled, _on;
    private static bool _ready;
    private static NkLoadImgFn _loadOrig;
    private static IntPtr _getOrig;
    private static AurGetLayeredRasterWrapperFn _getPlt;
    private static AurGetImageWrapperFn _getImage;
    private static DataBlockFromBase64Fn _fromBase64;
    private static IntPtr* _releasePlt, _releaseRaster;
    private static byte** _palette;
    private static int* _paletteRows;
    private static IntPtr _rules;
    /// <summary>The synthetic resref (NUL-padded) and base64 TGA of the one nk_loadimg call in flight: set by
    /// <see cref="OnLoadImg"/>, answered by the Get it makes, synchronously, then cleared.</summary>
    private static byte* _ref;
    private static byte[]? _tga, _b64;
    private static int _b64Len;
    private static uint[]? _canvas, _layer;
    private static int _w, _h, _logged;
    private static NwcCreateItemFn _createItem;
    private static IntPtr _identity;

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _enabled = NuiKnobs.Enabled;
        _on = NuiKnobs.ItemIcons;
        Capabilities.Register("itemicon", 1, static () => _ready && _enabled!.V && _on!.V);
        Capabilities.Register("lookicon", 1, static () => _ready && _createItem != null && _enabled!.V && _on!.V);
        _enabled.Changed += Capabilities.Refresh;
        _on.Changed += Capabilities.Refresh;
        if (!RadialIcons.Init(log)
            || !SymbolMap.Require(log, "NuiItemIcons", SymbolMap.NkLoadImg, SymbolMap.ExoResManGet, SymbolMap.DataBlockFromBase64,
                                  SymbolMap.AurGetLayeredRasterWrapper, SymbolMap.AurReleaseLayeredRaster, SymbolMap.PaletteImage,
                                  SymbolMap.PaletteHeight, SymbolMap.AurGetImageWrapper, SymbolMap.AurReleaseRaster,
                                  SymbolMap.Rules, SymbolMap.GetBaseItem)
            || !EngineLayout.Require(log, "NuiItemIcons", "CNWRules", "CNWBaseItem", "CNWCItem"))
        {
            Fail("its engine symbols or offsets did not resolve");
            return;
        }
        _getPlt = EngineFn.AurGetLayeredRasterWrapper;
        _getImage = EngineFn.AurGetImageWrapper;
        _fromBase64 = EngineFn.DataBlockFromBase64;
        _releasePlt = (IntPtr*)Native.Sym(SymbolMap.AurReleaseLayeredRaster);
        _releaseRaster = (IntPtr*)Native.Sym(SymbolMap.AurReleaseRaster);
        _palette = (byte**)Native.Sym(SymbolMap.PaletteImage);
        _paletteRows = (int*)Native.Sym(SymbolMap.PaletteHeight);
        _rules = Native.Sym(SymbolMap.Rules);
        if (SymbolMap.Require(log, "NuiItemIcons @look:", SymbolMap.NwcCreateItem, SymbolMap.IdentityVisualTransform))
        {
            _createItem = EngineFn.NwcCreateItem;
            _identity = Native.Sym(SymbolMap.IdentityVisualTransform);
        }
        else Notice.Show(log, "NUI @look: icons are off: CNWCMessage::CreateItem did not resolve (store stock shows stand-ins)");
        _ref = (byte*)NativeMemory.AllocZeroed(RefLen + 1);
        _getOrig = OperatingSystem.IsWindows()
            ? hooks.Install(SymbolMap.ExoResManGet, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte*, ushort, IntPtr>)&OnGetWin64)
            : hooks.Install(SymbolMap.ExoResManGet, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte*, ushort, IntPtr>)&OnGetItanium);
        _loadOrig = hooks.InstallNkLoadImg(&OnLoadImg);
        _ready = _getOrig != IntPtr.Zero && _loadOrig != null;
        if (!_ready) Fail("nk_loadimg or CExoResMan::Get did not hook");
    }

    private static void Fail(string why)
    {
        _log?.Always($"NuiItemIcons: {why} -- @item: NUI icons stand down (the engine's error sprite shows)");
        Notice.Show(_log, "NUI @item: icons are off: " + why);
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnLoadImg(IntPtr ret, byte* resref, ushort type)
    {
        if (_ready && resref != null && resref[0] == (byte)'@' && NuiModelView.IsModelName(resref))
        {
            if (!NuiModelView.Prepare(resref)) return _loadOrig(ret, resref, type);
            IntPtr m = _loadOrig(ret, _ref, ResTga);
            _b64Len = 0;
            NuiModelView.Loaded(resref, ret);
            return m;
        }
        if (!_ready || resref == null || resref[0] != (byte)'@' || !_enabled!.V || !_on!.V || !Prepare(resref, out ushort t))
            return _loadOrig(ret, resref, type);
        IntPtr r = _loadOrig(ret, _ref, t);
        _b64Len = 0;
        return r;
    }

    /// <summary>Itanium order for a member returning by value: (ret, this, resref, type).</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetItanium(IntPtr ret, IntPtr self, byte* resref, ushort type) =>
        Ours(resref, type) ? Answer(ret) : ((delegate* unmanaged<IntPtr, IntPtr, byte*, ushort, IntPtr>)_getOrig)(ret, self, resref, type);

    /// <summary>Win64 order for a member returning by value: (this, ret, resref, type).</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetWin64(IntPtr self, IntPtr ret, byte* resref, ushort type) =>
        Ours(resref, type) ? Answer(ret) : ((delegate* unmanaged<IntPtr, IntPtr, byte*, ushort, IntPtr>)_getOrig)(self, ret, resref, type);

    private static bool Ours(byte* resref, ushort type)
    {
        if (_b64Len == 0 || type != ResTga || resref == null) return false;
        for (int i = 0; i < RefLen; i++) if (resref[i] != _ref[i]) return false;
        return true;
    }

    /// <summary>The pending TGA as the <c>shared_ptr&lt;DataBlock&gt;</c> the engine asked for, through the engine's own
    /// <c>FromBase64</c> (which adopts its decode, so the block frees itself). The string header only points at ours.</summary>
    private static IntPtr Answer(IntPtr ret)
    {
        byte* str = stackalloc byte[StrBytes];
        NativeMemory.Clear(str, StrBytes);
        fixed (byte* b = _b64)
        {
            *(byte**)str = b;
            if (OperatingSystem.IsWindows()) { *(ulong*)(str + StrLenMsvc) = (ulong)_b64Len; *(ulong*)(str + StrCapMsvc) = (ulong)_b64Len; }
            else *(ulong*)(str + StrLenGnu) = (ulong)_b64Len;
            return _fromBase64(ret, (IntPtr)str);
        }
    }

    /// <summary>Resolve <paramref name="s"/> into what the original nk_loadimg should load, left in <see cref="_ref"/>: a
    /// synthetic resref with its TGA pending (type TGA), or a plain unpadded icon's own resref (type ANY, the engine's
    /// own image). False = not an @item/@look string, or the icon would not build: the original string loads as it would.</summary>
    private static bool Prepare(byte* s, out ushort type)
    {
        type = ResTga;
        if (Parse(s, out uint oid))
        {
            IntPtr item = RadialIcons.Item((IntPtr)(oid | ServerIdBit));
            IntPtr icon = item == IntPtr.Zero ? IntPtr.Zero : new CNWCItem(item).GuiIcon;
            if (icon != IntPtr.Zero) return FromIcon(item, icon, $"item {oid:x}", out type);
            Log(item == IntPtr.Zero
                ? $"@item:{oid:x} is not an item this client knows -- the NUI image is left empty"
                : $"item {oid:x} has no inventory icon yet -- the NUI image is left empty");
            _w = _h = 1;
            Grow(1);
            _canvas![0] = 0;
            Emit(1, 1);
            SetRef("cxnoitem");
            return true;
        }
        Look look;
        if (!ParseLook(s, &look)) return false;
        IntPtr minted = Mint(&look);
        if (minted == IntPtr.Zero)
        {
            Log($"@look: base item {look.BaseItem} made no item -- the NUI image shows the engine's error sprite");
            return false;
        }
        try
        {
            IntPtr icon = new CNWCItem(minted).GuiIcon;
            return icon != IntPtr.Zero && FromIcon(minted, icon, $"@look: base item {look.BaseItem}", out type);
        }
        finally
        {
            Destroy(minted);
        }
    }

    /// <summary>The composite of <paramref name="icon"/> cut to its art's own frame (the icon of <paramref name="item"/>, whose
    /// base item gives the cells when the frame is unknown), or a plain icon's own resref when its art fills the sheet.</summary>
    private static bool FromIcon(IntPtr item, IntPtr icon, string what, out ushort type)
    {
        type = ResTga;
        Cells(item, out int cw, out int ch);
        float fw = RadialIcons.ContentFrac(cw), fh = RadialIcons.ContentFrac(ch);
        IconFrames.Frame f = IconFrames.Of(icon);
        if (f.Known) { fw = f.Cw / (float)f.Tw; fh = f.Ch / (float)f.Th; }
        IconKind kind = RadialIcons.Classify(icon);
        if (kind == IconKind.Plain && fw >= 1f && fh >= 1f)
        {
            RadialIcons.CName(new CGuiIcon(icon).Resref, _ref);
            type = ResAny;
            return true;
        }
        if (!Composite(icon, kind))
        {
            Log($"{what}'s {kind} icon has no layer the engine can read uncompressed -- the NUI image shows the engine's error sprite");
            return false;
        }
        Emit(Math.Max(1, (int)MathF.Round(_w * fw)), Math.Max(1, (int)MathF.Round(_h * fh)));
        SetRef($"cx{Hash():x8}");
        return true;
    }

    /// <summary>The TGA a model view's image loads as: the stand-in <paramref name="standin"/> (a resref, empty for none), cut to
    /// <paramref name="crop"/> when that fits it, at its own size, or stretched to <paramref name="w"/> x <paramref name="h"/> when the server declared that size (<paramref name="sized"/>),
    /// else <paramref name="w"/> x <paramref name="h"/> fully transparent. Leaves the synthetic resref in <see cref="_ref"/>
    /// and the pixels (RGBA, R low) and size in <paramref name="pixels"/>.</summary>
    internal static void ModelImage(ReadOnlySpan<byte> standin, int w, int h, bool sized, StandinRect crop, string key, out uint[] pixels, out int pw, out int ph)
    {
        _w = _h = 0;
        if (standin.Length > 0 && standin[0] != 0) AddPlain(standin);
        if (_w > 0 && crop.Fits(_w, _h))
        {
            uint[] cut = crop.Cut(_canvas!, _w, _h);
            _w = crop.W; _h = crop.H;
            Grow(_w * _h);
            Array.Copy(cut, _canvas!, cut.Length);
        }
        if (_w == 0) { _w = w; _h = h; Grow(w * h); Array.Clear(_canvas!, 0, w * h); }
        else if (sized && (_w != w || _h != h))
        {
            uint[] src = new uint[_w * _h];
            Array.Copy(_canvas!, src, src.Length);
            int sw = _w, sh = _h;
            _w = w; _h = h;
            Grow(w * h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) _canvas![y * w + x] = src[(y * sh / h) * sw + x * sw / w];
        }
        Emit(_w, _h);
        SetRef(ModelRef(Hash(), key));
        pw = _w; ph = _h;
        pixels = new uint[pw * ph];
        Array.Copy(_canvas!, pixels, pixels.Length);
    }

    /// <summary>An item's appearance, as an engine store sends it and <see cref="SymbolMap.NwcCreateItem"/> takes it.</summary>
    private struct Look
    {
        public uint BaseItem;
        public byte Gender, VisualEffect;
        public fixed ushort Models[LookModels];
        public fixed byte Colors[LookColors];
        public fixed byte PartColors[LookPartColors];
    }

    private const int LookModels = 19, LookColors = 6, LookPartColors = LookModels * LookColors;

    /// <summary>
    /// <c>"@look:"</c> then hex in <c>AddItemAppearanceToMessage</c>'s field order: base item (4 digits), gender (2,
    /// <c>gender.2da</c>'s <c>GENDER</c> column), visual effect (2), 19 models (4 each: a simple or layered item at
    /// [0], a composite weapon's bottom/middle/top at [0..2], armor's 19 parts), 6 colours (2 each: leather, cloth,
    /// metal), then optionally 114 part colours (part*6 + channel, 2 each; <c>ff</c> = base). Nothing may follow.
    /// </summary>
    private static bool ParseLook(byte* s, Look* look)
    {
        ReadOnlySpan<byte> prefix = "@look:"u8;
        for (int i = 0; i < prefix.Length; i++) if (s[i] != prefix[i]) return false;
        byte* p = s + prefix.Length;
        if (!Hex(ref p, 4, out uint v)) return false;
        look->BaseItem = v;
        if (!Hex(ref p, 2, out v)) return false;
        look->Gender = (byte)v;
        if (!Hex(ref p, 2, out v)) return false;
        look->VisualEffect = (byte)v;
        for (int i = 0; i < LookModels; i++)
        {
            if (!Hex(ref p, 4, out v)) return false;
            look->Models[i] = (ushort)v;
        }
        for (int i = 0; i < LookColors; i++)
        {
            if (!Hex(ref p, 2, out v)) return false;
            look->Colors[i] = (byte)v;
        }
        bool parts = *p != 0;
        for (int i = 0; i < LookPartColors; i++)
        {
            if (parts && !Hex(ref p, 2, out v)) return false;
            look->PartColors[i] = parts ? (byte)v : (byte)0xff;
        }
        return *p == 0;
    }

    private static bool Hex(ref byte* p, int digits, out uint value)
    {
        value = 0;
        for (int i = 0; i < digits; i++, p++)
        {
            int d = *p is >= (byte)'0' and <= (byte)'9' ? *p - '0' : (*p | 0x20) is >= 'a' and <= 'f' ? (*p | 0x20) - 'a' + 10 : -1;
            if (d < 0) return false;
            value = value << 4 | (uint)d;
        }
        return true;
    }

    /// <summary>A throwaway item for an <c>@look:</c> string, or zero when it does not parse or the engine makes none.
    /// The caller owns it and ends it with <see cref="Destroy"/>.</summary>
    internal static IntPtr MintLook(string text)
    {
        byte* b = stackalloc byte[512];
        if (text.Length >= 511) return IntPtr.Zero;
        for (int i = 0; i < text.Length; i++) b[i] = (byte)text[i];
        b[text.Length] = 0;
        Look look;
        return ParseLook(b, &look) ? Mint(&look) : IntPtr.Zero;
    }

    /// <summary>A throwaway item for a doll's <c>@look:</c> string, made as <see cref="MintLook"/> makes one but only when the client has the base
    /// item and the files the engine would load for its models. The engine meets a missing model file with the base item's default model (a
    /// leather bag for a sword) and a cloak number with no row in its table with a table read past the end. Zero, with the reason logged once, when
    /// the text does not parse or a check fails; <paramref name="why"/> says which (empty when the engine made none).</summary>
    internal static IntPtr MintLookForModel(string text, out string why)
    {
        why = "";
        byte* b = stackalloc byte[512];
        if (text.Length >= 511) { why = "longer than 510 characters"; return IntPtr.Zero; }
        for (int i = 0; i < text.Length; i++) b[i] = (byte)text[i];
        b[text.Length] = 0;
        Look look;
        if (!ParseLook(b, &look)) { why = "not an @look: string of hex fields"; return IntPtr.Zero; }
        if (!LookModelsExist(&look, out why))
        {
            if (NuiModelGuard.Once(text)) Log($"@look: {why} -- the item is left out");
            return IntPtr.Zero;
        }
        return Mint(&look);
    }

    /// <summary>Does the client have <paramref name="look"/>'s base item, a model file for each part the base item's model type loads (a cloak's
    /// number is a row of its own table instead), and the item visual effect it names? The reason when not.</summary>
    private static bool LookModelsExist(Look* look, out string why)
    {
        why = "";
        IntPtr rules = *(IntPtr*)_rules;
        if (rules == IntPtr.Zero) return true;
        var b = new CNWBaseItem(EngineFn.GetBaseItem(new CNWRules(rules).BaseItems, (int)look->BaseItem));
        if (b.IsNull) { why = $"base item {look->BaseItem} is not in the client's baseitems.2da"; return false; }
        ReadOnlySpan<ushort> models = new(look->Models, LookModels);
        if (look->VisualEffect != 0 && !NuiModelGuard.RowInTable(look->VisualEffect - 1, NuiModelTarget.ItemVisualEffectRows()))
        { why = $"item visual effect {look->VisualEffect} is past the rows the client's item visual effects table has"; return false; }
        if (look->BaseItem == NuiModelGuard.CloakBaseItem)
        {
            if (NuiModelGuard.RowInTable(models[0], NuiModelTarget.CloakModelRows())) return true;
            why = $"cloak model {models[0]} is past the rows the client's cloakmodel.2da has";
            return false;
        }
        var names = new string[3];
        int n = NuiModelGuard.ItemModels(b.ModelType, ClassOf(b), models, names);
        for (int i = 0; i < n; i++)
            if (!CX.Alloy.ModelFeed.Present(names[i])) { why = $"the client has no model '{names[i]}' for base item {look->BaseItem}"; return false; }
        return true;
    }

    /// <summary>The <c>ItemClass</c> prefix of a base item's model names, up to its end mark.</summary>
    private static string ClassOf(CNWBaseItem b)
    {
        Span<byte> raw = b.ItemClass;
        int len = raw.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(raw[..(len < 0 ? raw.Length : len)]);
    }

    /// <summary>The model files <paramref name="item"/> was built from, as <see cref="NuiModelGuard.ItemModels"/> names them, up to <c>into.Length</c> (three) of them.
    /// None for armour, or when the client has no base item for it.</summary>
    internal static int ModelNamesOf(IntPtr item, string[] into)
    {
        IntPtr rules = *(IntPtr*)_rules;
        if (rules == IntPtr.Zero || item == IntPtr.Zero) return 0;
        var v = new CNWCItem(item);
        var b = new CNWBaseItem(EngineFn.GetBaseItem(new CNWRules(rules).BaseItems, v.BaseItem));
        return b.IsNull ? 0 : NuiModelGuard.ItemModels(b.ModelType, ClassOf(b), v.Models, into);
    }

    /// <summary>The 114 per-part colours an <c>@look:</c> string carries into <paramref name="dst"/> (0xff = the base colour).
    /// False when the text does not parse or <paramref name="dst"/> is shorter than that.</summary>
    internal static bool ReadLookPartColors(string text, Span<byte> dst)
    {
        byte* b = stackalloc byte[512];
        if (text.Length >= 511 || dst.Length < LookPartColors) return false;
        for (int i = 0; i < text.Length; i++) b[i] = (byte)text[i];
        b[text.Length] = 0;
        Look look;
        if (!ParseLook(b, &look)) return false;
        new Span<byte>(look.PartColors, LookPartColors).CopyTo(dst);
        return true;
    }

    /// <summary>A throwaway item that looks like <paramref name="item"/>: its base item, model parts, colours and gender read,
    /// every per-part colour left at the base. Zero when the engine makes none. The caller ends it with <see cref="Destroy"/>.</summary>
    internal static IntPtr MintCopy(IntPtr item)
    {
        if (item == IntPtr.Zero) return IntPtr.Zero;
        var v = new CNWCItem(item);
        Look look = default;
        look.BaseItem = (uint)v.BaseItem;
        look.Gender = v.Gender;
        v.Models.CopyTo(new Span<ushort>(look.Models, LookModels));
        v.Colors.CopyTo(new Span<byte>(look.Colors, LookColors));
        for (int i = 0; i < LookPartColors; i++) look.PartColors[i] = 0xff;
        return Mint(&look);
    }

    /// <summary>The <c>INVENTORY_SLOT_*</c> bits the item's base item can sit in, or zero.</summary>
    internal static uint EquipMask(IntPtr item)
    {
        IntPtr rules = *(IntPtr*)_rules;
        if (rules == IntPtr.Zero || item == IntPtr.Zero) return 0;
        var b = new CNWBaseItem(EngineFn.GetBaseItem(new CNWRules(rules).BaseItems, new CNWCItem(item).BaseItem));
        return b.IsNull ? 0 : b.EquipSlots;
    }

    /// <summary>A throwaway client item with that appearance, icon built: the engine's own store path, minus the game-object
    /// array. <see cref="Destroy"/> frees it.</summary>
    private static IntPtr Mint(Look* look)
    {
        if (_createItem == null || _identity == IntPtr.Zero) return IntPtr.Zero;
        return _createItem(IntPtr.Zero, look->BaseItem, look->Models, look->Colors, look->PartColors,
                           (sbyte)look->Gender, look->VisualEffect, _identity);
    }

    /// <summary>The engine's deleting destructor through the item's own vtable: Itanium's D0 takes only <c>this</c>, MSVC's
    /// scalar deleting destructor also the delete flag.</summary>
    internal static void Destroy(IntPtr item)
    {
        IntPtr dtor = new CNWCItem(item).DeleteFn;
        if (OperatingSystem.IsWindows()) ((delegate* unmanaged<IntPtr, uint, IntPtr>)dtor)(item, 1);
        else ((delegate* unmanaged<IntPtr, void>)dtor)(item);
    }

    /// <summary><c>"@item:"</c> then 1-8 hex digits; whatever follows is ignored.</summary>
    internal static bool Parse(byte* s, out uint oid)
    {
        oid = 0;
        ReadOnlySpan<byte> prefix = "@item:"u8;
        for (int i = 0; i < prefix.Length; i++) if (s[i] != prefix[i]) return false;
        int n = 0;
        for (byte* p = s + prefix.Length; n < HexDigits; p++, n++)
        {
            int d = *p is >= (byte)'0' and <= (byte)'9' ? *p - '0' : (*p | 0x20) is >= 'a' and <= 'f' ? (*p | 0x20) - 'a' + 10 : -1;
            if (d < 0) break;
            oid = oid << 4 | (uint)d;
        }
        return n > 0;
    }

    internal static void Cells(IntPtr item, out int cw, out int ch)
    {
        cw = ch = 1;
        IntPtr rules = *(IntPtr*)_rules;
        if (rules == IntPtr.Zero) return;
        var b = new CNWBaseItem(EngineFn.GetBaseItem(new CNWRules(rules).BaseItems, new CNWCItem(item).BaseItem));
        if (b.IsNull) return;
        cw = Math.Max(1, (int)b.InvSlotWidth);
        ch = Math.Max(1, (int)b.InvSlotHeight);
    }

    /// <summary>Every layer of the icon, bottom to top, into <see cref="_canvas"/> (the first layer read sets the size) --
    /// the walk <see cref="RadialIcons.Layers"/> makes, reading pixels instead of finding textures. Armour's per-layer
    /// scheme index advances only on a PLT that exists, as <c>CArmorIcon::LoadArmorLayeredIconModel</c> advances it.</summary>
    private static bool Composite(IntPtr icon, IconKind kind)
    {
        _w = _h = 0;
        Span<byte> resref = new CGuiIcon(icon).Resref;
        switch (kind)
        {
            case IconKind.Layered:
                var layered = new CLayeredIcon(icon);
                AddPlt(resref, (ushort*)layered.Scheme, layered.SchemeCount);
                break;
            case IconKind.Composite:
                var composite = new CCompositeIcon(icon);
                AddPlain(resref);
                AddPlain(composite.Resref2);
                AddPlain(composite.Resref3);
                break;
            case IconKind.Armor:
                var armor = new CArmorIcon(icon);
                int count = armor.SchemeCount, k = 0;
                ushort* shared = (ushort*)armor.Scheme, perLayer = (ushort*)armor.LayerSchemes;
                Span<byte> names = armor.Resrefs;
                for (int slot = 0; slot < EngineLayout.ArmorIconLayers; slot++)
                {
                    Span<byte> name = names.Slice(slot * EngineLayout.ArmorIconResRefStride, EngineLayout.ArmorIconResRefStride);
                    if (name[0] != 0 && AddPlt(name, perLayer != null ? perLayer + k * count : shared, count)) k++;
                }
                break;
            default:
                AddPlain(resref);
                break;
        }
        return _w > 0;
    }

    /// <summary>One PLT layer through the engine's reader and <c>ImageDecode</c>'s lookup (<c>palette[(scheme[layer] &lt;&lt; 10)
    /// + index*4]</c>), a pixel whose layer or row is out of range left transparent where the engine would read stale
    /// memory. True when the PLT exists.</summary>
    private static bool AddPlt(ReadOnlySpan<byte> name, ushort* scheme, int count)
    {
        if (scheme == null || (uint)count > RadialIcons.MaxScheme) return false;
        byte* c = stackalloc byte[RefLen + 1];
        if (RadialIcons.CName(name, c) == 0) return false;
        int w, h, bpp;
        byte* px = _getPlt(c, &w, &h, &bpp);
        if (px == null) return false;
        if (Fit(w, h, c))
        {
            byte* pal = *_palette;
            int rows = *_paletteRows;
            uint[] dst = _layer!;
            for (int i = 0; i < w * h; i++)
            {
                int layer = px[2 * i + 1];
                int row = layer < count ? scheme[layer] : -1;
                dst[i] = pal != null && (uint)row < (uint)rows ? *(uint*)(pal + (row << PaletteRowShift) + px[2 * i] * 4) : 0;
            }
            Blend(w * h);
        }
        ((delegate* unmanaged<byte*, int, int, void>)*_releasePlt)(px, w, h);
        return true;
    }

    /// <summary>One plain layer, uncompressed through the engine's reader (TGA/4PC; a DDS-only one is skipped).</summary>
    private static void AddPlain(ReadOnlySpan<byte> name)
    {
        byte* c = stackalloc byte[RefLen + 1];
        if (RadialIcons.CName(name, c) == 0) return;
        int w, h, bpp, size, container, format;
        float alpha = 0;
        byte* px = (byte*)_getImage((IntPtr)c, &w, &h, &bpp, &size, &container, &format, &alpha, 0);
        if (px == null)
        {
            Log($"{Marshal.PtrToStringAnsi((IntPtr)c)} has no uncompressed image (DDS-only?) -- that layer is left out");
            return;
        }
        if ((bpp == 3 || bpp == 4) && Fit(w, h, c))
        {
            uint[] dst = _layer!;
            for (int i = 0; i < w * h; i++)
            {
                byte* p = px + i * bpp;
                dst[i] = (uint)(p[0] | p[1] << 8 | p[2] << 16 | (bpp == 4 ? p[3] : 255) << 24);
            }
            Blend(w * h);
        }
        ((delegate* unmanaged<byte*, int, int, int, int, int, void>)*_releaseRaster)(px, w, h, bpp, size, container);
    }

    /// <summary>The first layer sets the canvas; a later one of another size is left out, loudly.</summary>
    private static bool Fit(int w, int h, byte* name)
    {
        if (w <= 0 || h <= 0) return false;
        if (_w == 0) { _w = w; _h = h; Grow(w * h); Array.Clear(_canvas!, 0, w * h); return true; }
        if (w == _w && h == _h) return true;
        Log($"{Marshal.PtrToStringAnsi((IntPtr)name)} is {w}x{h} on a {_w}x{_h} icon -- that layer is left out");
        return false;
    }

    /// <summary>Straight-alpha source-over of <see cref="_layer"/> onto <see cref="_canvas"/> (RGBA, R in the low byte) --
    /// the engine's stacked quads under <c>GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA</c>.</summary>
    internal static void Blend(int n)
    {
        uint[] src = _layer!, dst = _canvas!;
        for (int i = 0; i < n; i++)
        {
            uint s = src[i], sa = s >> 24;
            if (sa == 0) continue;
            uint d = dst[i], da = d >> 24;
            if (sa == 255 || da == 0) { dst[i] = s; continue; }
            uint keep = da * (255 - sa) / 255, oa = sa + keep, o = oa << 24;
            for (int sh = 0; sh < 24; sh += 8)
                o |= ((((s >> sh) & 255) * sa + ((d >> sh) & 255) * keep) / oa) << sh;
            dst[i] = o;
        }
    }

    /// <summary>The canvas cropped to its top-left <paramref name="cw"/>x<paramref name="ch"/> (the content of a
    /// power-of-two padded sheet: the first columns, and the LAST rows of a bottom-up raster) as a 32-bit bottom-up
    /// BGRA TGA, base64'd into <see cref="_b64"/>.</summary>
    internal static void Emit(int cw, int ch)
    {
        int n = TgaHeader + cw * ch * 4;
        if (_tga == null || _tga.Length < n) _tga = new byte[n];
        Span<byte> t = _tga.AsSpan(0, n);
        t[..TgaHeader].Clear();
        t[2] = 2;
        t[12] = (byte)cw; t[13] = (byte)(cw >> 8);
        t[14] = (byte)ch; t[15] = (byte)(ch >> 8);
        t[16] = 32;
        t[17] = 8;
        int o = TgaHeader;
        for (int y = _h - ch; y < _h; y++)
            for (int x = 0; x < cw; x++, o += 4)
            {
                uint p = _canvas![y * _w + x];
                t[o] = (byte)(p >> 16); t[o + 1] = (byte)(p >> 8); t[o + 2] = (byte)p; t[o + 3] = (byte)(p >> 24);
            }
        int max = Base64.GetMaxEncodedToUtf8Length(n);
        if (_b64 == null || _b64.Length < max + 1) _b64 = new byte[max + 1];
        Base64.EncodeToUtf8(t, _b64, out _, out _b64Len);
        _b64[_b64Len] = 0;
    }

    /// <summary>FNV-1a over the emitted TGA: a re-dye is a new synthetic resref, so it misses nk_loadimg's cache.</summary>
    private static uint Hash()
    {
        uint h = 2166136261;
        foreach (byte b in _b64.AsSpan(0, _b64Len)) h = (h ^ b) * 16777619;
        return h;
    }

    /// <summary>A model view's synthetic resref: its TGA hash mixed with its key, so two views of one size and stand-in do not share
    /// nk_loadimg's cached image (and so one texture).</summary>
    internal static string ModelRef(uint tgaHash, string key)
    {
        uint h = tgaHash;
        foreach (char c in key) h = (h ^ c) * 16777619;
        return $"cx{h:x8}";
    }

    private static void SetRef(string name)
    {
        NativeMemory.Clear(_ref, RefLen + 1);
        for (int i = 0; i < name.Length && i < RefLen; i++) _ref[i] = (byte)name[i];
    }

    private static void Grow(int n)
    {
        if (_canvas != null && _canvas.Length >= n) return;
        _canvas = new uint[n];
        _layer = new uint[n];
    }

    private static void Log(string line)
    {
        if (_logged++ < LogCap) _log?.Always("NuiItemIcons: " + line);
    }

    /// <summary>Off-device: the parser, the blend and the emitted TGA's header, crop and byte order, and that the base64
    /// round-trips. The two hooks and the icon walk are engine calls -- live-unverified here.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        byte* s = stackalloc byte[32];
        uint oid;
        void Put(string v) { for (int i = 0; i < v.Length; i++) s[i] = (byte)v[i]; s[v.Length] = 0; }
        Put("@item:7f0012A3?2"); check.Expect(Parse(s, out oid) && oid == 0x7f0012a3, "hex id parsed, case-blind, suffix ignored");
        Put("@item:2a"); check.Expect(Parse(s, out oid) && oid == 42, "short id");
        Put("@item:"); check.Expect(!Parse(s, out _), "no digits is not ours");
        Put("@items:1"); check.Expect(!Parse(s, out _), "another prefix is not ours");

        byte* l = stackalloc byte[400];
        void PutLook(string v) { for (int i = 0; i < v.Length; i++) l[i] = (byte)v[i]; l[v.Length] = 0; }
        string models = "0003" + "0000" + "0000" + "0000" + "0000" + "0000" + "000a" + "0007" + "0002" + string.Concat(System.Linq.Enumerable.Repeat("0000", 9)) + "0012";
        string armor = "@look:0010" + "4d" + "00" + models + "0102030405ff";
        Look look;
        PutLook(armor);
        check.Expect(ParseLook(l, &look) && look.BaseItem == 16 && look.Gender == (byte)'M' && look.Models[6] == 10
                     && look.Models[7] == 7 && look.Models[18] == 0x12 && look.Colors[0] == 1 && look.Colors[5] == 0xff
                     && look.PartColors[0] == 0xff && look.PartColors[113] == 0xff, "@look: armor, no per-part colours = all inherit");
        PutLook(armor + string.Concat(System.Linq.Enumerable.Repeat("ff", 42)) + "2a" + string.Concat(System.Linq.Enumerable.Repeat("ff", 71)));
        check.Expect(ParseLook(l, &look) && look.PartColors[42] == 0x2a && look.PartColors[41] == 0xff, "@look: per-part colours, part-major");
        PutLook(armor + "f"); check.Expect(!ParseLook(l, &look), "@look: a trailing half byte is refused");
        PutLook("@look:0010"); check.Expect(!ParseLook(l, &look), "@look: a truncated string is refused");
        PutLook("@item:0010"); check.Expect(!ParseLook(l, &look), "@look: another prefix is not ours");

        uint[]? savedC = _canvas, savedL = _layer; byte[]? savedT = _tga, savedB = _b64; int sw = _w, sh = _h, sl = _b64Len;
        try
        {
            _canvas = null; Grow(4);
            _canvas![0] = 0xff0000ff; _layer![0] = 0x8000ff00;
            _canvas[1] = 0; _layer[1] = 0x80123456;
            _canvas[2] = 0xff0000ff; _layer[2] = 0;
            Blend(3);
            check.Expect(_canvas[0] >> 24 == 255 && (_canvas[0] & 255) is > 120 and < 135 && ((_canvas[0] >> 8) & 255) is > 120 and < 135, "half over opaque mixes");
            check.Expect(_canvas[1] == 0x80123456 && _canvas[2] == 0xff0000ff, "over transparent copies; transparent leaves");

            _w = 2; _h = 2;
            _canvas[0] = 0xff000001; _canvas[1] = 0xff000002; _canvas[2] = 0xff000003; _canvas[3] = 0xff000004;
            Emit(1, 1);
            byte[] tga = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(_b64!, 0, _b64Len));
            check.Expect(tga.Length == TgaHeader + 4 && tga[2] == 2 && tga[12] == 1 && tga[14] == 1 && tga[16] == 32 && tga[17] == 8, "TGA header: 1x1, 32-bit, bottom-up");
            check.Expect(tga[TgaHeader + 2] == 3 && tga[TgaHeader + 3] == 255, "the crop keeps the first column of the LAST row, as BGRA");
        }
        finally
        {
            _canvas = savedC; _layer = savedL; _tga = savedT; _b64 = savedB; _w = sw; _h = sh; _b64Len = sl;
        }
        log.Info(check.Ok ? "NuiItemIcons.SelfCheck: passed" : "NuiItemIcons.SelfCheck: FAILED");
    }
}
