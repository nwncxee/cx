// CX-83: the area-transition highlight as a PROJECTED DECAL. Vanilla builds a stencil shadow volume out of
// each trigger's extruded outline and then alpha-blends a camera-sized hull through it
// (Scene::DoHighlightTriggers @0x462310), which needs a stencil buffer on the framebuffer it draws into.
// This does the same test directly: reconstruct the world point under the pixel from the frame's own depth,
// and ask whether it is inside the trigger's prism -- exact for a concave outline too, and with no stencil
// anywhere. One fullscreen pass covers every highlighted trigger.
#version 460 core
in vec2 vUV;
const int CX_TRIG_MAX = 8;
layout(std430, binding = 8) readonly buffer CxTrigBuf { vec4 uTrig[]; };
// uTrig[k]                      = (first point, point count, world z min, world z max)
// uTrig[CX_TRIG_MAX + k]        = (r, g, b, alpha)
// uTrig[2 * CX_TRIG_MAX + p]    = (x, y, -, -)
uniform int uTrigCount;
uniform mat4 uInvVP;
uniform sampler2D uTrigDepth;
out vec4 frag;

void main()
{
    float d = texture(uTrigDepth, vUV).r;
    if (d <= 0.0) discard;                        // the sky sits at exactly 0.0: nothing to decal
    vec4 h = uInvVP * vec4(vUV * 2.0 - 1.0, d, 1.0);
    if (abs(h.w) < 1e-6) discard;
    vec3 p = h.xyz / h.w;
    for (int k = 0; k < uTrigCount; k++)
    {
        vec4 hdr = uTrig[k];
        if (p.z < hdr.z || p.z > hdr.w) continue;
        int first = 2 * CX_TRIG_MAX + int(hdr.x), n = int(hdr.y);
        bool inside = false;
        vec2 b = uTrig[first + n - 1].xy;
        for (int i = 0; i < n; i++)
        {
            vec2 a = uTrig[first + i].xy;
            if ((a.y > p.y) != (b.y > p.y)
                && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            b = a;
        }
        if (!inside) continue;
        vec4 c = uTrig[CX_TRIG_MAX + k];
        frag = vec4(c.rgb, c.a);
        return;
    }
    discard;
}
