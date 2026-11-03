#version 460 core
// CX-419: a mip chain in ONE dispatch. A group folds a 64x64 tile of its base level six times -- twice in
// registers, four times through shared memory -- and stores each level through its own storage view, so up to
// seven levels cost one dispatch and one barrier instead of one of each per level. A deeper chain is a second
// dispatch whose base is the last level this one wrote.
//
// A level's texel is the 2x2 of the level below, which is the footprint the reflection trace's cell index
// assumes: cell c at level l covers pixels [c << l, (c + 1) << l). A level of odd width therefore has a last
// column no texel above it covers -- the same column the trace's own index cannot reach, the device sizing
// each mip by halving and flooring. Everything a stored texel folds is in its own group, so the only reads
// clamped to an edge are ones no stored texel takes.
//
// CX_SPD_HIZ: the base level is the frame depth as INVERSE view depth (1/w, which is linear in screen space,
// so the march interpolates it exactly) and each level the MAX -- the CLOSEST surface in the cell, which a ray
// must reach before it can hit anything there. Otherwise the base is the source sampled at the chain's size
// and each level is the box average.
layout(local_size_x = 16, local_size_y = 16) in;
layout(binding = 0) uniform sampler2D uSrc;   // the base level's source; read only while uGen is set
layout(push_constant) uniform CxSpd { vec2 uNf; int uLevels; int uGen; };

#ifdef CX_SPD_HIZ
#define CxV float
#define CxLoad(img, p) (imageLoad(img, p).r)
#define CxStore(img, p, v) imageStore(img, p, vec4(v))
layout(r32f, binding = 0) uniform image2D uDst0;
layout(r32f, binding = 1) writeonly uniform image2D uDst1;
layout(r32f, binding = 2) writeonly uniform image2D uDst2;
layout(r32f, binding = 3) writeonly uniform image2D uDst3;
layout(r32f, binding = 4) writeonly uniform image2D uDst4;
layout(r32f, binding = 5) writeonly uniform image2D uDst5;
layout(r32f, binding = 6) writeonly uniform image2D uDst6;
CxV CxFold(CxV a, CxV b, CxV c, CxV d) { return max(max(a, b), max(c, d)); }
CxV CxBase(ivec2 p, ivec2 sz)
{
    float d = texelFetch(uSrc, p, 0).r;
    return (uNf.x + d * (uNf.y - uNf.x)) / (uNf.x * uNf.y);
}
#else
#define CxV vec4
#define CxLoad(img, p) (imageLoad(img, p))
#define CxStore(img, p, v) imageStore(img, p, v)
layout(rgba16f, binding = 0) uniform image2D uDst0;
layout(rgba16f, binding = 1) writeonly uniform image2D uDst1;
layout(rgba16f, binding = 2) writeonly uniform image2D uDst2;
layout(rgba16f, binding = 3) writeonly uniform image2D uDst3;
layout(rgba16f, binding = 4) writeonly uniform image2D uDst4;
layout(rgba16f, binding = 5) writeonly uniform image2D uDst5;
layout(rgba16f, binding = 6) writeonly uniform image2D uDst6;
CxV CxFold(CxV a, CxV b, CxV c, CxV d) { return 0.25 * (a + b + c + d); }
// a texel centre of the base level, between four source texels, is their 2:1 box
CxV CxBase(ivec2 p, ivec2 sz) { return textureLod(uSrc, (vec2(p) + 0.5) / vec2(sz), 0.0); }
#endif

// Level 2 at 0 (16x16), level 3 at 256 (8x8), level 4 at 320 (4x4), level 5 at 336 (2x2). Each level has its
// own slice, so one barrier between phases is enough: no thread overwrites a value another still reads.
#define CX_SPD_SM3 256
#define CX_SPD_SM4 320
#define CX_SPD_SM5 336
shared CxV gSm[340];

/// This level's extent, as the device sizes a mip: halved and floored, never under one.
ivec2 CxSpdSize(ivec2 sz0, int level) { return max(sz0 >> level, ivec2(1)); }

void main()
{
    ivec2 sz0 = imageSize(uDst0);
    ivec2 g = ivec2(gl_WorkGroupID.xy) * 64, t = ivec2(gl_LocalInvocationID.xy);
    ivec2 lim0 = sz0 - ivec2(1), sz1 = CxSpdSize(sz0, 1);
    CxV a[4], v[4];
    for (int q = 0; q < 4; q++)
    {
        ivec2 qo = ivec2(q & 1, q >> 1);
        for (int n = 0; n < 4; n++)
        {
            ivec2 p = g + t * 4 + qo * 2 + ivec2(n & 1, n >> 1);
            v[n] = uGen != 0 ? CxBase(min(p, lim0), sz0) : CxLoad(uDst0, min(p, lim0));
            if (uGen != 0 && all(lessThan(p, sz0))) CxStore(uDst0, p, v[n]);
        }
        a[q] = CxFold(v[0], v[1], v[2], v[3]);
        ivec2 p1 = (g >> 1) + t * 2 + qo;
        if (uLevels > 1 && all(lessThan(p1, sz1))) CxStore(uDst1, p1, a[q]);
    }
    CxV r = CxFold(a[0], a[1], a[2], a[3]);
    ivec2 p2 = (g >> 2) + t;
    if (uLevels > 2 && all(lessThan(p2, CxSpdSize(sz0, 2)))) CxStore(uDst2, p2, r);
    gSm[t.y * 16 + t.x] = r;

    barrier();
    if (all(lessThan(t, ivec2(8))))
    {
        int i = t.y * 32 + t.x * 2;
        r = CxFold(gSm[i], gSm[i + 1], gSm[i + 16], gSm[i + 17]);
        ivec2 p = (g >> 3) + t;
        if (uLevels > 3 && all(lessThan(p, CxSpdSize(sz0, 3)))) CxStore(uDst3, p, r);
        gSm[CX_SPD_SM3 + t.y * 8 + t.x] = r;
    }
    barrier();
    if (all(lessThan(t, ivec2(4))))
    {
        int i = CX_SPD_SM3 + t.y * 16 + t.x * 2;
        r = CxFold(gSm[i], gSm[i + 1], gSm[i + 8], gSm[i + 9]);
        ivec2 p = (g >> 4) + t;
        if (uLevels > 4 && all(lessThan(p, CxSpdSize(sz0, 4)))) CxStore(uDst4, p, r);
        gSm[CX_SPD_SM4 + t.y * 4 + t.x] = r;
    }
    barrier();
    if (all(lessThan(t, ivec2(2))))
    {
        int i = CX_SPD_SM4 + t.y * 8 + t.x * 2;
        r = CxFold(gSm[i], gSm[i + 1], gSm[i + 4], gSm[i + 5]);
        ivec2 p = (g >> 5) + t;
        if (uLevels > 5 && all(lessThan(p, CxSpdSize(sz0, 5)))) CxStore(uDst5, p, r);
        gSm[CX_SPD_SM5 + t.y * 2 + t.x] = r;
    }
    barrier();
    if (uLevels > 6 && all(equal(t, ivec2(0))))
    {
        r = CxFold(gSm[CX_SPD_SM5], gSm[CX_SPD_SM5 + 1], gSm[CX_SPD_SM5 + 2], gSm[CX_SPD_SM5 + 3]);
        ivec2 p = g >> 6;
        if (all(lessThan(p, CxSpdSize(sz0, 6)))) CxStore(uDst6, p, r);
    }
}
