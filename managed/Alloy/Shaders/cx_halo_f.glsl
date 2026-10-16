// The look: the element's own flare texture, modulated by the vertex colour the way vanilla's fixed-function
// quad is, and where content named no resident texture a procedural stand-in -- a hot gaussian core, a soft
// skirt, a faint six-point streak and a slight chromatic spread. Both are multiplied by a per-pixel occlusion
// against the scene depth AT THE LIGHT (soft over one radius), so a pillar takes the whole chain down with it.
// Linear, additive, no encode. /effects haloviz 1 paints that visibility alone.
#version 460 core
in vec2 vUV;
in vec3 vColor;
in float vViewZ;
in float vRadius;
in vec2 vOcc;
uniform sampler2D uFlare;
uniform int uTextured;
#include "cx_scenedepth.glsl"
void main()
{
    float r = length(vUV);
    if (uTextured == 0 && r >= 1.0) discard;
    vec2 at = gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0)) + vOcc * 0.5;
    float sceneDepth = CxSceneViewZ(clamp(at, vec2(0.0), vec2(1.0)));   // positive distance
    float lightDepth = -vViewZ;
    float vis = clamp((sceneDepth - (lightDepth - vRadius)) / max(vRadius, 1e-3), 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(vis), 1.0); return; }
    vec3 profile;
    if (uTextured != 0)
    {
        profile = pow(max(texture(uFlare, vUV * 0.5 + 0.5).rgb, vec3(0.0)), vec3($SRGB$));
    }
    else
    {
        vec3 rr = vec3(r * 0.92, r, r * 1.08);            // chromatic spread: red reaches furthest
        vec3 core = exp(-rr * rr * 22.0);
        vec3 skirt = pow(max(1.0 - rr, 0.0), vec3(3.0)) * 0.35;
        float ang = atan(vUV.y, vUV.x);
        float streak = pow(max(abs(cos(ang * 3.0)), 0.0), 40.0) * pow(1.0 - r, 2.0) * 0.25;
        profile = core + skirt + streak;
    }
    frag = vec4(vColor * profile * vis, 1.0);
}
