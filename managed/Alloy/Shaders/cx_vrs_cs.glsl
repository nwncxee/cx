#version 460 core
// One shading rate per tile from the scene colour's contrast, for the next frame's G-buffer pass. A tile coarsens
// along an axis when two means stay under uContrast, measured on sqrt(L / (1 + L)), a tonemap then a gamma: the
// step inside the pixel pairs a 2-wide (or 2-tall) fragment would merge, and half the step from one 2x2 block to
// the next. The second is what a tile already shaded coarse still shows; the first reads 0 there and would hold
// it coarse for good. Written as Vulkan's rate encoding,
// (log2 width) << 2 | log2 height: 0 = 1x1, 1 = 1x2, 4 = 2x1, 5 = 2x2.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 0) uniform sampler2D uScene;
layout(r8ui, binding = 0) writeonly uniform uimage2D uRate;
layout(push_constant) uniform CxVrs { float uContrast; int uTile; };

// the step sums in 1/65536ths: a tile of 32 holds 512 pairs per axis, far under the word
const float CX_VRS_FIX = 65536.0;
shared uint sStepX, sStepY, sPairsX, sPairsY, sCrossX, sCrossY, sBlocksX, sBlocksY;

float CxVrsLevel(ivec2 p)
{
    float l = dot(texelFetch(uScene, p, 0).rgb, vec3(0.2126, 0.7152, 0.0722));
    l = min(l > 0.0 ? l : 0.0, 65504.0);   // NaN and negatives read as black
    return sqrt(l / (1.0 + l));
}

void main()
{
    if (gl_LocalInvocationIndex == 0u)
    {
        sStepX = 0u; sStepY = 0u; sPairsX = 0u; sPairsY = 0u;
        sCrossX = 0u; sCrossY = 0u; sBlocksX = 0u; sBlocksY = 0u;
    }
    barrier();
    ivec2 size = textureSize(uScene, 0), origin = ivec2(gl_WorkGroupID.xy) * uTile;
    ivec2 end = min(size, origin + uTile);
    int side = uTile / 2;
    uint stepX = 0u, stepY = 0u, pairsX = 0u, pairsY = 0u, crossX = 0u, crossY = 0u, blocksX = 0u, blocksY = 0u;
    for (int b = int(gl_LocalInvocationIndex); b < side * side; b += int(gl_WorkGroupSize.x * gl_WorkGroupSize.y))
    {
        ivec2 p = origin + 2 * ivec2(b % side, b / side);
        if (any(greaterThanEqual(p, size))) continue;
        bool right = p.x + 1 < size.x, down = p.y + 1 < size.y;
        float a = CxVrsLevel(p);
        float r = right ? CxVrsLevel(p + ivec2(1, 0)) : a;
        float d = down ? CxVrsLevel(p + ivec2(0, 1)) : a;
        float rd = right && down ? CxVrsLevel(p + ivec2(1, 1)) : d;
        if (right) { stepX += uint(abs(a - r) * CX_VRS_FIX + 0.5); pairsX++; }
        if (right && down) { stepX += uint(abs(d - rd) * CX_VRS_FIX + 0.5); pairsX++; }
        if (down) { stepY += uint(abs(a - d) * CX_VRS_FIX + 0.5); pairsY++; }
        if (right && down) { stepY += uint(abs(r - rd) * CX_VRS_FIX + 0.5); pairsY++; }
        if (p.x + 2 < end.x) { crossX += uint(abs(a - CxVrsLevel(p + ivec2(2, 0))) * 0.5 * CX_VRS_FIX + 0.5); blocksX++; }
        if (p.y + 2 < end.y) { crossY += uint(abs(a - CxVrsLevel(p + ivec2(0, 2))) * 0.5 * CX_VRS_FIX + 0.5); blocksY++; }
    }
    atomicAdd(sStepX, stepX); atomicAdd(sStepY, stepY);
    atomicAdd(sPairsX, pairsX); atomicAdd(sPairsY, pairsY);
    atomicAdd(sCrossX, crossX); atomicAdd(sCrossY, crossY);
    atomicAdd(sBlocksX, blocksX); atomicAdd(sBlocksY, blocksY);
    barrier();
    if (gl_LocalInvocationIndex != 0u) return;
    float limit = uContrast * CX_VRS_FIX;
    bool wide = float(sStepX) < limit * float(max(sPairsX, 1u)) && float(sCrossX) < limit * float(max(sBlocksX, 1u));
    bool tall = float(sStepY) < limit * float(max(sPairsY, 1u)) && float(sCrossY) < limit * float(max(sBlocksY, 1u));
    imageStore(uRate, ivec2(gl_WorkGroupID.xy), uvec4((wide ? 4u : 0u) | (tall ? 1u : 0u)));
}
