#version 460 core
// The flare occlusion fade, one invocation per staged element. Scene::RenderLensFlares @0x471860 casts a ray from
// the light to the camera and walks PartLight+0xa0 toward its answer at 3 per second; this measures how much of
// the light the frame's depth copy shows instead and walks the light's fade slot toward that coverage by uStep
// (the rate times the floored frame delta, worked out on the CPU). Coverage is the mean of five taps of the halo
// fragment stage's own soft test, so the whole-chain fade and the per-pixel cut agree about a pillar. Only a
// light's lead element is measured: the rest share its slot and would step it again. Off screen or behind the
// camera the fade HOLDS. The halo vertex stage reads the slot in the same frame.
layout(local_size_x = 64) in;
layout(std430, binding = 8) readonly buffer CxHaloBuf { vec4 uHalos[]; };   // (pos, soft), (rgb, flareradius), (size, axis, lead, slot)
layout(std430, binding = 9) buffer CxHaloFade { float uFade[]; };           // one per flare light, by slot
layout(binding = 0) uniform sampler2D uSceneDepth;
// uViewProj: world -> clip, the pass's own (jittered) pair. uProjTerms: proj (0,0), (1,1) for the tap spread, and
// the (2,2), (3,2) pair cx_scenedepth.glsl linearises reversed depth with.
layout(push_constant) uniform CxHaloOcc { mat4 uViewProj; vec4 uProjTerms; int uCount; float uStep; };

float CxHaloViewZ(vec2 uv)
{
    return uProjTerms.w / (texture(uSceneDepth, uv).r + uProjTerms.z);
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uCount)) return;
    vec4 a = uHalos[i * 3u], c = uHalos[i * 3u + 2u];
    if (c.z == 0.0) return;
    vec4 clip = uViewProj * vec4(a.xyz, 1.0);
    if (clip.w <= 0.0 || any(greaterThan(abs(clip.xy), vec2(clip.w)))) return;
    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    // a quarter of the soft range through the projection (NDC, halved again for UV): around the light, never past
    // what the per-pixel cut softens over
    vec2 spread = 0.25 * a.w * uProjTerms.xy / clip.w;
    float lightDepth = clip.w;
    float soft = max(a.w, 1e-3);
    float sum = 0.0;
    for (int t = 0; t < 5; t++)
    {
        vec2 o = t == 0 ? vec2(0.0)
               : t == 1 ? vec2( spread.x, 0.0) : t == 2 ? vec2(-spread.x, 0.0)
               : t == 3 ? vec2(0.0,  spread.y) : vec2(0.0, -spread.y);
        sum += clamp((CxHaloViewZ(clamp(uv + o, vec2(0.0), vec2(1.0))) - (lightDepth - soft)) / soft, 0.0, 1.0);
    }
    float target = sum / 5.0;
    uint slot = uint(c.w);
    float fade = uFade[slot];
    uFade[slot] = fade < target ? min(target, fade + uStep) : max(target, fade - uStep);
}
