// The lit stage's weather reads, composed after AlloyLights.SunShadowLib (they sample
// the sun array's cover layer). Exposure is a four-tap PCF like a cascade's; the hollow test is eight
// compares against this point's own depth minus a step -- a neighbour standing higher fails it.
#include "cx_wx_impact.glsl"
#include "cx_wx_map.glsl"
#include "cx_wx_edge.glsl"
// ---- #617/#618/#619: weather on surfaces --------------------------------------------------------
uniform vec4 uWeather;         // x wetness, y snow, z puddle fill (all 0..1), w = /weather viz
uniform vec4 uWeatherLook;     // x wet darkening, y snow albedo, z puddle edge, w puddle coverage
// uCoverVp / uCoverLayer are declared by cx_wx_edge above: the shelter test owns them now, and the reads
// below share them.
uniform vec4 uSnowLook;        // x sparkle, y mound relief, z drift against walls, w = cos(steepest slope snow holds to)
uniform vec4 uSnowLight;       // x scatter wrap, y sky in the shade, z drift width in cover uv, w ground variation
uniform vec4 uSnowPom;         // x relief height in metres (0 = off), y march steps, z = 1 when tileset parts take no cover

uniform sampler2D uCoverEsm;   // the filtered cover map (Weather.Esm), on the snapshot-depth unit
uniform vec4 uCoverEsmP;       // x = the variance floor in depth units squared, y = height bias in depth units, z = 1 when the map is bound
// R5, the realism terms the splice reads: x = run-off down walls and the band at their foot,
// y = the puddle shoreline's rim; zw = the footprint clock and refill rate (cx_wx_track).
uniform vec4 uWxRun;
#include "cx_wx_track.glsl"

/// Up-facing enough to be ground rather than a wall: past this a face takes the even wetting, under it
/// the run-off streaks.
const float CX_WX_WALL_UP = 0.85;
/// Metres of fall the streak noise spans down a wall -- a run-off streak is long and thin, so the noise
/// is slack along z and tight across the face.
const float CX_WX_STREAK_Z = 0.5;
/// The driest a streak leaves a wall, as a share of the even wetting. The bright side goes as far the
/// other way, so the streaks REDISTRIBUTE the wetness rather than removing it.
const float CX_WX_STREAK_MIN = 0.35;
/// Metres up the wall the run-off band at its foot reaches, and metres out along the face the ground's
/// own height is read from (the map texel under the wall holds the wall's top, not the floor).
const float CX_WX_FOOT_M = 0.35;
/// The shoreline rim's width, as a share of the puddle-depth knob: ground this far under the water's own
/// level, in depth terms, is soaked through without being covered.
const float CX_WX_SHORE = 0.6;
/// The noise micro-relief's swing about its mean, as a share of the puddle-depth knob: wider than the
/// depth, so a full basin on level ground pools in pockets instead of one sheet.
const float CX_WX_MICRO_AMP = 2.0;
/// ...and its mean: what distant ground, where the noise mips to its mean, stands above the water. Under
/// 1, a distant full basin still shows a sheen; near, a full one films where the noise falls under it.
const float CX_WX_MICRO_MEAN = 0.8;
/// The share of its roughness a soaked POROUS surface keeps (a sealed one keeps 0.6): water in the pores
/// smooths dirt a little, a sealed stone a lot.
const float CX_WX_PORO_ROUGH = 0.85;

/// How much of the rain soaks in, 0..1 (Lagarde, "Water drop 2"): a heuristic off the stock parameters --
/// rough, non-metal and low specularity read as porous. ponytail: the upgrade is a .mtr porosity channel.
float CxWxPoro(float rough, float spec, float metal)
{
    return (1.0 - metal) * smoothstep(0.25, 0.7, rough) * (1.0 - clamp((spec - CX_DIELECTRIC_F0) / 0.2, 0.0, 1.0));
}

float CxWeatherExposure(vec3 worldPos, vec3 N)
{
    if (uCoverVp[3][3] == 0.0) return 1.0;
    vec4 clip = uCoverVp * vec4(worldPos + N * 0.15, 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return 1.0;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float ref = ndc.z * 0.5 + 0.5;
    // the filtered map: one fetch of the two moments, Chebyshev's bound on how much of the kernel
    // stands above this point -- a ramp as wide as the feather whatever the roof's height. Under the
    // OIT accumulate the unit holds the opaque depth instead, and a blended part takes the hard compare.
    if (uCoverEsmP.z > 0.5 && uOit == 0)
    {
        vec2 m = texture(uCoverEsm, uv).rg;
        float dd = ref - uCoverEsmP.y - m.x;
        if (dd <= 0.0) return 1.0;
        float var = max(m.y - m.x * m.x, uCoverEsmP.x);
        // the bleed cut: a roof's own texels, all at one depth, must read 0 under it and not the
        // floor's share; the ramp keeps its width
        return smoothstep(0.2, 1.0, var / (var + dd * dd));
    }
    return texture(uSunShadow, vec4(uv, uCoverLayer.x, ref - 0.0015));
}

float CxWeatherHollow(vec3 worldPos)
{
    if (uCoverVp[3][3] == 0.0) return 0.0;
    vec4 clip = uCoverVp * vec4(worldPos, 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return 0.0;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float ref = ndc.z * 0.5 + 0.5 - uCoverLayer.y;
    vec2 o = 4.0 / vec2(textureSize(uSunShadow, 0).xy);
    float L = uCoverLayer.x, s = 0.0;
    for (int i = 0; i < 8; i++)
    {
        float a = float(i) * 0.7853982;
        s += texture(uSunShadow, vec4(uv + vec2(cos(a), sin(a)) * o, L, ref));
    }
    return 1.0 - s * 0.125;
}

// The weather noise tile (Weather.Shelter): four value-noise fields baked once into a 32 m world tile, so the
// splice and the snow relief march read a texel where they used to hash four lattice corners per octave.
//   x the soak / puddle micro-relief (0.75 /m), y its fine octave (3.125 /m), z the snow's ground variation
//   (0.25 /m), w the snow mound field 0.3 + 0.45 n(1.6875 /m) + 0.25 n(6 /m)
// The frequencies are the old ones rounded to whole cells per tile, so the tile repeats seamlessly.
layout(binding = 24) uniform sampler2D uWxNoise;
const float CX_WX_NOISE_TILE_M = 32.0;
/// Metres per texel of the tile's base level: the LOD a footprint reads at.
const float CX_WX_NOISE_TEXEL_M = CX_WX_NOISE_TILE_M / 1024.0;

/// The mip a footprint of <c>px</c> metres reads the tile at. Explicit, because the relief march samples it
/// inside a loop that breaks per pixel, where implicit derivatives are undefined.
float CxWxNoiseLod(float px) { return max(log2(max(px, 1e-6) / CX_WX_NOISE_TEXEL_M), 0.0); }

vec4 CxWxNoise(vec2 worldXY, float lod) { return textureLod(uWxNoise, worldXY / CX_WX_NOISE_TILE_M, lod); }

// The snow relief, 0..1 of the depth knob: the tile's mound field under its ground-variation patches, plus
// the drift banked against the wall (from the shelter cache, interpolated by the caller along the ray).
float CxSnowHeight(vec4 nz, float vary, float pile)
{
    float m = nz.w * (1.0 - vary * smoothstep(0.35, 0.7, nz.z) * 0.6);
    return clamp(m * 0.7 + pile * 0.6, 0.0, 1.0);
}
