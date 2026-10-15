using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Radial;

namespace CX.Services.Nui;

/// <summary>What a model view's <c>target</c> key names.</summary>
internal enum TargetKind { None, Player, Creature, Appearance, Model, Item }

/// <summary>A parsed <c>target</c>: <see cref="Id"/> for a creature, <see cref="Row"/> for an appearance row, <see cref="Text"/> for a
/// model resref or an item (an <c>@look:</c> string or a hex object id, the same text <c>wear</c> takes).</summary>
internal readonly record struct Target(TargetKind Kind, uint Id = 0, int Row = 0, string Text = "")
{
    /// <summary>Does the doll copy a creature the client holds, so its look follows that creature?</summary>
    internal bool FollowsCreature => Kind is TargetKind.Player or TargetKind.Creature;
}

/// <summary>
/// The things a model view can show besides a copy of a creature: an appearance row (<c>app:&lt;row&gt;</c>), a model by name
/// (<c>model:&lt;resref&gt;</c>) and an item as a model of its own (<c>item:&lt;look or id&gt;</c>), plus the parse of the whole <c>target</c> value.
/// </summary>
internal static unsafe class NuiModelTarget
{
    private const string AppPrefix = "app:", ModelPrefix = "model:", ItemPrefix = "item:";
    /// <summary>The highest <c>appearance.2da</c> row the record's 16-bit appearance field holds.</summary>
    internal const int MaxRow = 65535;
    /// <summary>The bytes of an engine <c>CExoString</c> (a pointer and two counts), the caller-owned value a 2DA cell is written into.</summary>
    private const int ExoSize = 16;   // not-an-offset: sizeof(CExoString)
    /// <summary>The model slot an item's one model sits in, the one the engine fetches a worn item's model from.</summary>
    internal const byte ModelSlot = 1;   // not-an-offset: CNWCItem model slot

    /// <summary>The engine's <c>CResRef</c>: 17 bytes, empty here. Itanium copies it to the stack, Win64 passes a pointer to it.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 17)]
    private struct ResRefArg { public byte First; }

    private sealed class TargetState { public IntPtr LoadModel, Rules; public ExtenderLog? Log; }

    private static readonly TargetState _t = new();

    internal static void Init(ExtenderLog log)
    {
        _t.Log = log;
        _t.Rules = Native.Sym(SymbolMap.Rules);
        _t.LoadModel = SymbolMap.Resolve(SymbolMap.ItemLoadModel, log);
        if (_t.LoadModel == IntPtr.Zero) log.Always("NuiModelTarget: the engine's item model loader did not resolve -- no item targets");
    }

    /// <summary>The target a <c>target</c> value names: <c>player</c>, <c>app:&lt;row&gt;</c>, <c>model:&lt;resref&gt;</c>,
    /// <c>item:&lt;look or id&gt;</c>, or a creature's object id in hex. Anything else names nothing.</summary>
    internal static Target Parse(string value)
    {
        string v = value.Trim();
        if (v == "player") return new Target(TargetKind.Player);
        if (v.StartsWith(AppPrefix, StringComparison.Ordinal))
            return int.TryParse(v.AsSpan(AppPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int row) && row <= MaxRow
                ? new Target(TargetKind.Appearance, Row: row) : default;
        if (v.StartsWith(ModelPrefix, StringComparison.Ordinal))
        {
            string r = v.Substring(ModelPrefix.Length).ToLowerInvariant();
            return ValidResRef(r) ? new Target(TargetKind.Model, Text: r) : default;
        }
        if (v.StartsWith(ItemPrefix, StringComparison.Ordinal))
        {
            string t = v.Substring(ItemPrefix.Length);
            return t.Length > 0 ? new Target(TargetKind.Item, Text: t) : default;
        }
        return uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id) && id != 0 ? new Target(TargetKind.Creature, Id: id) : default;
    }

    /// <summary>A resref: 1 to 16 letters, digits and underscores.</summary>
    internal static bool ValidResRef(string s) => s.Length is > 0 and <= RadialIcons.ResRefLen && !s.AsSpan().ContainsAnyExcept(ResRefChars);

    private static readonly SearchValues<char> ResRefChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary><paramref name="text"/> as ASCII bytes and a zero in <paramref name="dst"/>, which must be one longer.</summary>
    internal static void AsciiZ(string text, Span<byte> dst)
    {
        dst[Encoding.ASCII.GetBytes(text, dst)] = 0;
    }

    /// <summary>An appearance record that holds nothing but <paramref name="row"/>: the engine builds that row's model with
    /// every part, colour and head at 0.</summary>
    internal static void Blank(int row, Span<byte> record)
    {
        record.Clear();
        fixed (byte* p = record)
        {
            new CPathfindInformation((IntPtr)p).Appearance = (ushort)row;
            new CCAppearanceInfo((IntPtr)p).HelmId = NuiModelDoll.InvalidId;
        }
    }

    /// <summary>The engine's table of 2DAs, or null before a module has loaded them.</summary>
    private static CTwoDimArrays? Tables()
    {
        IntPtr rules = _t.Rules == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_t.Rules;
        IntPtr tables = rules == IntPtr.Zero ? IntPtr.Zero : new CNWRules(rules).TwoDimArrays;
        return tables == IntPtr.Zero ? null : new CTwoDimArrays(tables);
    }

    /// <summary>How many rows a 2DA has, or -1 when its table is not reachable yet.</summary>
    private static int RowsOf(IntPtr table) => table == IntPtr.Zero ? -1 : new C2DA(table).Rows;

    /// <summary>How many rows <c>appearance.2da</c> has, or -1 when the engine's tables are not reachable yet.</summary>
    internal static int AppearanceRows() => Tables() is { } t ? RowsOf(t.Appearance) : -1;

    /// <summary>The rows of <c>visualeffects.2da</c>, <c>cloakmodel.2da</c> and the item visual effects table; -1 when not reachable yet.</summary>
    internal static int VisualEffectRows() => Tables() is { } t ? RowsOf(t.VisualEffects) : -1;
    internal static int CloakModelRows() => Tables() is { } t ? RowsOf(t.CloakModel) : -1;
    internal static int ItemVisualEffectRows() => Tables() is { } t ? RowsOf(t.ItemVisualEffects) : -1;

    /// <summary>Does <paramref name="row"/> name an <c>appearance.2da</c> row the client has? Logs once when it does not.</summary>
    internal static bool AppearanceKnown(int row)
    {
        int rows = AppearanceRows();
        if (NuiModelGuard.RowInTable(row, rows)) return true;
        _t.Log?.Always($"NuiModelTarget: appearance row {row} is past the {rows} the client's appearance.2da has");
        return false;
    }

    /// <summary>A cell of <c>appearance.2da</c> as text, or null when the row, the column or the tables are not there.</summary>
    internal static string? AppearanceCell(int row, string column)
    {
        IntPtr table = Tables() is { } t ? t.Appearance : IntPtr.Zero;
        if (table == IntPtr.Zero || EngineFn.ExoStringInit == null || EngineFn.C2DAGetCExoStringEntry == null) return null;
        byte* col = stackalloc byte[ExoSize];
        byte* cell = stackalloc byte[ExoSize];
        new Span<byte>(col, ExoSize).Clear();
        new Span<byte>(cell, ExoSize).Clear();
        Span<byte> name = stackalloc byte[column.Length + 1];
        AsciiZ(column, name);
        fixed (byte* n = name) EngineFn.ExoStringInit((IntPtr)col, n, uint.MaxValue);
        string? text = null;
        if (EngineFn.C2DAGetCExoStringEntry(table, row, (IntPtr)col, (IntPtr)cell) != 0 && *(byte**)cell != null)
            text = Marshal.PtrToStringAnsi((IntPtr)(*(byte**)cell));
        EngineFn.ExoStringDtor((IntPtr)cell);
        EngineFn.ExoStringDtor((IntPtr)col);
        return text;
    }

    /// <summary>The model <paramref name="resref"/> as an object of its own, or zero when the client has no model by that name.</summary>
    internal static IntPtr NewModel(string resref)
    {
        if (!CX.Alloy.ModelFeed.Present(resref)) { _t.Log?.Always($"NuiModelTarget: the client has no model '{resref}'"); return IntPtr.Zero; }
        Span<byte> name = stackalloc byte[RadialIcons.ResRefLen + 1];
        AsciiZ(resref, name);
        byte empty = 0;
        fixed (byte* n = name) return EngineFn.NewAurObject(n, &empty, IntPtr.Zero);
    }

    /// <summary>Load <paramref name="item"/>'s model into <paramref name="slot"/> and return it: slot <see cref="ModelSlot"/> for an item's single model. Zero when the engine builds none.</summary>
    internal static IntPtr LoadItem(IntPtr item, byte slot)
    {
        if (_t.LoadModel == IntPtr.Zero) return IntPtr.Zero;
        ResRefArg none = default;
        long done = OperatingSystem.IsWindows()
            ? (long)((delegate* unmanaged<IntPtr, ResRefArg*, byte, byte, IntPtr>)_t.LoadModel)(item, &none, slot, 0)
            : ((delegate* unmanaged<IntPtr, ResRefArg, byte, byte, int>)_t.LoadModel)(item, none, slot, 0);
        IntPtr model = new CNWCCreature(new CNWCItem(item).AsObject.Ptr).GetModel(0xff, slot);
        if (model == IntPtr.Zero) _t.Log?.Always($"NuiModelTarget: LoadModel slot 0x{slot:x} answered {done & 0xff}, and the item has no model there");
        return model;
    }

    /// <summary>The item <paramref name="text"/> names (the <c>wear</c> syntax) as a model of its own: a throwaway copy with its model
    /// loaded the way a dropped item's is. Zero when there is no such item or the engine builds no model; otherwise
    /// <paramref name="item"/> is the copy, which the caller destroys after the model is gone.</summary>
    internal static IntPtr NewItemModel(string text, out IntPtr item)
    {
        item = IntPtr.Zero;
        if (_t.LoadModel == IntPtr.Zero) return IntPtr.Zero;
        IntPtr src = NuiModelGear.WearItem(text, out bool minted, out _);
        IntPtr copy = src == IntPtr.Zero ? IntPtr.Zero : minted ? src : NuiItemIcons.MintCopy(src);
        IntPtr model = copy == IntPtr.Zero ? IntPtr.Zero : LoadItem(copy, ModelSlot);
        if (model == IntPtr.Zero)
        {
            _t.Log?.Always($"NuiModelTarget: '{text}' gives no item model");
            if (copy != IntPtr.Zero) NuiItemIcons.Destroy(copy);
            return IntPtr.Zero;
        }
        item = copy;
        return model;
    }

    /// <summary>Oracle: hand-written <c>target</c> values and what each names.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Parse("player").Kind == TargetKind.Player && Parse(" player ").Kind == TargetKind.Player, "player names the player");
        Target c = Parse("180000f3");
        check.Expect(c.Kind == TargetKind.Creature && c.Id == 0x180000f3, "a hex number names a creature by its object id");
        check.Expect(Parse("").Kind == TargetKind.None && Parse("0").Kind == TargetKind.None && Parse("zz").Kind == TargetKind.None, "empty, 0 and text name nothing");
        Target a = Parse("app:1234");
        check.Expect(a.Kind == TargetKind.Appearance && a.Row == 1234, "app: names an appearance row in decimal");
        check.Expect(Parse("app:0").Row == 0 && Parse("app:0").Kind == TargetKind.Appearance, "row 0 is a row");
        check.Expect(Parse("app:65535").Kind == TargetKind.Appearance && Parse("app:65536").Kind == TargetKind.None, "rows end at 65535");
        check.Expect(Parse("app:").Kind == TargetKind.None && Parse("app:-1").Kind == TargetKind.None && Parse("app:12x").Kind == TargetKind.None, "an empty, negative or half-numeric row names nothing");
        Target m = Parse("model:C_Ogre_01");
        check.Expect(m.Kind == TargetKind.Model && m.Text == "c_ogre_01", "model: names a resref, lower-cased");
        check.Expect(Parse("model:").Kind == TargetKind.None && Parse("model:has space").Kind == TargetKind.None && Parse("model:abcdefghijklmnopq").Kind == TargetKind.None,
                     "an empty, spaced or 17-letter resref names nothing");
        Target i = Parse("item:@look:0001");
        check.Expect(i.Kind == TargetKind.Item && i.Text == "@look:0001", "item: keeps the wear text whole, colons and all");
        check.Expect(Parse("item:180000f3").Text == "180000f3" && Parse("item:").Kind == TargetKind.None, "item: takes a hex id, and nothing is no item");
        check.Expect(Parse("player").FollowsCreature && Parse("180000f3").FollowsCreature && !Parse("app:1").FollowsCreature && !Parse("model:x").FollowsCreature,
                     "only the player and a creature id follow a creature");
        byte* b = stackalloc byte[256];
        var rec = new Span<byte>(b, 256);
        rec.Fill(0xaa);
        Blank(0x1234, rec);
        var view = new CPathfindInformation((IntPtr)b);
        check.Expect(view.Appearance == 0x1234, "a blank record holds the row");
        check.Expect(new CCAppearanceInfo((IntPtr)b).HelmId == 0x7f000000, "and no helmet");
        view.Appearance = 0;
        new CCAppearanceInfo((IntPtr)b).HelmId = 0;
        int stray = 0;
        for (int k = 0; k < rec.Length; k++) if (rec[k] != 0) stray++;
        check.Expect(stray == 0, "and nothing else");
    }
}
