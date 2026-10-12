// CX-117: PLT resolved in the shader, not composited per creature.
//
// A PLT texel is a pair -- intensity in R, layer index in G -- uploaded as RG8 once per PLT file, however
// many creatures wear it. The colour is a lookup into the master palette (one 256x1792 RGBA8 texture, the
// engine's own `masterPalette`): colour = master[row[layer]][intensity], where row[layer] is this draw's
// scheme integer, a global row carried per record in CxPltBuf. That is exactly ImageDecode @0x4b6580's
// `palette[(rows[layer] << 10) + intensity*4]`, one texel at a time.
//
// Filtering is done HERE, over resolved colours, because the pair texture must not be filtered: the mean of
// two layer indices is a third layer, and the mean of two intensities across a layer boundary reads out of
// the wrong palette row. So the pair texture is sampled NEAREST and this does the bilinear (and the mip
// blend) itself -- the same arithmetic a filtered composite would have performed, with no composite.
layout(binding = 19) uniform sampler2D uPltPal;
layout(std430, binding = 7) readonly buffer CxPltBuf { uint uPltRow[]; };

#define CX_PLT_WORDS    6   // five row words (two 16-bit rows each) + one flag word
#define CX_PLT_ROWWORDS 5
#define CX_PLT_LAYERS   10
#define CX_PLT_FLAG     1u  // this draw's diffuse is a PLT
#define CX_PLT_CLAMP    2u  // ...and its sampler clamps rather than repeats
// CX-223: the rows are staged for a reader that is NOT the diffuse resolve -- a palette-tint material whose
// diffuse is an ordinary texture still wears the ten rows, and reads them as the engine's PLTscheme feed.
#define CX_PLT_ROWS     4u
// Past this LOD the second mip level is never blended in: those levels are a few texels wide, their colours
// are within a rounding of each other, and a creature that far away is a handful of pixels. Four taps instead
// of eight, and no dither and no temporal term, so it holds with TAA off.
#define CX_PLT_LOD_CUT  4.0

// Does this draw's diffuse resolve as a PLT? One scalar load, uniform across the primitive.
// The length() test is what makes a session with no PLT record defined rather than merely lucky: the block is
// then bound to a buffer of exactly CX_PLT_WORDS zero words, so every record past the first is out of range
// and the first reads a zero flag word. Without it an unbound binding is an undefined read, and one stray bit
// would resolve
// a wall through the palette.
bool CxPltOn(int draw)
{
    int i = draw * CX_PLT_WORDS + CX_PLT_ROWWORDS;
    return i < uPltRow.length() && (uPltRow[i] & CX_PLT_FLAG) != 0u;
}

// Does this draw's PLT clamp rather than repeat? A sampler's wrap mode is not visible to the shader and this
// does its own addressing, so the registry states it in the flag word.
bool CxPltClamps(int draw)
{
    return (uPltRow[draw * CX_PLT_WORDS + CX_PLT_ROWWORDS] & CX_PLT_CLAMP) != 0u;
}

// Are this draw's ROWS staged at all -- either because its diffuse resolves as a PLT, or because something
// else on the record reads the wearer's scheme? One scalar load, uniform across the primitive.
bool CxPltRowsOn(int draw)
{
    int i = draw * CX_PLT_WORDS + CX_PLT_ROWWORDS;
    return i < uPltRow.length() && (uPltRow[i] & (CX_PLT_FLAG | CX_PLT_ROWS)) != 0u;
}

// This draw's master-palette row for one layer.
int CxPltRow(int draw, int layer)
{
    uint v = uPltRow[draw * CX_PLT_WORDS + (layer >> 1)];
    return int((layer & 1) == 0 ? (v & 0xffffu) : (v >> 16));
}

// The engine's own PLT feed to a CUSTOM shader: `uniform float PLTscheme[15]`, which it sets per material
// bind as row / 1792 -- a normalised V coordinate into the master palette, one per layer. The engine has a
// uniform to set because it draws one material at a time; Alloy draws a multidraw batch, so the same number
// comes out of this record's rows instead and the bridge rewrites every `PLTscheme[n]` into this call.
// The literal 1792 and the absence of a half-texel offset are Material::ReplaceTexturePLT's, not the GPU
// compositor's (which adds 0.5 and divides by the live palette height).
#define CX_PLT_HEIGHT 1792.0
float CxPltScheme(int draw, int layer)
{
    return CxPltRowsOn(draw) && layer >= 0 && layer < CX_PLT_LAYERS
         ? float(CxPltRow(draw, layer)) / CX_PLT_HEIGHT : 0.0;
}

// One pair texel resolved to a colour, at an explicit level, under the texture's own wrap mode.
vec4 CxPltFetch(sampler2D plt, ivec2 p, ivec2 dim, int level, int draw, bool clampWrap)
{
    p = clampWrap ? clamp(p, ivec2(0), dim - 1)
                  : p - dim * ivec2(floor(vec2(p) / vec2(dim)));
    vec2 rg = texelFetch(plt, p, level).rg;
    int layer = min(int(rg.g * 255.0 + 0.5), CX_PLT_LAYERS - 1);
    int inten = int(rg.r * 255.0 + 0.5);
    return texelFetch(uPltPal, ivec2(inten, CxPltRow(draw, layer)), 0);
}

// Bilinear over four resolved colours at one level.
vec4 CxPltBilinear(sampler2D plt, vec2 uv, int level, int draw, bool clampWrap)
{
    ivec2 dim = max(textureSize(plt, level), ivec2(1));
    vec2 t = uv * vec2(dim) - 0.5;
    vec2 f = fract(t);
    ivec2 p = ivec2(floor(t));
    vec4 c00 = CxPltFetch(plt, p + ivec2(0, 0), dim, level, draw, clampWrap);
    vec4 c10 = CxPltFetch(plt, p + ivec2(1, 0), dim, level, draw, clampWrap);
    vec4 c01 = CxPltFetch(plt, p + ivec2(0, 1), dim, level, draw, clampWrap);
    vec4 c11 = CxPltFetch(plt, p + ivec2(1, 1), dim, level, draw, clampWrap);
    return mix(mix(c00, c10, f.x), mix(c01, c11, f.x), f.y);
}

// The diffuse a PLT part draws: trilinear over resolved colours. The LOD is the one the hardware would have
// picked for this texture (textureQueryLod), plus whatever bias the pass is running. The second level is
// skipped where it would contribute nothing -- a coherent branch, so it costs the wave nothing when taken --
// and past CX_PLT_LOD_CUT it is never taken at all.
vec4 CxPltResolve(sampler2D plt, vec2 uv, float bias, int draw)
{
    bool clampWrap = CxPltClamps(draw);
    float top = float(max(textureQueryLevels(plt) - 1, 0));
    float lod = clamp(textureQueryLod(plt, uv).y + bias, 0.0, top);
    int lo = int(floor(lod));
    float f = lod - float(lo);
    vec4 c = CxPltBilinear(plt, uv, lo, draw, clampWrap);
    if (f > 0.004 && lod < CX_PLT_LOD_CUT && float(lo) < top)
        c = mix(c, CxPltBilinear(plt, uv, lo + 1, draw, clampWrap), f);
    return c;
}

// The diffuse read every lit stage performs: a PLT draw resolves, everything else samples. One scalar load
// decides, so a non-PLT frame pays a buffer read per fragment and nothing else.
// A stage that reads the diffuse more than once per fragment asks CxPltOn ONCE and reuses the resolve
// (cx_ownlit_synth does): a PLT resolve is up to eight texel fetches, and three of them per fragment is a
// bill no creature should pay.
vec4 CxDiffuse(sampler2D t, vec2 uv, float bias, int draw)
{
    return CxPltOn(draw) ? CxPltResolve(t, uv, bias, draw) : texture(t, uv, bias);
}
