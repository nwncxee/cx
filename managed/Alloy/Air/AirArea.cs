using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>Reads what the air's place resolver needs once an area loads: the area's tileset, flags and tile terrain,
/// and the content's <c>cx_airplace.2da</c> merged over the code's defaults. Cold, once an area; keeps no engine pointer.</summary>
public sealed unsafe class AirArea : IExtenderService
{
    private static AirAreaState _s = new();

    /// <summary>Outside the tile window <c>TexStream</c> closes after the original, so the tile lookups pass straight through.</summary>
    private const int ChainPriority = 60;

    /// <summary>Below the links that serve the call: this one only notes which tileset answered to which resref.</summary>
    private const int TileSetPriority = 4;

    /// <summary>The place table's resref and its two columns.</summary>
    private const string Table = "cx_airplace", TilesetColumn = "Tileset", PlaceColumn = "Place";

    /// <summary>The features of the area loaded last; empty until one loads.</summary>
    internal static AreaFeatures Features => _s.Features;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        if (!EngineLayout.Require(log, "AirArea", "CNWCArea", "CNWTile", "CNWTileData")
            || !SymbolMap.Require(log, "AirArea", SymbolMap.AreaLoadArea, SymbolMap.TileSetGet, SymbolMap.TileSetGetTileData)) return;
        _s.TableReady = EngineLayout.Require(log, "AirArea", "CNWRules", "CTwoDimArrays", "C2DA")
                        && SymbolMap.Require(log, "AirArea", SymbolMap.Rules, SymbolMap.ExoResMan, SymbolMap.ResManExists,
                                             SymbolMap.TwoDimGetCached2DA, SymbolMap.C2DAGetCExoStringEntry,
                                             SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor);
        _s.Rules = Native.Sym(SymbolMap.Rules);
        _s.ResMan = Native.Sym(SymbolMap.ExoResMan);
        _s.Set = hooks.ChainTileSetGet(&OnGetTileSet, TileSetPriority);
        _s.Load = hooks.ChainAreaLoadArea(&OnLoadArea, ChainPriority);
        if (_s.Set == null || _s.Load == null) log.Always("AirArea: the area load did not chain -- every area's air reads as an outdoor meadow with no tileset");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnGetTileSet(IntPtr mgr, IntPtr resref)
    {
        IntPtr set = (*_s.Set)(mgr, resref);
        if (set != IntPtr.Zero && resref != IntPtr.Zero)
        {
            int n = 0; byte* p = (byte*)resref;
            while (n < ResRef.Chars && p[n] != 0) n++;
            _s.SetPtr = set;
            _s.SetName = System.Text.Encoding.ASCII.GetString(p, n);
        }
        return set;
    }

    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr areaPtr, IntPtr camera, IntPtr loadscreen)
    {
        int ok = (*_s.Load)(areaPtr, camera, loadscreen);
        if (ok != 0 && areaPtr != IntPtr.Zero) Enter(new CNWCArea(areaPtr));
        return ok;
    }

    /// <summary>Cold, once an area: copy the new area's features and read the place table afresh.</summary>
    private static void Enter(CNWCArea area)
    {
        AirAreaState s = _s;
        IntPtr set = area.TileSet.Ptr;
        string tileset = set != IntPtr.Zero && set == s.SetPtr ? s.SetName : string.Empty;
        var corners = new List<string[]>();
        var edges = new List<string[]>();
        int count = area.TileWidth * area.TileHeight;
        CNWTile tiles = area.Tiles;
        if (set != IntPtr.Zero && tiles.Ptr != IntPtr.Zero && count > 0)
            for (int i = 0; i < count; i++)
            {
                IntPtr data = new CNWTileSet(set).TileSetGetTileData(tiles[i].Id);
                if (data == IntPtr.Zero) continue;
                corners.Add(Names(new CNWTileData(data).Corners));
                edges.Add(Names(new CNWTileData(data).Edges));
            }
        s.Features = AreaFeatures.Of(tileset, area.Flags, corners, edges);
        List<(string, string)>? rows = Rows();
        AirPlaces.Load(rows);
        s.Log?.Info($"AirArea: '{tileset}' flags {area.Flags}, {corners.Count} tile(s), {(rows == null ? "no cx_airplace.2da" : $"cx_airplace.2da with {rows.Count} row(s)")}");
    }

    /// <summary>The text of each of <paramref name="strings"/>.</summary>
    private static string[] Names(Span<ExoStringRec> strings)
    {
        var names = new string[strings.Length];
        for (int i = 0; i < names.Length; i++) names[i] = strings[i].Buf == null ? string.Empty : strings[i].Text;
        return names;
    }

    /// <summary>The content's place table rows, or null when no content supplies <c>cx_airplace.2da</c>; asked of the
    /// resource chain first, because the 2DA cache logs a failed load for a file that is missing.</summary>
    private static List<(string, string)>? Rows()
    {
        AirAreaState s = _s;
        if (!s.TableReady || s.ResMan == IntPtr.Zero || s.Rules == IntPtr.Zero) return null;
        IntPtr resMan = *(IntPtr*)s.ResMan, rules = *(IntPtr*)s.Rules;
        if (resMan == IntPtr.Zero || rules == IntPtr.Zero) return null;
        ResRef name = ResRef.Of(Table);
        if (EngineFn.ResManExists(resMan, &name, ResTypes.TwoDa, null) == 0) return null;
        IntPtr arrays = new CNWRules(rules).TwoDimArrays;
        if (arrays == IntPtr.Zero) return null;
        IntPtr table = new CTwoDimArrays(arrays).Cached(name);
        if (table == IntPtr.Zero) return null;
        var rows = new List<(string, string)>();
        int count = new C2DA(table).Rows;
        for (int r = 0; r < count; r++)
            if (RigMap.Cell(table, r, TilesetColumn) is { } set && RigMap.Cell(table, r, PlaceColumn) is { } place)
                rows.Add((set, place));
        return rows;
    }
}

/// <summary>The lifetime owner: the chain slots, the last tileset the load named and the last area's features.</summary>
internal sealed unsafe class AirAreaState
{
    internal ExtenderLog? Log;
    internal TileSetGetFn* Set;
    internal AreaLoadAreaFn* Load;
    internal IntPtr Rules, ResMan, SetPtr;
    internal bool TableReady;
    internal string SetName = string.Empty;
    internal AreaFeatures Features = AreaFeatures.Of(string.Empty, 0, [], []);
}
