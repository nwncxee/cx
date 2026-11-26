// The pale wisps high over a meadow, one invocation per pixel after the motes: the view ray is marched through the
// wisps' band over the player's floor, up to what the depth buffer holds, and the frame is veiled by what it crosses,
// lit by the area's ambient and its sun or moon (cx_air's CxAirSky). The wisps thin out with distance and are gone
// short of where a ray low over the horizon would cross the band for kilometres.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

#include "cx_pass.glsl"
#include "cx_defblk.glsl"
#include "cx_air.glsl"

layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
/// The wisps: their band's floor and depth, metres, how thick at its middle, per metre, and the front's line from the
/// feet; how far carried and stretched; the front's heading and the player's feet; what is left behind the line and
/// ahead of it, a streak's length and its wide width across the wind; its narrow width, how far it wanders across the
/// wind and the span of one wander, metres.
layout(std430, binding = 15) readonly buffer CxAirWisps { vec4 uWisp[5]; };

/// How far the wisps reach, metres, the share of that out to which they show whole before thinning to none, and the
/// steps the march takes across the band.
const float CX_WISP_FAR = 220.0;
const float CX_WISP_CLEAR = 0.5;
const int CX_WISP_STEPS = 8;
const float CX_TAU = 6.2831853;

/// The lattice point `i`'s value, 0 to 1: AirScene's WispLattice bit for bit.
float CxWispLattice(ivec2 i)
{
    uint h = uint(i.x) * 0x8da6b343u ^ uint(i.y) * 0xd8163841u;
    h = (h ^ (h >> 13)) * 0x5bd1e995u;
    h ^= h >> 15;
    return float(h & 0xffffu) / 65535.0;
}

/// Smooth value noise at `p`, -1 to 1, one lattice cell a unit wide.
float CxWispWander(vec2 p)
{
    vec2 f = floor(p), u = p - f;
    ivec2 i = ivec2(f);
    u = u * u * (3.0 - 2.0 * u);
    float lo = mix(CxWispLattice(i), CxWispLattice(i + ivec2(1, 0)), u.x);
    float hi = mix(CxWispLattice(i + ivec2(0, 1)), CxWispLattice(i + ivec2(1, 1)), u.x);
    return 2.0 * mix(lo, hi, u.y) - 1.0;
}

/// The streaks at world `xy`, from 0 to 2: bands along the wind that wander across it, each waxing and waning along it.
float CxWispStreaks(vec2 xy)
{
    vec2 stretch = uWisp[1].zw;
    float len = length(stretch);
    vec2 dir = len > 0.0 ? stretch / len : vec2(1.0, 0.0);
    vec2 q = xy - uWisp[1].xy;
    float along = dot(q, dir) / (uWisp[3].z * max(len, 1.0));
    float across = q.y * dir.x - q.x * dir.y;
    float a = across + uWisp[4].y * CxWispWander(vec2(along, across / uWisp[4].z));
    float b = across + uWisp[4].y * CxWispWander(vec2(along + 7.3, across / uWisp[4].z + 3.1));
    return 1.0 + 0.6 * sin(CX_TAU * a / uWisp[3].w) * (0.5 + 0.5 * sin(CX_TAU * along + 0.4))
               + 0.4 * sin(CX_TAU * b / uWisp[4].x + 2.1) * (0.5 + 0.5 * sin(1.7 * CX_TAU * along + 2.9));
}

void main()
{
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(c, uRect.zw))) return;
    ivec2 id = c + uRect.xy;
    vec2 ndc = 2.0 * (vec2(c) + 0.5) / vec2(uRect.zw) - 1.0;
    vec4 nearW = uInvVp * vec4(ndc, 1.0, 1.0);
    vec3 dir = normalize(nearW.xyz / nearW.w - uEyePos);
    float d = texelFetch(uGbDepth, id, 0).r;
    float reach = CX_WISP_FAR;
    if (d > 0.0)
    {
        vec4 w = uInvVp * vec4(ndc, d, 1.0);
        reach = min(reach, distance(w.xyz / w.w, uEyePos));
    }
    // ponytail: the band lies over the player's floor everywhere; per-column floors off the air's field once a meadow climbs
    float lo = uWisp[0].x, depth = uWisp[0].y;
    float t0 = 0.0, t1 = reach;
    if (abs(dir.z) > 1e-4)
    {
        float a = (lo - uEyePos.z) / dir.z, b = (lo + depth - uEyePos.z) / dir.z;
        t0 = max(t0, min(a, b));
        t1 = min(t1, max(a, b));
    }
    else if (uEyePos.z <= lo || uEyePos.z >= lo + depth) return;
    if (t1 <= t0) return;
    float clear = CX_WISP_CLEAR * CX_WISP_FAR;
    float dt = (t1 - t0) / float(CX_WISP_STEPS), tau = 0.0;
    for (int k = 0; k < CX_WISP_STEPS; k++)
    {
        float t = t0 + (float(k) + 0.5) * dt;
        vec3 p = uEyePos + dir * t;
        float z = p.z - lo;
        float left = dot(p.xy - uWisp[2].zw, uWisp[2].xy) > uWisp[0].w ? uWisp[3].y : uWisp[3].x;
        float fade = 1.0 - smoothstep(clear, CX_WISP_FAR, t);
        tau += uWisp[0].z * left * fade * sin(3.14159265 * clamp(z / depth, 0.0, 1.0)) * CxWispStreaks(p.xy) * dt;
    }
    float keep = exp(-tau);
    vec4 hdr = imageLoad(uHdr, id);
    imageStore(uHdr, id, vec4(hdr.rgb * keep + CxAirSky(uAreaAmbient, uAreaDiffuse) * (1.0 - keep), hdr.a));
}
