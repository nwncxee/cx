// The ground mist (GroundMist): its field, its shape and its extinction, shared by the volumetric fog's two
// passes. cx_vfog_cs reads the mist at a point to light it; cx_vfogsum_cs integrates it along each slice of a ray.
// Needs cx_noise ahead of it.
#ifndef CX_MIST_LIB
#define CX_MIST_LIB

/// The height no ground mist reaches over its floor, metres: a standing character's waist.
const float CX_MIST_CAP = 0.8;

/// Extinction per metre inside the mist: thick enough that a path cut through it reads from across a street.
const float CX_MIST_INSIDE = 0.6;

/// How far the wind has carried the ground mist's patches (xy, metres) and its clock (z, seconds).
uniform vec4 uMistFlow;

/// The ground mist's extinction at height `z` over a column whose floor is `floorZ` and whose mist's top stands `top`
/// metres up: thickest on the floor, thinning all the way up to it, never past a standing character's waist.
float CxMistDensity(float top, float floorZ, float z)
{
    float t = min(top, CX_MIST_CAP), above = z - floorZ;
    if (t <= 0.0 || above < 0.0) return 0.0;
    return CX_MIST_INSIDE * (1.0 - smoothstep(t * 0.2, t, above));
}

/// The integral of CxMistDensity from the floor up to `above` metres over it: the mist a vertical metre of air holds.
float CxMistRise(float top, float above)
{
    float t = min(top, CX_MIST_CAP), x = clamp(above, 0.0, t), a = t * 0.2;
    if (t <= 0.0) return 0.0;
    if (x <= a) return CX_MIST_INSIDE * x;
    // smoothstep's own antiderivative across the thinning band
    float u = (x - a) / (t - a);
    return CX_MIST_INSIDE * (x - (t - a) * (u * u * u - 0.5 * u * u * u * u));
}

/// Where the top of mist `height` metres deep stands at `p` (metres, the wind's carry taken off) at `clock` seconds:
/// a deeper field covers more ground, and its top rises and dips with patches a few metres across that boil as they go.
float CxMistTop(float height, vec2 p, float clock)
{
    const float fullCover = 1.2, across = 2.0, boil = 0.4;
    vec3 q = vec3(p * (1.0 / across), clock * boil);
    vec2 warp = vec2(CxNoise3D(q * 0.5 + vec3(5.2, 1.3, 0.0)), CxNoise3D(q * 0.5 + vec3(9.7, 2.8, 4.1)));
    float n = 0.5 + 0.5 * CxNoise3D(q + vec3(warp * 0.9, 0.0));
    return CX_MIST_CAP * clamp(min(height / fullCover, 1.0) * (0.3 + 1.4 * n), 0.0, 1.0);
}

// The ground mist's field (GroundMist): xy its corner in whole metres, z its side in columns, w 1 = mist shows;
// then per column, row-major from the corner, its floor height and the height its mist stands.
layout(std430, binding = 12) readonly buffer CxMistBuf
{
    vec4 uMistHead;
    vec2 uMistCell[];
};

/// Column `c` of a field `side` columns across: its floor, its drawn height of mist and its motion across the ground.
vec4 CxMistTap(ivec2 c, int side)
{
    vec2 col = uMistCell[c.y * side + c.x];
    return vec4(col.x, col.y, 0.0, 0.0);
}

/// The floor and the height of mist under world point `xy`, blended over the four columns around it; false past the
/// field's edge, over a column with no floor, or where no mist stands.
bool CxMistColumn(vec2 xy, out float floorZ, out float height)
{
    floorZ = 0.0; height = 0.0;
    if (uMistHead.w <= 0.0) return false;
    int side = int(uMistHead.z);
    vec2 f = xy - uMistHead.xy - 0.5;
    ivec2 c0 = ivec2(floor(f));
    vec2 t = f - vec2(c0);
    float wsum = 0.0;
    for (int k = 0; k < 4; k++)
    {
        ivec2 o = ivec2(k & 1, k >> 1), c = c0 + o;
        if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(side)))) continue;
        vec4 col = CxMistTap(c, side);
        if (col.x < -1e8) continue;
        float w = mix(1.0 - t.x, t.x, float(o.x)) * mix(1.0 - t.y, t.y, float(o.y));
        wsum += w; floorZ += w * col.x; height += w * col.y;
    }
    if (wsum <= 1e-4 || height <= 0.0) return false;
    floorZ /= wsum;
    return true;
}

/// The ground mist's extinction at `world`.
float CxVfMist(vec3 world)
{
    float floorZ, height;
    if (!CxMistColumn(world.xy, floorZ, height)) return 0.0;
    if (world.z < floorZ || world.z >= floorZ + CX_MIST_CAP) return 0.0;
    return CxMistDensity(CxMistTop(height, world.xy - uMistFlow.xy, uMistFlow.z), floorZ, world.z);
}

/// The ground mist's optical depth along the straight segment from `a` to `b`, integrated exactly through its height
/// over the column and top found at the segment's middle; a segment that lies wholly in the air above takes none.
float CxMistDepth(vec3 a, vec3 b)
{
    vec3 m = 0.5 * (a + b);
    float floorZ, height;
    if (!CxMistColumn(m.xy, floorZ, height)) return 0.0;
    float za = a.z - floorZ, zb = b.z - floorZ;
    if (min(za, zb) >= CX_MIST_CAP) return 0.0;
    float top = CxMistTop(height, m.xy - uMistFlow.xy, uMistFlow.z), len = distance(a, b);
    // a level segment: the slope's quotient below would divide by nothing
    if (abs(zb - za) < 1e-3) return CxMistDensity(top, floorZ, m.z) * len;
    return (CxMistRise(top, zb) - CxMistRise(top, za)) / (zb - za) * len;
}
#endif
