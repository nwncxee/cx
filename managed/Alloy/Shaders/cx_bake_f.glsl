#version 460 core
layout(location = 0) in vec3 vBary;
layout(location = 1) in vec3 vNrm;
layout(location = 2) flat in vec3 vW0;
layout(location = 3) flat in vec3 vW1;
layout(location = 4) flat in vec3 vW2;
uniform vec4 uBakeLight;   // xyz = position, w = the lit radius (GetLightAdjustedRadius)
uniform vec3 uBakeColor;   // the walk's colour, linearised in CxPointLight exactly as the loop does
uniform int uBakeMode;     // 0 = coverage only (A' alpha 1), 1 = one light's contribution, additive
uniform int uBakeChan;     // CX-56: the light's place in the rect's pick 0..3; 4 = not picked (LightmapBake.Channels)
uniform int uBakeCube;     // the scratch cube this light's shadow is in: slot 0 on GL, the step's own on Vulkan
layout(location = 0) out vec4 oA;   // sum rgb, coverage alpha
layout(location = 1) out vec4 oB;   // sum luminance * direction
layout(location = 2) out vec4 oC;   // CX-56: per channel, the luminance a mover can take back (all but the floor)
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"
void main()
{
    if (uBakeMode == 0) { oA = vec4(0.0, 0.0, 0.0, 1.0); oB = vec4(0.0); oC = vec4(0.0); return; }
    // a pushed texel shades the face's nearest interior point: barycentrics clamped back into the triangle
    vec3 b = max(vBary, vec3(0.0));
    b /= max(b.x + b.y + b.z, 1e-6);
    vec3 vWorld = b.x * vW0 + b.y * vW1 + b.z * vW2;
    vec3 N = normalize(vNrm);
    vec3 c, L; float att, f2, ndlRaw, ndl;
    if (!CxPointLight(uBakeLight, uBakeColor, vWorld, N, c, L, att, f2, ndlRaw, ndl) || ndl <= 0.0) discard;
    // the step's slot of the scratch array: the same receiver the frame's cube slots go through
    float sh = CxLocalShadow(uBakeCube, vWorld, N, ndlRaw, 0.0, 0.0);
    vec3 unsh = CxPointDiffuse(c, att, ndlRaw, ndl);
    vec3 lit = unsh * mix(uLocalFloor, 1.0, sh);
    float lum = dot(lit, vec3(0.2126, 0.7152, 0.0722));
    oA = vec4(lit, 0.0);
    oB = vec4(lum * L, 0.0);
    // what a mover standing in this light's path removes: everything the bake let through above the floor
    float mover = lum - uLocalFloor * dot(unsh, vec3(0.2126, 0.7152, 0.0722));
    oC = uBakeChan < 4 ? vec4(equal(ivec4(uBakeChan), ivec4(0, 1, 2, 3))) * max(mover, 0.0) : vec4(0.0);
}
