#version 460 core
layout(triangles) in;
layout(triangle_strip, max_vertices = 3) out;
layout(location = 0) in vec3 gWorld[];
layout(location = 1) in vec3 gNrm[];
layout(location = 0) out vec3 vBary;
layout(location = 1) out vec3 vNrm;
layout(location = 2) flat out vec3 vW0;
layout(location = 3) flat out vec3 vW1;
layout(location = 4) flat out vec3 vW2;
uniform float uBakeDepth;   // NDC z: the light's step under GREATER (see BakeDepthSteps); -1 = coverage
const float CX_LM_PAGE = 4096.0;
const float CX_LM_PUSH = 0.6;    // texels: past the half that puts a centre inside any strip
const float CX_LM_MITRE = 3.0;   // texels: the most a sharp corner may travel
void main()
{
    vec2 p0 = (gl_in[0].gl_Position.xy * 0.5 + 0.5) * CX_LM_PAGE;
    vec2 p1 = (gl_in[1].gl_Position.xy * 0.5 + 0.5) * CX_LM_PAGE;
    vec2 p2 = (gl_in[2].gl_Position.xy * 0.5 + 0.5) * CX_LM_PAGE;
    vec2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
    float area = e0.x * (p2.y - p0.y) - e0.y * (p2.x - p0.x);
    float s = area < 0.0 ? -1.0 : 1.0;
    bool flat_ = abs(area) < 1e-4;
    // outward unit normals of the three edges (right-hand side of a CCW edge)
    vec2 n0 = s * normalize(vec2(e0.y, -e0.x));
    vec2 n1 = s * normalize(vec2(e1.y, -e1.x));
    vec2 n2 = s * normalize(vec2(e2.y, -e2.x));
    vec2 q[3]; vec2 p[3] = vec2[3](p0, p1, p2);
    vec2 nin[3] = vec2[3](n2, n0, n1), nout[3] = vec2[3](n0, n1, n2);
    for (int i = 0; i < 3; i++)
    {
        vec2 m = nin[i] + nout[i];
        float d = 1.0 + dot(nin[i], nout[i]);
        vec2 push = d > 1e-3 ? m * (CX_LM_PUSH / d) : nout[i] * CX_LM_PUSH;
        float l = length(push);
        if (l > CX_LM_MITRE) push *= CX_LM_MITRE / l;
        q[i] = flat_ ? p[i] : p[i] + push;
    }
    for (int i = 0; i < 3; i++)
    {
        vec3 b;
        if (flat_) b = vec3(i == 0 ? 1.0 : 0.0, i == 1 ? 1.0 : 0.0, i == 2 ? 1.0 : 0.0);
        else
        {
            vec2 x = q[i];
            float a0 = (p1.x - x.x) * (p2.y - x.y) - (p1.y - x.y) * (p2.x - x.x);
            float a1 = (p2.x - x.x) * (p0.y - x.y) - (p2.y - x.y) * (p0.x - x.x);
            b = vec3(a0, a1, area - a0 - a1) / area;
        }
        vBary = b;
        vNrm = gNrm[i];
        vW0 = gWorld[0]; vW1 = gWorld[1]; vW2 = gWorld[2];
        gl_Position = vec4(q[i] / CX_LM_PAGE * 2.0 - 1.0, uBakeDepth, 1.0);
        EmitVertex();
    }
    EndPrimitive();
}
