using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The cull dispatch's six frustum planes, at std140's vec4 stride.</summary>
[InlineArray(6)]
internal struct CullPlanes { private Vector4 _e; }

/// <summary>The <c>CxCull</c> block in the GLSL's own member order (<c>cx_cullblk.glsl</c>). C# sequential
/// layout IS std140 here: no member is a vec3 or an array of anything narrower than a vec4, so neither side
/// pads, and <see cref="Pin"/> holds the two together.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CullBlock
{
    public Matrix4x4 Vp, PyrVp;
    public CullPlanes Planes;
    public Vector4 FogOrigin, Eye;
    public Vector2 PyrSize;
    public float MaxLod, LodPx, LodScale, FogCull, ClipZ;
    public int DrawN, HiZ, Rej, CompactBelow;

    /// <summary>The GLSL against this struct. A member moved in either file stages every term after it into
    /// its neighbour -- a wrong frustum plane, with nothing in the log.</summary>
    internal static void Pin(Checker check)
    {
        var b = default(CullBlock);
        Std140.Pin(check, ShaderSource.Get("cx_cullblk.glsl"), "CxCull", UboSlots.Cull, Unsafe.SizeOf<CullBlock>(),
        [
            ("uVP", Std140.At(ref b, ref b.Vp)), ("uPyrVP", Std140.At(ref b, ref b.PyrVp)),
            ("uPlanes", Std140.At(ref b, ref b.Planes)), ("uFogOrigin", Std140.At(ref b, ref b.FogOrigin)),
            ("uCullEye", Std140.At(ref b, ref b.Eye)), ("uPyrSize", Std140.At(ref b, ref b.PyrSize)),
            ("uMaxLod", Std140.At(ref b, ref b.MaxLod)), ("uLodPx", Std140.At(ref b, ref b.LodPx)),
            ("uLodScale", Std140.At(ref b, ref b.LodScale)), ("uFogCull", Std140.At(ref b, ref b.FogCull)),
            ("uClipZ", Std140.At(ref b, ref b.ClipZ)), ("uDrawN", Std140.At(ref b, ref b.DrawN)),
            ("uHiZ", Std140.At(ref b, ref b.HiZ)), ("uRej", Std140.At(ref b, ref b.Rej)),
            ("uCompactBelow", Std140.At(ref b, ref b.CompactBelow)),
        ]);
    }
}

/// <summary>
/// Alloy's own visibility (ADR 0017): a compute dispatch tests each draw's world bounding sphere against this
/// pass's frustum and, for the colour pass, a hi-Z pyramid; survivors compact into an indirect command buffer
/// with a per-batch count, submitted so no count comes back to the CPU. Standing down means the caller's
/// existing <c>BaseVertex</c> submission -- a cull that cannot run must never mean an empty world.
/// </summary>
internal static unsafe class CullPass
{
    /// <summary>SSBO bindings (<see cref="SsboSlots"/>), private to the dispatch except
    /// <see cref="AlloyRenderer.BindDrawIdx"/>, which the compacted slot buffer takes at DRAW time.</summary>
    internal const uint BindSrc = SsboSlots.CullSrc, BindSphere = SsboSlots.CullSphere, BindCmd = SsboSlots.CullCmd,
                        BindOutSlot = SsboSlots.CullOut, BindCount = SsboSlots.CullCount, BindKey = SsboSlots.CullKey,
                        BindRej = SsboSlots.CullRej;

    /// <summary>Uints per source record: index count, first index, base vertex, record slot, batch, and the
    /// batch's output base. Six rather than five because the batch's start offset is what turns an
    /// <c>atomicAdd</c> into an output index without a second buffer to look it up in. STAGED BY THE CALLER,
    /// at the one site that already writes the same draw's count, offset, base and slot.</summary>
    internal const int SrcUints = 6;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>: count, instanceCount, firstIndex, baseVertex,
    /// baseInstance. Fixed by GL, and the stride the draw calls pass.</summary>
    internal const int CmdUints = 5;

    /// <summary>Do both compute stages stride the source list by <see cref="SrcUints"/>? The stride is a
    /// layout shared with GLSL, so the shader text is what says whether the two halves still agree -- a drift
    /// reads every draw out of its neighbour's record, silently.</summary>
    internal static bool SourceStrideSafe()
        => CullCs.Contains($"uint b = i * {SrcUints}u;")
        && SortCs.Contains($"uint b = i * {SrcUints}u;")
        && SortCs.Contains($"uSrc[j * {SrcUints}u + 4u] != batch")
        && SortCs.Contains("uCmd[c + 1u] = dropped ? 0u : 1u;");

    /// <summary>Compute workgroup size; the dispatch is 1D over draws.</summary>
    internal const int GroupSize = 64;

    /// <summary>The three dispatch phases, and the three output sets they fill. <see cref="PhaseRetest"/> is
    /// the prepass's second phase, with a set of its own because <c>MultiDrawElementsIndirectCount</c> takes a
    /// CPU byte offset for the start and phase one's count never comes back to say where its survivors end.</summary>
    internal const int PhasePrepass = 0, PhaseColour = 1, PhaseRetest = 2;
    /// <summary>The shader's <c>uRej</c> mode per phase: the colour phase ignores the verdict buffer, phase
    /// one WRITES it, phase two READS it and tests only the marked draws.</summary>
    internal const int RejOff = 0, RejWrite = 1, RejRead = 2;

    /// <summary>Run the prepass's second phase (<c>/alloy retest</c>). Off = phase one tests the frustum
    /// alone, which is the before half of the measurement.</summary>
    internal static bool Retest => Renderer.Retest.V;
    internal const bool DefRetest = true;

    /// <summary>Live dial behind <c>/alloy cull</c>, three-state rather than two because culling is the one
    /// system whose bug is invisible geometry. <b>0</b> stands the whole pass down -- every owned part drawn
    /// every frame, with no cull anywhere, slow and correct, which is what makes it the A/B for "is the cull
    /// eating this?"; <b>1</b> is the frustum test alone; <b>2</b> adds the hi-Z occlusion test.</summary>
    internal static int Mode => Renderer.Cull.V;
    internal const int ModeOff = 0, ModeHiZ = 2;

    /// <summary>LOD selection, in pixels: a draw whose bounding sphere projects to a screen radius smaller
    /// than this is dropped by the same dispatch that culls it. 0 disables it and <c>/alloy cull 0</c> stands
    /// it down with everything else, because it IS the cull shader. No tile exemption -- the hole it can
    /// leave is by construction under <see cref="LodPx"/> pixels wide.</summary>
    internal static float LodPx => Renderer.Lod.V;

    /// <summary>Drop geometry the fog has already painted over: a fragment past
    /// <see cref="SceneFog.OpaqueDistance"/> is EXACTLY the fully-fogged colour. Opaque is not enough, though
    /// -- what is behind it must be that colour too -- so the test is two conditions a frustum plane cannot
    /// express: past that distance AND entirely below the eye, where a downward ray never reaches the sky.</summary>
    internal static bool FogCull => SceneFog.Cull;

    /// <summary>The extent of the staged sphere CENTRES, and the eye they were culled against. A diagnostic
    /// with one job: the bounds come from <c>PartTriMesh</c>'s bbox, which is world-space for a world-space
    /// part and object-space for a matrix-class one, and that reading has never been verified for TILES. If
    /// it is wrong for them every tile's sphere collapses toward one point, and this line says so.</summary>
    internal static float SpreadX, SpreadY, SpreadZ, SpreadMaxR;
    /// <summary>The diagnostic above, measured by the staging walk that already touches every one of these
    /// spheres.</summary>
    internal static void SetSpread(float x, float y, float z, float maxR)
    { SpreadX = x; SpreadY = y; SpreadZ = z; SpreadMaxR = maxR; }

    /// <summary>The texture unit the hi-Z pyramid is bound on.</summary>
    internal const uint PyramidUnit = TextureSlots.HiZ;

    /// <summary>The six frustum planes of a row-major <c>view*proj</c>, Gribb-Hartmann, normalised so the
    /// plane distance is in world units and the sphere test is a plain compare. Row-major here means the
    /// combination is <c>clip = v * VP</c>, so the planes come off the COLUMNS. <paramref name="eye"/> is the
    /// eye the caller's vp was built from: a mirrored subframe measuring from the wrong camera is the bug.</summary>
    internal static void Planes(in Matrix4x4 m, Span<Vector4> p, Vector3 eye)
    {
        // clip.c = sum_r v[r] * m.row[r].c, so a plane is built from column 3 and column c of every row.
        Vector4 r0 = new(m.M11, m.M12, m.M13, m.M14), r1 = new(m.M21, m.M22, m.M23, m.M24),
                r2 = new(m.M31, m.M32, m.M33, m.M34), r3 = new(m.M41, m.M42, m.M43, m.M44);
        for (int i = 0; i < 6; i++)
        {
            int c = i >> 1;
            float s = (i & 1) == 0 ? 1f : -1f;
            float x = r0.W + s * Col(r0, c);
            float y = r1.W + s * Col(r1, c);
            float z = r2.W + s * Col(r2, c);
            float w = r3.W + s * Col(r3, c);
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 0f) { x /= len; y /= len; z /= len; w /= len; }
            p[i] = new Vector4(x, y, z, w);
        }
    }

    private static float Col(Vector4 row, int c) => c == 0 ? row.X : c == 1 ? row.Y : row.Z;

    /// <summary>Headless check of the two pieces that are arithmetic rather than GL: the plane extraction
    /// and the compacted list itself, against the CPU-staged one the <c>BaseVertex</c> path would submit.
    /// Both fail silently in the worst possible way -- a wrong plane culls the world, a wrong compaction
    /// draws a neighbour's geometry -- and neither needs a context.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        // A right-handed look-down-(-Z) camera in the row-vector convention the renderer uses.
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, -10), Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.0f, 1f, 100f);
        Matrix4x4 vp = view * proj;
        Vector4[] planes = new Vector4[6];
        Planes(in vp, planes, Vector3.Zero);
        bool Inside(Vector3 pt, float r)
        {
            for (int i = 0; i < 6; i++)
                if (planes[i].X * pt.X + planes[i].Y * pt.Y + planes[i].Z * pt.Z + planes[i].W < -r) return false;
            return true;
        }
        check.Expect(Inside(new Vector3(0, 0, -10), 1f), "a point straight ahead is inside the frustum");
        check.Expect(!Inside(new Vector3(0, 0, 10), 1f), "a point BEHIND the camera is outside it");
        check.Expect(!Inside(new Vector3(0, 0, -500), 1f), "a point past the far plane is outside it");
        check.Expect(!Inside(new Vector3(200, 0, -10), 1f), "a point far off to the side is outside it");
        check.Expect(Inside(new Vector3(200, 0, -10), 400f), "...and is back inside once its radius reaches the camera");

        // #341: and the fog does NOT touch these planes any more -- the test is the dispatch's, because it
        // needs the sphere's height. A fog that came back to the frustum would cull a skyline again.
        bool fogWas = FrameInputs.FogOn; float endWas = FrameInputs.FogEnd;
        FrameInputs.FogOn = true; FrameInputs.FogEnd = 50f;
        Planes(in vp, planes, Vector3.Zero);
        check.Expect(Inside(new Vector3(0, 0, -60), 1f), "fog does not slide a frustum plane: a point past the fog end survives it");
        FrameInputs.FogOn = fogWas; FrameInputs.FogEnd = endWas;
        Planes(in vp, planes, Vector3.Zero);
        check.Expect(!Inside(new Vector3(0, 0, -500), 1f), "fog off: the far plane is the engine's again");

        // The pack, against what the shader reads out of it. Not a GL call in sight: this is a memory layout
        // shared with GLSL, and every field landing one slot over is a draw reading its neighbour's range.
        CullSrcRec* recs = stackalloc CullSrcRec[2];
        uint* src = (uint*)recs;
        check.Expect(sizeof(CullSrcRec) == 6 * sizeof(uint) && sizeof(CullSrcRec) == SrcUints * sizeof(uint),
                     "the staged record is six words, the stride both compute stages read");
        Pack(ref recs[0], 30, 200, 7, 11, 0, 0);
        Pack(ref recs[1], 60, 400, 9, 13, 0, 0);
        check.Expect(src[0] == 30 && src[1] == 100 && src[2] == 7 && src[3] == 11 && src[4] == 0 && src[5] == 0,
          "draw 0 packs count/firstIndex/baseVertex/slot/batch/start in that order");
        check.Expect(src[SrcUints + 1] == 200, "...and the byte offset becomes an ELEMENT index (two-byte indices)");
        check.Expect(SourceStrideSafe(), "the cull and sort stages stride the source list by SrcUints");

        check.Ok &= Parity(log, vp, planes);
        log.Info(check.Ok ? "CullPass.SelfCheck: passed" : "CullPass.SelfCheck: FAILED -- see above");
    }

    /// <summary>One draw's source record, written where the batch walk already writes the same draw's
    /// count/offset/base. The one place the layout <see cref="CullCs"/> reads back is spelled out.</summary>
    internal static void Pack(ref CullSrcRec r, int idxCount, nint byteOff, int baseVertex, int slot,
                             int batch, int batchStart)
    {
        r.IndexCount = (uint)idxCount;
        // The indirect command's firstIndex is in ELEMENTS; the staged offset is pre-multiplied to the byte
        // offset MultiDrawElementsBaseVertex takes. Two-byte indices, so the halving is exact.
        r.FirstIndex = (uint)((long)byteOff / 2);
        r.BaseVertex = (uint)baseVertex;
        r.Slot = (uint)slot;
        r.Batch = (uint)batch;
        r.OutBase = (uint)batchStart;
    }

    /// <summary>Does the compute-produced draw list agree with the CPU-staged one? A line-by-line port of
    /// <see cref="CullCs"/>'s <c>main</c>, run one invocation at a time -- which is what a sequential
    /// <c>atomicAdd</c> is. Two standards: a COMPACTING batch is asserted as a SET (the atomic's order is not
    /// deterministic on hardware), an ORDERED one as a SEQUENCE, which is the whole point of one.</summary>
    private static bool Parity(ExtenderLog log, Matrix4x4 vp, Vector4[] planes)
    {
        var check = new Checker(log, "CullPass.ParitySelfCheck");

        // Three batches, all inside the frustum the caller built: straight ahead at 10 units. Batch 2 is
        // the SORTED one -- #290's half, where the back-to-front order is produced rather than inherited.
        const int drawN = 10, groups = 3, compactBelow = 2;
        int* gStart = stackalloc int[groups] { 0, 3, 6 };
        int* gLen = stackalloc int[groups] { 3, 3, 4 };
        CullSrcRec* src = stackalloc CullSrcRec[drawN];
        SphereRec* sph = stackalloc SphereRec[drawN];
        float* key = stackalloc float[drawN];
        var eye = Vector3.Zero;
        int n = 0;
        for (int g = 0; g < groups; g++)
            for (int d = 0; d < gLen[g]; d++, n++)
            {
                Pack(ref src[n], 30 + n, 2 * n, n, n, g, gStart[g]);
                sph[n] = new SphereRec { Centre = new Vector3(0f, 0f, -10f), Radius = 1f };
            }
        // ...and the three populations a cull has to get right: outside the frustum, exempt, sub-pixel.
        sph[1].Centre.X = 500f;                                   // draw 1: far off to the side
        sph[4].Radius = AlloyRenderer.NeverCull;                // draw 4: unknown bounds, never culled
        sph[5].Radius = 0.001f;                                 // draw 5: a speck, for the LOD test
        // The sorted batch: near, far, equally far (the tie), and one the frustum drops.
        sph[6].Centre.Z = -10f;
        sph[7].Centre.Z = -30f;
        sph[8].Centre.Z = -30f;
        sph[9].Centre.X = 500f;

        DrawCmdRec* cmd = stackalloc DrawCmdRec[drawN];
        int* outSlot = stackalloc int[drawN];
        uint* cnt = stackalloc uint[groups];

        // Everything visible: the compaction must reproduce the staged list exactly, batch for batch.
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd, outSlot, cnt);
        check.Expect(cnt[0] == 2 && cnt[1] == 3, "only the out-of-frustum draw is dropped when nothing else can be");
        check.Expect(Agrees(src, drawN, groups, gStart, cmd, outSlot, cnt, log),
          "the compacted list agrees with the CPU-staged one draw for draw");

        // #290 (AC1 + AC5), the sorted half. Not "the same set": the submitted order has to BE the
        // back-to-front one, produced from keys the dispatch measured, with the culled draw compacted out.
        check.Expect(cnt[2] == 3, "the sorted batch's count is its survivors, not its staged length");
        check.Expect(outSlot[6] == 7 && outSlot[7] == 8 && outSlot[8] == 6,
          "the sorted batch submits farthest first, and an exact tie keeps the lower staged index");
        check.Expect(Sorted(sph, outSlot, gStart[2], (int)cnt[2], eye, log),
          "...which is to say every neighbour in it is no nearer than the one before");
        check.Expect(Agrees(src, drawN, groups, gStart, cmd, outSlot, cnt, log),
          "...and each of its commands is its own staged draw's range");
        // CX-329: the region is DENSE past the survivors -- the dropped draw takes the slot behind them as a
        // zero-instance command, which is what lets a run of batches submit as one indirect call. Hand
        // counted: batch 2 stages draws 6..9, draw 9 is the one off the frustum, so slot 9 is its padding.
        check.Expect(cmd[9].InstanceCount == 0u && cmd[9].Count == src[9].IndexCount
                     && cmd[9].FirstIndex == src[9].FirstIndex && cmd[9].BaseVertex == src[9].BaseVertex,
          "a dropped sorted draw pads its batch's region with a zero-instance command, not a hole");
        check.Expect(cmd[6].InstanceCount == 1u && cmd[7].InstanceCount == 1u && cmd[8].InstanceCount == 1u,
          "...behind the survivors, which keep the front of the region and draw one instance each");
        // A rank is order-independent by construction; this is what pins that it really is.
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd, outSlot, cnt,
                scramble: true);
        check.Expect(cnt[2] == 3 && outSlot[6] == 7 && outSlot[7] == 8 && outSlot[8] == 6,
          "the sort is the same when the atomic hands its draws out backwards");

        // LOD: a speck goes, an exempt record stays, and the ordinary draws are untouched. uLodScale is
        // proj.M22 * viewportH / 2 -- 1.0 * 1080 / 2 for the caller's 90-degree projection.
        Emulate(src, sph, drawN, groups, planes, vp, 1f, 540f, compactBelow, eye, key, cmd, outSlot, cnt);
        check.Expect(cnt[1] == 2 && outSlot[gStart[1]] == 3 && outSlot[gStart[1] + 1] == 4,
          "a sub-pixel draw is LOD-culled and the ones that are not are the ones that survive");
        check.Expect(Agrees(src, drawN, groups, gStart, cmd, outSlot, cnt, log),
          "...and what survives it is still the staged record, field for field");
        Emulate(src, sph, drawN, groups, planes, vp, 1f, 0f, compactBelow, eye, key, cmd, outSlot, cnt);
        check.Expect(cnt[1] == 3, "a pass with no perspective scale runs no LOD test rather than culling everything");
        Emulate(src, sph, drawN, groups, planes, vp, 1e6f, 540f, compactBelow, eye, key, cmd, outSlot, cnt);
        check.Expect(cnt[1] == 1 && outSlot[gStart[1]] == 4,
          "an absurd LOD threshold still cannot touch a NeverCull record");
        check.Expect(cnt[2] == 3, "...nor anything in a SORTED batch, which runs no LOD test at all");

        // #489: the two prepass phases. A stale pyramid says draws 0, 2 and 6 are hidden -- 2 wrongly, and
        // 6 is in the SORTED batch, which phase one must not test. Phase one draws the rest and marks
        // exactly the hi-Z rejects; the fresh pyramid then admits 2, and phase two draws it and nothing else.
        bool* occ = stackalloc bool[drawN];
        uint* rej = stackalloc uint[drawN];
        DrawCmdRec* cmd2 = stackalloc DrawCmdRec[drawN];
        int* out2 = stackalloc int[drawN];
        uint* cnt2 = stackalloc uint[groups];
        occ[0] = true; occ[2] = true; occ[6] = true;
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd, outSlot, cnt,
                rejMode: RejWrite, rej: rej, occ: occ);
        check.Expect(cnt[0] == 0 && cnt[1] == 3, "phase one draws what the stale pyramid and the frustum admit");
        check.Expect(rej[0] == 1 && rej[2] == 1 && rej[1] == 0 && rej[3] == 0 && rej[4] == 0 && rej[5] == 0,
          "phase one marks the stale hi-Z rejects and NOT the frustum's");
        check.Expect(rej[6] == 0 && cnt[2] == 3, "a sorted draw is never hi-Z tested in phase one, so it is never marked");
        occ[2] = false;
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd2, out2, cnt2,
                rejMode: RejRead, rej: rej, occ: occ);
        check.Expect(cnt2[0] == 1 && out2[0] == 2, "phase two draws exactly the draw the stale pyramid wrongly rejected");
        check.Expect(cnt2[1] == 0 && cnt2[2] == 0, "...and nothing phase one already drew, nor any sorted draw");
        check.Expect(Agrees(src, drawN, groups, gStart, cmd2, out2, cnt2, log), "...as its own staged record");
        // The property the ticket promises: phase one plus phase two IS the fresh-pyramid list.
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd, outSlot, cnt, occ: occ);
        check.Expect(cnt[0] == 1 && outSlot[0] == 2 && cnt[1] == 3,
          "the union of the two phases is what a same-frame test against the fresh pyramid draws");
        // A stale rejection the fresh pyramid confirms stays out, but a frustum reject is never re-tested.
        occ[0] = true; rej[1] = 1;   // a forged mark on the off-frustum draw
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd2, out2, cnt2,
                rejMode: RejRead, rej: rej, occ: occ);
        check.Expect(cnt2[0] == 1 && out2[0] == 2, "phase two still applies the frustum, and a confirmed occlusion stays culled");
        return check.Ok;
    }

    /// <summary>The port. <c>i</c> is <c>gl_GlobalInvocationID.x</c> and the counter is the atomic; every
    /// other line is <see cref="CullCs"/>'s and <see cref="SortCs"/>'s, in order -- two loops because there
    /// are two dispatches, and the barrier between them is what the loop boundary stands for.
    /// <paramref name="scramble"/> runs the invocations backwards, which the sorted range must survive.</summary>
    private static void Emulate(CullSrcRec* src, SphereRec* sph, int drawN, int groups, Vector4[] planes, Matrix4x4 vp,
                                float lodPx, float lodScale, int compactBelow, Vector3 eye, float* key,
                                DrawCmdRec* cmd, int* outSlot, uint* cnt, bool scramble = false,
                                int rejMode = 0, uint* rej = null, bool* occ = null)
    {
        for (int g = 0; g < groups; g++) cnt[g] = 0;
        for (int q = 0; q < drawN; q++)
        {
            int i = scramble ? drawN - 1 - q : q;
            ref CullSrcRec b = ref src[i];
            bool sorted = b.Batch >= (uint)compactBelow;
            if (rejMode == RejRead && (sorted || rej[i] == 0u)) continue;
            int slot = (int)b.Slot;
            var c = sph[slot].Centre;
            float r = sph[slot].Radius;
            bool keep = true;
            bool hiz = false;
            if (r < 1e29f)
            {
                for (int p = 0; p < 6 && keep; p++)
                    keep = !(planes[p].X * c.X + planes[p].Y * c.Y + planes[p].Z * c.Z + planes[p].W < -r);
                if (keep && !sorted && lodPx > 0f && lodScale > 0f)
                {
                    float w = Vector4.Transform(new Vector4(c, 1f), vp).W;
                    if (w > 0f && r * lodScale < lodPx * w) keep = false;
                }
                // occ stands in for CxOccluded: the pyramid's verdict per slot, which has no CPU model.
                if (keep && occ != null && !(sorted && rejMode == RejWrite) && occ[slot]) { keep = false; hiz = true; }
            }
            if (rejMode == RejWrite) rej[i] = hiz ? 1u : 0u;
            if (sorted)
            {
                Vector3 d = c - eye;
                key[i] = keep ? Vector3.Dot(d, d) : -1f;
                if (keep) cnt[b.Batch]++;
                continue;
            }
            if (!keep) continue;
            uint o = b.OutBase + cnt[b.Batch]++;
            cmd[o] = new DrawCmdRec { Count = b.IndexCount, InstanceCount = 1u, FirstIndex = b.FirstIndex, BaseVertex = b.BaseVertex };
            outSlot[o] = slot;
        }
        // ...and the second dispatch: the rank sort, which also pads.
        for (int q = 0; q < drawN; q++)
        {
            int i = scramble ? drawN - 1 - q : q;
            ref CullSrcRec b = ref src[i];
            uint batch = b.Batch;
            if (batch < (uint)compactBelow) continue;
            float k = key[i];
            uint bas = b.OutBase;
            bool dropped = k < 0f;
            uint rank = 0, live = 0, deadBefore = 0;
            for (uint j = bas; j < (uint)drawN; j++)
            {
                if (src[j].Batch != batch) break;
                float kj = key[j];
                if (kj < 0f) { if (j < (uint)i) deadBefore++; continue; }
                live++;
                if (j == (uint)i) continue;
                if (kj > k || (kj == k && j < (uint)i)) rank++;
            }
            uint o = dropped ? bas + live + deadBefore : bas + rank;
            cmd[o] = new DrawCmdRec { Count = b.IndexCount, InstanceCount = dropped ? 0u : 1u,
                                      FirstIndex = b.FirstIndex, BaseVertex = b.BaseVertex };
            outSlot[o] = (int)b.Slot;
        }
    }

    /// <summary>The submitted run really is back-to-front. Read off the SPHERES rather than the
    /// keys, so a key the emulator computed wrongly cannot agree with itself.</summary>
    private static bool Sorted(SphereRec* sph, int* outSlot, int start, int count, Vector3 eye, ExtenderLog log)
    {
        float prev = float.MaxValue;
        for (int o = start; o < start + count; o++)
        {
            Vector3 d = sph[outSlot[o]].Centre - eye;
            float k = Vector3.Dot(d, d);
            if (k > prev)
            { log.Info($"CullPass.ParitySelfCheck FAIL: output {o} is farther than the one before it"); return false; }
            prev = k;
        }
        return true;
    }

    /// <summary>Every survivor of <paramref name="cmd"/> is a staged draw of its own batch, once, with its
    /// own record slot -- and it landed inside that batch's output region. The two ways a compaction can be
    /// wrong that the draw call cannot survive: a draw written into someone else's region, and a command
    /// paired with the wrong record slot.</summary>
    private static bool Agrees(CullSrcRec* src, int drawN, int groups, int* gStart, DrawCmdRec* cmd,
                              int* outSlot, uint* cnt, ExtenderLog log)
    {
        bool ok = true;
        for (int g = 0; g < groups; g++)
            for (int o = gStart[g]; o < gStart[g] + (int)cnt[g]; o++)
            {
                int found = -1;
                for (int d = 0; d < drawN; d++)
                    if ((int)src[d].Slot == outSlot[o] && (int)src[d].Batch == g)
                        found = d;
                if (found < 0)
                { log.Info($"CullPass.ParitySelfCheck FAIL: output {o} names a slot batch {g} never staged"); ok = false; continue; }
                ref CullSrcRec b = ref src[found];
                ref DrawCmdRec k = ref cmd[o];
                if (k.Count != b.IndexCount || k.InstanceCount != 1u || k.FirstIndex != b.FirstIndex || k.BaseVertex != b.BaseVertex || k.BaseInstance != 0u)
                { log.Info($"CullPass.ParitySelfCheck FAIL: output {o}'s command is not its staged draw's range"); ok = false; }
            }
        return ok;
    }

    /// <summary>The cull itself. One thread per draw; a survivor appends into its batch's own region with an
    /// atomicAdd, so the compacted list keeps every batch's boundaries and only its interior order moves. A
    /// radius of infinity is "do not cull me" -- skinned, dangly, a displacing feature, a degenerate bbox.
    /// Unknown bounds mean visible, always: the failure mode of this pass is invisible geometry.</summary>
    internal static string CullCs => ShaderSource.Get("cx_cull_c.glsl");

    /// <summary>The back-to-front sort, as a RANK rather than a shuffle: a survivor counts how many
    /// survivors of its batch are FARTHER, that count is its offset from the batch's output base, and the
    /// tie-break is the STAGED index. A dropped draw pads behind them, zero-instance, zero-instance.
    /// ponytail: O(n^2) compares per batch across n threads; a bitonic sort drops in if it ever shows.</summary>
    internal static string SortCs => ShaderSource.Get("cx_cullsort_c.glsl");
}
