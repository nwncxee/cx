/// CX-46: the probe's SH9 irradiance, written by cx_probe_sh after each capture with the cosine lobe and basis
/// constants folded in, and the world point the capture stood at. Read only under uProbeMips > 0.
layout(std140, binding = 1) uniform CxProbeSh { vec4 uProbeSh[9]; vec4 uProbeAt; };

/// The probe's directional shape over the area ambient: the SH irradiance toward N over the mean luminance of
/// N and -N, in [0, 2] by luminance. Normalised against its own opposite, so the authored ambient keeps its
/// level and gains a direction and a colour -- sky above, ground below, the lit wall beside. The colour is
/// capped at 2x per channel: a saturated capture must tint the ambient, not multiply one channel tenfold.
vec3 CxAmbientShape(vec3 N)
{
    vec3 even = uProbeSh[0].rgb + uProbeSh[4].rgb * (N.x * N.y) + uProbeSh[5].rgb * (N.y * N.z)
              + uProbeSh[6].rgb * (3.0 * N.z * N.z - 1.0) + uProbeSh[7].rgb * (N.x * N.z)
              + uProbeSh[8].rgb * (N.x * N.x - N.y * N.y);
    vec3 odd = uProbeSh[1].rgb * N.y + uProbeSh[2].rgb * N.z + uProbeSh[3].rgb * N.x;
    vec3 a = max(even + odd, vec3(0.0)), b = max(even - odd, vec3(0.0));
    const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
    float la = dot(a, lum);
    return min(a / max(la, 1e-4), vec3(2.0)) * (2.0 * la / max(la + dot(b, lum), 1e-4));
}
