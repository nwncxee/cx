#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uHdr;
uniform sampler2D uBloom;
uniform sampler2D uDepth;  // CX-374: the owned depth, reversed; the sky sits at exactly 0.0 (SkyOwnedPass's DepthRange)
uniform int uSkyRaw;      // CX-374: 1 = a sky pixel skips exposure and the tonemap (see main); 0 with no dome drawn
// The fog's horizon band (SceneFog.SkyBand; 0 = none): at and below the horizon a far-depth pixel is fog
// over culled ground, not sky, and is graded like the fogged geometry beside it -- raw, it read as a hole.
uniform float uSkyBand;
// 1 = no dome drew this view: a far pixel is the bare clear colour, which the volumetric fog covers here as a dome's
// own stage would have, so a lamp's glow does not stop at the last silhouette
uniform int uSkyBare;
uniform mat4 uInvVP;      // the main view's inverse view-projection: this pixel's world ray
// uEyePos, and the volumetric fog's volume: with it, the share of a sky pixel's light the fog put there is graded
// like the fogged geometry beside it, and the band stands down
#include "cx_fogdecl.glsl"
#include "cx_vfog.glsl"
uniform float uStrength;
uniform int uMode;
uniform float uKnee;
uniform float uExposure;  // scene-referred gain before the tonemap; 1 = identity
uniform float uAcesMix;   // mode 2 only: ACES mixed against the shoulder, in linear; 1 = full ACES
uniform float uDither;   // one LSB of the output target, or 0
uniform int uViz;        // 0 = the frame; 1 = bloom only; 2 = HDR only; 3 = bloom x20; 4 = motion vectors
// A native stage's overlay (cx_overlay): display-linear light added over sky and scene alike -- after the grade,
// so a lens ghost crossing a ridgeline is one ghost and not two exposures of it.
uniform sampler2D uOverlay;
uniform int uOverlayOn;
#include "cx_postcolor.glsl"
#include "cx_postdither.glsl"
vec3 CxShoulder(vec3 c, float k)
{
    if (k <= 0.0) return c;
    // k = 1 makes m zero and m*t/(m+t) a 0/0 NaN below the knee -- black on NVIDIA. Its limit is a clip.
    if (k >= 1.0) return min(c, vec3(1.0));
    vec3 t = max(c - vec3(k), vec3(0.0));
    // The curve is C1 at the knee and asymptotic to 1, so anything the target could already show is
    // untouched and only the range that used to clip moves. k is a LINEAR value here; the encode
    // below puts the result back in display space.
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

void main()
{
    // The stage views. Deliberately BEFORE the tonemap and the dither: the question they answer is
    // "which stage introduced this", and running the answer through two more stages first is how a
    // diagnostic ends up lying about the thing it was added to find.
    if (uViz == 1 || uViz == 3)
    {
        vec3 b = textureLod(uBloom, vUV, 0.0).rgb * uStrength * (uViz == 3 ? 20.0 : 1.0);
        frag = vec4(CxEncode(b), 1.0);
        return;
    }
    if (uViz == 2) { frag = vec4(CxEncode(texture(uHdr, vUV).rgb), 1.0); return; }
    // #388: the velocity target on the bloom unit. Mid-grey is zero motion; +-32 render pixels of
    // motion is a full swing of red (x) or green (y), so a pan paints one flat colour over static
    // geometry and standing still paints flat grey.
    if (uViz == 4) { frag = vec4(0.5 + textureLod(uBloom, vUV, 0.0).rg / 64.0, 0.5, 1.0); return; }

    vec3 c = texture(uHdr, vUV).rgb;
    if (uSkyBare != 0 && uVFog.x > 0.0 && texture(uDepth, vUV).r <= 0.0) { vec4 v = CxVFogSkyUv(vUV); c = c * v.a + v.rgb; }
    vec3 b = uStrength > 0.0 ? textureLod(uBloom, vUV, 0.0).rgb * uStrength : vec3(0.0);
    // CX-374: the sky is display-referred as its shader authored it (vanilla encodes it straight),
    // and exposure + the tonemap re-graded it ~2x at night. A sky pixel keeps its own value and
    // takes only the EXPOSED bleed on top, so bright things still glow onto it.
    float graded = 1.0;
    vec3 raw = vec3(0.0);
    vec3 overlay = uOverlayOn != 0 ? max(textureLod(uOverlay, vUV, 0.0).rgb, vec3(0.0)) : vec3(0.0);
    if (uSkyRaw != 0 && texture(uDepth, vUV).r <= 0.0)
    {
        vec4 p = uInvVP * vec4(vUV * 2.0 - 1.0, 0.0, 1.0);
        const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
        if (uVFog.x > 0.0) graded = clamp(dot(CxVFogSkyAt(p.xyz / p.w).rgb, lum) / max(dot(c, lum), 1e-6), 0.0, 1.0);
        else if (uSkyBand > 0.0) graded = 1.0 - smoothstep(0.0, uSkyBand, normalize(p.xyz / p.w - uEyePos).z);
        else graded = 0.0;
        vec3 g = b * uExposure;
        raw = CxEncode(c + g + overlay);
        if (graded <= 0.0)
        {
            frag = vec4(CxDither(raw, gl_FragCoord.xy, uDither), texture(uHdr, vUV).a);
            return;
        }
    }
    c += b;
    c *= uExposure;
    if (uMode == 1) c = CxShoulder(c, uKnee);
    // ACES against the SHOULDER, not the raw value: the shoulder is the identity below its knee, so
    // the mix keeps the darks the ACES toe would crush and takes only the roll-off and colour.
    else if (uMode == 2) c = mix(CxShoulder(c, uKnee), CxAces(c), uAcesMix);
    else if (uMode == 3) c = CxNeutral(c);
    c += overlay;
    vec3 outc = CxEncode(c);
    if (graded < 1.0) outc = mix(raw, outc, graded);
    // Dithered in DISPLAY space, after the encode: this is where the frame meets 8 bits (or, when
    // the post stack takes it, uDither is 0 and the stack's last stage dithers instead).
    outc = CxDither(outc, gl_FragCoord.xy, uDither);
    // The alpha channel passes through untouched: the Alloy blended classes wrote real coverage
    // there and the engine's own later passes are entitled to find it, tonemapping or not.
    frag = vec4(outc, texture(uHdr, vUV).a);
}
