#version 460 core
// One bloom level as a dispatch: cx_bloomdown_f's 13-tap downsample (the Karis average and the soft-knee threshold
// on the first level), or cx_bloomup_f's tent upsample added onto the level it writes. The same taps at the same
// coordinates, so the chain reads as the fragment one does.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 0) uniform sampler2D uSrc;
layout(rgba16f, binding = 0) uniform image2D uDst;
layout(push_constant) uniform CxBloom { vec2 uTexel; vec2 uThresh; float uExposure; int uFirst; int uUp; };

vec2 gUv;

vec3 T(vec2 o) { return textureLod(uSrc, gUv + o * uTexel, 0.0).rgb; }

vec3 CxDown()
{
    vec3 a = T(vec2(-2.0,  2.0)), b = T(vec2( 0.0,  2.0)), c = T(vec2( 2.0,  2.0));
    vec3 d = T(vec2(-2.0,  0.0)), e = T(vec2( 0.0,  0.0)), f = T(vec2( 2.0,  0.0));
    vec3 g = T(vec2(-2.0, -2.0)), h = T(vec2( 0.0, -2.0)), i = T(vec2( 2.0, -2.0));
    vec3 j = T(vec2(-1.0,  1.0)), k = T(vec2( 1.0,  1.0));
    vec3 l = T(vec2(-1.0, -1.0)), m = T(vec2( 1.0, -1.0));
    vec3 grp[5] = vec3[5]((j + k + l + m) * 0.25, (a + b + d + e) * 0.25, (b + c + e + f) * 0.25,
                          (d + e + g + h) * 0.25, (e + f + h + i) * 0.25);
    const float w[5] = float[5](0.5, 0.125, 0.125, 0.125, 0.125);
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    for (int n = 0; n < 5; n++)
    {
        float kw = uFirst != 0 ? w[n] / (1.0 + dot(grp[n], vec3(0.2126, 0.7152, 0.0722))) : w[n];
        sum += grp[n] * kw;
        wsum += kw;
    }
    vec3 col = sum / max(wsum, 1e-6);
    if (uFirst != 0)
    {
        float br = max(col.r, max(col.g, col.b)) * uExposure;
        float soft = clamp(br - uThresh.x + uThresh.y, 0.0, 2.0 * uThresh.y);
        soft = soft * soft / (4.0 * uThresh.y);
        col *= max(soft, br - uThresh.x) / max(br, 1e-6);
    }
    return max(col, vec3(0.0));
}

vec3 CxUp()
{
    return (T(vec2(-1.0,  1.0)) + T(vec2(0.0,  1.0)) * 2.0 + T(vec2(1.0,  1.0))
          + T(vec2(-1.0,  0.0)) * 2.0 + T(vec2(0.0, 0.0)) * 4.0 + T(vec2(1.0, 0.0)) * 2.0
          + T(vec2(-1.0, -1.0)) + T(vec2(0.0, -1.0)) * 2.0 + T(vec2(1.0, -1.0))) * (1.0 / 16.0);
}

void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy), sz = imageSize(uDst);
    if (any(greaterThanEqual(p, sz))) return;
    gUv = (vec2(p) + 0.5) / vec2(sz);
    if (uUp != 0) imageStore(uDst, p, imageLoad(uDst, p) + vec4(CxUp(), 1.0));
    else imageStore(uDst, p, vec4(CxDown(), 1.0));
}
