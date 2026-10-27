#version 460 core
out vec4 frag;
uniform sampler2D uAccum;
uniform sampler2D uReveal;
uniform int uViz;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    float reveal = texelFetch(uReveal, p, 0).r;
    if (uViz == 1) { frag = vec4(vec3(reveal), 1.0); return; }
    if (uViz == 2) { vec4 a = texelFetch(uAccum, p, 0); frag = vec4(a.rgb / max(a.a, 1e-5), 1.0); return; }
    if (uViz == 3) { frag = vec4(vec3(texelFetch(uAccum, p, 0).a * 0.1), 1.0); return; }
    if (!(reveal < 1.0)) discard;   // also NaN: nothing well-formed wrote here
    reveal = max(reveal, 0.0);
    vec4 accum = texelFetch(uAccum, p, 0);
    vec3 c = accum.rgb / max(accum.a, 1e-5);
    if (any(isinf(c)) || any(isnan(c))) c = vec3(0.0);
    frag = vec4(c, reveal);
}
