// The movie plate on Vulkan: one full-screen triangle drawn with the viewport at the letterbox rect, so the whole
// frame maps onto it. The decoder's first row is the top of the screen, as the engine's quad shows it.
#version 460 core
uniform sampler2D uSrc;
in vec2 vUV;
out vec4 o;

void main()
{
    o = vec4(texture(uSrc, vec2(vUV.x, 1.0 - vUV.y)).rgb, 1.0);
}
