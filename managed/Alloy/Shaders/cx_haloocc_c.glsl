// CX-85: one invocation per halo, sampling Alloy's own scene depth to say how much of this flare the frame
// can see. Replaces vanilla's occlusion ray (Scene::RenderLensFlares @0x471860 hit-checks light -> camera and
// integrates the answer into PartLight+0xa0); the CPU reads this two frames later and integrates it there.
// Coverage is the average of five taps of the same soft test the halo fragment stage runs, so the whole-halo
// fade and the per-pixel one cannot disagree about what a pillar does.
// Slot values: 0 = no sample (off screen, or behind the camera) and the CPU holds the fade; otherwise
// 1 + round(coverage * 1024).
#version 460 core
layout(local_size_x = 64) in;
layout(std430, binding = 8) readonly buffer CxHaloBuf { vec4 uHalos[]; };   // (pos, soft), (rgb, flareradius), (size, axis, probe, -)
layout(std430, binding = 9) writeonly buffer CxHaloVis { uint uVis[]; };   // Halos.BindVis
uniform mat4 uView;
uniform mat4 uProj;
uniform sampler2D uSceneDepth;
uniform int uCount;

// The cx_scenedepth.glsl linearisation, from the projection this pass is using: positive view distance.
float ViewZ(vec2 uv)
{
    float ndc = texture(uSceneDepth, uv).r * 2.0 - 1.0;
    return uProj[3].z / (ndc + uProj[2].z);
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uCount)) return;
    // Every element of a light sits at the same world point, so only its lead is measured; the CPU reads
    // nothing from the rest and five depth taps an element would buy that nothing.
    if (uHalos[i * 3u + 2u].z == 0.0) { uVis[i] = 0u; return; }
    vec4 a = uHalos[i * 3u];
    vec3 vp = (uView * vec4(a.xyz, 1.0)).xyz;
    vec4 clip = uProj * vec4(vp, 1.0);
    if (clip.w <= 0.0 || any(greaterThan(abs(clip.xy), vec2(clip.w)))) { uVis[i] = 0u; return; }
    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    // Taps sit at a quarter of the soft range through the projection (the NDC extent, halved again for UV),
    // so the probe samples around the light without reaching past what the per-pixel cut softens over.
    vec2 spread = 0.25 * a.w * vec2(uProj[0][0], uProj[1][1]) / clip.w;
    float lightDepth = -vp.z;
    float soft = max(a.w, 1e-3);
    float sum = 0.0;
    for (int t = 0; t < 5; t++)
    {
        vec2 o = t == 0 ? vec2(0.0)
               : t == 1 ? vec2( spread.x, 0.0) : t == 2 ? vec2(-spread.x, 0.0)
               : t == 3 ? vec2(0.0,  spread.y) : vec2(0.0, -spread.y);
        sum += clamp((ViewZ(clamp(uv + o, vec2(0.0), vec2(1.0))) - (lightDepth - soft)) / soft, 0.0, 1.0);
    }
    uVis[i] = 1u + uint(round(sum * (1024.0 / 5.0)));
}
