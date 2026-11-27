// The air's looks: how dust lies over a floor, how a layer takes the room's light and a lamp's, and the colour the
// sky lends what hangs outdoors. Every air main calls these (tools/gates/air-mains.sh pins each to its call), and
// the air goldens run them on the device.
#ifndef CX_AIR
#define CX_AIR
// Density of a dust layer `top` metres deep at `z` metres over its floor: thickest on the floor, falling straight to
// nothing at `top`, and integrating to 1 over the layer, so a column's depth times this is its dust per metre.
float CxAirDust(float z, float top)
{
    if (top <= 0.0 || z < 0.0 || z >= top) return 0.0;
    return 2.0 * (1.0 - z / top) / top;
}

// The mean of CxAirDust over heights h give or take hz, exactly: a march step that lays its whole span of height adds the
// same air wherever along the step it reads its columns, so the steps neither band nor need a dither. It divides by the
// span's width as given, never by its two ends' difference, which rounds to nothing far over a floor.
float CxAirDustSpan(float h, float hz, float top)
{
    if (top <= 0.0) return 0.0;
    float lo = clamp(h - hz, 0.0, top) / top, hi = clamp(h + hz, 0.0, top) / top;
    return (2.0 * (hi - lo) - (hi * hi - lo * lo)) / (2.0 * hz);
}

// Do neighbouring columns with floors `a` and `b` join into one floor, their rise at most `step`? A wall's floor lies
// far below anything, so a wall never joins.
bool CxAirJoined(float a, float b, float step)
{
    return abs(a - b) <= step;
}

// The shares two neighbouring column centres take a fraction `f` of the way from the first to the second: a straight
// blend over a joined floor, and over a wall or a ledge each fading to nothing halfway, so no air crosses the edge.
vec2 CxAirPair(float f, bool joined)
{
    return joined ? vec2(1.0 - f, f) : vec2(max(1.0 - 2.0 * f, 0.0), max(2.0 * f - 1.0, 0.0));
}

// The light a layer scatters: the room's `ambient`, plus a `lamp` `dist` metres off that reaches `reach` metres,
// falling off as the square of what is left of its reach and gone at its edge.
vec3 CxAirLit(vec3 ambient, vec3 lamp, float dist, float reach)
{
    float left = reach > 0.0 ? clamp(1.0 - dist / reach, 0.0, 1.0) : 0.0;
    return ambient + lamp * left * left;
}

// The colour an outdoor layer takes away from lamps: the light the ground takes, the area's `ambient` and its sun or
// moon `diffuse`, never the fog colour a builder tints the sky with.
vec3 CxAirSky(vec3 ambient, vec3 diffuse)
{
    return ambient + diffuse;
}
#endif
