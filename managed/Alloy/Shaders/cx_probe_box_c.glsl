#version 460 core
// The probe capture's box chain where the device cannot generate mips (Vulkan): one level of the cube as the
// 2x2 mean of the level below, read through that level's own storage view. The prefilter and SH9 read the chain.
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba16f, binding = 0) writeonly uniform imageCube uDst;
layout(rgba16f, binding = 1) readonly uniform imageCube uPrev;
void main()
{
    ivec3 p = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(p.xy, imageSize(uDst)))) return;
    ivec2 lim = imageSize(uPrev) - ivec2(1), s = p.xy * 2;
    vec4 c = 0.25 * (imageLoad(uPrev, ivec3(min(s, lim), p.z)) + imageLoad(uPrev, ivec3(min(s + ivec2(1, 0), lim), p.z))
                   + imageLoad(uPrev, ivec3(min(s + ivec2(0, 1), lim), p.z)) + imageLoad(uPrev, ivec3(min(s + ivec2(1, 1), lim), p.z)));
    imageStore(uDst, p, c);
}
