
vec3 CxSafeNorm(vec3 v)
{
    float l = dot(v, v);
    return l > 0.0 ? v * inversesqrt(l) : vec3(0.0);
}

void main()
{
    int corner = gl_VertexID % 6;
    CxParticle p = particles[partIdx[gl_VertexID / 6]];

    // (A, B, C, C, B, D) as sign pairs: sx picks the right axis, sy picks the up axis AND which
    // end of a stretched pair the vertex sits on.
    const vec2 SIGNS[6] = vec2[6](vec2( 1.0,  1.0), vec2(-1.0,  1.0), vec2( 1.0, -1.0),
                                  vec2( 1.0, -1.0), vec2(-1.0,  1.0), vec2(-1.0, -1.0));
    vec2 sg = SIGNS[corner];
    bool near = sg.y > 0.0;

    vec3 base = near ? p.posCol.xyz : p.farColFar.xyz;
    vec3 world = base + sg.x * p.rightSoft.xyz + sg.y * p.upType.xyz;

    vec3 rn = CxSafeNorm(p.rightSoft.xyz);
    vec3 un = CxSafeNorm(p.upType.xyz);
    vec3 facing = CxSafeNorm(uEye - p.centre.xyz);

    vColor = unpackUnorm4x8(floatBitsToUint(near ? p.posCol.w : p.farColFar.w));
    vUv = vec2(sg.x > 0.0 ? p.uv.y : p.uv.x, near ? p.uv.w : p.uv.z);
    vWorld = world;
    vNrm = CxSafeNorm(sg.x * rn + sg.y * un + facing);
    vSoft = p.rightSoft.w;
    vType = int(p.upType.w);
    vec4 vp = uView * vec4(world, 1.0);
    vViewZ = -vp.z;
    gl_Position = uProj * vp;

#ifdef CX_MOTION
    // CX-48: the velocity arm. The whole quad moves by its centre's own step -- which is what the stretched
    // and linked types do too, whose far end is another point entirely -- so the previous corner is this
    // corner translated back. The size and the facing basis are this frame's.
    vCur = gl_Position;
    vPrev = uPrevVP * vec4(world - (p.centre.xyz - p.prev.xyz), 1.0);
    return;
#endif

    // #148's terms, the ones grass runs, on the hemispherical billboard normal -- per vertex (#336).
    // Only the alpha-blended class is lit: smoke, dust and fog are media a torch should pick out,
    // while additive and punch-through emit their own light and were never lit in vanilla either.
    vLight = vec3(1.0);
    if (uLit != 0)
    {
        // The cluster is the particle CENTRE's, for all six corners: a quad at the camera has corners
        // behind the eye and off the screen, and a corner clamped to an edge tile reads a different
        // light list from its neighbours -- a vertex popping dark as the smoke drifts, i.e. flicker.
        vec4 cv = uView * vec4(p.centre.xyz, 1.0);
        vec4 cc = uProj * cv;
        cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
        float ndl = clamp(dot(vNrm, uAreaDir), 0.0, 1.0);
        vec3 sun = uAreaDiffuse * (ndl * CxSunShadow(world, vNrm, ndl, ndl));
        vec3 point = CxClusteredDiffuse(world, vNrm, max(-cv.z, 1e-3));
        // the 0.15 floor and the sun-facing wrap, as every other owned stage runs them (a content
        // lighting fork's term; stock's ambient is flat)
        vLight = uAreaAmbient * mix(0.15, 1.0, ndl) + sun + point;
    }
}
