using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Shadow caster selection in compute, in <see cref="CullPass"/>'s shape (ADR 0018): one staging pass per
/// FRAME, then per view a handful of uniforms, one dispatch that tests each caster and compacts the survivors
/// into an indirect command buffer, and one <c>glMultiDrawElementsIndirectCount</c> per batch -- no count ever
/// comes back to the CPU. The bounds are <see cref="CullPass"/>'s own spheres, so both culls test one set.
/// </summary>
internal static unsafe class CasterGather
{
    /// <summary>SSBO bindings private to this dispatch (<see cref="SsboSlots"/>), beside
    /// <see cref="CullPass.BindSphere"/>, which it also reads because the bounds are the colour cull's own.</summary>
    internal const uint BindSrc = SsboSlots.GatherSrc, BindCmd = SsboSlots.GatherCmd, BindCount = SsboSlots.GatherCount,
                        BindCast = SsboSlots.CastSphere;

    // CX-50: ...and the two COARSE runs of the same mesh, out of which every view picks one by its own texel
    // size. ONE record per caster whatever levels it has: the staging, its batch regions, the command buffer
    // and the thread count are all per caster, and a record per level would multiply every one of them --
    // four more words per record is the cheaper half of that trade. A level a mesh does not have is a zero
    // count, and the dispatch then bakes the finer run it does have, so a caster never bakes nothing.
    /// <summary>Uints per source record, and their order -- index count, first index, base vertex, OWNERSHIP
    /// slot (the sphere index, and the <c>baseInstance</c> the depth stages read <c>uDraw</c> through), batch,
    /// batch output base, the DYNAMIC flag (1 = a mover this frame, which the set filter tests), then count
    /// and first index at detail level 1 and at level 2. Otherwise <see cref="CullPass.SrcUints"/>'s layout.</summary>
    internal const int SrcUints = 11;
    /// <summary>The source word that carries the set flag -- the one word the standing staging rewrites
    /// per frame, so its index is a contract shared with <c>GatherCs</c>.</summary>
    internal const int SetFlagWord = 6;
    /// <summary>The coarse runs' words, a contract shared with both dispatches: count then first index,
    /// level 1 then level 2.</summary>
    internal const int Cnt1Word = 7, First1Word = 8, Cnt2Word = 9, First2Word = 10;

    /// <summary>Which half of the population a dispatch admits. <see cref="SetAll"/> = no filter (the
    /// sun, and the combined bake <c>/shadow split 0</c> restores); <see cref="SetStatic"/> = still casters
    /// only; <see cref="SetDynamic"/> = movers only. Applied BEFORE the cull and independent of
    /// <c>/shadow gather 0</c>, because the filter is what the two arrays' contents MEAN, not a cull.</summary>
    internal const int SetAll = 0, SetStatic = 1, SetDynamic = 2;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>. Fixed by GL.</summary>
    internal const int CmdUints = 5, CmdBytes = CmdUints * 4;
    /// <summary>Count-buffer words past the per-batch counts: the receiver-cull tally, then triangles baked
    /// at each detail level. A shared contract with both dispatches, which index them off <c>uGroups</c>.</summary>
    internal const int CountExtra = 1 + MeshLod.Levels;
    /// <summary>Where one level's triangle tally sits in the count buffer -- past the per-batch counts and
    /// the receiver word. The one expression: both dispatches spell it and <see cref="Sample"/> reads it.</summary>
    internal static int TriWord(int groups, int level) => groups + 1 + level;
    private const int GroupSize = 64;
    /// <summary>Command/count buffer pairs rotated per VIEW, against a write-after-read: view K's indirect
    /// draws source their commands from one pair while view K+Rotation's dispatch rewrites it. Reusing a pair
    /// R views later leaves R-1 symmetric barriers standing between the last read and the rewrite, so 2 is the
    /// proven floor (pinned by <c>CasterPass.SelfCheck</c>) and 3 is that floor with one spare.</summary>
    internal const int Rotation = 3;

    /// <summary>Live dial behind <c>/shadow gather</c>: 1 = the dispatch culls, 0 = it admits every staged
    /// caster into every view, the same A/B pair <c>/alloy cull</c> spells for the colour path. The third
    /// bisect step is <c>shadow.mode=0</c>, which hands the whole shadow question back to the engine's
    /// stencil.</summary>
    internal static int Mode => Shadows.Gather.V ? 1 : 0;
    /// <summary>Live dial behind <c>/shadow receiver</c>: 1 = a view flagged as re-baked under this
    /// frame's camera also rejects casters whose shadow volume cannot reach the camera frustum; 0 = the
    /// light-cone tests alone. Inside <see cref="Mode"/>'s gate, so <c>/shadow gather 0</c> still disables
    /// everything. Its own dial because the bake A/B has to isolate this term from the cone cull.</summary>
    internal static int Receiver => Shadows.Receiver.V ? 1 : 0;
    /// <summary>This frame's receiver frustum, six planes as <c>ShadowMath.ReceiverPlanes</c> writes
    /// them (already pushed out by the camera epoch's slack). Filled by <c>CasterPass</c> once per frame.</summary>
    internal static readonly float[] Planes = new float[24];

    private static ExtenderLog? _log;
    /// <summary>Everything the GL context owns, in one object: <see cref="ContextLost"/> replaces it.</summary>
    private sealed class GatherGlState
    {
        public uint Prog;
        public bool Failed;
        public int UDrawN, UPoint, UFocus, UDir, UReach, UAlong, UCull, USet, URx, UGroups, UPlanes;
        /// <summary>The view's two detail thresholds: a caster whose bound clears the first bakes its base
        /// run, the second its middle one, below both its coarsest.</summary>
        public int ULodR1, ULodR2;
        public uint SrcBuf;
        public int SrcCap;
        /// <summary>The DYNAMIC source: just the staged records whose set flag is 1, the same layout and
        /// their OWN batch id and output base, so a dynamic view's dispatch covers a few hundred draws.</summary>
        public uint DynSrcBuf;
        public int DynSrcCap, DynDrawN;
        /// <summary>A caller reported changed bytes that have not reached the buffer yet (a refused Begin keeps it).</summary>
        public bool SrcDirty;
        public readonly uint[] CmdBuf = new uint[Rotation], CntBuf = new uint[Rotation];
        public int CmdCap, CntCap, At;
        public int DrawN, Groups;
        /// <summary>The latent count sample's ring: the LAST view's per-batch counts, read back two frames
        /// later -- <see cref="CullPass"/>'s instrument shape, with no readback on the draw path.</summary>
        public GpuReadback SampleRead;
        public readonly int[] ReadN = new int[3], ReadDrawN = new int[3], ReadViews = new int[3], ReadRxViews = new int[3];
        public bool Dispatched;
        /// <summary>The count buffer the last dispatch (per-view or layered) wrote, and how many views it
        /// covered -- what <see cref="Sample"/> copies aside.</summary>
        public uint SampleBuf;
        public int SampleViews, SampleRxViews;
        /// <summary>Draws the sampled dispatch covered (the DYNAMIC source's count when it ran over that), and
        /// whether it did, so kept/staged compares like with like.</summary>
        public int SampleDrawN;
        public bool SampleDyn;
        /// <summary>Did the dispatch this slot sampled run on the dynamic source? Decides which pair it folds into.</summary>
        public readonly bool[] ReadDyn = new bool[3];
        public uint LayerProg;
        public bool LayerFailed;
        public int LuDrawN, LuViewN, LuCull, LuRx, LuGroups, LuPlanes;
        public uint ViewBuf;
        public int ViewCap;
        public uint CastBuf;
        public int CastCap;
        public readonly uint[] LyCmd = new uint[Rotation], LyCnt = new uint[Rotation];
        public long LyCmdCap;
        public int LyCntCap, LyAt;
    }

    private static GatherGlState _gl = new();
    /// <summary>Dispatches that ran over the dynamic source rather than the full staging -- against
    /// <see cref="Views"/> plus <see cref="LayerDispatches"/>, the period line's witness for S8.</summary>
    internal static long DynDispatches;
    /// <summary>Capacity tracker for a sample slot: the per-batch counts plus the receiver tally.</summary>
    private static uint[] _counts = new uint[1024];
    private static int _readSeq;

    /// <summary>Views dispatched, batches submitted, and the latent sample's tally (survivors and staged
    /// draws of one view per frame, three frames late) -- the gather's half of the caster pass's period
    /// line.</summary>
    internal static long Views, Batches, SampledKept, SampledStaged, Samples;
    /// <summary>S8: the same three over the samples whose dispatch ran on the DYNAMIC source. Kept apart
    /// because the two populations are two orders of magnitude apart -- averaging ~438 staged draws with ~37k
    /// gives a figure that describes neither.</summary>
    internal static long SampledKeptDyn, SampledStagedDyn, SamplesDyn, SampledRxDyn;
    /// <summary>Of the sampled view's staged draws, how many the receiver-frustum test rejected
    /// (the word after the per-batch counts, <c>uCnt[uGroups]</c>).</summary>
    internal static long SampledRx;
    /// <summary>Triangles the sampled views baked at each detail level, per view, over the period: the
    /// bake's answer to <c>MeshLod.Drew</c>. Levels 1 and 2 reading 0 in a dense outdoor area means every
    /// cascade is still baking full detail.</summary>
    internal static readonly long[] SampledTris = new long[MeshLod.Levels];
    /// <summary>Frames whose staged source matched the persistent buffer byte for byte, so nothing was
    /// uploaded -- the number that says the per-frame upload is really gone.</summary>
    internal static long UploadsSkipped, Uploads;

    /// <summary>The program failed to build and will never arm; <see cref="CasterPass"/> disarms on it
    /// rather than idling forever.</summary>
    internal static bool Failed => _gl.Failed;

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The GL names went with the context; nothing is deleted (that would be a call into a dead
    /// context) and everything rebuilds on the next frame. See docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _gl = new GatherGlState();
    }

    /// <summary>Everything the dispatch needs from GL, checked once and latched: a missing entry point is a
    /// permanent property of the driver and the gather then simply never arms.</summary>
    private static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null;

    /// <summary>Build the dispatch program. Not an overridable stage (ADR 0012's table is what a content
    /// author may restyle): an override here could only delete shadows, and there is no look to restyle.</summary>
    internal static bool Ensure()
    {
        if (_gl.Prog != 0) return true;
        if (_gl.Failed || !Capable()) { _gl.Failed = true; return false; }
        _gl.Prog = GlProgram.Compute(GatherCs, _log, "CasterGather caster cull");
        if (_gl.Prog == 0) { _gl.Failed = true; return false; }
        _gl.UDrawN = GlProgram.Loc(_gl.Prog, "uDrawN");
        _gl.UPoint = GlProgram.Loc(_gl.Prog, "uPoint");
        _gl.UFocus = GlProgram.Loc(_gl.Prog, "uFocus");
        _gl.UDir = GlProgram.Loc(_gl.Prog, "uDir");
        _gl.UReach = GlProgram.Loc(_gl.Prog, "uReach");
        _gl.UAlong = GlProgram.Loc(_gl.Prog, "uAlong");
        _gl.UCull = GlProgram.Loc(_gl.Prog, "uCull");
        _gl.USet = GlProgram.Loc(_gl.Prog, "uSet");
        _gl.URx = GlProgram.Loc(_gl.Prog, "uRx");
        _gl.UGroups = GlProgram.Loc(_gl.Prog, "uGroups");
        _gl.UPlanes = GlProgram.Loc(_gl.Prog, "uPlanes");
        _gl.ULodR1 = GlProgram.Loc(_gl.Prog, "uLodR1");
        _gl.ULodR2 = GlProgram.Loc(_gl.Prog, "uLodR2");
        _log?.Info("CasterGather: armed -- shadow caster selection in compute over the shared CxCullSphere " +
                   "bounds, glMultiDrawElementsIndirectCount submission (/shadow gather 0 disables the cull)");
        return true;
    }

    /// <summary>Is the gather driving this frame's views with a non-empty staging? A frame that staged ZERO
    /// draws is not inactive -- its views still clear -- so <see cref="CasterPass"/> gates on its own staged
    /// flag, not on this.</summary>
    internal static bool Active => _gl.Prog != 0 && _gl.DrawN > 0;

    /// <summary>Forget the frame's staging. Called before every staging attempt, so a frame that stages
    /// nothing (a collect came back empty, an upload was refused) bakes empty views instead of baking the
    /// PREVIOUS frame's staging into this frame's views.</summary>
    internal static void Stand() { _gl.DrawN = 0; _gl.DynDrawN = 0; }

    /// <summary>S8: publish this frame's dynamic source. Uploaded whole every frame it is used -- it is a few
    /// hundred records, and its membership moves every frame by construction. False = there is none, and a
    /// dynamic view falls back to the full source, which is the same set by a longer road.</summary>
    internal static bool DynamicSource(int drawN, uint* src)
    {
        _gl.DynDrawN = 0;
        if (_gl.Prog == 0 || drawN <= 0) return false;
        if (_gl.DynSrcBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.DynSrcBuf = b; _gl.DynSrcCap = 0; }
        long bytes = (long)drawN * SrcUints * sizeof(uint);
        if (drawN > _gl.DynSrcCap)
        { Gl.NamedBufferData(_gl.DynSrcBuf, (IntPtr)bytes, null, Gl.DYNAMIC_DRAW); _gl.DynSrcCap = drawN; }
        Gl.NamedBufferSubData(_gl.DynSrcBuf, IntPtr.Zero, (IntPtr)bytes, src);
        _gl.DynDrawN = drawN;
        return true;
    }

    /// <summary>No dynamic source this frame: every view dispatches over the full staging, as before S8.</summary>
    internal static void StandDynamic() => _gl.DynDrawN = 0;

    /// <summary>Is the dynamic source standing for this frame?</summary>
    internal static bool DynamicReady => _gl.DynDrawN > 0 && _gl.DynSrcBuf != 0;

    /// <summary>Publish one frame's staging. The source buffer is persistent, and a frame whose caller
    /// reports no <paramref name="changed"/> bytes sends nothing at all (the caller knows; no hash). The
    /// bounds live in <see cref="CullPass"/>'s sphere buffer, which the colour build maintains, so nothing
    /// per-draw is uploaded here on a settled frame.</summary>
    internal static bool Begin(int drawN, int groups, uint* src, bool changed)
    {
        _gl.DrawN = 0;
        _gl.SrcDirty |= changed;
        if (drawN <= 0 || groups <= 0 || !Ensure()) return false;
        if (CullPass.SphereBuf == 0 || _gl.CastBuf == 0) return false;   // no colour build yet: no bounds to cull against

        long srcWords = (long)drawN * SrcUints;
        bool grow = drawN > _gl.SrcCap;
        if (_gl.SrcBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.SrcBuf = b; grow = true; }
        if (grow || _gl.SrcDirty)
        {
            if (grow)
            { Gl.NamedBufferData(_gl.SrcBuf, (IntPtr)((long)drawN * SrcUints * sizeof(uint)), null, Gl.DYNAMIC_DRAW); _gl.SrcCap = drawN; }
            Gl.NamedBufferSubData(_gl.SrcBuf, IntPtr.Zero, (IntPtr)(srcWords * sizeof(uint)), src);
            _gl.SrcDirty = false;
            Uploads++;
        }
        else UploadsSkipped++;

        for (int i = 0; i < Rotation; i++)
        {
            Gl.EnsureStorage(ref _gl.CmdBuf[i], (long)drawN * CmdBytes, drawN > _gl.CmdCap);
            Gl.EnsureStorage(ref _gl.CntBuf[i], (long)(groups + CountExtra) * sizeof(uint), groups > _gl.CntCap);
        }
        if (drawN > _gl.CmdCap) _gl.CmdCap = drawN;
        if (groups > _gl.CntCap) _gl.CntCap = groups;
        _gl.DrawN = drawN; _gl.Groups = groups;
        return true;
    }

    /// <summary>Select this view's casters. <paramref name="point"/> picks the cube face's cone test over the
    /// cascade fit's axis test; a <see cref="AlloyRenderer.NeverCull"/> sphere is exempt from the reach
    /// tests -- the same exemption, from the same buffer, the colour cull applies.</summary>
    internal static void Dispatch(Vector3 focus, Vector3 dir, float reach, float along, bool point,
                                  float lodR1, float lodR2,
                                  int set = SetAll, bool receiver = false, bool dynSrc = false)
    {
        _gl.At = (_gl.At + 1) % Rotation;
        Gl.ZeroBuffer(_gl.CntBuf[_gl.At], (long)(_gl.Groups + CountExtra) * sizeof(uint));
        // S8: the source, and with it the thread count. Everything downstream is indexed by the records'
        // OWN batch and output base, so the shorter source writes into the same regions.
        bool dyn = dynSrc && DynamicReady;
        int drawN = dyn ? _gl.DynDrawN : _gl.DrawN;
        if (dyn) DynDispatches++;

        Gl.UseProgram(_gl.Prog);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, dyn ? _gl.DynSrcBuf : _gl.SrcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CullPass.BindSphere, CullPass.SphereBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCast, _gl.CastBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _gl.CmdBuf[_gl.At]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _gl.CntBuf[_gl.At]);
        Gl.Uniform1i(_gl.UDrawN, drawN);
        Gl.Uniform1i(_gl.UPoint, point ? 1 : 0);
        Gl.Uniform1i(_gl.UCull, Mode != 0 ? 1 : 0);
        Gl.Uniform1i(_gl.USet, set);
        Gl.Uniform1i(_gl.URx, receiver && Receiver != 0 ? 1 : 0);
        Gl.Uniform1i(_gl.UGroups, _gl.Groups);
        fixed (float* pl = Planes) Gl.Uniform4fv(_gl.UPlanes, 6, pl);
        float* v = stackalloc float[3];
        v[0] = focus.X; v[1] = focus.Y; v[2] = focus.Z;
        Gl.Uniform3fv(_gl.UFocus, 1, v);
        v[0] = dir.X; v[1] = dir.Y; v[2] = dir.Z;
        Gl.Uniform3fv(_gl.UDir, 1, v);
        Gl.Uniform1f(_gl.UReach, reach);
        Gl.Uniform1f(_gl.UAlong, along);
        Gl.Uniform1f(_gl.ULodR1, lodR1);
        Gl.Uniform1f(_gl.ULodR2, lodR2);
        Gl.DispatchCompute((uint)((drawN + GroupSize - 1) / GroupSize), 1, 1);
        // The commands are consumed as indirect state and the counts as parameter-buffer state; neither is in
        // the shader-storage class, so SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _gl.CmdBuf[_gl.At]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _gl.CntBuf[_gl.At]);
        Views++;
        _gl.Dispatched = true; _gl.SampleBuf = _gl.CntBuf[_gl.At]; _gl.SampleViews = 1; _gl.SampleRxViews = receiver ? 1 : 0;
        _gl.SampleDrawN = drawN; _gl.SampleDyn = dyn;
    }

    /// <summary>One batch, out of the compacted list the dispatch just wrote. <paramref name="start"/> is the
    /// batch's own output base (the region the dispatch compacted into), <paramref name="batch"/> indexes the
    /// parameter buffer, and <paramref name="max"/> is the batch's staged length -- the ceiling GL clamps the
    /// count to.</summary>
    internal static void Draw(uint mode, int batch, int start, int max)
    {
        Gl.MultiDrawElementsIndirectCount(mode, CasterPass.GlUnsignedShort,
                                          (void*)((long)start * CmdBytes),
                                          (IntPtr)((long)batch * sizeof(uint)), max, CmdBytes);
        Batches++;
    }

    /// <summary>Hand the indirect bindings back, so nothing later in the frame -- the colour pass's own
    /// submission -- finds them bound.</summary>
    internal static void End()
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, 0);
    }

    /// <summary>Once per frame, after the views: copy the LAST dispatched view's per-batch counts aside and
    /// harvest the copy from two frames ago. One view per frame is a sample, not a total -- but it is the
    /// same latent-copy instrument <see cref="CullPass"/> uses, it costs no stall, and a cull that started
    /// deleting shadows shows in it immediately.</summary>
    internal static void Sample()
    {
        if (!_gl.Dispatched || _gl.Groups <= 0 || _gl.SampleBuf == 0 || !GpuReadback.EntryPoints) return;
        // Sample-only entry points: a driver without them loses the figure, never the shadows.
        _gl.Dispatched = false;
        int w = _readSeq % GpuReadback.Slots, r = (_readSeq + 1) % GpuReadback.Slots;
        _readSeq++;
        if (_gl.Groups + CountExtra > _counts.Length)
        {
            int cap = _counts.Length;
            while (cap < _gl.Groups + CountExtra) cap *= 2;
            _counts = new uint[cap];
        }
        // a regrow drops every slot in flight, so none is read at a size it was not written at
        long need = (long)_counts.Length * sizeof(uint);
        if (need > _gl.SampleRead.SlotBytes) { Array.Clear(_gl.ReadN); if (!_gl.SampleRead.Ensure(need)) return; }
        // No extra barrier: the dispatch's own MemoryBarrier names BUFFER_UPDATE_BARRIER_BIT, which is the
        // bit that orders shader-storage writes before buffer-copy reads, and it stands between the last
        // dispatch's atomicAdds and this copy in command order.
        _gl.SampleRead.Copy(_gl.SampleBuf, 0, w, 0, (long)(_gl.Groups + CountExtra) * sizeof(uint));
        _gl.SampleRead.Fence(w);
        _gl.ReadN[w] = _gl.Groups; _gl.ReadDrawN[w] = _gl.SampleDrawN > 0 ? _gl.SampleDrawN : _gl.DrawN;
        _gl.ReadViews[w] = _gl.SampleViews; _gl.ReadRxViews[w] = _gl.SampleRxViews; _gl.ReadDyn[w] = _gl.SampleDyn;
        if (_gl.ReadN[r] <= 0) return;
        uint* c = (uint*)_gl.SampleRead.Ready(r);
        if (c == null) return;
        long kept = 0;
        for (int i = 0; i < _gl.ReadN[r]; i++) kept += c[i];
        // Against the draw count of the frame that WROTE those counts, not this frame's; a layered chunk's
        // counts cover every view it dispatched, so they are averaged back to one view -- the receiver
        // tally over the views that carried the flag.
        int views = Math.Max(_gl.ReadViews[r], 1), rxViews = Math.Max(_gl.ReadRxViews[r], 1);
        long keptV = Math.Min(kept / views, _gl.ReadDrawN[r]), rx = Math.Min(c[_gl.ReadN[r]] / rxViews, _gl.ReadDrawN[r]);
        // CX-50: ...and the triangles the sampled view(s) baked at each level, per view like the rest -- the
        // bake's twin of MeshLod.Drew. A chunk's counts cover every view it dispatched, hence the divide.
        for (int k = 0; k < MeshLod.Levels; k++) SampledTris[k] += c[TriWord(_gl.ReadN[r], k)] / views;
        if (_gl.ReadDyn[r])
        { SampledKeptDyn += keptV; SampledRxDyn += rx; SampledStagedDyn += _gl.ReadDrawN[r]; SamplesDyn++; }
        else
        { SampledKept += keptV; SampledRx += rx; SampledStaged += _gl.ReadDrawN[r]; Samples++; }
    }

    // --- #307: the layered bake. One dispatch selects casters for MANY stale views at once -- one thread
    // per (draw, view) pair -- and writes layer-tagged commands into one command buffer, region per batch
    // sized draws x views. The depth stages then route by gl_Layer out of the view table, so the whole bake
    // for one target texture is one dispatch plus one MDI per batch instead of that pair per view.

    /// <summary>The view table's SSBO binding, and the packing of a command's <c>baseInstance</c>:
    /// bits [0, <see cref="ViewShift"/>) are the ownership slot, the bits above are the view-table index the
    /// vertex stage reads its fit matrix and layer from. <c>CasterPass.SelfCheck</c> pins the shift against
    /// <see cref="AlloyRenderer.MaxOwned"/>.</summary>
    internal const uint BindView = SsboSlots.ViewTbl;
    internal const int ViewShift = 18;
    /// <summary>Floats per view-table record: mat4 vp + (focus, reach) + (dir, along) + (receiver, point,
    /// set, layer) + (the two detail thresholds, two spare). std430 gives this struct no padding.</summary>
    internal const int ViewFloats = 32;
    /// <summary>The staged view's words the layered dispatch reads its detail thresholds at -- a contract
    /// shared with <c>CxView.d</c>, which <c>CasterPass.StageView</c> fills.</summary>
    internal const int ViewLodR1 = 28, ViewLodR2 = 29;

    /// <summary>The view table's GLSL declaration, shared by the layered dispatch and both layered depth
    /// stages -- <c>AlloyRenderer.DrawBufDecl</c>'s pattern, so the three cannot drift apart.</summary>
    internal static string ViewTblDecl => ShaderSource.Get("cx_viewtbl.glsl");

    /// <summary>The per-slot caster sphere upload (<c>AlloyRenderer._dCastSph</c>), beside the colour cull's: always
    /// geometric, so a posed skin or dangly part the colour cull exempts is still reach-culled per shadow view.</summary>
    internal static void UploadCasterSpheres(float* spheres, int slots)
    {
        if (slots <= 0 || Gl.NamedBufferSubData == null) return;
        if (_gl.CastBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.CastBuf = b; _gl.CastCap = 0; }
        var size = (IntPtr)((long)slots * 4 * sizeof(float));
        if (slots > _gl.CastCap) { Gl.NamedBufferData(_gl.CastBuf, size, null, Gl.DYNAMIC_DRAW); _gl.CastCap = slots; }
        Gl.NamedBufferSubData(_gl.CastBuf, IntPtr.Zero, size, spheres);
    }

    /// <summary>Layered dispatches issued, and views they covered -- the period line's halves.</summary>
    internal static long LayerDispatches, LayerViews;

    /// <summary>The layered dispatch program could not build; the caller keeps the per-view path.</summary>
    internal static bool LayerFailed => _gl.LayerFailed;

    /// <summary>Build the layered dispatch. Same non-overridable stance as <see cref="Ensure"/>.</summary>
    internal static bool EnsureLayer()
    {
        if (_gl.LayerProg != 0) return true;
        if (_gl.LayerFailed || !Capable()) { _gl.LayerFailed = true; return false; }
        _gl.LayerProg = GlProgram.Compute(LayerCs, _log, "CasterGather layered caster cull");
        if (_gl.LayerProg == 0) { _gl.LayerFailed = true; return false; }
        _gl.LuDrawN = GlProgram.Loc(_gl.LayerProg, "uDrawN");
        _gl.LuViewN = GlProgram.Loc(_gl.LayerProg, "uViewN");
        _gl.LuCull = GlProgram.Loc(_gl.LayerProg, "uCull");
        _gl.LuRx = GlProgram.Loc(_gl.LayerProg, "uRx");
        _gl.LuGroups = GlProgram.Loc(_gl.LayerProg, "uGroups");
        _gl.LuPlanes = GlProgram.Loc(_gl.LayerProg, "uPlanes");
        return true;
    }

    /// <summary>Select every queued view's casters in ONE dispatch: upload the chunk's view table, size the
    /// command buffer to draws x views, and leave the indirect/parameter bindings armed for
    /// <see cref="DrawLayer"/>. Same rotation argument as <see cref="Dispatch"/> -- chunks are serial, and
    /// reusing a pair <see cref="Rotation"/> chunks later leaves the barriers standing.</summary>
    internal static bool DispatchLayer(float* views, int viewN, bool dynSrc = false)
    {
        if (_gl.LayerProg == 0 || _gl.DrawN <= 0 || viewN <= 0 || CullPass.SphereBuf == 0 || _gl.CastBuf == 0) return false;
        // S8, as in Dispatch. The COMMAND buffer stays sized by the full staging: a survivor's region is its
        // own batch's, which is the full staging's, whichever source the thread came out of.
        bool dyn = dynSrc && DynamicReady;
        int srcN = dyn ? _gl.DynDrawN : _gl.DrawN;
        if (dyn) DynDispatches++;
        if (_gl.ViewBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.ViewBuf = b; _gl.ViewCap = 0; }
        if (viewN > _gl.ViewCap)
        { Gl.NamedBufferData(_gl.ViewBuf, (IntPtr)((long)viewN * ViewFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW); _gl.ViewCap = viewN; }
        Gl.NamedBufferSubData(_gl.ViewBuf, IntPtr.Zero, (IntPtr)((long)viewN * ViewFloats * sizeof(float)), views);

        _gl.LyAt = (_gl.LyAt + 1) % Rotation;
        long cmds = (long)_gl.DrawN * viewN;
        bool growCmd = cmds > _gl.LyCmdCap, growCnt = _gl.Groups > _gl.LyCntCap;
        for (int i = 0; i < Rotation; i++)
        {
            Gl.EnsureStorage(ref _gl.LyCmd[i], cmds * CmdBytes, growCmd);
            Gl.EnsureStorage(ref _gl.LyCnt[i], (long)(_gl.Groups + CountExtra) * sizeof(uint), growCnt);
        }
        if (growCmd) _gl.LyCmdCap = cmds;
        if (growCnt) _gl.LyCntCap = _gl.Groups;
        Gl.ZeroBuffer(_gl.LyCnt[_gl.LyAt], (long)(_gl.Groups + CountExtra) * sizeof(uint));

        Gl.UseProgram(_gl.LayerProg);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, dyn ? _gl.DynSrcBuf : _gl.SrcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CullPass.BindSphere, CullPass.SphereBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCast, _gl.CastBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _gl.LyCmd[_gl.LyAt]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _gl.LyCnt[_gl.LyAt]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindView, _gl.ViewBuf);
        Gl.Uniform1i(_gl.LuDrawN, srcN);
        Gl.Uniform1i(_gl.LuViewN, viewN);
        Gl.Uniform1i(_gl.LuCull, Mode != 0 ? 1 : 0);
        Gl.Uniform1i(_gl.LuRx, Receiver != 0 ? 1 : 0);
        Gl.Uniform1i(_gl.LuGroups, _gl.Groups);
        fixed (float* pl = Planes) Gl.Uniform4fv(_gl.LuPlanes, 6, pl);
        Gl.DispatchCompute((uint)((srcN + GroupSize - 1) / GroupSize), (uint)viewN, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _gl.LyCmd[_gl.LyAt]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _gl.LyCnt[_gl.LyAt]);
        LayerDispatches++; LayerViews += viewN;
        int rxViews = 0;
        for (int i = 0; i < viewN; i++) if (views[i * ViewFloats + 24] != 0f) rxViews++;
        _gl.Dispatched = true; _gl.SampleBuf = _gl.LyCnt[_gl.LyAt]; _gl.SampleViews = viewN; _gl.SampleRxViews = rxViews;
        _gl.SampleDrawN = srcN; _gl.SampleDyn = dyn;
        return true;
    }

    /// <summary>One batch of the layered pass: the same indirect-count draw as <see cref="Draw"/>, with the
    /// batch's region and ceiling scaled by the chunk's view count.</summary>
    internal static void DrawLayer(uint mode, int batch, int start, int max, int viewN)
    {
        Gl.MultiDrawElementsIndirectCount(mode, CasterPass.GlUnsignedShort,
                                          (void*)((long)start * viewN * CmdBytes),
                                          (IntPtr)((long)batch * sizeof(uint)), max * viewN, CmdBytes);
        Batches++;
    }

    /// <summary>The cone slope, as the shader spells it. Interpolated from <see cref="CasterPass.FaceConeSlope"/>
    /// so the GPU test cannot drift from the CPU reference it is a port of, and pinned by
    /// <c>CasterPass.SelfCheck</c>.</summary>
    private static readonly string Slope =
        CasterPass.FaceConeSlope.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The two culls both dispatches share, <c>@SLOPE@</c> unexpanded: the receiver cull
    /// and the point-light face cull. Spliced, then the slope is interpolated over the whole text.</summary>
    private static string ReceiverCullLib => ShaderSource.Get("cx_recvcull.glsl");

    // CX-50: the per-view detail pick, spliced into BOTH dispatches from this one text so they cannot drift.
    // The thresholds are BOUNDS, not pixels: the CPU already divided its two pixel thresholds by the view's
    // world units per shadow texel, so the pick is two compares. lq is the caster's own bound, over its
    // distance from the light for a cube face (a 90deg face spans 2d at depth d, so its texels grow with
    // it). The ladder is MeshArena.Draw's, term for term -- level 2 falls back to level 1 and then to the
    // base run -- so a level the simplifier declined bakes the finer one and never nothing. Two cases need
    // no arithmetic at all and fall out of it: thresholds of 0 (detail off) and a bound exempt from the cull
    // (1e30, a slot with no caster sphere either), both of which keep the base run.
    // A posed skin or dangly reads its caster sphere here (uCast), so it is reach-culled per view; it still
    // bakes its base run, because no coarser run is generated for a skinned or dangly mesh.
    /// <summary>The detail pick's GLSL, with the view's focus, its point flag and its two bound thresholds
    /// spelled as the calling dispatch reads them. Leaves <c>cnt</c>/<c>first</c> declared: the command
    /// write takes them instead of the record's level-0 words.</summary>
    private static string LodPick(string focus, string point, string r1, string r2) => "\n" + $$"""
                uint cnt = uSrc[b], first = uSrc[b + 1u], lvl = 0u;
                if (cnt != 0u && {{r1}} > 0.0)
                {
                    float lq = {{point}} ? s.w / max(length(s.xyz - {{focus}}), 1e-4) : s.w;
                    if (lq < {{r1}})
                    {
                        uint c1 = uSrc[b + {{Cnt1Word}}u], c2 = uSrc[b + {{Cnt2Word}}u];
                        if (lq < {{r2}} && c2 != 0u) { cnt = c2; first = uSrc[b + {{First2Word}}u]; lvl = 2u; }
                        else if (c1 != 0u) { cnt = c1; first = uSrc[b + {{First1Word}}u]; lvl = 1u; }
                    }
                }
                atomicAdd(uCnt[uint(uGroups) + 1u + lvl], cnt / 3u);
        """ + "\n";

    /// <summary>The cull. One thread per staged draw; a survivor appends into its batch's own output region
    /// with an <c>atomicAdd</c>, so every batch keeps its boundaries and only its interior order moves. Both
    /// view tests are the CPU reference's, term for term, and the cone test is conservative BY CONSTRUCTION:
    /// admitting a caster a face cannot see costs a depth draw, rejecting one it can see deletes a shadow.</summary>
    internal static readonly string GatherCs = ("""
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize

        layout(std430, binding = 9) readonly buffer CxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 12) readonly buffer CxCastSphere { vec4 uCast[]; };
        layout(std430, binding = 8) readonly buffer CxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 10) writeonly buffer CxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 11) buffer CxGatherCount         { uint uCnt[]; };
        uniform int uDrawN;
        uniform int uPoint;
        uniform int uCull;
        uniform int uSet;
        uniform vec3 uFocus;
        uniform vec3 uDir;
        uniform float uReach;
        uniform float uAlong;
        uniform int uRx;
        uniform int uGroups;
        uniform float uLodR1;
        uniform float uLodR2;

        """ + ReceiverCullLib + """
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 11u;
            // #305: the set filter, ahead of the cull and NOT gated on uCull -- which half of the split a
            // caster belongs in is what the two arrays' contents mean, not a cull /shadow gather 0 may waive.
            if (uSet == 1 && uSrc[b + 6u] != 0u) return;
            if (uSet == 2 && uSrc[b + 6u] == 0u) return;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            // a bound the colour cull exempts (posed skin, dangly, displacing) takes the caster's geometric
            // sphere, pose slack included; only a slot with neither stays never reach-culled
            if (s.w >= 1e29) { vec4 g = uCast[slot]; if (g.w > 0.0 && g.w < 1e29) s = g; }
            if (uCull != 0 && s.w < 1e29)
            {
                float r = s.w;
                vec3 d = s.xyz - uFocus;
                float t = dot(d, uDir);
                bool offAxis, offEnds, rx = uRx != 0;
                vec3 p1 = s.xyz; float r0 = r, r1 = r;
                if (uPoint != 0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (uReach + r) * (uReach + r);
                    offEnds = CxFaceCulls(dSq, t, r);
                    // the tangent cone from the light, out to its reach; a light inside the caster keeps it
                    float dist = sqrt(dSq);
                    if (dist <= r) rx = false;
                    else { float k = 1.0 / (dist - r); r0 = r * dist * k; r1 = r * uReach * k; p1 = uFocus + d * (uReach / dist); }
                }
                else
                {
                    vec3 perp = d - t * uDir;
                    offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);
                    offEnds = abs(t) > uAlong + r;
                    p1 = s.xyz + uDir * max(0.0, uAlong - t);
                }
                if (offAxis || offEnds) return;
                if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }
            }
        """ + LodPick("uFocus", "uPoint != 0", "uLodR1", "uLodR2") + """
            uint o = uSrc[b + 5u] + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = cnt;                 // count, at this view's detail level
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = first;          // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot);     // baseInstance IS the ownership slot the depth stages read uDraw at
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>The layered cull -- <see cref="GatherCs"/>'s tests, term for term, with the view's parameters
    /// read out of the view table instead of uniforms and one thread per (draw, view) pair. A survivor's
    /// command region is its batch's staged region scaled by the chunk's view count, and its
    /// <c>baseInstance</c> packs the view-table index above the ownership slot, for routing by <c>gl_Layer</c>.</summary>
    internal static readonly string LayerCs = ("""
        #version 460 core
        layout(local_size_x = 64, local_size_y = 1) in;   // GroupSize

        layout(std430, binding = 9) readonly buffer CxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 12) readonly buffer CxCastSphere { vec4 uCast[]; };
        layout(std430, binding = 8) readonly buffer CxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 10) writeonly buffer CxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 11) buffer CxGatherCount         { uint uCnt[]; };

        """ + ViewTblDecl + """
        uniform int uDrawN;
        uniform int uViewN;
        uniform int uCull;
        uniform int uRx;
        uniform int uGroups;

        """ + ReceiverCullLib + """
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            uint vi = gl_GlobalInvocationID.y;
            if (i >= uint(uDrawN) || vi >= uint(uViewN)) return;
            uint b = i * 11u;
            vec4 vc = uViewTbl[vi].c;
            int set = int(vc.z);
            if (set == 1 && uSrc[b + 6u] != 0u) return;
            if (set == 2 && uSrc[b + 6u] == 0u) return;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            if (s.w >= 1e29) { vec4 g = uCast[slot]; if (g.w > 0.0 && g.w < 1e29) s = g; }
            if (uCull != 0 && s.w < 1e29)
            {
                float r = s.w;
                vec3 d = s.xyz - uViewTbl[vi].a.xyz;
                float reach = uViewTbl[vi].a.w;
                vec3 dir = uViewTbl[vi].b.xyz;
                float t = dot(d, dir);
                bool offAxis, offEnds, rx = uRx != 0 && vc.x != 0.0;
                vec3 p1 = s.xyz; float r0 = r, r1 = r;
                if (vc.y != 0.0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (reach + r) * (reach + r);
                    offEnds = CxFaceCulls(dSq, t, r);
                    float dist = sqrt(dSq);
                    if (dist <= r) rx = false;
                    else { float k = 1.0 / (dist - r); r0 = r * dist * k; r1 = r * reach * k; p1 = uViewTbl[vi].a.xyz + d * (reach / dist); }
                }
                else
                {
                    vec3 perp = d - t * dir;
                    offAxis = dot(perp, perp) > (reach + r) * (reach + r);
                    offEnds = abs(t) > uViewTbl[vi].b.w + r;
                    p1 = s.xyz + dir * max(0.0, uViewTbl[vi].b.w - t);
                }
                if (offAxis || offEnds) return;
                if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }
            }
        """ + LodPick("uViewTbl[vi].a.xyz", "vc.y != 0.0", "uViewTbl[vi].d.x", "uViewTbl[vi].d.y") + """
            uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = cnt;                 // count, at this view's detail level
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = first;          // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot) | (vi << 18u);   // baseInstance = ownership slot | view index
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the layered dispatch, on <see cref="SourceSafe"/>'s
    /// argument: every term whose silent drift deletes a shadow, spelled from the same constants.</summary>
    internal static bool LayerSafe()
        => LayerCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && LayerCs.Contains("offEnds = abs(t) > uViewTbl[vi].b.w + r;")
           && LayerCs.Contains("offAxis = dot(perp, perp) > (reach + r) * (reach + r);")
           && LayerCs.Contains($"binding = {CullPass.BindSphere})") && LayerCs.Contains("CxCullSphere")
           && LayerCs.Contains($"binding = {BindCast})") && LayerCs.Contains("if (s.w >= 1e29) { vec4 g = uCast[slot];")
           && LayerCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && LayerCs.Contains($"uint b = i * {SrcUints}u;")
           && LayerCs.Contains("if (set == 1 && uSrc[b + 6u] != 0u) return;")
           && LayerCs.Contains("if (set == 2 && uSrc[b + 6u] == 0u) return;")
           // CX-50: the detail pick, reading this view's own thresholds out of the view table's d lane --
           // whose float offsets are pinned against the struct DECLARATION both depth stages share -- and
           // the command taking the PICKED run. A pick whose result nothing reads is full detail
           // everywhere, silently, and a lane one float over is another view's thresholds.
           && ViewTblDecl.Contains("struct CxView { mat4 vp; vec4 a; vec4 b; vec4 c; vec4 d; };")
           && ViewFloats == 32 && ViewLodR1 == 28 && ViewLodR2 == 29
           && LayerCs.Contains("float lq = vc.y != 0.0 ? s.w / max(length(s.xyz - uViewTbl[vi].a.xyz), 1e-4) : s.w;")
           && LayerCs.Contains("if (lq < uViewTbl[vi].d.x)")
           && LayerCs.Contains("if (lq < uViewTbl[vi].d.y && c2 != 0u) { cnt = c2; first = uSrc[b + 10u]; lvl = 2u; }")
           && LayerCs.Contains("else if (c1 != 0u) { cnt = c1; first = uSrc[b + 8u]; lvl = 1u; }")
           && LayerCs.Contains("atomicAdd(uCnt[uint(uGroups) + 1u + lvl], cnt / 3u);")
           && LayerCs.Contains("uint c1 = uSrc[b + 7u], c2 = uSrc[b + 9u];")
           && LayerCs.Contains("uCmd[q] = cnt;") && LayerCs.Contains("uCmd[q + 2u] = first;")
           // the pack: the shift is ViewShift on both sides, the batch region scales by the view count, and
           // the slot rides the low bits the depth stage masks back out
           && LayerCs.Contains($"uCmd[q + 4u] = uint(slot) | (vi << {ViewShift}u);")
           && LayerCs.Contains("uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);")
           && LayerCs.Contains($"binding = {BindView})")
           // #488: the receiver cull is per-VIEW flagged (c.x), inside the uCull gate, and conservative in
           // the two-spheres form; a rejection tallies past the batch counts, never into one
           && LayerCs.Contains("bool offAxis, offEnds, rx = uRx != 0 && vc.x != 0.0;")
           && LayerCs.Contains("if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;")
           && LayerCs.Contains("if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }")
           && LayerCs.Contains("if (dist <= r) rx = false;")
           && LayerCs.Contains("p1 = s.xyz + dir * max(0.0, uViewTbl[vi].b.w - t);")
           && LayerCs.Contains($"binding = {BindSrc})") && LayerCs.Contains($"binding = {BindCmd})")
           && LayerCs.Contains($"binding = {BindCount})");

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the dispatch: the cull it runs is the CPU
    /// reference's, over the SHARED sphere buffer, with the shared exemption, conservative in the direction
    /// that costs a draw. Text rather than arithmetic because GLSL cannot be run headless, but every term
    /// that would silently delete a shadow if it drifted is named here.</summary>
    internal static bool SourceSafe()
        => GatherCs.Contains("if (t < -r) return true;")
           && GatherCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && GatherCs.Contains("return lim < 0.0 || dSq - t * t > lim * lim;")
           && GatherCs.Contains("offEnds = abs(t) > uAlong + r;")
           && GatherCs.Contains("offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);")
           // #303: the shared bounds, at the colour cull's own binding, with its own exemption value.
           && GatherCs.Contains($"binding = {CullPass.BindSphere})") && GatherCs.Contains("CxCullSphere")
           && GatherCs.Contains($"binding = {BindCast})") && GatherCs.Contains("if (s.w >= 1e29) { vec4 g = uCast[slot];")
           && GatherCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && GatherCs.Contains("uCmd[q + 4u] = uint(slot);")
           // #488: same receiver cull, flagged per dispatch
           && GatherCs.Contains("bool offAxis, offEnds, rx = uRx != 0;")
           && GatherCs.Contains("if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;")
           && GatherCs.Contains("if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }")
           && GatherCs.Contains("if (dist <= r) rx = false;")
           && GatherCs.Contains("p1 = s.xyz + uDir * max(0.0, uAlong - t);")
           // #305: the set filter reads the staged dynamic flag at the record stride, in BOTH directions --
           // a filter that lost either arm bakes the same caster into both arrays, and min() then hides it.
           // CX-50: ...and the stride is the one the staging writes, spelled as a LITERAL in the GLSL and
           // interpolated here, so a record that grows on one side only fails this.
           && GatherCs.Contains("uint b = i * 11u;") && SrcUints == 11
           && GatherCs.Contains("if (uSet == 1 && uSrc[b + 6u] != 0u) return;")
           && GatherCs.Contains("if (uSet == 2 && uSrc[b + 6u] == 0u) return;")
           // CX-50: the detail pick, and the command taking the PICKED run rather than the record's level-0
           // words -- a pick whose result nothing reads is full detail everywhere, silently. Every word index
           // is a LITERAL in the GLSL against the const the staging writes at, so the two cannot renumber
           // apart; the ladder is MeshArena.Draw's (level 2 where it exists, else level 1, else the base
           // run); and an entry turned OFF (count 0) stays off at every level.
           && Cnt1Word == 7 && First1Word == 8 && Cnt2Word == 9 && First2Word == 10
           && GatherCs.Contains("float lq = uPoint != 0 ? s.w / max(length(s.xyz - uFocus), 1e-4) : s.w;")
           && GatherCs.Contains("if (lq < uLodR1)")
           && GatherCs.Contains("if (lq < uLodR2 && c2 != 0u) { cnt = c2; first = uSrc[b + 10u]; lvl = 2u; }")
           && GatherCs.Contains("else if (c1 != 0u) { cnt = c1; first = uSrc[b + 8u]; lvl = 1u; }")
           // ...and the per-level tally sits past the batch counts and the receiver word, where Sample reads it
           && GatherCs.Contains($"atomicAdd(uCnt[uint(uGroups) + {TriWord(0, 0)}u + lvl], cnt / 3u);")
           && CountExtra == 4 && MeshLod.Levels == 3
           && GatherCs.Contains("uint c1 = uSrc[b + 7u], c2 = uSrc[b + 9u];")
           && GatherCs.Contains("if (cnt != 0u && uLodR1 > 0.0)")
           && GatherCs.Contains("uCmd[q] = cnt;") && GatherCs.Contains("uCmd[q + 2u] = first;")
           && GatherCs.Contains($"binding = {BindSrc})") && GatherCs.Contains($"binding = {BindCmd})")
           && GatherCs.Contains($"binding = {BindCount})");
}
