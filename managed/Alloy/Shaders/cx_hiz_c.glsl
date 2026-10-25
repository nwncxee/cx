#version 460 core
// One level of the hi-Z pyramid: each texel the max of the 3x3 under it one level up, clamped at the edge, as
// cx_hiz_f does on GL. CX_HIZ_FIRST reads the scene depth; otherwise the level above, through its storage view.
layout(local_size_x = 8, local_size_y = 8) in;
#ifdef CX_HIZ_FIRST
layout(binding = 0) uniform sampler2D uSrc;
#define CX_SIZE textureSize(uSrc, 0)
#define CX_FETCH(p) texelFetch(uSrc, p, 0).r
#else
layout(r32f, binding = 1) readonly uniform image2D uSrc;
#define CX_SIZE imageSize(uSrc)
#define CX_FETCH(p) imageLoad(uSrc, p).r
#endif
layout(r32f, binding = 0) writeonly uniform image2D uDst;
void main()
{
    ivec2 o = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(o, imageSize(uDst)))) return;
    ivec2 lim = CX_SIZE - ivec2(1);
    ivec2 s = o * 2;
    float m = 0.0;
    for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
            m = max(m, CX_FETCH(min(s + ivec2(x, y), lim)));
    imageStore(uDst, o, vec4(m));
}
