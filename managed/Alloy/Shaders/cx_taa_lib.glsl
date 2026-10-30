// The accumulate's colour math, shared by the fragment resolve and the fused compute stage. Declares nothing:
// the including stage declares uHist first.
// The clip box's half-width in standard deviations. Below 1 the history is yanked on every noisy
// surface; above 1.5 a ghost fits inside it.
const float CX_TAA_GAMMA = 1.25;

vec3 CxToYCoCg(vec3 c)
{
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * (c.r - c.b), -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}
vec3 CxFromYCoCg(vec3 c)
{
    float t = c.x - c.z;
    return max(vec3(t + c.y, c.x + c.z, t - c.y), vec3(0.0));
}

// Catmull-Rom through five bilinear taps (the 9-texel cubic with the corner taps folded): a bilinear
// history resample every frame is the blur a naive TAA is known for, and this is the standard cure.
vec3 CxHistory(vec2 uv, vec2 size)
{
    vec2 pos = uv * size;
    vec2 center = floor(pos - 0.5) + 0.5;
    vec2 f = pos - center;
    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;
    vec2 tc12 = (center + w2 / w12) / size;
    vec2 tc0 = (center - 1.0) / size, tc3 = (center + 2.0) / size;
    vec3 c = textureLod(uHist, vec2(tc12.x, tc0.y), 0.0).rgb * (w12.x * w0.y)
           + textureLod(uHist, vec2(tc0.x, tc12.y), 0.0).rgb * (w0.x * w12.y)
           + textureLod(uHist, tc12, 0.0).rgb * (w12.x * w12.y)
           + textureLod(uHist, vec2(tc3.x, tc12.y), 0.0).rgb * (w3.x * w12.y)
           + textureLod(uHist, vec2(tc12.x, tc3.y), 0.0).rgb * (w12.x * w3.y);
    return max(c / ((w12.x * w0.y) + (w0.x * w12.y) + (w12.x * w12.y) + (w3.x * w12.y) + (w12.x * w3.y)), vec3(0.0));
}

// Clip q toward the box centre rather than clamping it per channel: a clamp moves the sample onto a
// face of the box and shifts its hue, a clip keeps it on the line to the current colour.
vec3 CxClipToBox(vec3 mn, vec3 mx, vec3 q)
{
    vec3 center = 0.5 * (mx + mn), extent = max(0.5 * (mx - mn), vec3(1e-5));
    vec3 v = q - center;
    vec3 a = abs(v / extent);
    float m = max(max(a.x, a.y), a.z);
    return m > 1.0 ? center + v / m : q;
}
