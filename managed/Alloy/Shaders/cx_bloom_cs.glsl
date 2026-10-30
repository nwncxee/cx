#version 460 core
// One bloom level as a dispatch: cx_bloomdown_f's 13-tap downsample (the Karis average and the soft-knee threshold
// on the first level), or cx_bloomup_f's tent upsample added onto the level it writes. The same taps at the same
// coordinates, so the chain reads as the fragment one does.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 0) uniform sampler2D uSrc;
layout(rgba16f, binding = 0) uniform image2D uDst;
layout(push_constant) uniform CxBloom { vec2 uTexel; vec2 uThresh; float uExposure; int uFirst; int uUp; };

#include "cx_bloom_lib.glsl"

void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy), sz = imageSize(uDst);
    if (any(greaterThanEqual(p, sz))) return;
    gUv = (vec2(p) + 0.5) / vec2(sz); gLod = 0.0; gTexel = uTexel;
    if (uUp != 0) imageStore(uDst, p, imageLoad(uDst, p) + vec4(CxBloomUp(), 1.0));
    else imageStore(uDst, p, vec4(CxBloomDown(uFirst != 0, uThresh, uExposure), 1.0));
}
