// One corner of a drop's or flake's camera-facing quad, from its five records (CxWxPlace): the vertex stage and the
// mesh stage both build it here, so the fragment stage sees one interface whichever path drew the drop.
#ifndef CX_WX_QUAD
#define CX_WX_QUAD
#include "cx_wx_drop.glsl"

/// What a corner hands the fragment stage, and where it stands.
struct CxWxCorner
{
    vec3 pos;      // world position
    vec2 uv;       // -1..1 across the quad
    float seed;    // a flake's hashed brightness, or a drop's share of its widened quad
    vec3 light;    // a flake's light, or the lamps on a drop
    vec3 behind;   // the sky seen through a flake, or overhead of a drop
    vec3 skyLow;   // the sky at the horizon behind a drop
    vec3 lampDir;  // toward the lamps lighting a drop
    float sunVis;  // the sun's visibility at a drop
    vec3 side;     // the quad's across axis
    vec3 up;       // the quad's along axis, the fall as the camera sees it
};

/// Corner `corner` (each of x and y is -1 or 1) of the drop in records r0..r4.
CxWxCorner CxWxQuad(vec4 r0, vec4 r1, vec4 r2, vec4 r3, vec4 r4, vec2 corner)
{
    CxWxCorner o;
    vec3 P = r0.xyz;
    vec3 toCam = uEye - P;
    float along, across;
    o.seed = r1.w;
    if (uKind == 2)
    {
        o.side = normalize(cross(vec3(0.0, 0.0, 1.0), toCam));
        o.up = normalize(cross(toCam, o.side));
        along = r0.w; across = uQuad.y;
    }
    else
    {
        // a bead faces the camera, stretched only along the fall it is seen to make
        vec3 s = cross(uFall, toCam);
        if (dot(s, s) < 1e-10) s = cross(vec3(1.0, 0.0, 0.0), toCam);
        o.side = normalize(s);
        o.up = normalize(cross(toCam, o.side));
        float vz = max(-(uView * vec4(P, 1.0)).z, 1e-3);
        vec3 sz = CxWxDropSize(uPxScale / vz, uSpeed * r4.w, uKind == 3 ? 0.0 : uBlur);
        along = sz.x; across = sz.y; o.seed = sz.z;
    }
    o.pos = P + o.up * (corner.y * along * 0.5) + o.side * (corner.x * across * 0.5);
    o.uv = corner;
    o.light = r1.rgb;
    o.behind = r2.rgb;
    o.skyLow = r4.rgb;
    o.lampDir = r3.xyz;
    o.sunVis = r3.w;
    return o;
}
#endif
