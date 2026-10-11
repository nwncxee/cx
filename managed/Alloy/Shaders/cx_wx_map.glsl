#ifndef CX_WX_MAP
#define CX_WX_MAP
#include "cx_wx_soak.glsl"
// R2: the area wetness map (Weather.Map) -- one world-XY texture per area, updated by the two weather
// compute passes and READ here. Every consumer goes through these three functions.
//   x  damp wetness, 0..1, integrated per texel at the soak/dry rates
//   y  the basin's water level, 0..1 of the puddle-depth knob
//   z  world Z of the top-down first static hit (what the rain lands on); <= CX_WX_UNVISITED = the
//      cover view has never reached this texel, so a consumer falls back to its own exposure read
//   w  metres this texel stands above its basin's own low point -- the relief the water fills against
// Past the 0..15 texture budget on purpose, for cx_localshadow's reason: every unit below is spent in
// the lit bracket and every driver Alloy has met reports 32 per stage (TextureSlots.WetMap).
layout(binding = 18) uniform sampler2D uWxMap;
// x,y the world XY of the map's origin corner, z texels per metre (0 = no map), w the wet-surfaces knob
uniform vec4 uWxMapXf;
// x puddle depth at a full basin (m), y shoreline feather (m), z the height a point may stand under the
// latched surface and still count as open (m), w unused
uniform vec4 uWxMapP;   // x puddle depth (m), y shoreline edge (m), z shelter metres, w the puddles knob 0..1

const float CX_WX_UNVISITED = -1000.0;

/// This point's map record, or an unvisited one where there is no map or the point is outside the area.
vec4 CxWetMap(vec2 worldXY)
{
    vec4 none = vec4(0.0, 0.0, CX_WX_UNVISITED - 1.0, 0.0);
    if (uWxMapXf.z <= 0.0) return none;
    vec2 uv = (worldXY - uWxMapXf.xy) * uWxMapXf.z / vec2(textureSize(uWxMap, 0));
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return none;
    return texture(uWxMap, uv);
}

/// Has the cover view reached this texel? A bilinear fetch across the map's frontier mixes the sentinel
/// in, which reads unvisited too -- one texel of fallback border, which is the safe side.
bool CxWetMapped(vec4 m) { return m.z > CX_WX_UNVISITED; }

/// 1 where this point IS the surface the map latched (open to the sky), 0 where it stands under it: the
/// map's own shelter test, one fetch instead of the cover map's filtered compare.
float CxWetShelter(vec4 m, float worldZ)
{
    return 1.0 - smoothstep(uWxMapP.z, uWxMapP.z * 3.0, m.z - worldZ);
}

/// Metres of standing water over this point: the basin's level less how far this texel stands above its
/// low point. A draining level uncovers the shoreline first, which is what makes a puddle shrink inward.
float CxWetDepth(vec4 m) { return max(m.y * uWxMapP.x - m.w, 0.0); }
#endif
