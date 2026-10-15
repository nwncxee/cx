using System;
using System.Numerics;

namespace CX.Services.DayNight;

/// <summary>The engine terms one frame of the machine reads, as values: no engine memory past this point.</summary>
internal struct DayNightReading
{
    /// <summary>The area the rest of the reading came from. A different one resets the machine.</summary>
    public IntPtr Area;
    /// <summary>The area's two palettes, as the slots hold them this frame -- a server fade moves them.</summary>
    public LightSet Night, Day;
    /// <summary>The two light directions the area holds, each pointing TO its body.</summary>
    public Vector3 MoonDir, SunDir;
    public Phase Running;
    /// <summary>The palette the area says it is on.</summary>
    public bool IsNight;
    /// <summary>The module's transition clock as a fraction, elapsed over length. May exceed 1.</summary>
    public float T;
    /// <summary>The area's shadow opacity, 0..100.</summary>
    public byte Opacity;
}

/// <summary>What the machine carries between frames: one owner, replaced rather than patched.</summary>
internal struct DayNightMemory
{
    public IntPtr Area;
    public Phase Running;
    public bool IsNight, Have, Done;
    /// <summary>The highest clock fraction this transition has reached, and the one it started from.</summary>
    public float TSeen, T0;
    /// <summary>Seconds left of a cut crossfade, 0 when none runs.</summary>
    public float CutLeft;
    /// <summary>What was on screen when the running transition began, and the light direction with it.</summary>
    public LightFrame From;
    public Vector3 FromDir;
    /// <summary>The values the machine last applied -- what "from what is on screen" means.</summary>
    public LightFrame Applied;
    public Vector3 Direction;
}

/// <summary>What the machine asks of this frame, and what it diagnosed about the engine's own move.</summary>
internal struct DayNightStep
{
    public LightFrame Frame;
    public Vector3 Direction;
    /// <summary>Apply <see cref="Frame"/>; apply <see cref="Direction"/>. False means the engine's own
    /// values already stand where the machine wants them, so nothing is pushed.</summary>
    public bool Push, Aim;
    /// <summary>Non-negative: hold the module's transition clock at this fraction, because it was rewound.</summary>
    public float HoldT;
    /// <summary>Vanilla cut the light to a palette here; the machine crossfades instead.</summary>
    public bool Cut;
    /// <summary>Vanilla rewound a running transition's clock, replaying it from where it was told.</summary>
    public bool Restart;
    /// <summary>Vanilla flipped the area onto the palette the running transition is leaving.</summary>
    public bool Flip;
}

/// <summary>
/// Owned day/night, one frame at a time: a transition is a crossfade from what is on screen to the
/// destination's steady frame, a palette cut becomes a short crossfade, and the transition clock only ever
/// moves forward. Pure -- the caller reads the engine and applies what comes back.
/// </summary>
internal static class DayNightMachine
{
    /// <summary>One frame. <paramref name="dt"/> is the area update's own delta in seconds.</summary>
    internal static DayNightStep Advance(in DayNightReading r, ref DayNightMemory m, float dt)
    {
        var s = new DayNightStep { HoldT = -1f };
        bool fresh = !m.Have || m.Area != r.Area;
        bool edge = fresh || m.Running != r.Running;
        LightSet live = r.IsNight ? r.Night : r.Day;
        Vector3 liveDir = r.IsNight ? r.MoonDir : r.SunDir;

        if (!edge)
        {
            if (r.Running == Phase.None) s.Cut = r.IsNight != m.IsNight;
            else
            {
                s.Restart = r.T < m.TSeen;
                s.Flip = r.IsNight != m.IsNight && r.IsNight != (r.Running == Phase.Dusk);
            }
        }

        float t = s.Restart ? m.TSeen : r.T;
        if (s.Restart) s.HoldT = t;
        if (fresh) Seed(in r, ref m, t);

        if (r.Running == Phase.None)
        {
            LightFrame to = DayNightBlend.Steady(in live, r.IsNight, r.Opacity);
            // A transition that ran to its end already landed on this; one cut short did not.
            if (edge) m.CutLeft = !fresh && m.Running != Phase.None && !m.Done ? DayNightBlend.CutFade : 0f;
            else if (s.Cut) m.CutLeft = DayNightBlend.CutFade;
            if (m.CutLeft > 0f)
            {
                float k = dt >= m.CutLeft ? 1f : dt / m.CutLeft;
                m.Applied = DayNightBlend.Mix(in m.Applied, in to, k);
                m.Direction = DayNightBlend.Turn(m.Direction, liveDir, k);
                m.CutLeft -= dt;
                s.Push = s.Aim = true;
            }
            else { m.Applied = to; m.Direction = liveDir; }
            m.T0 = 0f;
            m.Done = false;
        }
        else
        {
            bool dawn = r.Running == Phase.Dawn;
            if (edge)
            {
                m.From = m.Applied;
                m.FromDir = m.Direction;
                m.T0 = MathF.Min(MathF.Max(t, 0f), 1f);
                m.CutLeft = 0f;
            }
            LightSet dest = dawn ? r.Day : r.Night;
            LightFrame to = DayNightBlend.Steady(in dest, !dawn, r.Opacity);
            float tt = Progress(t, m.T0);
            m.Applied = DayNightBlend.Mix(in m.From, in to, tt);
            m.Direction = DayNightBlend.Turn(m.FromDir, dawn ? r.SunDir : r.MoonDir, tt);
            m.Done = tt >= 1f;
            s.Push = s.Aim = true;
        }

        m.Have = true;
        m.Area = r.Area;
        m.Running = r.Running;
        m.IsNight = r.IsNight;
        m.TSeen = r.Running == Phase.None ? 0f : t;
        s.Frame = m.Applied;
        s.Direction = m.Direction;
        return s;
    }

    /// <summary>How far a transition has come since the machine picked up its starting frame. A transition
    /// taken over part-way still ends when the engine's clock does, so the rest of it is re-based.</summary>
    internal static float Progress(float t, float t0)
        => t0 >= 1f ? 1f : Math.Clamp((MathF.Min(t, 1f) - t0) / (1f - t0), 0f, 1f);

    /// <summary>The first frame on an area, and the first frame owned mode runs: start from what the engine
    /// shows, dip and all, so taking the policy key over mid-transition is no jump of its own.</summary>
    private static void Seed(in DayNightReading r, ref DayNightMemory m, float t)
    {
        m.CutLeft = 0f;
        m.Done = false;
        if (r.Running == Phase.None)
        {
            LightSet live = r.IsNight ? r.Night : r.Day;
            m.Applied = DayNightBlend.Steady(in live, r.IsNight, r.Opacity);
            m.Direction = r.IsNight ? r.MoonDir : r.SunDir;
            return;
        }
        bool dawn = r.Running == Phase.Dawn;
        LightSet from = dawn ? r.Night : r.Day, to = dawn ? r.Day : r.Night;
        m.Applied = DayNightBlend.Vanilla(in from, in to, t, r.Running, r.Opacity);
        // Vanilla has not swapped the body until the midpoint, which is what the seed must match.
        bool swapped = MathF.Min(t, 1f) >= 0.5f;
        m.Direction = swapped == dawn ? r.SunDir : r.MoonDir;
    }
}
