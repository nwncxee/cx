// GTAO on the frame depth (Jimenez et al. 2016, the XeGTAO reference's structure): per slice the
// two horizons, cosine-weighted against the normal projected into the slice, falloff over the outer
// 60% of the reach (the radius, or less where the screen cap binds). Normals come from depth (the smaller-difference neighbour on each axis), a 4x4
// pattern rotates slices and steps, and the blur stage folds that pattern away.
#version 460 core
in vec2 vUV;
out float frag;
#include "cx_ssaolib.glsl"
const float PI = 3.14159265, HALF_PI = 1.57079633;
// 4x4 ordered pattern (Bayer), so the blur's 4x4 window sees every slice and step phase once.
const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

vec3 CxViewNormal(vec2 uv, vec3 P)
{
    vec3 Pr = CxViewPos(uv + vec2(uRt.x, 0.0)), Pl = CxViewPos(uv - vec2(uRt.x, 0.0));
    vec3 Pu = CxViewPos(uv + vec2(0.0, uRt.y)), Pd = CxViewPos(uv - vec2(0.0, uRt.y));
    vec3 dx = abs(Pr.z - P.z) < abs(P.z - Pl.z) ? Pr - P : P - Pl;
    vec3 dy = abs(Pu.z - P.z) < abs(P.z - Pd.z) ? Pu - P : P - Pd;
    return normalize(cross(dx, dy));
}

float CxSliceOpen(float projLen, float cosN, float n) { return projLen * (cosN + n * sin(n)); }

// One slice's unoccluded estimate swings with its angle to the normal's tilt, and clamping it per pixel greys an angled
// flat surface: this is that grey, the mean of the clamped estimate over the slice angles, with no pattern in it.
float CxFlatGrey(float cosT)
{
    float sinT = sqrt(max(1.0 - cosT * cosT, 0.0)), sum = 0.0;
    for (int i = 0; i < 4; i++)
    {
        float s = sinT * cos((float(i) + 0.5) * PI / 8.0);
        float n = atan(s, cosT);
        sum += pow(min(CxSliceOpen(length(vec2(s, cosT)), cos(n), n), 1.0), uPower);
    }
    return sum * 0.25;
}

void main()
{
    float depth = textureLod(uDepth, vUV, 0.0).r;
    if (depth <= 0.0) { frag = 1.0; return; }
    vec3 P = CxViewPos(vUV);
    vec3 N = CxViewNormal(vUV, P);
    vec3 V = normalize(-P);
    float dist = -P.z;
    // world radius -> pixels: ndc spans 2 over h pixels, and x_ndc = x_v * M11 / dist
    float rFull = uRadius * uProj[1].y / dist * 0.5 * uRt.w;
    float rPx = min(rFull, uMaxPx);
    if (rPx < 1.0) { frag = 1.0; return; }
    ivec2 pix = ivec2(gl_FragCoord.xy);
    float b = BAYER[(pix.x & 3) + ((pix.y & 3) << 2)];
    // #390: the phase walks the golden ratio per frame under the accumulator, so successive frames
    // sample different slices and steps; at uFrame 0 both are exactly the single-frame pattern.
    float noiseSlice = fract(b / 16.0 + float(uFrame) * 0.618034), noiseStep = fract(b * 0.618034 + float(uFrame) * 0.381966);
    // the world reach the capped screen reach covers: fading over the whole radius would leave occlusion at full strength where the samples stop
    float reach = uRadius * rPx / rFull;
    float falloffMul = -1.0 / (0.6 * reach);
    const float falloffAdd = 0.4 / 0.6 + 1.0;
    float minS = 1.3 / rPx;
    float vis = 0.0, open = 0.0;
    for (int s = 0; s < uDirs; s++)
    {
        float phi = (float(s) + noiseSlice) / float(uDirs) * PI;
        vec2 omega = vec2(cos(phi), sin(phi));
        vec3 dirVec = vec3(omega, 0.0);
        vec3 ortho = dirVec - dot(dirVec, V) * V;
        vec3 axis = normalize(cross(ortho, V));
        vec3 projN = N - axis * dot(N, axis);
        float projLen = length(projN);
        float signN = sign(dot(ortho, projN));
        float cosN = clamp(dot(projN, V) / max(projLen, 1e-5), 0.0, 1.0);
        float n = signN * acos(cosN);
        float low0 = cos(n + HALF_PI), low1 = cos(n - HALF_PI);
        float hc0 = low0, hc1 = low1;
        for (int j = 0; j < uSteps; j++)
        {
            float t = (float(j) + fract(noiseStep + float(s) * 0.618034)) / float(uSteps);
            t = t * t + minS;
            // on a pixel centre: the position comes from this uv and the depth from a whole texel, so anything between them leaves the surface
            vec2 off = round(omega * (t * rPx)) * uRt.xy;
            vec3 S0 = CxViewPos(vUV + off) - P, S1 = CxViewPos(vUV - off) - P;
            float l0 = max(length(S0), 1e-5), l1 = max(length(S1), 1e-5);
            float w0 = clamp(l0 * falloffMul + falloffAdd, 0.0, 1.0), w1 = clamp(l1 * falloffMul + falloffAdd, 0.0, 1.0);
            // only what stands above this surface's own plane occludes it
            hc0 = max(hc0, mix(low0, dot(S0, V) / l0, w0 * smoothstep(0.0, 0.05, dot(S0, N) / l0)));
            hc1 = max(hc1, mix(low1, dot(S1, V) / l1, w1 * smoothstep(0.0, 0.05, dot(S1, N) / l1)));
        }
        float h0 = n + clamp(-acos(hc1) - n, -HALF_PI, HALF_PI);
        float h1 = n + clamp(acos(hc0) - n, -HALF_PI, HALF_PI);
        float a0 = (cosN + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) * 0.25;
        float a1 = (cosN + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) * 0.25;
        vis += projLen * (a0 + a1);
        open += CxSliceOpen(projLen, cosN, n);
    }
    // occlusion as a share of what these slices see unoccluded, so an occluder counts in every slice angle
    vis = clamp(vis / max(open, 1e-4), 0.0, 1.0);
    frag = max(pow(vis, uPower) * CxFlatGrey(clamp(dot(N, V), 0.0, 1.0)), 0.03);
}
