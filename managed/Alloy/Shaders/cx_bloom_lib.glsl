// The bloom chain's taps, shared by the downsample and upsample stages and the compute bloom. Declares nothing:
// the including stage declares uSrc, and sets gUv (this texel's centre), gLod and gTexel before a call.
vec2 gUv;
float gLod;
vec2 gTexel;

vec3 T(vec2 o) { return textureLod(uSrc, gUv + o * gTexel, gLod).rgb; }

// The 13-tap downsample; on the first level (first) the Karis average and the soft-knee threshold.
vec3 CxBloomDown(bool first, vec2 thresh, float exposure)
{
    vec3 a = T(vec2(-2.0,  2.0)), b = T(vec2( 0.0,  2.0)), c = T(vec2( 2.0,  2.0));
    vec3 d = T(vec2(-2.0,  0.0)), e = T(vec2( 0.0,  0.0)), f = T(vec2( 2.0,  0.0));
    vec3 g = T(vec2(-2.0, -2.0)), h = T(vec2( 0.0, -2.0)), i = T(vec2( 2.0, -2.0));
    vec3 j = T(vec2(-1.0,  1.0)), k = T(vec2( 1.0,  1.0));
    vec3 l = T(vec2(-1.0, -1.0)), m = T(vec2( 1.0, -1.0));
    // Karis average on the first downsample only: each 2x2 group is weighted by 1/(1+luma), so one
    // runaway texel cannot dominate its group. Past level 0 the fireflies are gone, and reweighting
    // filtered mips would only darken the bright cores.
    vec3 grp[5] = vec3[5]((j + k + l + m) * 0.25, (a + b + d + e) * 0.25, (b + c + e + f) * 0.25,
                          (d + e + g + h) * 0.25, (e + f + h + i) * 0.25);
    const float w[5] = float[5](0.5, 0.125, 0.125, 0.125, 0.125);
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    for (int n = 0; n < 5; n++)
    {
        float kw = first ? w[n] / (1.0 + dot(grp[n], vec3(0.2126, 0.7152, 0.0722))) : w[n];
        sum += grp[n] * kw;
        wsum += kw;
    }
    vec3 col = sum / max(wsum, 1e-6);
    if (first)
    {
        // Unreal's soft-knee curve: nothing below the threshold blooms at all, which is what keeps
        // the effect off engine-drawn geometry's range and off every ordinary lit surface.
        float br = max(col.r, max(col.g, col.b)) * exposure;
        float soft = clamp(br - thresh.x + thresh.y, 0.0, 2.0 * thresh.y);
        soft = soft * soft / (4.0 * thresh.y);
        col *= max(soft, br - thresh.x) / max(br, 1e-6);
    }
    return max(col, vec3(0.0));
}

// The 3x3 tent upsample.
vec3 CxBloomUp()
{
    return (T(vec2(-1.0,  1.0)) + T(vec2(0.0,  1.0)) * 2.0 + T(vec2(1.0,  1.0))
           + T(vec2(-1.0,  0.0)) * 2.0 + T(vec2(0.0, 0.0)) * 4.0 + T(vec2(1.0, 0.0)) * 2.0
           + T(vec2(-1.0, -1.0)) + T(vec2(0.0, -1.0)) * 2.0 + T(vec2(1.0, -1.0))) * (1.0 / 16.0);
}
