#ifndef CX_WX_DROP
#define CX_WX_DROP
// A rain drop as the water bead it is: a sphere a few millimetres across, reflecting the sky round its rim, showing
// the sky behind it bent and turned over through its body, and catching a lamp or the sun as one glint on the side
// facing it. The drop and the crown droplets share it. Self-contained: no uniform, no include, so the GPU goldens
// run it as it ships, and a hak override of this file restyles every drop.

/// A rain drop's diameter, metres.
const float CX_WX_DROP_M = 0.003;
/// Seconds of fall one drop smears over at motion blur 1: a camera's shutter at 60 frames a second.
const float CX_WX_SHUTTER = 1.0 / 60.0;
/// Water's reflectance looking straight at it.
const float CX_WX_WATER_F0 = 0.02;
/// Water's refractive index against air.
const float CX_WX_WATER_IOR = 1.33;
/// The share of the scene behind it a drop hides: what its body bends away from the straight line through it.
const float CX_WX_BEAD_EXT = 0.15;
/// The glint's microfacet alpha: a falling drop wobbles and spans a few pixels, so a lamp is a small soft spot on
/// it rather than a sub-pixel point that would sparkle on and off.
const float CX_WX_GLINT_ALPHA = 0.1;
/// How tightly a light seen THROUGH the drop focuses: a cosine power over the angle off its bent view.
const float CX_WX_CAUSTIC_POW = 2000.0;
/// The share of a light a drop's lit side sends back by bouncing it round inside: what makes a drop in a lamp's beam
/// glow as a whole and not only at its glint. Water's own, as the earlier drop shading took it.
const float CX_WX_BEAD_GLOW = 0.25;

/// One drop's extent on screen at <c>pxPerM</c> pixels per metre, falling <c>speed</c> m/s under the motion blur
/// setting <c>blur</c> (0 none, 1 a 60 fps shutter, 2 at most): x metres along its fall, y across, both at least a pixel, and z
/// the share of that quad the drop itself covers, so a widened drop dims by what it was widened by.
vec3 CxWxDropSize(float pxPerM, float speed, float blur)
{
    float along = CX_WX_DROP_M + clamp(blur, 0.0, 2.0) * speed * CX_WX_SHUTTER;
    float px = 1.0 / max(pxPerM, 1e-3);
    float l = max(along, px), w = max(CX_WX_DROP_M, px);
    return vec3(l, w, (along * CX_WX_DROP_M) / (l * w));
}

/// The sky seen along <c>d</c>: <c>up</c> overhead, <c>low</c> at and under the horizon.
vec3 CxWxSkyAlong(vec3 d, vec3 up, vec3 low)
{
    return mix(low, up, clamp(d.z, 0.0, 1.0));
}

/// GGX's normal distribution at <c>nh</c>, the glint's lobe.
float CxWxGlint(float nh)
{
    float a2 = CX_WX_GLINT_ALPHA * CX_WX_GLINT_ALPHA;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    return a2 / (3.14159265 * d * d);
}

/// A light <c>col</c> reaching the drop from <c>L</c> as the eye at <c>V</c> sees it off surface normal <c>N</c>: its
/// mirror glint, Fresnel-weighted, and the light seen through the drop along the bent view <c>T</c>.
vec3 CxWxBeadLight(vec3 N, vec3 V, vec3 T, float fresnel, vec3 L, vec3 col)
{
    float nl = dot(N, L), nv = max(dot(N, V), 1e-3);
    vec3 H = normalize(L + V);
    float vh = clamp(dot(V, H), 0.0, 1.0);
    float f = CX_WX_WATER_F0 + (1.0 - CX_WX_WATER_F0) * pow(1.0 - vh, 5.0);
    float mirror = nl > 0.0 ? CxWxGlint(max(dot(N, H), 0.0)) * f / (4.0 * nv) : 0.0;
    float through = (1.0 - fresnel) * pow(max(dot(T, L), 0.0), CX_WX_CAUSTIC_POW);
    return col * (mirror + through + CX_WX_BEAD_GLOW * max(nl, 0.0));
}

/// A drop as premultiplied colour at <c>uv</c> on its quad (the unit disc is the bead), seen along <c>V</c> (toward
/// the eye) with the quad's <c>right</c> and <c>up</c>: the sky (<c>skyUp</c> overhead, <c>skyLow</c> at the horizon)
/// reflected and refracted, one lamp's light <c>lampCol</c> from <c>lampDir</c> and the sun's <c>sunCol</c> from
/// <c>sunDir</c>. Alpha is the share of the scene behind it the drop hides.
vec4 CxWxBead(vec2 uv, vec3 V, vec3 right, vec3 up, vec3 skyUp, vec3 skyLow, vec3 lampDir, vec3 lampCol, vec3 sunDir, vec3 sunCol)
{
    float r2 = dot(uv, uv);
    if (r2 >= 1.0) return vec4(0.0);
    float z = sqrt(1.0 - r2);
    vec3 across = right * uv.x + up * uv.y;
    vec3 N = normalize(across + V * z);
    float fresnel = CX_WX_WATER_F0 + (1.0 - CX_WX_WATER_F0) * pow(1.0 - z, 5.0);
    vec3 R = reflect(-V, N);
    // a ball lens: a ray at impact parameter b leaves bent by twice its refraction's loss, toward the far side
    float b = sqrt(r2), bend = 2.0 * (asin(b) - asin(b / CX_WX_WATER_IOR));
    vec3 away = b > 1e-4 ? -across / b : vec3(0.0);
    vec3 T = normalize(-V * cos(bend) + away * sin(bend));
    vec3 c = fresnel * CxWxSkyAlong(R, skyUp, skyLow) + (1.0 - fresnel) * CX_WX_BEAD_EXT * CxWxSkyAlong(T, skyUp, skyLow);
    c += CxWxBeadLight(N, V, T, fresnel, lampDir, lampCol) + CxWxBeadLight(N, V, T, fresnel, sunDir, sunCol);
    // ...and the sky's own light, bounced round inside as a lamp's is
    c += CX_WX_BEAD_GLOW * CxWxSkyAlong(N, skyUp, skyLow);
    return vec4(c, CX_WX_BEAD_EXT);
}
#endif
