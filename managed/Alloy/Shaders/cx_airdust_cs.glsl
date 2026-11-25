// The dust lying over a crypt's floor and the night mist over water, one invocation per pixel of the main view after
// the point lights: it marches the eye's ray to the surface through the columns of the air's field (AirScene, packed
// by GroundMist.Pack), sums the dust and the mist it crosses by cx_air's profile, and lays the light each scatters
// over what the HDR target holds -- the dust by the area's ambient, the mist by the sky, both by the lamps.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

#include "cx_pass.glsl"
#include "cx_defblk.glsl"
#include "cx_air.glsl"

layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(std430, binding = 8) readonly buffer CxLightBuf { vec4 uLights[]; };
/// The field: its corner (x, y), its side in columns and 1 while anything shows; the sky's fog colour and the mist's
/// height; then per column its floor, its mist's depth, its dust's depth and the height that dust reaches, metres.
layout(std430, binding = 12) readonly buffer CxAirBuf { vec4 uAirHead; vec4 uAirSky; vec4 uAirCells[]; };

/// Steps along the ray, and the farthest the march looks, metres: dust past it is too thin to read.
const int CX_AIR_STEPS = 24;
const float CX_AIR_FAR = 32.0;
/// How strongly a metre of dust per metre of path dims and scatters, per unit of the profile.
const float CX_AIR_SIGMA = 6.0;
/// Lamps the dust's light walks, from the front of the rig.
const int CX_AIR_LAMPS = 32;

/// The cell under world (x, y), or a cell with no floor off the field.
vec4 CxAirCell(vec2 p)
{
    ivec2 c = ivec2(floor(p - uAirHead.xy));
    int side = int(uAirHead.z);
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(side)))) return vec4(-1e9, 0.0, 0.0, 0.0);
    return uAirCells[c.y * side + c.x];
}

/// The light the lamps of the rig lay on air at world point at.
vec3 CxAirLamps(vec3 at)
{
    vec3 lamps = vec3(0.0);
    int n = min(uLightCount, CX_AIR_LAMPS);
    for (int i = 0; i < n; i++)
    {
        vec4 pr = uLights[i * 2];
        lamps += CxAirLit(vec3(0.0), uLights[i * 2 + 1].rgb, distance(pr.xyz, at), pr.w);
    }
    return lamps;
}

void main()
{
    if (any(greaterThanEqual(gl_GlobalInvocationID.xy, uvec2(uRect.zw))) || uAirHead.w < 0.5) return;
    ivec2 id = ivec2(gl_GlobalInvocationID.xy) + uRect.xy;
    float d = texelFetch(uGbDepth, id, 0).r;
    vec4 w = uInvVp * vec4(2.0 * (vec2(gl_GlobalInvocationID.xy) + 0.5) / vec2(uRect.zw) - 1.0, d, 1.0);
    vec3 surf = w.xyz / w.w;
    vec3 ray = surf - uEyePos;
    float len = min(length(ray), CX_AIR_FAR);
    if (len <= 0.0) return;
    vec3 dir = normalize(ray);
    float dl = len / float(CX_AIR_STEPS);
    float tau = 0.0, fog = 0.0;
    vec3 mid = vec3(0.0), wet = vec3(0.0);
    for (int k = 0; k < CX_AIR_STEPS; k++)
    {
        vec3 p = uEyePos + dir * (dl * (float(k) + 0.5));
        vec4 cell = CxAirCell(p.xy);
        float rho = cell.z * CxAirDust(p.z - cell.x, cell.w) * CX_AIR_SIGMA * dl;
        float mist = cell.y * CxAirDust(p.z - cell.x, uAirSky.w) * CX_AIR_SIGMA * dl;
        tau += rho;
        mid += p * rho;
        fog += mist;
        wet += p * mist;
    }
    float all = tau + fog;
    if (all <= 1e-5) return;
    vec3 dust = tau > 0.0 ? CxAirLit(uAreaAmbient, vec3(0.0), 0.0, 0.0) + CxAirLamps(mid / tau) : vec3(0.0);
    vec3 sky = fog > 0.0 ? CxAirSky(uAirSky.rgb, uAreaDiffuse) + CxAirLamps(wet / fog) : vec3(0.0);
    vec3 lit = (dust * tau + sky * fog) / all;
    float keep = exp(-all);
    vec4 hdr = imageLoad(uHdr, id);
    imageStore(uHdr, id, vec4(hdr.rgb * keep + lit * (1.0 - keep), hdr.a));
}
