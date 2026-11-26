// The dust lying over a crypt's floor and the night mist over water, one invocation per pixel of the main view after
// the point lights: it marches the part of the eye's ray that crosses the heights the air lies between (AirScene,
// packed by GroundMist.Pack), reads the dust and the mist there from the four nearest columns of the field by cx_air's
// profile, and lays over what the HDR target holds the light each scatters on the way -- the dust by the area's
// ambient, the mist by the sky, both by the lamps where the air is.
#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;

#include "cx_pass.glsl"
#include "cx_defblk.glsl"
#include "cx_air.glsl"

layout(binding = 7) uniform sampler2D uGbDepth;
layout(rgba16f, binding = 0) uniform image2D uHdr;
layout(std430, binding = 8) readonly buffer CxLightBuf { vec4 uLights[]; };
/// The field: its corner (x, y), its side in columns and 1 while anything shows; the sky's fog colour and the mist's
/// height; the lowest floor and the highest top the air reaches; then per column its floor, its mist's depth, its
/// dust's depth and the height that dust reaches, metres.
layout(std430, binding = 12) readonly buffer CxAirBuf { vec4 uAirHead; vec4 uAirSky; vec4 uAirSlab; vec4 uAirCells[]; };

/// Steps along the part of the ray the air lies across, and the farthest the march looks, metres.
const int CX_AIR_STEPS = 24;
const float CX_AIR_FAR = 64.0;
/// How strongly a metre of dust per metre of path dims and scatters, per unit of the profile: the floor 10 m off a
/// standing eye stays in sight through the dust.
const float CX_AIR_SIGMA = 0.5;
/// The rise between neighbouring columns past which their floors do not join (GroundMist.MaxStep), metres.
const float CX_AIR_STEP = 1.0;
/// How far under a column's floor a ray may end before that floor is the lip of a drop its air does not hang over.
const float CX_AIR_DROP = 2.0;
/// Points along the air a ray crosses where the lamps' light is taken, and lamps of the rig each walks.
const int CX_AIR_PROBES = 4;
const int CX_AIR_LAMPS = 32;

/// The cell of column c, or a cell with no floor off the field.
vec4 CxAirCell(ivec2 c)
{
    int side = int(uAirHead.z);
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(side)))) return vec4(-1e9, 0.0, 0.0, 0.0);
    return uAirCells[c.y * side + c.x];
}

/// The dust (x) and mist (y) per metre one cell lays at height z by its share w, none on a ray that ends in a drop below it.
vec2 CxAirLayer(vec4 cell, float w, float z, float end)
{
    if (w <= 0.0 || end < cell.x - CX_AIR_DROP) return vec2(0.0);
    return w * vec2(cell.z * CxAirDust(z - cell.x, cell.w), cell.y * CxAirDust(z - cell.x, uAirSky.w));
}

/// The dust (x) and mist (y) per metre at world point p on a ray that ends at height end, from the four nearest columns.
vec2 CxAirAt(vec3 p, float end)
{
    vec2 q = p.xy - uAirHead.xy - 0.5;
    ivec2 c = ivec2(floor(q));
    vec2 f = q - vec2(c);
    vec4 c00 = CxAirCell(c), c10 = CxAirCell(c + ivec2(1, 0)), c01 = CxAirCell(c + ivec2(0, 1)), c11 = CxAirCell(c + ivec2(1, 1));
    vec2 x0 = CxAirPair(f.x, CxAirJoined(c00.x, c10.x, CX_AIR_STEP)), x1 = CxAirPair(f.x, CxAirJoined(c01.x, c11.x, CX_AIR_STEP));
    vec2 y0 = CxAirPair(f.y, CxAirJoined(c00.x, c01.x, CX_AIR_STEP)), y1 = CxAirPair(f.y, CxAirJoined(c10.x, c11.x, CX_AIR_STEP));
    return CxAirLayer(c00, x0.x * y0.x, p.z, end) + CxAirLayer(c10, x0.y * y1.x, p.z, end)
         + CxAirLayer(c01, x1.x * y0.y, p.z, end) + CxAirLayer(c11, x1.y * y1.y, p.z, end);
}

/// The light the lamps of the rig lay on air at world point at, each colour made linear as the lights take it.
vec3 CxAirLamps(vec3 at)
{
    vec3 lamps = vec3(0.0);
    int n = min(uLightCount, CX_AIR_LAMPS);
    for (int i = 0; i < n; i++)
    {
        vec4 pr = uLights[i * 2];
        lamps += CxAirLit(vec3(0.0), pow(max(uLights[i * 2 + 1].rgb, vec3(0.0)), vec3($SRGB$)), distance(pr.xyz, at), pr.w);
    }
    return lamps * uDayDim;
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
    float t0 = 0.0, t1 = len;
    if (abs(dir.z) > 1e-6)
    {
        float ta = (uAirSlab.x - uEyePos.z) / dir.z, tb = (uAirSlab.y - uEyePos.z) / dir.z;
        t0 = max(t0, min(ta, tb));
        t1 = min(t1, max(ta, tb));
    }
    else if (uEyePos.z < uAirSlab.x || uEyePos.z > uAirSlab.y) return;
    if (t1 <= t0) return;
    float dl = (t1 - t0) / float(CX_AIR_STEPS);
    // interleaved gradient noise: each pixel's steps start at their own offset, so the steps dither instead of banding
    float jit = fract(52.9829189 * fract(dot(vec2(id), vec2(0.06711056, 0.00583715))));
    vec2 tau[CX_AIR_STEPS];
    int first = -1, last = -1;
    for (int k = 0; k < CX_AIR_STEPS; k++)
    {
        tau[k] = CxAirAt(uEyePos + dir * (t0 + dl * (float(k) + jit)), surf.z) * (CX_AIR_SIGMA * dl);
        if (tau[k].x + tau[k].y <= 0.0) continue;
        if (first < 0) first = k;
        last = k;
    }
    if (first < 0) return;
    float span = float(last - first);
    vec3 probe[CX_AIR_PROBES];
    for (int i = 0; i < CX_AIR_PROBES; i++)
        probe[i] = CxAirLamps(uEyePos + dir * (t0 + dl * (float(first) + span * float(i) / float(CX_AIR_PROBES - 1) + jit)));
    vec3 dust = CxAirLit(uAreaAmbient, vec3(0.0), 0.0, 0.0), sky = CxAirSky(uAirSky.rgb, uAreaDiffuse), lit = vec3(0.0);
    float keep = 1.0;
    for (int k = first; k <= last; k++)
    {
        float all = tau[k].x + tau[k].y;
        if (all <= 0.0) continue;
        float s = span > 0.0 ? float(k - first) / span * float(CX_AIR_PROBES - 1) : 0.0;
        int i = min(int(s), CX_AIR_PROBES - 2);
        vec3 lamp = mix(probe[i], probe[i + 1], s - float(i));
        lit += keep * (1.0 - exp(-all)) * (tau[k].x * (dust + lamp) + tau[k].y * (sky + lamp)) / all;
        keep *= exp(-all);
    }
    vec4 hdr = imageLoad(uHdr, id);
    imageStore(uHdr, id, vec4(hdr.rgb * keep + lit, hdr.a));
}
