#version 460 core
in vec2 vUV;
out float frag;
layout(binding = 1) uniform sampler2D uSrc;      // the raw visibility
#include "cx_ssaolib.glsl"
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
            float d = CxViewDist(textureLod(uDepth, (vec2(p) + 0.5) * inv, 0.0).r);
            float w = clamp(1.0 - abs(d - d0) / (0.05 * d0), 0.0, 1.0);
            sum += texelFetch(uSrc, p, 0).r * w;
            wsum += w;
        }
    frag = wsum > 0.0 ? sum / wsum : texelFetch(uSrc, pix, 0).r;
}
