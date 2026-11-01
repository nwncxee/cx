#ifndef CX_WX_LOOK
#define CX_WX_LOOK
// What standing water and soaking look like on THIS surface: how glossy the water stands on it, how much of the
// rain it drinks and where in its own relief the water collects. Pure functions of the material, no uniform and
// no include beyond CX_DIELECTRIC_F0 (cx_ssr), so the GPU goldens run them as they ship. Where the material's maps say how smooth it is, they
// decide; where nothing does, the albedo's brightness stands in: light stone polishes, dark cobble drinks.

/// Linear albedo luminance a surface of unknown smoothness reads as fully dull (x) and as its glossiest (y).
const vec2 CX_WX_GUESS_LUM = vec2(0.02, 0.6);
/// The glossiest a surface of unknown smoothness is taken to be: water on it is never quite a polished mirror.
const float CX_WX_GUESS_MAX = 0.8;
/// Roughness at which a surface its maps describe stops taking a mirror film (x) and takes only a sheen (y).
const vec2 CX_WX_KNOWN_ROUGH = vec2(0.25, 0.7);
/// The film's alpha on the dullest surface: wide enough that a lamp spreads into a glow and is no image.
const float CX_WX_SHEEN_ALPHA = 0.3;
/// The share of a full film's weight the dullest surface keeps: a sheen, still plainly wet.
const float CX_WX_SHEEN_W = 0.3;
/// The noise micro-relief's swing about its mean, as a share of the puddle-depth knob: wider than the
/// depth, so a full basin on level ground pools in pockets instead of one sheet.
const float CX_WX_MICRO_AMP = 2.0;
/// ...and its mean at the puddles knob's midpoint (the knob scales it 2..0): what distant ground, where the
/// noise mips to its mean, stands above the water; near, a full basin films where the noise falls under it.
const float CX_WX_MICRO_MEAN = 0.8;
/// The gradient scale a surface of no height map reads its neighbourhood's brightness at: three mips up.
const float CX_WX_NEAR_GRAD = 8.0;
/// The share of the micro-relief the fine relief owns: the height map, the albedo's own relief or the fine noise.
const float CX_WX_FINE_W = 0.65;

/// How glossy water stands on this surface, 0 a sheen to 1 a mirror: from its roughness where its maps or its
/// declaration say how smooth it is (<c>known</c>), from its linear albedo luminance where nothing does.
float CxWxGloss(float lum, float rough, bool known)
{
    if (known) return 1.0 - smoothstep(CX_WX_KNOWN_ROUGH.x, CX_WX_KNOWN_ROUGH.y, rough);
    return CX_WX_GUESS_MAX * smoothstep(CX_WX_GUESS_LUM.x, CX_WX_GUESS_LUM.y, lum);
}

/// How much of the rain soaks in, 0..1 (Lagarde, "Water drop 2"). Known: rough, non-metal and low specularity
/// read as porous. Unknown: the stock parameters say nothing, so the dull guess drinks and the glossy one sheds.
float CxWxPoroOf(float rough, float spec, float metal, float lum, bool known)
{
    if (!known) return (1.0 - metal) * mix(1.0, 0.45, smoothstep(CX_WX_GUESS_LUM.x, CX_WX_GUESS_LUM.y, lum));
    return (1.0 - metal) * smoothstep(0.25, 0.7, rough) * (1.0 - clamp((spec - CX_DIELECTRIC_F0) / 0.2, 0.0, 1.0));
}

/// The film over this surface: x its coat weight, y its alpha (the knob's alpha at its glossiest), z how far it
/// flattens the shading normal toward the water plane. A dull surface takes a thin, rough film its own relief shows through.
vec3 CxWxFilmLook(float film, float lum, float rough, bool known, float knobAlpha)
{
    float g = CxWxGloss(lum, rough, known);
    float a = mix(max(CX_WX_SHEEN_ALPHA, knobAlpha), knobAlpha, g);
    return vec3(film * mix(CX_WX_SHEEN_W, 1.0, g), a, film * g);
}

/// The fine relief, 0 low to 1 high, a surface of no height map reads off its albedo: darker than its
/// neighbourhood (<c>lumNear</c>, a coarse mip of the same texture) is lower -- a joint, a crack, a rut.
float CxWxFineLum(float lum, float lumNear)
{
    return 0.5 + 0.5 * clamp((lum - lumNear) / max(lumNear, 1e-3), -1.0, 1.0);
}

/// Metres this point stands above its basin's low point from the micro-relief alone: <c>coarse</c> the broad
/// noise octave, <c>fine</c> the fine relief (both 0..1); porous ground drinks its shallow pockets, so counts double.
float CxWxMicro(float depth, float puddles, float coarse, float fine, float poro)
{
    float swing = (1.0 - CX_WX_FINE_W) * coarse + CX_WX_FINE_W * fine - 0.5;
    return depth * (CX_WX_MICRO_MEAN * 2.0 * (1.0 - puddles) + CX_WX_MICRO_AMP * swing) * (1.0 + poro);
}
#endif
