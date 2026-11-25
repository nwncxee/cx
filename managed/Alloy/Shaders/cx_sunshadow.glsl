#include "cx_pass.glsl"
uniform sampler2DArrayShadow uSunShadow;

// #306: the sun's DYNAMIC half -- movers only, re-baked every frame with the SAME per-cascade fit
// matrix as the static layer, composed by min(): the cube pool's own rule (#305), so the receiver
// has one composition rule, not two. uSunDyn[i] is 1 when layer i holds a fresh dynamic bake this
// frame and 0 otherwise.
uniform sampler2DArrayShadow uSunDynShadow;
/// shadow.raytrace: how much the STATIC map counts (1 = all); a stage whose rays answer for the static world takes it to 0
/// and keeps the movers' map, so creatures -- not in the ray-traced scene -- still cast.
float cxSunStaticW = 1.0;

// CX-49: the soft sun filter -- a blocker search and a rotated disc at the penumbra it estimates -- compiles
// ONLY into a stage whose head defines CX_SUN_SOFT. The sun lights every outdoor fragment, so the stages that
// ask are the ones where a disc shows: the lit surface (cx_ownlit_fs, which is the forward program, its OIT
// and hosted twins AND the deferred twin's G-buffer pass). Grass, particles, weather and water keep the
// four-tap cross -- a disc in a VERTEX stage blurs a whole blade or billboard, and near grass is already
// shaded against the same cascades the ground under it uses.
// The two prerequisites come from the cube receiver, which every stage that defines CX_SUN_SOFT composes
// BEFORE this library: CxSpin/CxVogelTurn (the Vogel disc) and CxShadowNoise (the per-pixel rotation). The
// order is pinned by CasterPass's self-check, not remembered.
// The sun's CEILING on blocker-search ring taps, under which shadow.search picks the count: HALF the cube's
// CX_LOCAL_SEARCH, because the sun's ring sits at the same uLocalSoft.x ceiling but is paid by EVERY outdoor
// fragment, and eight Vogel taps already cross every texel ring inside a four-texel default ceiling.
const int CX_SUN_SEARCH = 8;
// Below this penumbra radius (in texels) the disc stands down to CxSunTaps' cross: a fragment in contact,
// where the cross IS the correct hard edge and the disc would only cost fetches. Outside the CX_SUN_SOFT
// guard because the cascade handoff carries a radius across without reading either sampler.
const float CX_SUN_DISC_MIN = 0.5;
#ifdef CX_SUN_SOFT
// The cascade pair again through the no-compare sampler object ProbeRawSampler mints (TextureSlots.SunShadowRaw):
// a stored depth, not a verdict. uSunPenum is 0 where that sampler is absent, which stands the search down.
layout(binding = 29) uniform sampler2DArray uSunShadowRaw;
layout(binding = 30) uniform sampler2DArray uSunDynShadowRaw;
#endif

// Cascade edge kept clear of the PCF kernel: a fragment whose taps would straddle the border falls
// through to the next cascade instead of sampling past the map and reading "unshadowed".
const float CX_SUN_BORDER = 0.985;
// #308 follow-up (the "scanline"): the cascade handoff is a 4x jump in texel size, offset and bias,
// so the shadow visibly re-draws along the selection boundary -- and under the latched fits that
// boundary leaps several units at every re-latch. The fix is a cross-fade to the next cascade whose
// weight is a pure function of the fragment's distance from the LIVE fit focus -- never anything
// latched: a weight keyed on the latched fit jumps at every re-latch, which reads as the whole band
// flickering while walking. Fade runs from CX_SUN_FADE_LO to CX_SUN_FADE_HI of the cascade's
// radius; full handover at 90% keeps the weight saturated before the latched fit (drift <= 5% of
// the radius, CasterPass.SunFitSlack) can run out under the fragment. A zero radius disables the
// blend -- the pre-fade hard handoff.
const float CX_SUN_FADE_LO = 0.70;
const float CX_SUN_FADE_HI = 0.90;
// Shadow-accuracy pass: offsets halved from #308 (see LocalShadowLib); csmbias default halved with them.
// Sample point pushed along the normal by this many texels. Normal offset does what a depth bias
// cannot: it scales with the texel a slope actually spans, so acne goes without the detached-shadow
// peter-panning a bias big enough for grazing light would cost.
const float CX_SUN_NORMAL_OFFSET = 1.0;
// #308 follow-up (acne): the constant offset above is sized for a face the light hits square-on. A
// face lit at grazing incidence spans tan(theta) texels of DEPTH per texel of map, so the terminator
// marches across it in texel-sized stair steps unless the offset grows with the slope. Extra texels
// of push per unit of tan(theta), and the clamp that keeps a fully edge-on face (tan -> inf) from
// being lifted into the next caster over.
const float CX_SUN_SLOPE_OFFSET = 0.75;
const float CX_SUN_SLOPE_MAX = 2.0;
// #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
// 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
// actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
// along it walks the sample point sideways across the shadow map's texel grid and turns a clean
// lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
// what it needs.
// Unset reads 0, which is the surface behaviour every existing program and every content override
// already relies on -- the safe default is the one nobody has to declare.
uniform float uSunMedia;

// The fragment in cascade i's NDC, sampled from a point pushed along the normal by that cascade's
// texel-scaled acne offset -- so each cascade tests the surface it can actually resolve.
vec3 CxSunNdc(int i, vec3 worldPos, vec3 N, float slope)
{
    vec4 clip = uSunVp[i] * vec4(worldPos + N * (uSunTexel[i]
                    * (CX_SUN_NORMAL_OFFSET * (1.0 - uSunMedia)
                       + CX_SUN_SLOPE_OFFSET * slope)), 1.0);
    return clip.xyz / clip.w;
}

// One cascade's answer: the four-tap half-texel cross on the static layer, min-composed with the
// same kernel on the dynamic layer when that layer is fresh (#306) -- matching kernels, because the
// dynamic half is the player's own shadow and a harder edge on exactly that shadow is the
// regression the split must not have. The depth bias grows on the slope: the stored depth inside
// one grazing texel varies by tan(theta) texels' worth of world depth, so the flat bias under-covers it.
float CxSunTaps(int i, vec3 ndc, float slope, vec2 texel)
{
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float layer = float(i);
    float ref = ndc.z * 0.5 + 0.5 - uSunBias[i] * (1.0 + slope);
    vec2 o = texel * 0.5;
    float vs = 0.25 * (texture(uSunShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                     + texture(uSunShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                     + texture(uSunShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                     + texture(uSunShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
    float vd = 1.0;
    if (uSunDyn[i] > 0.5)
        vd = 0.25 * (texture(uSunDynShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                   + texture(uSunDynShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                   + texture(uSunDynShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                   + texture(uSunDynShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
    return min(mix(1.0, vs, cxSunStaticW), vd);
}

#ifdef CX_SUN_SOFT
// CX-49: the penumbra radius in texels, 0 .. uLocalSoft.x -- PCSS with the sun's ANGULAR size as the source.
// The mean of the blockers shadow.search raw reads (CX_SUN_SEARCH at most) find in front of the receiver, then
// (receiver - blocker) * uSunPenum, which carries tan(half the sun's angular size) times the map size
// (ShadowMath.SunPenumbraScale): a cascade's stored depth spans exactly mapSize texels of world, so one
// number serves every cascade.
// Both layers are read in ONE ring and min-composed -- depth is a min-of-blockers, the same rule CxSunTaps'
// min() rests on -- so the pair answers with a single radius and the two discs below come out at identical
// kernels, which is what the dynamic half (the player's own shadow) must not lose.
// The ring is NOT dithered: neighbouring fragments must agree on the penumbra, or the edge speckles where one
// blurs and the next takes the hard tap (cx_localshadow's CxPenumbra, same rule, same reason).
// Its RADIUS is bounded the way the cube's is, by what the source size and the receiver's depth allow: a
// cascade is orthographic, so a blocker k texels from the receiver's own texel casts its edge k texels away,
// and the widest penumbra any blocker in front of this fragment can make is ref * uSunPenum (a blocker at the
// cascade's near plane). A wider ring would sample casters that cannot reach here at all.
// `full` = every read found a blocker, so the cross already holds the umbra and the disc would re-confirm it.
float CxSunPenumbra(vec2 uv, float layer, float ref, vec2 texel, bool dyn, out bool full)
{
    full = false;
    float sum = 0.0, n = 0.0;
    float sr = min(uLocalSoft.x, ref * uSunPenum);
    int ns = min(CxSearchTaps(), CX_SUN_SEARCH);
    mat2 ring = CxSpin(ns, 0.0, sr);
    vec2 dir = vec2(1.0, 0.0);
    for (int k = 0; k < ns; k++)
    {
        vec2 o = ring * (sqrt(float(k) + 0.5) * dir);
        dir = CxVogelTurn(dir);
        vec2 t = uv + o * texel;
        float zk = texture(uSunShadowRaw, vec3(t, layer)).r;
        if (dyn) zk = min(zk, texture(uSunDynShadowRaw, vec3(t, layer)).r);
        if (zk < ref) { sum += zk; n += 1.0; }
    }
    if (n <= 0.0) return 0.0;
    full = n >= float(ns);
    return clamp((ref - sum / n) * uSunPenum, 0.0, uLocalSoft.x);
}

// One layer's rotated Vogel disc at `rs` texels, the centre tap included. Each fetch is itself a hardware 2x2
// comparison, so the disc WIDENS that filter rather than replacing it. The reference depth is the centre's
// for every tap: re-deriving it per tap would compare each neighbour against its own surface and dissolve the
// penumbra the disc exists to create.
float CxSunDisc(sampler2DArrayShadow map, vec2 uv, float layer, float ref, vec2 texel, float rs, int taps, float rot)
{
    float v = texture(map, vec4(uv, layer, ref));
    mat2 disc = CxSpin(taps, rot, rs);
    vec2 dir = vec2(1.0, 0.0);
    for (int i = 0; i < taps; i++)
    {
        vec2 o = disc * (sqrt(float(i) + 0.5) * dir);
        dir = CxVogelTurn(dir);
        v += texture(map, vec4(uv + o * texel, layer, ref));
    }
    return v / float(taps + 1);
}
#endif

// CX-49: the penumbra radius this fragment wants in cascade i's texels, or -1 for "take the cheap cross".
// -1 is every stand-down: no CX_SUN_SOFT in this stage, uSunPenum 0 (no raw view on this driver, or
// shadow.sunangle 0), shadow.taps 0, shadow.soft 0, a receiver too near the cascade's near plane for any
// blocker to make a disc, a search that found no blocker, and a search where every read did.
float CxSunRadius(int i, vec3 ndc, float slope, vec2 texel)
{
#ifdef CX_SUN_SOFT
    if (uSunPenum <= 0.0 || uLocalSoft.x <= 0.0 || int(uLocalPcss.z) <= 0) return -1.0;
    float ref = ndc.z * 0.5 + 0.5 - uSunBias[i] * (1.0 + slope);
    // `ref` is the deepest a blocker gap in front of this fragment can be, so uSunPenum * ref is the widest
    // penumbra this cascade could produce here -- under the disc's own floor, a search is fetches spent on an
    // edge that comes out hard either way.
    if (uSunPenum * ref < CX_SUN_DISC_MIN) return -1.0;
    bool full;
    float rs = CxSunPenumbra(ndc.xy * 0.5 + 0.5, float(i), ref, texel, uSunDyn[i] > 0.5, full);
    return full || rs < CX_SUN_DISC_MIN ? -1.0 : rs;
#else
    return -1.0;
#endif
}

// The SAME penumbra in cascade i+1's texels. A penumbra is a world width and uSunTexel is world units per
// texel, so the handoff carries the estimate across instead of searching the next cascade's (4x coarser) map
// for a worse answer -- which is what kept the cross-fade band at one search rather than two. Negative in,
// negative out: the cheap cross crosses the handoff too.
float CxSunCarry(int i, float rs)
{
    if (rs < 0.0 || uSunTexel[i] <= 0.0 || uSunTexel[i + 1] <= 0.0) return -1.0;
    float r2 = min(uLocalSoft.x, rs * uSunTexel[i] / uSunTexel[i + 1]);
    return r2 < CX_SUN_DISC_MIN ? -1.0 : r2;
}

// One cascade's answer at an estimate already in hand: CxSunTaps' cross where `rs` is negative, else a
// rotated Vogel disc at that radius on each layer, min-composed exactly as the cross composes them.
float CxSunFilter(int i, vec3 ndc, vec3 worldPos, float slope, vec2 texel, float rs)
{
#ifdef CX_SUN_SOFT
    if (rs >= 0.0)
    {
        vec2 uv = ndc.xy * 0.5 + 0.5;
        float layer = float(i);
        float ref = ndc.z * 0.5 + 0.5 - uSunBias[i] * (1.0 + slope);
        int taps = min(int(uLocalPcss.z), CX_VOGEL_MAX);
        float rot = fract(CxShadowNoise(worldPos) + uLocalPcss.y);
        float vs = CxSunDisc(uSunShadow, uv, layer, ref, texel, rs, taps, rot);
        float vd = uSunDyn[i] > 0.5 ? CxSunDisc(uSunDynShadow, uv, layer, ref, texel, rs, taps, rot) : 1.0;
        return min(mix(1.0, vs, cxSunStaticW), vd);
    }
#endif
    return CxSunTaps(i, ndc, slope, texel);
}

// Sun visibility at a world point, 0 (fully shadowed) .. 1 (lit). Four hardware-PCF fetches on a
// half-texel cross: each fetch is itself a bilinear 2x2 comparison, so the kernel is a 4x4 filter for
// the price of four samples and the edge is soft without any per-pixel loop.
// `ndl` gates: the lit stage passes its WRAPPED value so a wrap-lit terminator fragment can still be
// shadowed. `ndlRaw` is the geometric dot for the slope terms -- the wrap inflates it exactly where
// grazing incidence needs the extra bias, so the two must stay separate arguments.
float CxSunShadow(vec3 worldPos, vec3 N, float ndl, float ndlRaw)
{
    if (uSunCascades <= 0 || ndl <= 0.0) return 1.0;
    vec2 texel = 1.0 / vec2(textureSize(uSunShadow, 0).xy);
    // tan of the light-to-normal angle, the media gate folded in: a particle's camera-facing normal
    // makes the slope meaningless, so a volume keeps the constant bias alone as before. Zero where
    // the raw dot is not positive: those fragments are lit only by the WRAP, have no sun-facing
    // surface to grow acne on, and a slope pinned at max there punches the bias through doors and
    // thin casters exactly inside the band the wrapped gate exists to keep shadowable.
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_SUN_SLOPE_MAX)
                    * (1.0 - uSunMedia);
    for (int i = 0; i < uSunCascades; i++)
    {
        vec3 ndc = CxSunNdc(i, worldPos, N, slope);
        if (any(greaterThan(abs(ndc.xy), vec2(CX_SUN_BORDER))) || abs(ndc.z) > 1.0) continue;
        float rs = CxSunRadius(i, ndc, slope, texel);
        float v = CxSunFilter(i, ndc, worldPos, slope, texel, rs);
        // The handoff gradient: fade toward the next cascade's answer over a world-space band tied
        // to the live focus, so the 4x resolution/bias jump never draws a line and never jumps.
        float R = uSunRadius[i];
        float f = R > 0.0
                  ? clamp((distance(worldPos, uSunFocus) - CX_SUN_FADE_LO * R)
                          / ((CX_SUN_FADE_HI - CX_SUN_FADE_LO) * R), 0.0, 1.0)
                  : 0.0;
        if (f > 0.0 && i + 1 < uSunCascades)
        {
            vec3 n2 = CxSunNdc(i + 1, worldPos, N, slope);
            if (all(lessThan(abs(n2.xy), vec2(CX_SUN_BORDER))) && abs(n2.z) <= 1.0)
                // the SAME filter on both sides of the handoff, at the same estimate carried into the next
                // cascade's texels (CxSunCarry): fading a disc into a cross would draw the cascade boundary
                // as a softness step, which is the line #308's cross-fade exists to remove
                v = mix(v, CxSunFilter(i + 1, n2, worldPos, slope, texel, CxSunCarry(i, rs)), f);
        }
        return v;
    }
    return 1.0;   // past the outermost cascade: no shadow information, and lit is the safe answer
}
