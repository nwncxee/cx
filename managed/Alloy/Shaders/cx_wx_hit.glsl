#ifndef CX_WX_HIT
#define CX_WX_HIT
// What a rain drop does where it lands, by the water standing there. On bare wet stone a thin crown of water rises
// round it, flares and sinks back, and a few beads break from its lip; into a thin film the crown comes up lower and
// thinner; into a puddle as deep as the puddle-depth setting no crown rises at all -- a ring spreads and a short,
// thick column rebounds from the dip the drop punched. Self-contained: no uniform, no include, so the GPU goldens
// run it as it ships.

/// Standing water (0 none, 1 the puddle-depth setting) at which the crown has no droplets left.
const float CX_HIT_DROWN = 0.85;
/// The crown's height in the deepest water it still rises from, as a share of a crown off bare stone.
const float CX_HIT_SLOW = 0.6;
/// The ring's strength on bare stone, as a share of its strength on a puddle: as strong, as a crown's ring always was.
const float CX_HIT_RING_DRY = 1.0;
/// Standing water from which a rebound column starts to rise, and the share of drops that throw one in the deepest water.
const vec2 CX_HIT_JET = vec2(0.3, 0.35);

/// A drop's impact into <c>standing</c> water: x the crown's strength (its sheet and its beads), y its height as a share
/// of a crown's off bare stone, z the ring's strength, w the share of drops that throw a rebound column.
vec4 CxSplashByWater(float standing)
{
    float s = clamp(standing, 0.0, 1.0);
    float crown = 1.0 - smoothstep(0.0, CX_HIT_DROWN, s);
    float speed = mix(1.0, CX_HIT_SLOW, smoothstep(0.0, 1.0, s));
    return vec4(crown, speed, mix(CX_HIT_RING_DRY, 1.0, s), CX_HIT_JET.y * smoothstep(CX_HIT_JET.x, 1.0, s));
}

/// Gravity, m/s².
const float CX_HIT_G = 9.81;
/// The impact's parts, as the splash's vertex stage hands them to its fragment stage: the ring, the crown's sheet,
/// a puddle's rebound column, a bead off the lip.
const int CX_PART_RING = 0, CX_PART_SHEET = 1, CX_PART_COLUMN = 2, CX_PART_BEAD = 3;
/// /weather viz 5: every impact unlit, green on bare ground through blue in standing water, as it read the water.
const int CX_SPLASH_VIZ = 5;
/// /weather viz 6: one dot per rain crown, whether it draws or not, coloured by what became of it.
const int CX_SPLASH_FATE_VIZ = 6;

/// The tallest a full crown rises off bare stone, metres.
const float CX_CROWN_H = 0.02;
/// A full crown's radius at its lip as it starts, metres, and how far the lip has spread by its life's end.
const float CX_CROWN_R = 0.01;
const float CX_CROWN_SPREAD = 2.5;

/// A full crown at <c>age</c> (0 the drop's arrival, 1 its life's end): x its height, y its radius at the lip,
/// metres. It shoots up, then sinks back slower as it spreads, and is gone at the end.
vec2 CxCrownShape(float age)
{
    float a = sqrt(clamp(age, 0.0, 1.0));
    return vec2(CX_CROWN_H * max(sin(3.14159265 * a), 0.0), CX_CROWN_R * (1.0 + (CX_CROWN_SPREAD - 1.0) * a));
}

/// The rebound column in a puddle at its tallest, and its width, metres.
const vec2 CX_COLUMN = vec2(0.012, 0.004);
/// The share of the life the drop's dip takes to open and close before the column rises out of it.
const float CX_COLUMN_WAIT = 0.2;

/// The rebound column at <c>age</c>: x its height, y its width, metres.
vec2 CxSplashColumn(float age)
{
    float a = clamp((age - CX_COLUMN_WAIT) / (1.0 - CX_COLUMN_WAIT), 0.0, 1.0);
    return vec2(CX_COLUMN.x * max(sin(3.14159265 * a), 0.0), CX_COLUMN.y);
}

/// The share of a crown's life at which its lip beads break free.
const float CX_BEAD_BREAK = 0.25;
/// A lip bead's launch speed, m/s, slowest and fastest, and its tilt above level, radians, lowest and steepest.
const vec2 CX_BEAD_V = vec2(0.3, 0.7);
const vec2 CX_BEAD_TILT = vec2(0.4, 0.9);

/// A bead <c>t</c> seconds after it left a lip at <c>rim</c> (x its radius, y its height, metres), launched at <c>v</c>
/// m/s tilted <c>tilt</c> above level: x how far out from where the drop hit, y its height; below 0 it has landed.
vec2 CxBeadFlight(float t, float v, float tilt, vec2 rim)
{
    return vec2(rim.x + v * cos(tilt) * t, rim.y + v * sin(tilt) * t - 0.5 * CX_HIT_G * t * t);
}

/// A lip bead's diameter, metres.
const float CX_BEAD_M = 0.003;
/// Screen width a bead is never drawn narrower than, pixels: a smaller quad flickers as the rasteriser catches and misses it.
const float CX_BEAD_MINPX = 1.2;
/// Seconds of flight a bead smears over: short of a camera's shutter, so it reads as a bead and never as a line.
const float CX_BEAD_SMEAR_S = 0.004;

/// A bead's extent at <c>pxPerM</c> pixels per metre, flying at <c>speed</c> m/s: x along its flight, y across, at
/// least a pixel each, and never more than twice as long as it is wide.
vec2 CxSplashBeadSize(float speed, float pxPerM)
{
    float floorM = CX_BEAD_MINPX / max(pxPerM, 1e-3);
    float w = max(CX_BEAD_M, floorM);
    return vec2(min(max(CX_BEAD_M + speed * CX_BEAD_SMEAR_S, floorM), 2.0 * w), w);
}
#endif
