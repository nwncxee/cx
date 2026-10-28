#version 460 core
// the coverage layer's colour back into the frame: the box mean of its samples, which the seed made the composite
uniform sampler2DMS uColMs;
out vec4 frag;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    int n = textureSamples(uColMs);
    vec4 c = vec4(0.0);
    for (int i = 0; i < n; i++) c += texelFetch(uColMs, p, i);
    frag = c / float(n);
}
