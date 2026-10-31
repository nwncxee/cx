#version 460 core
// The reflection's own history, independent of TAA: this frame's resolve (cx_ssrres_f) blended over last frame's
// result, reprojected by the point the reflection SHOWS -- along the view ray, as far again past the surface as the
// hit lies -- so a reflection keeps its place under a moving camera where the surface's own reprojection smears it.
// The history is clamped to this frame's 3x3 neighbourhood, and with none (a reset, off-screen) the resolve shows
// alone. Out: the new history, and the same correction added onto the frame under ONE/ONE.
layout(location = 0) out vec4 hist;
layout(location = 1) out vec4 frag;
uniform sampler2D uRes;      // this frame's resolve: rgb = the correction, a = the hit's distance (0 = the surface)
uniform sampler2D uHist;     // last frame's history
uniform sampler2D uDepth;    // the frame depth
uniform mat4 uInvProj, uInvView, uPrevVp;
uniform int uHistOn;         // 1 when uHist holds last frame's
/// The share of this frame's resolve in the blend: an eighth-ish converges the trace's noise in a few frames.
const float CX_SSR_BLEND = 0.15;
void main()
{
    ivec2 px = ivec2(gl_FragCoord.xy), lim = textureSize(uRes, 0) - 1;
    vec4 cur = texelFetch(uRes, px, 0);
    vec3 res = cur.rgb;
    float depth = texelFetch(uDepth, px, 0).r;
    if (uHistOn != 0 && depth > 0.0)
    {
        vec3 mn = cur.rgb, mx = cur.rgb;
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                vec3 c = texelFetch(uRes, clamp(px + ivec2(x, y), ivec2(0), lim), 0).rgb;
                mn = min(mn, c); mx = max(mx, c);
            }
        vec2 uv = (vec2(px) + 0.5) / vec2(lim + 1);
        vec4 v = uInvProj * vec4(uv * 2.0 - 1.0, depth, 1.0);
        v.xyz /= v.w;
        float d = length(v.xyz);
        vec4 c = uPrevVp * (uInvView * vec4(v.xyz * ((d + max(cur.a, 0.0)) / max(d, 1e-4)), 1.0));
        vec2 huv = c.xy / c.w * 0.5 + 0.5;
        if (c.w > 0.0 && all(greaterThanEqual(huv, vec2(0.0))) && all(lessThanEqual(huv, vec2(1.0))))
            res = mix(clamp(textureLod(uHist, huv, 0.0).rgb, mn, mx), cur.rgb, CX_SSR_BLEND);
    }
    hist = vec4(res, 0.0);
    if (any(isnan(hist)) || any(isinf(hist))) hist = vec4(0.0);
    frag = hist;
}
