// CX-188 step 3 (ADR 0029): the deferred path's SHADOW pass -- one invocation per record texel, run between the
// cutout pass and cx_deferred_cs. For each deferred pixel it picks the shadow.perpixel shadow-casting lights
// contributing most to that pixel, evaluates each one's cube visibility ONCE through the cheap filter
// (CxLocalShadowFast), fades a lane sitting near the budget's edge toward unshadowed (CxShFade, so the budget's
// membership step is invisible rather than a crawling contour), and writes the four verdicts plus the surface's
// own depth and normal into the visibility record (cx_shrec). cx_deferred_cs's light walk then READS a light's shadow out of that record instead of
// filtering it inline per candidate, which is what the forward loop had to do for every material that might
// need one.
// <para>The choice is per PIXEL, never per tile or froxel (#336: a per-cluster choice draws the grid on screen),
// and it is ranked by CxLightRank -- the light's irradiance at the pixel, a function of POSITION only, which is
// the one score the forward cap and the hot-light cap also order by. No surface term is in it: the contour that
// separates the lights keeping a shadow from the ones that do not must not follow the normal map. With the
// budget off the score is -f2, which is the pre-budget order exactly.</para>
// Its light list is the FROXEL table, the forward loop's own input, so the record's top-N is the top-N the
// forward path would have shadowed. Matched by slot, so the two lists need not agree -- but with the budget on,
// a light the deferred pass's tile cull keeps that this list never saw is UNSHADOWED there, and it is NOT
// counted: the counter is the budget's own spend and that light was never a candidate here. Only reachable with
// light.deferredtiles on, which is off by default precisely because the froxel lists are what the walk reads.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;

/// This invocation's SCREEN pixel centre -- the gl_FragCoord.xy of the fragment that wrote the record it reads.
/// The froxel lookup is defined in terms of it. Set first in main.
vec2 cxPixel;
#define CX_PIXEL cxPixel
#define CX_CLUSTER_XY ((cxPixel - uTile.xy) / uTile.zw)
#define CX_CLUSTER_FRAG

// CX-188 step 4: this pass IS the deferred path's shadow verdict for an unbaked receiver, so it compiles
// cx_localshadow's pair tap -- a static light that traded its dyn page for (light, creature) tiles has its
// movers there and nowhere else. Nowhere but here and cx_deferred_cs / cx_pairdebt, which is what keeps the
// tap out of the forward loop's registers (the reverted attempt's lesson, 504b6e0).
#define CX_PAIR_RECV 1

#include "cx_pass.glsl"
#include "cx_chartbuf.glsl"
#include "cx_gbuf.glsl"
#include "cx_shrec.glsl"
#include "cx_localshadow.glsl"
// The walk's own library, for ONE reason: CxLightSlot, CxLightImportance and CxPointLight are the rules this
// pass has to mirror, and a second transcription of the engine's light curve is what ADR 0029 exists to avoid.
// CxClusteredLightLm itself is unreferenced here and compiles out.
#include "cx_lightfrag.glsl"
#include "cx_shpick.glsl"

layout(binding = 3) uniform usampler2D uGb0;
layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba8, binding = 0) uniform writeonly image2D uOutVis;
layout(rg32ui, binding = 1) uniform writeonly uimage2D uOutKey;

uniform ivec4 uRect;     // the render viewport: xy = origin in window pixels, zw = size
uniform mat4 uInvVp;     // inverse of the JITTERED view-projection the prepass rasterised with
uniform mat4 uProj;      // ...and that projection, whose depth row linearises the buffer
uniform ivec2 uShSize;   // the record's own size in texels
uniform int uShRes;      // shadow.deferredres: the record's divisor, 1 = full resolution, 2 = half

/// [3] shadow-casting lights left UNSHADOWED past shadow.perpixel's budget (the walk hardware-taps them only on
/// a pixel with no record at all). A budget with a counter, never a budget with a silence. Shared with
/// cx_deferred_cs (DeferredLights owns the buffer).
layout(std430, binding = 14) buffer CxDeferredStats { uint uDefStats[]; };

shared uint sDrop;

void main()
{
    uint lane = gl_LocalInvocationIndex;
    if (lane == 0u) sDrop = 0u;
    barrier();

    ivec2 r = ivec2(gl_GlobalInvocationID.xy);
    bool mine = all(lessThan(r, uShSize));
    vec4 vis = vec4(1.0);
    uvec2 rec = uvec2(0u);
    uint drop = 0u;
    if (mine)
    {
        // The screen pixel this texel READS: at half resolution the top-left of its 2x2 block. The reader
        // weights the texel as if it sat at the block's CENTRE, which buys symmetric weights -- four texels
        // at 9/16, 3/16, 3/16 and 1/16 for every pixel -- at the price of shifting the half-resolution shadow
        // field by half a full-resolution pixel on each axis. A uniform sub-pixel shift, against a weighting
        // that would otherwise read one texel alone for a quarter of the pixels and mix none of the rotations.
        ivec2 p = r * uShRes + uRect.xy;
        cxPixel = vec2(p) + 0.5;
        float ndcZ = texelFetch(uGbDepth, p, 0).r * 2.0 - 1.0;
        float viewDepth = uProj[3].z / (ndcZ + uProj[2].z);
        CxGbuf g;
        if (CxGbufReadHead(texelFetch(uGb0, p, 0), viewDepth, g))
        {
            cxRecvShadow = (g.flags & CX_GB_NOSHADOW) != 0u ? 0.0 : 1.0;
            // shadow.cubedist and the receiver's own flag, exactly as the walk computes them: a pixel that owes
            // no cube read at all gets no record, and the walk's own dfade then reads 1 for every light.
            float dfade = (uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, viewDepth) : 1.0)
                        * cxRecvShadow;
            // shadow.cap 0 means "every light, one hardware tap", so it stands the record -- and with it the
            // per-pixel budget -- down outright. Otherwise the deferred budget is shadow.perpixel up to the
            // record's CX_SH_MAX, not the forward cap's two named slots.
            if (dfade > 0.0 && uLocalSoft.w > 0.0)
            {
                vec4 h = uInvVp * vec4(2.0 * (cxPixel - vec2(uRect.xy)) / vec2(uRect.zw) - 1.0, ndcZ, 1.0);
                if (abs(h.w) > 1e-6)
                {
                    vec3 worldPos = h.xyz / h.w;
                    float baked = (g.flags & CX_GB_BAKED) != 0u ? 1.0 : 0.0;
                    float onMover = (g.flags & CX_GB_ONMOVER) != 0u ? 1.0 : 0.0;
#if defined(CX_ABLATE) && CX_ABLATE == 1
                    // Ablation rung 1 (`/alloy bench`, CX.Alloy.AblateProgram): the G-buffer read, the
                    // record's gate and the world position, with no candidate ranked and no cube tapped.
                    // The key is still written, so the walk accepts the record -- an all-visible one, which
                    // is why this rung is a brighter frame as well as a cheaper one.
                    vis = vec4(1.0 - fract(dot(worldPos, vec3(1.0)) + baked + onMover + dfade) * CX_ABL_EPS);
                    rec = uvec2(0u, CxShPackKey(viewDepth, g.N));
#else
                    ivec4 ks; vec4 kn, kw; float rDrop;
                    drop = CxShRank(worldPos, g.N, viewDepth, baked, ks, kn, kw, rDrop);
#if defined(CX_ABLATE) && CX_ABLATE == 2
                    // Ablation rung 2: the candidate RANKING and the dissolve, with not one cube tapped.
                    // The slots are written, so the walk reads this record and lights those lights fully
                    // visible; the sink is what keeps the ranking chain alive.
                    vis = vec4(1.0 - fract(kn.x + rDrop + dot(kw, vec4(1.0)) + float(drop)) * CX_ABL_EPS);
#else
                    // the reader's bilateral upsample mixes the four interleaved rotations: 16 distinct tap
                    // positions per screen 2x2 at half resolution
                    vis = CxShTapRecord(ks, kn, kw, rDrop, worldPos, g.N, onMover, viewDepth, CxShRot(r));
#endif
                    rec = uvec2(CxShPackSlots(ks), CxShPackKey(viewDepth, g.N));
#endif
                }
            }
        }
        imageStore(uOutVis, r, vis);
        // a record with no key is no record: the reader drops it rather than reading a stale texel
        imageStore(uOutKey, r, uvec4(rec, 0u, 0u));
    }
    if (drop != 0u) atomicAdd(sDrop, drop);
    barrier();
    if (lane == 0u && sDrop != 0u) atomicAdd(uDefStats[3], sDrop);
}
