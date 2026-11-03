// One instanced quad per authored flare element, corners from gl_VertexID, view-aligned: Scene::RenderLensFlares
// @0x471860's chain carried out to the light's depth. Vanilla's quad sits at view depth 2, flaresizes x 0.1 x
// reach half-wide, at flarepositions along the line from the screen centre through the light; scaled by
// depth / 2 it covers the same pixels here. Colour: (light + shift) x fade x cos^2 off the view axis x reach,
// each channel clamped, then linearised. Reach is gone at flareradius. Occlusion is the fragment stage's.
#version 460 core
layout(std430, binding = 8) readonly buffer CxHaloBuf { vec4 uHalos[]; };   // (pos, soft), (rgb, flareradius), (size, axis, lead, slot)
// Each light's occlusion fade, by slot: cx_haloocc_c walks it toward what the depth copy shows just before this draw.
layout(std430, binding = 9) readonly buffer CxHaloFade { float uFade[]; };
uniform mat4 uView;
uniform mat4 uProj;
uniform float uGain;
out vec2 vUV;
out vec3 vColor;
out float vViewZ;     // the light's view-space z (negative forward)
out float vRadius;    // world radius: the soft occlusion range
// NDC step from this element's centre to the LIGHT. The depth read is about what covers the light, not what
// sits under a ghost parked elsewhere on screen, so the fragment stage shifts its sample by this.
out vec2 vOcc;

void main()
{
    // The list is grouped by texture, one draw a group, and the group's first element rides the draw's
    // firstInstance -- gl_BaseInstance here -- so the instance id counts from it.
    int h = (gl_BaseInstance + gl_InstanceID) * 3;
    vec4 a = uHalos[h], b = uHalos[h + 1], c = uHalos[h + 2];
    vec3 vp = (uView * vec4(a.xyz, 1.0)).xyz;
    float d = length(vp);
    float reach = clamp(1.0 - d / max(b.w, 1e-3), 0.0, 1.0);
    float facing = d > 0.0 ? max(-vp.z / d, 0.0) : 0.0;
    vColor = pow(clamp(b.rgb * (uFade[int(c.w)] * facing * facing * reach), 0.0, 1.0), vec3($SRGB$)) * uGain;
    float hw = c.x * 0.1 * reach * -vp.z * 0.5;
    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    vUV = corner;
    vViewZ = vp.z;
    vRadius = a.w;
    vec2 centre = vp.xy * c.y;
    vec4 cl = uProj * vec4(centre, vp.z, 1.0), cLight = uProj * vec4(vp, 1.0);
    vOcc = cl.w > 0.0 && cLight.w > 0.0 ? cLight.xy / cLight.w - cl.xy / cl.w : vec2(0.0);
    vec3 pos = vec3(centre + corner * hw, vp.z);
    gl_Position = hw > 0.0 && facing > 0.0 ? uProj * vec4(pos, 1.0) : vec4(2.0, 2.0, 2.0, 1.0);
}
