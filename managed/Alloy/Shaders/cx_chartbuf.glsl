// #674: the bake record per ownership slot -- rec = (page, u0, v0, s): page layer (-1 = unbaked), the rect's
// origin in page texels, page texels per atlas texel; CX-56 pick = the four static lights (set indices, -1 =
// none) whose mover shares page C's four bytes hold. Written on landing/enrol/vacate edges, never per frame.
struct CxChart { vec4 rec; vec4 pick; };
layout(std430, binding = 13) readonly buffer CxChartBuf { CxChart uChart[]; };
layout(binding = 20) uniform sampler2DArray uLmA;   // irradiance, BC6H
layout(binding = 21) uniform sampler2DArray uLmB;   // dominant direction, BC7 as xyz * 0.5 + 0.5
layout(binding = 22) uniform sampler2DArray uLmC;   // CX-56: per mover channel, the share of the texel a mover takes back
uniform int uLmPages;                                // resident layers; 0 = no lightmap this frame
const float CX_LM_PAGE = 4096.0;
// The read: false = unbaked (dynamic instance, refused mesh, bake in flight, past the page cap).
bool CxLightmap(vec4 rec, vec2 chart, out vec3 irr, out vec3 dir)
{
    irr = vec3(0.0); dir = vec3(0.0, 0.0, 1.0);
    if (rec.x < 0.0 || uLmPages <= 0) return false;
    vec3 uv = vec3((rec.yz + chart * rec.w) / CX_LM_PAGE, rec.x);
    irr = texture(uLmA, uv).rgb;
    vec4 bt = texture(uLmB, uv);
    vec3 b = bt.xyz * 2.0 - 1.0;
    float l = length(b);
    if (l > 1e-4) dir = b / l;
    return true;
}
// CX-56: the texel's mover shares, one per pick: a mover wholly in the path of the record's pick[k] leaves
// lm * (1 - share.k). Zero on an unbaked record, where nothing reads it.
vec4 CxLightmapMover(vec4 rec, vec2 chart)
{
    if (rec.x < 0.0 || uLmPages <= 0) return vec4(0.0);
    return texture(uLmC, vec3((rec.yz + chart * rec.w) / CX_LM_PAGE, rec.x));
}
// A lightmap texel in world units: what the bake widened its shadow disc to (LightmapBake.DrawRect).
float CxLmTexelWorld(vec4 rec) { return rec.w > 0.0 ? 1.0 / (8.0 * rec.w) : 0.0; }
// Unity's directional decode: the irradiance was baked against the VERTEX normal Ng; the shading
// normal N (a normal map) rebalances it, so bump survives under static light (#666).
vec3 CxLightmapLit(vec3 irr, vec3 dir, vec3 N, vec3 Ng)
{
    return irr * max(dot(N, dir), 0.0) / max(dot(Ng, dir), 0.1);
}
