// The frame's tone curves, in one place: the resolve grades the frame with CxTonemap and the reactive mask
// measures the change the last four passes made through the same call, so the two cannot drift.
#ifndef CX_TONEMAP
#define CX_TONEMAP
vec3 CxShoulder(vec3 c, float k)
{
    if (k <= 0.0) return c;
    // k = 1 makes m zero and m*t/(m+t) a 0/0 NaN below the knee -- black on NVIDIA. Its limit is a clip.
    if (k >= 1.0) return min(c, vec3(1.0));
    vec3 t = max(c - vec3(k), vec3(0.0));
    // The curve is C1 at the knee and asymptotic to 1, so anything the target could already show is
    // untouched and only the range that used to clip moves. k is a LINEAR value here; the encode
    // the caller applies puts the result back in display space.
    float m = 1.0 - k;
    return min(c, vec3(k)) + m * t / (m + t);
}

vec3 CxAces(vec3 c)
{
    const float a = 2.51, b = 0.03, d = 2.43, e = 0.59, f = 0.14;
    return clamp((c * (a * c + b)) / (c * (d * c + e) + f), 0.0, 1.0);
}

// CX-47: Khronos PBR Neutral (KhronosGroup/ToneMapping, PBR_Neutral), as the reference writes it. Identity below 0.76 apart from a small toe
// offset, then a hue-preserving roll-off to 1 that desaturates only what it compresses.
vec3 CxNeutral(vec3 c)
{
    const float start = 0.8 - 0.04, desat = 0.15;
    float x = min(c.r, min(c.g, c.b));
    c -= x < 0.08 ? x - 6.25 * x * x : 0.04;
    float peak = max(c.r, max(c.g, c.b));
    if (peak < start) return c;
    const float d = 1.0 - start;
    float np = 1.0 - d * d / (peak + d - start);
    c *= np / peak;
    return mix(c, vec3(np), 1.0 - 1.0 / (desat * (peak - np) + 1.0));
}

// The curve uMode picks, as the resolve picks it: 1 the shoulder, 2 ACES mixed against that shoulder,
// 3 PBR Neutral, anything else the linear value untouched.
vec3 CxTonemap(vec3 c, int mode, float knee, float acesMix)
{
    if (mode == 1) return CxShoulder(c, knee);
    // ACES against the SHOULDER, not the raw value: the shoulder is the identity below its knee, so
    // the mix keeps the darks the ACES toe would crush and takes only the roll-off and colour.
    if (mode == 2) return mix(CxShoulder(c, knee), CxAces(c), acesMix);
    if (mode == 3) return CxNeutral(c);
    return c;
}
#endif
