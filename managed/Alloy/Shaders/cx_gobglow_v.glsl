// The engine's highlight glow quads (WritePartHighlightVertices @0x45fe40), one instance each, corners from
// gl_VertexID. The record is the quad's world centre and its two half-axes, built on the CPU from each mesh
// part's bounding box under the part's composed transform.
#version 460 core
layout(std430, binding = 8) readonly buffer CxGobGlowBuf { vec4 uGlow[]; };   // (centre, alpha), (U, 0), (V, 0), (rgb, 0)
uniform mat4 uView;
uniform mat4 uProj;
out vec2 vUV;
out vec4 vColor;
out float vViewZ;

void main()
{
    int q = gl_InstanceID * 4;
    vec4 c = uGlow[q], u = uGlow[q + 1], v = uGlow[q + 2], rgb = uGlow[q + 3];
    vec2 s = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    vec4 vp = uView * vec4(c.xyz + u.xyz * (s.x * 2.0 - 1.0) + v.xyz * (s.y * 2.0 - 1.0), 1.0);
    vUV = s;
    vViewZ = vp.z;
    vColor = vec4(rgb.rgb, c.w);
    gl_Position = uProj * vp;
}
