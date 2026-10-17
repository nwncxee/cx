// CX-51: what every reflecting stage shares -- the split-sum weight, its dielectric floor and the radiance cap. The
// screen-space trace itself is a pass of its own (Ssr, cx_ssrtrace_f): the lit stage writes a reflection record
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
