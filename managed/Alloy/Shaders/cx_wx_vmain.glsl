void main()
{
    uint id = uint(gl_BaseInstance + gl_InstanceID) * 3u;
    vec4 at = uDrops[id], lit = uDrops[id + 1u];
    vec3 P = at.xyz;
    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    vec3 toCam = uEye - P;
    vec3 axis, side;
    if (uKind != 2) { axis = uFall; side = normalize(cross(uFall, toCam)); }
    else { side = normalize(cross(vec3(0.0, 0.0, 1.0), toCam)); axis = normalize(cross(toCam, side)); }
    vec3 pos = P + axis * (corner.y * at.w * 0.5) + side * (corner.x * uSize.y * 0.5);
    vUV = corner;
    vSeed = lit.w;
    vLight = lit.rgb;
    vBehind = uDrops[id + 2u].rgb;
    vec4 vp = uView * vec4(pos, 1.0);
    vViewZ = vp.z;
    gl_Position = uProj * vp;
}
