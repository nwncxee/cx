using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.DayNight;

/// <summary>
/// Off-device proof of the day/night seam. Oracle: the two palettes and every expected value below are
/// hand-written literals worked out from <c>CNWCArea::UpdateDayNightCycle</c>'s disassembly (the lerp, the
/// <c>|2t-1|</c> shaping, <c>0.05</c>, <c>0.5</c>, <c>100</c>) and from the engine's own setters.
/// </summary>
internal static class DayNightSelfCheck
{
    /// <summary>The area's night (moon) palette in the fixture.</summary>
    private static LightSet Night => new()
    {
        Ambient = new Vector3(0.10f, 0.20f, 0.30f),
        Diffuse = new Vector3(0.40f, 0.50f, 0.60f),
        Fog = new Vector3(0.70f, 0.80f, 0.90f),
        FogAmount = 20f,
    };

    /// <summary>The area's day (sun) palette in the fixture.</summary>
    private static LightSet Day => new()
    {
        Ambient = new Vector3(0.50f, 0.60f, 0.70f),
        Diffuse = new Vector3(0.80f, 0.90f, 1.00f),
        Fog = new Vector3(0.10f, 0.20f, 0.30f),
        FogAmount = 60f,
    };

    private const byte Opacity = 80;
    private static Vector3 MoonDir => new(0f, 0f, 1f);
    private static Vector3 SunDir => new(1f, 0f, 0f);

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= 1e-5f;

    private static bool Near(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    private static bool Near(in LightSet a, in LightSet b)
        => Near(a.Ambient, b.Ambient) && Near(a.Diffuse, b.Diffuse) && Near(a.Fog, b.Fog)
        && Near(a.FogAmount, b.FogAmount);

    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "DayNight.SelfCheck");
        Vanilla(check);
        Owned(check);
        Replay(check);
        Policy(check);
        log.Info(check.Ok ? "DayNight.SelfCheck: passed" : "DayNight.SelfCheck: FAILED");
    }

    /// <summary>The ported vanilla shaping, against values computed by hand off the disassembly.</summary>
    private static void Vanilla(Checker check)
    {
        LightSet night = Night, day = Day;

        // Dusk at t = 0.25: the crossfade is day*0.75 + night*0.25, then |2t-1| = 0.5 moves half the lost
        // diffuse into ambient and halves the diffuse, and the beam alpha is the negative (t-1)*0.05.
        LightFrame v = DayNightBlend.Vanilla(in day, in night, 0.25f, Phase.Dusk, Opacity);
        check.Expect(Near(v.Light.Ambient, new Vector3(0.575f, 0.700f, 0.825f)), "vanilla dusk ambient at t=0.25 takes half the lost diffuse");
        check.Expect(Near(v.Light.Diffuse, new Vector3(0.350f, 0.400f, 0.450f)), "vanilla dusk diffuse at t=0.25 is the crossfade halved");
        check.Expect(Near(v.Light.Fog, new Vector3(0.250f, 0.350f, 0.450f)), "vanilla dusk fog colour at t=0.25 is the plain crossfade");
        check.Expect(Near(v.Light.FogAmount, 50f), "vanilla dusk fog amount at t=0.25 is the plain crossfade");
        check.Expect(Near(v.ShadowAlpha, 0.40f), "vanilla dusk shadow alpha at t=0.25 is opacity x |2t-1| / 100");
        check.Expect(Near(v.ProjectionAlpha, -0.0375f), "vanilla dusk beam alpha at t=0.25 is the negative (t-1)*0.05");

        // Dawn at the midpoint: no direct light at all, half of it in ambient, shadows fully off.
        LightFrame mid = DayNightBlend.Vanilla(in night, in day, 0.5f, Phase.Dawn, Opacity);
        check.Expect(Near(mid.Light.Diffuse, Vector3.Zero), "vanilla loses all direct light at the midpoint");
        check.Expect(Near(mid.Light.Ambient, new Vector3(0.60f, 0.75f, 0.90f)), "vanilla midpoint ambient is the crossfade plus half the diffuse");
        check.Expect(Near(mid.ShadowAlpha, 0f), "vanilla blanks the shadows at the midpoint");
        check.Expect(Near(mid.ProjectionAlpha, 0.025f), "vanilla dawn beam alpha at the midpoint is t*0.05");

        // The two ends take the `|2t-1| <= -1` and `>= 1` branches, where no shaping applies at all.
        LightFrame start = DayNightBlend.Vanilla(in night, in day, 0f, Phase.Dawn, Opacity);
        check.Expect(Near(in start.Light, in night) && Near(start.ShadowAlpha, 0.80f) && Near(start.ProjectionAlpha, 0f),
            "a dawn opens on the night palette at full shadows");
        LightFrame over = DayNightBlend.Vanilla(in night, in day, 1.7f, Phase.Dawn, Opacity);
        check.Expect(Near(in over.Light, in day) && Near(over.ShadowAlpha, 0.80f) && Near(over.ProjectionAlpha, 0.05f),
            "a clock past its length clamps to the day palette, not past it");
    }

    /// <summary>The owned crossfade: the same endpoints, no shaping between them.</summary>
    private static void Owned(Checker check)
    {
        LightSet night = Night, day = Day;
        LightFrame from = DayNightBlend.Steady(in night, isNight: true, Opacity);
        LightFrame to = DayNightBlend.Steady(in day, isNight: false, Opacity);
        check.Expect(Near(from.ShadowAlpha, 0.80f) && Near(from.ProjectionAlpha, 0f)
                  && Near(to.ProjectionAlpha, 0.05f), "a steady frame is the palette at full shadows and its own beam alpha");

        LightFrame mid = DayNightBlend.Mix(in from, in to, 0.5f);
        check.Expect(Near(mid.Light.Diffuse, new Vector3(0.60f, 0.70f, 0.80f)), "owned mode keeps the full crossfaded diffuse at the midpoint");
        check.Expect(Near(mid.Light.Ambient, new Vector3(0.30f, 0.40f, 0.50f)), "owned mode adds nothing to ambient");
        check.Expect(Near(mid.ShadowAlpha, 0.80f), "owned mode never fades the shadows out");
        check.Expect(Near(mid.ProjectionAlpha, 0.025f), "owned dawn beam alpha matches vanilla's own dawn curve");

        LightFrame duskMid = DayNightBlend.Mix(in to, in from, 0.25f);
        check.Expect(Near(duskMid.ProjectionAlpha, 0.0375f), "owned dusk beam alpha is the positive (1-t)*0.05");

        check.Expect(Near(DayNightMachine.Progress(0.6f, 0.2f), 0.5f), "a transition taken over at t=0.2 is half done at t=0.6");
        check.Expect(Near(DayNightMachine.Progress(0.4f, 0.4f), 0f) && Near(DayNightMachine.Progress(2f, 0.4f), 1f),
            "re-based progress starts at 0 and stops at 1");
        check.Expect(Near(DayNightBlend.Turn(MoonDir, SunDir, 0.5f), new Vector3(0.70710678f, 0f, 0.70710678f)),
            "two directions a quarter turn apart meet half way along the arc");
        check.Expect(Near(DayNightBlend.Turn(MoonDir, -MoonDir, 0.4f), MoonDir)
                  && Near(DayNightBlend.Turn(MoonDir, -MoonDir, 0.6f), -MoonDir),
            "opposed directions have no short arc, so they swap at the midpoint as vanilla does");
    }

    /// <summary>The dawn an extra time-of-day broadcast ruins, replayed frame by frame: the dawn-hour state
    /// arriving on an area already lit for day, a forged repeat, a transition cut short, and a plain cut.</summary>
    private static void Replay(Checker check)
    {
        LightSet night = Night, day = Day;
        var m = default(DayNightMemory);
        var r = new DayNightReading
        {
            Area = 1, Night = night, Day = day, MoonDir = MoonDir, SunDir = SunDir,
            Running = Phase.None, IsNight = false, T = 0f, Opacity = Opacity,
        };

        // The server has already made it day. Nothing is pushed: the engine's own values stand there.
        DayNightStep s = DayNightMachine.Advance(in r, ref m, 0.016f);
        check.Expect(!s.Push && Near(in s.Frame.Light, in day), "a steady day needs nothing pushed");

        // The engine's dawn hour: SetAsNight cuts the area to night and SetAsDawn starts an hour of dawn.
        // Vanilla shows the night palette here; owned mode must still show day.
        r.Running = Phase.Dawn;
        r.IsNight = true;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        LightFrame vanillaHere = DayNightBlend.Vanilla(in night, in day, 0f, Phase.Dawn, Opacity);
        check.Expect(Near(in vanillaHere.Light, in night), "vanilla would have shown the night palette at the dawn's first frame");
        check.Expect(s.Push && Near(in s.Frame.Light, in day) && Near(s.Frame.ProjectionAlpha, 0.05f),
            "the dawn-hour state does not flip an area that is already lit for day");
        check.Expect(Near(s.Direction, SunDir), "and it does not point the light back at the moon");

        // The engine swaps the body at the midpoint. Owned mode is already there, and keeps its shadows.
        r.T = 0.5f;
        r.IsNight = false;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        check.Expect(!s.Flip && Near(in s.Frame.Light, in day) && Near(s.Frame.ShadowAlpha, 0.80f),
            "the midpoint body swap is not a dip and not a flip");

        // A forged repeat of the same state rewinds the module's clock to zero.
        r.T = 0f;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        check.Expect(s.Restart && Near(s.HoldT, 0.5f) && Near(in s.Frame.Light, in day),
            "a repeated time-of-day state holds the clock where it was instead of replaying the dawn");

        // ...and the real clock runs on from there.
        r.T = 1f;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        check.Expect(!s.Restart && Near(s.HoldT, -1f), "a clock that moves forward is left alone");
        r.Running = Phase.None;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        check.Expect(!s.Push, "a transition that ran to its end leaves the engine's own values standing");

        // A dusk cut short by a day state: the part-done transition must glide, not snap back.
        r.Running = Phase.Dusk;
        r.T = 0f;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        r.T = 0.5f;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        LightFrame half = s.Frame;
        check.Expect(Near(half.Light.Ambient, new Vector3(0.30f, 0.40f, 0.50f)), "a dusk half done is half way between the palettes");
        r.Running = Phase.None;
        r.IsNight = false;
        s = DayNightMachine.Advance(in r, ref m, DayNightBlend.CutFade / 2f);
        check.Expect(s.Push && !Near(in s.Frame.Light, in day) && !Near(in s.Frame.Light, in half.Light),
            "a transition cut short crossfades the rest of the way rather than snapping");
        s = DayNightMachine.Advance(in r, ref m, DayNightBlend.CutFade);
        check.Expect(Near(in s.Frame.Light, in day), "and the crossfade finishes within its own length");

        // A plain palette cut with no transition: the night state on an area showing day.
        r.IsNight = true;
        s = DayNightMachine.Advance(in r, ref m, DayNightBlend.CutFade / 2f);
        check.Expect(s.Cut && s.Push && Near(s.Frame.Light.Ambient, new Vector3(0.30f, 0.40f, 0.50f)),
            "a palette cut is half crossfaded half way through the cut fade");
        s = DayNightMachine.Advance(in r, ref m, DayNightBlend.CutFade);
        check.Expect(s.Push && Near(in s.Frame.Light, in night), "and lands exactly on the new palette");
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        check.Expect(!s.Push && Near(in s.Frame.Light, in night), "a finished cut fade stops pushing");

        // A different area starts over from whatever that area shows, with no glide carried across.
        r.Area = 2;
        r.Running = Phase.Dawn;
        r.T = 0.25f;
        s = DayNightMachine.Advance(in r, ref m, 0.016f);
        LightFrame seeded = DayNightBlend.Vanilla(in night, in day, 0.25f, Phase.Dawn, Opacity);
        check.Expect(Near(in s.Frame.Light, in seeded.Light),
            "a new area is picked up at what the engine shows there, dip and all");
    }

    /// <summary>The policy key: a component property, listed by <c>/policy</c> like any other.</summary>
    private static void Policy(Checker check)
    {
        ServerPolicy.Set(DayNightPolicy.Key, "");
        check.Expect(!DayNightPolicy.Read(), "no key means vanilla shaping");
        ServerPolicy.Set(DayNightPolicy.Key, "1");
        check.Expect(DayNightPolicy.Read(), "the key switches owned mode on");
        check.Expect(ServerPolicy.Report().Contains(DayNightPolicy.Key + "=1", StringComparison.Ordinal),
            "/policy lists the key with its value");
        ServerPolicy.Set(DayNightPolicy.Key, "");
        check.Expect(!DayNightPolicy.Read(), "releasing the key returns the area to vanilla shaping");
    }
}
