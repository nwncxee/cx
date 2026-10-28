using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The area wetness map -- one world-XY texture per area, fitted to the area's own tile grid, updated
/// entirely on the GPU and read per fragment by the lit splice and the grass blade. Two compute passes:
/// <c>scatter</c>, per cover BAKE, latches the top-down first static hit's world Z and its height above the
/// basin's low point (a real depression field), and <c>integrate</c>, per cadence, soaks, dries and drains.</summary>
internal static unsafe partial class Weather
{
    /// <summary>Texels a side, ceiling. RGBA32F: the height channel is a world Z and the relief is compared
    /// at centimetres, which fp16 cannot hold at both ends of an area. 512^2 x 16 B = 4 MB.
    /// ponytail: a bigger area gets fewer texels per metre rather than more memory; the upgrade is a
    /// sparse tile atlas keyed on the tiles the player has visited.</summary>
    private const int MapSizeMax = 512;
    private const int MapSizeMin = 64;
    /// <summary>Compute workgroup side, matching both compute stages' <c>local_size</c>.</summary>
    private const int MapGroup = 8;
    /// <summary>The image unit the two passes bind the map to, and the texture unit consumers sample it on.</summary>
    private const uint MapImageUnit = 0;
    /// <summary>Slack on the scatter dispatch rect: the cover fit re-latches only past a quarter of its
    /// extent (<c>CasterPass.CoverSlack</c>), so the window can sit that far off the focus.</summary>
    private const float MapRectSlack = 1.35f;

    internal static Knob<bool> Map = null!;
    internal static Knob<float> MapRes = null!, PuddleDepth = null!, Basin = null!, FilmRough = null!, WetDark = null!;
    internal static Knob<int> MapCadence = null!, FilmLights = null!;

    private static float _mapTpm;
    private static IntPtr _mapArea;
    private static bool _mapClear, _mapArrive;
    private static long _mapTicks;
    private static int _mapFrame, _mapTicksDone, _mapScatters;

    private static void DeclareMapKnobs()
    {
        G.Section("");
        Map = G.Toggle("map", "Wetness map", true, Tier.ChatOnly,
                       help: "Wetness, and the puddles that pool in real low ground, are kept in an area-sized map updated on the GPU: rain soaks where it reaches, dries where it stops, and puddles fill basins and shrink from their shorelines. Off falls back to the older per-fragment estimate, which pools nothing in real hollows.");
        MapRes = G.Slider("mapres", "Map detail", 1f, 4f, 0.5f, 3f, "texels/m", Tier.ChatOnly,
                          help: "Texels per metre the map is fitted at. A large area is capped, so it gets fewer.")
                  .Ladder(Quality.Weather, 1.5f, 2f, 3f, 4f);
        MapCadence = G.Slider("mapcadence", "Map update interval", 1, 16, 1, 4, tier: Tier.ChatOnly,
                              help: "Frames between map updates. The soak and drain are integrated by wall clock, so this changes the cost and not the rates.")
                      .Ladder(Quality.Weather, 8, 6, 4, 2);
        Basin = G.Slider("basin", "Basin size", 0.5f, 4f, 0.1f, 1.5f, "m", Tier.ChatOnly,
                         help: "How far out the map looks for the low point a puddle fills toward. Small = many shallow pools, large = fewer broader ones.");
        PuddleDepth = G.Slider("puddledepth", "Puddle depth", 0.005f, 0.12f, 0.005f, 0.03f, "m", Tier.ChatOnly,
                               help: "How deep standing water gets in a full basin. Deeper water floods further up its banks.");
        FilmRough = G.Slider("filmrough", "Water film gloss", 0.1f, 0.4f, 0.01f, 0.22f, tier: Tier.ChatOnly,
                             help: "Roughness of the water film over wet ground: low is a mirror, high a matte sheen. Perceptual, so the low end is still a real lobe and not a needle.");
        FilmLights = G.Slider("filmlights", "Film highlights", 0, 4, 1, 2, tier: Tier.ChatOnly,
                              help: "Baked static lights a water film may mirror per cluster: the brightest few, evaluated on the film's own lobe. 0 = the film takes moving lights only.")
                      .Ladder(Quality.Weather, 0, 1, 2, 4);
        WetDark = G.Slider("wetdark", "Wet darkening", 0f, 2f, 0.05f, 1f, tier: Tier.ChatOnly,
                           help: "How far a porous surface darkens and saturates when it soaks -- the albedo is raised to a power, so stone deepens and polished stone barely moves.");
    }

    /// <summary>R4: the film is live, so the cluster build may rank reflection candidates and the lit stage
    /// may walk them. 0 = the build and the stage run exactly as they do on a dry frame.</summary>
    internal static int ReflK => Enabled.V && !_off && !FrameInputs.Interior && _wet > 0f && FilmLights.V > 0 ? FilmLights.V : 0;

    /// <summary>The map is allocated, belongs to the current area and is fit to read.</summary>
    internal static bool MapLive => MapHeld && _mapArea == FrameInputs.AreaPtr
                                    && !_mapClear && Map.V && !_off && !FrameInputs.Interior;

    /// <summary>The metres a point may stand under the latched surface and still count as open to the sky.
    /// The cover edge knob feathers it, so a lip reads the same way the shelter test does.</summary>
    private static float MapShelterM => MathF.Max(0.25f + CoverSoft.V * 0.25f, 0.1f);

    /// <summary>Per lit / grass program bind: the map's fit and its look terms, and the map itself on its
    /// unit. A zero texels-per-metre is "no map" to every consumer, which falls back to its own read.</summary>
    internal static void UploadMap(int locXf, int locP, bool capturing)
    {
        bool on = MapLive && !capturing && Enabled.V;
        if (locXf >= 0) Gl.Uniform4f(locXf, 0f, 0f, on ? _mapTpm : 0f, WetStrength.V);
        if (locP >= 0) Gl.Uniform4f(locP, PuddleDepth.V, MathF.Max(PuddleEdge.V * PuddleDepth.V, 1e-4f), MapShelterM, Puddles.V);
    }

    /// <summary>The area edge: the map belongs to the area it was latched in.</summary>
    private static void MapReset() { _mapClear = true; _mapArrive = true; _mapTicks = 0; }

    /// <summary>Context loss: the map texture went with the context, so nothing is latched to an area any more.</summary>
    private static void MapLost() { _mapArea = IntPtr.Zero; _mapClear = true; _mapTicks = 0; }

    /// <summary>GL internal format of the map. Not on <see cref="Gl"/>: the one other RGBA32F target
    /// (<see cref="OitPass"/>) names it locally too.</summary>
    private const uint RgbaFloat = 0x8814;   // not-an-offset: GL internal-format enum GL_RGBA32F

    /// <summary>The map's square fit over an area's XY extent: the requested detail, capped both ways, and
    /// the texels per metre that actually results. 0 = no area extent yet, so no map.</summary>
    internal static int MapFit(Vector2 extent, float texelsPerMetre, out float tpm)
    {
        tpm = 0f;
        float span = MathF.Max(extent.X, extent.Y);
        if (!(span > 0f)) return 0;
        int size = (int)MathF.Ceiling(span * MathF.Max(texelsPerMetre, 0.1f));
        size = Math.Clamp(size, MapSizeMin, MapSizeMax);
        tpm = size / span;
        return size;
    }

    /// <summary>The three reads every consumer of the map makes; composed into the lit stage (through
    /// <c>cx_wx_lib</c>) and into the grass blade.</summary>
    internal static string MapLib => ShaderSource.Get("cx_wx_map.glsl");

    /// <summary>R2/R3/R4: the map's fit arithmetic, the reflection-list gate and the shader wiring the three
    /// depend on. Called from <see cref="SelfCheck"/> with the composed lit stage in hand.</summary>
    private static void MapSelfCheck(Checker check, string lit)
    {
        // a 16x16-tile area (160 m) at the shipped 3 texels/m fits exactly, under the cap
        int s = MapFit(new Vector2(160f, 160f), 3f, out float tpm);
        check.Expect(s == 480 && MathF.Abs(tpm - 3f) < 1e-4f, "the map fits a 160 m area at the detail asked for");
        // ...a 32x32-tile one is capped, and the texels per metre falls out of the cap, never the knob
        s = MapFit(new Vector2(320f, 320f), 4f, out tpm);
        check.Expect(s == MapSizeMax && MathF.Abs(tpm - MapSizeMax / 320f) < 1e-4f,
          "a large area is capped and reports the detail it actually got");
        // a non-square area fits its LONGER side, so the whole area is covered
        s = MapFit(new Vector2(80f, 200f), 2f, out tpm);
        check.Expect(s == 400 && MathF.Abs(tpm - 2f) < 1e-4f, "the fit spans the longer side of a non-square area");
        check.Expect(MapFit(default, 3f, out _) == 0 && MapFit(new Vector2(4f, 4f), 0.5f, out _) == MapSizeMin,
          "no area extent means no map, and a tiny area still gets the floor");

        bool en = Enabled.V; float w = _wet; int fl = FilmLights.V;
        Enabled.V = true; _wet = 0f; FilmLights.V = 2;
        check.Expect(ReflK == 0, "a dry frame ranks no reflection candidates, so the cluster build is untouched");
        _wet = 0.5f;
        check.Expect(ReflK == 2, "a wet frame ranks the knob's candidates");
        FilmLights.V = 0;
        check.Expect(ReflK == 0, "film highlights off ranks none");
        Enabled.V = en; _wet = w; FilmLights.V = fl;

        check.Expect(lit.IndexOf("vec4 CxWetMap(", StringComparison.Ordinal) < lit.IndexOf("vec4 wxM = CxWetMap(vWorld.xy);", StringComparison.Ordinal)
          && lit.Contains("CxWetShelter(wxM, vWorld.z)") && lit.Contains("CxWetDepth(wxM)"),
          "the lit splice reads the area wetness map through the library composed above it");
        check.Expect(lit.Contains("albedo = CxWetSoak(albedo, uWeatherLook.x * wxPoro * wxWet);")
          && !lit.Contains("rough = mix(rough, 0.03, wxPuddle)")
          && lit.Contains("rough * mix(0.6, CX_WX_PORO_ROUGH, wxPoro)")
          && !lit.Contains("0.25 * wxWet") && !lit.Contains("spec = max(spec,"),
          "damp ground keeps a dielectric F0 and porous ground keeps its roughness");
        check.Expect(lit.Contains("const float CX_WX_MICRO_AMP = 2.0;") && lit.Contains("(CX_WX_MICRO_MEAN * 2.0 * (1.0 - uWxMapP.w) + CX_WX_MICRO_AMP * (0.35 * n1 + 0.65 * n2 - 0.5))")
          && lit.Contains("smoothstep(CX_WX_FILM_FAR_M.x, CX_WX_FILM_FAR_M.y, vDepth)")
          && lit.IndexOf("wxPoro = (1.0 - metal)", StringComparison.Ordinal) < lit.IndexOf("float wxMicro", StringComparison.Ordinal),
          "the micro-relief outruns a full basin, and porosity is known before the film forms");
        check.Expect(lit.IndexOf("wxFilmRough = max(uWeatherLook.w, CX_ALPHA_MIN);", StringComparison.Ordinal)
            < lit.IndexOf("if (wxFilm >= cxCoat) cxCoatRough = wxFilmRough;", StringComparison.Ordinal)
          && lit.Contains("cxCoat = max(cxCoat, wxFilm);"),
          "standing water reaches the lighting as the material's clear coat");
        check.Expect(lit.Contains("wxFilmRough = CxWeatherRippleRough(wxFilmRough, wxRing, wxAmp, uCoverLayer.w);"),
          "the rain rings roughen the film and not the bed under it");
        check.Expect(lit.IndexOf("vec3 CxFilmStatics(", StringComparison.Ordinal) < lit.IndexOf("specLight += CxFilmStatics(", StringComparison.Ordinal)
          && lit.Contains("if (wxFilm > 0.0 && baked && cxCoat > 0.0 && uCapture != 2)")
          && lit.Contains("uint CxClusterRefl(uint cl) { return min(uClCount[cl * 3u + 1u] & 0xffffu, CX_CLUSTER_CAP); }"),
          "the film's static highlights come off the cluster's short reflection list, gated on the film");
        string build = ShaderStages.Source(AlloyLights.ShdClusterCs);
        check.Expect(build.Contains("uniform int uReflK;") && build.Contains("if (uReflK > 0)")
          && build.Contains("uClCount[id * 3u] = n | (live << 16u);") && build.Contains("uClCount[id * 3u + 1u] = refl | (liveCand << 16u);")
          && build.Contains("uClCount[id * 3u + 2u] = coldCand | (kept << 16u);"),
          "the cluster build writes all three header words and ranks candidates only when the film asked for them");
        check.Expect(ShaderSource.Get("cx_wx_int_c.glsl").Contains("if (m.z <= CX_WX_UNVISITED) return;")
          && ShaderSource.Get("cx_wx_scat_c.glsl").Contains("uniform sampler2D uCoverDepth;"),
          "the two map compute stages integrate only latched texels and latch off the cover depth");
        check.Expect(GrassField.EmbeddedFs.Contains("vec4 gWx = CxWetMap(vWorld.xy);")
          && !GrassField.EmbeddedFs.Contains("albedo *= 1.0 - uLook.w * uLook.z;"),
          "the grass blade reads the same map instead of its own hard-coded copy");
    }

    /// <summary>The share of its own reflectance a fully soaked surface keeps -- <c>CX_WX_SOAK_FLOOR</c> in
    /// <c>cx_wx_soak</c>, here so the self-check pins the two together.</summary>
    internal const float SoakFloor = 0.45f;
    /// <summary>A leaf takes this share of the exponent stone does -- <c>CX_WX_LEAF_SOAK</c>.</summary>
    internal const float LeafSoak = 0.5f;

    /// <summary>R5: <c>cx_wx_soak</c>'s <c>CxWetSoak</c>, transcribed -- the factor a soak of
    /// <paramref name="k"/> extra exponent leaves of an albedo of luminance <paramref name="lum"/>. Never
    /// under <see cref="SoakFloor"/>, which is the whole point: the bare power it replaced ran a blade's
    /// 0.05 albedo to 0.0025 at the top of the darkening knob, which is black.</summary>
    internal static float SoakFactor(float lum, float k)
    {
        if (k <= 0f) return 1f;
        lum = Math.Clamp(lum, 1e-3f, 0.999f);
        float kMax = MathF.Log2(SoakFloor) / MathF.Log2(lum);
        return MathF.Pow(lum, MathF.Min(k, kMax));
    }

    /// <summary>R5: the bounded soak and the reimagined shelter edge -- the maths in C#, the wiring in the
    /// stages that read them.</summary>
    private static void SurfaceLookSelfCheck(Checker check, string lit)
    {
        string soak = ShaderSource.Get("cx_wx_soak.glsl"), edge = ShaderSource.Get("cx_wx_edge.glsl");
        check.Expect(soak.Contains($"const float CX_WX_SOAK_FLOOR = {SoakFloor.ToString(System.Globalization.CultureInfo.InvariantCulture)};")
          && soak.Contains($"const float CX_WX_LEAF_SOAK = {LeafSoak.ToString(System.Globalization.CultureInfo.InvariantCulture)};")
          && soak.Contains("float kMax = log2(CX_WX_SOAK_FLOOR) / log2(lum);")
          && soak.Contains("return pow(a, vec3(1.0 + min(k, kMax)));"),
          "the soak library caps the EXPONENT per fragment, at the floor this suite transcribes");
        // dry is untouched, bit for bit, at every luminance
        bool dry = true, floored = true, monotone = true;
        for (int i = 1; i < 100; i++)
        {
            float lum = i * 0.01f;
            dry &= SoakFactor(lum, 0f) == 1f;
            float prev = 1f;
            // the knobs' own ceiling: wetdark 2 x porosity 1 x wetness 1, and every step under it
            for (int j = 1; j <= 20; j++)
            {
                float f = SoakFactor(lum, j * 0.1f);
                floored &= f >= SoakFloor - 1e-4f && f <= 1f + 1e-6f;
                monotone &= f <= prev + 1e-6f;
                prev = f;
            }
        }
        check.Expect(dry, "a dry fragment takes the albedo it always had, at every luminance");
        check.Expect(floored, $"no wetness, porosity or darkening setting takes a surface under {SoakFloor:F2} of its own albedo");
        check.Expect(monotone, "...and a higher darkening setting is never BRIGHTER than a lower one");
        // the reported bug: a dark blade at the top of the darkening knob. Before the cap this was 0.0025.
        check.Expect(SoakFactor(0.05f, WetDark.Max * LeafSoak) >= SoakFloor - 1e-4f
          && !GrassField.EmbeddedBladeFs.Contains("albedo *= 1.0 - uLook.w * uLook.z;"),
          "the procedural blade's linear scale is gone -- it went NEGATIVE past darkening 1 at full wetness");
        check.Expect(GrassField.EmbeddedFs.Contains("albedo = CxWetSoak(albedo, uLook.w * gWet * CX_WX_LEAF_SOAK);")
          && GrassField.EmbeddedBladeFs.Contains("albedo = CxWetSoak(albedo, uLook.w * bWet * CX_WX_LEAF_SOAK);")
          && lit.Contains("albedo = CxWetSoak(albedo, uWeatherLook.x * wxFilm);"),
          "both grass stages and the film take the bounded soak, and foliage takes a leaf's share of it");

        // the shelter edge: the cover map's own resolution, filtered in WORLD space, with the area map's
        // coarse test demoted to the out-of-window fallback it is
        check.Expect(edge.Contains("float CxWetOpen(vec3 worldPos, vec3 N, float fallback)")
          && edge.Contains("open += texture(uSunShadow, vec4(uv + sqrt(t) * r * vec2(cos(a), sin(a)), uCoverLayer.x, ref));")
          && edge.Contains("float a0 = CxWxEdgeHash(floor(worldPos.xy * CX_WX_EDGE_CELL)) * 6.2831853;")
          && edge.Contains("return CxWetOpenShape(open, worldPos.xy, uWxEdge.z);")
          && edge.Contains("return clamp(smoothstep(0.15, 0.85, open + n * breakup * 4.0 * open * (1.0 - open)), 0.0, 1.0);"),
          "the shelter test is a world-rotated disk of hardware compares on the cover layer, thresholded through a breakup");
        string shel = ShaderSource.Get("cx_wx_shel_c.glsl");
        check.Expect(shel.Contains("open = CxWetOpenShape(open / float(CX_WX_SHEL_TAPS), p.xy, uEdge.z);")
          && edge.Contains("if (N.z >= CX_WX_TOP_UP && CxWxTop(worldPos, uv)) return textureLod(uWxShelter, uv, 0.0).x;")
          && edge.Contains($"layout(binding = {ShelterUnit}) uniform sampler2D uWxShelter;"),
          "the shelter cache ends in the live test's own ramp, and only a level surface the rain lands on reads it");
        check.Expect(lit.Contains("float wxWetExp = CxWetOpenFast(vWorld, Ng, wxMapped ? CxWetShelter(wxM, vWorld.z) : wxExp);")
          && GrassField.EmbeddedFs.Contains("float gOpen = CxWetOpenFast(vWorld, vec3(0.0, 0.0, 1.0), gMapped ? CxWetShelter(gWx, vWorld.z) : 1.0);")
          && GrassField.EmbeddedBladeFs.Contains("CxWetOpenFast(vWorld, vec3(0.0, 0.0, 1.0), bMapped ? CxWetShelter(bWx, vWorld.z) : 1.0)"),
          "every stage that wets a surface draws ONE boundary, and the area map's texel is only the fallback past the cover window");
        check.Expect(lit.Contains("wxWet = max(wxWet, wxBand * wxWetAmt * wxWetExp * uWxRun.x);")
          && lit.Contains("vec4 wxFoot = CxWetMap(vWorld.xy + Ng.xy * CX_WX_FOOT_M);")
          && lit.Contains("* smoothstep(-CX_WX_SHORE * uWxMapP.x, 0.0, wxD) * uWxRun.y;")
          && lit.Contains("N = normalize(mix(N, Ng, wxFilm));") && !lit.Contains("mix(N, vec3(0.0, 0.0, 1.0), wxFilm)"),
          "the run-off band reads the ground beside the wall, the shoreline has a rim, and the film flattens toward the GEOMETRIC normal");
        // the kernel's fit: the knob is the feather in metres, and a zeroed one still filters
        check.Expect(MathF.Abs(MathF.Max(1f * EdgeKernelShare, EdgeKernelMinM) - 0.4f) < 1e-5f
          && MathF.Max(0f * EdgeKernelShare, EdgeKernelMinM) == EdgeKernelMinM
          && EdgeTaps.Max == EdgeTapsMax && edge.Contains($"const int CX_WX_EDGE_TAPS = {EdgeTapsMax};"),
          "the kernel radius follows the cover-edge knob, floors at a cover texel, and the tap knob cannot outrun the loop");
    }

    private static string MapStats() => !Map.V ? ", map off" : _vks.MapFailed ? ", map UNAVAILABLE"
        : MapLive ? $", map {_vks.MapSize}^2 @{_mapTpm:F2}/m ({_mapTicksDone} ticks / {_mapScatters} latches)" : ", map idle";
}
