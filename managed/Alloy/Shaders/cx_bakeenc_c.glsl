#version 460 core
// The lightmap finish as a block encoder, one invocation per 4x4 block of the page region. Each texel
// finishes as the fragment finish did: covered = as baked, uncovered within two of a covered texel = that
// neighbour (nearest first), else no texel. A goes out as one BC6H block (mode 11 or mode 1, fitted in
// half-bit space), B as one BC7 block (mode 6), C straight into the page layer where the finish has a texel.
// Every fit brackets its texels with endpoints quantised OUTWARD, so a flat block keeps its index steps.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 0) uniform sampler2D uScrA;   // sum rgb + coverage alpha
layout(binding = 1) uniform sampler2D uScrB;   // sum luminance * direction
layout(binding = 2) uniform sampler2D uScrC;   // per mover channel, its luminance
layout(binding = 0, rgba32ui) writeonly uniform uimage2D uBlkA;
layout(binding = 1, rgba32ui) writeonly uniform uimage2D uBlkB;
layout(binding = 2, rgba8) writeonly uniform image2D uPageC;
uniform ivec4 uRegion;   // first block, block count
const vec3 CX_LUM = vec3(0.2126, 0.7152, 0.0722);
const uint CX_W4[16] = uint[16](0u, 4u, 9u, 13u, 17u, 21u, 26u, 30u, 34u, 38u, 43u, 47u, 51u, 55u, 60u, 64u);
const uint CX_W3[8] = uint[8](0u, 9u, 18u, 27u, 37u, 46u, 55u, 64u);
// The format's 32 two-region partitions (bit i set = texel i in region 1) and region 1's anchor texel.
const uint CX_P2[32] = uint[32](0xCCCCu, 0x8888u, 0xEEEEu, 0xECC8u, 0xC880u, 0xFEECu, 0xFEC8u, 0xEC80u,
                                0xC800u, 0xFFECu, 0xFE80u, 0xE800u, 0xFFE8u, 0xFF00u, 0xFFF0u, 0xF000u,
                                0xF710u, 0x008Eu, 0x7100u, 0x08CEu, 0x008Cu, 0x7310u, 0x3100u, 0x8CCEu,
                                0x088Cu, 0x3110u, 0x6666u, 0x366Cu, 0x17E8u, 0x0FF0u, 0x718Eu, 0x399Cu);
const uint CX_A2[32] = uint[32](15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u,
                                15u, 2u, 8u, 2u, 2u, 8u, 8u, 15u, 2u, 8u, 2u, 2u, 8u, 8u, 2u, 2u);

bool Finish(ivec2 p, ivec2 size, out vec3 a, out vec3 d, out vec4 c)
{
    vec4 sa = texelFetch(uScrA, p, 0);
    vec4 sb = vec4(0.0), sc = vec4(0.0);
    a = vec3(0.0); d = vec3(0.0); c = vec4(0.0);
    if (sa.a > 0.0) { sb = texelFetch(uScrB, p, 0); sc = texelFetch(uScrC, p, 0); }
    else
    {
        float best = 1e9;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                ivec2 q = p + ivec2(dx, dy);
                if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, size))) continue;
                vec4 s = texelFetch(uScrA, q, 0);
                float k = float(dx * dx + dy * dy);
                if (s.a > 0.0 && k < best) { best = k; sa = s; sb = texelFetch(uScrB, q, 0); sc = texelFetch(uScrC, q, 0); }
            }
        if (best > 1e8) return false;
    }
    float l = length(sb.xyz);
    vec3 dir = l > 1e-6 ? sb.xyz / l : vec3(0.0, 0.0, 1.0);
    a = max(sa.rgb, vec3(0.0));
    d = dir * 0.5 + 0.5;
    c = clamp(sc / max(dot(sa.rgb, CX_LUM), 1e-6), 0.0, 1.0);
    return true;
}

void Put(inout uvec4 b, uint pos, uint n, uint v)
{
    uint i = pos >> 5u, o = pos & 31u;
    b[i] |= v << o;
    if (o + n > 32u) b[i + 1u] |= v >> (32u - o);
}

// The block's points, per invocation (A in half-bit space, then B in 8-bit space), and two index sets.
vec3 gP[16];
uint gIdx[16], gCur[16];

// The masked points' principal axis by power iteration on their covariance, from the box diagonal.
vec3 Axis(mat3 cov, vec3 start)
{
    vec3 ax = dot(start, start) > 0.0 ? start : vec3(1.0);
    for (int k = 0; k < 6; k++)
    {
        vec3 v = cov * ax;
        float s = max(abs(v.x), max(abs(v.y), abs(v.z)));
        if (s <= 1e-20) break;
        ax = v / s;
    }
    return normalize(ax);
}

// The masked points' principal line, clipped to their extent.
void Line(uint mask, out vec3 lo, out vec3 hi)
{
    vec3 m = vec3(0.0), mn = vec3(1e30), mx = vec3(-1e30);
    float n = 0.0;
    for (int i = 0; i < 16; i++)
        if ((mask & (1u << i)) != 0u) { m += gP[i]; n += 1.0; mn = min(mn, gP[i]); mx = max(mx, gP[i]); }
    m /= n;
    vec3 d = vec3(0.0), o = vec3(0.0);
    for (int i = 0; i < 16; i++)
    {
        if ((mask & (1u << i)) == 0u) continue;
        vec3 v = gP[i] - m;
        d += v * v; o += v.xxy * v.yzz;
    }
    vec3 ax = Axis(mat3(d.x, o.x, o.y, o.x, d.y, o.z, o.y, o.z, d.z), mx - mn);
    float t0 = 1e30, t1 = -1e30;
    for (int i = 0; i < 16; i++)
        if ((mask & (1u << i)) != 0u) { float t = dot(gP[i] - m, ax); t0 = min(t0, t); t1 = max(t1, t); }
    lo = m + ax * t0; hi = m + ax * t1;
}

// Raw moments of the masked points -- count, sum, squares, cross terms -- so a partition's second region
// is the block's total less its first.
struct Mom { float n; vec3 s, d, o; };
Mom Moments(uint mask)
{
    Mom m = Mom(0.0, vec3(0.0), vec3(0.0), vec3(0.0));
    for (int i = 0; i < 16; i++)
        if ((mask & (1u << i)) != 0u) { vec3 p = gP[i]; m.n += 1.0; m.s += p; m.d += p * p; m.o += p.xxy * p.yzz; }
    return m;
}

// What one line with 8 index steps should cost the region: the spread off its axis, plus the axis's own
// variance over the 7 gaps squared (an even spread's rounding).
float Spread(Mom m)
{
    if (m.n < 1.0) return 0.0;
    vec3 mu = m.s / m.n, d = m.d - m.s * mu, o = m.o - m.s.xxy * mu.yzz;
    mat3 cov = mat3(d.x, o.x, o.y, o.x, d.y, o.z, o.y, o.z, d.z);
    vec3 ax = Axis(cov, d.x >= d.y && d.x >= d.z ? cov[0] : d.y >= d.z ? cov[1] : cov[2]);
    float l1 = dot(ax, cov * ax);
    return max(d.x + d.y + d.z - l1, 0.0) + l1 / 49.0;
}

uint Weight(uint bits, uint k) { return bits == 4u ? CX_W4[k] : CX_W3[k]; }

// Per masked point, the nearest interpolant between the decoded endpoints into gCur; returns the squared error.
float Assign(uint mask, vec3 d0, vec3 d1, uint bits)
{
    vec3 e = d1 - d0;
    float ee = dot(e, e), err = 0.0;
    uint n = 1u << bits;
    for (int i = 0; i < 16; i++)
    {
        if ((mask & (1u << i)) == 0u) continue;
        float w = ee > 0.0 ? clamp(dot(gP[i] - d0, e) / ee, 0.0, 1.0) * 64.0 : 0.0;
        uint best = 0u; float bd = 1e30;
        for (uint k = 0u; k < n; k++) { float dd = abs(float(Weight(bits, k)) - w); if (dd < bd) { bd = dd; best = k; } }
        gCur[i] = best;
        vec3 r = gP[i] - mix(d0, d1, float(Weight(bits, best)) / 64.0);
        err += dot(r, r);
    }
    return err;
}

// An anchor texel's index must have its top bit clear: past the middle, the subset swaps its endpoints.
bool Anchor(uint a, uint bits, uint mask)
{
    uint top = (1u << bits) - 1u;
    if (gCur[a] <= top >> 1) return false;
    for (int i = 0; i < 16; i++) if ((mask & (1u << i)) != 0u) gCur[i] = top - gCur[i];
    return true;
}

// BC6H unsigned, 10-bit endpoints: the half-bit value a quantised endpoint decodes to.
float Bc6Dec(uint q) { return q == 0u ? 0.0 : q >= 1023u ? 31743.0 : float(q * 31u + 15u); }
vec3 Bc6Dec(uvec3 q) { return vec3(Bc6Dec(q.x), Bc6Dec(q.y), Bc6Dec(q.z)); }
uint Bc6Down(float v) { return v < 15.0 ? 0u : min(uint((v - 15.0) / 31.0), 1023u); }
uint Bc6Up(float v) { return v <= 0.0 ? 0u : clamp(uint(ceil((v - 15.0) / 31.0)), 1u, 1023u); }

// One subset's line, its endpoints quantised outward to 10 bits, its indices; returns the squared error.
float Bc6Fit(uint mask, uint bits, out uvec3 q0, out uvec3 q1)
{
    q0 = uvec3(0u); q1 = uvec3(0u);
    if (mask == 0u) return 0.0;
    vec3 lo, hi;
    Line(mask, lo, hi);
    for (int k = 0; k < 3; k++)
    {
        bool up = lo[k] <= hi[k];
        q0[k] = up ? Bc6Down(lo[k]) : Bc6Up(lo[k]);
        q1[k] = up ? Bc6Up(hi[k]) : Bc6Down(hi[k]);
    }
    return Assign(mask, Bc6Dec(q0), Bc6Dec(q1), bits);
}

void Swap(inout uvec3 a, inout uvec3 b) { uvec3 t = a; a = b; b = t; }

// Half-bit error a block may keep without the partition search: a few half ulps a texel.
const float CX_BC6_GOOD = 16.0 * 9.0;

// Mode 11 (one line, 4-bit indices) against mode 1 (two lines, 10-bit base and 5-bit deltas, 3-bit indices)
// over the partition whose moments promise the least: two coloured lights spread a block off any one line.
uvec4 Bc6h(uint mask)
{
    uvec3 w, x, y, z;
    float best = Bc6Fit(mask, 4u, w, x);
    if (Anchor(0u, 4u, mask)) Swap(w, x);
    for (int i = 0; i < 16; i++) gIdx[i] = gCur[i];
    int part = -1;
    if (best > CX_BC6_GOOD)
    {
        Mom t = Moments(mask);
        float pe = 1e30;
        int pick = -1;
        for (int p = 0; p < 32; p++)
        {
            uint m1 = mask & CX_P2[p];
            if (m1 == 0u || m1 == mask) continue;
            Mom b = Moments(m1);
            float e = Spread(b) + Spread(Mom(t.n - b.n, t.s - b.s, t.d - b.d, t.o - b.o));
            if (e < pe) { pe = e; pick = p; }
        }
        if (pick >= 0)
        {
            uint m1 = mask & CX_P2[pick], m0 = mask & ~CX_P2[pick];
            uvec3 a0, a1, b0, b1;
            float e = Bc6Fit(m0, 3u, a0, a1) + Bc6Fit(m1, 3u, b0, b1);
            if (Anchor(0u, 3u, m0)) Swap(a0, a1);
            if (Anchor(CX_A2[pick], 3u, m1)) Swap(b0, b1);
            ivec3 dx = ivec3(a1) - ivec3(a0), dy = ivec3(b0) - ivec3(a0), dz = ivec3(b1) - ivec3(a0);
            ivec3 lo = min(dx, min(dy, dz)), hi = max(dx, max(dy, dz));
            if (e < best && all(greaterThanEqual(lo, ivec3(-16))) && all(lessThanEqual(hi, ivec3(15))))
            {
                part = pick; w = a0; x = uvec3(dx); y = uvec3(dy); z = uvec3(dz);
                for (int i = 0; i < 16; i++) gIdx[i] = gCur[i];
            }
        }
    }
    uvec4 b = uvec4(0u);
    if (part < 0)
    {
        Put(b, 0u, 5u, 3u);   // mode 11
        Put(b, 5u, 10u, w.x); Put(b, 15u, 10u, w.y); Put(b, 25u, 10u, w.z);
        Put(b, 35u, 10u, x.x); Put(b, 45u, 10u, x.y); Put(b, 55u, 10u, x.z);
        Put(b, 65u, 3u, gIdx[0]);
        for (uint i = 1u; i < 16u; i++) Put(b, 64u + 4u * i, 4u, gIdx[i]);
        return b;
    }
    x &= 31u; y &= 31u; z &= 31u;
    // mode 1's field order is the format's own, not the endpoints' -- w base, x/y/z deltas, d the partition
    Put(b, 0u, 2u, 0u);
    Put(b, 2u, 1u, y.y >> 4); Put(b, 3u, 1u, y.z >> 4); Put(b, 4u, 1u, z.z >> 4);
    Put(b, 5u, 10u, w.x); Put(b, 15u, 10u, w.y); Put(b, 25u, 10u, w.z);
    Put(b, 35u, 5u, x.x); Put(b, 40u, 1u, z.y >> 4); Put(b, 41u, 4u, y.y & 15u); Put(b, 45u, 5u, x.y);
    Put(b, 50u, 1u, z.z & 1u); Put(b, 51u, 4u, z.y & 15u); Put(b, 55u, 5u, x.z); Put(b, 60u, 1u, (z.z >> 1) & 1u);
    Put(b, 61u, 4u, y.z & 15u); Put(b, 65u, 5u, y.x); Put(b, 70u, 1u, (z.z >> 2) & 1u); Put(b, 71u, 5u, z.x);
    Put(b, 76u, 1u, (z.z >> 3) & 1u); Put(b, 77u, 5u, uint(part));
    uint pos = 82u;
    for (uint i = 0u; i < 16u; i++)
    {
        uint n = i == 0u || i == CX_A2[part] ? 2u : 3u;
        Put(b, pos, n, gIdx[i]);
        pos += n;
    }
    return b;
}

// BC7 mode 6: a 7-bit endpoint plus a p-bit shared by its channels. Each endpoint takes the p-bit that
// lands it nearest while still rounding outward per channel.
uvec4 Bc7Endpoint(vec3 v, bvec3 down)
{
    uvec4 best = uvec4(0u); float bc = 1e30;
    for (uint p = 0u; p < 2u; p++)
    {
        vec3 h = (v - float(p)) * 0.5;
        uvec3 c = uvec3(clamp(mix(ceil(h), floor(h), vec3(down)), 0.0, 127.0));
        float cost = dot(abs(vec3(c * 2u + p) - v), vec3(1.0));
        if (cost < bc) { bc = cost; best = uvec4(c, p); }
    }
    return best;
}

uvec4 Bc7(uint mask)
{
    vec3 lo, hi;
    Line(mask, lo, hi);
    bvec3 up = lessThanEqual(lo, hi);
    uvec4 e0 = Bc7Endpoint(lo, up), e1 = Bc7Endpoint(hi, not(up));
    for (int i = 0; i < 16; i++) gCur[i] = 0u;
    Assign(mask, vec3(e0.xyz * 2u + e0.w), vec3(e1.xyz * 2u + e1.w), 4u);
    if (Anchor(0u, 4u, mask)) { uvec4 t = e0; e0 = e1; e1 = t; }
    uvec4 b = uvec4(0u);
    Put(b, 0u, 7u, 64u);   // mode 6
    Put(b, 7u, 7u, e0.x); Put(b, 14u, 7u, e1.x);
    Put(b, 21u, 7u, e0.y); Put(b, 28u, 7u, e1.y);
    Put(b, 35u, 7u, e0.z); Put(b, 42u, 7u, e1.z);
    Put(b, 49u, 7u, 127u); Put(b, 56u, 7u, 127u);
    Put(b, 63u, 1u, e0.w); Put(b, 64u, 1u, e1.w);
    Put(b, 65u, 3u, gCur[0]);
    for (uint i = 1u; i < 16u; i++) Put(b, 64u + 4u * i, 4u, gCur[i]);
    return b;
}

void main()
{
    ivec2 g = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(g, uRegion.zw))) return;
    ivec2 bp = uRegion.xy + g;
    ivec2 size = textureSize(uScrA, 0);
    vec3 db[16];
    uint mask = 0u;
    for (int i = 0; i < 16; i++)
    {
        ivec2 p = bp * 4 + ivec2(i & 3, i >> 2);
        vec3 a, d; vec4 c;
        gP[i] = vec3(0.0); db[i] = vec3(0.0); gCur[i] = 0u;
        if (!Finish(p, size, a, d, c)) continue;
        mask |= 1u << i;
        uvec2 rg = uvec2(packHalf2x16(min(a.rg, vec2(65504.0))), packHalf2x16(vec2(min(a.b, 65504.0), 0.0)));
        gP[i] = vec3(float(rg.x & 0xFFFFu), float(rg.x >> 16), float(rg.y & 0xFFFFu));
        db[i] = d * 255.0;
        imageStore(uPageC, p, c);
    }
    if (mask == 0u) { imageStore(uBlkA, bp, uvec4(0u)); imageStore(uBlkB, bp, uvec4(0u)); return; }
    imageStore(uBlkA, bp, Bc6h(mask));
    for (int i = 0; i < 16; i++) gP[i] = db[i];
    imageStore(uBlkB, bp, Bc7(mask));
}
