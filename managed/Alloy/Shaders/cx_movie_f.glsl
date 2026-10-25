// Straight out, no colour-space step: the present image holds the FINISHED display-space frame, the stage
// the engine drew this quad through is one of its UNLIT ones (fsglu), and FRAMEBUFFER_SRGB is off either
// side of the bracket. Alpha is forced to 1 -- blending is off, so the frame's own texel alpha would reach
// nothing but the present image's unused alpha channel.
#version 460 core
layout(binding = 0) uniform sampler2D uTex;
in vec2 vUV;
out vec4 o;

void main()
{
    o = vec4(texture(uTex, vUV).rgb, 1.0);
}
