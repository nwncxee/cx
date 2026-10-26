#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 2) in vec3 aNrm;
layout(location = 3) in vec2 aChart;
#include "cx_drawbuf.glsl"
uniform int uSlot;      // the instance's CxDraw record: its model matrix is the placement
uniform vec4 uRect;     // x, y = the rect's origin in page texels, z = page texels per atlas texel
const float CX_LM_PAGE = 4096.0;
layout(location = 0) out vec3 gWorld;
layout(location = 1) out vec3 gNrm;
void main()
{
    CxDraw d = uDraw[uSlot];
    vec4 w = d.model * vec4(aPos, 1.0);
    gWorld = w.xyz;
    gNrm = mat3(d.model) * aNrm;
    vec2 t = (uRect.xy + aChart * uRect.z) / CX_LM_PAGE;
    gl_Position = vec4(t * 2.0 - 1.0, 0.0, 1.0);
}
