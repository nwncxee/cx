#version 460 core
// One lane of a target into a word array, row by row from row 0, for a read-back (VkReadback, the frame tap).
// CX_READBACK_UINT reads an integer target (the G-buffer), else a float one; CX_READBACK_WORDS is the words per texel:
// 4 or 1 for an integer lane, 2 for colour (two half pairs), 1 for depth.
layout(local_size_x = 8, local_size_y = 8) in;
layout(std430, binding = 8) writeonly buffer CxReadbackOut { uint uOut[]; };
#ifdef CX_READBACK_UINT
layout(binding = 0) uniform usampler2D uSrc;
#else
layout(binding = 0) uniform sampler2D uSrc;
#endif
void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy), sz = textureSize(uSrc, 0);
    if (any(greaterThanEqual(p, sz))) return;
    uint i = uint(p.y * sz.x + p.x) * uint(CX_READBACK_WORDS);
#ifdef CX_READBACK_UINT
    uvec4 v = texelFetch(uSrc, p, 0);
    uOut[i] = v.x;
#if CX_READBACK_WORDS == 4
    uOut[i + 1u] = v.y; uOut[i + 2u] = v.z; uOut[i + 3u] = v.w;
#endif
#elif CX_READBACK_WORDS == 2
    vec4 v = texelFetch(uSrc, p, 0);
    uOut[i] = packHalf2x16(v.rg);
    uOut[i + 1u] = packHalf2x16(v.ba);
#else
    uOut[i] = floatBitsToUint(texelFetch(uSrc, p, 0).r);
#endif
}
