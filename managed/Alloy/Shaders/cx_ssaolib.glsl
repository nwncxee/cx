// Shared by both stages: view-space reconstruction off the frame depth and the pass's own
// projection -- the same two coefficients the particle fade linearises with, so nothing can drift.
layout(binding = 0) uniform sampler2D uDepth;    // the frame depth after the prepass
// CX-316: every non-opaque term all three stages take, one std140 block filled once per pass by SsaoBlock.
// A stage reads the members it needs and ignores the rest; the layout is the same for all of them.
layout(std140, binding = 3) uniform CxSsao
{
    mat4 uProj;      // this pass's projection, raw engine bytes (see CxViewDist)
    vec4 uRt;        // (1/w, 1/h, w, h) of the pass's own target
    float uRadius;   // world units
    float uPower;    // visibility exponent, the option's intensity folded in
    float uMaxPx;    // screen-radius clamp
    float uWeight;   // #390: the history's share; 0 = take the frame whole
    int uDirs;       // slices
    int uSteps;      // steps per side
    int uFrame;      // #390: the noise phase; 0 on the single-frame path
};

// Positive view distance: z_clip = z_v*M22 + M32, w_clip = -z_v, so z_v = -M32 / (ndc + M22).
float CxViewDist(float depth)
{
    return uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z);
}
// View-space position (camera at the origin looking down -z) of the pixel at uv.
vec3 CxViewPos(vec2 uv)
{
    float dist = CxViewDist(textureLod(uDepth, uv, 0.0).r);
    vec2 n = uv * 2.0 - 1.0;
    return vec3(n.x * dist / uProj[0].x, n.y * dist / uProj[1].y, -dist);
}
