#version 460 core
in vec2 vUV;
out vec2 frag;
uniform sampler2D uRaw;      // this frame's visibility, blurred
uniform sampler2D uHist;     // last frame's (visibility, view distance), LINEAR
uniform sampler2D uVel;      // #388: prev - cur, render pixels
#include "cx_ssaolib.glsl"
void main()
{
    float raw = textureLod(uRaw, vUV, 0.0).r;
    float depth = textureLod(uDepth, vUV, 0.0).r;
    float dist = depth >= 1.0 ? 0.0 : CxViewDist(depth);
    ivec2 hsz = textureSize(uHist, 0);
    vec2 huv = vUV + textureLod(uVel, vUV, 0.0).rg / vec2(textureSize(uVel, 0));
    float w = uWeight;
    if (dist <= 0.0 || any(lessThan(huv, vec2(0.0))) || any(greaterThan(huv, vec2(1.0)))) w = 0.0;
    vec2 h = textureLod(uHist, huv, 0.0).rg;
    // the depth test reads the NEAREST texel: LINEAR blends near and far at a silhouette and rejected
    // every edge pixel every frame (the fluorescent-edge flicker)
    float hd = texelFetch(uHist, clamp(ivec2(huv * vec2(hsz)), ivec2(0), hsz - 1), 0).g;
    w *= 1.0 - clamp(abs(hd - dist) / (0.1 * max(dist, 1e-3)), 0.0, 1.0);
    // the neighbourhood clamp: history outside the blurred estimate's 3x3 range is stale, not signal.
    // ponytail: fixed pad, not a variance clip -- a tight clamp on a six-fetch estimate yanks the
    // history every frame at edges where the depth-aware blur left the 3x3 unsmoothed
    const float PAD = 0.1;
    ivec2 pix = ivec2(gl_FragCoord.xy), sz = textureSize(uRaw, 0);
    float mn = raw, mx = raw;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            float n = texelFetch(uRaw, clamp(pix + ivec2(x, y), ivec2(0), sz - 1), 0).r;
            mn = min(mn, n); mx = max(mx, n);
        }
    frag = vec2(mix(raw, clamp(h.r, mn - PAD, mx + PAD), w), dist);
}
