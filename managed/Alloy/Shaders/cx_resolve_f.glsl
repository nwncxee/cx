#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uHdr;
uniform sampler2D uBloom;
uniform sampler2D uDepth;  // CX-374: the owned depth, reversed; the sky sits at exactly 0.0 (SkyOwnedPass's DepthRange)
uniform int uSkyRaw;      // CX-374: 1 = a sky pixel skips exposure and the tonemap (see main); 0 with no dome drawn
uniform int uSkyCover;    // 1 = uDepth holds the sky's coverage of the pixel, 0..1, laid on an FSR frame's colour
uniform int uSkyFogged;   // 1 = the volumetric fog already lies on bare sky: an FSR frame's, laid before FSR (cx_skyfog_f)
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
#include "cx_tonemap.glsl"

// How much of this pixel is sky: the depth's answer, or the coverage FSR's frame came with.
float CxSky()
{
    float d = texture(uDepth, vUV).r;
    return uSkyCover != 0 ? d : (d <= 0.0 ? 1.0 : 0.0);
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
    float sky = CxSky();
    if (uSkyBare != 0 && uSkyFogged == 0 && CxVFogOn() && sky > 0.0) { vec4 v = CxVFogSkyUv(vUV); c = mix(c, c * v.a + v.rgb, sky); }
    vec3 b = uStrength > 0.0 ? textureLod(uBloom, vUV, 0.0).rgb * uStrength : vec3(0.0);
    // CX-374: the sky is display-referred as its shader authored it (vanilla encodes it straight),
    // and exposure + the tonemap re-graded it ~2x at night. A sky pixel keeps its own value and
    // takes only the EXPOSED bleed on top, so bright things still glow onto it.
    float graded = 1.0;
    vec3 raw = vec3(0.0);
    vec3 overlay = uOverlayOn != 0 ? max(textureLod(uOverlay, vUV, 0.0).rgb, vec3(0.0)) : vec3(0.0);
    if (uSkyRaw != 0 && sky > 0.0)
    {
        vec4 p = uInvVP * vec4(vUV * 2.0 - 1.0, 0.0, 1.0);
        const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
        // the volume's share where this pass reads one, and the dome's own band wherever that band still
        // draws -- which, under the rain veil alone, is both
        graded = CxVFogOn() ? clamp(dot(CxVFogSkyAt(p.xyz / p.w).rgb, lum) / max(dot(c, lum), 1e-6), 0.0, 1.0) : 0.0;
        if (!CxVFogWhole() && uSkyBand > 0.0)
            graded = 1.0 - (1.0 - graded) * smoothstep(0.0, uSkyBand, normalize(p.xyz / p.w - uEyePos).z);
        // a pixel only part sky takes the sky's own grade for that part alone
        graded = 1.0 - sky * (1.0 - graded);
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
    c = CxTonemap(c, uMode, uKnee, uAcesMix);
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
