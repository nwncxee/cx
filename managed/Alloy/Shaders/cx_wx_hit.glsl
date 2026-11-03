#ifndef CX_WX_HIT
#define CX_WX_HIT
// What a rain drop does where it lands, by the water standing there. On bare wet stone it shatters into a crown of
// droplets thrown wide; into a thin film the crown comes up lower and thinner; into a puddle as deep as the
// puddle-depth setting no crown leaves the water at all -- a ring spreads and a single small jet rebounds from the
// cavity the drop punched. Self-contained: no uniform, no include, so the GPU goldens run it as it ships.

/// Standing water (0 none, 1 the puddle-depth setting) at which the crown has no droplets left.
const float CX_HIT_DROWN = 0.85;
/// The crown droplets' launch speed in the deepest water, as a share of a crown off bare stone.
const float CX_HIT_SLOW = 0.6;
/// The ring's strength on bare stone, as a share of its strength on a puddle: as strong, as a crown's ring always was.
const float CX_HIT_RING_DRY = 1.0;
/// Standing water from which a rebound jet starts to rise, and its strength in the deepest water: one small droplet,
/// well under a crown's.
const vec2 CX_HIT_JET = vec2(0.3, 0.35);

/// A drop's impact into <c>standing</c> water: x the share of a full crown's droplets thrown, y their launch speed as a
/// share of a crown's off bare stone, z the ring's strength, w the rebound jet's.
vec4 CxSplashByWater(float standing)
{
    float s = clamp(standing, 0.0, 1.0);
    float crown = 1.0 - smoothstep(0.0, CX_HIT_DROWN, s);
    float speed = mix(1.0, CX_HIT_SLOW, smoothstep(0.0, 1.0, s));
    return vec4(crown, speed, mix(CX_HIT_RING_DRY, 1.0, s), CX_HIT_JET.y * smoothstep(CX_HIT_JET.x, 1.0, s));
}
#endif
