#version 460 core
uniform vec4 uColor;
out vec4 frag;
#include "cx_reactive.glsl"

void main()
{
    frag = uColor;
    fragReact = CxReactive(CxReactiveShare(uColor.rgb, uColor.a, true));
}
