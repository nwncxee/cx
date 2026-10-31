// CX-102: the splash. One instanced quad per crown part, placed where the rain actually lands -- the cover
// map is an orthographic depth view along the fall vector, so its depth over a point IS the first surface
// the rain reaches there: a roof, a ledge, a wall top, the ground, and never anything under shelter. The
// owner's complaint was that impacts only showed where a puddle happened to be reflecting a light; this
// draws them wherever rain falls, lit like the drops and independent of any reflection.
// No CPU work per splash and no buffer: the instance id hashes to a point on a camera-centred disc and to
// a time slice, and each slice re-draws the point, so the field never strobes in fixed spots.

/// Droplets in one crown. The last two are each dropped on their own hash, so a crown is three to five.
const int CX_SPLASH_DROPS = 5;
/// Parts per crown: the ring on the ground (index 0), then the droplets. The C# side sizes the draw by this.
const int CX_SPLASH_PARTS = CX_SPLASH_DROPS + 1;
const float CX_SPLASH_G = 9.81;
/// Launch speed of a crown droplet, m/s: what a rain drop throws back up off a hard surface.
const vec2 CX_SPLASH_V0 = vec2(1.1, 1.9);
/// The ring's own reach, metres. Pinned to the puddle rings' CX_RIPPLE_REACH_M -- the same drop makes both,
/// so the one on the water and the one on the stone beside it may not be different sizes (Weather.SelfCheck).
const float CX_SPLASH_RING_M = 0.08;
/// Width of a droplet streak, metres. Its LENGTH is the distance it covers in one exposure.
const float CX_SPLASH_DROP_M = 0.008;
/// Seconds of travel one streak is drawn over: its own motion blur.
const float CX_SPLASH_BLUR_S = 0.025;
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
/// Screen width a droplet streak is never drawn narrower than, in pixels. A millimetre-scale droplet is a
/// fraction of a pixel past a few metres, and a sub-pixel quad does not dim -- it FLICKERS, as the rasteriser
/// catches and misses it. Widening it to about a pixel and dividing the scatter by the area that added is
/// the energy-conserving answer, and it is the DROPLETS only; the ring above never moves.
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
/// Droplet launch speed, as a share of a rain crown's: damp ground (x) flicks, standing water (y) throws.
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

/// False when the frame shows the scene well past `p`: the cover map has a surface there the frame never drew
/// (a wall the keyhole dissolved) and a crown on it would hang in the air. Off screen there is nothing to
/// test, and a point something nearer hides is the depth test's to clip.
bool CxSplashSeen(vec3 p)
{
    vec4 v = uView * vec4(p, 1.0);
    vec4 c = uProj * v;
    if (c.w <= 0.0 || any(greaterThan(abs(c.xy), vec2(c.w)))) return true;
    float ndc = texture(uSceneDepth, c.xy / c.w * 0.5 + 0.5).r;
    float scene = uProj[3].z / (ndc + uProj[2].z);
    return scene + v.z < CX_SPLASH_CONTACT.x - CX_SPLASH_CONTACT.y * v.z;
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
    }

    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    vec3 P = L, axis = vec3(0.0), side = vec3(0.0);
    // the crown's frame on the surface it hit; on level ground T and B are world x and y
    vec3 T = normalize(cross(vec3(0.0, 1.0, 0.0), up)), B = cross(up, T);
    vec3 N = up;
    float len = 0.0;
    if (part > 0)
    {
        vec3 hd = CxHash3(crown * 2246822519u + uint(slice) * 3266489917u + uint(part));
        if (part >= CX_SPLASH_DROPS - 1 && hd.z > 0.5) alive = false;
        float a = (float(part) - 0.5 + hc.y) * 6.2831853 / float(CX_SPLASH_DROPS);
        float theta = 0.5 + 0.45 * hd.x;          // radians off the normal: a crown leans out 29 to 54 degrees
        vec3 dir = T * (cos(a) * sin(theta)) + B * (sin(a) * sin(theta)) + up * cos(theta);
        float v0 = mix(CX_SPLASH_V0.x, CX_SPLASH_V0.y, hd.y) * v0k;
        float t = age * life;
        vec3 vel = dir * v0 - vec3(0.0, 0.0, CX_SPLASH_G * t);
        P = L + dir * (v0 * t) - vec3(0.0, 0.0, 0.5 * CX_SPLASH_G * t * t);
        if (dot(P - L, up) < 0.0) alive = false;  // back in the water: the droplet is spent
        vec3 toCam = uEye - P;
        axis = normalize(vel);
        side = normalize(cross(axis, toCam));
        len = clamp(length(vel) * CX_SPLASH_BLUR_S, CX_SPLASH_DROP_M, CX_SPLASH_DROP_M * 5.0);
        // a droplet has no surface: the camera-facing direction stands in for its normal, so a lamp behind
        // the camera lights it and one behind the droplet does not -- the drop stage's own read
        N = normalize(toCam);
    }

    vec4 cv = uView * vec4(P, 1.0);
    float px = uSplashP.w / max(-cv.z, 0.05);     // pixels per metre at this part's own depth
    float dim;
    vec3 pos;
    if (part == 0)
    {
        // the ring: a quad in the surface plane over the landing point, its radius run out inside the fragment
        // stage so the quad never changes size. Strictly world-sized -- the footprint only FADES it.
        dim = smoothstep(CX_SPLASH_PX.x, CX_SPLASH_PX.y, ringM * CX_SPLASH_CREST * px);
        pos = P + (T * corner.x + B * corner.y) * (ringM * 1.15) + up * CX_SPLASH_LIFT;
    }
    else
    {
        float floorM = CX_SPLASH_MINPX / max(px, 1e-3);
        float w = max(CX_SPLASH_DROP_M, floorM), l = max(len, floorM);
        dim = (CX_SPLASH_DROP_M * len) / (w * l);
        pos = P + axis * (corner.y * l * 0.5) + side * (corner.x * w * 0.5);
    }

    dim *= gain * smoothstep(CX_SPLASH_NEAR.x, CX_SPLASH_NEAR.y, -cv.z);
    vUV = corner;
    vAge = age;
    vPart = float(part);
    vSeed = (0.4 + 0.6 * fract(hc.x * 7.31)) * dim;
    vLight = uAreaAmbient + uAreaDiffuse * 0.5;
    if (uLit != 0 && alive)
    {
        vec4 cc = uProj * cv;
        cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
        float ndl = clamp(dot(N, uAreaDir), 0.0, 1.0);
        vec3 sun = uAreaDiffuse * (ndl * CxSunShadow(P, N, ndl, ndl));
        vLight = uAreaAmbient * mix(CX_SPLASH_AMB, 1.0, ndl) + sun + CxClusteredDiffuse(P, N, max(-cv.z, 1e-3));
    }
    gl_Position = alive && dim > 0.0 ? uProj * (uView * vec4(pos, 1.0)) : vec4(2.0, 2.0, 2.0, 1.0);
}
