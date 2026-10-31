#version 460 core
// CX-51: the reflection trace's hi-Z pyramid. Level 0 is the frame depth as INVERSE view depth (1/w, which
// is linear in screen space, so the march interpolates it exactly); each level above is the MAX over the
// footprint of the one below -- the CLOSEST surface in the cell, which a ray must reach before it can hit
// anything there. 3x3 rather than 2x2 so an odd-sized level's last row and column are never dropped.
// CX_SSR_STORAGE (Vulkan) reads the level below through its own storage view, as an image cannot be sampled and
// stored at once there; CX_SSR_COLOUR is that device's mip chain of the colour copy, which it cannot generate.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 2) uniform sampler2D uSrc;          // the world depth (level 0, reversed) or this pyramid (above)
#ifdef CX_SSR_COLOUR
layout(rgba16f, binding = 0) writeonly uniform image2D uDst;
layout(rgba16f, binding = 1) readonly uniform image2D uPrev;
#else
layout(r32f, binding = 0) writeonly uniform image2D uDst;
#ifdef CX_SSR_STORAGE
layout(r32f, binding = 1) readonly uniform image2D uPrev;
#endif
#endif
uniform int uLevel;
uniform vec2 uNf;                                     // the frame's near/far
void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(p, imageSize(uDst)))) return;
#ifdef CX_SSR_COLOUR
    // level 0 is the frame at the copy's size: a texel centre between four frame texels is their 2:1 box
    vec4 c;
    if (uLevel == 0) c = textureLod(uSrc, (vec2(p) + 0.5) / vec2(imageSize(uDst)), 0.0);
    else
    {
        ivec2 lim = imageSize(uPrev) - ivec2(1), s = p * 2;
        c = 0.25 * (imageLoad(uPrev, min(s, lim)) + imageLoad(uPrev, min(s + ivec2(1, 0), lim))
                  + imageLoad(uPrev, min(s + ivec2(0, 1), lim)) + imageLoad(uPrev, min(s + ivec2(1, 1), lim)));
    }
    imageStore(uDst, p, c);
#else
    float k = 0.0;
    if (uLevel == 0)
    {
        float d = texelFetch(uSrc, p, 0).r;
        k = (uNf.x + d * (uNf.y - uNf.x)) / (uNf.x * uNf.y);
    }
    else
    {
#ifdef CX_SSR_STORAGE
        ivec2 lim = imageSize(uPrev) - ivec2(1), s = p * 2;
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                k = max(k, imageLoad(uPrev, min(s + ivec2(x, y), lim)).r);
#else
        ivec2 lim = textureSize(uSrc, uLevel - 1) - ivec2(1), s = p * 2;
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                k = max(k, texelFetch(uSrc, min(s + ivec2(x, y), lim), uLevel - 1).r);
#endif
    }
    imageStore(uDst, p, vec4(k));
#endif
}
