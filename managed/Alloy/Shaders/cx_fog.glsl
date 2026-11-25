#include "cx_fogmix.glsl"
/// Outdoor distance haze (fog.haze): how much of a surface survives the air between it and the eye. 1 = no haze.
float CxHazeT(vec3 world)
{
    return uFogHaze.x > 0.0 ? exp(-distance(world, uEyePos) * uFogHaze.x) : 1.0;
}
/// The haze over a lit LINEAR colour: toward the sky's colour with distance, brighter looking toward the sun
/// (a Henyey-Greenstein lobe, 1 at g 0). Applied under the author's fog, so the area's own fog still wins at its end.
vec3 CxHaze(vec3 lin, vec3 world)
{
    if (uFogHaze.x <= 0.0) return lin;
    vec3 v = world - uEyePos;
    float d = length(v);
    float t = exp(-d * uFogHaze.x);
    float g = uFogHaze.z;
    float mu = dot(v / max(d, 1e-4), uFogSun.xyz);
    float lobe = (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * mu, 1e-4), 1.5);
    return lin * t + uFogHazeColor * (1.0 + uFogHaze.y * lobe) * (1.0 - t);
}
/// The area author's own ramp, times the height layer: how fogged this fragment is with no volume to read.
float CxFogRamp(vec3 world, float depth)
{
    if (uFogParams.x == 0.0) return 0.0;
    // radial from the player (uFogOrigin.w == 1), so zooming the camera out does not move the fog;
    // else the engine's own view depth. CullPass measures the same way, or the cut shows.
    float d = uFogOrigin.w > 0.0 ? distance(world, uFogOrigin.xyz) : depth;
    float f = (d - uFogParams.y) * uFogParams.w;
    if (f <= 0.0) return 0.0;
    // #341: fog as a LAYER, thinning above uFogSky.z. Off (uFogSky.y == 0) is the boot default and
    // the author's ramp alone -- this is the one term here that changes density rather than colour.
    if (uFogSky.y > 0.0) f *= exp(-max(world.z - uFogSky.z, 0.0) * uFogSky.y);
    return min(f, 1.0);
}
/// How fogged this fragment is, 0..1: the ramp, the volume's own share where this pass reads the whole fog, or
/// the two together where the volume carries the rain veil alone.
float CxFogFactor(vec3 world, float depth)
{
    // the volume's transmittance, where this pass reads it: the share of the surface the fog takes
    if (CxVFogWhole()) return 1.0 - CxVFogAt(world).a;
    float f = CxFogRamp(world, depth);
    return CxVFogOn() ? 1.0 - (1.0 - f) * CxVFogAt(world).a : f;
}
vec3 CxFog(vec3 lin, vec3 world, float depth)
{
    lin = CxHaze(lin, world);   // the air first; the author's fog lies over it
    if (!CxVFogOn()) return CxFogMix(lin, world, CxFogFactor(world, depth));
    vec4 v = CxVFogAt(world);
    // the whole fog, or the rain veil over what the area's own ramp already did here
    return (CxVFogWhole() ? lin : CxFogMix(lin, world, CxFogRamp(world, depth))) * v.a + v.rgb;
}

/// CX-120: the same ramp under FOG OF WAR. An unexplored tile is fogged toward black rather than
/// faded out -- vanilla's own treatment (RenderDrawBucket @0x46d060 scales the fog colour by f^2 and
/// the fog end by f), which is why this is a fog term and the tile's alpha is left alone.
/// The lane carries HIDDEN (1 - reveal), not reveal, and that polarity is load-bearing: the record's
/// spare lane is zero in every record nobody writes -- every record before its first fog-of-war write --
/// and zero has to mean EXPLORED. Carrying reveal instead fogged the
/// entire world to black the moment this shipped, because an unwritten 0 read as "never seen".
///   hidden 0   = explored: exactly CxFogFactor, and the area author's colour.
///   hidden > 0 = the ramp is pulled in to (reveal*start, reveal*end + 0.1) and the target darkens by
///                reveal^2, so an unseen tile reads black at the far end instead of sky-coloured haze.
/// The ramp applies even where the area declares NO fog: an unexplored tile is hidden whatever the
/// author's ramp says, which is what the engine does with it.
float CxFogRevealFactor(vec3 world, float depth, float hidden)
{
    float reveal = 1.0 - hidden;
    if (hidden <= 0.0) return CxFogFactor(world, depth);
    float d = uFogOrigin.w > 0.0 ? distance(world, uFogOrigin.xyz) : depth;
    float start = reveal * uFogParams.y;
    float end = reveal * uFogParams.z + 0.1;
    float f = (d - start) / max(end - start, 1e-4);
    return clamp(max(f, 0.0), 0.0, 1.0);
}

vec3 CxFogReveal(vec3 lin, vec3 world, float depth, float hidden)
{
    if (hidden <= 0.0 && CxVFogOn()) return CxFog(lin, world, depth);
    if (hidden <= 0.0) lin = CxHaze(lin, world);   // explored only: an unseen tile stays the author's black
    float reveal = 1.0 - hidden;
    float f = CxFogRevealFactor(world, depth, hidden);
    if (f <= 0.0) return lin;
    if (hidden <= 0.0) return CxFogMix(lin, world, f);
    // The target, darkened by reveal^2 and taken from the author's fog colour rather than the sky: an
    // unexplored tile has no horizon to match, so the sky and sun terms have nothing to contribute.
    vec3 g = vec3(1.0 / $SRGB$);
    vec3 target = uFogColor.rgb * (reveal * reveal);
    vec3 disp = mix(pow(max(lin, vec3(0.0)), g), pow(max(target, vec3(0.0)), g), f);
    return pow(max(disp, vec3(0.0)), vec3($SRGB$));
}
