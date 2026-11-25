using System;
using System.Collections.Generic;
using CX.Services.Sound;

namespace CX.Alloy;

/// <summary>What an area offers the place resolver: its tileset, its ARE flags and each tile's corner and edge terrain.</summary>
internal readonly struct AreaFeatures
{
    internal readonly string Tileset;
    internal readonly uint Flags;
    internal readonly List<string[]> Corners, Edges;

    private AreaFeatures(string tileset, uint flags, List<string[]> corners, List<string[]> edges)
    { Tileset = tileset; Flags = flags; Corners = corners; Edges = edges; }

    /// <summary>The features of an area with <paramref name="tileset"/>, its flags and its tiles' terrain.</summary>
    internal static AreaFeatures Of(string tileset, uint flags, List<string[]> corners, List<string[]> edges)
        => new(tileset, flags, corners, edges);
}

/// <summary>The tileset-to-place rows in force: the code's defaults with a content table's rows merged over them.</summary>
internal sealed class AirPlaceTableState
{
    internal readonly Dictionary<string, string> Rows = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reads an area's place from its features, a builder's held <c>air.place</c> and the tileset table.</summary>
internal static class AirPlaces
{
    /// <summary>The place each stock tileset takes whatever its tiles hold; a <c>winter</c> row marks the set winter and
    /// lets its features pick the place.</summary>
    private static readonly (string Tileset, string Place)[] Defaults =
    {
        ("tcn01", "city"), ("tcm02", "city"),
        ("tin01", "none"), ("tni01", "none"), ("tic01", "none"), ("tni02", "none"), ("twc03", "none"),
        ("tsw01", "none"), ("dag01", "none"), ("tss13", "none"), ("ttd01", "none"), ("tti01", "none"),
        ("trs02", "winter"),
    };

    /// <summary>The terrain whose share of the tiles makes an outdoor area a forest.</summary>
    private static readonly string[] TreeCorners = { "trees", "forest" };

    /// <summary>The edges that put water beside an outdoor area as surely as a water corner does.</summary>
    private static readonly string[] WaterEdges = { "stream", "river", "dock", "sandbank" };

    private static AirPlaceTableState _table = Table(null);

    /// <summary>The look an area with <paramref name="f"/> takes: the place a held <c>air.place</c> names, else the
    /// table's row for its tileset, else the place its features give it.</summary>
    internal static AirLook Resolve(in AreaFeatures f)
    {
        bool indoor = SoundScene.Indoor(f.Flags);
        bool cave = SoundScene.Material(f.Flags) == SoundScene.CaveRock;
        bool water = !indoor && (Any(f.Corners, "water") || AnyOf(f.Edges, WaterEdges));
        string? row = _table.Rows.TryGetValue(f.Tileset, out string? r) ? r : null;
        bool winter = row == "winter" || Any(f.Corners, "snow");
        AirPlace place = Held() ?? Named(row) ?? Featured(f, indoor);
        return new AirLook(place, winter, water, cave);
    }

    /// <summary>The place an area's own features give it: indoors a crypt, outdoors a forest when at least half its
    /// tiles hold trees, else a meadow.</summary>
    private static AirPlace Featured(in AreaFeatures f, bool indoor)
    {
        if (indoor) return AirPlace.Crypt;
        int trees = 0;
        foreach (string[] tile in f.Corners)
            if (Treed(tile)) trees++;
        return f.Corners.Count > 0 && trees * 2 >= f.Corners.Count ? AirPlace.Forest : AirPlace.Meadow;
    }

    /// <summary>Does a tile whose corners are <paramref name="corners"/> hold trees?</summary>
    internal static bool Treed(string[] corners) => Holds(corners, TreeCorners);

    /// <summary>Does any tile in <paramref name="tiles"/> hold <paramref name="terrain"/>?</summary>
    private static bool Any(List<string[]> tiles, string terrain)
    {
        foreach (string[] tile in tiles)
            foreach (string t in tile)
                if (Is(t, terrain)) return true;
        return false;
    }

    /// <summary>Does any tile in <paramref name="tiles"/> hold one of <paramref name="terrains"/>?</summary>
    private static bool AnyOf(List<string[]> tiles, string[] terrains)
    {
        foreach (string[] tile in tiles)
            if (Holds(tile, terrains)) return true;
        return false;
    }

    /// <summary>Does <paramref name="tile"/> hold one of <paramref name="terrains"/>?</summary>
    private static bool Holds(string[] tile, string[] terrains)
    {
        foreach (string t in tile)
            foreach (string want in terrains)
                if (Is(t, want)) return true;
        return false;
    }

    /// <summary>Is <paramref name="name"/> the terrain <paramref name="want"/>? A raised one ends in <c>+</c>, and the
    /// .set keeps its own case.</summary>
    private static bool Is(string? name, string want)
    {
        if (string.IsNullOrEmpty(name)) return false;
        ReadOnlySpan<char> n = name.AsSpan();
        if (n[^1] == '+') n = n[..^1];
        return n.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The place a server or area hold of <c>air.place</c> names; null when none is held or it holds <c>auto</c>.
    /// The player's own value never picks one.</summary>
    private static AirPlace? Held() => !Air.Place.Enforced ? null : Named(Air.Place.V);

    /// <summary>The place <paramref name="name"/> names; null for no name, <c>winter</c>, <c>auto</c> or one unknown.</summary>
    private static AirPlace? Named(string? name) => name switch
    {
        "none" => AirPlace.None, "crypt" => AirPlace.Crypt, "city" => AirPlace.City,
        "forest" => AirPlace.Forest, "meadow" => AirPlace.Meadow, _ => null,
    };

    /// <summary>Replace the content table's rows, merged over the defaults; null restores the shipped table.</summary>
    internal static void Load(IEnumerable<(string Tileset, string Place)>? rows) => _table = Table(rows);

    /// <summary>The defaults with <paramref name="rows"/> merged over them; a row naming no place is dropped.</summary>
    private static AirPlaceTableState Table(IEnumerable<(string Tileset, string Place)>? rows)
    {
        var s = new AirPlaceTableState();
        foreach ((string set, string place) in Defaults) s.Rows[set] = place;
        if (rows == null) return s;
        foreach ((string set, string place) in rows)
        {
            string p = place.ToLowerInvariant();
            if (p == "winter" || Named(p) != null) s.Rows[set] = p;
        }
        return s;
    }
}
