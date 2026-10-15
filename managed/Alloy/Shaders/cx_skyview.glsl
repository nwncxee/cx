// CX-290: the sky-visibility term -- how much open sky a point sees, off the one map cx_skybake writes
// (R = the column's top surface in world metres, G = the sky share that surface sees). It scales the AREA
// AMBIENT, so an alley or a covered walkway is darker than the open street with no content change, where a
// vanilla area has one ambient for the whole of it. Interiors have no sky: uSkyTerm.x is 0 there.
// One tap and a handful of multiplies per fragment; the sweep behind it ran once per area.
// CX_SKY_VIEW comes out of the stage on a driver too short of fragment texture units to seat uSkyMap
// (Programs.WithoutSkyView), and the term is then 1 everywhere.
#ifndef CX_SKYVIEW_LIB
#define CX_SKYVIEW_LIB
#ifdef CX_SKY_VIEW
layout(binding = 31) uniform sampler2D uSkyMap;
uniform mat4 uSkyVp;     // world -> the overhead view's clip space
uniform vec4 uSkyTerm;   // x = strength (0 = off), y = normal step (m), z = cover band (m), w = rise band (m)

float CxSkyView(vec3 P, vec3 N)
{
    if (uSkyTerm.x <= 0.0) return 1.0;
    // The lookup steps OFF the surface along its own normal, so each class of surface asks the right
    // column: a wall face asks about the open ground beside it instead of its own roofline, a ceiling asks
    // about the space it hangs over, and level ground asks about itself.
    vec3 probe = P + N * uSkyTerm.y;
    vec4 c = uSkyVp * vec4(probe, 1.0);
    vec2 uv = c.xy / c.w * 0.5 + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return 1.0;
    vec2 m = texture(uSkyMap, uv).rg;
    float dz = probe.z - m.x;
    // well above the column's top surface the point sees more sky than that surface does (the step itself
    // is discounted, so level ground reads the baked value exactly), and well under it there is a roof
    float open = mix(m.y, 1.0, clamp((dz - uSkyTerm.y) / max(uSkyTerm.w, 1e-3), 0.0, 1.0));
    open *= 1.0 - clamp(-dz / max(uSkyTerm.z, 1e-3), 0.0, 1.0);
    return 1.0 - uSkyTerm.x * (1.0 - open);
}
#else
float CxSkyView(vec3 P, vec3 N) { return 1.0; }
#endif
#endif
