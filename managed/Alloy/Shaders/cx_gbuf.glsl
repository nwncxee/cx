// CX-188 (ADR 0029): the deferred G-buffer -- the colour pass's CX_DEFERRED twin writes it beside the colour on
// draw buffers 4..5 (opaque and cutout, main view only), and cx_deferred_cs reads it back per pixel to add the
// point lights the twin no longer walks. This file IS the format: the writer and the reader both include it (a
// reader includes cx_drawbuf and cx_chartbuf ahead of it), and docs/spec/deferred-lighting.md says what every lane
// means. Change a lane here or nowhere.
// 32 bytes a pixel: two RGBA32UI words, G0 the header every reader takes (flags, normal, depth, the lobe's
// scalars, the draw's slot) and G1 the transfer weights only the light walk and the debt read. What the reader can
// rebuild is not stored: cxSpecScale (CxSpecScaleOf, off N, V, rough, coat and the one colour each model's lobe
// reads), and the per-draw sheen and lightmap record, fetched by slot from the same buffers the twin read them from.
// A pixel no twin draw covered holds G0 = 0, which reads back as flags 0 -- "not mine" -- so the sky, grass and
// every forward-lit class are left alone by construction.
// A forward draw that lands on a pixel a twin already wrote (a hosted or viz batch, a non-prepassed LEQUAL batch)
// writes no G-buffer and leaves that record behind; the record therefore carries the twin's own view depth and
// the reader drops it when the frame's depth no longer agrees -- the same test Ssr's record takes (CX_SSR_RECORD).
#ifndef CX_GBUF
#define CX_GBUF
#include "cx_ssrrec.glsl"

/// G0.x's flag byte. CX_GB_DEFERRED is the only bit the reader gates on; the rest select the walk's branches
/// exactly as the forward loop's own arguments did (baked, onMover, the film statics, cxRecvShadow == 0).
const uint CX_GB_DEFERRED = 1u, CX_GB_BAKED = 2u, CX_GB_ONMOVER = 4u, CX_GB_FILM = 8u, CX_GB_NOSHADOW = 16u,
           CX_GB_STOCKLOBE = 32u;
/// How far (a share of view depth) the record's depth may sit off the frame's before a later draw is judged to
/// have covered it.
const float CX_GB_RECORD = 0.01;
/// G0.w's slot lane: ownership slots up to 2^18 - 1 (AlloyRenderer.MaxOwned is held under it by a self-check).
const uint CX_GB_SLOT_BITS = 18u;

/// Everything the point-light walk reads off the fragment, plus the transfer weights that carry its result into
/// the finished pixel. Field names follow the lit stage's own variables.
struct CxGbuf
{
    uint flags;
    vec3 N;            // the shading normal the forward loop was handed
    float rough;       // the loop's `rough` (after the wet splice and specular AA)
    float spec;        // cxSpec / the loop's `specularity`
    float fresnel;     // cxFresnel
    float coat;        // cxCoat (film included)
    float coatRough;   // cxCoatRough (an alpha)
    vec3 specC;        // cxF0 under the modern model, the specular colour under the engine's (CxSpecScaleOf's c)
    int slot;          // the draw's ownership slot: uDraw[slot].ext1 is its sheen, uChart[slot] its lightmap record
    float specT;       // P: d(final pixel)/d(pointSpec) -- dirOcc, the ghost divide, keyhole, fog transmittance
    vec3 diffT;        // Wd: d(final pixel)/d(point) -- albedo, matDiffuse, kd, coatT, wxTransmit, dirOcc, the env
                       // pattern and coat-probe couplings, keyhole, fog transmittance
    float lmLum;       // luminance of the UNDIMMED lightmap term `lm` (the lmSub.x denominator); 0 = not baked
    float lmTexel;     // CxLmTexelWorld(chart.rec) -- read side only, from uChart[slot]
    vec4 lmMover;      // CxLightmapMover(chart.rec, vChart)
    vec4 lmPick;       // chart.pick (-1 = no channel) -- read side only, from uChart[slot]
    float viewDepth;   // the twin's own linear view depth (1 / gl_FragCoord.w), the record's ownership test
    vec3 lmShare;      // L: the baked term's whole contribution to the final pixel (diffuse and its lobe), which the
                       // hot-light debt scales back: final -= L * min(lmSub.x / lmLum + lmSub.y, 1)
};

/// sqrt-encoded unorm for the small alphas and the colour, where a linear code would lose a film's 0.02.
float CxGbSq(float x) { return sqrt(clamp(x, 0.0, 1.0)); }
float CxGbUnsq(float x) { return x * x; }

/// Shared-exponent RGB (GL's RGB9_E5 layout, which GLSL has no pack builtin for): non-negative, 9 mantissa bits
/// against the largest channel -- the weights' own range, half floats' dynamic range in one word.
uint CxGbPack9e5(vec3 c)
{
    c = clamp(c, vec3(0.0), vec3(65408.0));
    int e;
    frexp(max(c.r, max(c.g, c.b)), e);
    e = max(e, -15) + 15;
    uvec3 m = uvec3(min(floor(c * exp2(float(24 - e)) + 0.5), vec3(511.0)));
    return m.r | (m.g << 9u) | (m.b << 18u) | (uint(e) << 27u);
}
vec3 CxGbUnpack9e5(uint p)
{
    return vec3(p & 511u, (p >> 9u) & 511u, (p >> 18u) & 511u) * exp2(float(int(p >> 27u)) - 24.0);
}

/// The two words a pixel's record packs into -- the writer's whole half, outside CX_GBUF_OUT so check-shaders.py
/// can round-trip it through the reader.
void CxGbufPack(CxGbuf g, out uvec4 w0, out uvec4 w1)
{
    // the octahedral normal as two 12-bit snorms, 0 exact (a flat floor stays flat)
    uvec2 n = uvec2(ivec2(round(clamp(CxOctEncode(g.N), -1.0, 1.0) * 2047.0)) + 2048);
    uint coat = uint(round(clamp(g.coat, 0.0, 1.0) * 127.0)), coatR = uint(round(CxGbSq(g.coatRough) * 127.0));
    w0 = uvec4((g.flags & 255u) | (n.x << 8u) | (n.y << 20u),
               packHalf2x16(vec2(g.viewDepth, 0.0)) | packUnorm4x8(vec4(0.0, 0.0, g.rough, g.spec)),
               packUnorm4x8(vec4(CxGbSq(g.specC.r), CxGbSq(g.specC.g), CxGbSq(g.specC.b), g.fresnel)),
               (uint(g.slot) & ((1u << CX_GB_SLOT_BITS) - 1u)) | (coat << 18u) | (coatR << 25u));
    w1 = uvec4(CxGbPack9e5(g.diffT), CxGbPack9e5(g.lmShare), packHalf2x16(vec2(g.specT, g.lmLum)),
               packUnorm4x8(g.lmMover));
}

#ifdef CX_GBUF_OUT
layout(location = 4) out uvec4 oGb0;   // N + flags, viewDepth + rough + spec, specC + fresnel, slot + coat + coatRough
layout(location = 5) out uvec4 oGb1;   // Wd, L, P + lmLum, lmMover

void CxGbufWrite(CxGbuf g) { CxGbufPack(g, oGb0, oGb1); }
#endif

/// The reader's header: flags, normal, view depth, the lobe's scalars and the slot -- all G0, so a reader that
/// needs no weight (cx_defshadow_cs) fetches 16 bytes. False = no twin draw lit this pixel, or a later draw
/// covered it; leave it alone. <paramref name="frameDepth"/> is the frame depth buffer's linear view depth.
bool CxGbufReadHead(uvec4 g0, float frameDepth, out CxGbuf g)
{
    g.flags = g0.x & 255u;
    if ((g.flags & CX_GB_DEFERRED) == 0u) return false;
    vec4 a = unpackUnorm4x8(g0.y);
    g.viewDepth = unpackHalf2x16(g0.y).x;
    if (!(abs(g.viewDepth / max(frameDepth, 1e-6) - 1.0) < CX_GB_RECORD)) return false;
    g.N = CxOctDecode(vec2(ivec2((g0.x >> 8u) & 4095u, g0.x >> 20u) - 2048) / 2047.0);
    g.rough = a.z; g.spec = a.w;
    vec4 c = unpackUnorm4x8(g0.z);
    g.specC = vec3(CxGbUnsq(c.r), CxGbUnsq(c.g), CxGbUnsq(c.b)); g.fresnel = c.w;
    g.slot = int(g0.w & ((1u << CX_GB_SLOT_BITS) - 1u));
    g.coat = float((g0.w >> 18u) & 127u) / 127.0;
    g.coatRough = CxGbUnsq(float(g0.w >> 25u) / 127.0);
    return true;
}

/// The whole record: the header, the weights, and the lightmap record the twin read by the same slot.
bool CxGbufRead(uvec4 g0, uvec4 g1, float frameDepth, out CxGbuf g)
{
    if (!CxGbufReadHead(g0, frameDepth, g)) return false;
    g.diffT = CxGbUnpack9e5(g1.x);
    g.lmShare = CxGbUnpack9e5(g1.y);
    vec2 pl = unpackHalf2x16(g1.z);
    g.specT = pl.x; g.lmLum = pl.y;
    g.lmMover = unpackUnorm4x8(g1.w);
    CxChart chart = uChart[g.slot];
    g.lmTexel = CxLmTexelWorld(chart.rec);
    g.lmPick = chart.pick;
    return true;
}

/// The LIGHT RECORD (GBuffer.TexLight, RGBA32UI): what cx_deferred_cs's walk answered at the pixel -- the point
/// and pointSpec sums before the transfer weights, the record's view depth (negative on a baked receiver) and
/// the share of its baked term the movers took back (the walk's own, plus cx_pairdebt's). A blended fragment
/// lying on that same surface reads it instead of walking (cx_ownlit_light). All zero = no record.
uvec4 CxLightRecPack(vec3 point, vec3 spec, float viewDepth, bool baked, float owe)
{
    return uvec4(packHalf2x16(point.rg), packHalf2x16(vec2(point.b, spec.r)), packHalf2x16(spec.gb),
                 packHalf2x16(vec2(baked ? -viewDepth : viewDepth, owe)));
}

/// The reader: false unless the record sits at <paramref name="viewDepth"/> (CX_GB_RECORD, the G-buffer's own
/// ownership test) and was baked exactly as the reader is -- a baked record's walk skipped the static lights
/// the reader's own lightmap may not carry.
bool CxLightRecRead(uvec4 r, float viewDepth, bool baked, out vec3 point, out vec3 spec, out float owe)
{
    vec2 a = unpackHalf2x16(r.x), b = unpackHalf2x16(r.y), c = unpackHalf2x16(r.z), d = unpackHalf2x16(r.w);
    point = vec3(a, b.x); spec = vec3(b.y, c); owe = d.y;
    return (d.x < 0.0) == baked && abs(abs(d.x) - viewDepth) < CX_GB_RECORD * viewDepth;
}
#endif
