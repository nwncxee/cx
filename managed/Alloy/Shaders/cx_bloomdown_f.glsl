#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;
uniform int uLod;
uniform vec2 uTexel;    // 1 / size of the SOURCE level
uniform int uFirst;     // 1 = this pass reads the HDR colour: apply the threshold
uniform vec2 uThresh;   // x = brightness threshold, y = soft-knee width
uniform float uExposure;    // the resolve's gain: the threshold reads the exposed value

#include "cx_bloom_lib.glsl"

void main()
{
    gUv = vUV; gLod = float(uLod); gTexel = uTexel;
    frag = vec4(CxBloomDown(uFirst != 0, uThresh, uExposure), 1.0);
}
