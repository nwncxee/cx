// The volumetric fog's lookup (FogVolume, vfog.enabled): the froxel volume cx_vfogsum_cs integrated from the main
// view's eye, read at a world point. Slice k holds the integral to its FAR edge, so the last is the whole ray: the
// sky's. Needs cx_fogdecl ahead of it.
#ifndef CX_VFOG_LIB
#define CX_VFOG_LIB
/// In-scattered light (rgb, LINEAR) and transmittance (a) between the main view's eye and `world`.
vec4 CxVFogAt(vec3 world)
{
    vec4 c = uVFogVp * vec4(world, 1.0);
    float w = max(c.w, 1e-4);
    float s = clamp(log(max(w, uVFog.z) / uVFog.z) * uVFog.w, 0.0, 1.0) * uVFog.y;
    vec4 v = texture(uVFogVol, vec3(c.xy / w * 0.5 + 0.5, (max(s, 1.0) - 0.5) / uVFog.y));
    // inside the first slice: from nothing at the eye to that slice's far edge
    return s < 1.0 ? mix(vec4(0.0, 0.0, 0.0, 1.0), v, s) : v;
}
/// ...and along the whole ray through a SKY point, out to the volume's far plane.
vec4 CxVFogSkyAt(vec3 world)
{
    vec4 c = uVFogVp * vec4(world, 1.0);
    return texture(uVFogVol, vec3(c.xy / max(c.w, 1e-4) * 0.5 + 0.5, (uVFog.y - 0.5) / uVFog.y));
}
#endif
