// CX-303: the CONTENT TABLE -- what a content pair's stages need of their material and nothing else in the world reads:
// the .mtr's own uniform values (one lane per uniform the stage declares, ContentStage) and its texture7..texture10 as
// bindless handles. A row is per MATERIAL, or per OBJECT for a row carrying that object's own SetMaterialShaderUniform
// values, so the per-draw record carries only the row index, in feat.z.
//
// Lane map, which ContentTable writes and its self-check holds against this text:
//   pal01 = texture7 and texture8 as bindless handles; pal23 = texture9 and texture10
//   cp    = x: the row's first uConLanes entry, y: its lane count, z: the entry of its lane 0
// A row's span in uConLanes: ceil(count / 128) on-mask entries (bit n = lane n), then one entry per lane, float bits.
#define CX_CON_ROWS 1024
#define CX_CP_MASK 128
struct CxConRow { uvec4 pal01; uvec4 pal23; uvec4 cp; };
layout(std430, binding = 15) readonly buffer CxContentBuf { CxConRow uConTab[CX_CON_ROWS]; uvec4 uConLanes[]; };

// The first palette slot's bit in the record's slot mask (AlloyRenderer.PalBit0); the other three follow it.
// Declared here rather than beside the other CX_SLOT_ bits because this library is included ahead of them.
#define CX_SLOT_PAL0 512

// This draw's row, or -1 -- for a record that is not a content pair's (feat.z means something else there), for a
// table with no row for it, and for an index past the header array. The bound test is what makes these DEFINED; the
// bind always covers the headers, so uConLanes.length() is the pool's real extent.
int CxContentRow(int draw)
{
    if (int(uDraw[draw].feat.x) < CX_FEAT_CONTENT0) return -1;
    int row = int(uDraw[draw].feat.z);
    return row >= 0 && row < CX_CON_ROWS ? row : -1;
}

// This vertex's or fragment's row, resolved once by the content body's wrapper. A global as well as what the
// accessors read, because the engine-named macros a content body reads (texUnit7, a parameter) expand inside that
// body, which has no local of the stage's to reach.
int cxConRow = -1;

// Does the material state content lane l? A lane it does not state reads the uniform's own GLSL default.
// A lane past the row's count, or a span past the pool, is off.
bool CxCpOn(int l)
{
    if (cxConRow < 0 || l < 0 || uint(l) >= uConTab[cxConRow].cp.y) return false;
    int at = int(uConTab[cxConRow].cp.x) + l / CX_CP_MASK;
    return at < uConLanes.length() && (uConLanes[at][(l / 32) & 3] & (1u << uint(l & 31))) != 0u;
}
vec4 CxCp(int l)
{
    if (cxConRow < 0 || l < 0 || uint(l) >= uConTab[cxConRow].cp.y) return vec4(0.0);
    int at = int(uConTab[cxConRow].cp.z) + l;
    return at < uConLanes.length() ? uintBitsToFloat(uConLanes[at]) : vec4(0.0);
}

// One of the four palette maps' handle. They live in the row rather than in the per-record handle block: four more
// lanes there would cost every record in the world 32 bytes for a handful of materials. A macro, not a function
// returning a sampler: an opaque return type is the one relaxation of the bindless extension not every driver
// takes, and a macro needs none of it.
#ifdef CX_BINDLESS
uvec2 CxConPalH(int draw, int k)
{
    // a null handle is a GPU fault on AMD's driver, so a record with no row of its own falls back to its DIFFUSE's
    // handle, which that table guarantees is non-null; the bound flag below asks the slot mask
    if (cxConRow < 0) return uTexH[draw << 3];
    uvec4 h = k < 2 ? uConTab[cxConRow].pal01 : uConTab[cxConRow].pal23;
    return (k & 1) == 0 ? h.xy : h.zw;
}
#define CxConPal(k) sampler2D(CxConPalH(vDraw, k))
#elif defined(CX_GUI_TWIN)
// A GUI draw binds its maps by name per draw, so each palette map has a sampler of its own there.
#define CxConPal(k) uGuiPal##k
#endif

// The engine's per-stage `texture<N>Bound` flag for the four palette slots, off the record's slot mask -- vanilla's
// own reading: a material that bound nothing there gets nothing sampled.
int CxConPalBound(int draw, int k) { return (int(uDraw[draw].pbr.w) & (CX_SLOT_PAL0 << k)) != 0 ? 1 : 0; }
