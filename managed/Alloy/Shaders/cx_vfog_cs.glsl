// The volumetric fog's first pass (FogVolume): one invocation per froxel of the main view. It writes the light the
// medium scatters toward the eye (rgb, LINEAR, per unit of extinction) and the haze's own extinction (a) at a point
// jittered inside the froxel, blended with last frame's answer re-projected onto the same point of the world, so the
// grid's steps average out over frames. cx_vfogsum_cs adds the area's fog wall and integrates the result.
// The extinction also carries the far rain veil (cx_vfrain), gated by the cover map; a rain-only build carries
// the veil alone, with no haze and no lamp glow, and the area's own fog ramp still draws under it.
#version 460 core
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// The light libraries' cluster lookup takes this froxel's tile, in tile units, from here (no gl_FragCoord). A froxel
// lies inside its own light cluster -- the same frustum, cut coarser -- which is what the build's bounds assume.
vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
#define CX_CLUSTER_FRAG
// cx_lightfrag's slim walk: nothing here is a baked receiver; the loop below calls only its per-light body.
#define CX_LIGHT_LITE 1
#include "cx_pass.glsl"
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"
#include "cx_sunshadow.glsl"
#include "cx_noise.glsl"
#include "cx_vfogmed.glsl"

layout(rgba16f, binding = 0) uniform writeonly image3D uVfOut;
uniform sampler3D uVfHist;  // last frame's output of this pass
uniform vec2 uVfHeight;     // (haze floor world Z, haze falloff per metre above it)
uniform vec2 uVfNoise;      // (patchiness 0..1, 1 / patch size)
uniform vec3 uVfDrift;      // how far the patches have travelled, metres
uniform vec3 uVfAmbient;    // LINEAR light the medium scatters, over every direction and shade: the area's fog colour
uniform vec4 uVfSun;        // w: the share of it the sun's beam brings, through the cascades and the phase; rgb: the sun's hue times w
uniform vec3 uVfLocal;      // (point-light strength, 1 = their cube shadows reach the fog, how far reach sets strength)
uniform float uVfPhase;     // forward lobe's anisotropy g
uniform vec3 uVfJitter;     // this frame's sample offset inside the froxel: xy in [-0.5, 0.5), z in [0, 1)
uniform mat4 uVfPrevVp;     // last frame's unjittered view-projection
uniform vec4 uVfPrevZ;      // (last near, 1 / last log(far / near), history weight, 1 = history valid)
uniform vec2 uVfRain;       // (share of one whole rain the far veil carries, 1 = this build draws the fog's own haze too)
uniform sampler2DArrayShadow uCover;   // the sun array; the rain cover map is layer uVfCoverLayer
uniform mat4 uVfCoverVp;    // world -> the cover view's clip space; zero matrix = no map this frame
uniform float uVfCoverLayer;
uniform int uVfHidden[32];  // the fog of war: bit x of row y is set where tile (x, y) of the area is unexplored
uniform ivec2 uVfTiles;     // the area's grid, tiles; zero = no tile is unexplored

// A second, backward lobe and its share: a camera looking down on a lit scene sees back-scattered light, which a
// forward lobe alone leaves almost black.
const float CX_VF_BACK_G = -0.3;
const float CX_VF_BACK_W = 0.3;
// A lamp's core in the air, metres: inside it the inverse square stops climbing, so a sample landing on the lamp
// itself stays finite and the temporal filter has nothing to flicker on.
const float CX_VF_CORE = 1.0;
// The share of its reach at which a lamp lights the air exactly as the surface curve says: brighter inside, dimmer out.
const float CX_VF_PIVOT = 0.5;
// The cover map's depth bias, as the precipitation pass's own cover test takes it.
const float CX_VF_COVER_BIAS = 0.002;
// A tile's edge, metres.
const float CX_VF_TILE = 10.0;

/// Henyey-Greenstein, scaled so the isotropic medium (g = 0) is 1 everywhere: its mean over the sphere is 1.
float CxVfHg(float c, float g)
{
    float d = max(1.0 + g * g - 2.0 * g * c, 1e-4);
    return (1.0 - g * g) / (d * sqrt(d));
}

/// The medium's phase for light arriving along `L` (point -> light) and leaving toward an eye looking along `V`.
float CxVfPhase(vec3 L, vec3 V)
{
    float c = dot(L, V);
    return mix(CxVfHg(c, uVfPhase), CxVfHg(c, CX_VF_BACK_G), CX_VF_BACK_W);
}

/// The haze's extinction at `world`: thinning above the floor, broken into drifting patches.
float CxVfHaze(vec3 world)
{
    float h = uVfMed.x * exp(-uVfHeight.y * max(world.z - uVfHeight.x, 0.0));
    if (uVfNoise.x > 0.0 && h > 0.0)
    {
        vec3 p = (world - uVfDrift) * uVfNoise.y;
        float n = 0.65 * CxNoise3D(p) + 0.35 * CxNoise3D(p * 2.03 + vec3(17.1, 3.7, 9.2));
        h *= max(1.0 + uVfNoise.x * n, 0.0);
    }
    return h;
}

#define CX_VFRAIN_HAZE 1
#include "cx_vfrain.glsl"

/// 1 = open sky above this point along the fall vector, 0 = roofed. No map, or a point past its edge, is open --
/// the verdict the precipitation pass's own cover test gives there.
float CxVfOpen(vec3 world)
{
    if (uVfCoverVp[3][3] == 0.0) return 1.0;
    vec4 c = uVfCoverVp * vec4(world, 1.0);
    vec3 n = c.xyz / c.w * 0.5 + 0.5;
    if (any(lessThan(n.xy, vec2(0.0))) || any(greaterThan(n.xy, vec2(1.0)))) return 1.0;
    return texture(uCover, vec4(n.xy, uVfCoverLayer, n.z - CX_VF_COVER_BIAS));
}

/// 1 = tile `c` is explored. A cell past the area's edge is the edge tile's: no air stands lit beyond a hidden one.
float CxVfSeenCell(ivec2 c)
{
    c = clamp(c, ivec2(0), uVfTiles - 1);
    return 1.0 - float((uVfHidden[c.y] >> c.x) & 1);
}

/// The share of the air at `world` the player may see lit: 1 over explored tiles, 0 over unexplored ones, blended
/// between tile centres. The engine fogs an unexplored tile toward black, and the air over it is no brighter.
float CxVfSeen(vec3 world)
{
    if (uVfTiles.x <= 0) return 1.0;
    vec2 p = world.xy / CX_VF_TILE - 0.5;
    vec2 b = floor(p), f = p - b;
    ivec2 i = ivec2(b);
    return mix(mix(CxVfSeenCell(i), CxVfSeenCell(i + ivec2(1, 0)), f.x),
               mix(CxVfSeenCell(i + ivec2(0, 1)), CxVfSeenCell(i + ivec2(1, 1)), f.x), f.y);
}

/// Every clustered point light's light at `world`. A lamp is a SOURCE: the lit stage's curve sharpened toward an
/// inverse square whose intensity grows with the square of its reach (uVfLocal.z = 1: twice the reach, four times as
/// bright at the same distance; 0: the curve alone), equal to the curve at CX_VF_PIVOT -- then shadowed and phased.
/// An ambient-class light (a tile's own) adds the surface curve as it is, with no direction and no shadow.
vec3 CxVfLamps(vec3 world, vec3 V, float depth)
{
    uint cl = CxCluster(depth);
    // light.relevance's kept prefix: a lamp too faint to matter to the surfaces in this cluster is too faint here
    uint n = CxListWalk(cl);
    float dfade = uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, depth) : 1.0;
    vec3 acc = vec3(0.0);
    for (uint k = 0u; k < n; k++)
    {
        uint li = CxListIdx(cl, k);
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        vec3 c, L; float att, f2, ndlRaw, ndl;
        if (!CxPointLight(pr, cs.rgb, world, V, c, L, att, f2, ndlRaw, ndl)) continue;
        int slot = CxLightSlot(int(cs.w));
        if (slot == -2) { acc += c * att; continue; }
        float rr = pr.w * pr.w;
        if (uVfLocal.z > 0.0) att *= pow(CX_VF_PIVOT * CX_VF_PIVOT * rr / (f2 * rr + CX_VF_CORE * CX_VF_CORE), uVfLocal.z);
        float sh = 1.0;
        // `false` is a literal, so the blocker search and both discs fold out of the inlined body
        if (uVfLocal.y > 0.0 && slot >= 0 && dfade > 0.0)
            sh = mix(1.0, CxLocalShadowQ(slot, world, L, 1.0, 0.0, depth, false), dfade);
        acc += c * (att * sh * CxVfPhase(L, V));
    }
    return acc * uDayDim;
}

/// The light the medium at `world` scatters toward the eye, per unit of extinction.
vec3 CxVfLight(vec3 world, vec3 V, float depth)
{
    vec3 l = uVfAmbient * (1.0 - uVfSun.w);
    if (uVfSun.w > 0.0) l += uVfAmbient * uVfSun.rgb * (CxVfPhase(uAreaDir, V) * CxSunShadow(world, uAreaDir, 1.0, 1.0));
    // a rain-only build gathers no lamps at all: uVfLocal.x is 0 there, so the walk folds out here
    vec3 lamps = uVfLocal.x > 0.0 ? uVfLocal.x * CxVfLamps(world, V, depth) : vec3(0.0);
    return CxVfScatter(l, lamps, uVfRain.y);
}

void main()
{
    ivec3 id = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(id, uVfGrid))) return;
    vec2 grid = vec2(uVfGrid.xy);
    vec2 uv = (vec2(id.xy) + 0.5 + uVfJitter.xy) / grid;
    float depth = CxVfDepth(float(id.z) + uVfJitter.z);
    vec3 world = uVfEye + CxVfRay(uv) * depth;
    vec3 toEye = world - uVfEye;
    float dist = length(toEye);
    vec3 V = toEye / max(dist, 1e-4);
    cxClusterXY = uv * vec2(uGrid.xy);   // the light grid spans the same viewport in tile units
    float open = uVfRain.x > 0.0 ? CxVfOpen(world) : 1.0;
    // rain thins across the bank's top as the wall does, so a ray climbing to the sky leaves the veil behind
    float rain = uVfRain.x * CxVfBank(world);
    vec4 v = vec4(CxVfLight(world, V, depth) * CxVfSeen(world), CxVfDensity(world, dist, open, rain, uVfRain.y));
    if (uVfPrevZ.w > 0.0)
    {
        // the froxel's CENTRE, where last frame's grid stored its own average of this froxel
        vec2 cuv = (vec2(id.xy) + 0.5) / grid;
        vec3 centre = uVfEye + CxVfRay(cuv) * CxVfDepth(float(id.z) + 0.5);
        vec4 c = uVfPrevVp * vec4(centre, 1.0);
        if (c.w > 0.0)
        {
            vec3 p = vec3(c.xy / c.w * 0.5 + 0.5, log(max(c.w, uVfPrevZ.x) / uVfPrevZ.x) * uVfPrevZ.y);
            if (all(greaterThanEqual(p, vec3(0.0))) && all(lessThanEqual(p, vec3(1.0))))
                v = mix(v, texture(uVfHist, p), uVfPrevZ.z);
        }
    }
    imageStore(uVfOut, id, v);
}
