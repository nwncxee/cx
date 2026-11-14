#version 460 core
// the coverage layer into the reactive mask: a pixel the layer covers in part is a blend of two things that move
// apart, and below half it keeps the background's depth, so its history is reprojected as the background's
#include "cx_reactive.glsl"
uniform sampler2DMS uDepthMs;
uniform sampler2D uDepth;
layout(location = 0) out vec4 frag;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    // the seed wrote this very value into every sample, so a sample that differs is one the layer covered
    float bg = texelFetch(uDepth, p, 0).r;
    int n = textureSamples(uDepthMs);
    int k = 0;
    for (int i = 0; i < n; i++) if (texelFetch(uDepthMs, p, i).r != bg) k++;
    float share = float(k) / float(n);
    frag = vec4(0.0);
    fragReact = CxReactive(min(share, 1.0 - share));
}
