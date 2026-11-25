// The volumetric fog's second pass (FogVolume): one invocation per froxel COLUMN, marching its slices from the eye.
// Each slice adds the area's fog wall to cx_vfog_cs's haze and integrates both over the slice's length, storing the
// light scattered toward the eye (rgb) and the transmittance (a) up to its far edge. The sky reads the last slice: it
// sees the same air as the geometry in front of it, so the two cannot part along a silhouette.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
#include "cx_vfogmed.glsl"

layout(rgba16f, binding = 0) uniform readonly image3D uVfMedium;
layout(rgba16f, binding = 1) uniform writeonly image3D uVfSum;

// Points the wall is averaged over per slice: a thin ramp (an area whose fog starts a metre short of its end) falls
// inside one slice, and one point would put the whole slice on one side of it.
const int CX_VF_WALL_TAPS = 4;

/// One homogeneous step of `dl` metres at extinction `sigma`, scattering `l` per unit extinction.
void CxVfStep(inout vec3 s, inout float t, vec3 l, float sigma, float dl)
{
    float k = exp(-sigma * dl);
    s += t * l * (1.0 - k);
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
    float d0 = uVfZ.x;
    for (int k = 0; k < uVfGrid.z; k++)
    {
        float d1 = CxVfDepth(float(k + 1));
        vec4 m = imageLoad(uVfMedium, ivec3(id, k));
        float wall = 0.0;
        for (int j = 0; j < CX_VF_WALL_TAPS; j++)
            wall += CxVfWall(uVfEye + ray * mix(d0, d1, (float(j) + 0.5) / float(CX_VF_WALL_TAPS)));
        wall /= float(CX_VF_WALL_TAPS);
        float dl = (d1 - d0) * len;
        CxVfStep(s, t, m.rgb, m.a + wall, dl);
        imageStore(uVfSum, ivec3(id, k), vec4(s, t));
        d0 = d1;
    }
}
