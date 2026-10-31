#version 460 core
// CX-189: the coverage layer's depth back into the frame. Drawn at the world's nearer test against the seeded depth: the
// (n/2)-th nearest sample is nearer than the background exactly when half the samples or more were covered,
// so a pixel the layer mostly covers takes its depth and every other pixel fails the test and keeps its own.
uniform sampler2DMS uDepthMs;
uniform int uSamples;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    float d[8];
    int n = clamp(uSamples, 1, 8);
    for (int i = 0; i < n; i++)
    {
        float v = texelFetch(uDepthMs, p, i).r;
        int j = i;
        for (; j > 0 && d[j - 1] < v; j--) d[j] = d[j - 1];   // nearest first: the world depth is reversed
        d[j] = v;
    }
    gl_FragDepth = d[max(n / 2 - 1, 0)];
}
