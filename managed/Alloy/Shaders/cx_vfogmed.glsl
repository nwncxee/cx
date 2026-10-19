// The volumetric fog's froxel grid and medium, shared by its two passes (cx_vfog_cs lights it, cx_vfogsum_cs
// integrates it). The grid is the main view's frustum: uVfGrid.xy columns across the screen, uVfGrid.z slices
// spaced evenly in log(view depth) from uVfZ.x to uVfZ.y.
#ifndef CX_VFOGMED_LIB
#define CX_VFOGMED_LIB
uniform ivec3 uVfGrid;
uniform vec3 uVfZ;          // (near, far, log(far / near))
uniform mat4 uVfInvVp;      // the main view's unjittered inverse view-projection
uniform vec3 uVfEye;
uniform vec3 uVfFwd;        // the view direction, unit length: view depth is distance along it
// (haze extinction per metre at the haze floor, wall extinction per metre, wall start, wall end) -- the wall is
// the area's fog ramp, radial from uVfOrigin, and reaches its full extinction at the end distance
uniform vec4 uVfMed;
uniform vec3 uVfOrigin;     // where the area's fog is measured from: the player's feet

/// View depth at slice coordinate `k` (0 = the near edge, uVfGrid.z = the far edge).
float CxVfDepth(float k) { return uVfZ.x * exp(uVfZ.z * k / float(uVfGrid.z)); }

/// The world ray through screen point `uv`, scaled to one unit of VIEW DEPTH: eye + ray * depth is the point.
vec3 CxVfRay(vec2 uv)
{
    vec4 p = uVfInvVp * vec4(uv * 2.0 - 1.0, 0.0, 1.0);
    vec3 d = p.xyz / p.w - uVfEye;
    return d / dot(d, uVfFwd);
}

/// The area fog's own extinction at `world`: nothing inside its start, rising to uVfMed.y at its end and on.
float CxVfWall(vec3 world) { return uVfMed.y * smoothstep(uVfMed.z, uVfMed.w, distance(world, uVfOrigin)); }
#endif
