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
// alloy.tileblend: the frame's colour chain (half size), this frame's view-projection, and (strength, band metres, tile metres)
uniform sampler2D uColor;
uniform mat4 uSeamVp;
uniform vec4 uSeam;
/// The mirrored point's floor must sit this close (metres) to the pixel's own: ground across the line, not a wall or a figure.
const float CX_SEAM_LEVEL = 0.15;
/// A ground pixel's normal z at least, rebuilt from depth.
const float CX_SEAM_UP = 0.85;
/// How far the far side's brightness may be pulled toward this side's: textures differ in tone, light should not cross.
const vec2 CX_SEAM_LIGHT = vec2(0.6, 1.6);

vec3 CxSeamWorld(ivec2 p, vec2 size)
{
    vec2 uv = (vec2(p) + 0.5) / size;
    vec4 v = uInvProj * vec4(uv * 2.0 - 1.0, texelFetch(uDepth, p, 0).r, 1.0);
    return (uInvView * vec4(v.xyz / v.w, 1.0)).xyz;
}

float CxSeamNoise(float t)
{
    float i = floor(t), f = fract(t);
    float a = fract(sin(i * 127.1) * 43758.5453), b = fract(sin((i + 1.0) * 127.1) * 43758.5453);
    return mix(a, b, f * f * (3.0 - 2.0 * f));
}

/// alloy.tileblend: NWN's tiles meet on a 10 m grid, and two different ground textures meet along it as a ruled line. A
/// ground pixel near that line takes the far side's colour as seen at its mirror image, over a band its own noise bends,
/// at the colour chain's coarse level -- its own fine detail stays. The correction the frame's ONE/ONE blend adds.
vec3 CxSeamBlend(ivec2 px, vec2 size)
{
    if (uSeam.x <= 0.0 || texelFetch(uDepth, px, 0).r <= 0.0) return vec3(0.0);
    vec3 P = CxSeamWorld(px, size);
    vec3 dx = CxSeamWorld(min(px + ivec2(1, 0), ivec2(size) - 1), size) - P, dy = CxSeamWorld(min(px + ivec2(0, 1), ivec2(size) - 1), size) - P;
    vec3 N = normalize(cross(dx, dy));
    if (abs(N.z) < CX_SEAM_UP) return vec3(0.0);
    // the nearer grid line, its distance and the coordinate running along it
    vec2 cell = P.xy / uSeam.z, off = (cell - floor(cell + 0.5)) * uSeam.z;
    bool onX = abs(off.x) < abs(off.y);
    float d = onX ? off.x : off.y, along = onX ? P.y : P.x;
    float band = uSeam.y * (0.55 + 0.9 * CxSeamNoise(along * 0.9) - 0.25 * CxSeamNoise(along * 3.1));
    if (abs(d) >= band) return vec3(0.0);
    vec3 M = P;
    if (onX) M.x -= 2.0 * d; else M.y -= 2.0 * d;
    vec4 c = uSeamVp * vec4(M, 1.0);
    if (c.w <= 0.0) return vec3(0.0);
    vec2 muv = c.xy / c.w * 0.5 + 0.5;
    if (any(lessThan(muv, vec2(0.0))) || any(greaterThan(muv, vec2(1.0)))) return vec3(0.0);
    // the far side's own floor must be where the mirror says: across the line on the same ground
    vec3 F = CxSeamWorld(clamp(ivec2(muv * size), ivec2(0), ivec2(size) - 1), size);
    if (abs(F.z - P.z) > CX_SEAM_LEVEL || distance(F.xy, M.xy) > 0.5) return vec3(0.0);
    vec2 uv = (vec2(px) + 0.5) / size;
    float w = 0.5 * (1.0 - smoothstep(0.0, band, abs(d))) * uSeam.x;
    vec3 far = textureLod(uColor, muv, 0.0).rgb, own = textureLod(uColor, uv, 0.0).rgb;
    // the far side's colour at about this side's brightness: its hue and texture cross the line, a shadow lying over
    // there (or a lit patch) does not, so nothing mirrors
    const vec3 lw = vec3(0.2126, 0.7152, 0.0722);
    far *= clamp(dot(own, lw) / max(dot(far, lw), 1e-4), CX_SEAM_LIGHT.x, CX_SEAM_LIGHT.y);
    return (far - own) * w;
}
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
    vec3 seam = CxSeamBlend(px, vec2(lim + 1));
    if (!any(isnan(seam)) && !any(isinf(seam))) frag.rgb += seam;
}
