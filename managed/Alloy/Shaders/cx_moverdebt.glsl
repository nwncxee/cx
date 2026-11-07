// The mover debt at half resolution. A baked surface holds a static light in its lightmap, so a creature standing
// under that light owes the shadow the bake never saw. The deferred walk has no branch for it (CX_NO_HOT): each tile
// here culls the lights' mover spheres against its own box, and only the pixels a kept sphere's ray test passes read
// the G-buffer and tap the light's dynamic page. uMoverOcc holds the share of the baked term owed and the view depth
// it was found at; cx_moverapply upsamples it and subtracts.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

#define CX_NO_HOT 1
/// This invocation's pixel centre in window pixels, as the deferred walk defines it.
vec2 cxPixel;
#define CX_PIXEL cxPixel
#define CX_CLUSTER_XY ((cxPixel - uTile.xy) / uTile.zw)
#define CX_CLUSTER_FRAG

#include "cx_pass.glsl"
#include "cx_chartbuf.glsl"
#include "cx_gbuf.glsl"
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"

layout(binding = 3) uniform usampler2D uGb0;
layout(binding = 4) uniform usampler2D uGb1;
layout(binding = 5) uniform usampler2D uGb2;
/// The frame depth after the cutout pass: the surface this pass reconstructs.
layout(binding = 7) uniform sampler2D uGbDepth;
layout(rg16f, binding = 0) uniform writeonly image2D uMoverOccOut;
#include "cx_defblk.glsl"

/// The uLights index of the static light in each cube slot, negative for none.
uniform float uSlotLight[64];

const float CX_DEBT_SLACK = 1e-3;

/// Mover spheres one tile keeps, as (slot << 8 | sphere). A tile past it drops the rest, and a dropped shadow is
/// its shadow there: 16x16 pixels of one surface slab rarely see more than a crowd under two lamps.
const uint CX_DEBT_TILE_CAP = 96u;
shared uint sHit[CX_DEBT_TILE_CAP];
shared uint sN, sMinZ, sMaxZ;

/// This group's world-space bounding sphere: its padded NDC rect over the depth slab its pixels span.
void CxDebtTileBox(out vec3 fcw, out float fr)
{
    vec2 sz = vec2(uRect.zw);
    vec2 t0 = vec2(gl_WorkGroupID.xy * gl_WorkGroupSize.xy) * 2.0, t1 = min(t0 + vec2(gl_WorkGroupSize.xy) * 2.0, sz);
    vec2 pad = 2.0 / sz;
    vec2 n0 = 2.0 * t0 / sz - 1.0 - pad, n1 = 2.0 * t1 / sz - 1.0 + pad;
    float d0 = uintBitsToFloat(sMinZ) * (1.0 - CX_DEBT_SLACK), d1 = uintBitsToFloat(sMaxZ) * (1.0 + CX_DEBT_SLACK);
    vec3 lo = vec3(1e30), hi = vec3(-1e30);
    for (int c = 0; c < 8; c++)
    {
        float d = (c & 4) == 0 ? d0 : d1;
        vec3 q = vec3(((c & 1) == 0 ? n0.x : n1.x) * d / uProj[0].x,
                      ((c & 2) == 0 ? n0.y : n1.y) * d / uProj[1].y,
                      -d);
        lo = min(lo, q); hi = max(hi, q);
    }
    vec3 fc = 0.5 * (lo + hi);
    fr = 0.5 * length(hi - lo);
    fcw = transpose(mat3(uView)) * (fc - uView[3].xyz);
}

/// Does mover sphere <paramref name="ms"/> under the light at <paramref name="l"/> shadow any point of the sphere
/// (fc, fr)? Its shadow volume is the cone from the light tangent to it.
bool CxSphereReaches(vec3 l, vec4 ms, vec3 fc, float fr)
{
    vec3 ax = ms.xyz - l;
    float rr = ms.w, d = length(ax);
    // a light this close to the mover is shadowed in every direction: the tangent cone degenerates
    if (d * d <= 2.0 * rr * rr) return true;
    ax /= d;
    vec3 v = fc - l;
    float sn = rr / d, cs = sqrt(1.0 - sn * sn);
    float along = dot(v, ax);
    if (along < -fr) return false;
    float perp = sqrt(max(dot(v, v) - along * along, 0.0));
    return cs * perp - along * sn <= fr;
}

/// Does the segment from the light at <paramref name="lp"/> to this point pass through the mover sphere?
bool CxSphereRay(vec3 lp, vec4 ms, vec3 worldPos)
{
    vec3 dp = worldPos - lp;
    float dl = max(length(dp), 1e-4);
    vec3 dc = ms.xyz - lp;
    float t = dot(dc, dp) / dl;
    return t > -ms.w && t < dl + ms.w && dot(dc, dc) - t * t <= ms.w * ms.w;
}

/// The share of this pixel's baked term the kept spheres take back, 0 where nothing shadows it.
float CxDebtAt(ivec2 p, float z, float viewDepth)
{
    uint held = min(sN, CX_DEBT_TILE_CAP);
    if (held == 0u || !(z > 0.0)) return 0.0;
    vec4 h = uInvVp * vec4(2.0 * (cxPixel - vec2(uRect.xy)) / vec2(uRect.zw) - 1.0, z, 1.0);
    if (!(abs(h.w) > 1e-6)) return 0.0;
    vec3 worldPos = h.xyz / h.w;
    // from depth alone, ahead of any G-buffer read: most pixels of a tile lie in no sphere's shadow
    uvec2 hits = uvec2(0u);
    for (uint k = 0u; k < held; k++)
    {
        int slot = int(sHit[k] >> 8u);
        if ((hits[slot >> 5] & (1u << uint(slot & 31))) != 0u) continue;
        if (CxSphereRay(uLocalLight[slot].xyz, uDynMovers[slot * CX_DYN_SPHERES + int(sHit[k] & 255u)], worldPos))
            hits[slot >> 5] |= 1u << uint(slot & 31);
    }
    if (hits == uvec2(0u)) return 0.0;
    uvec4 g1 = texelFetch(uGb1, p, 0);
    CxGbuf g;
    if (!CxGbufReadWalk(texelFetch(uGb0, p, 0), g1.w, viewDepth, uGbDepth, p, uProj, g)) return 0.0;
    CxGbufWeights(g1, g);
    if ((g.flags & CX_GB_BAKED) == 0u || !(g.lmLum > 0.0)) return 0.0;
    CxGbufFade(texelFetch(uGb2, p, 0).r, g);
    cxSurfFade = g.surfFade;
    cxFragNormal = (g.flags & CX_GB_NORMALMAP) != 0u;
    float recv = (g.flags & CX_GB_NOSHADOW) != 0u ? 0.0 : 1.0;
    float dfade = (uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, viewDepth) : 1.0) * recv;
    if (dfade <= 0.0) return 0.0;

    vec3 lmSub = vec3(0.0);
    for (int w = 0; w < 2; w++)
        for (uint m = hits[w]; m != 0u; m &= m - 1u)
        {
            int slot = w * 32 + findLSB(m);
            int li = int(uSlotLight[slot]);
            vec4 cs = uLights[li * 2 + 1];
            vec3 c, L; float att, f2, nr, nl;
            if (!CxPointLight(uLights[li * 2], cs.rgb, worldPos, g.N, c, L, att, f2, nr, nl) || nl <= 0.0) continue;
            float lum = dot(CxPointDiffuse(c, att, nr, nl), vec3(0.2126, 0.7152, 0.0722));
            CxMoverOwe(slot, CxPickChan(CxLightSet(int(cs.w)), g.lmPick), lum, nr, true, worldPos, g.N, dfade, g.lmMover, g.lmTexel, lmSub);
        }
    // a baked shadow keeps uLocalFloor of its light (cx_bake_f), so a mover's takes back only the rest; the channel
    // shares in y were baked without the floor already
    lmSub.x *= 1.0 - uLocalFloor;
    return min(lmSub.x / max(g.lmLum, 1e-5) + lmSub.y, 1.0);
}

void main()
{
    // Every barrier sits at the top level of main and runs for every invocation: a group where a lane had returned
    // early would hang on the first one.
    uint lane = gl_LocalInvocationIndex;
    if (lane == 0u) { sMinZ = 0xffffffffu; sMaxZ = 0u; sN = 0u; }
    barrier();

    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    ivec2 p = id * 2 + uRect.xy;
    cxPixel = vec2(p) + 0.5;
    bool mine = all(lessThan(id * 2, uRect.zw));
    float z = mine ? texelFetch(uGbDepth, p, 0).r : 0.0;
    float viewDepth = mine ? CxDepthViewZ(z, uProj) : 0.0;
    if (mine && z > 0.0) { atomicMin(sMinZ, floatBitsToUint(viewDepth)); atomicMax(sMaxZ, floatBitsToUint(viewDepth)); }
    barrier();

    if (sMinZ <= sMaxZ && !(uLocalPcss.w > 0.0 && uintBitsToFloat(sMinZ) >= uLocalPcss.w))
    {
        vec3 fcw; float fr;
        CxDebtTileBox(fcw, fr);
        // one lane a slot: the slots are few and each is a run of up to CX_DYN_SPHERES tests
        for (int s = int(lane); s < min(uLocalCubes, 64); s += int(gl_WorkGroupSize.x * gl_WorkGroupSize.y))
        {
            if (uLocalDynPage[s] < 0.5 || uSlotLight[s] < 0.0) continue;
            vec3 l = uLocalLight[s].xyz;
            float far = uLocalLight[s].w;
            vec3 v = fcw - l;
            if (dot(v, v) > (far + fr) * (far + fr)) continue;
            for (int q = 0; q < CX_DYN_SPHERES; q++)
            {
                vec4 ms = uDynMovers[s * CX_DYN_SPHERES + q];
                if (ms.w < 0.0) break;   // a slot's spheres run from 0 up, unused ones after
                if (!CxSphereReaches(l, ms, fcw, fr)) continue;
                uint at = atomicAdd(sN, 1u);
                if (at < CX_DEBT_TILE_CAP) sHit[at] = uint(s) << 8u | uint(q);
            }
        }
    }
    barrier();

    if (mine) imageStore(uMoverOccOut, id, vec4(CxDebtAt(p, z, viewDepth), viewDepth, 0.0, 0.0));
}
