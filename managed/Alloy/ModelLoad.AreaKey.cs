using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The AREA key the lightmap bake names a stored plan by, folded out of the read-aheads' peeks: the
/// tileset, every tile's id, orientation, height and flag word, every static placeable's appearance and place --
/// the area's SHAPE -- plus the settings the bake's look derives from, since nothing the engine holds says which
/// area this is. NO key rather than a wrong one: a fold that saw less than the area names none.</summary>
internal static unsafe partial class ModelLoad
{
    private const ulong FoldSeed = 14695981039346656037UL, FoldPrime = 1099511628211UL;
    private static ulong Fold(ulong h, long v) { h ^= (ulong)v; return h * FoldPrime; }
    /// <summary>A float at a fixed quantum: two loads of the same area read the same bytes, but a quantum
    /// keeps the fold from being brittle if a future path ever recomputes rather than re-reads one.</summary>
    private static ulong FoldQ(ulong h, float v, float step) => Fold(h, (long)MathF.Round(v / step));

    /// <summary>What the tile walk folded (id, orientation, height and the flag word, in loop order) and how
    /// many records it covered; set only by a walk that recognised the whole grid.</summary>
    private static ulong _tileFold;
    private static int _tileFoldN;
    private static bool _tileKeyOk;
    /// <summary>The same for the static-placeable block: appearance and the six floats per record.</summary>
    private static ulong _objFold;
    private static int _objFoldN;
    private static bool _objKeyOk;
    /// <summary>The tileset resref <c>LoadArea</c> asked for, folded at the ask.</summary>
    private static ulong _tilesetFold;
    private static bool _tilesetOk;

    /// <summary>1 cm of position and 1e-3 of orientation: the same quanta the bake key itself uses for a
    /// placement, so an area key can never be finer than the rect keys it stands in front of.</summary>
    private const float PosQuantum = 0.01f, OrientQuantum = 1e-3f;

    /// <summary><c>LoadArea</c> entry: the last area's fold is not this one's.</summary>
    private static void ResetAreaKey()
    {
        _tileFold = _objFold = _tilesetFold = 0;
        _tileFoldN = _objFoldN = 0;
        _tileKeyOk = _objKeyOk = _tilesetOk = false;
    }

    /// <summary>The tileset the load asked for, by name: a <c>CResRef</c>'s 16 characters, case-folded (the
    /// engine compares resrefs case-insensitively, so two spellings are one tileset).</summary>
    private static void AreaKeyTileset(IntPtr resref)
    {
        if (resref == IntPtr.Zero) return;
        byte* p = (byte*)resref;
        ulong h = FoldSeed;
        for (int i = 0; i < ResRefChars && p[i] != 0; i++) h = Fold(h, p[i] >= (byte)'A' && p[i] <= (byte)'Z' ? p[i] + 32 : p[i]);
        _tilesetFold = h; _tilesetOk = true;
    }

    /// <summary>The tile grid, from <see cref="WalkTiles"/>' own fold.</summary>
    private static void AreaKeyTiles(ulong fold, int n) { _tileFold = fold; _tileFoldN = n; _tileKeyOk = true; }

    /// <summary>The static-placeable block, from <see cref="FoldPlaceables"/>.</summary>
    private static void AreaKeyObjects(ulong fold, int n) { _objFold = fold; _objFoldN = n; _objKeyOk = true; }

    /// <summary>Fold every record of the peeked static-placeable block: the appearance and the six floats
    /// after it (position then orientation). The floats sit past the appearance's own two bytes, one
    /// <c>ExpectType</c> byte apiece where the stream is typed -- which is the same <c>at - </c>
    /// <see cref="PlainAppearanceAt"/> the shape was accepted at.</summary>
    private static ulong FoldPlaceables(byte* buf, uint rec0, uint count, uint stride, uint at)
    {
        uint type = at - PlainAppearanceAt, first = 6 + 2 * type, step = 4 + type;
        ulong h = FoldSeed;
        for (uint k = 0; k < count; k++)
        {
            byte* rec = &buf[rec0 + k * stride];
            h = Fold(h, *(ushort*)&rec[at]);
            for (uint j = 0; j < 6; j++)
                h = FoldQ(h, *(float*)&rec[first + j * step], j < 3 ? PosQuantum : OrientQuantum);
        }
        return h;
    }

    /// <summary><c>LoadArea</c> has returned, so every peek has run and the area object now carries the
    /// grid, the flags and the static-placeable count the fold is proved against. Hands the bake its area key,
    /// or 0 with the reason the fold saw less than the area.</summary>
    internal static void OnLoadAreaEnd(IntPtr area, bool ok)
    {
        if (!_armed) return;
        var a = new CNWCArea(area);
        SceneInputs.LatchAtten();
        long grid = area == IntPtr.Zero ? 0 : (long)a.TileWidth * a.TileHeight;
        string? no = !ok ? "LoadArea failed"
                   : area == IntPtr.Zero ? "the load named no area"
                   : !_tilesetOk ? "the load never asked for a tileset"
                   : _tileBlockOff ? "the tile read-ahead stood down"
                   : !_tileKeyOk ? "no tile block was read ahead"
                   : _tileFoldN != grid ? $"{_tileFoldN} tile record(s) were read ahead of a {a.TileWidth}x{a.TileHeight} grid"
                   : _blockOff ? "the static-placeable read-ahead stood down"
                   : !_objKeyOk && a.StaticPlaceableCount > 0 ? $"{a.StaticPlaceableCount} static placeable(s) were loaded that no read-ahead saw"
                   : _objKeyOk && _objFoldN != a.StaticPlaceableCount ? $"{_objFoldN} static placeable record(s) were read ahead of {a.StaticPlaceableCount} loaded"
                   : !AlloyLights.AttenLatched ? "the engine's light attenuation params are not readable yet, and the bake settings fold them"
                   : null;
        if (no != null)
        {
            _log?.Info($"ModelLoad: this area gets no area key -- {no}; the lightmap bake packs and probes its rects as on a first visit");
            LightmapBake.OnAreaKey(0);
            return;
        }
        ulong h = Fold(FoldSeed, (long)_tilesetFold);
        h = Fold(h, a.TileWidth); h = Fold(h, a.TileHeight);
        h = Fold(h, (long)_tileFold);
        h = Fold(h, (long)_objFold); h = Fold(h, _objFoldN);
        h = Fold(h, a.Flags);
        ulong settings = LightmapBake.AreaSettingsHash(), key = Fold(h, (long)settings);
        _log?.Info($"ModelLoad: area key {key:x16} -- tileset {_tilesetFold:x16}, grid {a.TileWidth}x{a.TileHeight}, tiles {_tileFold:x16}, "
                 + $"{_objFoldN} static placeable(s) {_objFold:x16}, flags {a.Flags:x}, settings {settings:x16}");
        LightmapBake.OnAreaKey(key);
    }

    /// <summary>Off-device: the fold is stable, and every term the area key claims to carry actually moves it.
    /// A key that ignored a term would serve one area's bake to another.</summary>
    internal static bool SelfCheckAreaKey(ExtenderLog log)
    {
        var check = new Checker(log, "ModelLoad.AreaKey.SelfCheck");
        const int Records = 3;
        foreach (bool typed in new[] { false, true })
        {
            int stride = typed ? TypedStride : PlainStride, at = typed ? TypedAppearanceAt : PlainAppearanceAt;
            uint type = (uint)at - PlainAppearanceAt, first = 6 + 2 * type, step = 4 + type;
            int len = Records * stride;
            byte* buf = stackalloc byte[len];
            for (int i = 0; i < len; i++) buf[i] = 0;
            for (int k = 0; k < Records; k++)
            {
                byte* rec = &buf[k * stride];
                *(ushort*)&rec[at] = (ushort)(100 + k);
                for (uint j = 0; j < 6; j++) *(float*)&rec[first + j * step] = k + j * 0.25f;
            }
            ulong h0 = FoldPlaceables(buf, 0, Records, (uint)stride, (uint)at);
            check.Expect(h0 == FoldPlaceables(buf, 0, Records, (uint)stride, (uint)at), $"typed={typed}: the placeable fold is deterministic");
            check.Expect(h0 != FoldPlaceables(buf, 0, Records - 1, (uint)stride, (uint)at), $"typed={typed}: a record dropped is a different fold");
            *(ushort*)&buf[2 * stride + at] = 7;
            check.Expect(h0 != FoldPlaceables(buf, 0, Records, (uint)stride, (uint)at), $"typed={typed}: an appearance is in the fold");
            *(ushort*)&buf[2 * stride + at] = 102;
            *(float*)&buf[stride + first] += 1f;
            check.Expect(h0 != FoldPlaceables(buf, 0, Records, (uint)stride, (uint)at), $"typed={typed}: a moved placeable is in the fold");
            *(float*)&buf[stride + first] -= 1f;
            *(float*)&buf[stride + first + 5 * step] += 0.5f;
            check.Expect(h0 != FoldPlaceables(buf, 0, Records, (uint)stride, (uint)at), $"typed={typed}: a turned placeable is in the fold");
            *(float*)&buf[stride + first + 5 * step] -= 0.5f;
            *(float*)&buf[stride + first] += PosQuantum / 4f;
            check.Expect(h0 == FoldPlaceables(buf, 0, Records, (uint)stride, (uint)at), $"typed={typed}: a quarter of the position quantum is the same fold");
            *(float*)&buf[stride + first] -= PosQuantum / 4f;
            check.Expect(first + 5 * step + 4 == stride, $"typed={typed}: the six floats end exactly at the record's stride");
        }
        ulong t = FoldSeed;
        check.Expect(Fold(t, 1) != Fold(t, 2) && Fold(Fold(t, 1), 2) != Fold(Fold(t, 2), 1), "the fold is order-dependent, so two grids of the same tiles differ");
        log.Info(check.Ok ? "ModelLoad.AreaKey.SelfCheck: passed" : "ModelLoad.AreaKey.SelfCheck: FAILED");
        return check.Ok;
    }
}
