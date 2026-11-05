#version 460 core
#include "cx_guilib.glsl"

void main()
{
    int r = vRec * 16;
    int flags = int(uGui[r + 13].x);
    float envLevel;
    vec4 t = GuiTexel(r, flags, envLevel);
    vec4 c = GuiShade(r, flags, GuiLin(t.rgb), t.a, envLevel, vNrm);
    c.rgb = GuiEnc(c.rgb);
    if ((flags & 8) != 0 && c.a <= 0.2) discard;   // SetUniforms: fAlphaDiscardValue = cap 1 ? 0.2 : -1
    frag = c;
}
