#version 460 core
// The model view's image from the same doll drawn over black and over white: what it covers is how much of the white
// the black one lacks, so an effect that only adds light covers what it lights and no more.
layout(binding = 0) uniform sampler2D uOverBlack;
layout(binding = 1) uniform sampler2D uOverWhite;
uniform vec4 uBg;   // the server's background, straight (not premultiplied)
in vec2 vUV;
out vec4 oColor;
void main()
{
    vec3 lit = texture(uOverBlack, vUV).rgb;
    vec3 seen = texture(uOverWhite, vUV).rgb;
    vec3 left = seen - lit;
    float cover = clamp(1.0 - min(left.r, min(left.g, left.b)), 0.0, 1.0);
    vec3 under = uBg.rgb * uBg.a * (1.0 - cover);
    float a = cover + uBg.a * (1.0 - cover);
    oColor = vec4(a > 0.0 ? (lit + under) / a : vec3(0.0), a);
}
