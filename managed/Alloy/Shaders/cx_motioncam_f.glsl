#version 460 core
in vec2 vUV;
out vec2 frag;
uniform sampler2D uDepth;
uniform mat4 uInvVP;     // this frame's inverse view-projection
uniform mat4 uPrevVP;    // last frame's view-projection
uniform vec2 uSize;      // the render size, pixels
uniform vec2 uJitter;    // #389: this frame's sub-pixel jitter, NDC (0 with TAA off)

void main()
{
    float d = texture(uDepth, vUV).r;
    vec4 ndc = vec4(vUV * 2.0 - 1.0, d, 1.0);
    vec4 prev = uPrevVP * (uInvVP * ndc);
    if (abs(prev.w) < 1e-6) { frag = vec2(0.0); return; }
    vec2 v = (prev.xy / prev.w - (ndc.xy - uJitter)) * 0.5 * uSize;
    if (any(isnan(v)) || any(isinf(v))) v = vec2(0.0);
    frag = clamp(v, -uSize, uSize);
}
