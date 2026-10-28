using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>The live check (<c>alloy.pltverify</c>): Alloy's whole PLT chain against the engine's own finished
/// composite, on real content. The engine composites a PLT on the CPU into <c>CAurTextureBasic+0x58</c> at the
/// tail of <c>ConstructImage</c> @0x48a5d0 and frees it in the build that follows, so the hook's return is the
/// one place to read it -- no GL readback. Once per resref, up to a cap; off unless the knob is set.</summary>
internal static unsafe class PltVerify
{
    /// <summary>Textures checked before the verifier stands itself down. A session meets a few dozen distinct
    /// PLTs; the cap is what stops a module with thousands turning a diagnostic into a load.</summary>
    private const int MaxTextures = 64;

    private static ExtenderLog? _log;
    private static bool _armed;
    private static readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private static readonly Handles _handles = new();
    private static int* _paletteHeight;
    private static IntPtr* _paletteImage;
    private static long _pass, _fail, _skipped;
    private static bool _paletteChecked;

    internal static bool On => _armed;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _armed = Renderer.PltVerify.Boot;
        if (!_armed) return;
        _paletteHeight = (int*)Native.Sym(SymbolMap.PaletteHeight);
        _paletteImage = (IntPtr*)Native.Sym(SymbolMap.PaletteImage);
        if (_paletteHeight == null || _paletteImage == null)
        {
            _armed = false;
            log.Always("PltVerify: paletteimage or paletteheight unresolved -- alloy.pltverify cannot compare "
                     + "against the engine's composite this build");
            return;
        }
        log.Info("PltVerify: ARMED (alloy.pltverify=1) -- each PLT texture's first build is compared against "
               + "the engine's own composite; look for \"PltVerify: PASS\"/\"FAIL\" lines");
    }

    /// <summary>The engine has just finished building <paramref name="tex"/>'s image. Cold; returns at once
    /// for anything that is not a freshly composited PLT.</summary>
    internal static void Offer(IntPtr tex)
    {
        if (!_armed || _seen.Count >= MaxTextures) return;
        var b = new CAurTextureBasic(tex);
        if (b.PltKind != PltKind || b.Image == IntPtr.Zero || b.Bpp != 4) return;
        if (b.Schemes == IntPtr.Zero || b.SchemeCount <= 0) return;
        string name = NameOf(tex);
        if (name.Length == 0 || !_seen.Add(name)) return;
        try { Run(tex, name, b); }
        catch (Exception e)
        {
            _fail++;
            _log?.Always($"PltVerify: FAIL {name} -- the check itself threw: {e.GetType().Name}: {e.Message}");
        }
        if (_seen.Count == MaxTextures)
            _log?.Always($"PltVerify: {MaxTextures} textures checked -- {_pass} PASS, {_fail} FAIL, {_skipped} skipped; standing down");
    }

    /// <summary><c>CAurTextureBasic::LoadImagePLT</c>'s marker at <c>+0x84</c>: 4 means this image came from a
    /// PLT, which is the only test that separates a composite from an ordinary decode.</summary>
    private const int PltKind = 4;

    private static void Run(IntPtr tex, string name, CAurTextureBasic b)
    {
        if (!_paletteChecked) { _paletteChecked = true; CheckPalette(); }

        int w = b.Width, h = b.Height;
        if (w <= 0 || h <= 0) { Skip(name, $"the engine reports {w}x{h}"); return; }
        int count = b.SchemeCount;
        Span<ushort> rows = stackalloc ushort[SceneDb.PltLayers];
        int* sch = (int*)b.Schemes;
        for (int l = 0; l < SceneDb.PltLayers; l++)
            rows[l] = (ushort)(l < count ? Math.Clamp(sch[l], 0, PltPalettes.Height - 1) : 0);

        byte[]? file = ReadPlt(name, out string? why);
        if (file == null) { Skip(name, $"Alloy could not read the PLT: {why}"); return; }
        byte* pairs = PltImage.Decode(file, out int pw, out int ph, out _, out string? dw);
        if (pairs == null) { Skip(name, $"Alloy's decode refused it: {dw}"); return; }
        try
        {
            if (pw != w || ph != h)
            { Skip(name, $"Alloy read {pw}x{ph} where the engine built {w}x{h}"); return; }
            ReadOnlySpan<byte> master = PltPalettes.Master;
            if (master.Length == 0) { Skip(name, "Alloy's master palette is empty"); return; }
            var engine = new ReadOnlySpan<byte>((void*)b.Image, w * h * 4);
            long n = (long)w * h, bad = 0, firstAt = -1;
            uint gotFirst = 0, wantFirst = 0;
            for (long i = 0; i < n; i++)
            {
                uint mine = PltImage.Resolve(master, rows, pairs[i * 2], pairs[i * 2 + 1]);
                uint theirs = BinaryPrimitives.ReadUInt32LittleEndian(engine[(int)(i * 4)..]);
                if (mine == theirs) continue;
                if (bad++ == 0) { firstAt = i; gotFirst = mine; wantFirst = theirs; }
            }
            if (bad == 0)
            {
                _pass++;
                _log?.Always($"PltVerify: PASS {name} {w}x{h} -- all {n} texels match the engine's composite "
                           + $"(rows {RowsText(rows, count)})");
            }
            else
            {
                _fail++;
                _log?.Always($"PltVerify: FAIL {name} {w}x{h} -- {bad} of {n} texels differ; first at {firstAt} "
                           + $"(pair {pairs[firstAt * 2]},{pairs[firstAt * 2 + 1]}): Alloy 0x{gotFirst:x8}, engine "
                           + $"0x{wantFirst:x8} (rows {RowsText(rows, count)})");
            }
        }
        finally { NativeMemory.Free(pairs); }
    }

    /// <summary>Alloy's master palette against the engine's. One comparison per session: both are built once
    /// and neither moves unless <c>addpalette</c> runs.</summary>
    private static void CheckPalette()
    {
        PltPalettes.LoadDefaults();
        ReadOnlySpan<byte> mine = PltPalettes.Master;
        IntPtr theirs = *_paletteImage;
        int rows = *_paletteHeight;
        if (theirs == IntPtr.Zero || rows <= 0)
        { _log?.Always($"PltVerify: the engine's paletteimage is {theirs:x}/{rows} rows -- palette comparison SKIPPED"); return; }
        if (mine.Length == 0) { _log?.Always("PltVerify: Alloy's master palette is empty -- palette comparison SKIPPED"); return; }
        // The engine's buffer may be taller (addpalette) or the same; compare the rows both hold, and only
        // the 176 rows per slot a palette file actually fills -- vanilla leaves the rest uninitialised.
        int slots = Math.Min(rows / PltPalettes.RowsPerSlot, PltPalettes.Slots);
        const int FilledRows = 176;   // not-an-offset: the shipped pal_*.tga height
        long bad = 0; int firstSlot = -1, firstRow = -1, firstByte = -1;
        for (int s = 0; s < slots; s++)
            for (int r = 0; r < FilledRows; r++)
            {
                int at = (s * PltPalettes.RowsPerSlot + r) * PltPalettes.RowBytes;
                var a = mine.Slice(at, PltPalettes.RowBytes);
                var e = new ReadOnlySpan<byte>((byte*)theirs, at + PltPalettes.RowBytes).Slice(at);
                if (a.SequenceEqual(e)) continue;
                if (bad++ != 0) continue;
                firstSlot = s; firstRow = r;
                for (int k = 0; k < PltPalettes.RowBytes; k++) if (a[k] != e[k]) { firstByte = k; break; }
            }
        if (bad == 0)
            _log?.Always($"PltVerify: PASS master palette -- {slots} slots x {FilledRows} rows match the engine's "
                       + $"paletteimage ({rows} rows total)");
        else
            _log?.Always($"PltVerify: FAIL master palette -- {bad} rows differ; first slot {firstSlot} row "
                       + $"{firstRow} byte {firstByte}. Alloy's AurSetPalette reproduction (grow, ImageFlipY, "
                       + "copy at slot<<18) is wrong, so every PLT colour is wrong");
    }

    private static void Skip(string name, string why)
    {
        _skipped++;
        _log?.Always($"PltVerify: SKIPPED {name} -- {why}");
    }

    private static string RowsText(ReadOnlySpan<ushort> rows, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int l = 0; l < Math.Min(count, rows.Length); l++) { if (l > 0) sb.Append(','); sb.Append(rows[l]); }
        return sb.ToString();
    }

    /// <summary>The resolved texture name at <c>+0xa8</c>, which is the PLT's resref.</summary>
    private static string NameOf(IntPtr tex)
    {
        byte* p = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new CAurTextureBasic(tex).ResolvedName);
        int n = 0;
        while (n < 32 && p[n] != 0) n++;
        return n == 0 ? "" : System.Text.Encoding.ASCII.GetString(p, n);
    }

    private static byte[]? ReadPlt(string resref, out string? why)
    {
        why = null;
        if (!ResLocate.Ready && !ResLocate.Attach(out why)) { why ??= "no resource manager"; return null; }
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        TexRegistry.ResRef(resref, name);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        TexLocation loc;
        fixed (byte* p = name)
        {
            if (!ResLocate.Find(p, PltResType, out IntPtr table, out IntPtr entry)) { why = "no PLT resource"; return null; }
            if (!ResLocate.Locate(table, entry, resId, out loc, out why, out ResLocate.Source src))
            { why ??= $"the resource is {src}"; return null; }
        }
        if (!_handles.Read(loc, out ReadOnlySpan<byte> raw, out why)) return null;
        return raw.ToArray();
    }

    private const ushort PltResType = 6;   // not-an-offset: restype id (PLT)

    internal static string Note()
        => _armed ? $"pltverify: {_pass} PASS, {_fail} FAIL, {_skipped} skipped over {_seen.Count} textures" : "";
}
