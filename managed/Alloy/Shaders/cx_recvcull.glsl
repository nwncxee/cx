// #488: the receiver cull. The shadow volume is bounded by the hull of two spheres (the caster's own
// and the extrusion's end) and misses the camera frustum only when BOTH sit wholly outside ONE
// plane. Conservative: a rejected caster casts on no visible pixel. uPlanes is the dispatch's own block
// member (cx_gatherblk.glsl), which every caller of this splices ahead of it.
bool CxShadowMisses(vec3 p0, float r0, vec3 p1, float r1)
{
    for (int i = 0; i < 6; i++)
    {
        vec4 pl = uPlanes[i];
        if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;
    }
    return false;
}

bool CxFaceCulls(float dSq, float t, float r)
{
    if (t < -r) return true;
    float lim = t * @SLOPE@ + 2.0 * r;
    return lim < 0.0 || dSq - t * t > lim * lim;
}
