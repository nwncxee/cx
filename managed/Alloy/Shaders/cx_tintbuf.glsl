// CX-223: the palette tint's SIDE TABLE -- everything the palette-tint character pair needs that the
// per-draw record does not already carry, in a block only this feature reads. The per-draw record is 288
// bytes read by every vertex and every fragment in the world; these terms belong to a handful of materials,
// so they sit here and the record carries only the ROW INDEX, in the feature's own feat.z lane.
//
// A row is per MATERIAL (a .mtr does not animate), so the table is indexed by recipe, not by draw slot. The
// ten palette rows are per WEARER and are NOT here: they are already in CxPltBuf, two per word per draw
// record, off the same source the engine's layered texture hands over (AurTextureSetLayered's row array).
//
// Lane map, which TintTerms writes and its self-check holds against this text:
//   glow  = illumination colour rgb, alpha mip scale in w
//   terms = anisotropy, specular intensity, sway speed, sway strength
//   pal01 = texture7 and texture8 as bindless handles; pal23 = texture9 and texture10
struct CxTint { vec4 glow; vec4 terms; uvec4 pal01; uvec4 pal23; };
layout(std430, binding = 15) readonly buffer CxTintBuf { CxTint uTint[]; };

// Which palette map the COVERAGE comes from: texture9, whose blue channel is the authored coverage -- the
// diffuse's alpha is palette intensity on this material, not an opacity.
#define CX_TINT_COVER_PAL 2
// The first palette slot's bit in the record's slot mask (AlloyRenderer.PalBit0); the other three follow it.
// Declared here rather than beside the other CX_SLOT_ bits because this library is included ahead of them.
#define CX_SLOT_PAL0 512

// This draw's row, or -1 -- for a record that is not a palette-tint one (feat.z means something else there),
// for a table with no row for it, and for a session whose table is the one zero row the upload leaves until
// a palette material exists. The length test is what makes all three DEFINED rather than merely unlikely.
int CxTintRow(int draw)
{
    if (int(uDraw[draw].feat.x) != CX_FEAT_PALETTETINT) return -1;
    int row = int(uDraw[draw].feat.z);
    return row >= 0 && row < uTint.length() ? row : -1;
}

// One of the four palette maps' handle. They live in the row rather than in the per-record handle block:
// four more lanes there would cost every record in the world 32 bytes for a handful of materials.
uvec2 CxTintPalH(int draw, int k)
{
    int row = CxTintRow(draw);
    uvec4 h = k < 2 ? uTint[max(row, 0)].pal01 : uTint[max(row, 0)].pal23;
    return row < 0 ? uvec2(0u) : ((k & 1) == 0 ? h.xy : h.zw);
}
// A macro, not a function returning a sampler: an opaque return type is the one relaxation of the bindless
// extension not every driver takes, and a macro needs none of it.
#ifdef CX_BINDLESS
#define CxTintPal(draw, k) sampler2D(CxTintPalH(draw, k))
#else
// No handle table, and the lit stage has spent units 0..30 -- the palette maps are out of reach. This keeps
// the hosted stage COMPILING; the coverage below reads zero, so such a part draws nothing rather than
// something shaded off whatever texture happened to be on a unit. TintTerms says so in the log.
#define CxTintPal(draw, k) uTex
#endif

// The engine's per-stage `texture<N>Bound` flag for the four palette slots, off the record's slot mask --
// vanilla's own reading: a material that bound nothing there gets nothing sampled.
int CxTintBound(int draw, int k)
{
    return (int(uDraw[draw].pbr.w) & (CX_SLOT_PAL0 << k)) != 0 ? 1 : 0;
}

// The material's own mip compensation for its coverage map, 0 = the plain test.
float CxTintMip(int draw)
{
    int row = CxTintRow(draw);
    return row < 0 ? 0.0 : uTint[row].glow.w;
}

// The illumination colour the tint adds, and the (anisotropy, specular intensity) its lobe takes.
vec3 CxTintGlow(int draw)
{
    int row = CxTintRow(draw);
    return row < 0 ? vec3(0.0) : uTint[row].glow.rgb;
}
vec2 CxTintLobe(int draw)
{
    int row = CxTintRow(draw);
    return row < 0 ? vec2(0.0) : uTint[row].terms.xy;
}

// The coverage this part draws, raw, and the extent the compensation above is measured against.
// Without the handle table there is no texture unit left for the palette maps (the lit stage spends 0..30),
// so the coverage reads ZERO and the part draws nothing -- the stand-down TintTerms says in the log, rather
// than a character shaded off whatever texture happened to be on a unit.
#ifdef CX_BINDLESS
float CxTintCover(int draw, vec2 uv) { return texture(CxTintPal(draw, CX_TINT_COVER_PAL), uv).b; }
vec2 CxTintCoverSize(int draw) { return vec2(textureSize(CxTintPal(draw, CX_TINT_COVER_PAL), 0)); }
#else
float CxTintCover(int draw, vec2 uv) { return 0.0; }
vec2 CxTintCoverSize(int draw) { return vec2(1.0); }
#endif

