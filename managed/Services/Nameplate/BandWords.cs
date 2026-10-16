using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>
/// The health-band captions a plate draws: each row of the client's <c>damagelevels.2da</c> names a strref in
/// its <c>STRING_REF</c> column, and the TLK lookup answers with whatever the hak table, a custom TLK or a
/// server override says. Resolved once and cached; the TLK entry points and the module edge invalidate the
/// cache and the next frame re-resolves. Cold work only: the draw path reads <see cref="Current"/>.
/// </summary>
internal static unsafe class BandWords
{
    /// <summary>The 2da column holding each band's strref.</summary>
    private static ReadOnlySpan<byte> StringRefColumn => "STRING_REF\0"u8;

    /// <summary>Bytes for one CExoString buffer: sizeof is 16, and 16 more of slack.</summary>
    private const int ExoScratch = 32;

    /// <summary>The cache and the counter behind it; one object, so nothing here is a loose static.</summary>
    private sealed class BandWordsState
    {
        public string[] Words = PlateLayout.BandCaptions(new string?[PlateLayout.BandCount]);
        /// <summary>Bumped by every invalidation; the cache is current when <see cref="SeenGeneration"/> matches it.</summary>
        public int Generation;
        public int SeenGeneration = -1;
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

    private static readonly BandWordsState _state = new();
    private static readonly OriginalCalls _originals = new();

    /// <summary>The captions to draw, one per band. All empty until the first resolve succeeds.</summary>
    public static string[] Current => _state.Words;

    /// <summary>Hook every TLK entry point that can change what a strref resolves to. A server sends overrides
    /// at login and can send more later; a module opens or closes a custom TLK. Each handler runs the original
    /// and marks the cache stale. False when any hook did not take.</summary>
    public static bool Install(HookService hooks)
    {
        _originals.SetOverride = hooks.InstallTlkSetOverride(&OnSetOverride);
        _originals.ClearOverrides = hooks.InstallTlkClearOverrides(&OnClearOverrides);
        _originals.OpenFileAlternate = hooks.InstallTlkOpenFileAlternate(&OnOpenFileAlternate);
        _originals.CloseFileAlternate = hooks.InstallTlkCloseFileAlternate(&OnCloseFileAlternate);
        _originals.Reload = hooks.InstallTlkReload(&OnReload);
        return _originals.SetOverride != null && _originals.ClearOverrides != null && _originals.OpenFileAlternate != null
            && _originals.CloseFileAlternate != null && _originals.Reload != null;
    }

    /// <summary>Marks the cache stale: the next <see cref="Refresh"/> resolves again.</summary>
    internal static void Stale() => _state.Generation++;

    [UnmanagedCallersOnly]
    private static void OnSetOverride(IntPtr self, uint strref, IntPtr text)
    { _originals.SetOverride(self, strref, text); Stale(); }

    [UnmanagedCallersOnly]
    private static void OnClearOverrides(IntPtr self)
    { _originals.ClearOverrides(self); Stale(); }

    [UnmanagedCallersOnly]
    private static int OnOpenFileAlternate(IntPtr self, IntPtr file)
    { int r = _originals.OpenFileAlternate(self, file); Stale(); return r; }

    [UnmanagedCallersOnly]
    private static void OnCloseFileAlternate(IntPtr self)
    { _originals.CloseFileAlternate(self); Stale(); }

    [UnmanagedCallersOnly]
    private static int OnReload(IntPtr self)
    { int r = _originals.Reload(self); Stale(); return r; }

    /// <summary>Re-resolve the captions when the cache is stale. One int compare when nothing changed; the
    /// resolve itself is six table reads and six TLK lookups. If the tables are not reachable yet the cache
    /// stays stale and the next call tries again.</summary>
    public static void Refresh()
    {
        if (_state.SeenGeneration == _state.Generation) return;
        string?[]? resolved = Read();
        if (resolved == null) return;
        _state.Words = PlateLayout.BandCaptions(resolved);
        _state.SeenGeneration = _state.Generation;
    }

    /// <summary>Each band's resolved text, null where its cell does not parse; null overall when the rules, the TLK or
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
        NativeMemory.Clear(col, ExoScratch);
        fixed (byte* name = StringRefColumn) EngineFn.ExoStringInit((IntPtr)col, name, uint.MaxValue);
        for (int row = 0; row < words.Length; row++)
        {
            int strref = -1;
            if (EngineFn.C2DAGetINTEntry(table, row, (IntPtr)col, &strref) == 0 || strref == -1) continue;
            words[row] = Native.TlkText((uint)strref);
        }
        EngineFn.ExoStringDtor((IntPtr)col);
        return words;
    }
}
