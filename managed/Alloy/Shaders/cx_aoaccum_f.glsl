// The visibility accumulator: this frame's blurred estimate blended over the reprojected history, so a
// one-slice horizon per frame averages into a quiet one. The history carries the view distance it was
// computed at, which is the disocclusion test, and the blend is clamped into the estimate's own 3x3 range
// so stale history cannot outvote the frame.
#version 460 core
in vec2 vUV;
out vec2 frag;
layout(binding = 1) uniform sampler2D uSrc;      // this frame's visibility, blurred
layout(binding = 2) uniform sampler2D uHist;     // last frame's (visibility, view distance), LINEAR
#include "cx_ssaolib.glsl"
// A history view distance this far from the pixel's own, as a share of it, is another surface.
const float CX_AO_DISOCCLUDE = 0.1;
// ponytail: a fixed pad on the neighbourhood, not a variance clip -- a tight clamp on a six-fetch
// estimate yanks the history every frame at edges where the depth-aware blur left the 3x3 unsmoothed.
const float CX_AO_CLAMP_PAD = 0.1;

// Where the pixel at `uv` sat last frame, in this target's uv: the camera velocity pass's own expression
// (previous minus current, the current unjittered) in uv rather than render pixels.
vec2 CxAoPrevUv(vec2 uv, float worldDepth)
{
    vec4 ndc = vec4(uv * 2.0 - 1.0, worldDepth, 1.0);
    vec4 prev = uReproj * ndc;
    if (abs(prev.w) < 1e-6) return uv;
    vec2 v = prev.xy / prev.w - (ndc.xy - uJitter);
    if (any(isnan(v)) || any(isinf(v))) v = vec2(0.0);
    return uv + clamp(v, vec2(-2.0), vec2(2.0)) * 0.5;
}

void main()
{
    float raw = textureLod(uSrc, vUV, 0.0).r;
    float worldDepth = textureLod(uDepth, vUV, 0.0).r;
    // the world's depth is reversed, so nothing at all (the sky) reads 0 and has no distance to test
    float dist = worldDepth <= 0.0 ? 0.0 : CxViewDist(worldDepth);
    vec2 huv = CxAoPrevUv(vUV, worldDepth);
    float w = uWeight;
    if (dist <= 0.0 || any(lessThan(huv, vec2(0.0))) || any(greaterThan(huv, vec2(1.0)))) w = 0.0;
    // the pair holds whatever the allocation held until a frame writes it: a zero share must not even fetch it,
    // since one NaN texel carries through the blend and back into the history
    if (w <= 0.0) { frag = vec2(raw, dist); return; }
    ivec2 hsz = textureSize(uHist, 0);
    vec2 h = textureLod(uHist, huv, 0.0).rg;
    // the depth test reads the NEAREST texel: LINEAR blends near and far at a silhouette and would reject
    // every edge pixel every frame
    float hd = texelFetch(uHist, clamp(ivec2(huv * vec2(hsz)), ivec2(0), hsz - 1), 0).g;
    w *= 1.0 - clamp(abs(hd - dist) / (CX_AO_DISOCCLUDE * max(dist, 1e-3)), 0.0, 1.0);
    // by uv like every other term here: a viewport origin the pass inherits is not in the texel grid
    ivec2 sz = textureSize(uSrc, 0), pix = ivec2(vUV * vec2(sz));
    float mn = raw, mx = raw;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            float n = texelFetch(uSrc, clamp(pix + ivec2(x, y), ivec2(0), sz - 1), 0).r;
            mn = min(mn, n); mx = max(mx, n);
        }
    frag = vec2(mix(raw, clamp(h.r, mn - CX_AO_CLAMP_PAD, mx + CX_AO_CLAMP_PAD), w), dist);
}
