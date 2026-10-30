#version 460 core
// The reflection trace: one ray per block of 1 << uSsr.w pixels, after the opaque set, sky, cutouts and grass (Ssr).
// Which pixel of the block, and which direction off its GGX lobe, are drawn from the R2 sequence, phased per frame;
// the ray walks THIS frame's depth as a hi-Z pyramid: it climbs while the ray stays in front of each cell's closest
// surface and confirms a hit at the finest level. Out goes where the ray landed and how likely its direction was --
// never a colour: cx_ssrres_f resolves the hits per full-res pixel, reusing its neighbours', and cx_ssrtmp_f keeps
// them over frames. A miss, a back face or a ray off the screen is pdf 0.
#include "cx_ssr.glsl"
#include "cx_ssrrec.glsl"
in vec2 vUV;
layout(location = 0) out vec4 frag;  // xy = the hit's uv, z = the ray's pdf (0 = no hit), w = the origin's 1 / view depth
uniform sampler2D uDepth;    // the frame depth
uniform sampler2D uHiZ;      // 1 / view depth: level 0 per pixel, each level up the MAX (closest) of the one below
uniform sampler2D uRecN;     // the record: octahedral world normal, roughness, view depth (cx_ssrrec)
uniform sampler2D uRecW;     // ...its weight
uniform mat4 uProj, uInvProj, uView;
uniform vec2 uNf;            // near, far
uniform vec4 uSsr;           // x = roughness cap, y = cell visits per ray, z = finest hi-Z level, w = log2 of the block
uniform int uFrame;          // the noise phase

/// The pyramid level the walk may climb to (a 64-pixel cell): coarser cells overlap too much of the scene to skip.
const int CX_SSR_TOP = 6;
/// The longest reflection, in view units.
const float CX_SSR_DIST = 60.0;
/// How far behind a pixel's surface the ray may sit and still hit it: a floor, and a share of the distance for
/// depth precision. Thin on purpose -- a thicker skin reads a ray passing BEHIND an object as a hit on it, which
/// draws a stretched second copy of the object below its reflection.
const float CX_SSR_THICK = 0.15, CX_SSR_THICKREL = 0.015;
vec3 ViewPos(vec2 uv, float depth)
{
    vec4 p = uInvProj * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    return p.xyz / p.w;
}

/// A surface the ray meets from behind or edge-on: what the mirror sees is past it, so the walk goes on. Only a
/// recorded surface knows its normal; an unrecorded one (grass, a hosted draw) is taken as facing.
bool Behind(ivec2 p, vec3 r)
{
    vec3 n, w; float rough;
    return CxRecRead(uRecN, uRecW, uHiZ, p, n, rough, w) && dot(mat3(uView) * n, r) >= 0.0;
}

/// Where view-space ray r from view position o meets the screen's surface, in pixels; false = it does not.
bool Trace(vec3 o, vec3 r, vec2 size, out vec2 ph)
{
    ph = vec2(0.0);
    float len = r.z > 0.0 ? min(CX_SSR_DIST, (-uNf.x * 1.01 - o.z) / r.z) : CX_SSR_DIST;
    int top = min(textureQueryLevels(uHiZ) - 1, CX_SSR_TOP);
    int fine = min(int(uSsr.z), top);
    if (len <= 0.0) return false;
    vec4 h0 = uProj * vec4(o, 1.0), h1 = uProj * vec4(o + r * len, 1.0);
    float k0 = 1.0 / h0.w, k1 = 1.0 / h1.w;
    vec2 p0 = (h0.xy * k0 * 0.5 + 0.5) * size, p1 = (h1.xy * k1 * 0.5 + 0.5) * size, d = p1 - p0;
    bvec2 moves = greaterThan(abs(d), vec2(1e-4));
    vec2 invD = 1.0 / mix(vec2(1.0), d, moves), dirS = step(0.0, d);
    vec2 tAx = mix(vec2(1.0), (mix(vec2(-1e-3), size + 1e-3, dirS) - p0) * invD, moves);
    float tEnd = clamp(min(tAx.x, tAx.y), 0.0, 1.0);
    float px = max(abs(d.x), abs(d.y));
    if (px * tEnd < 1.0) return false;
    // in t, a hundredth of a pixel: what nudges a step across the cell wall it stopped on
    float tEps = 0.01 / px;
    // (1/w is linear in screen space: the ray's inverse depth at t is mix(k0, k1, t))
    vec2 tx = mix(vec2(1e30), (floor(p0) + dirS - p0) * invD, moves);
    float t = min(tx.x, tx.y) + tEps;   // out of the pixel's own texel first
    int iter = int(uSsr.y), lv = max(fine, 1);
    for (int i = 0; i < iter && t < tEnd; i++)
    {
        float cs = float(1 << lv);
        vec2 cell = floor((p0 + d * t) / cs);
        tx = mix(vec2(1e30), ((cell + dirS) * cs - p0) * invD, moves);
        float tExit = min(min(tx.x, tx.y), tEnd);
        float kS = texelFetch(uHiZ, ivec2(cell), lv).r;
        float kA = mix(k0, k1, t), kB = mix(k0, k1, tExit);
        // in front of the closest surface across the whole cell: skip it, and try a coarser cell next
        if (min(kA, kB) > kS) { t = tExit + tEps; lv = min(lv + 1, top); continue; }
        // the ray reaches that surface inside the cell: move to where, if it is still ahead
        float tc = abs(k1 - k0) > 1e-9 ? (kS - k0) / (k1 - k0) : t;
        if (kA > kS) t = clamp(tc, t, tExit);
        if (lv > fine) { lv--; continue; }
        float ray = 1.0 / mix(k0, k1, t);
        if (ray - 1.0 / kS < max(CX_SSR_THICK, CX_SSR_THICKREL * ray) && !Behind(ivec2(p0 + d * t), r))
        {
            ph = p0 + d * t;
            return true;
        }
        t = tExit + tEps;   // behind it, or its back: walk on at this level
    }
    return false;
}

void main()
{
    frag = vec4(0.0);
    int blk = 1 << int(uSsr.w), cells = blk * blk;
    ivec2 lim = textureSize(uHiZ, 0) - 1, base = ivec2(gl_FragCoord.xy) * blk, px = ivec2(-1);
    vec2 u = CxSsrNoise(ivec2(gl_FragCoord.xy), uFrame);
    vec3 n = vec3(0.0, 0.0, 1.0);
    float rough = 1.0;
    // the block's pixels from a drawn start: the first that reflects at all traces
    int first = int(u.x * float(cells));
    for (int j = 0; j < cells; j++)
    {
        int c = (first + j) % cells;
        ivec2 q = min(base + ivec2(c % blk, c / blk), lim);
        vec3 qn, qw; float qr;
        if (!CxRecRead(uRecN, uRecW, uHiZ, q, qn, qr, qw)) continue;
        if (CxSsrGate(max(qw.r, max(qw.g, qw.b)), qr, uSsr.x) <= 0.0) continue;
        px = q; n = qn; rough = qr;
        break;
    }
    if (px.x < 0) return;
    vec2 size = vec2(textureSize(uHiZ, 0));
    vec3 o = ViewPos((vec2(px) + 0.5) / size, texelFetch(uDepth, px, 0).r);
    vec3 N = normalize(mat3(uView) * n), V = -normalize(o);
    // a GGX half vector off the lobe (alpha = the record's roughness), its polar draw leaned toward the centre
    float a = max(rough, 1e-3), e = u.y * (1.0 - CX_SSR_BIAS), phi = 6.2831853 * fract(u.x * float(cells));
    float cosT = sqrt((1.0 - e) / (1.0 + (a * a - 1.0) * e)), sinT = sqrt(max(1.0 - cosT * cosT, 0.0));
    vec3 tx = normalize(cross(abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0), N)), ty = cross(N, tx);
    vec3 H = normalize(tx * (sinT * cos(phi)) + ty * (sinT * sin(phi)) + N * cosT);
    vec3 r = reflect(-V, H);
    // a draw that leaves under the surface takes the mirror direction, the lobe's peak
    if (dot(r, N) <= 0.0) { H = N; r = reflect(-V, N); }
    float NoH = max(dot(N, H), 1e-4), VoH = max(dot(V, H), 1e-4);
    float pdf = CxGgxD(NoH, a) * NoH / (4.0 * VoH);
    float k = texelFetch(uHiZ, px, 0).r;
    vec2 ph;
    frag = Trace(o, r, size, ph) ? vec4(ph / size, pdf, k) : vec4(0.0, 0.0, 0.0, k);
    // a record some other program wrote garbage into can pass the depth test by chance: one NaN here is the
    // whole frame's, through the bloom
    if (any(isnan(frag)) || any(isinf(frag))) frag = vec4(0.0);
}
