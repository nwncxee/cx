using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Services.AreaSettings;
using CX.Services.Chat;
using CX.Ui;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The places the air's approved examples stand in: a crypt room, a meadow, a grove, a quay, built from
/// walkmesh tiles and handed to the air the way an area load does.</summary>
internal static partial class AirFixture
{
    /// <summary>The bits of an area's own flags, as the engine sets them and the sound's material reads them.</summary>
    internal const uint Interior = 1, Underground = 2, Natural = 4;

    /// <summary>A crypt room's half width: stone floor inside, unwalkable wall outside.</summary>
    internal const float RoomHalf = 8f;

    /// <summary>A lamp post's reach, in metres.</summary>
    internal const float LampReach = 5f;

    /// <summary>The lamp the lamplit examples stand by: 2 m up, 4 m east of the player.</summary>
    internal static readonly Vector3 Lamp = new(4f, 0f, 2f);

    /// <summary>A strong area wind from the west, and a weak one, as the area's palette sets them.</summary>
    internal static readonly Vector2 Strong = HighWindX, Weak = new(0.5f, 0f);

    /// <summary>A strong wind from the east, and the same strength from the north, as a script sets them.</summary>
    internal static readonly Vector2 FromEast = new(-2f, 0f), FromNorth = new(0f, -2f);

    /// <summary>A standing character's knee, in metres above the floor: the dust is gone by it.</summary>
    internal const float Knee = 0.5f;

    /// <summary>A height just over the knee, where no dust lies unless something lifts it.</summary>
    internal const float OverKnee = Knee + 0.1f;

    /// <summary>A particle slower than this, in metres a second, drifts; faster, it is thrown or races.</summary>
    internal const float Slow = 0.5f;

    /// <summary>The seconds a night takes to gather its mist over still water.</summary>
    internal const float Gather = 300f;

    /// <summary>The ground layers: what "lies on the ground" means in an example.</summary>
    internal const AirLayer Lying = AirLayer.Dust | AirLayer.NightMist;

    /// <summary>The top of a column the readers sum, and the height of each step up it, in metres.</summary>
    private const float Ceiling = 4f, Rise = 0.1f;

    /// <summary>The four looks the places take, as their features give them.</summary>
    internal static readonly AirLook Crypt = new(AirPlace.Crypt, false, false, false),
        Meadow = new(AirPlace.Meadow, false, false, false), Forest = new(AirPlace.Forest, false, false, false),
        City = new(AirPlace.City, false, true, false), DryCity = new(AirPlace.City, false, false, false);

    /// <summary>A crypt room: stone floor <see cref="RoomHalf"/> either side of the origin, walls round it.</summary>
    internal static List<Tile> Room()
        => Ground(20f, 1f, (_, _) => 0f, (x, y) => MathF.Abs(x) < RoomHalf && MathF.Abs(y) < RoomHalf ? Stone : Nonwalk);

    /// <summary>A quay: stone street west of x = 0, open water east of it.</summary>
    internal static List<Tile> Quay() => Ground(40f, 1f, (_, _) => 0f, (x, _) => x < 0f ? Stone : Water);

    /// <summary>Open ground with a 2 m stream along x = 0.</summary>
    internal static List<Tile> Stream() => Ground(40f, 1f, (_, _) => 0f, (x, _) => MathF.Abs(x) < 1f ? Water : Stone);

    /// <summary>The trunks of a grove: one every 4 m west of x = 0, a clearing east of it.</summary>
    internal static IEnumerable<Vector2> Grove()
    {
        for (float x = -38f; x < 0f; x += 4f)
            for (float y = -38f; y < 40f; y += 4f) yield return new Vector2(x, y);
    }

    /// <summary>A fresh area of <paramref name="tiles"/> under <paramref name="look"/>, its fog amount
    /// <paramref name="amount"/>, the player at the origin, by night or by day.</summary>
    internal static void Enter(AirLook look, List<Tile> tiles, float amount, bool night)
    {
        Lay(tiles, Vector3.Zero, true, amount);
        AirScene.Enter(look, night);
    }

    /// <summary><see cref="Enter"/> with a grove's trunks and the lamp at <see cref="Lamp"/> seated after it.</summary>
    internal static void EnterWith(AirLook look, List<Tile> tiles, float amount, bool night, bool trunks, bool lamp)
    {
        Enter(look, tiles, amount, night);
        if (trunks) foreach (Vector2 t in Grove()) AirScene.Trunk(t);
        if (lamp) AirScene.Lamp(Lamp, LampReach);
    }

    /// <summary>Step the whole air <paramref name="seconds"/> at 60 fps under the area wind <paramref name="wind"/>.</summary>
    internal static void Go(float seconds, Vector2 wind)
    {
        for (int n = (int)MathF.Round(seconds / Frame); n > 0; n--) AirScene.Step(Frame, wind);
    }

    /// <summary>The mean of <paramref name="layer"/>'s density over the square of half side <paramref name="r"/> round
    /// (<paramref name="x"/>, <paramref name="y"/>), <paramref name="z"/> above the ground.</summary>
    internal static float Thick(AirLayer layer, float x, float y, float r, float z)
    {
        float sum = 0f;
        int n = 0;
        for (float px = x - r + 0.5f; px < x + r; px += 1f)
            for (float py = y - r + 0.5f; py < y + r; py += 1f) { sum += AirScene.Density(layer, new Vector3(px, py, z)); n++; }
        return n > 0 ? sum / n : 0f;
    }

    /// <summary><paramref name="layers"/>' depth at (<paramref name="x"/>, <paramref name="y"/>): the air's density summed up
    /// the column to <see cref="Ceiling"/>, in metres of full density.</summary>
    internal static float Column(AirLayer layers, float x, float y)
    {
        float sum = 0f;
        for (int bit = 1; bit <= (int)layers; bit <<= 1)
        {
            if (((int)layers & bit) == 0) continue;
            for (float z = Rise / 2f; z < Ceiling; z += Rise) sum += AirScene.Density((AirLayer)bit, new Vector3(x, y, z)) * Rise;
        }
        return sum;
    }

    /// <summary>The mean <see cref="Column"/> of <paramref name="layers"/> over the rectangle, read at each metre's centre.</summary>
    internal static float Mean(AirLayer layers, float x0, float y0, float x1, float y1)
    {
        float sum = 0f;
        int n = 0;
        for (float x = x0 + 0.5f; x < x1; x += 1f)
            for (float y = y0 + 0.5f; y < y1; y += 1f) { sum += Column(layers, x, y); n++; }
        return n > 0 ? sum / n : 0f;
    }

    /// <summary>The mean depth of <paramref name="layers"/> over the square of <paramref name="r"/> metres either side of
    /// (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float Near(AirLayer layers, float x, float y, float r) => Mean(layers, x - r, y - r, x + r, y + r);

    /// <summary>The centre of <paramref name="layers"/> deeper than <paramref name="floor"/> over the square
    /// <paramref name="half"/> metres either side of the origin, and how much of it there is.</summary>
    internal static Vector2 Centroid(AirLayer layers, float half, float floor, out float mass)
    {
        Vector2 at = Vector2.Zero;
        mass = 0f;
        for (float x = -half + 0.5f; x < half; x += 1f)
            for (float y = -half + 0.5f; y < half; y += 1f)
            {
                float m = MathF.Max(Column(layers, x, y) - floor, 0f);
                at += m * new Vector2(x, y);
                mass += m;
            }
        return mass > 0f ? at / mass : Vector2.Zero;
    }

    /// <summary>The mean depth of <paramref name="layers"/> along a 5 m walk at <paramref name="y"/> from
    /// <paramref name="x"/>, its ends left out.</summary>
    internal static float Path(AirLayer layers, float x, float y) => Mean(layers, x + 1f, y - 0.5f, x + 4f, y + 0.5f);

    /// <summary>The mean depth of <paramref name="layers"/> a metre and a half beside that walk.</summary>
    internal static float Beside(AirLayer layers, float x, float y) => Mean(layers, x + 1f, y + 1.5f, x + 4f, y + 2.5f);

    /// <summary>The bytes the fog stage is handed.</summary>
    internal static readonly byte[] Sent = new byte[GroundMist.PackedBytes];

    /// <summary>Does the fog stage draw the air, read from a first upload as a fresh fog pass takes it?</summary>
    internal static bool Drawn()
    {
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        return MemoryMarshal.Read<Vector4>(Sent).W > 0f;
    }

    /// <summary>How many of <paramref name="layer"/>'s particles lie within <paramref name="r"/> of <paramref name="at"/>.</summary>
    internal static int Within(AirLayer layer, Vector3 at, float r)
    {
        int n = 0;
        foreach (AirParticle p in AirScene.Particles(layer)) if (Vector3.Distance(p.Pos, at) <= r) n++;
        return n;
    }

    /// <summary>The mean velocity of <paramref name="layer"/>'s particles within <paramref name="r"/> of <paramref name="at"/>.</summary>
    internal static Vector3 Drift(AirLayer layer, Vector3 at, float r)
    {
        Vector3 sum = Vector3.Zero;
        int n = 0;
        foreach (AirParticle p in AirScene.Particles(layer))
            if (Vector3.Distance(p.Pos, at) <= r) { sum += p.Vel; n++; }
        return n > 0 ? sum / n : Vector3.Zero;
    }

    /// <summary>The fastest any of <paramref name="layer"/>'s particles within <paramref name="r"/> of <paramref name="at"/> moves.</summary>
    internal static float Fastest(AirLayer layer, Vector3 at, float r)
    {
        float most = 0f;
        foreach (AirParticle p in AirScene.Particles(layer))
            if (Vector3.Distance(p.Pos, at) <= r) most = MathF.Max(most, p.Vel.Length());
        return most;
    }

    /// <summary>How fast, on the mean, <paramref name="layer"/>'s particles within <paramref name="r"/> of <paramref name="at"/>
    /// move away from it, in metres a second.</summary>
    internal static float Outward(AirLayer layer, Vector3 at, float r)
    {
        float sum = 0f;
        int n = 0;
        foreach (AirParticle p in AirScene.Particles(layer))
        {
            Vector3 d = p.Pos - at;
            if (d.Length() is float len && len > 0.1f && len <= r) { sum += Vector3.Dot(p.Vel, d / len); n++; }
        }
        return n > 0 ? sum / n : 0f;
    }

    /// <summary>The area's variables <paramref name="vars"/> (knob keys, the <c>cx.</c> prefix dropped) held as an area
    /// load holds them, the last area's holds released first.</summary>
    internal static void Hold(params (string Key, string Value)[] vars)
        => AreaSettingsService.Apply(AreaSettings.Holds(new List<(string, string)>(vars), KnobGroup.Find));

    /// <summary>An area with no <c>cx.</c> variables entered: every hold the last area made released.</summary>
    internal static void Unhold() => AreaSettingsService.Apply(new List<(string, string)>());

    /// <summary>The features of an area of <paramref name="n"/> tiles on <paramref name="tileset"/>, each tile's four
    /// corners from <paramref name="corner"/> and its edges from <paramref name="edge"/>.</summary>
    internal static AreaFeatures Features(string tileset, uint flags, int n, Func<int, string[]> corner, Func<int, string[]> edge)
    {
        var corners = new List<string[]>();
        var edges = new List<string[]>();
        for (int i = 0; i < n; i++) { corners.Add(corner(i)); edges.Add(edge(i)); }
        return AreaFeatures.Of(tileset, flags, corners, edges);
    }

    /// <summary>An area of 64 tiles on <paramref name="tileset"/> whose every corner is <paramref name="corner"/>, with no edges.</summary>
    internal static AreaFeatures Plain(string tileset, uint flags, string corner)
        => Features(tileset, flags, 64, _ => new[] { corner, corner, corner, corner }, _ => new[] { "", "", "", "" });

    /// <summary>The look an area with no <c>cx.</c> variables takes from its features.</summary>
    internal static AirLook LookOf(in AreaFeatures f) => AirPlaces.Resolve(f);

    /// <summary>Is the row of <paramref name="k"/> in <paramref name="g"/>'s settings panel greyed? Null when no row shows it.</summary>
    internal static bool? Greyed(KnobGroup g, Knob k)
    {
        var p = new KnobPanel();
        p.OpenAt(1280f, 720f, 24f, ChatExamplesKit.PanelFace(), ChatExamplesKit.PanelFace());
        p._subs = new[] { new KnobPanel.SubMenu(g) }; p.Sub = 0;
        p.LayoutRows();
        foreach (KnobPanel.Row r in p.Rows) if (ReferenceEquals(r.K, k)) return p.Greyed(r);
        return null;
    }

    /// <summary>The air switches an air suite runs under, whatever the player's cx.cfg holds: the air on at Medium, its
    /// dust at the default, Volumetric fog on. Disposing it releases every hold and puts the player's back.</summary>
    internal readonly struct Switches : IDisposable
    {
        private readonly bool _on, _fog;
        private readonly string _quality;
        private readonly float _dust;

        private Switches(bool on, string quality, float dust, bool fog) { _on = on; _quality = quality; _dust = dust; _fog = fog; }

        /// <summary>Saves the player's switches and turns the air on at Medium under Volumetric fog.</summary>
        internal static Switches Force()
        {
            var was = new Switches(Air.Enabled.V, Air.Quality.V, Air.Dust.V, VolumetricFog.Enabled.V);
            Air.Enabled.V = true;
            Air.Quality.V = "medium";
            Air.Dust.V = Air.Dust.Default;
            VolumetricFog.Enabled.V = true;
            return was;
        }

        /// <summary>Releases the suite's holds, puts the player's switches back and clears the air the suite laid.</summary>
        public void Dispose()
        {
            Unhold();
            Air.Enabled.V = _on;
            Air.Quality.V = _quality;
            Air.Dust.V = _dust;
            VolumetricFog.Enabled.V = _fog;
            GroundMist.Reset();
            AirScene.Reset();
        }
    }

    /// <summary>A walker crossing the scene <paramref name="world"/> from <paramref name="from"/> to <paramref name="to"/> in
    /// exactly <paramref name="frames"/> frames, the scene's animate step and the air's run each frame.</summary>
    internal static void Walk(DbId world, DbId who, Vector3 from, Vector3 to, bool player, int frames)
    {
        for (int n = 1; n <= frames; n++)
        {
            Vector3 p = Vector3.Lerp(from, to, (float)n / frames);
            MistPartingApprovedExamples.Place(who, p);
            if (player) GroundMist.Follow(p);
            DbAnim.Animate(world, Frame);
            AirScene.Step(Frame, Vector2.Zero);
        }
    }

    /// <summary>The frames a walk of <paramref name="metres"/> takes at a walking pace of 1.5 m/s.</summary>
    internal static int Pace(float metres) => (int)MathF.Round(metres / 1.5f / Frame);

    /// <summary>The frames in <paramref name="seconds"/> at 60 fps.</summary>
    internal static int Frames(float seconds) => (int)MathF.Round(seconds / Frame);

    /// <summary>Hold <paramref name="world"/> still for exactly <paramref name="frames"/> frames, its animate step and the
    /// air running.</summary>
    internal static void Still(DbId world, int frames)
    {
        for (int n = frames; n > 0; n--) { DbAnim.Animate(world, Frame); AirScene.Step(Frame, Vector2.Zero); }
    }
}
