// The highlight mask resolved over frames, the way the upscaler resolves the scene: the mask is drawn with the
// jittered camera at the render size, so a single frame's edge steps by up to a render texel between jitter
// phases. Each pixel keeps last frame's resolved coverage, reprojected through the camera and clamped to this
// frame's 3x3 neighbourhood, and blends this frame's in. The composite reads the result instead of the mask.
#version 460 core
in vec2 vUV;
uniform sampler2D uMask;   // this frame's mask, jittered, render size
uniform sampler2D uHist;   // last frame's resolved mask, unjittered, render size
uniform sampler2D uDepth;  // this frame's world depth, jittered
uniform vec2 uUnjitter;    // the mask's camera jitter in uv, read back out
uniform mat4 uInvVP;       // this frame's unjittered camera, inverted
uniform mat4 uPrevVP;      // last frame's unjittered camera
uniform float uKeep;       // the history's weight; 0 when there is none
out vec4 frag;

/// One pixel's resolved coverage: last frame's, held inside this frame's neighbourhood [lo, hi], weighted keep.
vec4 CxHlResolve(vec4 hist, vec4 cur, vec4 lo, vec4 hi, float keep)
{
    return cur;
}

void main()
{
    vec2 ts = 1.0 / vec2(textureSize(uMask, 0));
    vec2 uv = vUV + uUnjitter;
    vec4 cur = texture(uMask, uv);
    vec4 lo = cur, hi = cur;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            vec4 n = texture(uMask, uv + vec2(x, y) * ts);
            lo = min(lo, n);
            hi = max(hi, n);
        }
    vec2 prev = vUV;
    float d = texture(uDepth, uv).r;
    if (d > 0.0)
    {
        vec4 w = uInvVP * vec4(vUV * 2.0 - 1.0, d, 1.0);
        vec4 p = uPrevVP * vec4(w.xyz / w.w, 1.0);
        if (abs(p.w) > 1e-6) prev = p.xy / p.w * 0.5 + 0.5;
    }
    bool inside = all(greaterThanEqual(prev, vec2(0.0))) && all(lessThanEqual(prev, vec2(1.0)));
    frag = CxHlResolve(texture(uHist, prev), cur, lo, hi, inside ? uKeep : 0.0);
}
