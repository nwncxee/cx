// Where each lattice point of the precipitation field stands this frame, whether it is kept and the light on it: the
// one decision the compute cull and the task stage both make, so either path draws the same drops in the same places.
// A drop is kept only on screen, in the thinned field and under open sky -- a roof drip only under a lip -- and lit once.
#ifndef CX_WX_PLACE
#define CX_WX_PLACE
#include "cx_wx_uni.glsl"
#include "cx_pass.glsl"
#include "cx_wx_drop.glsl"
/// The forward lobe of a snowflake's sun scatter at no flake glow (Henyey-Greenstein g).
const float CX_WX_RAIN_G = 0.35;
/// Metres from the camera inside which no falling drop is kept, fading in over three times that: one by the lens is a
/// bar across the screen. Roof drips keep to their lips.
const float CX_WX_LENS = 1.0;

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
#define CX_LIGHT_LITE 1   // cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light
#include "cx_wx_hash.glsl"
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"
#include "cx_sunshadow.glsl"
#include "cx_probeshape.glsl"
#define CX_WX_LAMPS 1
#include "cx_wx_shade.glsl"
#include "cx_vfrain.glsl"

// 1 = open sky above this point along the fall vector, 0 = covered. No map = open.
float CxCovered(vec3 p)
{
    if (uCoverVp[3][3] == 0.0) return 1.0;
    vec4 c = uCoverVp * vec4(p, 1.0);
    vec3 n = c.xyz / c.w * 0.5 + 0.5;
    if (any(lessThan(n.xy, vec2(0.0))) || any(greaterThan(n.xy, vec2(1.0)))) return 1.0;
    return texture(uCover, vec4(n.xy, uCoverLayer, n.z - 0.002));
}

// 1 = under a lip: covered here, open somewhere within the feather distance. Mid-roof and open sky are 0.
float CxLip(vec3 P, vec3 R, vec3 U)
{
    float e = max(uCoverSoft, 0.15);
    if (CxCovered(P) >= 0.5) return 0.0;
    return max(max(CxCovered(P + R * e), CxCovered(P - R * e)), max(CxCovered(P + U * e), CxCovered(P - U * e)));
}

/// The sky's light arriving along `d`: the area ambient shaped by the probe where one is captured.
vec3 CxWxSky(vec3 d)
{
    return uAreaAmbient * (uProbeMips > 0.0 ? CxAmbientShape(d) : vec3(1.0));
}

/// Lattice point `id`: false when it is not kept, else its five records -- r0 (position, length), r1 (light, seed),
/// r2 (sky overhead or seen through a flake, metres fallen this frame), r3 (the lamps' direction, the sun's
/// visibility), r4 (the sky at the horizon behind it, its speed as a share of the field's).
bool CxWxPlace(uint id, out vec4 r0, out vec4 r1, out vec4 r2, out vec4 r3, out vec4 r4)
{
    r0 = vec4(0.0); r1 = vec4(0.0); r2 = vec4(0.0); r3 = vec4(0.0); r4 = vec4(0.0);
    if (id >= uint(uCount)) return false;
    float speed = 0.75 + float(id & 3u) * 0.125;    // eighths: continuous at the phase wrap
    vec3 h = CxHash3(id * 2654435761u + 1u);
    vec3 F = uFall;
    vec3 R = normalize(cross(F, abs(F.z) < 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0)));
    vec3 U = cross(R, F);
    vec3 size = vec3(uBox.x, uBox.x, uBox.y);
    vec3 home = h * size;
    home.z = mod(home.z + uPhase * speed, size.z);
    vec3 c = vec3(dot(uCentre, R), dot(uCentre, U), dot(uCentre, F));
    vec3 q = home + size * floor((c - home) / size + 0.5);
    if (uKind == 2) q.xy += vec2(sin(uTime + h.y * 6.2832), cos(uTime * 1.5 + h.z * 6.2832)) * uDrift;
    vec3 P = R * q.x + U * q.y + F * q.z;
    float len = uKind != 2 ? uQuad.x * (0.5 + 0.8 * h.y) : uQuad.x;
    // thinned by drop COUNT to none 25 m out, where the fog's veil holds the same share; CX_WX_LENS keeps the lens clear
    float dist = length(uEye - P);
    float edge = CxWxNearKeep(dist) * (uKind == 3 ? 1.0 : smoothstep(CX_WX_LENS, CX_WX_LENS * 3.0, dist));
    vec4 cv = uView * vec4(P, 1.0);
    vec4 cc = uProj * cv;
    float r = max(len, CX_WX_DROP_M) + uQuad.y;
    bool seen = cc.w > -r && abs(cc.x) - r * abs(uProj[0][0]) <= cc.w && abs(cc.y) - r * abs(uProj[1][1]) <= cc.w;
    // the cover test at a hashed offset in the fall-normal plane: wind and splash feather a roof's edge
    vec3 Pc = P + (R * (h.y - 0.5) + U * (h.z - 0.5)) * uCoverSoft * 2.0;
    bool keep = seen && fract(h.z * 91.7) <= edge && (uKind == 3 ? CxLip(P, R, U) : CxCovered(Pc)) >= 0.5;
    if (!keep) return false;
    vec3 V = normalize(uEye - P);
    // the view ray bent up through a flake toward the sky; straight overhead it is the zenith
    vec3 bent = vec3(0.0, 0.0, 1.0) - V;
    vec3 up = dot(bent, bent) > 1e-6 ? normalize(bent) : vec3(0.0, 0.0, 1.0);
    vec3 low = vec3(-V.xy, 0.0);
    low = dot(low, low) > 1e-6 ? normalize(low) : vec3(1.0, 0.0, 0.0);
    float seed = 0.3 + 0.7 * fract(h.x * 7.31);
    vec3 lampDir = vec3(0.0, 0.0, 1.0), lamps = vec3(0.0);
    float sunVis = 1.0;
    vec3 light = uAreaAmbient + uAreaDiffuse * 0.5;
    if (uLit != 0)
    {
        cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
        lamps = CxWxLampsDir(P, max(-cv.z, 1e-3), lampDir);
        sunVis = CxSunShadow(P, uAreaDir, 1.0, 1.0);
        // no surface normal: the sun reaches a flake through a forward-scattering lobe, a lamp from wherever it stands
        float g = uKind == 2 ? clamp(uBack * 0.5, 0.0, 0.8) : CX_WX_RAIN_G;
        vec3 sun = uAreaDiffuse * (CxWxPhase(-uAreaDir, V, g) * sunVis);
        light = uAreaAmbient + sun + lamps;
    }
    r0 = vec4(P, len);
    r1 = vec4(uKind == 2 ? light : lamps, seed);
    r2 = vec4(uKind == 2 ? CxWxSky(up) : CxWxSky(vec3(0.0, 0.0, 1.0)), uFallStep * speed);
    r3 = vec4(lampDir, sunVis);
    r4 = vec4(CxWxSky(low), speed);
    return true;
}
#endif
