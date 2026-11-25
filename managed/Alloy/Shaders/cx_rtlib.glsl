// alloy.raytrace: the ray-traced scene a stage traces where the screen runs out (AlloyRenderer.VkRt). Everything is
// reached by device address, so nothing here takes a binding; a stage defines CX_RT, puts the three extension lines
// after its version line and includes this. CX_RT_LM also lights a hit from the lightmap, whose pages the stage declares as uRtLmA.
layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer CxRtF { float v[]; };
layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer CxRtU { uint v[]; };
layout(buffer_reference, std430, buffer_reference_align = 16) readonly buffer CxRtG { uvec4 v[]; };
layout(buffer_reference, std430, buffer_reference_align = 16) readonly buffer CxRtC { vec4 v[]; };
// addresses as (low, high) words, x/y then z/w
uniform uvec4 uRtA;          // the top level (0 = no ray this frame), the per-slot geometry rows
uniform uvec4 uRtV01, uRtV23; // the four vertex layouts' buffers
uniform uvec4 uRtE;          // the indices, the texture handle table
uniform uvec4 uRtL;          // the lightmap's bake records, z = its resident pages (0 = none)
uniform vec4 uRtSun;         // the sun's direction
uniform vec4 uRtSunCol, uRtAmb;
uniform vec4 uRtFog;         // display-space colour, w = fog on
uniform vec2 uRtFogRange;    // linear start and end
uniform vec2 uRtShadow;      // shadow.raytrace: x on, y the sun's disc as a cone half-angle (radians)
/// A geometry row's flags past its layout byte: an alpha-tested cutout, a charted copy that reads the lightmap.
const uint CX_RT_CUTOUT = 256u, CX_RT_BAKED = 512u;
float CxRtLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
/// Rays start this far off the surface (metres) and give up past this distance.
const float CX_RT_BIAS = 0.02, CX_RT_FAR = 60.0;
/// Perceptual roughness over which a mirror ray stops standing for the lobe; the probe takes the rest.
const vec2 CX_RT_ROUGH = vec2(0.25, 0.5);
const float CX_RT_LM_PAGE = 4096.0;   // cx_chartbuf's CX_LM_PAGE
const float CX_RT_ALPHA = 0.5;   // a cutout's alpha test, as the lit stage's discard

CxRtF CxRtVbo(uint fmt)
{
    uvec2 a = fmt == 0u ? uRtV01.xy : fmt == 1u ? uRtV01.zw : fmt == 2u ? uRtV23.xy : uRtV23.zw;
    return CxRtF(packUint2x32(a));
}

/// Index <paramref name="i"/> of the arena's 16-bit list, read as half a word.
uint CxRtIndex(uint i)
{
    uint w = CxRtU(packUint2x32(uRtE.xy)).v[i >> 1];
    return (i & 1u) != 0u ? w >> 16 : w & 0xFFFFu;
}

/// The uv, object-space normal and lightmap uv at barycentric <paramref name="bc"/> of slot <paramref name="slot"/>'s triangle <paramref name="prim"/>.
void CxRtTri(uint slot, uint prim, vec2 bc, out vec2 uv, out vec3 nrm, out vec2 chart)
{
    uvec4 g = CxRtG(packUint2x32(uRtA.zw)).v[slot];
    CxRtF vb = CxRtVbo(g.x & 0xFFu);
    uv = vec2(0.0); nrm = vec3(0.0); chart = vec2(0.0);
    for (uint k = 0u; k < 3u; k++)
    {
        uint b = (g.y + CxRtIndex(g.z + prim * 3u + k)) * g.w;
        float wk = k == 0u ? 1.0 - bc.x - bc.y : k == 1u ? bc.x : bc.y;
        nrm += vec3(vb.v[b + 3u], vb.v[b + 4u], vb.v[b + 5u]) * wk;
        uv += vec2(vb.v[b + 6u], vb.v[b + 7u]) * wk;
        chart += vec2(vb.v[b + 8u], vb.v[b + 9u]) * wk;
    }
}

/// Slot <paramref name="slot"/>'s diffuse at <paramref name="uv"/>, still display-encoded; white where it has none.
vec4 CxRtDiffuse(uint slot, vec2 uv, float lod)
{
    uint h = CxRtU(packUint2x32(uRtE.zw)).v[slot << 3];
    return h == 0u ? vec4(1.0) : textureLod(sampler2D(uvec2(h, 0u)), uv, lod);
}

/// The colour a ray from <paramref name="o"/> along <paramref name="dir"/> meets: the hit's diffuse lit by the area's sun
/// (a shadow ray to it) and ambient, faded into the fog. False on a miss.
bool CxRtTrace(vec3 o, vec3 dir, float perceptual, vec3 eye, out vec3 colour)
{
    colour = vec3(0.0);
    rayQueryEXT q;
    rayQueryInitializeEXT(q, accelerationStructureEXT(packUint2x32(uRtA.xy)), gl_RayFlagsNoneEXT, 0xFFu, o, 0.0, dir, CX_RT_FAR);
    while (rayQueryProceedEXT(q))
    {
        // only a cutout's triangles come here: the alpha test the lit stage's discard makes
        if (rayQueryGetIntersectionTypeEXT(q, false) != gl_RayQueryCandidateIntersectionTriangleEXT) continue;
        vec2 cuv, cch; vec3 cn;
        CxRtTri(uint(rayQueryGetIntersectionInstanceCustomIndexEXT(q, false)), uint(rayQueryGetIntersectionPrimitiveIndexEXT(q, false)),
                rayQueryGetIntersectionBarycentricsEXT(q, false), cuv, cn, cch);
        if (CxRtDiffuse(uint(rayQueryGetIntersectionInstanceCustomIndexEXT(q, false)), cuv, 0.0).a >= CX_RT_ALPHA)
            rayQueryConfirmIntersectionEXT(q);
    }
    if (rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionTriangleEXT) return false;
    float t = rayQueryGetIntersectionTEXT(q, true);
    uint slot = uint(rayQueryGetIntersectionInstanceCustomIndexEXT(q, true));
    vec2 uv, chart; vec3 nObj;
    CxRtTri(slot, uint(rayQueryGetIntersectionPrimitiveIndexEXT(q, true)), rayQueryGetIntersectionBarycentricsEXT(q, true), uv, nObj, chart);
    mat4x3 toWorld = rayQueryGetIntersectionObjectToWorldEXT(q, true);
    vec3 N = normalize(mat3(toWorld) * nObj);
    if (dot(N, dir) > 0.0) N = -N;
    // farther and rougher reads a blurrier mip, as the screen's cone does
    float lod = clamp(log2(1.0 + t * 0.15) + perceptual * 6.0, 0.0, 8.0);
    vec3 albedo = pow(CxRtDiffuse(slot, uv, lod).rgb, vec3(2.2));
    vec3 P = o + dir * t;
    float sun = max(dot(N, uRtSun.xyz), 0.0);
    if (sun > 0.0 && CxRtLuma(uRtSunCol.rgb) > 1e-3)
    {
        rayQueryEXT s;
        rayQueryInitializeEXT(s, accelerationStructureEXT(packUint2x32(uRtA.xy)),
                              gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsOpaqueEXT, 0xFFu, P + N * CX_RT_BIAS, 0.0, uRtSun.xyz, CX_RT_FAR);
        while (rayQueryProceedEXT(s)) { }
        if (rayQueryGetIntersectionTypeEXT(s, true) != gl_RayQueryCommittedIntersectionNoneEXT) sun = 0.0;
    }
    vec3 lm = vec3(0.0);
#ifdef CX_RT_LM
    // a charted copy's lightmap: every static lamp and torch the bake saw, as the lit stage reads it
    if ((CxRtG(packUint2x32(uRtA.zw)).v[slot].x & CX_RT_BAKED) != 0u && uRtL.z > 0u)
    {
        vec4 rec = CxRtC(packUint2x32(uRtL.xy)).v[slot * 2u];
        if (rec.x >= 0.0) lm = textureLod(uRtLmA, vec3((rec.yz + chart * rec.w) / CX_RT_LM_PAGE, rec.x), 0.0).rgb;
    }
#endif
    colour = albedo * (uRtAmb.rgb + uRtSunCol.rgb * sun + lm);
    if (uRtFog.w > 0.0)
    {
        float f = clamp((distance(P, eye) - uRtFogRange.x) / max(uRtFogRange.y - uRtFogRange.x, 1e-3), 0.0, 1.0);
        colour = mix(colour, pow(uRtFog.rgb, vec3(2.2)), f);
    }
    return true;
}

/// Is anything solid between <paramref name="o"/> and <paramref name="tmax"/> along <paramref name="d"/>? A cutout's
/// texels are alpha-tested, so a tree throws leaves, not cards.
bool CxRtOccluded(vec3 o, vec3 d, float tmax)
{
    rayQueryEXT q;
    rayQueryInitializeEXT(q, accelerationStructureEXT(packUint2x32(uRtA.xy)), gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, o, 0.0, d, tmax);
    while (rayQueryProceedEXT(q))
    {
        if (rayQueryGetIntersectionTypeEXT(q, false) != gl_RayQueryCandidateIntersectionTriangleEXT) continue;
        uint slot = uint(rayQueryGetIntersectionInstanceCustomIndexEXT(q, false));
        vec2 uv, ch; vec3 nn;
        CxRtTri(slot, uint(rayQueryGetIntersectionPrimitiveIndexEXT(q, false)), rayQueryGetIntersectionBarycentricsEXT(q, false), uv, nn, ch);
        if (CxRtDiffuse(slot, uv, 0.0).a >= CX_RT_ALPHA) rayQueryConfirmIntersectionEXT(q);
    }
    return rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT;
}

/// shadow.raytrace: the sun's visibility at <paramref name="p"/>, two rays across its disc turned by <paramref name="noise"/>
/// (per pixel and frame) for the temporal filter to average into a penumbra that hardens where a caster meets its shadow.
float CxRtSunVis(vec3 p, vec3 ng, vec3 L, float noise)
{
    vec3 t = normalize(cross(L, abs(L.z) < 0.9 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0))), b = cross(L, t);
    vec3 o = p + ng * CX_RT_BIAS;
    float vis = 0.0;
    for (int k = 0; k < 2; k++)
    {
        float a = (noise + 0.5 * float(k)) * 6.2831853, r = sqrt(fract(noise * 7.13 + 0.37 * float(k))) * uRtShadow.y;
        vec3 d = normalize(L + (t * cos(a) + b * sin(a)) * r);
        if (!CxRtOccluded(o, d, CX_RT_FAR)) vis += 0.5;
    }
    return vis;
}
