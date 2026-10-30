// CX-51: what every reflecting stage shares -- the split-sum weight, its dielectric floor and the radiance cap --
// and what the screen-space pass's stages share: the lobe's bias and distribution, the noise and the view position.
// The pass itself is Ssr (cx_ssrtrace_f, cx_ssrres_f, cx_ssrhist_f): the lit stage writes a reflection record
// (cx_reflrec) and adds the probe; the pass swaps the probe for what the screen shows where it can see it.
#ifndef CX_SSR
#define CX_SSR
/// Brightest radiance a reflection (a screen hit or a probe texel) may carry: one inf texel poisons its mips.
const float CX_SSR_MAX = 16.0;

/// The dielectric reflectance at normal incidence a glTF material assumes (IOR 1.5) -- and the stock
/// fGeneratedSpecularityMin, so a physical and a legacy dielectric start from the same F0.
const float CX_DIELECTRIC_F0 = 0.04;

/// Karis' analytic fit of the split-sum DFG integral: (scale, bias) on F0 for the directional albedo of a
/// GGX lobe. `perceptual` is sqrt(alpha).
vec2 CxEnvBrdf(float NoV, float perceptual)
{
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = perceptual * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a004 + r.zw;
}

/// How far the trace's GGX draw leans toward the lobe's centre (0 = the whole lobe): the tail rays a rough surface
/// would spend on grazing directions mostly miss, and the resolve's cone blur stands in for them.
const float CX_SSR_BIAS = 0.7;

/// Two R2 low-discrepancy values in [0, 1) at pixel p, phased per frame: a closed-form blue-ish pattern, so no
/// noise texture is bound. The second axis swaps the R2 weights rather than offsetting, which would correlate them.
vec2 CxSsrNoise(ivec2 p, int frame)
{
    vec2 q = vec2(p);
    return fract(vec2(dot(q, vec2(0.7548777, 0.5698403)), dot(q, vec2(0.5698403, 0.7548777)))
               + float(frame & 255) * vec2(0.618034, 0.324718));
}

/// The view position at uv and window depth, through the inverse projection.
vec3 CxViewPos(mat4 invProj, vec2 uv, float depth)
{
    vec4 p = invProj * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    return p.xyz / p.w;
}

/// The GGX normal distribution at alpha a.
float CxGgxD(float NoH, float a)
{
    float a2 = a * a, d = NoH * NoH * (a2 - 1.0) + 1.0;
    return a2 / max(3.14159265 * d * d, 1e-8);
}

/// CX-46: the probe's SH9 irradiance, written by cx_probe_sh after each capture with the cosine lobe and basis
/// constants folded in, and the world point the capture stood at. Read only under uProbeMips > 0.
layout(std140, binding = 1) uniform CxProbeSh { vec4 uProbeSh[9]; vec4 uProbeAt; };

/// The probe's radiance along R from world point P, parallax-corrected. The cube's alpha is 1 / the distance to
/// what the capture saw each way, so the lookup turns toward where the ray meets that surface instead of reading
/// R as if everything stood infinitely far, which showed a street's buildings as sky. Two fixed-point steps of a
/// distance impostor (Szirmay-Kalos 2005); the sky's alpha 0 leaves R alone.
vec3 CxProbeFetch(samplerCube probe, vec3 P, vec3 R, float lod)
{
    vec3 p = P - uProbeAt.xyz, l = R;
    float b = -dot(p, R), c = dot(p, p);
    for (int i = 0; i < 2; i++)
    {
        float dist = 1.0 / max(textureLod(probe, l, lod).a, 1e-4);
        l = p + R * (b + sqrt(max(b * b - c + dist * dist, 0.0)));
    }
    return min(textureLod(probe, l, lod).rgb, vec3(CX_SSR_MAX));
}
#endif
