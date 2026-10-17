// CX-303: the CONTENT TABLE -- what a content pair's stages need of their material and nothing else in the world reads:
// the .mtr's own uniform values (one lane per uniform the stage declares, ContentStage) and its texture7..texture10 as
// bindless handles. A row is per MATERIAL, or per OBJECT for a row carrying that object's own SetMaterialShaderUniform
// values, so the per-draw record carries only the row index, in feat.z.
//
// Lane map, which ContentTable writes and its self-check holds against this text:
//   pal01 = texture7 and texture8 as bindless handles; pal23 = texture9 and texture10
//   cpOn  = which content lanes the material states (bit n = lane n), in x
//   cp    = the stage's parameters, one vec4 lane per uniform the table feeds
#define CX_CP_LANES 12
struct CxConRow { uvec4 pal01; uvec4 pal23; uvec4 cpOn; vec4 cp[CX_CP_LANES]; };
layout(std430, binding = 15) readonly buffer CxContentBuf { CxConRow uConTab[]; };

// The first palette slot's bit in the record's slot mask (AlloyRenderer.PalBit0); the other three follow it.
// Declared here rather than beside the other CX_SLOT_ bits because this library is included ahead of them.
#define CX_SLOT_PAL0 512

// This draw's row, or -1 -- for a record that is not a content pair's (feat.z means something else there), for a
// table with no row for it, and for a session whose table is the one zero row the upload leaves until a content
// material exists. The length test is what makes all three DEFINED rather than merely unlikely.
int CxContentRow(int draw)
{
    if (int(uDraw[draw].feat.x) < CX_FEAT_CONTENT0) return -1;
    int row = int(uDraw[draw].feat.z);
    return row >= 0 && row < uConTab.length() ? row : -1;
}

// This vertex's or fragment's row, resolved once by the content body's wrapper. A global as well as what the
// accessors read, because the engine-named macros a content body reads (texUnit7, a parameter) expand inside that
// body, which has no local of the stage's to reach.
int cxConRow = -1;

// Does the material state content lane l? A lane it does not state reads the uniform's own GLSL default.
bool CxCpOn(int l) { return cxConRow >= 0 && (uConTab[cxConRow].cpOn.x & (1u << uint(l))) != 0u; }
vec4 CxCp(int l) { return cxConRow >= 0 ? uConTab[cxConRow].cp[clamp(l, 0, CX_CP_LANES - 1)] : vec4(0.0); }

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
#else
// No handle table, and the lit stage has spent units 0..30 -- the palette maps are out of reach. This keeps the
// stage COMPILING, sampling the diffuse; ContentTable says so in the log.
#define CxConPal(k) uTex
#endif

// The engine's per-stage `texture<N>Bound` flag for the four palette slots, off the record's slot mask -- vanilla's
// own reading: a material that bound nothing there gets nothing sampled.
int CxConPalBound(int draw, int k) { return (int(uDraw[draw].pbr.w) & (CX_SLOT_PAL0 << k)) != 0 ? 1 : 0; }
