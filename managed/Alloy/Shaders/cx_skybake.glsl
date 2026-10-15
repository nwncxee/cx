// CX-290: the sky-visibility bake. Once per area -- and again when the static geometry changes -- this
// turns the overhead depth view of the area's still casters into one small map the lit stage reads with a
// single tap: R is the column's top surface in world metres, G the share of the sky that surface sees.
// The horizon estimate IS CX.Alloy.SkyMath.Openness, which is the self-checked reference; the ladder below
// and the C# one must agree, so a change to either is a change to both.
// A texel no caster covered stores a height far below the world and full openness, so neither a fragment
// over it nor a neighbouring sweep reads it as cover.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rg16f, binding = 0) writeonly uniform image2D uOut;
uniform sampler2DArray uSkyDepth;   // the overhead view's depth layer, compare mode off
uniform ivec2 uSize;
uniform vec2 uSkyZ;                 // world z at device depth 0, and metres of world z per unit of depth
uniform vec2 uSkyFit;               // x = texture-coordinate units per metre, y = the sweep's range in metres

const int CX_SKY_DIRS = 8;
const int CX_SKY_STEPS = 4;
const float CX_SKY_NEAR_M = 1.0;
const float CX_SKY_VOID_M = -8192.0;

/// The top surface at one texture coordinate, in world metres.
float CxSkyH(vec2 uv)
{
    ivec2 s = textureSize(uSkyDepth, 0).xy;
    float d = texelFetch(uSkyDepth, ivec3(clamp(ivec2(uv * vec2(s)), ivec2(0), s - 1), 0), 0).r;
    return d >= 1.0 ? CX_SKY_VOID_M : uSkyZ.x + d * uSkyZ.y;
}

void main()
{
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(t, uSize))) return;
    vec2 uv = (vec2(t) + 0.5) / vec2(uSize);
    float h = CxSkyH(uv);
    if (h <= CX_SKY_VOID_M) { imageStore(uOut, t, vec4(CX_SKY_VOID_M, 1.0, 0.0, 0.0)); return; }
    float open = 0.0;
    for (int j = 0; j < CX_SKY_DIRS; j++)
    {
        float a = float(j) * (6.2831853 / float(CX_SKY_DIRS));
        vec2 dir = vec2(cos(a), sin(a));
        float tmax = 0.0;
        for (int k = 0; k < CX_SKY_STEPS; k++)
        {
            float r = CX_SKY_NEAR_M * pow(max(uSkyFit.y, CX_SKY_NEAR_M) / CX_SKY_NEAR_M,
                                          float(k) / float(CX_SKY_STEPS - 1));
            tmax = max(tmax, (CxSkyH(uv + dir * (r * uSkyFit.x)) - h) / r);
        }
        open += 1.0 / (1.0 + tmax * tmax);
    }
    imageStore(uOut, t, vec4(h, open / float(CX_SKY_DIRS), 0.0, 0.0));
}
