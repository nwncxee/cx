// The motes floating in a crypt's lamplight, one invocation per mote after the dust: each is projected into the main
// view, hidden behind what the depth buffer holds in front of it, and laid over the HDR target as a soft speck lit
// by the lamps it floats among (cx_air's CxAirLit). Built with CX_AIR_SKY it lays a meadow's pollen instead, lit
// by the area's ambient and its sun or moon (cx_air's CxAirSky).
#version 460 core
layout(local_size_x = 64) in;

#include "cx_pass.glsl"
#include "cx_defblk.glsl"
#include "cx_air.glsl"

layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(std430, binding = 8) readonly buffer CxLightBuf { vec4 uLights[]; };
/// The motes: how many in the head's x, then per mote its place, metres,
/// and how brightly it shows (0 to 1) in w.
layout(std430, binding = 15) readonly buffer CxAirMotes { vec4 uMoteHead; vec4 uMotes[]; };

/// A mote's radius, metres, and the smallest and largest speck it lays, pixels.
const float CX_MOTE_SIZE = 0.012;
const float CX_MOTE_MIN_PX = 0.75;
const int CX_MOTE_MAX_PX = 3;
/// How much brighter than the dust a lit mote glows: a speck catches the light the dust spreads thin.
const float CX_MOTE_GAIN = 3.0;
/// Lamps a mote's light walks, from the front of the rig, as the dust's does.
const int CX_MOTE_LAMPS = 32;

void main()
{
    int i = int(gl_GlobalInvocationID.x);
    if (i >= int(uMoteHead.x)) return;
    vec4 mote = uMotes[i];
    if (mote.w <= 0.0) return;
    vec4 clip = uProj * (uView * vec4(mote.xyz, 1.0));
    if (clip.w <= 0.05) return;
    vec2 at = (clip.xy / clip.w + 1.0) * 0.5 * vec2(uRect.zw) - 0.5;
    ivec2 c = ivec2(floor(at + 0.5));
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, uRect.zw))) return;
    float d = texelFetch(uGbDepth, c + uRect.xy, 0).r;
    vec4 w = uInvVp * vec4(2.0 * (vec2(c) + 0.5) / vec2(uRect.zw) - 1.0, d, 1.0);
    if (distance(w.xyz / w.w, uEyePos) < distance(mote.xyz, uEyePos)) return;
#ifdef CX_AIR_SKY
    vec3 lit = CxAirSky(uAreaAmbient, uAreaDiffuse) * (CX_MOTE_GAIN * mote.w);
#else
    vec3 lamps = vec3(0.0);
    int n = min(uLightCount, CX_MOTE_LAMPS);
    for (int k = 0; k < n; k++)
    {
        vec4 pr = uLights[k * 2];
        lamps += CxAirLit(vec3(0.0), uLights[k * 2 + 1].rgb, distance(pr.xyz, mote.xyz), pr.w);
    }
    vec3 lit = (CxAirLit(uAreaAmbient, vec3(0.0), 0.0, 0.0) + lamps) * (CX_MOTE_GAIN * mote.w);
#endif
    float px = max(CX_MOTE_SIZE * uProj[1][1] * 0.5 * float(uRect.w) / clip.w, CX_MOTE_MIN_PX);
    int r = min(int(ceil(px * 2.0)), CX_MOTE_MAX_PX);
    float cover = min(px * px, 1.0);
    for (int y = -r; y <= r; y++)
        for (int x = -r; x <= r; x++)
        {
            ivec2 p = c + ivec2(x, y);
            if (any(lessThan(p, ivec2(0))) || any(greaterThanEqual(p, uRect.zw))) continue;
            vec2 off = vec2(p) - at;
            float fall = exp(-dot(off, off) / (2.0 * px * px)) * cover;
            if (fall < 0.004) continue;
            ivec2 id = p + uRect.xy;
            // ponytail: two motes on one pixel race their read-modify-write and one speck is lost; a sorted splat or an
            // atomic accumulation target once motes crowd
            vec4 hdr = imageLoad(uHdr, id);
            imageStore(uHdr, id, vec4(hdr.rgb + lit * fall, hdr.a));
        }
}
