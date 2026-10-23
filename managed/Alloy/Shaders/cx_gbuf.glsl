// CX-188 (ADR 0029): the deferred G-buffer -- the colour pass's CX_DEFERRED twin writes it beside the colour on
// draw buffers 4..7 (opaque and cutout, main view only), and cx_deferred_cs reads it back per pixel to add the
// point lights the twin no longer walks. This file IS the format: the writer and the reader both include it, and
// docs/spec/deferred-lighting.md says what every lane means. Change a lane here or nowhere.
// 56 bytes a pixel: three RGBA32UI words and one RGBA16F. A pixel no twin draw covered holds G0 = 0, which reads
// back as flags 0 -- "not mine" -- so the sky, grass and every forward-lit class are left alone by construction.
// A forward draw that lands on a pixel a twin already wrote (a hosted or viz batch, a non-prepassed LEQUAL batch)
// writes no G-buffer and leaves that record behind; the record therefore carries the twin's own view depth and
// the reader drops it when the frame's depth no longer agrees -- the same test Ssr's record takes (CX_SSR_RECORD).
#ifndef CX_GBUF
#define CX_GBUF
#include "cx_ssrrec.glsl"

/// G0.y's flag byte. CX_GB_DEFERRED is the only bit the reader gates on; the rest select the walk's branches
/// exactly as the forward loop's own arguments did (baked, onMover, the film statics, cxRecvShadow == 0).
const uint CX_GB_DEFERRED = 1u, CX_GB_BAKED = 2u, CX_GB_ONMOVER = 4u, CX_GB_FILM = 8u, CX_GB_NOSHADOW = 16u,
           CX_GB_STOCKLOBE = 32u;
/// How far (a share of view depth) the record's depth may sit off the frame's before a later draw is judged to
/// have covered it.
const float CX_GB_RECORD = 0.01;

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
    float sheenRough;  // cxSheenRough (an alpha; 0 = no sheen)
    vec3 sheen;        // cxSheen (linear)
    vec3 f0;           // cxF0
    vec3 specScale;    // cxSpecScale
    float specT;       // P: d(final pixel)/d(pointSpec) -- dirOcc, the ghost divide, keyhole, fog transmittance
    vec3 diffT;        // Wd: d(final pixel)/d(point) -- albedo, matDiffuse, kd, coatT, wxTransmit, dirOcc, the env
                       // pattern and coat-probe couplings, keyhole, fog transmittance
    float lmLum;       // luminance of the UNDIMMED lightmap term `lm` (the lmSub.x denominator); 0 = not baked
    float lmTexel;     // CxLmTexelWorld(chart.rec)
    vec4 lmMover;      // CxLightmapMover(chart.rec, vChart)
    vec4 lmPick;       // chart.pick (-1 = no channel); a byte lane: set indices past 254 read back as none
    float viewDepth;   // the twin's own linear view depth (1 / gl_FragCoord.w), the record's ownership test
    vec3 lmShare;      // L: the baked term's whole contribution to the final pixel (diffuse and its lobe), which the
                       // hot-light debt scales back: final -= L * min(lmSub.x / lmLum + lmSub.y, 1)
};

/// sqrt-encoded unorm8 for the small alphas and the sheen colour, where a linear byte would lose a film's 0.02.
float CxGbSq(float x) { return sqrt(clamp(x, 0.0, 1.0)); }
float CxGbUnsq(float x) { return x * x; }

#ifdef CX_GBUF_OUT
layout(location = 4) out uvec4 oGb0;   // N, rough/spec/fresnel/flags, coat/coatRough/sheenRough, f0
layout(location = 5) out uvec4 oGb1;   // specScale + P, lmLum + lmTexel, sheen
layout(location = 6) out uvec4 oGb2;   // Wd, lmMover, lmPick
layout(location = 7) out vec4 oGb3;    // L (rgb), a unused

void CxGbufWrite(CxGbuf g)
{
    oGb0 = uvec4(packSnorm2x16(CxOctEncode(g.N)),
                 packUnorm4x8(vec4(g.rough, g.spec, g.fresnel, float(g.flags & 255u) / 255.0)),
                 packUnorm4x8(vec4(g.coat, CxGbSq(g.coatRough), CxGbSq(g.sheenRough), 0.0)),
                 packUnorm4x8(vec4(g.f0, 0.0)));
    oGb1 = uvec4(packHalf2x16(g.specScale.rg), packHalf2x16(vec2(g.specScale.b, g.specT)),
                 packHalf2x16(vec2(g.lmLum, g.lmTexel)),
                 packUnorm4x8(vec4(CxGbSq(g.sheen.r), CxGbSq(g.sheen.g), CxGbSq(g.sheen.b), 0.0)));
    // a pick lane holds the set index plus one, 0 = none; set indices past 254 have no lane
    uvec4 pk = uvec4(clamp(g.lmPick + 1.0, vec4(0.0), vec4(255.0)));
    oGb2 = uvec4(packHalf2x16(g.diffT.rg), packHalf2x16(vec2(g.diffT.b, g.viewDepth)),
                 packUnorm4x8(g.lmMover), pk.x | (pk.y << 8u) | (pk.z << 16u) | (pk.w << 24u));
    oGb3 = vec4(g.lmShare, 0.0);
}
#endif

/// The reader's half. False = no twin draw lit this pixel, or a later draw covered it; leave it alone.
/// <paramref name="frameDepth"/> is the linear view depth the frame's depth buffer holds at the pixel.
bool CxGbufRead(uvec4 g0, uvec4 g1, uvec4 g2, vec4 g3, float frameDepth, out CxGbuf g)
{
    vec4 a = unpackUnorm4x8(g0.y);
    g.flags = uint(a.w * 255.0 + 0.5);
    if ((g.flags & CX_GB_DEFERRED) == 0u) return false;
    g.N = CxOctDecode(unpackSnorm2x16(g0.x));
    g.rough = a.x; g.spec = a.y; g.fresnel = a.z;
    vec4 b = unpackUnorm4x8(g0.z);
    g.coat = b.x; g.coatRough = CxGbUnsq(b.y); g.sheenRough = CxGbUnsq(b.z);
    g.f0 = unpackUnorm4x8(g0.w).rgb;
    vec2 s0 = unpackHalf2x16(g1.x), s1 = unpackHalf2x16(g1.y), lm = unpackHalf2x16(g1.z);
    g.specScale = vec3(s0, s1.x); g.specT = s1.y;
    g.lmLum = lm.x; g.lmTexel = lm.y;
    vec4 sh = unpackUnorm4x8(g1.w);
    g.sheen = vec3(CxGbUnsq(sh.r), CxGbUnsq(sh.g), CxGbUnsq(sh.b));
    vec2 d0 = unpackHalf2x16(g2.x), d1 = unpackHalf2x16(g2.y);
    g.viewDepth = d1.y;
    if (!(abs(g.viewDepth / max(frameDepth, 1e-6) - 1.0) < CX_GB_RECORD)) return false;
    g.diffT = vec3(d0, d1.x);
    g.lmMover = unpackUnorm4x8(g2.z);
    uvec4 pk = uvec4(g2.w & 255u, (g2.w >> 8u) & 255u, (g2.w >> 16u) & 255u, g2.w >> 24u);
    g.lmPick = vec4(pk) - 1.0;
    g.lmShare = g3.rgb;
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
