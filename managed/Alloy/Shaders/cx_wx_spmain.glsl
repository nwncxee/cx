// CX-102: the splash. One instanced quad per crown part, placed where the rain actually lands -- the cover
// map is an orthographic depth view along the fall vector, so its depth over a point IS the first surface
// the rain reaches there: a roof, a ledge, a wall top, the ground, and never anything under shelter. The
// owner's complaint was that impacts only showed where a puddle happened to be reflecting a light; this
// draws them wherever rain falls, lit like the drops and independent of any reflection.
// No CPU work per splash and no buffer: the instance id hashes to a point on a camera-centred disc and to
// a time slice, and each slice re-draws the point, so the field never strobes in fixed spots.

/// Beads off one crown's lip. The last is dropped on its own hash, so a crown throws three or four.
const int CX_SPLASH_BEADS = 4;
/// Parts per crown: the ring on the ground (index 0), the sheet or the rebound column (1), then the beads. The C#
/// side sizes the draw by this.
const int CX_SPLASH_PARTS = CX_SPLASH_BEADS + 2;
/// The ring's own reach, metres. Pinned to the puddle rings' CX_RIPPLE_REACH_M -- the same drop makes both,
/// so the one on the water and the one on the stone beside it may not be different sizes (Weather.SelfCheck).
const float CX_SPLASH_RING_M = 0.08;
/// Share of the area ambient a crown takes with the sun straight behind it: the floor that keeps an impact
/// readable in dim light. Premultiplied scatter means it can only ADD -- where the area ambient is nothing
/// so is the splash, so this never becomes a grey film in the dark.
const float CX_SPLASH_AMB = 0.3;
/// Metres the ring quad stands off the surface under it, against z-fighting with that surface.
const float CX_SPLASH_LIFT = 0.02;
/// The ring's crest, as a share of its reach: what the ring's own footprint fade is measured against, and
/// what cx_splash_f draws it as.
const float CX_SPLASH_CREST = 0.3;
/// Crest widths on screen: under the first the ring is gone, past the second it is at full strength. The
/// ring's WORLD size never moves with distance -- this only fades it, which is the whole point of CX-102.
const vec2 CX_SPLASH_PX = vec2(1.0, 2.5);
/// Screen size the crown's quad is never drawn smaller than, in pixels. A crown a couple of centimetres tall is under
/// a pixel past a few tens of metres, and a sub-pixel quad does not dim -- it FLICKERS; the quad grows to this and the
/// crown's light is divided by the area that added. The ring never moves.
const float CX_SPLASH_MINPX = 1.2;
/// View depths, metres, over which a crown fades in from the lens: nearer than the first it is not drawn. A
/// world-sized crown that close is a screen-wide smear, which is what a camera inside a wall sees.
const vec2 CX_SPLASH_NEAR = vec2(0.5, 1.0);
/// Metres the landing point may stand off the plane through its four cover-texel neighbours before it is an
/// edge and draws nothing. A plane's filtered depth is exact, so this only has to clear depth precision.
const float CX_SPLASH_FLAT_M = 0.02;
/// The steepest surface a crown stands on, as the normal's z: about 80 degrees. Past it the fall grazes the
/// face, and the cover map sees it edge-on.
const float CX_SPLASH_STEEP = 0.17;
/// How far past a landing point the frame may show the scene before the point hangs in the air: metres, plus a
/// share of the view depth for the spread a grazing view puts across one pixel.
const vec2 CX_SPLASH_CONTACT = vec2(0.25, 0.02);
/// Crowns one footfall throws, scattered over the sole (Weather.FootCrowns).
const int CX_FOOT_CROWNS = 4;
/// A foot crown's life and the spread of its births, seconds (Weather.FootLifeSec / FootStaggerSec).
const float CX_FOOT_LIFE = 0.3;
const float CX_FOOT_STAGGER = 0.06;
/// Metres over which a footfall's crowns scatter about the foot.
const float CX_FOOT_SPREAD = 0.12;
/// Metres the cover map's hit under a footfall may sit from the foot and still be the ground it stands on;
/// farther, it is a roof over the foot, and the crown keeps world up.
const float CX_FOOT_GROUND_M = 0.3;
/// A footfall crown's size, as a share of a rain crown's: damp ground (x) flicks, standing water (y) throws.
const vec2 CX_FOOT_V0 = vec2(0.9, 1.6);
/// Ring reach, as a share of a rain drop's: a foot displaces far more water.
const vec2 CX_FOOT_RING = vec2(1.5, 3.0);
/// Wetness at the foot from which a crown starts to show, and at which it is full.
const vec2 CX_FOOT_WET = vec2(0.15, 0.6);

vec3 CxCoverWorld(vec2 uv, float d)
{
    vec4 w = uCoverVpInv * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
    return w.xyz / w.w;
}

vec3 CxCoverAt(vec2 uv) { return CxCoverWorld(uv, texture(uCoverDepth, uv).r); }

/// Where the rain lands over `xy`, and the normal of the surface it lands on: the cover view's first hit
/// along the fall. Two passes, the second a Newton step -- the first projection has to guess the height and
/// the reconstruction knows it, so re-projecting at the real Z takes out the lateral error the fall's lean
/// caused (the map scatter's own trick). `ok` false = no map, outside its fit, nothing stood in that ray,
/// or the hit is no plane a crown can stand on (an edge, or steeper than CX_SPLASH_STEEP).
vec3 CxSplashLand(vec2 xy, out vec3 up, out bool ok)
{
    ok = false;
    up = vec3(0.0, 0.0, 1.0);
    vec3 p = vec3(xy, uSplashC.z);
    if (uCoverVp[3][3] == 0.0) return p;
    vec2 uv;
    for (int i = 0; i < 2; i++)
    {
        vec4 c = uCoverVp * vec4(xy, p.z, 1.0);
        vec3 n = c.xyz / c.w;
        if (any(greaterThan(abs(n.xy), vec2(1.0)))) return p;
        uv = n.xy * 0.5 + 0.5;
        float d = texture(uCoverDepth, uv).r;
        if (d >= 1.0) return p;
        p = CxCoverWorld(uv, d);
    }
    // the filtered depth is exact on a plane and a blend across an edge: off the plane its four neighbours
    // span, the point hangs between a roof and the ground under it
    vec2 e = 1.0 / vec2(textureSize(uCoverDepth, 0));
    vec3 xp = CxCoverAt(uv + vec2(e.x, 0.0)), xn = CxCoverAt(uv - vec2(e.x, 0.0));
    vec3 yp = CxCoverAt(uv + vec2(0.0, e.y)), yn = CxCoverAt(uv - vec2(0.0, e.y));
    if (length(xp + xn - 2.0 * p) + length(yp + yn - 2.0 * p) > CX_SPLASH_FLAT_M) return p;
    up = normalize(cross(xp - xn, yp - yn));
    if (up.z < 0.0) up = -up;
    ok = up.z >= CX_SPLASH_STEEP;
    return p;
}

#include "cx_depthlin.glsl"

/// False when the frame shows the scene well past `p`: the cover map has a surface there the frame never drew
/// (a wall the keyhole dissolved) and a crown on it would hang in the air. Off screen there is nothing to
/// test, and a point something nearer hides is the depth test's to clip.
bool CxSplashSeen(vec3 p)
{
    vec4 v = uView * vec4(p, 1.0);
    vec4 c = uProj * v;
    if (c.w <= 0.0 || any(greaterThan(abs(c.xy), vec2(c.w)))) return true;
    float ndc = texture(uSceneDepth, c.xy / c.w * 0.5 + 0.5).r;
    float scene = CxDepthViewZ(ndc, uProj);
    return scene + v.z < CX_SPLASH_CONTACT.x - CX_SPLASH_CONTACT.y * v.z;
}

/// The water standing where a crown lands at `p`, as the deferred twin recorded it under that pixel; 0 off screen or
/// when this frame recorded none.
float CxSplashStanding(vec3 p)
{
    if (uStandOn == 0) return 0.0;
    vec4 c = uProj * (uView * vec4(p, 1.0));
    if (c.w <= 0.0 || any(greaterThan(abs(c.xy), vec2(c.w)))) return 0.0;
    return textureLod(uWxStand, c.xy / c.w * 0.5 + 0.5, 0.0).r;
}

void main()
{
    uint id = uint(gl_InstanceID);
    bool foot = id >= uint(uSplashRain);
    if (foot) id -= uint(uSplashRain);
    uint crown = id / uint(CX_SPLASH_PARTS);
    int part = int(id % uint(CX_SPLASH_PARTS));
    vec3 hc = CxHash3(crown * 2654435761u + 7u);
    float ph = uSplashP.x / uSplashP.y + hc.z;
    float slice = floor(ph);
    float age = fract(ph);
    float life = uSplashP.y, gain = 1.0, v0k = 1.0, ringM = CX_SPLASH_RING_M;
    // what the water standing where it lands makes of a drop: crown strength, height, ring, rebound column
    vec4 hit = vec4(1.0, 1.0, 1.0, 0.0);
    float standing = 0.0;
    vec3 L, up = vec3(0.0, 0.0, 1.0);
    bool alive;
    if (foot)
    {
        // a footfall: its crowns scatter over the sole a few ms apart, sized by the water standing there --
        // a flick off damp stone, a sheet out of a puddle -- and nothing at all where the map says sheltered
        vec4 f = uFoot[min(crown / uint(CX_FOOT_CROWNS), uint(CX_FOOT_MAX - 1))];
        vec3 hf = CxHash3(crown * 374761393u + 11u);
        life = CX_FOOT_LIFE;
        age = (f.w - hf.z * CX_FOOT_STAGGER) / life;
        slice = float(crown);
        float fr = CX_FOOT_SPREAD * sqrt(hf.x), fa = hf.y * 6.2831853;
        L = f.xyz + vec3(cos(fa) * fr, sin(fa) * fr, 0.0);
        vec3 n;
        bool onPlane;
        vec3 ground = CxSplashLand(L.xy, n, onPlane);
        if (onPlane && abs(ground.z - L.z) < CX_FOOT_GROUND_M) { L = ground; up = n; }
        vec4 m = CxWetMap(L.xy);
        bool mapped = CxWetMapped(m);
        float wet = mapped ? clamp(m.x * uWxMapXf.w, 0.0, 1.0) * CxWetShelter(m, L.z) : uFootW.x;
        float pool = mapped ? smoothstep(0.0, max(uWxMapP.y, 1e-4), CxWetDepth(m)) : 0.0;
        v0k = mix(CX_FOOT_V0.x, CX_FOOT_V0.y, pool);
        ringM = CX_SPLASH_RING_M * mix(CX_FOOT_RING.x, CX_FOOT_RING.y, pool);
        gain = uFootW.y * smoothstep(CX_FOOT_WET.x, CX_FOOT_WET.y, wet);
        alive = age >= 0.0 && age < 1.0 && gain > 0.0;
        age = clamp(age, 0.0, 1.0);
    }
    else
    {
        // the landing point is re-drawn every lifetime: one fixed point per instance would read as a sprinkler
        vec3 hp = CxHash3(crown * 374761393u + uint(slice) * 668265263u + 1u);
        float rad = uSplashP.z * sqrt(hp.x);          // sqrt: uniform over the disc's AREA, not its radius
        float ang = hp.y * 6.2831853;
        // off the centre as it stood at this crown's birth, so a crown never rides along with a moving player
        vec2 xy = uSplashC.xy - uSplashV * (age * uSplashP.y) + vec2(cos(ang), sin(ang)) * rad;
        bool ok;
        L = CxSplashLand(xy, up, ok);
        // no edge: the field thins by crown COUNT from three quarters of the disc out, the way the drops do --
        // a brightness ramp on a circle reads as a ring on the ground
        float edge = 1.0 - smoothstep(0.75, 1.0, rad / max(uSplashP.z, 1e-3));
        alive = ok && fract(hp.z * 91.7) <= edge && CxSplashSeen(L);
        standing = CxSplashStanding(L);
        hit = CxSplashByWater(standing);
    }

    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    vec3 P = L, axis = vec3(0.0), side = vec3(0.0);
    // the crown's frame on the surface it hit; on level ground T and B are world x and y
    vec3 T = normalize(cross(vec3(0.0, 1.0, 0.0), up)), B = cross(up, T);
    vec3 N = up;
    float k = hit.y * v0k;
    int kind = CX_PART_RING;
    vec2 quad = vec2(0.0), q = vec2(0.0);
    float seed = 0.4 + 0.6 * fract(hc.x * 7.31);
    if (part == 1)
    {
        // the crown's sheet, or in standing water the column its dip throws back up: an upright quad over the landing
        // point turned to the camera, the shape itself drawn in the fragment stage
        vec3 hd = CxHash3(crown * 2246822519u + uint(slice) * 3266489917u + 1u);
        bool column = hd.y < hit.w;
        kind = column ? CX_PART_COLUMN : CX_PART_SHEET;
        if (!column) alive = alive && hit.x > 0.01;
        quad = column ? vec2(0.5 * CX_COLUMN.y, CX_COLUMN.x)
                      : vec2(CX_CROWN_R * CX_CROWN_SPREAD, CX_CROWN_H) * k;
        vec3 toCam = uEye - L;
        side = cross(up, toCam);
        side = dot(side, side) > 1e-10 ? normalize(side) : T;
        P = L + up * (0.5 * quad.y);
        seed = column ? 1.0 : hit.x;
    }
    else if (part > 1)
    {
        vec3 hd = CxHash3(crown * 2246822519u + uint(slice) * 3266489917u + uint(part));
        kind = CX_PART_BEAD;
        if ((part == CX_SPLASH_PARTS - 1 && hd.z > 0.5) || fract(hd.z * 13.7) >= hit.x) alive = false;
        float a = (float(part) - 1.5 + hc.y) * 6.2831853 / float(CX_SPLASH_BEADS);
        float v = mix(CX_BEAD_V.x, CX_BEAD_V.y, hd.y) * k, tilt = mix(CX_BEAD_TILT.x, CX_BEAD_TILT.y, hd.x);
        float t = (age - CX_BEAD_BREAK) * life;
        vec2 rim = CxCrownShape(CX_BEAD_BREAK) * k;
        vec2 f = CxBeadFlight(max(t, 0.0), v, tilt, vec2(rim.y, rim.x));
        if (t < 0.0 || f.y < 0.0) alive = false;   // still part of the lip, or landed
        vec3 away = T * cos(a) + B * sin(a);
        P = L + away * f.x + up * f.y;
        vec3 vel = away * (v * cos(tilt)) + up * (v * sin(tilt) - CX_HIT_G * max(t, 0.0));
        axis = normalize(vel);
        side = normalize(cross(axis, uEye - P));
        quad = vec2(length(vel), 0.0);
        // a bead has no surface: the camera-facing direction stands in for its normal, as on a falling drop
        N = normalize(uEye - P);
    }

    vec4 cv = uView * vec4(P, 1.0);
    float px = uSplashP.w / max(-cv.z, 0.05);     // pixels per metre at this part's own depth
    float dim;
    vec3 pos;
    if (part == 0)
    {
        // the ring: a quad in the surface plane over the landing point, its radius run out inside the fragment
        // stage so the quad never changes size. Strictly world-sized -- the footprint only FADES it.
        dim = smoothstep(CX_SPLASH_PX.x, CX_SPLASH_PX.y, ringM * CX_SPLASH_CREST * px) * hit.z;
        pos = P + (T * corner.x + B * corner.y) * (ringM * 1.15) + up * CX_SPLASH_LIFT;
    }
    else if (part == 1)
    {
        float grow = max(CX_SPLASH_MINPX / max(min(2.0 * quad.x, quad.y) * px, 1e-6), 1.0);
        dim = 1.0 / (grow * grow);
        // q is in a full crown's metres, so the fragment stage draws the shipped shape whatever this crown's size
        q = vec2(corner.x * quad.x, (corner.y * 0.5 + 0.5) * quad.y) / (kind == CX_PART_COLUMN ? 1.0 : max(k, 1e-3));
        pos = L + (side * (corner.x * quad.x) + up * ((corner.y * 0.5 + 0.5) * quad.y - 0.5 * quad.y)) * grow
            + up * (0.5 * quad.y);
    }
    else
    {
        vec2 b = CxSplashBeadSize(quad.x, px), truth = CxSplashBeadSize(quad.x, 1.0e9);
        dim = (truth.x * truth.y) / (b.x * b.y);
        pos = P + axis * (corner.y * b.x * 0.5) + side * (corner.x * b.y * 0.5);
    }

    dim *= gain * smoothstep(CX_SPLASH_NEAR.x, CX_SPLASH_NEAR.y, -cv.z);
    vUV = part == 1 ? q : corner;
    vAge = age;
    vPart = float(kind);
    vSeed = seed * dim;
    vLight = uAreaAmbient + uAreaDiffuse * 0.5;
    vSide = side;
    vUp = vec3(0.0, 0.0, 1.0);
    vLampDir = vec3(0.0, 0.0, 1.0);
    vSunVis = 1.0;
    vSkyUp = vec3(0.0);
    vSkyLow = vec3(0.0);
    if (part > 0)
    {
        // the sheet, the column and the beads are water like a falling drop: their axes, the sky over them, the lamps
        vec3 toEye = uEye - P;
        vUp = part == 1 ? up : normalize(cross(toEye, side));
        vec3 low = vec3(-toEye.xy, 0.0);
        low = dot(low, low) > 1e-8 ? normalize(low) : vec3(1.0, 0.0, 0.0);
        vSkyUp = CxWxSky(vec3(0.0, 0.0, 1.0));
        vSkyLow = CxWxSky(low);
        vLight = vec3(0.0);
        if (uLit != 0 && alive)
        {
            vec4 cc = uProj * cv;
            cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
            vLight = CxWxLampsDir(P, max(-cv.z, 1e-3), vLampDir);
            vSunVis = CxSunShadow(P, uAreaDir, 1.0, 1.0);
        }
    }
    else if (uLit != 0 && alive)
    {
        vec4 cc = uProj * cv;
        cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
        float ndl = clamp(dot(N, uAreaDir), 0.0, 1.0);
        vec3 sun = uAreaDiffuse * (ndl * CxSunShadow(P, N, ndl, ndl));
        vLight = uAreaAmbient * mix(CX_SPLASH_AMB, 1.0, ndl) + sun + CxClusteredDiffuse(P, N, max(-cv.z, 1e-3));
    }
    if (uViz == CX_SPLASH_VIZ) vLight = mix(vec3(0.0, 1.0, 0.0), vec3(0.0, 0.3, 1.0), standing);
    vec4 pv = uView * vec4(pos, 1.0);
    vViewZ = pv.z;
    gl_Position = alive && dim > 0.0 ? uProj * pv : vec4(2.0, 2.0, 2.0, 1.0);
}
