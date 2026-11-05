#version 460 core
in vec2 vUV;
out float frag;
layout(binding = 1) uniform sampler2D uSrc;      // the raw visibility
#include "cx_ssaolib.glsl"
// A rejected texel below this weight hands its place to a twin one pattern tile away.
const float CX_AO_TWIN_BELOW = 0.5;
const ivec2 CX_AO_TILE[4] = ivec2[4](ivec2(-4, 0), ivec2(4, 0), ivec2(0, -4), ivec2(0, 4));

// How much the texel at p counts for a pixel at view distance d0.
float CxBlurWeight(ivec2 p, vec2 inv, float d0)
{
    float d = CxViewDist(textureLod(uDepth, (vec2(p) + 0.5) * inv, 0.0).r);
    return clamp(1.0 - abs(d - d0) / (0.05 * d0), 0.0, 1.0);
}

void main()
{
    ivec2 sz = textureSize(uSrc, 0);
    ivec2 pix = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
    vec2 inv = 1.0 / vec2(sz);
    float d0 = CxViewDist(textureLod(uDepth, vUV, 0.0).r);
    float sum = 0.0, wsum = 0.0;
    for (int y = -1; y <= 2; y++)
        for (int x = -1; x <= 2; x++)
        {
            ivec2 p = clamp(pix + ivec2(x, y), ivec2(0), sz - 1);
            float w = CxBlurWeight(p, inv, d0);
            // beside a silhouette the window loses part of the pattern: the same phase 4 texels on keeps all 16 in the sum
            for (int i = 0; i < 4 && w < CX_AO_TWIN_BELOW; i++)
            {
                ivec2 twin = clamp(pix + ivec2(x, y) + CX_AO_TILE[i], ivec2(0), sz - 1);
                float tw = CxBlurWeight(twin, inv, d0);
                if (tw > w) { w = tw; p = twin; }
            }
            sum += texelFetch(uSrc, p, 0).r * w;
            wsum += w;
        }
    frag = wsum > 0.0 ? sum / wsum : texelFetch(uSrc, pix, 0).r;
}
