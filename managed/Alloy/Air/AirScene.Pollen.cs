using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The pollen a meadow holds about the player by day: each grain wanders a path of its own low over the grass,
/// drifts with the air, and is lit by the sun or the moon.</summary>
internal static partial class AirScene
{
    /// <summary>The grains of pollen about the player, and the records <see cref="PackPollen"/> writes at most: a head and one a grain.</summary>
    internal const int MaxPollen = 1024, PollenPacked = MaxPollen + 1;

    /// <summary>How far either side of the player's feet the pollen floats, metres, and the share of that it fades out over at the edge.</summary>
    private const float PollenHalf = 10f, PollenEdge = 0.2f;

    /// <summary>How high over the floor the lowest and the highest grain rests, metres.</summary>
    private const float PollenLow = 0.3f, PollenHigh = 2.5f;

    /// <summary>The share of the air's drift a grain takes, and how fast a grain thrown up or down drifts back to its height, seconds.</summary>
    private const float PollenShare = 1f, PollenSettle = 3f;

    /// <summary>Turns the pollen's draws from the motes', so a grain and a mote of one index take different paths.</summary>
    private const uint PollenSalt = 0x5BD1E995u; // not-an-offset: a hash salt

    /// <summary>Does the area float pollen: was it seeded on entry, which only a look that floats it does?</summary>
    private static bool Glints(AirSceneState s) => s.Pollen.Seeded;

    /// <summary>Set every grain afloat about <paramref name="feet"/>, each at its own height over the floor under it; a place
    /// with no pollen seeds none.</summary>
    private static void SeedPollen(AirSceneState s, Vector2 feet)
    {
        if ((s.Look.Layers(s.Night) & AirLayer.Pollen) == 0) return;
        AirPollen g = s.Pollen;
        g.Seeded = true;
        for (int i = 0; i < MaxPollen; i++)
        {
            uint key = ((uint)i * 0x9E3779B1u ^ g.Births[i]++ * 0x85EBCA77u) ^ PollenSalt;
            float x = feet.X + PollenHalf * (2f * Hash(key, 0) - 1f), y = feet.Y + PollenHalf * (2f * Hash(key, 1) - 1f);
            g.HomeUp[i] = PollenLow + (PollenHigh - PollenLow) * Hash(key, 2);
            int c = Column(s, x, y);
            Afloat(g, i, key, new Vector3(x, y, (c >= 0 ? s.Floor[c] : 0f) + g.HomeUp[i]));
            g.Dark[i] = c < 0;
        }
    }

    /// <summary>Move every grain on by <paramref name="dt"/>: its own wander, the air's drift and a live front's rush, back
    /// toward its height over the floor; a grain that leaves the square about the player comes in at its far side. Hot:
    /// allocates nothing.</summary>
    private static void StepPollen(AirSceneState s, float dt)
    {
        if (!Glints(s)) return;
        AirPollen g = s.Pollen;
        g.Clock += dt;
        AirFront f = s.Front;
        Vector3 air = new(s.Drift * PollenShare + (f.Live ? f.Dir * (RacePerSpeed * f.Strength) : Vector2.Zero), 0f);
        float follow = 1f - MathF.Exp(-dt / (f.Live ? RaceTau : FollowTau)), fade = MathF.Exp(-dt / KickTau);
        GroundMist.Corner(out _, out _, out Vector2 feet);
        for (int i = 0; i < MaxPollen; i++)
        {
            Vector3 p = g.Pos[i];
            int c = Column(s, p.X, p.Y);
            float home = c >= 0 ? (s.Floor[c] + g.HomeUp[i] - p.Z) / PollenSettle : 0f;
            Float(g, i, air + new Vector3(0f, 0f, home), follow, fade, dt);
            p = g.Pos[i];
            p.X = Wrap(p.X, feet.X);
            p.Y = Wrap(p.Y, feet.Y);
            g.Pos[i] = p;
            g.Dark[i] = Column(s, p.X, p.Y) < 0;
        }
    }

    /// <summary><paramref name="v"/> brought into the span <see cref="PollenHalf"/> either side of <paramref name="mid"/>.</summary>
    private static float Wrap(float v, float mid)
    {
        float span = 2f * PollenHalf, off = v - (mid - PollenHalf);
        return mid - PollenHalf + (off - span * MathF.Floor(off / span));
    }

    /// <summary>Does pollen glint this frame: the air on, in a place that floats it?</summary>
    internal static bool Glinting => _s.Laid && On(_s) && Glints(_s);

    /// <summary>Write the lit pollen into <paramref name="into"/> as the mote stage's sky-lit build reads it: a head holding
    /// their count, then each grain's place and how brightly it
    /// shows, fading in once set afloat and out toward the square's edge. Returns the records written, 0 when none
    /// glints. Hot: allocates nothing.</summary>
    internal static int PackPollen(Span<Vector4> into)
    {
        if (!Glinting || into.Length < 1) return 0;
        AirPollen g = _s.Pollen;
        GroundMist.Corner(out _, out _, out Vector2 feet);
        int n = 0;
        for (int i = 0; i < MaxPollen && n + 1 < into.Length; i++)
        {
            if (g.Dark[i]) continue;
            Vector3 p = g.Pos[i];
            float edge = (PollenHalf - MathF.Max(MathF.Abs(p.X - feet.X), MathF.Abs(p.Y - feet.Y))) / (PollenEdge * PollenHalf);
            float glow = Math.Clamp(MathF.Min((g.Clock - g.Born[i]) / MoteFadeIn, edge), 0f, 1f);
            into[++n] = new Vector4(p, glow);
        }
        into[0] = new Vector4(n, 0f, 0f, 0f);
        return n + 1;
    }
}

/// <summary>One area's pollen about the player, and each grain's height over the floor it rests at.</summary>
internal sealed class AirPollen() : AirGrains(AirScene.MaxPollen)
{
    /// <summary>Per grain, metres over the floor it drifts back to.</summary>
    public readonly float[] HomeUp = new float[AirScene.MaxPollen];

    /// <summary>Whether the area's pollen was set afloat.</summary>
    public bool Seeded;
}
