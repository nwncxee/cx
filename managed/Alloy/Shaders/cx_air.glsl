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

// The light a layer scatters: the room's `ambient`, plus a `lamp` `dist` metres off that reaches `reach` metres,
// falling off as the square of what is left of its reach and gone at its edge.
vec3 CxAirLit(vec3 ambient, vec3 lamp, float dist, float reach)
{
    float left = reach > 0.0 ? clamp(1.0 - dist / reach, 0.0, 1.0) : 0.0;
    return ambient + lamp * left * left;
}

// The colour an outdoor layer takes away from lamps: the area's `fog` colour lit by its sun or moon `diffuse`, so a
// black fog under the moon still shows the moon's colour.
vec3 CxAirSky(vec3 fog, vec3 diffuse)
{
    return fog + diffuse;
}
#endif
