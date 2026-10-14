#version 460 core
in vec2 vUV;
in vec3 vWorld;
in float vDepth;
flat in int vDraw;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
#include "cx_idout.glsl"
uniform sampler2D uTex;
uniform float uAlphaCut;
#include "cx_keyhole.glsl"
#include "cx_alphamip.glsl"
#include "cx_plt.glsl"
void main()
{
    vec2 du1 = dFdx(vUV), du2 = dFdy(vUV);
    // a dissolving cutout/wind batch punches the same aperture DepthKeyFs does, so it stays in the
    // prepass (and keeps its screen-space AO) when the camera closes in
    if (CxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth) < 0.0) discard;
    oVisId = CxVisId(vDraw, gl_PrimitiveID);
    if (uAlphaCut < 0.0) return;
    // the per-draw multiplier (material alpha, gob fade) is 1 on every opaque/cutout draw, so this only
    // bites the blended sweep: a draw faded to nothing must not lay depth
    float mul = uDraw[vDraw].rot.w;
    if (mul <= uAlphaCut) discard;
    if ((int(uDraw[vDraw].pbr.w) & 64) != 0) return;
    // CX-191: the colour pass tests the same compensated alpha, or its GL_EQUAL draw finds holes -- a PLT's
    // included: its alpha is the palette's, and the pair texture's own alpha is 1 everywhere
    if (CxAlphaMip(CxDiffuse(uTex, vUV, uLodBias, vDraw).a, du1, du2, vec2(textureSize(uTex, 0)), uLodBias) * mul <= uAlphaCut) discard;
}
