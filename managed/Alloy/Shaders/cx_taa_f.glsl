#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uCur;      // this frame, display space, the stack's size
uniform sampler2D uHist;     // last frame's history, LINEAR
uniform sampler2D uVel;      // #388: prev - cur, render pixels, render size
uniform sampler2D uDepth;    // the frame depth, render size: the dilation's closest-depth pick
uniform float uBlend;        // the current frame's share at rest; 1 = no history
uniform vec2 uJitterPx;      // this frame's projection jitter in RENDER pixels
uniform int uUpsample;       // 1: uCur is render-sized, the history and this draw are display-sized

#include "cx_taa_lib.glsl"

// The velocity of the CLOSEST fragment in the 3x3: the neighbour standing in front of this pixel is
// the one whose motion its edge follows. An unresolved depth target reads all-equal and keeps the
// centre tap, so the dilation degrades to the plain fetch rather than to a corner.
vec2 CxDilatedVel(vec2 uv, vec2 vsz)
{
    vec2 texel = 1.0 / vsz, best = vec2(0.0);
    float bestD = textureLod(uDepth, uv, 0.0).r;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            vec2 o = vec2(x, y) * texel;
            float d = textureLod(uDepth, uv + o, 0.0).r;
            if (d < bestD) { bestD = d; best = o; }
        }
    return textureLod(uVel, uv + best, 0.0).rg;
}

void main()
{
    // from vUV, not gl_FragCoord: the stack draws at the DISPLAY viewport, whose origin need not be 0
    ivec2 sz = textureSize(uCur, 0);
    vec2 hsz = vec2(textureSize(uHist, 0));
    ivec2 pix;
    vec4 c;
    float conf = 1.0;
    if (uUpsample != 0)
    {
        // this display pixel's clean render position, shifted to where THIS frame drew it: a texel
        // at render position t holds the scene at t - jitter, so the clean point sits at t + jitter
        vec2 sp = vUV * vec2(sz) + uJitterPx;
        pix = clamp(ivec2(floor(sp)), ivec2(0), sz - 1);
        c = textureLod(uCur, sp / vec2(sz), 0.0);
        // how close the frame's sample fell to this pixel: at a texel centre the frame speaks fully,
        // between two texels the bilinear read is a blur and the history keeps more of its say
        conf = 1.0 - smoothstep(0.0, 0.7071, length(fract(sp) - 0.5));
    }
    else
    {
        pix = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
        c = texelFetch(uCur, pix, 0);
    }
    vec3 cy = CxToYCoCg(c.rgb);
    vec3 m1 = vec3(0.0), m2 = vec3(0.0);
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            vec3 n = CxToYCoCg(texelFetch(uCur, clamp(pix + ivec2(x, y), ivec2(0), sz - 1), 0).rgb);
            m1 += n; m2 += n * n;
        }
    vec3 mean = m1 / 9.0;
    vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));
    // the current sample is always inside its own box: a lone bright texel would otherwise clip
    // itself away and the resolve would never converge on it
    vec3 mn = min(mean - CX_TAA_GAMMA * sigma, cy), mx = max(mean + CX_TAA_GAMMA * sigma, cy);
    vec2 vsz = vec2(textureSize(uVel, 0));
    vec2 velPx = CxDilatedVel(vUV, vsz);
    vec2 huv = vUV + velPx / vsz;
    // a moving pixel takes more of the frame -- a walking camera converges in a few frames, not ten
    float a = uBlend >= 1.0 ? 1.0 : min(0.5, uBlend + length(velPx) * 0.05);
    if (uUpsample != 0) a = mix(a * 0.25, a, conf);
    if (any(lessThan(huv, vec2(0.0))) || any(greaterThan(huv, vec2(1.0)))) a = 1.0;
    vec3 hy = CxClipToBox(mn, mx, CxToYCoCg(CxHistory(huv, hsz)));
    vec3 h = CxFromYCoCg(hy);
    // inverse-luma weights (Karis): the brighter of the two speaks quieter, so a firefly that lives
    // one frame cannot outvote the history it lands on
    float wc = a / (1.0 + max(cy.x, 0.0)), wh = (1.0 - a) / (1.0 + max(hy.x, 0.0));
    frag = vec4((c.rgb * wc + h * wh) / max(wc + wh, 1e-5), c.a);
}
