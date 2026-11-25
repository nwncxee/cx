using System.Collections.Generic;

namespace CX.Alloy;

/// <summary>What an area offers the place resolver: its tileset, its ARE flags and each tile's corner and edge terrain.</summary>
internal readonly struct AreaFeatures
{
    internal readonly string Tileset;
    internal readonly uint Flags;
    internal readonly List<string[]> Corners, Edges;

    /// <summary>The area flag an interior sets, as an area's ARE file holds it.</summary>
    internal const uint Interior = 1;

    private AreaFeatures(string tileset, uint flags, List<string[]> corners, List<string[]> edges)
    { Tileset = tileset; Flags = flags; Corners = corners; Edges = edges; }

    /// <summary>The features of an area with <paramref name="tileset"/>, its flags and its tiles' terrain.</summary>
    internal static AreaFeatures Of(string tileset, uint flags, List<string[]> corners, List<string[]> edges)
        => new(tileset, flags, corners, edges);
}

/// <summary>Reads an area's place from its features, a builder's held <c>air.place</c> and the tileset table.</summary>
internal static class AirPlaces
{
    /// <summary>The look an area with <paramref name="f"/> takes.</summary>
    internal static AirLook Resolve(in AreaFeatures f) => default;

    /// <summary>Replace the tileset-to-place rows; null restores the shipped table.</summary>
    internal static void Load(IEnumerable<(string Tileset, string Place)>? rows) { }
}
