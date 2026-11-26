// The dust lying over a crypt's floor and the night mist over water, one invocation per pixel of the main view after
// the point lights: it marches the part of the eye's ray that crosses the heights the air lies between (AirScene,
// packed by GroundMist.Pack), reads the dust and the mist there from the four nearest columns of the field by cx_air's
// profile, and lays over what the HDR target holds the light each scatters on the way -- the dust by the area's
// ambient, the mist by the sky outdoors and by the ambient indoors, both by the lamps where the air is.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

#include "cx_pass.glsl"
#include "cx_defblk.glsl"
#include "cx_air.glsl"

layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(std430, binding = 8) readonly buffer CxLightBuf { vec4 uLights[]; };
/// The field: its corner (x, y), its side in columns and 1 while anything shows; the sky's fog colour and the mist's
/// height; the lowest floor and the highest top the air reaches, and 1 indoors; then per column its floor, its mist's depth, its
/// dust's depth and the height that dust reaches, metres.
layout(std430, binding = 12) readonly buffer CxAirBuf { vec4 uAirHead; vec4 uAirSky; vec4 uAirSlab; vec4 uAirCells[]; };

/// Steps along the part of the ray the air lies across, and the farthest the march looks, metres.
const int CX_AIR_STEPS = 24;
const float CX_AIR_FAR = 64.0;
/// How strongly a metre of dust per metre of path dims and scatters, per unit of the profile: the floor 10 m off a
/// standing eye stays in sight through the dust.
const float CX_AIR_SIGMA = 0.5;
/// The share of the light the dust, and an interior's mist, dim that they scatter back toward the eye: a dark floor stays dark.
const float CX_AIR_ALBEDO = 0.4;
/// The rise between neighbouring columns past which their floors do not join (GroundMist.MaxStep), metres.
const float CX_AIR_STEP = 1.0;
/// How far under a column's floor a ray may end before that floor is the lip of a drop its air does not hang over.
const float CX_AIR_DROP = 2.0;
/// A column whose floor lies below this has none: the air beside it fades out across it, a column wide.
const float CX_AIR_WALL = -1e8;
/// Points along the air a ray crosses where the lamps' light is taken, and lamps of the rig each walks.
const int CX_AIR_PROBES = 4;
const int CX_AIR_LAMPS = 32;

/// The cell of column c, or a cell with no floor off the field.
vec4 CxAirCell(ivec2 c)
{
    int side = int(uAirHead.z);
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(side)))) return vec4(-1e9, 0.0, 0.0, 0.0);
    return uAirCells[c.y * side + c.x];
}

/// Do neighbouring columns with floors a and b blend: one floor, or a floor beside a column with none.
bool CxAirBlends(float a, float b)
{
    return CxAirJoined(a, b, CX_AIR_STEP) || min(a, b) < CX_AIR_WALL;
}

/// The dust (x) and mist (y) per metre one cell lays at height z by its share w, its mist reaching `top` of its height;
/// none on a ray that ends in a drop below it.
vec2 CxAirLayer(vec4 cell, float w, float z, float end, float top)
{
    if (w <= 0.0 || end < cell.x - CX_AIR_DROP) return vec2(0.0);
    return w * vec2(cell.z * CxAirDust(z - cell.x, cell.w), cell.y * CxAirDust(z - cell.x, uAirSky.w * top));
}

/// The share w of a cell when it has a floor, nothing when it has none: the mist comes down as it fades off a floor.
float CxAirFloored(vec4 cell, float w)
{
    return cell.x < CX_AIR_WALL ? 0.0 : w;
}

/// The dust (x) and mist (y) per metre at world point p on a ray that ends at height end, from the four nearest columns.
vec2 CxAirAt(vec3 p, float end)
{
    vec2 q = p.xy - uAirHead.xy - 0.5;
    ivec2 c = ivec2(floor(q));
    vec2 f = q - vec2(c);
    vec4 c00 = CxAirCell(c), c10 = CxAirCell(c + ivec2(1, 0)), c01 = CxAirCell(c + ivec2(0, 1)), c11 = CxAirCell(c + ivec2(1, 1));
    vec2 x0 = CxAirPair(f.x, CxAirBlends(c00.x, c10.x)), x1 = CxAirPair(f.x, CxAirBlends(c01.x, c11.x));
    vec2 y0 = CxAirPair(f.y, CxAirBlends(c00.x, c01.x)), y1 = CxAirPair(f.y, CxAirBlends(c10.x, c11.x));
    vec4 w = vec4(x0.x * y0.x, x0.y * y1.x, x1.x * y0.y, x1.y * y1.y);
    float top = CxAirFloored(c00, w.x) + CxAirFloored(c10, w.y) + CxAirFloored(c01, w.z) + CxAirFloored(c11, w.w);
    return CxAirLayer(c00, w.x, p.z, end, top) + CxAirLayer(c10, w.y, p.z, end, top)
         + CxAirLayer(c01, w.z, p.z, end, top) + CxAirLayer(c11, w.w, p.z, end, top);
}

/// The share of its light a lamp of reach pr.w lays at world point at: the lit stage's own falloff over the engine's
/// attenuation pair, its master gain and light.binreach's window, with no surface to turn toward the lamp.
float CxAirLamp(vec4 pr, vec3 at)
{
    vec3 d = pr.xyz - at;
    float f2 = dot(d, d) / max(pr.w * pr.w, 1e-8);
    if (f2 >= 1.0) return 0.0;
    float fade = 1.0 - f2;
    if (uReach > 0.0)
    {
        float r2 = uReach * uReach;
        if (f2 > r2) return 0.0;
        fade *= min((r2 - f2) / (r2 * 0.3), 1.0);
    }
    vec2 pair = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    return uFalloff.x * fade / (pair.x + pair.y * f2);
}

/// The light the lamps of the rig lay on air at world point at, each colour made linear as the lights take it.
vec3 CxAirLamps(vec3 at)
{
    vec3 lamps = vec3(0.0);
    int n = min(uLightCount, CX_AIR_LAMPS);
    for (int i = 0; i < n; i++)
        lamps += pow(max(uLights[i * 2 + 1].rgb, vec3(0.0)), vec3($SRGB$)) * CxAirLamp(uLights[i * 2], at);
    return lamps * uDayDim;
}

void main()
{
    if (any(greaterThanEqual(gl_GlobalInvocationID.xy, uvec2(uRect.zw))) || uAirHead.w < 0.5) return;
    ivec2 id = ivec2(gl_GlobalInvocationID.xy) + uRect.xy;
    float d = texelFetch(uGbDepth, id, 0).r;
    vec4 w = uInvVp * vec4(2.0 * (vec2(gl_GlobalInvocationID.xy) + 0.5) / vec2(uRect.zw) - 1.0, d, 1.0);
    vec3 surf = w.xyz / w.w;
    vec3 ray = surf - uEyePos;
    float len = min(length(ray), CX_AIR_FAR);
    if (len <= 0.0) return;
    vec3 dir = normalize(ray);
    float t0 = 0.0, t1 = len;
    if (abs(dir.z) > 1e-6)
    {
        float ta = (uAirSlab.x - uEyePos.z) / dir.z, tb = (uAirSlab.y - uEyePos.z) / dir.z;
        t0 = max(t0, min(ta, tb));
        t1 = min(t1, max(ta, tb));
    }
    else if (uEyePos.z < uAirSlab.x || uEyePos.z > uAirSlab.y) return;
    if (t1 <= t0) return;
    float dl = (t1 - t0) / float(CX_AIR_STEPS);
    // interleaved gradient noise: each pixel's steps start at their own offset, so the steps dither instead of banding
    float jit = fract(52.9829189 * fract(dot(vec2(id), vec2(0.06711056, 0.00583715))));
    vec2 tau[CX_AIR_STEPS];
    int first = -1, last = -1;
    for (int k = 0; k < CX_AIR_STEPS; k++)
    {
        tau[k] = CxAirAt(uEyePos + dir * (t0 + dl * (float(k) + jit)), surf.z) * (CX_AIR_SIGMA * dl);
        if (tau[k].x + tau[k].y <= 0.0) continue;
        if (first < 0) first = k;
        last = k;
    }
    if (first < 0) return;
    float span = float(last - first);
    vec3 probe[CX_AIR_PROBES];
    for (int i = 0; i < CX_AIR_PROBES; i++)
        probe[i] = CxAirLamps(uEyePos + dir * (t0 + dl * (float(first) + span * float(i) / float(CX_AIR_PROBES - 1) + jit)));
    vec3 dust = CxAirLit(uAreaAmbient, vec3(0.0), 0.0, 0.0), sky = uAirSlab.z > 0.5 ? dust : CxAirSky(uAirSky.rgb, uAreaDiffuse), lit = vec3(0.0);
    float mistAlbedo = uAirSlab.z > 0.5 ? CX_AIR_ALBEDO : 1.0;
    float keep = 1.0;
    for (int k = first; k <= last; k++)
    {
        float all = tau[k].x + tau[k].y;
        if (all <= 0.0) continue;
        float s = span > 0.0 ? float(k - first) / span * float(CX_AIR_PROBES - 1) : 0.0;
        int i = min(int(s), CX_AIR_PROBES - 2);
        vec3 lamp = mix(probe[i], probe[i + 1], s - float(i));
        lit += keep * (1.0 - exp(-all)) * (tau[k].x * CX_AIR_ALBEDO * (dust + lamp) + tau[k].y * mistAlbedo * (sky + lamp)) / all;
        keep *= exp(-all);
    }
    vec4 hdr = imageLoad(uHdr, id);
    imageStore(uHdr, id, vec4(hdr.rgb * keep + lit, hdr.a));
}
