using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>
/// The health-band captions a plate draws: each row of the client's <c>damagelevels.2da</c> names a strref in
/// its <c>STRING_REF</c> column, and the TLK lookup answers with whatever the hak table, a custom TLK or a
/// server override says. Resolved once and cached; four TLK entry points invalidate the cache and the next
/// frame re-resolves. Cold work only: the draw path reads <see cref="Current"/>.
/// </summary>
internal static unsafe class BandWords
{
    /// <summary>The 2da column holding each band's strref.</summary>
    private static ReadOnlySpan<byte> StringRefColumn => "STRING_REF\0"u8;

    /// <summary>sizeof(CExoString) is 16; the sret buffer gets spare room, as a 16-byte one has smashed a canary.</summary>
    private const int ExoScratch = 32;

    /// <summary>Resolve attempts allowed per invalidation when the engine's tables are not reachable yet, so a
    /// client that never answers does not retry every frame.</summary>
    private const int MaxTries = 8;

    /// <summary>The cache and the counters behind it; one object, so nothing here is a loose static.</summary>
    private sealed class BandWordsState
    {
        public string[] Words = PlateLayout.StockBandTexts;
        /// <summary>Bumped by every TLK hook; the cache is current when <see cref="Seen"/> matches it.</summary>
        public int Generation;
        public int Seen = -1;
        public int Tries = MaxTries;
    }

    /// <summary>The hooks' originals, kept apart from the state because they are set once at install.</summary>
    private sealed class OriginalCalls
    {
        public TlkSetOverrideFn SetOverride;
        public TlkClearOverridesFn ClearOverrides;
        public TlkOpenFileAlternateFn OpenFileAlternate;
        public TlkCloseFileAlternateFn CloseFileAlternate;
        public TlkReloadFn Reload;
    }

    private static readonly BandWordsState _s = new();
    private static readonly OriginalCalls _o = new();

    /// <summary>The captions to draw, one per band. Stock words until the first resolve succeeds.</summary>
    public static string[] Current => _s.Words;

    /// <summary>Hook every TLK entry point that can change what a strref resolves to. A server sends overrides
    /// at login and can send more later; a module opens or closes a custom TLK. Each handler runs the original
    /// and marks the cache stale. Returns how many hooks took.</summary>
    public static int Install(HookService hooks)
    {
        _o.SetOverride = hooks.InstallTlkSetOverride(&OnSetOverride);
        _o.ClearOverrides = hooks.InstallTlkClearOverrides(&OnClearOverrides);
        _o.OpenFileAlternate = hooks.InstallTlkOpenFileAlternate(&OnOpenFileAlternate);
        _o.CloseFileAlternate = hooks.InstallTlkCloseFileAlternate(&OnCloseFileAlternate);
        _o.Reload = hooks.InstallTlkReload(&OnReload);
        int n = 0;
        if (_o.SetOverride != null) n++;
        if (_o.ClearOverrides != null) n++;
        if (_o.OpenFileAlternate != null) n++;
        if (_o.CloseFileAlternate != null) n++;
        if (_o.Reload != null) n++;
        return n;
    }

    private static void Stale() { _s.Generation++; _s.Tries = MaxTries; }

    [UnmanagedCallersOnly]
    private static void OnSetOverride(IntPtr self, uint strref, IntPtr text)
    { if (_o.SetOverride != null) _o.SetOverride(self, strref, text); Stale(); }

    [UnmanagedCallersOnly]
    private static void OnClearOverrides(IntPtr self)
    { if (_o.ClearOverrides != null) _o.ClearOverrides(self); Stale(); }

    [UnmanagedCallersOnly]
    private static int OnOpenFileAlternate(IntPtr self, IntPtr file)
    { int r = _o.OpenFileAlternate != null ? _o.OpenFileAlternate(self, file) : 0; Stale(); return r; }

    [UnmanagedCallersOnly]
    private static void OnCloseFileAlternate(IntPtr self)
    { if (_o.CloseFileAlternate != null) _o.CloseFileAlternate(self); Stale(); }

    [UnmanagedCallersOnly]
    private static int OnReload(IntPtr self)
    { int r = _o.Reload != null ? _o.Reload(self) : 0; Stale(); return r; }

    /// <summary>Re-resolve the captions when a TLK hook has fired since the last resolve. One int compare when
    /// nothing changed; the resolve itself is six table reads and six TLK lookups. If the tables are not
    /// reachable yet the stock words stand and the next call tries again, up to <see cref="MaxTries"/> times.</summary>
    public static void Refresh()
    {
        if (_s.Seen == _s.Generation) return;
        string?[]? resolved = Read();
        if (resolved == null)
        {
            if (--_s.Tries > 0) return;
            resolved = new string?[PlateLayout.BandCount];
        }
        _s.Words = PlateLayout.BandCaptions(resolved);
        _s.Seen = _s.Generation;
    }

    /// <summary>Each band's resolved text, null where its lookup failed; null overall when the rules, the TLK or
    /// the table itself is not reachable yet. A strref the TLK answers with an empty string is a real
    /// <c>""</c>, the table's way of saying the band has no word.</summary>
    private static string?[]? Read()
    {
        IntPtr rulesSym = Native.Sym(SymbolMap.Rules), tlkSym = Native.Sym(SymbolMap.TlkTable);
        if (rulesSym == IntPtr.Zero || tlkSym == IntPtr.Zero) return null;
        IntPtr rules = *(IntPtr*)rulesSym, tlk = *(IntPtr*)tlkSym;
        if (rules == IntPtr.Zero || tlk == IntPtr.Zero) return null;
        IntPtr arrays = new CNWRules(rules).TwoDimArrays;
        if (arrays == IntPtr.Zero) return null;
        IntPtr table = new CTwoDimArrays(arrays).DamageLevels;
        if (table == IntPtr.Zero || EngineFn.C2DAGetINTEntry == null || EngineFn.ExoStringInit == null
            || EngineFn.ExoStringDtor == null) return null;

        var words = new string?[PlateLayout.BandCount];
        byte* col = stackalloc byte[ExoScratch];
        new Span<byte>(col, ExoScratch).Clear();
        fixed (byte* name = StringRefColumn) EngineFn.ExoStringInit((IntPtr)col, name, uint.MaxValue);
        for (int row = 0; row < words.Length; row++)
        {
            int strref = -1;
            if (EngineFn.C2DAGetINTEntry(table, row, (IntPtr)col, &strref) == 0 || strref == -1) continue;
            words[row] = Native.EngineText(TlkText(tlk, (uint)strref));
        }
        EngineFn.ExoStringDtor((IntPtr)col);
        return words;
    }

    /// <summary>One TLK lookup through the sret call, the caller-owned buffer zeroed first and freed after.</summary>
    private static string TlkText(IntPtr tlk, uint strref)
    {
        byte* exo = stackalloc byte[ExoScratch];
        new Span<byte>(exo, ExoScratch).Clear();
        new CTlkTable(tlk).TlkGetSimpleString(exo, strref);
        string text = Native.ReadExo((IntPtr)exo);
        EngineFn.ExoStringDtor((IntPtr)exo);
        return text;
    }
}
