#version 460 core
// The sky coverage the resolve grades an FSR frame by: at display size, how much of each pixel FSR built from sky.
// This frame's share is read where the jitter laid it and joins the reprojected history the way FSR's colour does.
in vec2 vUV;
out float frag;
uniform sampler2D uDepth;   // this frame's world depth, render size, reversed; the sky sits at exactly 0.0
uniform sampler2D uHist;    // last frame's coverage, display size
uniform sampler2D uVel;     // render pixels, prev - cur
uniform vec2 uRenderSize;   // the render size, pixels
uniform vec2 uJitterPx;     // this frame's jitter, render pixels
uniform float uBlend;       // this frame's share: 1 starts the history over

float CxSkyAt(ivec2 p)
{
    ivec2 hi = ivec2(uRenderSize) - 1;
    return texelFetch(uDepth, clamp(p, ivec2(0), hi), 0).r <= 0.0 ? 1.0 : 0.0;
}

void main()
{
    // a render texel holds the scene a jitter's width behind its centre, so the unjittered point sits a jitter ahead
    vec2 p = vUV * uRenderSize + uJitterPx - 0.5;
    ivec2 i = ivec2(floor(p));
    vec2 f = p - vec2(i);
    float cur = mix(mix(CxSkyAt(i), CxSkyAt(i + ivec2(1, 0)), f.x),
                    mix(CxSkyAt(i + ivec2(0, 1)), CxSkyAt(i + ivec2(1, 1)), f.x), f.y);
    float hist = texture(uHist, vUV + texture(uVel, vUV).rg / uRenderSize).r;
    frag = mix(hist, cur, uBlend);
}
