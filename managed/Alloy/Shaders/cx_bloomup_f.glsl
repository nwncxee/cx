#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;
uniform int uLod;
uniform vec2 uTexel;    // 1 / size of the SOURCE level

#include "cx_bloom_lib.glsl"

void main()
{
    gUv = vUV; gLod = float(uLod); gTexel = uTexel;
    frag = vec4(CxBloomUp(), 1.0);
}
