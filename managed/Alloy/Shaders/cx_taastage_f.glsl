uniform sampler2D uHist;     // this frame's resolved history (Prepare wrote it)
uniform float uSharp;        // post.taasharp: the unsharp mask's amount, 0 = the raw resolve
uniform sampler2D uWx;       // the precipitation drawn after the resolve: premultiplied scene-referred light
uniform int uWxOn;           // 1 = uWx holds this frame's precipitation
uniform float uExposure;     // the resolve's exposure and tone curve, which the precipitation's light takes
uniform int uMode;
uniform float uKnee;
uniform float uAcesMix;
#include "cx_wx_over.glsl"
vec4 CxStage(vec4 c)
{
    ivec2 sz = textureSize(uHist, 0);
    ivec2 p = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
    vec3 h = texelFetch(uHist, p, 0).rgb;
    if (uSharp > 0.0)
    {
        vec3 n = texelFetch(uHist, clamp(p + ivec2(1, 0), ivec2(0), sz - 1), 0).rgb
               + texelFetch(uHist, clamp(p - ivec2(1, 0), ivec2(0), sz - 1), 0).rgb
               + texelFetch(uHist, clamp(p + ivec2(0, 1), ivec2(0), sz - 1), 0).rgb
               + texelFetch(uHist, clamp(p - ivec2(0, 1), ivec2(0), sz - 1), 0).rgb;
        h = max(h + (h * 4.0 - n) * (uSharp * 0.25), vec3(0.0));
    }
    if (uWxOn != 0)
    {
        // the stack is display-encoded: the precipitation lands in display-linear light, and the pixel goes back out encoded
        vec3 lin = CxWxOver(pow(h, vec3($SRGB$)), textureLod(uWx, vUV, 0.0), uExposure, uMode, uKnee, uAcesMix);
        h = pow(max(lin, vec3(0.0)), vec3(1.0 / $SRGB$));
    }
    return vec4(h, c.a);
}
