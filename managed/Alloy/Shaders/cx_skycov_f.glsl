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

void main()
{
    // a render texel holds the scene a jitter's width behind its centre, so the unjittered point sits a jitter ahead
    ivec2 at = clamp(ivec2(floor(vUV * uRenderSize + uJitterPx)), ivec2(0), ivec2(uRenderSize) - 1);
    float cur = texelFetch(uDepth, at, 0).r <= 0.0 ? 1.0 : 0.0;
    float hist = texture(uHist, vUV + texture(uVel, vUV).rg / uRenderSize).r;
    // a fresh start never reads the history: an image no frame wrote can hold NaN, which a zero weight keeps
    frag = uBlend >= 1.0 ? cur : mix(hist, cur, uBlend);
}
