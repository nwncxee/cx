using System;
using System.Numerics;

namespace CX.Services.Anim;

/// <summary>What an analysed clip is: a walk the stride warp may pace and plant, or why it is not.</summary>
internal enum GaitVerdict : byte { Walk, NoLength, OffTheGround, NotUpright, NoStride, NoStance }

/// <summary>
/// One clip's locomotion, read off its keys once (<see cref="GaitClips"/>): how fast its feet carry the body at rate 1,
/// which way, and per sample of the cycle whether each foot is on the ground and how far through its swing it is.
/// A clip that does not depict walking -- a swim, a crawl, a float, a turn on the spot -- gets a verdict saying so and
/// is left to the engine.
/// </summary>
internal sealed class GaitClip
{
    /// <summary>Samples per clip: a contact edge lands within 1/64 of the cycle, about 15 ms of a walk.</summary>
    internal const int Samples = 64;

    public GaitVerdict Verdict;
    public bool Walk => Verdict == GaitVerdict.Walk;
    /// <summary>The clip's length in seconds, and whether it ends where it starts (a loop).</summary>
    public float Length;
    public bool Cyclic;
    /// <summary>Model units per second the stance foot travels against the body at rate 1, and the body's travel
    /// direction in the model's frame (horizontal, unit).</summary>
    public float Speed;
    public Vector3 Dir;
    /// <summary>Hip-to-ankle length of the leg, model units, from the clip's own bones.</summary>
    public float LegLength;
    /// <summary>Each ankle's lowest height over the clip, model units: the floor its lift is measured from.</summary>
    public float FloorR, FloorL;
    /// <summary>Share of the cycle each foot is on the ground.</summary>
    public float DutyR, DutyL;
    /// <summary>Per sample (<see cref="Samples"/> + 1, the last the clip's end): the foot is on the ground; and 0..1
    /// through its swing, from lifting off to landing, by the clip's own travel of that foot.</summary>
    public bool[] ContactR = [], ContactL = [];
    public float[] SwingR = [], SwingL = [];

    /// <summary>Is the foot on the ground at <paramref name="phase"/> (0..1 of the clip)?</summary>
    internal bool Contact(bool right, float phase) => (right ? ContactR : ContactL)[(int)MathF.Floor(At(phase))];

    /// <summary>How far through its swing the foot is at <paramref name="phase"/>, 0 at lift-off to 1 at landing.</summary>
    internal float Swing(bool right, float phase)
    {
        float x = At(phase);
        int i = (int)MathF.Floor(x);
        float[] u = right ? SwingR : SwingL;
        return i >= Samples ? u[Samples] : u[i] + (u[i + 1] - u[i]) * (x - i);
    }

    /// <summary>The sample position of <paramref name="phase"/>: wrapped on a loop, clamped on a one-shot.</summary>
    private float At(float phase)
    {
        float p = Cyclic ? phase - MathF.Floor(phase) : Math.Clamp(phase, 0f, 1f);
        return p * Samples;
    }

    /// <summary>Analyses one clip from its ankles and hip midpoint sampled at <see cref="Samples"/> + 1 even times over
    /// <paramref name="length"/> seconds, in the model's frame and units, for a leg <paramref name="legLength"/> long.</summary>
    internal static GaitClip Analyse(ReadOnlySpan<Vector3> r, ReadOnlySpan<Vector3> l, ReadOnlySpan<Vector3> hip,
                                     float legLength, float length)
    {
        var c = new GaitClip { Length = length, LegLength = legLength };
        if (!(length > 0f) || !(legLength > 0f)) { c.Verdict = GaitVerdict.NoLength; return c; }
        float close = LoopClose * legLength;
        c.Cyclic = Vector3.Distance(r[0], r[Samples]) < close && Vector3.Distance(l[0], l[Samples]) < close
                   && Vector3.Distance(hip[0], hip[Samples]) < close;
        c.FloorR = c.FloorL = float.MaxValue;
        for (int i = 0; i <= Samples; i++) { c.FloorR = MathF.Min(c.FloorR, r[i].Z); c.FloorL = MathF.Min(c.FloorL, l[i].Z); }
        if (MathF.Min(c.FloorR, c.FloorL) > Grounded * legLength) { c.Verdict = GaitVerdict.OffTheGround; return c; }

        float[] rise = new float[Samples + 1];
        for (int i = 0; i <= Samples; i++) rise[i] = hip[i].Z - MathF.Min(r[i].Z, l[i].Z);
        if (Median(rise) < Upright * legLength) { c.Verdict = GaitVerdict.NotUpright; return c; }

        Vector2[] vr = Velocities(r, c.Cyclic, length), vl = Velocities(l, c.Cyclic, length);
        float band = Band * legLength;
        var xs = new System.Collections.Generic.List<float>();
        var ys = new System.Collections.Generic.List<float>();
        for (int i = 0; i <= Samples; i++)
        {
            if (r[i].Z <= c.FloorR + band) { xs.Add(vr[i].X); ys.Add(vr[i].Y); }
            if (l[i].Z <= c.FloorL + band) { xs.Add(vl[i].X); ys.Add(vl[i].Y); }
        }
        Vector2 stance = new(Median(xs.ToArray()), Median(ys.ToArray()));
        c.Speed = stance.Length();
        if (!(c.Speed >= MinSpeed * legLength)) { c.Verdict = GaitVerdict.NoStride; return c; }
        c.Dir = new Vector3(-stance / c.Speed, 0f);

        c.ContactR = Contacts(r, vr, c.FloorR + band, stance, c.Cyclic);
        c.ContactL = Contacts(l, vl, c.FloorL + band, stance, c.Cyclic);
        c.DutyR = Duty(c.ContactR);
        c.DutyL = Duty(c.ContactL);
        bool stands = c.Cyclic
            ? c.DutyR is >= MinDuty and <= MaxDuty && c.DutyL is >= MinDuty and <= MaxDuty
            : c.DutyR + c.DutyL > 0f;
        if (!stands) { c.Verdict = GaitVerdict.NoStance; return c; }
        c.SwingR = Swings(r, c.ContactR, c.Cyclic);
        c.SwingL = Swings(l, c.ContactL, c.Cyclic);
        c.Verdict = GaitVerdict.Walk;
        return c;
    }

    /// <summary>Share of a leg's length within which a clip's first and last samples are the same pose: a loop.</summary>
    private const float LoopClose = 0.05f;
    /// <summary>Share of a leg's length above the model's origin the lower ankle must reach: higher, the feet never
    /// touch the floor the body stands on (a float).</summary>
    private const float Grounded = 0.25f;
    /// <summary>Share of a leg's length the hips stand above the lower ankle, typically: below it the body is not
    /// carried on its legs (a crawl keeps about 0.35, a swim less). A crouched sneak keeps well over it.</summary>
    private const float Upright = 0.5f;
    /// <summary>Share of a leg's length above its lowest an ankle may be and still bear weight (heel-off included).</summary>
    private const float Band = 0.06f;
    /// <summary>Legs' lengths per second, at rate 1, under which the feet carry the body nowhere: a stand or a turn.</summary>
    private const float MinSpeed = 0.2f;
    /// <summary>Share of the stance speed a foot on the ground may differ from it by and still be bearing weight.</summary>
    private const float Slip = 0.4f;
    /// <summary>A loop's share of the cycle each foot must spend on the ground: outside it the feet do not take turns.</summary>
    private const float MinDuty = 0.15f, MaxDuty = 0.85f;
    /// <summary>Samples: a gap in contact this short is filled, then a contact this short dropped.</summary>
    private const int Gap = 2, MinRun = 4;

    /// <summary>Horizontal velocity per sample, model units per second at rate 1: central differences, wrapped on a loop.</summary>
    private static Vector2[] Velocities(ReadOnlySpan<Vector3> p, bool cyclic, float length)
    {
        var v = new Vector2[Samples + 1];
        float dt = length / Samples;
        for (int i = 0; i <= Samples; i++)
        {
            int a = i - 1, b = i + 1;
            if (cyclic) { a = (a + Samples) % Samples; b %= Samples; }
            else { a = Math.Max(a, 0); b = Math.Min(b, Samples); }
            float span = cyclic ? 2f * dt : (b - a) * dt;
            Vector3 d = p[b] - p[a];
            v[i] = new Vector2(d.X, d.Y) / span;
        }
        return v;
    }

    /// <summary>Per sample, the foot is low and moving with the stance: its weight is on it. Short gaps are filled and
    /// short contacts dropped, wrapping on a loop; the closing sample repeats the first on a loop.</summary>
    private static bool[] Contacts(ReadOnlySpan<Vector3> p, Vector2[] v, float top, Vector2 stance, bool cyclic)
    {
        int n = cyclic ? Samples : Samples + 1;
        var on = new bool[Samples + 1];
        float slip = Slip * stance.Length();
        for (int i = 0; i < n; i++) on[i] = p[i].Z <= top && Vector2.Distance(v[i], stance) <= slip;
        Clean(on, n, cyclic, true, Gap);
        Clean(on, n, cyclic, false, MinRun - 1);
        if (cyclic) on[Samples] = on[0];
        return on;
    }

    /// <summary>Flips every run of <paramref name="value"/>'s opposite no longer than <paramref name="max"/> samples that
    /// sits between two runs of <paramref name="value"/> (or, filling, any short one): fill gaps, or drop short contacts.</summary>
    private static void Clean(bool[] on, int n, bool cyclic, bool value, int max)
    {
        int start = -1;
        for (int i = 0; i < n; i++) if (on[i] == value) { start = i; break; }
        if (start < 0) return;
        for (int k = 1; k <= n; k++)
        {
            int i = (start + k) % n;
            if (!cyclic && start + k >= n) break;
            if (on[i] == value) continue;
            int len = 0;
            while (len < n && on[(i + len) % n] != value && (cyclic || i + len < n)) len++;
            bool bounded = cyclic || i + len < n;
            if (len <= max && bounded) for (int j = 0; j < len; j++) on[(i + j) % n] = value;
            k += len - 1;
        }
    }

    private static float Duty(bool[] on)
    {
        int n = 0;
        for (int i = 0; i < Samples; i++) if (on[i]) n++;
        return (float)n / Samples;
    }

    /// <summary>Per sample, how far through its swing the foot is: its travel along the chord from where it lifted off to
    /// where it lands, never going back; time through the swing when the chord is too short to read. 1 on the ground.</summary>
    private static float[] Swings(ReadOnlySpan<Vector3> p, bool[] on, bool cyclic)
    {
        int n = cyclic ? Samples : Samples + 1;
        var u = new float[Samples + 1];
        for (int i = 0; i <= Samples; i++) u[i] = 1f;
        for (int i = 0; i < n; i++)
        {
            if (on[i]) continue;
            int prev = i - 1;
            if (cyclic) { if (on[(prev + n) % n] == false) continue; }
            else if (prev >= 0 && !on[prev]) continue;
            int len = 0;
            while (len < n && !on[(i + len) % n] && (cyclic || i + len < n)) len++;
            int lift = prev, land = i + len;
            bool hasLift = cyclic || lift >= 0, hasLand = cyclic || land < n;
            Vector3 from = hasLift ? p[(lift + n) % n] : p[i], to = hasLand ? p[land % n] : p[Math.Min(land, Samples)];
            Vector2 chord = new(to.X - from.X, to.Y - from.Y);
            float chord2 = chord.LengthSquared(), best = 0f;
            for (int j = 0; j < len; j++)
            {
                int s = (i + j) % n;
                float time = (j + 1f) / (len + 1f), along = time;
                if (chord2 > 1e-8f && hasLift && hasLand)
                    along = Math.Clamp(Vector2.Dot(new Vector2(p[s].X - from.X, p[s].Y - from.Y), chord) / chord2, 0f, 1f);
                best = MathF.Max(best, along);
                u[s] = best;
            }
            i += len - 1;
        }
        if (cyclic) u[Samples] = u[0];
        return u;
    }

    /// <summary>The middle value (mean of the two middle ones for an even count); 0 for none. Sorts in place.</summary>
    private static float Median(float[] v)
    {
        if (v.Length == 0) return 0f;
        Array.Sort(v);
        int m = v.Length / 2;
        return v.Length % 2 == 1 ? v[m] : 0.5f * (v[m - 1] + v[m]);
    }

    /// <summary>The analysis against hand-built trajectories whose answers are worked out by hand: a walk whose
    /// stance foot travels back at exactly 1.5 units/s for 60 % of a 1 s cycle, and a swim, a crawl, a float and a
    /// stand that must each be refused for their own reason. The oracle is the analytic trajectory, not the analyser.</summary>
    internal static bool SelfCheck()
    {
        GaitClip walk = Synthetic(0.85f, 0.08f, 1.5f, 0.12f);
        bool walks = walk.Walk && walk.Cyclic && MathF.Abs(walk.Speed - 1.5f) < 0.03f && walk.Dir.Y > 0.999f
                     && MathF.Abs(walk.DutyR - 0.6f) < 0.04f && MathF.Abs(walk.DutyL - 0.6f) < 0.04f
                     && walk.Contact(true, 0.3f) && !walk.Contact(true, 0.8f) && walk.Contact(false, 0.8f) && !walk.Contact(false, 0.3f)
                     && walk.Contact(true, 1.3f) && MathF.Abs(walk.Swing(true, 0.8f) - 0.5f) < 0.03f
                     && MathF.Abs(walk.FloorR - 0.08f) < 1e-4f;
        bool crawl = Synthetic(0.4f, 0.08f, 1.5f, 0.12f).Verdict == GaitVerdict.NotUpright;
        bool floats = Synthetic(2.35f, 1.5f, 1.5f, 0.12f).Verdict == GaitVerdict.OffTheGround;
        bool stands = Synthetic(0.85f, 0.08f, 0f, 0f).Verdict == GaitVerdict.NoStride;

        Span<Vector3> r = stackalloc Vector3[Samples + 1], l = stackalloc Vector3[Samples + 1], h = stackalloc Vector3[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            float a = 2f * MathF.PI * i / Samples;
            h[i] = new Vector3(0f, 0f, 0.3f);
            r[i] = new Vector3(0.1f, -0.5f + 0.05f * MathF.Cos(a), 0.3f + 0.1f * MathF.Sin(a));
            l[i] = new Vector3(-0.1f, -0.5f - 0.05f * MathF.Cos(a), 0.3f - 0.1f * MathF.Sin(a));
        }
        bool swims = Analyse(r, l, h, 0.9f, 1f).Verdict == GaitVerdict.NotUpright;
        return walks && crawl && floats && stands && swims;
    }

    /// <summary>A 1 s, 0.9-long-legged biped cycle with the hips at <paramref name="hipZ"/>: each foot travels back at
    /// <paramref name="speed"/> on the ground at <paramref name="floor"/> for 60 % of the cycle and swings forward on
    /// a smoothstep, <paramref name="lift"/> high at mid-swing; the left a half cycle behind the right.</summary>
    internal static GaitClip Synthetic(float hipZ, float floor, float speed, float lift)
    {
        Span<Vector3> r = stackalloc Vector3[Samples + 1], l = stackalloc Vector3[Samples + 1], h = stackalloc Vector3[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            float phase = (float)i / Samples;
            h[i] = new Vector3(0f, 0f, hipZ);
            r[i] = SyntheticFoot(phase, 0.1f, floor, speed, lift);
            l[i] = SyntheticFoot(phase + 0.5f, -0.1f, floor, speed, lift);
        }
        return Analyse(r, l, h, 0.9f, 1f);
    }

    /// <summary>The synthetic foot of <see cref="Synthetic"/> at <paramref name="phase"/>, model frame, body travelling +Y.</summary>
    internal static Vector3 SyntheticFoot(float phase, float x, float floor, float speed, float lift)
    {
        const float Down = 0.6f;
        float q = phase - MathF.Floor(phase), half = speed * Down * 0.5f;
        if (q < Down) return new Vector3(x, half - speed * q, floor);
        float w = (q - Down) / (1f - Down);
        return new Vector3(x, -half + 2f * half * w * w * (3f - 2f * w), floor + lift * MathF.Sin(MathF.PI * w));
    }
}
