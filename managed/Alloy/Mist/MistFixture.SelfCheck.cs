using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy;

/// <summary>The ground mist examples' shared ground: walkmeshes built by hand and the readings every example takes.
/// Oracle: the stock surfacemat.2da rows and <c>CNWArea::SetWind</c>'s strong wind, both read off the game's data.</summary>
internal static class MistFixture
{
    /// <summary>Stock surfacemat.2da rows: Stone walked, Water walked, Nonwalk a wall, DeepWater not walked but a place,
    /// StoneBridge walked.</summary>
    internal const int Stone = 4, Water = 6, Nonwalk = 7, DeepWater = 17, StoneBridge = 22;

    /// <summary>One frame at 60 fps.</summary>
    internal const float Frame = 1f / 60f;

    /// <summary><c>CNWArea::SetWind</c> at power 2 ("strong"): magnitude 2 along normalize(1, 1, 0), which
    /// <c>CNWCArea::SetWind</c> hands to <c>setglobalwind</c> as direction times magnitude.</summary>
    internal static readonly Vector2 HighWind = new(MathF.Sqrt(2f), MathF.Sqrt(2f));

    /// <summary>The same strong wind turned to blow along +x.</summary>
    internal static readonly Vector2 HighWindX = new(2f, 0f);

    /// <summary>The side of one tile's walkmesh, in metres.</summary>
    internal const float TileSize = 10f;

    /// <summary>The fog amount the examples' fogged areas set.</summary>
    internal const float AreaAmount = 10f;

    /// <summary>One tile part as the area seats it: its walkmesh in model space and the world it stands at.</summary>
    internal readonly record struct Tile(MeshDef Mesh, Matrix4x4 World);

    /// <summary>A square of ground <paramref name="half"/> metres either side of the origin, cut into
    /// <see cref="TileSize"/> tiles of <paramref name="step"/> metre quads, each quad's row read at its centre.</summary>
    internal static List<Tile> Ground(float half, float step, Func<float, float, float> height, Func<float, float, int> surface)
    {
        var tiles = new List<Tile>();
        for (float tx = -half; tx < half; tx += TileSize)
            for (float ty = -half; ty < half; ty += TileSize)
            {
                var at = new Vector3(tx, ty, 0f);
                var pos = new List<Vector3>();
                var faces = new List<FaceDef>();
                for (float x = tx; x < tx + TileSize; x += step)
                    for (float y = ty; y < ty + TileSize; y += step)
                        Quad(pos, faces, at, new Vector3(x, y, height(x, y)), new Vector3(x + step, y, height(x + step, y)),
                            new Vector3(x + step, y + step, height(x + step, y + step)), new Vector3(x, y + step, height(x, y + step)),
                            surface(x + step * 0.5f, y + step * 0.5f));
                tiles.Add(new Tile(new MeshDef { Positions = pos.ToArray(), Faces = faces.ToArray() }, Matrix4x4.CreateTranslation(at)));
            }
        return tiles;
    }

    /// <summary>Two faces of <paramref name="surface"/> over the world corners a..d, stored relative to the tile at <paramref name="at"/>.</summary>
    private static void Quad(List<Vector3> pos, List<FaceDef> faces, Vector3 at, Vector3 a, Vector3 b, Vector3 c, Vector3 d, int surface)
    {
        int v = pos.Count;
        pos.Add(a - at); pos.Add(b - at); pos.Add(c - at); pos.Add(d - at);
        faces.Add(new FaceDef { V0 = v, V1 = v + 1, V2 = v + 2, Surface = surface, Adj0 = -1, Adj1 = -1, Adj2 = -1 });
        faces.Add(new FaceDef { V0 = v, V1 = v + 2, V2 = v + 3, Surface = surface, Adj0 = -1, Adj1 = -1, Adj2 = -1 });
    }

    /// <summary>Flat stone ground at 0 m, 160 m either side.</summary>
    internal static List<Tile> Flat() => Ground(160f, 2f, (_, _) => 0f, (_, _) => Stone);

    /// <summary>A flat deck of <paramref name="surface"/> at height <paramref name="z"/>, one more tile part on top of what is there.</summary>
    internal static void Deck(List<Tile> tiles, float x0, float y0, float x1, float y1, float z, int surface)
    {
        var at = new Vector3(x0, y0, z);
        var pos = new List<Vector3>();
        var faces = new List<FaceDef>();
        Quad(pos, faces, at, at, new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z), surface);
        tiles.Add(new Tile(new MeshDef { Positions = pos.ToArray(), Faces = faces.ToArray() }, Matrix4x4.CreateTranslation(at)));
    }

    /// <summary>A fresh area: the old field gone, each tile's walkmesh taken in, the player's feet at <paramref name="feet"/>.</summary>
    internal static void Lay(List<Tile> tiles, Vector3 feet, bool fogOn, float amount)
    {
        GroundMist.Reset();
        foreach (Tile t in tiles) GroundMist.TakeWalk(t.Mesh, t.World);
        GroundMist.Follow(feet);
        GroundMist.AreaFog(fogOn, amount);
    }

    /// <summary>A fresh area under fog of <see cref="AreaAmount"/>.</summary>
    internal static void Fogged(List<Tile> tiles, Vector3 feet) => Lay(tiles, feet, true, AreaAmount);

    /// <summary>Step the field <paramref name="seconds"/> at 60 fps under <paramref name="wind"/>.</summary>
    internal static void Run(float seconds, Vector2 wind)
    {
        for (int n = (int)MathF.Round(seconds / Frame); n > 0; n--) GroundMist.Step(Frame, wind);
    }

    /// <summary>The mist over the rectangle, summed column by column at each metre's centre: metres of mist times m^2.</summary>
    internal static float Mass(float x0, float y0, float x1, float y1)
    {
        float sum = 0f;
        for (float x = x0 + 0.5f; x < x1; x += 1f)
            for (float y = y0 + 0.5f; y < y1; y += 1f) sum += GroundMist.At(x, y);
        return sum;
    }

    /// <summary>The mean depth of mist over the rectangle.</summary>
    internal static float Mean(float x0, float y0, float x1, float y1)
        => Mass(x0, y0, x1, y1) / MathF.Max((x1 - x0) * (y1 - y0), 1f);

    /// <summary>The mean depth over the square of <paramref name="r"/> metres either side of (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float Near(float x, float y, float r) => Mean(x - r, y - r, x + r, y + r);

    /// <summary>The centre of the mist above <paramref name="floor"/> over the square <paramref name="half"/> metres either
    /// side of the origin, and how much of it there is.</summary>
    internal static Vector2 Centroid(float half, float floor, out float mass)
    {
        Vector2 at = Vector2.Zero;
        mass = 0f;
        for (float x = -half + 0.5f; x < half; x += 1f)
            for (float y = -half + 0.5f; y < half; y += 1f)
            {
                float m = MathF.Max(GroundMist.At(x, y) - floor, 0f);
                at += m * new Vector2(x, y);
                mass += m;
            }
        return mass > 0f ? at / mass : Vector2.Zero;
    }
}
