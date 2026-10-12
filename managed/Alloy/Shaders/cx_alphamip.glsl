// CX-191: mip-level alpha compensation for the cutout test (Ben Golus, "Anti-aliased Alpha Test"). A mip's
// alpha is the AVERAGE of the texels under it, so a leaf's coverage shrinks level by level and distant foliage
// thins out and shimmers as the level changes. The test reads the alpha scaled up by the level the fragment
// samples; the authored mips and the alpha that is drawn are untouched. du1/du2 are dFdx/dFdy of the uv,
// taken by the caller ahead of its first discard; bias is the lod bias the caller samples with. The level
// stops at the last mip: past it the sampler stops averaging, and scaling on would fill the whole quad.
uniform float uAlphaMip;        // alloy.cutmip: 0 = the plain test
// CX-218: slot-mask bit 13 (AlloyRenderer.CoverNrmBit) -- this record's COVERAGE is map slot 1's alpha, never
// the diffuse's. Set on a recipe whose customShaderFS is one of ShaderFeatures.CoverNrmFs: those content
// stages discard on `texUnit1.a < 0.2` and spend the diffuse alpha on the env-map level instead, so a cutout
// test on the diffuse draws them solid -- an iron fence with no gaps between its swirls, a head with solid
// eyelashes. Declared here because every stage that makes the test includes this library.
#define CX_SLOT_COVERNRM 8192
// CX-223: the same compensation at a scale the CALLER names, for a coverage source whose scale is the
// material's own rather than the knob's. Compensating twice over one channel fills a distant silhouette
// solid, so a material that states its own scale is compensated at that scale and at no other.
float CxAlphaMipAt(float a, vec2 du1, vec2 du2, vec2 texSize, float bias, float scale)
{
    if (scale <= 0.0) return a;
    vec2 dx = du1 * texSize, dy = du2 * texSize;
    float lod = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8)) + bias;
    return a * (1.0 + clamp(lod, 0.0, log2(max(texSize.x, texSize.y))) * scale);
}
float CxAlphaMip(float a, vec2 du1, vec2 du2, vec2 texSize, float bias)
{
    return CxAlphaMipAt(a, du1, du2, texSize, bias, uAlphaMip);
}
