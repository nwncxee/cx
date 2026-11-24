// The volumetric fog's second pass (FogVolume): one invocation per froxel COLUMN, marching its slices from the eye.
// Each slice adds the area's fog wall to cx_vfog_cs's haze and integrates both over the slice's length, storing the
// light scattered toward the eye (rgb) and the transmittance (a) up to its far edge. The sky reads the last slice: it
// sees the same air as the geometry in front of it, so the two cannot part along a silhouette.
// The ground mist is integrated here, exactly, and each slice's edge carries the NEXT slice's mist too: mist lies on
// the floor, so a floor read between two edges is read past all the mist in front of it, never ramped across a slice.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
#include "cx_vfogmed.glsl"
#include "cx_noise.glsl"
#include "cx_mist.glsl"

layout(rgba16f, binding = 0) uniform readonly image3D uVfMedium;
layout(rgba16f, binding = 1) uniform writeonly image3D uVfSum;

// Points the wall is averaged over per slice: a thin ramp (an area whose fog starts a metre short of its end) falls
// inside one slice, and one point would put the whole slice on one side of it.
const int CX_VF_WALL_TAPS = 4;

/// One step through `haze` of optical depth scattering `lh` per unit extinction, and `mist` scattering `lm`.
void CxVfStep(inout vec3 s, inout float t, vec3 lh, float haze, vec3 lm, float mist)
{
    float tau = haze + mist, k = exp(-tau);
    if (tau > 0.0) s += t * (lh * haze + lm * mist) / tau * (1.0 - k);
    t *= k;
}

void main()
{
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(id, uVfGrid.xy))) return;
    vec3 ray = CxVfRay((vec2(id) + 0.5) / vec2(uVfGrid.xy));
    float len = length(ray);
    vec3 s = vec3(0.0);
    float t = 1.0;
    float d0 = uVfZ.x, d1 = CxVfDepth(1.0);
    vec4 m = imageLoad(uVfMedium, ivec3(id, 0));
    CxVfStep(s, t, vec3(0.0), 0.0, m.rgb, CxMistDepth(uVfEye + ray * d0, uVfEye + ray * d1));
    for (int k = 0; k < uVfGrid.z; k++)
    {
        float wall = 0.0;
        for (int j = 0; j < CX_VF_WALL_TAPS; j++)
            wall += CxVfWall(uVfEye + ray * mix(d0, d1, (float(j) + 0.5) / float(CX_VF_WALL_TAPS)));
        wall /= float(CX_VF_WALL_TAPS);
        float haze = (m.a + wall) * (d1 - d0) * len;
        vec4 next = vec4(0.0);
        float mist = 0.0, d2 = d1;
        if (k + 1 < uVfGrid.z)
        {
            d2 = CxVfDepth(float(k + 2));
            next = imageLoad(uVfMedium, ivec3(id, k + 1));
            mist = CxMistDepth(uVfEye + ray * d1, uVfEye + ray * d2);
        }
        CxVfStep(s, t, m.rgb, haze, next.rgb, mist);
        imageStore(uVfSum, ivec3(id, k), vec4(s, t));
        m = next; d0 = d1; d1 = d2;
    }
}
