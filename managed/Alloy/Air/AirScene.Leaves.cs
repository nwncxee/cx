using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The leaves a forest's trunks shed about the player: each is loosed from a crown, tumbles down with the air's
/// drift, races with a live front and is gone once it reaches the floor.</summary>
internal static partial class AirScene
{
    /// <summary>The trunks a forest seats at most, the leaves aloft at once at most, and the records <see cref="PackLeaves"/> writes at most.</summary>
    internal const int MaxTrunks = 4096, MaxLeaves = 128, LeavesPacked = MaxLeaves + 1;

    /// <summary>How near the player's feet a trunk sheds, metres, and how far the feet move before the near trunks are counted again.</summary>
    private const float LeafReach = 16f, NearMove = 2f;

    /// <summary>Leaves loosed a second in calm air, and how many times more for each metre a second of wind.</summary>
    private const float LeafRate = 0.4f, LeafPerSpeed = 1.5f;

    /// <summary>How far from its trunk a crown looses a leaf, metres, and the lowest and highest it looses one over the floor.</summary>
    private const float LeafCrown = 2f, LeafLow = 3f, LeafHigh = 6f;

    /// <summary>How fast a leaf falls, metres a second, the share of the air's drift it takes, and how fast it sways as it tumbles.</summary>
    private const float LeafFall = 0.8f, LeafShare = 1f, LeafSway = 0.4f;

    /// <summary>Turns the leaves' draws from the motes' and the pollen's.</summary>
    private const uint LeafSalt = 0x27D4EB2Fu; // not-an-offset: a hash salt

    /// <summary>Does the area's look shed leaves?</summary>
    private static bool Falls(AirSceneState s) => (s.Look.Layers(s.Night) & AirLayer.Leaves) != 0;

    /// <summary>Seat a trunk at <paramref name="at"/> among <paramref name="l"/>'s; past <see cref="MaxTrunks"/> one sheds none.</summary>
    private static void AddTrunk(AirLeaves l, Vector2 at)
    {
        if (l.Trunks == MaxTrunks) return;
        l.TrunkAt[l.Trunks++] = at;
        l.NearAt = new Vector2(float.NaN);
    }

    /// <summary>Count again the trunks within <see cref="LeafReach"/> of <paramref name="feet"/>.</summary>
    private static void Gather(AirLeaves l, Vector2 feet)
    {
        l.NearAt = feet;
        l.NearCount = 0;
        for (int t = 0; t < l.Trunks; t++)
            if (Vector2.DistanceSquared(l.TrunkAt[t], feet) <= LeafReach * LeafReach) l.Near[l.NearCount++] = t;
    }

    /// <summary>Loose the leaves due by <paramref name="dt"/>, more in a stronger wind, and move every leaf aloft on: its
    /// sway, the air's drift and a live front's rush, falling; a leaf at the floor is gone. Hot: allocates nothing.</summary>
    private static void StepLeaves(AirSceneState s, float dt)
    {
        AirLeaves l = s.Leaves;
        if (l.Trunks == 0 || !Falls(s)) return;
        l.Clock += dt;
        GroundMist.Corner(out _, out _, out Vector2 feet);
        if (!(Vector2.Distance(feet, l.NearAt) <= NearMove)) Gather(l, feet);
        for (l.Due += dt * LeafRate * (1f + LeafPerSpeed * s.Speed); l.Due >= 1f; l.Due -= 1f) Loose(s, l);
        AirFront f = s.Front;
        Vector3 air = new(s.Drift * LeafShare + (f.Live ? f.Dir * (RacePerSpeed * f.Strength) : Vector2.Zero), -LeafFall);
        float follow = 1f - MathF.Exp(-dt / (f.Live ? RaceTau : FollowTau)), fade = MathF.Exp(-dt / KickTau);
        for (int i = 0; i < MaxLeaves; i++)
        {
            if (l.Dark[i]) continue;
            Vector3 w = l.Rate[i] * (2f * l.Clock) + l.Phase[i];
            Float(l, i, air + LeafSway * new Vector3(MathF.Sin(w.X), MathF.Sin(w.Y), 0.5f * MathF.Sin(w.Z)), follow, fade, dt);
            int c = Column(s, l.Pos[i].X, l.Pos[i].Y);
            if (c < 0 || l.Pos[i].Z <= s.Floor[c]) l.Dark[i] = true;
        }
    }

    /// <summary>Loose one leaf from the crown of a trunk near the player into the next free place; none with no trunk near
    /// or every place aloft.</summary>
    private static void Loose(AirSceneState s, AirLeaves l)
    {
        if (l.NearCount == 0) return;
        for (int tries = 0; tries < MaxLeaves; tries++)
        {
            int i = l.Next;
            l.Next = (l.Next + 1) % MaxLeaves;
            if (!l.Dark[i]) continue;
            uint key = ((uint)i * 0x9E3779B1u ^ l.Births[i]++ * 0x85EBCA77u) ^ LeafSalt;
            Vector2 trunk = l.TrunkAt[l.Near[Math.Min((int)(Hash(key, 0) * l.NearCount), l.NearCount - 1)]];
            float r = LeafCrown * MathF.Sqrt(Hash(key, 1)), round = MathF.Tau * Hash(key, 2);
            Vector2 at = trunk + r * new Vector2(MathF.Cos(round), MathF.Sin(round));
            int c = Column(s, at.X, at.Y);
            if (c < 0) return;
            Afloat(l, i, key, new Vector3(at, s.Floor[c] + LeafLow + (LeafHigh - LeafLow) * Hash(key, 9)));
            return;
        }
    }

    /// <summary>Do leaves fall this frame: the air on, in a forest with a trunk seated?</summary>
    internal static bool Falling => _s.Laid && On(_s) && Falls(_s) && _s.Leaves.Trunks > 0;

    /// <summary>Write the leaves aloft into <paramref name="into"/> as the mote stage's sky-lit build reads them: a head
    /// holding their count, then each leaf's place and how
    /// brightly it shows, fading in once loosed. Returns the records written, 0 when none fall. Hot: allocates nothing.</summary>
    internal static int PackLeaves(Span<Vector4> into)
    {
        if (!Falling || into.Length < 1) return 0;
        AirLeaves l = _s.Leaves;
        int n = 0;
        for (int i = 0; i < MaxLeaves && n + 1 < into.Length; i++)
        {
            if (l.Dark[i]) continue;
            into[++n] = new Vector4(l.Pos[i], Math.Clamp((l.Clock - l.Born[i]) / MoteFadeIn, 0f, 1f));
        }
        into[0] = new Vector4(n, 0f, 0f, 0f);
        return n + 1;
    }
}

/// <summary>One area's trunks and the leaves aloft, each leaf out of sight until a crown looses it.</summary>
internal sealed class AirLeaves : AirGrains
{
    /// <summary>Each seated trunk's place on the ground, and how many are seated.</summary>
    public readonly Vector2[] TrunkAt = new Vector2[AirScene.MaxTrunks];
    public int Trunks;

    /// <summary>The trunks near the player as last counted, where the feet stood then and how many there were.</summary>
    public readonly int[] Near = new int[AirScene.MaxTrunks];
    public Vector2 NearAt = new(float.NaN);
    public int NearCount;

    /// <summary>The leaves owed and not yet loosed, and the place the next one is looked for from.</summary>
    public float Due;
    public int Next;

    public AirLeaves() : base(AirScene.MaxLeaves) => Array.Fill(Dark, true);
}
