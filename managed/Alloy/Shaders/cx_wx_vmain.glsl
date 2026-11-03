void main()
{
    uint id = uint(gl_BaseInstance + gl_InstanceID) * 5u;
    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    CxWxCorner q = CxWxQuad(uDrops[id], uDrops[id + 1u], uDrops[id + 2u], uDrops[id + 3u], uDrops[id + 4u], corner);
    vUV = q.uv;
    vSeed = q.seed;
    vLight = q.light;
    vBehind = q.behind;
    vSkyLow = q.skyLow;
    vLampDir = q.lampDir;
    vSunVis = q.sunVis;
    vSide = q.side;
    vUp = q.up;
#ifdef CX_MOTION
    vViewZ = (uView * vec4(q.pos, 1.0)).z;
    gl_Position = uVp * vec4(q.pos, 1.0);
    vVel = CxMotionVec(gl_Position, uPrevVP * vec4(q.pos - uFall * uDrops[id + 2u].w, 1.0));
#else
    vec4 vp = uView * vec4(q.pos, 1.0);
    vViewZ = vp.z;
    gl_Position = uProj * vp;
#endif
}
