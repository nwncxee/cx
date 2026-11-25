// The foliage lanes (AlloyRenderer.DbWriteLeaf, DrawRecipe.LeafOf) and the leaf sway every stage that draws a record's
// vertices applies through ClipExpr. occ.z holds the canopy centre's model-space x and y as two halves; occ.w its z and a
// code as two halves. The code: 0 none, -1 a thin cut-out card, -2 / -3 refused (a content stage / another reason),
// > 0 foliage that sways, its canopy radius; <= CX_LEAF_STILL foliage that does not, radius CX_LEAF_STILL - code.
// Needs cx_drawbuf (CxDraw) and cx_featdecl (CxClock, uAreaWind) first.
#define CX_LEAF_STILL -10.0
// water.surfaces: painted water, a code no leaf range reaches (AlloyRenderer.WaterLookCode)
#define CX_WATER_LOOK -5.0
#define CX_SWAY_AMP 0.035        // metres a leaf at the canopy's rim travels in still air
#define CX_SWAY_WIND 2.0         // ...times this much more in a full area wind

/// The record's foliage code (see the head of this file).
float CxLeafCode(vec4 occ) { return unpackHalf2x16(floatBitsToUint(occ.w)).y; }

/// A foliage record's canopy, model space: false for anything else.
bool CxLeafCanopy(vec4 occ, out vec3 centre, out float radius, out bool sways)
{
    vec2 w = unpackHalf2x16(floatBitsToUint(occ.w));
    centre = vec3(unpackHalf2x16(floatBitsToUint(occ.z)), w.x);
    sways = w.y > 0.0;
    radius = sways ? w.y : CX_LEAF_STILL - w.y;
    return w.y > 0.0 || w.y <= CX_LEAF_STILL;
}

/// The model-space offset a foliage vertex at <p> sways by now: the rim moves and the core holds, each leaf on its own
/// phase so the canopy ripples rather than rocking whole, leaning with the area's wind. Zero for any other record.
vec3 CxLeafSway(CxDraw d, vec3 p)
{
    vec3 c;
    float r;
    bool sways;
    if (!CxLeafCanopy(d.occ, c, r, sways) || !sways) return vec3(0.0);
    float reach = clamp(length(p - c) / max(r, 0.1), 0.0, 1.0);
    float t = CxClock() * 0.001;
    float ph = dot(p, vec3(0.73, 0.91, 0.37));
    float wind = clamp(length(uAreaWind), 0.0, 1.0);
    vec3 dir = dot(uAreaWind.xy, uAreaWind.xy) > 1e-6 ? normalize(vec3(uAreaWind.xy, 0.0)) : vec3(0.7071, 0.7071, 0.0);
    float side = 0.65 * sin(t * 1.3 + ph) + 0.35 * sin(t * 2.9 + ph * 1.7);
    float lift = 0.3 * sin(t * 3.7 + ph * 2.3);
    return (dir * side + vec3(0.0, 0.0, lift)) * (CX_SWAY_AMP * (1.0 + CX_SWAY_WIND * wind) * reach);
}
