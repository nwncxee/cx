// The cull's own half: each kept drop's five records appended to the buffer the vertex stage reads by instance, and
// counted into the indirect draw's command. CX_WX_WANTED and CX_WX_REC_CAP are declared by cx_wx_cull_cs.

/// The vec4 records one drop takes (CxWxPlace).
const uint CX_WX_RECORD = 5u;

void main()
{
    vec4 r0, r1, r2, r3, r4;
    bool keep = CxWxPlace(gl_GlobalInvocationID.x, r0, r1, r2, r3, r4);
#ifdef CX_WX_WAVE
    // compaction: one atomic per subgroup, each survivor at its rank among the subgroup's
    uvec4 ballot = subgroupBallot(keep);
    uint n = subgroupBallotBitCount(ballot), first = 0u;
    if (subgroupElect() && n > 0u)
    {
        atomicAdd(uCmd[CX_WX_WANTED + uSlot], n);
        first = atomicAdd(uCmd[uSlot * 4 + 1], n);
        if (first + n > CX_WX_REC_CAP) atomicMin(uCmd[uSlot * 4 + 1], CX_WX_REC_CAP);
    }
    uint rank = subgroupBroadcastFirst(first) + subgroupBallotExclusiveBitCount(ballot);
    if (!keep || rank >= CX_WX_REC_CAP) return;
#else
    // a device without subgroup ballot in compute: one atomic per survivor
    if (!keep) return;
    atomicAdd(uCmd[CX_WX_WANTED + uSlot], 1u);
    uint rank = atomicAdd(uCmd[uSlot * 4 + 1], 1u);
    if (rank >= CX_WX_REC_CAP) { atomicMin(uCmd[uSlot * 4 + 1], CX_WX_REC_CAP); return; }
#endif
    uint at = (uint(uSlot) * CX_WX_REC_CAP + rank) * CX_WX_RECORD;
    uDropsOut[at] = r0;
    uDropsOut[at + 1u] = r1;
    uDropsOut[at + 2u] = r2;
    uDropsOut[at + 3u] = r3;
    uDropsOut[at + 4u] = r4;
}
