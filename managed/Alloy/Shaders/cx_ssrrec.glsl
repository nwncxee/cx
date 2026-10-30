// CX-51: the reflection record's format, in one place for the lit stage that writes it (cx_reflrec) and the Ssr
// passes that read it back (cx_ssrtrace_f, cx_ssrres_f). N = RGBA16F: octahedral world normal, roughness, view
// depth. W = R11F_G11F_B10F: the reflection's weight. 12 bytes a pixel, down from two RGBA16F.
#ifndef CX_SSRREC
#define CX_SSRREC
/// The record weight below which no ray is traced, faded in over this range. Under the dielectric floor (F0 0.04):
/// water seen from above reflects ~2-4%, and a dark figure standing in it still reads against the probe's sky.
/// The roughness cap, not this, is what keeps the march off matte ground.
const vec2 CX_SSR_WEIGHT = vec2(0.005, 0.01);
/// How far (a share of view depth) a record's depth may sit off the frame's before a later draw is judged to have
/// covered it without a record of its own -- a hosted program, the sky -- or the attachment holds nothing.
const float CX_SSR_RECORD = 0.01;

/// A unit normal onto the octahedron, unfolded to [-1, 1]^2.
vec2 CxOctEncode(vec3 n)
{
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 1e-6);
    vec2 p = n.xy;
    if (n.z < 0.0) p = (1.0 - abs(n.yx)) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    return p;
}

vec3 CxOctDecode(vec2 p)
{
    vec3 n = vec3(p, 1.0 - abs(p.x) - abs(p.y));
    float t = max(-n.z, 0.0);
    n.xy += vec2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

/// The record at full-res texel p, if it belongs to what the frame shows there (NaN-safe: garbage fails). hiZ level 0
/// is 1 / view depth.
bool CxRecRead(sampler2D recN, sampler2D recW, sampler2D hiZ, ivec2 p, out vec3 n, out float rough, out vec3 w)
{
    vec4 r = texelFetch(recN, p, 0);
    n = vec3(0.0); rough = 1.0; w = vec3(0.0);
    if (!(abs(r.w * texelFetch(hiZ, p, 0).r - 1.0) < CX_SSR_RECORD)) return false;
    n = CxOctDecode(r.xy);
    rough = r.z;
    w = texelFetch(recW, p, 0).rgb;
    return true;
}

/// How much of a record's reflection the pass may swap in: weight faded in over CX_SSR_WEIGHT, roughness faded out
/// under the cap.
float CxSsrGate(float wmax, float rough, float cap)
{
    return smoothstep(CX_SSR_WEIGHT.x, CX_SSR_WEIGHT.y, wmax) * (1.0 - smoothstep(0.75 * cap, cap, rough));
}
#endif
