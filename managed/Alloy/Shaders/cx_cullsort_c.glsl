#version 460 core
layout(local_size_x = 64) in;   // GroupSize
layout(std430, binding = 8) readonly buffer CxCullSrc     { uint uSrc[]; };
layout(std430, binding = 10) writeonly buffer CxCullCmd    { uint uCmd[]; };
layout(std430, binding = 11) writeonly buffer CxCullOutIdx { int  uOut[]; };
layout(std430, binding = 13) readonly buffer CxCullKey     { float uKey[]; };
uniform int uDrawN;
uniform int uCompactBelow;

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uDrawN)) return;
    uint b = i * 6u;
    uint batch = uSrc[b + 4u];
    if (batch < uint(uCompactBelow)) return;
    float k = uKey[i];
    uint base = uSrc[b + 5u];
    bool dropped = k < 0.0;
    // One walk of the batch answers both placements: how many survivors stand FARTHER than this draw
    // (its rank among them), how many survivors the batch has at all, and how many dropped draws sit at a
    // lower staged index than this one.
    uint rank = 0u, live = 0u, deadBefore = 0u;
    for (uint j = base; j < uint(uDrawN); j++)
    {
        if (uSrc[j * 6u + 4u] != batch) break;   // batches are contiguous in the source list
        float kj = uKey[j];
        if (kj < 0.0) { if (j < i) deadBefore++; continue; }
        live++;
        if (j == i) continue;
        // Farther first. An exact tie falls to the lower staged index, which is the CPU sort's own
        // stable order -- so the two agree draw for draw and /alloy cull 0 is a like-for-like A/B.
        if (kj > k || (kj == k && j < i)) rank++;
    }
    // CX-329: the survivors keep the front of the batch's region, back to front, exactly where the
    // compaction has always put them; a dropped draw takes a slot behind them as a ZERO-INSTANCE command.
    // That makes the region DENSE -- one command per staged draw -- which is what lets a run of batches
    // sharing GL state submit as one glMultiDrawElementsIndirect over the union.
    uint o = dropped ? base + live + deadBefore : base + rank;
    uint c = o * 5u;
    uCmd[c] = uSrc[b];
    uCmd[c + 1u] = dropped ? 0u : 1u;
    uCmd[c + 2u] = uSrc[b + 1u];
    uCmd[c + 3u] = uSrc[b + 2u];
    uCmd[c + 4u] = 0u;
    uOut[o] = int(uSrc[b + 3u]);
}
