using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>The world's response -- wet, snowed and puddled surfaces in the lit stage. Exposure is the rain
/// occlusion map sampled per fragment; HOW MUCH is three CPU integrators, uniforms the lit program reads, with
/// no per-texel storage. Puddle placement is the AO the lit stage already receives (ADR 0021), gated on a flat
/// up-facing normal, broken by world-space noise and ringed where the rain view's depth really is lower.</summary>
internal static unsafe partial class Weather
{
    /// <summary>The cover view's depth range is <c>2 x radius + margin</c> with margin = radius (CasterPass.Cover).</summary>
    private const float CoverDepthRadii = 3f;
    /// <summary>World height step a neighbour must stand above a point for the ring test to call it a hollow.</summary>
    private const float HollowStepM = 0.12f;
    /// <summary>Weather that starts this soon after an area edge was already running there: arrive soaked.</summary>
    private const float ArriveWindowSec = 10f;
    /// <summary>R5: the share of the cover-edge knob the shelter kernel's world radius takes. The ramp a
    /// disk of that radius produces is about twice the radius across, so the knob stays "the feather, in
    /// metres" and the kernel is what delivers it.</summary>
    private const float EdgeKernelShare = 0.4f;
    /// <summary>...floored, so a zeroed cover-edge knob still filters at roughly one cover texel rather
    /// than collapsing to a single compare and its stair steps.</summary>
    private const float EdgeKernelMinM = 0.06f;
    /// <summary>The tap ceiling <c>cx_wx_edge</c>'s loop is written to.</summary>
    private const int EdgeTapsMax = 12;

    internal static Knob<bool> Arrive = null!;
    internal static Knob<float> EdgeBreak = null!, RunOff = null!, ShoreRim = null!;
    internal static Knob<int> EdgeTaps = null!;
    internal static Knob<float> WetStrength = null!, SnowStrength = null!, AccumSec = null!, DrySec = null!, MeltSec = null!,
                                PuddleThreshold = null!, PuddleEdge = null!, Splash = null!,
                                SnowAlbedo = null!, SnowSlope = null!, SnowPile = null!, SnowBump = null!, SnowSparkle = null!, SnowWrap = null!, SnowSky = null!, SnowPileWidth = null!, SnowVary = null!, SnowDepth = null!, SnowSteps = null!;
    private static float _wet, _snow, _puddle;
    private static long _surfTicks, _enterTicks;

    internal static float Snow => _snow;

    /// <summary>The wetness the lit stage's splice is handed (0 while the weather is off), for ground the
    /// wetness map has not reached and for stages that read neither map nor cover.</summary>
    internal static float WetShown => !_off && Enabled.V ? _wet * WetStrength.V : 0f;

    private static void DeclareSurfaceKnobs()
    {
        G.Section("Surfaces");
        WetStrength = G.Slider("wet", "Wet surfaces", 0f, 1f, 0.05f, 1f,
                               help: "Exposed ground and roofs darken and turn glossy in rain, reflecting the sky, and puddles pool in low ground. 0 = off.");
        SnowStrength = G.Slider("snow", "Snow cover", 0f, 1f, 0.05f, 1f,
                                help: "Exposed up-facing surfaces whiten in snow, with drifts banked against walls. 0 = off.");
        AccumSec = G.Slider("accum", "Soak / settle time", 5f, 600f, 5f, 60f, "s", Tier.Advanced,
                            help: "Seconds of full-density weather to fully wet or snow the ground.");
        DrySec = G.Slider("dry", "Dry-out time", 10f, 1800f, 10f, 180f, "s", Tier.Advanced,
                          help: "Seconds for wet ground to dry after the rain stops.");
        MeltSec = G.Slider("melt", "Melt time", 30f, 7200f, 30f, 900f, "s", Tier.Advanced,
                           help: "Seconds for snow cover to melt after the snowfall ends.");
        G.Section("");
        Arrive = G.Toggle("arrive", "Arrive into settled weather", true, Tier.ChatOnly,
                          help: "Weather already running when you enter an area is fully soaked or snowed on arrival, instead of accumulating in front of you.");
        Splash = G.Slider("splash", "Puddle ripples", 0f, 1f, 0.05f, 1f, tier: Tier.ChatOnly,
                          help: "Rings spread where drops hit standing water, at their own size in the world -- they shrink with distance like anything else. 0 = off.")
                  .Ladder(Quality.Weather, 0f, 1f, 1f, 1f);
        EdgeBreak = G.Slider("edgebreak", "Shelter edge breakup", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                             help: "How ragged the line between wet and sheltered ground is. 0 draws the roof's own outline; higher lets splash-back and gusts wander it, which is what a real eave leaves.");
        EdgeTaps = G.Slider("edgetaps", "Shelter edge taps", 1, EdgeTapsMax, 1, 8, tier: Tier.ChatOnly,
                            help: "Depth compares each wet fragment makes against the cover map to find the edge. Fewer is cheaper and grainier at the boundary.")
                    .Ladder(Quality.Weather, 3, 5, 8, EdgeTapsMax);
        RunOff = G.Slider("runoff", "Run-off on walls", 0f, 1f, 0.05f, 0.6f, tier: Tier.ChatOnly,
                          help: "Rain running down a wall it strikes: streaks rather than an even sheen, and a darker band where the run-off collects at the foot. 0 = even.");
        ShoreRim = G.Slider("shorerim", "Puddle rim", 0f, 1f, 0.05f, 0.7f, tier: Tier.ChatOnly,
                            help: "Ground right at a puddle's edge is soaked through: a darker, more saturated rim around standing water. 0 = none.");
        SnowAlbedo = G.Slider("snowalbedo", "Snow brightness", 0.3f, 1f, 0.05f, 0.9f, tier: Tier.ChatOnly,
                              help: "Albedo of settled snow. Fresh snow is near white.");
        SnowSlope = G.Slider("snowslope", "Snow holds to", 10f, 85f, 5f, 55f, "°", Tier.ChatOnly,
                             help: "Steepest slope snow still lies on. Higher = snow on steeper roofs and banks.");
        SnowPile = G.Slider("snowpile", "Drift against walls", 0f, 2f, 0.1f, 1f, tier: Tier.ChatOnly,
                            help: "Mounds banked against walls and ledges, rising toward whatever stands higher. 0 = even cover.");
        SnowPileWidth = G.Slider("snowpilewidth", "Drift width", 0.3f, 4f, 0.1f, 1.5f, "m", Tier.ChatOnly,
                                 help: "How far out from a wall its drift reaches before the cover is level again.");
        SnowVary = G.Slider("snowvary", "Ground variation", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                            help: "Uneven depth across open ground: patches of thinner and thicker cover.");
        SnowDepth = G.Slider("snowdepth", "Snow depth", 0f, 0.6f, 0.02f, 0.25f, "m", Tier.ChatOnly,
                             help: "Height of the mounds and drifts: the ground is marched as a relief so they stand up, self-occlude and lean. 0 = flat shading only.");
        SnowSteps = G.Slider("snowsteps", "Relief steps", 4f, 24f, 1f, 10f, tier: Tier.ChatOnly,
                             help: "March steps through the relief. More is smoother at grazing angles and costs more per snowed pixel.")
                     .Ladder(Quality.Weather, 4f, 6f, 10f, 16f);
        SnowBump = G.Slider("snowbump", "Mound relief", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                            help: "Soft mounds in the cover, so it lights like drifted snow rather than paint. 0 = flat.");
        SnowSparkle = G.Slider("snowsparkle", "Sparkle", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                               help: "Glints on lit snow that shift as the view moves. 0 = off.");
        SnowWrap = G.Slider("snowwrap", "Soft lighting", 0f, 1f, 0.05f, 0.4f, tier: Tier.ChatOnly,
                            help: "Light scatters inside snow, so the terminator wraps past the sun-facing edge. 0 = hard.");
        SnowSky = G.Slider("snowsky", "Sky in the shade", 0f, 2f, 0.1f, 0.6f, tier: Tier.ChatOnly,
                           help: "Shaded snow takes the sky's colour (the blue in a snow shadow). 0 = off.");
        PuddleThreshold = G.Slider("puddle", "Puddle threshold", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                                   help: "Wetness at which puddles start to fill. 1 = never.");
        PuddleEdge = G.Slider("puddleedge", "Puddle edge", 0.02f, 0.5f, 0.01f, 0.2f, tier: Tier.ChatOnly,
                              help: "Softness of a puddle's shoreline, as a share of its depth.");
    }

    /// <summary>The integrators, by wall clock. Called from every lit upload; the small deltas between two
    /// uploads of one frame sum to the frame, so nothing needs a frame stamp.</summary>
    private static void TickSurface()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _surfTicks == 0 ? 0f : MathF.Min((float)(now - _surfTicks) / Stopwatch.Frequency, 0.25f);
        _surfTicks = now;
        if (_off || !Enabled.V || FrameInputs.Interior) { _wet = _snow = _puddle = 0f; return; }
        bool rain = CurrentKind == Kind.Rain && Density > 0f, snow = CurrentKind == Kind.Snow && Density > 0f;
        if (_enterTicks != 0 && (rain || snow))
        {
            bool fresh = (float)(now - _enterTicks) / Stopwatch.Frequency < ArriveWindowSec;
            _enterTicks = 0;
            if (fresh && Arrive.V) { _wet = rain ? 1f : 0f; _snow = snow ? 1f : 0f; _puddle = rain && PuddleThreshold.V < 1f ? 1f : 0f; }
        }
        _wet = Math.Clamp(_wet + (rain ? Density / AccumSec.V : -1f / DrySec.V) * dt, 0f, 1f);
        _snow = Math.Clamp(_snow + (snow ? Density / AccumSec.V : -(rain ? 3f : 1f) / MeltSec.V) * dt, 0f, 1f);
        float th = PuddleThreshold.V;
        float fill = th < 1f && _wet > th ? (_wet - th) / (1f - th) / AccumSec.V : -0.5f / DrySec.V;
        _puddle = Math.Clamp(_puddle + fill * dt, 0f, 1f);
    }

    /// <summary>Per lit program bind: the amounts, the look, and the cover view (zero matrix = no map, which the
    /// stage reads as fully exposed). Absent on a stale content override (location -1): the block drops it.</summary>
    internal static void UploadLit(int locWeather, int locLook, int locCoverVp, int locCoverLayer, int locSnowLook, int locSnowLight, int locSnowPom, int locEdge, int locRun, int locCache, bool capturing)
    {
        if (locWeather < 0) return;
        TickSurface();
        bool on = !capturing && !_off && Enabled.V;
        LooseBlock.Set(locWeather, on ? _wet * WetStrength.V : 0f, on ? _snow * SnowStrength.V : 0f, on ? _puddle : 0f, on ? Viz.V : 0f);
        float radius = MathF.Max(CoverRange.V, 1f);
        // w = the water film's roughness in the engine's alpha units; the knob is perceptual
        LooseBlock.Set(locLook, WetDark.V, SnowAlbedo.V, PuddleEdge.V, FilmRough.V * FilmRough.V);
        LooseBlock.Set(locSnowLook, SnowSparkle.V, SnowBump.V, SnowPile.V, MathF.Cos(SnowSlope.V * MathF.PI / 180f));
        // z = the drift width in cover-view uv (the fit is 2 x radius across), w = ground variation
        LooseBlock.Set(locSnowLight, SnowWrap.V, SnowSky.V, SnowPileWidth.V * 0.5f / radius, SnowVary.V);
        LooseBlock.Set(locSnowPom, SnowDepth.V, SnowSteps.V, TilesetSkipsSnow ? 1f : 0f, 0f);
        bool cover = on && Cover.V && CasterPass.CoverFresh && CasterPass.CascadesFresh;
        Matrix4x4 vp = cover ? CasterPass.CoverVp : default;
        LooseBlock.Set(locCoverVp, vp);
        // z = uv per metre in the cover view (the fit is 2 x radius across), for the soft-edge taps
        LooseBlock.Set(locCoverLayer, CasterPass.CoverLayer, HollowStepM / (CoverDepthRadii * radius), CoverSoft.V * 0.5f / radius, SplashRate(capturing));
        UploadEdge(locEdge, cover);
        UploadCache(locCache, cover);
        BindTracks();
        Vector2 tk = TrackUniform(on);
        if (locRun >= 0) LooseBlock.Set(locRun, on ? RunOff.V : 0f, on ? ShoreRim.V : 0f, tk.X, tk.Y);
    }

    /// <summary>R5: the shelter kernel <c>cx_wx_edge</c>'s <c>CxWetOpen</c> runs -- uv per metre in the cover
    /// view, the kernel's world radius, the breakup amount and the tap count. A zero uv per metre is "no
    /// cover map" and every consumer falls back to its own coarse read, so this is also how the test stands
    /// down on a captured or weather-less frame.</summary>
    private static void UploadEdge(int locEdge, bool cover)
    {
        if (locEdge < 0) return;
        float radius = MathF.Max(CoverRange.V, 1f);
        LooseBlock.Set(locEdge, cover ? 0.5f / radius : 0f, MathF.Max(CoverSoft.V * EdgeKernelShare, EdgeKernelMinM), EdgeBreak.V, Math.Clamp(EdgeTaps.V, 1, EdgeTapsMax));
    }

    /// <summary>The grass programs' shelter read: the cover view, its layer and the kernel, so a blade
    /// draws the SAME boundary the ground under it does. The lit stage gets these through
    /// <see cref="UploadLit"/>; nothing else may grow its own shelter test.</summary>
    internal static void UploadGrassEdge(int locCoverVp, int locCoverLayer, int locEdge, int locCache)
    {
        bool cover = !_off && Enabled.V && Cover.V && CasterPass.CoverFresh && CasterPass.CascadesFresh;
        Matrix4x4 vp = cover ? CasterPass.CoverVp : default;
        if (locCoverVp >= 0) LooseBlock.Set(locCoverVp, vp);
        if (locCoverLayer >= 0) LooseBlock.Set(locCoverLayer, CasterPass.CoverLayer, 0f, 0f, 0f);
        UploadEdge(locEdge, cover);
        UploadCache(locCache, cover);
    }

    /// <summary>Drops per cell per period, 0..1: the density under the splash knob while it rains.</summary>
    internal static float SplashRate(bool capturing)
        => !capturing && !_off && Enabled.V && CurrentKind == Kind.Rain ? Density * Splash.V : 0f;

    /// <summary>The value a GLSL library declares for <c>const float NAME = V;</c>, or NaN when it does not
    /// declare one. Read once at SelfCheck to hold the ripple library's own constants to each other.</summary>
    private static float GlslConst(string src, string name)
    {
        int at = src.IndexOf("const float " + name + " = ", StringComparison.Ordinal);
        if (at < 0) return float.NaN;
        at += 12 + name.Length + 3;
        int end = src.IndexOf(';', at);
        return end > at && float.TryParse(src.AsSpan(at, end - at), System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
    }

    private static void ResetSurface() { _wet = _snow = _puddle = 0f; _enterTicks = Stopwatch.GetTimestamp(); MapReset(); TrackReset(); }

    private static string SurfaceStats() => $" wet {_wet:F2} snow {_snow:F2} puddle {_puddle:F2}{TrackStats()}";

    /// <summary>The ripple library is world-sized and the pixel footprint only fades it: a text check on the
    /// library plus a real arithmetic one on its constants -- the 2x2 cell neighbourhood <see cref="RippleLib"/>
    /// walks is exact ONLY while the crest reach stays under half a cell, and a ring appearing from a cell
    /// nobody sampled is a hole in the field with no log line behind it.</summary>
    private static void RippleSelfCheck(Checker check)
    {
        string r = RippleLib;
        check.Expect(!r.Contains("CX_IMPACT_RUNGS") && !r.Contains("CX_IMPACT_STEP") && !r.Contains("CxWeatherImpactAt(")
          && !r.Contains("vec2 CxWeatherImpact(") && !r.Contains("CxWeatherImpactRough("),
          "the mip ladder and its entry points are gone from the ripple library");
        float cell = GlslConst(r, "CX_RIPPLE_CELL_M"), reach = GlslConst(r, "CX_RIPPLE_REACH_M");
        float life = GlslConst(r, "CX_RIPPLE_LIFE"), crest = GlslConst(r, "CX_RIPPLE_CREST_M");
        check.Expect(reach > 0.04f && reach < 0.12f && life >= 0.35f && life <= 0.7f && crest > 0.004f && crest < reach,
          $"a ring is a real drop's ring: {reach * 100f:F0} cm of reach over {life:F2} s, crest {crest * 100f:F1} cm");
        check.Expect(reach < cell * 0.5f,
          $"the 2x2 cell walk covers the reach ({reach:F3} m < half of the {cell:F2} m cell)");
        string tile = ShaderSource.Get("cx_wx_ripl_c.glsl");
        check.Expect(r.Contains("vec2 CxRippleLayer(vec2 p, float rate, float salt)")
          && r.Contains("kept = CxWeatherRippleFade(px);") && r.Contains("textureLod(uWxRipple, p / (CX_RIPPLE_CELL_M * CX_RIPPLE_WRAP), 0.0).xy * kept;")
          && tile.Contains("vec2 g = CxRippleLayer(p, uRate, 0.0) + CxRippleLayer(p + CX_RIPPLE_CELL_M * 0.5, uRate, 37.0);")
          && tile.Contains("imageStore(uOut, t, vec4(-g, 0.0, 0.0));"),
          "the footprint FADES the baked rings and never reaches the lattice or the reach that size them");
        check.Expect(GlslConst(r, "CX_RIPPLE_WRAP") == GlslConst(tile, "CX_RIPPLE_WRAP") && r.Contains($"layout(binding = {RippleUnit}) uniform sampler2D uWxRipple;"),
          "the ripple tile wraps the lattice at the period the stages read it at");
        check.Expect(ShaderStages.Source(AlloyRenderer.ShdLitFs).Contains("CxWeatherRipple(vWorld.xy, uCoverLayer.w, wxPx, wxRing)")
          && ShaderStages.Source(AlloyRenderer.ShdWaterFs).Contains("CxWeatherRipple(vWaterSurfaceCoords.xy, uSplash, CxWeatherImpactPx(vWorld, V), fSplashFade)"),
          "the film and the water surface both ring from the one library");
    }

    /// <summary>The world-space ripple library: rings of a fixed physical size, spawned per world cell, and
    /// the footprint fade that takes them out once a crest is thinner than a pixel or two. The water stage
    /// includes it too, so the sea and the puddles ring alike.</summary>
    internal static string RippleLib => ShaderSource.Get("cx_wx_impact.glsl");

    /// <summary>The lit main's splice, between material synthesis and the lighting: exposure, then the three
    /// masks, then the surface rewritten under them, on every surface: content that hosts its own weather body
    /// (<c>td_ground_fs</c>) stands it down on <c>CX_ALLOY</c>. <c>/weather viz 2</c> paints exposure.</summary>
    internal static string LitSplice => ShaderSource.Get("cx_wx_litsplice.glsl");

    /// <summary>The wet reflection, after the material's own probe term, in the ENGINE's form: the probe
    /// sample TINTS the light already arriving, behind a Schlick Fresnel. Added radiance gave the probe's one
    /// capture point away -- a fire's reflection smeared past the range the light itself reaches -- while a
    /// tint cannot exceed the light that is there. Kept blurry: one cube cannot mirror a plaza sharply.</summary>
    internal static string LitSpliceProbe => ShaderSource.Get("cx_wx_litprobe.glsl");

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string lit = ShaderStages.Source(AlloyRenderer.ShdLitFs);
        check.Expect(lit.IndexOf("uniform sampler2DArrayShadow uSunShadow;", StringComparison.Ordinal) < lit.IndexOf("float CxWeatherExposure(", StringComparison.Ordinal),
          "the weather lib composes after the sun shadow lib it samples");
        check.Expect(lit.Contains(LitSplice) && lit.Contains(LitSpliceProbe), "the lit main carries both splices");
        check.Expect(lit.IndexOf("float spec = pbr.x, rough = pbr.y, metal = pbr.z;", StringComparison.Ordinal) < lit.IndexOf("float wxWet = 0.0", StringComparison.Ordinal)
          && lit.IndexOf("float wxWet = 0.0", StringComparison.Ordinal) < lit.IndexOf("vec3 base = albedo * matDiffuse;", StringComparison.Ordinal),
          "the surface splice sits after synthesis and before the lighting reads albedo/rough/spec");
        check.Expect(lit.Contains("uniform vec3 uFogSkyColor;") && lit.IndexOf("float sunVis = ", StringComparison.Ordinal) < lit.IndexOf("if (wxSnow > 0.0)\n            {\n                // scatter", StringComparison.Ordinal)
          && lit.IndexOf("vec3 ambient = ", StringComparison.Ordinal) < lit.IndexOf("ambient += wxFill", StringComparison.Ordinal),
          "the snow light block sees the sky colour, the sun visibility and the ambient it adds to");
        check.Expect(lit.Contains("uniform int uOit;") && lit.IndexOf("uniform int uOit;", StringComparison.Ordinal) < lit.IndexOf("uCoverEsmP.z > 0.5 && uOit == 0", StringComparison.Ordinal),
          "the exposure read sees the OIT mode before it picks the filtered map");
        check.Expect(WeatherVs.Contains("uniform float uBack;") && WeatherVs.Contains("CxSunShadow(P, -N, back, back)"), "the flake backscatter reads the sun through its cascades");
        check.Expect(WeatherVs.Contains("uniform sampler2DArrayShadow uCover;") && WeatherVs.Contains("CxCovered(Pc);") && WeatherVs.Contains("shown < 0.5"),
          "the precipitation vertex stage tests its instance against the cover map");
        check.Expect(WeatherVs.Contains("uKind == 3 ? CxLip(P, R, U)") && WeatherVs.Contains("if (CxCovered(P) >= 0.5) return 0.0;"),
          "a drip instance survives only under a lip: covered here, open within the feather");
        check.Expect(ShaderContract.LinearSafe(RainFs) && ShaderContract.LinearSafe(SnowFs), "the precipitation stages stay linear");
        check.Expect(WeatherVs.IndexOf("vec3 CxClusteredDiffuse(", StringComparison.Ordinal) < WeatherVs.IndexOf("if (uLit != 0 && shown >= 0.5)", StringComparison.Ordinal)
          && WeatherVs.Contains("#define CX_CLUSTER_XY cxClusterXY") && WeatherVs.Contains("CxSunShadow(P, N, ndl, ndl)")
          && RainFs.Contains("uTint * vLight") && SnowFs.Contains("uTint * vLight"),
          "rain and snow take the light rig per vertex and both fragment stages multiply it in");
        check.Expect(RainFs.Contains("scatter * cov") && SnowFs.Contains("scatter * cov")
          && RainFs.Contains("dot(scatter, CX_WX_LUMA)") && SnowFs.Contains("dot(scatter, CX_WX_LUMA)"),
          "both precipitation stages write PREMULTIPLIED scatter with a scatter-proportional extinction");
        check.Expect(KindOf(1) == Kind.Rain && KindOf(2) == Kind.Snow && KindOf(0) == Kind.None && KindOf(7) == Kind.None,
          "stock rows 1/2 map to rain/snow, row 0 and the fog row to none");
        RippleSelfCheck(check);
        SplashSelfCheck(check);
        TrackSelfCheck(check);
        float d = Density; Kind k = CurrentKind; bool en = Enabled.V;
        // the rates are the player's cx.cfg (weather.accum=5 reads 20x faster): pin the declared ones
        float accum = AccumSec.V, dry = DrySec.V, melt = MeltSec.V;
        AccumSec.V = AccumSec.Default; DrySec.V = DrySec.Default; MeltSec.V = MeltSec.Default;
        Enabled.V = true; Density = 1f; CurrentKind = Kind.Rain; _wet = 0f; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_wet > 0f && _wet < 0.01f, $"one 100ms rain tick soaks a little, not a lot (wet {_wet:F4}, accum {AccumSec.V:F0}s, interior {FrameInputs.Interior}, enter {_enterTicks != 0})");
        CurrentKind = Kind.None; Density = 0f; float w = _wet; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_wet < w, "a dry tick decays");
        ResetSurface(); Density = 1f; CurrentKind = Kind.Snow; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_snow == 1f && _wet == 0f, "snow already running at the area edge arrives fully settled");
        _enterTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60; _snow = 0f; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_snow > 0f && _snow < 0.01f, $"snow starting a minute after the edge accumulates from nothing (snow {_snow:F4})");
        Density = d; CurrentKind = k; Enabled.V = en; _wet = _snow = _puddle = 0f; _surfTicks = 0; _enterTicks = 0;
        AccumSec.V = accum; DrySec.V = dry; MeltSec.V = melt;
        MapSelfCheck(check, lit);
        SurfaceLookSelfCheck(check, lit);
        log.Info(check.Ok ? "Weather.SelfCheck: passed" : "Weather.SelfCheck: FAILED");
    }
}
