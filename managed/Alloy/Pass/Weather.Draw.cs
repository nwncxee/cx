using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The precipitation field. One instanced attribute-less draw per frame: the instance id hashes to a
/// lattice point in a fall-aligned box, the copy of that point nearest the camera is the one drawn, and the
/// fall phase slides the lattice along the fall axis with <c>mod</c>, so the field wraps for ever and the CPU
/// stages nothing. Roof drips are the same draw with <c>uKind = 3</c>, kept only under a lip.</summary>
internal static unsafe partial class Weather
{
    internal const string ShdWeatherVs = "cx_weather_vs", ShdRainFs = "cx_rain_fs", ShdSnowFs = "cx_snow_fs";
    private const uint GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum
    /// <summary>Speed knobs are in m/s; the phase is kept in metres and wrapped at this many box lengths so
    /// the shader's <c>mod</c> never sees a large float, and the per-instance speed steps of 1/8 land on a
    /// box multiple at the wrap (1024/8 = 128 lengths).</summary>
    private const int PhaseWrapLengths = 1024;
    /// <summary>The wobble clock wraps where both wobble frequencies (1.0 and 1.5 rad/s) complete whole turns.</summary>
    private const float WobbleWrap = 400f * MathF.PI;

    internal static Knob<float> DensityScale = null!, StreakLen = null!, FlakeSize = null!, Brightness = null!, Opacity = null!, Range = null!,
                                RainSpeed = null!, SnowSpeed = null!, WindLean = null!, Soft = null!, Drips = null!, Backscatter = null!, SnowAngle = null!;
    internal static Knob<int> MaxCount = null!, Viz = null!;

    private static bool _failed;

    /// <summary>The precipitation program's uniform locations, resolved once per program.</summary>
    internal struct Loc { public int View, Proj, Eye, Centre, Fall, Phase, Time, Box, Size, Kind, Drift, Depth, Tint, Alpha, Soft, Viz, Cover, CoverVp, CoverLayer, CoverSoft, Lit, Back; }
    private static double _phase;
    private static float _wobble;
    private static long _ticks;
    private static int _drawn, _count, _drips;
    private static Vector3 _fall;


    private static void DeclareKnobs()
    {
        G.Section("Precipitation");
        DensityScale = G.Slider("density", "Density", 0f, 3f, 0.1f, 1f,
                                help: "How much rain or snow falls, on top of the weather the area asks for.")
                        .Ladder(Quality.Weather, 0.5f, 0.8f, 1f, 1.5f);
        Drips = G.Slider("drips", "Roof drips", 0f, 1f, 0.05f, 0.25f, tier: Tier.Advanced,
                         help: "Drops fall from roof edges and overhangs while the roofs are wet, and keep falling for the dry-out time after the rain stops. 0 = off.");
        G.Section("");
        StreakLen = G.Slider("streak", "Rain streak length", 0.1f, 2f, 0.05f, 0.35f, "m", Tier.ChatOnly, help: "Mean length of one rain streak (each varies 0.5x-1.3x).");
        FlakeSize = G.Slider("flake", "Snowflake size", 0.01f, 0.2f, 0.01f, 0.05f, "m", Tier.ChatOnly, help: "Diameter of one flake.");
        Brightness = G.Slider("bright", "Brightness", 0f, 3f, 0.1f, 0.6f, tier: Tier.ChatOnly, help: "Streaks and flakes take the light where they fall (sun, shadow, lamps); this scales it.");
        Opacity = G.Slider("opacity", "Rain opacity", 0.02f, 1f, 0.02f, 0.7f, tier: Tier.ChatOnly,
                           help: "How much of the scene one lit streak hides. A streak with no light on it is invisible whatever this says.");
        Range = G.Slider("range", "Range", 6f, 40f, 1f, 20f, "m", Tier.ChatOnly, help: "Size of the box around the camera the field fills.")
                 .Ladder(Quality.Weather, 12f, 16f, 20f, 28f);
        RainSpeed = G.Slider("rainspeed", "Rain speed", 2f, 30f, 0.5f, 12f, "m/s", Tier.ChatOnly);
        SnowSpeed = G.Slider("snowspeed", "Snow speed", 0.2f, 6f, 0.1f, 1.2f, "m/s", Tier.ChatOnly);
        WindLean = G.Slider("windlean", "Wind lean", 0f, 3f, 0.05f, 0.6f, tier: Tier.ChatOnly,
                            help: "How far the area wind tilts the fall. 0 falls straight down.");
        SnowAngle = G.Slider("snowangle", "Snow settle angle", 0f, 45f, 1f, 3f, "°", Tier.ChatOnly,
                             help: "Tilt of the settle direction from vertical, into the wind: how far snow reaches under an eave. Rain settles along its own fall.");
        Backscatter = G.Slider("backscatter", "Flake glow", 0f, 2f, 0.1f, 0.5f, tier: Tier.ChatOnly,
                               help: "A flake between you and a light glows, lit from behind. 0 = off.");
        Soft = G.Slider("soft", "Contact fade", 0f, 20f, 0.5f, 6f, tier: Tier.ChatOnly,
                        help: "How sharply a streak fades where it meets geometry (1/m).");
        MaxCount = G.Slider("count", "count", 500, 40000, 500, 16000, tier: Tier.ChatOnly)
                    .Ladder(Quality.Weather, 4000, 8000, 16000, 24000);
        Viz = G.Slider("viz", "viz", 0, VizMax, 1, 0, tier: Tier.ChatOnly, help: "1 streak contact fade, 2 exposure on the world, 3 record class (red creature, green object, blue tileset), 4 the wetness map (red wet, green fill, blue standing water; black = not latched)");
    }

    private static void InstallDraw(ExtenderLog log) => InstallCover(log);

    /// <summary>The cover map's bake direction: rain's wind-leaning fall vector, but a fixed small tilt into
    /// the wind while snow falls or lies -- the leaning vector snowed the lee side of every wall and left the
    /// windward roof edge bare. Settled snow sits near where the sky is overhead; the tilt is the reach under
    /// an eave.</summary>
    internal static Vector3 CoverDir
    {
        get
        {
            if (!(CurrentKind == Kind.Snow || (CurrentKind == Kind.None && _snow > 0f))) return _fall;
            Vector2 h = new(_fall.X, _fall.Y);
            if (h.LengthSquared() < 1e-6f) h = Vector2.UnitX;
            h = Vector2.Normalize(h);
            float a = SnowAngle.V * MathF.PI / 180f;
            return new Vector3(h.X * MathF.Sin(a), h.Y * MathF.Sin(a), -MathF.Cos(a));
        }
    }

    private static void Tick(Vector2 wind)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _ticks == 0 ? 0f : MathF.Min((float)(now - _ticks) / Stopwatch.Frequency, 0.1f);
        _ticks = now;
        Vector3 f = new(wind.X * WindLean.V, wind.Y * WindLean.V, -1f);
        _fall = Vector3.Normalize(f);
        float speed = CurrentKind == Kind.Snow ? SnowSpeed.V : RainSpeed.V;
        double wrap = Range.V * PhaseWrapLengths;
        _phase = (_phase + dt * speed) % wrap;
        _wobble = (_wobble + dt) % WobbleWrap;
        SplashTick(dt);
    }

    internal static void ContextLost() => MapLost();
    internal static void ResetPeriod() => _drawn = 0;
    internal static string Stats() => _off ? "weather unavailable" : !Enabled.V ? "weather off"
        : $"weather {CurrentKind} {Density * 100f:F0}% {_count} instances, {_drips} drips{SplashStats()}{CoverStats()}{SurfaceStats()}{MapStats()}{ShelterStats()}, records {AlloyRenderer.RecCreature} creature / {AlloyRenderer.RecObject} object / {AlloyRenderer.RecTile} tile";
    private static string DescribeDraw() => $" fall ({_fall.X:F2} {_fall.Y:F2} {_fall.Z:F2}) count {_count} drips {_drips}{SplashStats()}{CoverStats()}";

    /// <summary>One instanced quad per drop or flake; every position is a function of the instance id, the
    /// camera and the fall phase, so a hak override of this stage can reshape the field but never grow a
    /// buffer. The instance's lattice point is tested against the rain occlusion map once, here. The light is
    /// taken per vertex at the instance's own centre; <c>uLit = 0</c> falls back to the flat area terms.</summary>
    internal static string WeatherVs => ShaderSource.Get("cx_wx_v.glsl");

    /// <summary>A rain streak: a thin bright core tapered at both ends, faded where it meets geometry.
    /// Linear, alpha over. <c>/weather viz 1</c> paints the contact fade alone. The colour is the vertex stage's light times the brightness scalar.</summary>
    internal static string RainFs => ShaderSource.Get("cx_rain_f.glsl");

    /// <summary>A snowflake: a soft white disc, faded where it meets geometry. Linear, alpha over.</summary>
    internal static string SnowFs => ShaderSource.Get("cx_snow_f.glsl");
}
