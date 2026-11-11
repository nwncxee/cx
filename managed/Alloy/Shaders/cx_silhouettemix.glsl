#include "cx_silhouettein.glsl"
#include "cx_silhouette.glsl"
/// The colour a lit fragment leaves the stage with: its own, eased toward the look's by the surface's amount.
/// `own` is the share of the creature's reflections and deferred light that stays, eased the same way.
vec3 CxSilhouetteApply(CxSilhouetteSurface s, out float own)
{
    own = 1.0;
    if (s.amount <= 0.0) return s.lit;
    CxSilhouetteLook look = CxSilhouetteShade(s);
    float keep = clamp(look.own, 0.0, 1.0);
    // a whole silhouette is the look itself: the mix leaves each channel its own last bit of the lit one
    if (s.amount >= 1.0) { own = keep; return look.rgb; }
    own = mix(1.0, keep, s.amount);
    return mix(s.lit, look.rgb, s.amount);
}
