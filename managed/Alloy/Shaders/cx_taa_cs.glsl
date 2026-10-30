#version 460 core
// The taa stage in one dispatch: cx_taa_f's accumulate over a 16x16 tile and a one-texel border, the tile's new
// history stored, then cx_taastage_f's copy-out with its sharpen and the dither into the stage's target. The current
// frame and depth around the tile are read once into shared memory; the sharpen reads the resolve rounded to the
// history's half float, which is what the two-pass stage reads back. The stage is the stack's last, so its target
// is the 8-bit composite.
layout(local_size_x = 16, local_size_y = 16) in;
layout(binding = 0) uniform sampler2D uCur;
layout(binding = 1) uniform sampler2D uHist;
layout(binding = 2) uniform sampler2D uVel;
layout(binding = 3) uniform sampler2D uDepth;
layout(rgba16f, binding = 0) writeonly uniform image2D uHistOut;
layout(rgba8, binding = 1) writeonly uniform image2D uOut;
layout(push_constant) uniform CxTaa { vec2 uJitterPx; ivec2 uOutAt; float uBlend; float uSharp; float uDither; int uUpsample; };

#include "cx_taa_lib.glsl"
#include "cx_postdither.glsl"

// the tile, the resolved region (one texel of border for the sharpen), the loaded one (one more for the 3x3)
const int CX_T = 16, CX_R = CX_T + 2, CX_N = CX_T + 4;
shared vec4 sCur[CX_N * CX_N];
shared float sDepth[CX_N * CX_N];
shared vec4 sRes[CX_R * CX_R];

ivec2 gCur0, gDep0, gRes0, gCsz, gDsz, gOsz;

vec4 CxCur(ivec2 q)
{
    q = clamp(q, ivec2(0), gCsz - 1);
    ivec2 r = q - gCur0;
    return all(greaterThanEqual(r, ivec2(0))) && all(lessThan(r, ivec2(CX_N))) ? sCur[r.y * CX_N + r.x] : texelFetch(uCur, q, 0);
}

// the nearest tap cx_taa_f's dilation takes; shared memory only at render size, where every tap is a texel centre
float CxDepth(vec2 uv)
{
    if (uUpsample != 0) return textureLod(uDepth, uv, 0.0).r;
    ivec2 q = clamp(ivec2(floor(uv * vec2(gDsz))), ivec2(0), gDsz - 1);
    ivec2 r = q - gDep0;
    return all(greaterThanEqual(r, ivec2(0))) && all(lessThan(r, ivec2(CX_N))) ? sDepth[r.y * CX_N + r.x] : texelFetch(uDepth, q, 0).r;
}

// the render texel display pixel d reads, before the clamp
ivec2 CxPix(ivec2 d)
{
    vec2 uv = (vec2(d) + 0.5) / vec2(gOsz);
    return uUpsample != 0 ? ivec2(floor(uv * vec2(gCsz) + uJitterPx)) : ivec2(uv * vec2(gCsz));
}

vec2 CxDilatedVel(vec2 uv, vec2 vsz)
{
    vec2 texel = 1.0 / vsz, best = vec2(0.0);
    float bestD = CxDepth(uv);
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            vec2 o = vec2(x, y) * texel;
            float d = CxDepth(uv + o);
            if (d < bestD) { bestD = d; best = o; }
        }
    return textureLod(uVel, uv + best, 0.0).rg;
}

vec4 CxResolve(ivec2 d)
{
    vec2 uv = (vec2(d) + 0.5) / vec2(gOsz);
    vec2 hsz = vec2(textureSize(uHist, 0));
    ivec2 pix;
    vec4 c;
    float conf = 1.0;
    if (uUpsample != 0)
    {
        vec2 sp = uv * vec2(gCsz) + uJitterPx;
        pix = clamp(ivec2(floor(sp)), ivec2(0), gCsz - 1);
        c = textureLod(uCur, sp / vec2(gCsz), 0.0);
        conf = 1.0 - smoothstep(0.0, 0.7071, length(fract(sp) - 0.5));
    }
    else
    {
        pix = clamp(ivec2(uv * vec2(gCsz)), ivec2(0), gCsz - 1);
        c = CxCur(pix);
    }
    vec3 cy = CxToYCoCg(c.rgb);
    vec3 m1 = vec3(0.0), m2 = vec3(0.0);
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            vec3 n = CxToYCoCg(CxCur(pix + ivec2(x, y)).rgb);
            m1 += n; m2 += n * n;
        }
    vec3 mean = m1 / 9.0;
    vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));
    vec3 mn = min(mean - CX_TAA_GAMMA * sigma, cy), mx = max(mean + CX_TAA_GAMMA * sigma, cy);
    vec2 vsz = vec2(textureSize(uVel, 0));
    vec2 velPx = CxDilatedVel(uv, vsz);
    vec2 huv = uv + velPx / vsz;
    float a = uBlend >= 1.0 ? 1.0 : min(0.5, uBlend + length(velPx) * 0.05);
    if (uUpsample != 0) a = mix(a * 0.25, a, conf);
    if (any(lessThan(huv, vec2(0.0))) || any(greaterThan(huv, vec2(1.0)))) a = 1.0;
    vec3 hy = CxClipToBox(mn, mx, CxToYCoCg(CxHistory(huv, hsz)));
    vec3 h = CxFromYCoCg(hy);
    float wc = a / (1.0 + max(cy.x, 0.0)), wh = (1.0 - a) / (1.0 + max(hy.x, 0.0));
    return vec4((c.rgb * wc + h * wh) / max(wc + wh, 1e-5), c.a);
}

vec3 CxRes(ivec2 q)
{
    ivec2 r = clamp(q, ivec2(0), gOsz - 1) - gRes0;
    return sRes[r.y * CX_R + r.x].rgb;
}

void main()
{
    gCsz = textureSize(uCur, 0);
    gDsz = textureSize(uDepth, 0);
    gOsz = imageSize(uHistOut);
    ivec2 t0 = ivec2(gl_WorkGroupID.xy) * CX_T;
    gRes0 = t0 - 1;
    gCur0 = CxPix(gRes0) - 1;
    gDep0 = ivec2(floor((vec2(gRes0) + 0.5) / vec2(gOsz) * vec2(gDsz))) - 1;
    for (int i = int(gl_LocalInvocationIndex); i < CX_N * CX_N; i += CX_T * CX_T)
    {
        ivec2 q = ivec2(i % CX_N, i / CX_N);
        sCur[i] = texelFetch(uCur, clamp(gCur0 + q, ivec2(0), gCsz - 1), 0);
        sDepth[i] = texelFetch(uDepth, clamp(gDep0 + q, ivec2(0), gDsz - 1), 0).r;
    }
    barrier();
    for (int i = int(gl_LocalInvocationIndex); i < CX_R * CX_R; i += CX_T * CX_T)
    {
        ivec2 d = gRes0 + ivec2(i % CX_R, i / CX_R);
        if (any(lessThan(d, ivec2(0))) || any(greaterThanEqual(d, gOsz))) continue;
        vec4 r = CxResolve(d);
        ivec2 rel = d - t0;
        if (all(greaterThanEqual(rel, ivec2(0))) && all(lessThan(rel, ivec2(CX_T)))) imageStore(uHistOut, d, r);
        sRes[i] = vec4(unpackHalf2x16(packHalf2x16(r.rg)), unpackHalf2x16(packHalf2x16(r.ba)));
    }
    barrier();
    ivec2 d = t0 + ivec2(gl_LocalInvocationID.xy);
    if (any(greaterThanEqual(d, gOsz))) return;
    vec3 h = CxRes(d);
    if (uSharp > 0.0)
    {
        vec3 n = CxRes(d + ivec2(1, 0)) + CxRes(d - ivec2(1, 0)) + CxRes(d + ivec2(0, 1)) + CxRes(d - ivec2(0, 1));
        h = max(h + (h * 4.0 - n) * (uSharp * 0.25), vec3(0.0));
    }
    float alpha = textureLod(uCur, (vec2(d) + 0.5) / vec2(gOsz), 0.0).a;
    ivec2 o = uOutAt + d;
    imageStore(uOut, o, vec4(CxDither(h, vec2(o) + 0.5, uDither), alpha));
}
