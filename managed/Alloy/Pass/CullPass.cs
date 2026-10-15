using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

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
        && SortCs.Contains($"uSrc[j * {SrcUints}u + 4u] != batch");

    /// <summary>Compute workgroup size; the dispatch is 1D over draws.</summary>
    private const int GroupSize = 64;

    /// <summary>The three dispatch phases, and the three output sets they fill. <see cref="PhaseRetest"/> is
    /// the prepass's second phase, with a set of its own because <c>MultiDrawElementsIndirectCount</c> takes a
    /// CPU byte offset for the start and phase one's count never comes back to say where its survivors end.</summary>
    internal const int PhasePrepass = 0, PhaseColour = 1, PhaseRetest = 2;
    /// <summary>The planar reflection's mirrored draw -- frustum, size and plane only, never sampled.</summary>
    internal const int PhaseMirror = 3;
    private const int Phases = 4;
    /// <summary>The shader's <c>uRej</c> mode per phase: the colour phase ignores the verdict buffer, phase
    /// one WRITES it, phase two READS it and tests only the marked draws.</summary>
    private const int RejOff = 0, RejWrite = 1, RejRead = 2;

    /// <summary>Run the prepass's second phase (<c>/alloy retest</c>). Off = phase one tests the frustum
    /// alone, which is the before half of the measurement.</summary>
    internal static bool Retest => Renderer.Retest.V;
    internal const bool DefRetest = true;
    /// <summary>Phase one of this pass tested against a stale pyramid, so it may have marked rejects
    /// and phase two has something to re-test. False when there was no pyramid to reproject (first pass,
    /// after <see cref="Revoke"/>, after a resize) -- phase one was frustum-only and marked nothing.</summary>
    internal static bool RetestPending;

    /// <summary>Everything the GL context owns, in one object: context loss replaces it.</summary>
    private sealed class CullGlState
    {
        public uint Prog, ProgHiZ, ProgSort;
        public bool Failed;
        public int UDrawN, UPlanes, UHiZ, UVP, UPyr, UPyrSize, UMaxLod;
        public int ULodPx, ULodScale, UCompactBelow, UEye, UFogCull, UFogOrigin, UPyrVP, URej, UClipZ;
        public int SDrawN, SCompactBelow;
        public int HSrc, HLevel;
        public uint SphBuf, KeyBuf, RejBuf;
        /// <summary>The source list as <see cref="Begin"/> streamed it; the dispatches of the same pass rebind it.</summary>
        public StreamRing.Range Src;
        public readonly uint[] CmdBuf = new uint[Phases], SlotBuf = new uint[Phases], CntBuf = new uint[Phases];
        public int CmdCap, CntCap, SphCap;
        public uint PyrTex, PyrFbo, PyrVao;
        public int PyrW, PyrH, PyrLevels;
        /// <summary>The pass generation the pyramid was last BUILT for -- what gates the colour dispatch and
        /// phase two, which must test the FRESH pyramid. A declined build (no depth texture, a zero viewport)
        /// would otherwise hand the same-frame test the previous frame's pyramid.</summary>
        public int PyrGen = -1;
        /// <summary>A pyramid from a PRIOR pass is resident and <see cref="PyrVp"/> is the view-projection it
        /// was built under, so phase one may reproject against it. Separate from <see cref="PyrGen"/>: phase one
        /// wants exactly the pyramid a declined build leaves behind. Cleared on recreate and the world edge.</summary>
        public bool PyrStale;
        public Matrix4x4 PyrVp;
        /// <summary>What the last readable dispatch kept, copied out of the count buffer two samples late
        /// through a mapped ring, so the period line reports culled-vs-drawn without a readback near submission.</summary>
        public GpuReadback SampleRead;
        /// <summary>Count words each ring slot actually holds, so a shorter batch cut never reads an older
        /// copy's tail as counts.</summary>
        public readonly int[] ReadN = new int[3], ReadDrawN = new int[3];
        public GpuReadback WatchRead;
        public readonly int[] WatchN = new int[3], WatchDraws = new int[3], WatchFrame = new int[3];
    }

    private static CullGlState _gl = new();
    private static ExtenderLog? _log;

    /// <summary>Capacity tracker for a sample slot: <see cref="RingPhases"/> ranges of per-batch counts.</summary>
    private static uint[] _counts = new uint[1024];
    private static int _readSeq;
    internal static long CulledDraws, KeptDraws, CullFrames;
    /// <summary>What the prepass's two phases kept, the same three-frames-late way. The staged
    /// figure the period line already prints is the ceiling; these are the GPU's counts under it.</summary>
    internal static long PrepassKept, RetestKept;
    /// <summary>Count words per ring slot: one range per phase, back to back.</summary>
    private const int RingPhases = 3;
    /// <summary>Colour-phase dispatches between two <see cref="Sample"/> calls. <c>CullFrames</c> counts
    /// samples, so the period averages are unchanged by the cadence.</summary>
    private const int SamplePeriod = 60;
    private static int _sampleTick;

    /// <summary>Live dial behind <c>/alloy cull</c>, three-state rather than two because culling is the one
    /// system whose bug is invisible geometry. <b>0</b> stands the whole pass down -- every owned part drawn
    /// every frame, with no cull anywhere, slow and correct, which is what makes it the A/B for "is the cull
    /// eating this?"; <b>1</b> is the frustum test alone; <b>2</b> adds the hi-Z occlusion test.</summary>
    internal static int Mode => Renderer.Cull.V;
    internal const int ModeOff = 0, ModeFrustum = 1, ModeHiZ = 2;

    /// <summary>LOD selection, in pixels: a draw whose bounding sphere projects to a screen radius smaller
    /// than this is dropped by the same dispatch that culls it. 0 disables it and <c>/alloy cull 0</c> stands
    /// it down with everything else, because it IS the cull shader. No tile exemption -- the hole it can
    /// leave is by construction under <see cref="LodPx"/> pixels wide.</summary>
    internal static float LodPx => Renderer.Lod.V;
    /// <summary>The boot threshold: one pixel of projected radius.</summary>
    internal const float DefLodPx = 1.0f;

    /// <summary>Drop geometry the fog has already painted over: a fragment past
    /// <see cref="SceneFog.OpaqueDistance"/> is EXACTLY the fully-fogged colour. Opaque is not enough, though
    /// -- what is behind it must be that colour too -- so the test is two conditions a frustum plane cannot
    /// express: past that distance AND entirely below the eye, where a downward ray never reaches the sky.</summary>
    internal static bool FogCull => SceneFog.Cull;

    /// <summary>Pixels per unit of world radius at unit view depth -- <c>proj.M22 * viewportHeight / 2</c>,
    /// so <c>radius * this / depth</c> is the sphere's screen radius. Zero for a pass whose projection is not
    /// a perspective one, and the LOD test stands down rather than dividing by a scale it cannot trust.
    /// </summary>
    private static float _lodScale;

    /// <summary>This pass's eye, the sort key's one input. The same quantity
    /// <c>AlloyRenderer.EyeDist2</c> measures from on the CPU, so the two orders agree and
    /// <c>/alloy cull 0</c> is a like-for-like A/B rather than a different sort.</summary>
    private static Vector3 _eye;

    /// <summary>The pass can actually run: the programs built and every entry point it needs exists.</summary>
    internal static bool Ready => _gl.Prog != 0;

    /// <summary>The extent of the staged sphere CENTRES, and the eye they were culled against. A diagnostic
    /// with one job: the bounds come from <c>PartTriMesh</c>'s bbox, which is world-space for a world-space
    /// part and object-space for a matrix-class one, and that reading has never been verified for TILES. If
    /// it is wrong for them every tile's sphere collapses toward one point, and this line says so.</summary>
    internal static float SpreadX, SpreadY, SpreadZ, SpreadMaxR;
    private static int _drawN, _groups, _phase = -1;

    /// <summary>The first SORTED batch -- everything below it compacts in the first dispatch, and
    /// everything at or above it is placed by the second. Kept because the dispatch needs it as a uniform
    /// and <see cref="SortCs"/> needs it to know which draws are its.</summary>
    private static int _compactBelow;

    /// <summary>The diagnostic above, measured by the staging walk that already touches every one of these
    /// spheres.</summary>
    internal static void SetSpread(float x, float y, float z, float maxR)
    { SpreadX = x; SpreadY = y; SpreadZ = z; SpreadMaxR = maxR; }

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The GL names are gone with the context; nothing is deleted (that would be a call into a
    /// dead context) and everything rebuilds on the next pass. See docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _gl = new CullGlState(); RetestPending = false;
        _phase = -1; _compactBelow = 0;
    }

    /// <summary>The world edge (area transition, module unload). The resident pyramid is a scene that
    /// no longer exists, so the next pass's phase one is frustum-only rather than reprojecting against it.
    /// Phase two would correct the rejections anyway; this only spares it the whole area's worth.</summary>
    internal static void Revoke() { _gl.PyrStale = false; RetestPending = false; }

    /// <summary>Everything the dispatch needs from GL -- a BOOT term: with the engine's bucket walk gone
    /// there is no other visibility test in the frame, so a driver that cannot run the dispatch cannot run
    /// Alloy at all (see <see cref="AlloyGate"/>).</summary>
    internal static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null
           && Gl.GenFramebuffers != null && Gl.FramebufferTexture2D != null;

    /// <summary>Build the two programs. Neither is an overridable stage (ADR 0012's table is the set a
    /// content author can restyle): an override here could only make the world invisible, and there is no
    /// look to restyle.</summary>
    private static bool Ensure()
    {
        if (_gl.Prog != 0) return true;
        if (_gl.Failed || !Capable()) return false;
        _gl.Prog = GlProgram.Compute(CullCs, _log, "CullPass frustum/hi-Z cull");
        _gl.ProgSort = GlProgram.Compute(SortCs, _log, "CullPass back-to-front rank sort");
        _gl.ProgHiZ = GlProgram.Graphics(PostChain.FullscreenVs, HiZFs, _log, "CullPass hi-Z pyramid");
        if (_gl.Prog == 0 || _gl.ProgHiZ == 0 || _gl.ProgSort == 0)
        {
            _gl.Failed = true; _gl.Prog = 0;
            // #264: hard, not a downgrade. The frame has no other visibility test, and the boot verdict has
            // already frozen on Alloy by the time a program can fail to build.
            AlloyGate.Fail("the cull compute program did not build -- Alloy has no visibility test");
            return false;
        }
        _gl.UDrawN = GlProgram.Loc(_gl.Prog, "uDrawN");
        _gl.UPlanes = GlProgram.Loc(_gl.Prog, "uPlanes");
        _gl.UHiZ = GlProgram.Loc(_gl.Prog, "uHiZ");
        _gl.UVP = GlProgram.Loc(_gl.Prog, "uVP");
        _gl.UPyr = GlProgram.Loc(_gl.Prog, "uPyramid");
        _gl.UPyrSize = GlProgram.Loc(_gl.Prog, "uPyrSize");
        _gl.UMaxLod = GlProgram.Loc(_gl.Prog, "uMaxLod");
        _gl.ULodPx = GlProgram.Loc(_gl.Prog, "uLodPx");
        _gl.ULodScale = GlProgram.Loc(_gl.Prog, "uLodScale");
        _gl.UCompactBelow = GlProgram.Loc(_gl.Prog, "uCompactBelow");
        _gl.UEye = GlProgram.Loc(_gl.Prog, "uEye");
        _gl.UFogCull = GlProgram.Loc(_gl.Prog, "uFogCull");
        _gl.UFogOrigin = GlProgram.Loc(_gl.Prog, "uFogOrigin");
        _gl.UClipZ = GlProgram.Loc(_gl.Prog, "uClipZ");
        _gl.UPyrVP = GlProgram.Loc(_gl.Prog, "uPyrVP");
        _gl.URej = GlProgram.Loc(_gl.Prog, "uRej");
        _gl.SDrawN = GlProgram.Loc(_gl.ProgSort, "uDrawN");
        _gl.SCompactBelow = GlProgram.Loc(_gl.ProgSort, "uCompactBelow");
        _gl.HSrc = GlProgram.Loc(_gl.ProgHiZ, "uSrc");
        _gl.HLevel = GlProgram.Loc(_gl.ProgHiZ, "uLevel");
        if (_gl.PyrVao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _gl.PyrVao = v; }
        _log?.Info("CullPass: armed -- GPU frustum + hi-Z cull, GPU back-to-front sort of the blending "
                   + "ranges, glMultiDrawElementsIndirectCount submission");
        return true;
    }

    /// <summary>Is the cull driving this pass's opaque submission? Asked by the two issue sites, and the
    /// ONLY thing that may switch them off the <c>BaseVertex</c> path.</summary>
    internal static bool Active => Mode > ModeOff && _phase >= 0;

    /// <summary>Upload the pass's draw list. Returns false when the cull is not running this pass, and the
    /// caller then submits exactly as before. <paramref name="groups"/> is every batch of the pass and
    /// <paramref name="compactBelow"/> is where the compacting half ends; <paramref name="src"/> is
    /// <see cref="SrcUints"/> uints per draw, staged by the batch walk itself.</summary>
    internal static bool Begin(int groups, int drawN, uint* src,
                              float lodScale, int compactBelow, Vector3 eye)
    {
        _phase = -1;
        RetestPending = false;   // a pass that declines below must not inherit the last one's phase two
        if (Mode <= ModeOff || drawN <= 0 || groups <= 0 || _gl.SphBuf == 0 || !Ensure()) return false;
        // The per-batch count array is indexed by batch; the zero-fill in Dispatch is what resets it. GROWN
        // rather than capped: a refusal here would be a refusal AFTER the staging walk had already dropped
        // the engine's visibility gate on the strength of Ready, and the frame would then draw the whole
        // area unculled -- the cap has to not exist rather than be generous.
        // #489: RingPhases ranges of counts per sample, so the ring read never outgrows it mid-frame.
        if (RingPhases * groups > _counts.Length)
        {
            int cap = _counts.Length;
            while (cap < RingPhases * groups) cap *= 2;
            _counts = new uint[cap];
        }

        _drawN = drawN; _groups = groups; _lodScale = lodScale;
        _compactBelow = Math.Clamp(compactBelow, 0, groups);
        _eye = eye;

        if (!StreamRing.Stream(StreamRing.Site.CullSource, BindSrc, src, (long)drawN * SrcUints * sizeof(uint), out _gl.Src))
            return false;   // CX-45: the cull sits this pass out, loudly (StreamRing logs and paints it)

        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSphere, _gl.SphBuf);

        for (int i = 0; i < Phases; i++)
        {
            Gl.EnsureStorage(ref _gl.CmdBuf[i], (long)drawN * CmdUints * sizeof(uint), drawN > _gl.CmdCap);
            Gl.EnsureStorage(ref _gl.SlotBuf[i], (long)drawN * sizeof(uint), drawN > _gl.CmdCap);
            Gl.EnsureStorage(ref _gl.CntBuf[i], (long)groups * sizeof(uint), groups > _gl.CntCap);
        }
        // #489: phase one's per-draw verdict, read back by phase two. One word per draw, written for EVERY
        // draw of phase one so a mark from an older list cannot linger under a shorter one.
        Gl.EnsureStorage(ref _gl.RejBuf, (long)drawN * sizeof(uint), drawN > _gl.CmdCap);
        // #290: the sort's scratch -- one key per draw, negative for a draw the cull dropped. One buffer
        // rather than a key array beside a keep array: distances are never negative, so the sign IS the
        // verdict, and the rank scan reads one word per neighbour instead of two.
        Gl.EnsureStorage(ref _gl.KeyBuf, (long)drawN * sizeof(float), drawN > _gl.CmdCap);
        if (drawN > _gl.CmdCap) _gl.CmdCap = drawN;
        if (groups > _gl.CntCap) _gl.CntCap = groups;
        _phase = -2;   // staged; the first Dispatch publishes a phase
        return true;
    }

    /// <summary>The per-ownership-slot <c>CxCullSphere</c> upload, outside the Mode gate: the shadow gather
    /// culls against this same buffer, so it must stay current under <c>/alloy cull 0</c> and during a probe
    /// capture alike. Indexed by RECORD slot, exactly as the records are, and uploaded whole -- four floats
    /// against the record's forty-four. Not on <see cref="StreamRing"/>: next frame's gather reads it.</summary>
    internal static void UploadSpheres(float* spheres, int sphereSlots)
    {
        if (sphereSlots <= 0 || Gl.NamedBufferSubData == null) return;
        if (_gl.SphBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.SphBuf = b; }
        var sphSize = (IntPtr)((long)sphereSlots * 4 * sizeof(float));
        if (sphereSlots > _gl.SphCap) { Gl.NamedBufferData(_gl.SphBuf, sphSize, null, Gl.DYNAMIC_DRAW); _gl.SphCap = sphereSlots; }
        Gl.NamedBufferSubData(_gl.SphBuf, IntPtr.Zero, sphSize, spheres);
    }

    /// <summary>The shared sphere buffer's GL name, for the shadow gather's dispatch. 0 until the first
    /// colour build uploads it, and the gather stands down for that frame.</summary>
    internal static uint SphereBuf => _gl.SphBuf;

    /// <summary>Run one dispatch into output set <paramref name="phase"/>. <paramref name="hiZ"/> false is a
    /// frustum-only dispatch; true adds the occlusion test -- for <see cref="PhaseColour"/> and
    /// <see cref="PhaseRetest"/> against the pyramid this pass just built, for <see cref="PhasePrepass"/>
    /// against the PRIOR pass's in its own clip space, which also marks the rejects phase two re-tests.</summary>
    internal static bool Dispatch(int phase, Matrix4x4* vp, bool hiZ, int gen, float lodPx = -1f, float clipZ = -1e30f)
    {
        bool stale = phase == PhasePrepass;
        if (stale) RetestPending = false;
        if (_phase == -1 || _drawN <= 0) return false;
        if (!StreamRing.Rebind(StreamRing.Site.CullSource, BindSrc, in _gl.Src)) return false;   // logged and painted; _phase stays unpublished, so the pass draws unculled
        Gl.ZeroBuffer(_gl.CntBuf[phase], (long)_groups * sizeof(uint));
        bool useHiZ = hiZ && Mode >= ModeHiZ && _gl.PyrTex != 0 && _gl.PyrLevels > 0
                      && (stale ? Retest && _gl.PyrStale : _gl.PyrGen == gen);
        if (stale)
        {
            RetestPending = useHiZ;
            // A frame without a phase two leaves phase two's counts from an older frame; Sample would read them.
            if (!useHiZ) Gl.ZeroBuffer(_gl.CntBuf[PhaseRetest], (long)_groups * sizeof(uint));
        }

        Gl.UseProgram(_gl.Prog);
        _gl.Src.Bind(BindSrc);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSphere, _gl.SphBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _gl.CmdBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOutSlot, _gl.SlotBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _gl.CntBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindKey, _gl.KeyBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindRej, _gl.RejBuf);
        Gl.Uniform1i(_gl.UDrawN, _drawN);
        Gl.UniformMatrix4fv(_gl.UVP, 1, 0, (float*)vp);
        // The pyramid's own clip space: this pass's for the same-frame phases, the prior pass's for phase one.
        if (stale && useHiZ) fixed (Matrix4x4* pv = &_gl.PyrVp) Gl.UniformMatrix4fv(_gl.UPyrVP, 1, 0, (float*)pv);
        else Gl.UniformMatrix4fv(_gl.UPyrVP, 1, 0, (float*)vp);
        Gl.Uniform1i(_gl.URej, stale ? RejWrite : phase == PhaseRetest ? RejRead : RejOff);
        float* planes = stackalloc float[24];
        Planes((float*)vp, planes, _eye);
        Gl.Uniform4fv(_gl.UPlanes, 6, planes);
        Gl.Uniform1f(_gl.ULodPx, MathF.Max(lodPx >= 0f ? lodPx : LodPx, 0f));
        Gl.Uniform1f(_gl.UClipZ, clipZ);
        Gl.Uniform1f(_gl.ULodScale, _lodScale);
        Gl.Uniform1i(_gl.UCompactBelow, _compactBelow);
        fixed (Vector3* e = &_eye) Gl.Uniform3fv(_gl.UEye, 1, (float*)e);
        // 0 = no fog cull at all, which is what an infinite opaque distance means: nothing past the frustum
        // is provably fogged out.
        float opaque = FogCull ? SceneFog.OpaqueDistance() : float.PositiveInfinity;
        Gl.Uniform1f(_gl.UFogCull, float.IsInfinity(opaque) ? 0f : opaque);
        Vector3 origin = SceneFog.Origin(out bool radial);
        Gl.Uniform4f(_gl.UFogOrigin, origin.X, origin.Y, origin.Z, radial ? 1f : 0f);
        Gl.Uniform1i(_gl.UHiZ, useHiZ ? 1 : 0);
        if (useHiZ)
        {
            Gl.Uniform1i(_gl.UPyr, (int)PyramidUnit);
            Gl.Uniform2f(_gl.UPyrSize, _gl.PyrW, _gl.PyrH);
            Gl.Uniform1f(_gl.UMaxLod, _gl.PyrLevels - 1);
            Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, _gl.PyrTex);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        uint groups1D = (uint)((_drawN + GroupSize - 1) / GroupSize);
        Gl.DispatchCompute(groups1D, 1, 1);
        // #290: the sort is a SECOND dispatch, and the barrier between them is load-bearing -- a draw ranks
        // itself against keys every other invocation of the first dispatch wrote, so none of them may still
        // be in flight. One pass cannot do both: a rank needs the whole batch's verdicts decided.
        // #489: phase two has no sorted draws to place -- the prepass never submits them, and the shader
        // returns early on every one.
        if (_compactBelow < _groups && phase != PhaseRetest && phase != PhaseMirror)
        {
            Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
            Gl.UseProgram(_gl.ProgSort);
            _gl.Src.Bind(BindSrc);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _gl.CmdBuf[phase]);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOutSlot, _gl.SlotBuf[phase]);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindKey, _gl.KeyBuf);
            Gl.Uniform1i(_gl.SDrawN, _drawN);
            Gl.Uniform1i(_gl.SCompactBelow, _compactBelow);
            Gl.DispatchCompute(groups1D, 1, 1);
        }
        // Both consumers are outside the shader-storage class: the commands are read as indirect state and
        // the count as parameter-buffer state. SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        // The pyramid's unit is shared with the probe's cube (TextureSlots.HiZ): leave nothing of ours on it.
        if (useHiZ)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        _phase = phase;
        // The kept/culled figure is a period statistic, so it is sampled on a period cadence: on Mesa the
        // GetBufferSubData behind it cost 2-5ms of every frame (a mapped read of a GPU-written buffer is a
        // wait there, however old the buffer), where NVIDIA charged nothing.
        if (phase == PhaseColour && ++_sampleTick >= SamplePeriod) { _sampleTick = 0; Sample(); }
        if (phase == PhaseColour && Renderer.CullWatch.V) Watch();
        return true;
    }

    /// <summary>The flicker hunt's GPU half (<c>alloy.cullwatch</c>): a fenced 3-slot ring of the colour
    /// dispatch's per-batch kept counts, read once its fence has passed and never waited on. A frame whose
    /// cull kept almost nothing of a full draw list is logged with its <see cref="AlloyRenderer.FrameSeq"/>,
    /// the number the CPU-side [vischeck] line carries too.</summary>
    private static int _watchSeq, _watchLogged;
    internal static long WatchReads, WatchLate, WatchCollapses;

    private static void Watch()
    {
        int w = _watchSeq % GpuReadback.Slots, r = (_watchSeq + 1) % GpuReadback.Slots;
        _watchSeq++;
        if (_gl.WatchN[r] > 0)
        {
            uint* c = (uint*)_gl.WatchRead.Ready(r);
            if (c != null)
            {
                int n = _gl.WatchN[r];
                long kept = 0;
                for (int i = 0; i < n; i++) kept += c[i];
                WatchReads++;
                if (_gl.WatchDraws[r] > 400 && kept * 8 < _gl.WatchDraws[r])
                {
                    WatchCollapses++;
                    if (_watchLogged++ < 6)
                        _log?.Always($"CullPass: [cullwatch] COLLAPSE frame {_gl.WatchFrame[r]}: the colour cull kept {kept} of "
                                   + $"{_gl.WatchDraws[r]} draws over {n} batches (mode {Mode}, hi-Z {(Mode >= ModeHiZ ? "on" : "off")})");
                }
                _gl.WatchN[r] = 0;
            }
            else WatchLate++;
        }
        if (_groups <= 0 || !GpuReadback.EntryPoints) return;
        long need = (long)_counts.Length * sizeof(uint);
        if (need > _gl.WatchRead.SlotBytes) { Array.Clear(_gl.WatchN); if (!_gl.WatchRead.Ensure(need)) return; }
        _gl.WatchRead.Copy(_gl.CntBuf[PhaseColour], 0, w, 0, (long)_groups * sizeof(uint));
        _gl.WatchRead.Fence(w);
        _gl.WatchN[w] = _groups; _gl.WatchDraws[w] = _drawN; _gl.WatchFrame[w] = AlloyRenderer.FrameSeq;
    }

    /// <summary>Texture unit the pyramid takes for the dispatch -- see <see cref="TextureSlots.HiZ"/> for
    /// why it is shared rather than its own.</summary>
    private const uint PyramidUnit = TextureSlots.HiZ;

    /// <summary>Bind the compacted list of output set <paramref name="phase"/> for submission: the indirect
    /// commands, the parameter buffer the count comes from, and the per-draw slot array the vertex stages
    /// already read. The stages are untouched -- the compacted array holds the same record slots in a
    /// different order, so <c>uDraw[uDrawIdx[uDrawBase + gl_DrawID]]</c> resolves exactly as it did.</summary>
    internal static void BindForDraw(int phase)
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _gl.CmdBuf[phase]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _gl.CntBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, AlloyRenderer.BindDrawIdx, _gl.SlotBuf[phase]);
    }

    /// <summary>...and hand the bindings back, so a later pass on the <c>BaseVertex</c> path (the blended
    /// and water ranges) does not find an indirect buffer bound under it.</summary>
    internal static void UnbindForDraw()
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, 0);
    }

    /// <summary>One batch, submitted from the compacted list. <paramref name="start"/> is the batch's
    /// output base -- the same <c>_gStart[g]</c> the record lookup uses as <c>uDrawBase</c>, because the
    /// cull compacts each batch into its OWN source region rather than into one global run -- and
    /// <paramref name="batch"/> indexes the parameter buffer, whose word is that batch's survivor count.</summary>
    internal static void Draw(uint mode, int batch, int start, int max)
        => Gl.MultiDrawElementsIndirectCount(mode, AlloyRenderer.GlUnsignedShort,
                                             (void*)((long)start * CmdUints * sizeof(uint)),
                                             (IntPtr)((long)batch * sizeof(uint)),
                                             max, CmdUints * sizeof(uint));

    /// <summary>Build the hi-Z pyramid from the depth the prepass just wrote. Level 0 is a 2x2 MAX reduction
    /// of the depth buffer, and MAX is what makes the test conservative: an object is culled only when its
    /// NEAREST depth is behind the FARTHEST scene depth anywhere in the region it covers. Restores the
    /// framebuffer, viewport and program the caller had -- the colour bracket is mid-pass.</summary>
    internal static void BuildHiZ(uint depthTex, int w, int h, int* viewport, uint restoreFbo, int gen,
                                  Matrix4x4* vp)
    {
        _gl.PyrGen = -1;
        if (_phase == -1 || _gl.ProgHiZ == 0 || depthTex == 0 || w <= 0 || h <= 0) return;
        if (!EnsurePyramid(w / 2, h / 2)) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.PyrFbo);
        // NOT a PassState: this runs INSIDE the colour bracket, between the prepass and the opaque pass, and
        // the bracket's own state (depth test on, depth writes on, sRGB as the target declared it) has to be
        // exactly what the opaque pass finds. Only the two things a full-screen reduction cannot tolerate
        // are touched, and both are put back below.
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.DepthMask(0);
        Gl.BindVertexArray(_gl.PyrVao);
        Gl.UseProgram(_gl.ProgHiZ);
        Gl.Uniform1i(_gl.HSrc, (int)PyramidUnit);
        Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
        for (int i = 0; i < _gl.PyrLevels; i++)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.PyrTex, i);
            Gl.Viewport(0, 0, Math.Max(_gl.PyrW >> i, 1), Math.Max(_gl.PyrH >> i, 1));
            if (i == 0) { Gl.BindTexture(Gl.TEXTURE_2D, depthTex); Gl.Uniform1i(_gl.HLevel, 0); }
            else
            {
                // Reading level i-1 of the texture level i is attached to: legal only while BASE/MAX_LEVEL
                // exclude the attachment, which is exactly what the clamp is for (PostChain's bloom chain
                // takes the same trade for the same reason).
                Gl.BindTexture(Gl.TEXTURE_2D, _gl.PyrTex);
                Gl.TextureParameteri(_gl.PyrTex, Gl.TEXTURE_BASE_LEVEL, i - 1);
                Gl.TextureParameteri(_gl.PyrTex, Gl.TEXTURE_MAX_LEVEL, i - 1);
                Gl.Uniform1i(_gl.HLevel, 0);
            }
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }
        // The dispatch samples arbitrary LODs, so the clamp has to come back off.
        Gl.TextureParameteri(_gl.PyrTex, Gl.TEXTURE_BASE_LEVEL, 0);
        Gl.TextureParameteri(_gl.PyrTex, Gl.TEXTURE_MAX_LEVEL, _gl.PyrLevels - 1);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        _gl.PyrGen = gen;
        // #489: and it is next pass's stale pyramid, under THIS (jittered) view-projection -- the one the
        // depth it reduces was drawn with.
        _gl.PyrVp = *vp; _gl.PyrStale = true;
    }

    private static bool EnsurePyramid(int w, int h)
    {
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (_gl.PyrTex != 0 && w == _gl.PyrW && h == _gl.PyrH) return true;
        if (_gl.PyrTex != 0) { uint t = _gl.PyrTex; Gl.DeleteTextures(1, &t); _gl.PyrTex = 0; }
        _gl.PyrStale = false;
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        uint tex; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &tex);
        Gl.TextureStorage2D(tex, levels, Gl.R32F, w, h);
        // NEAREST all the way: an interpolated max is not a max, and a filtered pyramid would cull against
        // depths that are in no texel of it.
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST_MIPMAP_NEAREST);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        if (_gl.PyrFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.PyrFbo = f; }
        _gl.PyrTex = tex; _gl.PyrW = w; _gl.PyrH = h; _gl.PyrLevels = levels;
        _log?.Info($"CullPass: hi-Z pyramid {w}x{h}, {levels} levels (R32F, max reduction)");
        return true;
    }

    /// <summary>The culled-vs-drawn figure, taken two samples behind the dispatch that produced it,
    /// off a <see cref="GpuReadback"/> once the copy has landed. Never a <c>glGetBufferSubData</c>: NVIDIA
    /// queues that behind the whole frame so far and waits it out, whichever buffer it names.</summary>
    private static void Sample()
    {
        if (_groups <= 0 || !GpuReadback.EntryPoints) return;
        int w = _readSeq % GpuReadback.Slots, r = (_readSeq + 1) % GpuReadback.Slots;
        _readSeq++;
        long bytes = (long)_groups * sizeof(uint);
        long need = (long)_counts.Length * sizeof(uint);
        if (need > _gl.SampleRead.SlotBytes) { Array.Clear(_gl.ReadN); if (!_gl.SampleRead.Ensure(need)) return; }
        // #489: the three screen phases' counts back to back -- colour, then prepass phase one, then phase two.
        for (int ph = 0; ph < RingPhases; ph++)
            _gl.SampleRead.Copy(_gl.CntBuf[ph == 0 ? PhaseColour : ph == 1 ? PhasePrepass : PhaseRetest], 0, w, ph * bytes, bytes);
        _gl.SampleRead.Fence(w);
        _gl.ReadN[w] = _groups; _gl.ReadDrawN[w] = _drawN;
        if (_gl.ReadN[r] <= 0) return;
        uint* c = (uint*)_gl.SampleRead.Ready(r);
        if (c == null) return;
        long kept = 0, pre = 0, re = 0;
        for (int i = 0; i < _gl.ReadN[r]; i++)
        { kept += c[i]; pre += c[_gl.ReadN[r] + i]; re += c[2 * _gl.ReadN[r] + i]; }
        // Against the draw count of the frame that WROTE those counts, not this frame's.
        KeptDraws += Math.Min(kept, _gl.ReadDrawN[r]);
        CulledDraws += Math.Max(_gl.ReadDrawN[r] - kept, 0);
        PrepassKept += Math.Min(pre + re, _gl.ReadDrawN[r]);
        RetestKept += Math.Min(re, _gl.ReadDrawN[r]);
        CullFrames++;
    }

    internal static void ResetStats() { CulledDraws = 0; KeptDraws = 0; CullFrames = 0; PrepassKept = 0; RetestKept = 0; }

    /// <summary>The six frustum planes of a row-major <c>view*proj</c>, Gribb-Hartmann, normalised so the
    /// plane distance is in world units and the sphere test is a plain compare. Row-major here means the
    /// combination is <c>clip = v * VP</c>, so the planes come off the COLUMNS. <paramref name="eye"/> is the
    /// eye the caller's vp was built from: a mirrored subframe measuring from the wrong camera is the bug.</summary>
    internal static void Planes(float* m, float* p, Vector3 eye)
    {
        // m[r*4+c] is row r, column c of the row-vector matrix: clip.c = sum_r v[r] * m[r*4+c].
        for (int i = 0; i < 6; i++)
        {
            int c = i >> 1;
            float s = (i & 1) == 0 ? 1f : -1f;
            float x = m[0 * 4 + 3] + s * m[0 * 4 + c];
            float y = m[1 * 4 + 3] + s * m[1 * 4 + c];
            float z = m[2 * 4 + 3] + s * m[2 * 4 + c];
            float w = m[3 * 4 + 3] + s * m[3 * 4 + c];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 0f) { x /= len; y /= len; z /= len; w /= len; }
            p[i * 4 + 0] = x; p[i * 4 + 1] = y; p[i * 4 + 2] = z; p[i * 4 + 3] = w;
        }

    }

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
        float* planes = stackalloc float[24];
        Planes((float*)&vp, planes, Vector3.Zero);
        bool Inside(Vector3 pt, float r)
        {
            for (int i = 0; i < 6; i++)
                if (planes[i * 4] * pt.X + planes[i * 4 + 1] * pt.Y + planes[i * 4 + 2] * pt.Z
                    + planes[i * 4 + 3] < -r) return false;
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
        Planes((float*)&vp, planes, Vector3.Zero);
        check.Expect(Inside(new Vector3(0, 0, -60), 1f), "fog does not slide a frustum plane: a point past the fog end survives it");
        FrameInputs.FogOn = fogWas; FrameInputs.FogEnd = endWas;
        Planes((float*)&vp, planes, Vector3.Zero);
        check.Expect(!Inside(new Vector3(0, 0, -500), 1f), "fog off: the far plane is the engine's again");

        // The pack, against what the shader reads out of it. Not a GL call in sight: this is a memory layout
        // shared with GLSL, and every field landing one slot over is a draw reading its neighbour's range.
        uint* src = stackalloc uint[2 * SrcUints];
        Pack(src, 30, 200, 7, 11, 0, 0);
        Pack(&src[SrcUints], 60, 400, 9, 13, 0, 0);
        check.Expect(src[0] == 30 && src[1] == 100 && src[2] == 7 && src[3] == 11 && src[4] == 0 && src[5] == 0,
          "draw 0 packs count/firstIndex/baseVertex/slot/batch/start in that order");
        check.Expect(src[SrcUints + 1] == 200, "...and the byte offset becomes an ELEMENT index (two-byte indices)");
        check.Expect(SourceStrideSafe(), "the cull and sort stages stride the source list by SrcUints");

        check.Ok &= Parity(log, vp, planes);
        log.Info(check.Ok ? "CullPass.SelfCheck: passed" : "CullPass.SelfCheck: FAILED -- see above");
    }

    /// <summary>One draw's source record, written where the batch walk already writes the same draw's
    /// count/offset/base. The one place the layout <see cref="CullCs"/> reads back is spelled out.</summary>
    internal static void Pack(uint* r, int idxCount, nint byteOff, int baseVertex, int slot,
                             int batch, int batchStart)
    {
        r[0] = (uint)idxCount;
        // The indirect command's firstIndex is in ELEMENTS; the staged offset is pre-multiplied to the byte
        // offset MultiDrawElementsBaseVertex takes. Two-byte indices, so the halving is exact.
        r[1] = (uint)((long)byteOff / 2);
        r[2] = (uint)baseVertex;
        r[3] = (uint)slot;
        r[4] = (uint)batch;
        r[5] = (uint)batchStart;
    }

    /// <summary>Does the compute-produced draw list agree with the CPU-staged one? A line-by-line port of
    /// <see cref="CullCs"/>'s <c>main</c>, run one invocation at a time -- which is what a sequential
    /// <c>atomicAdd</c> is. Two standards: a COMPACTING batch is asserted as a SET (the atomic's order is not
    /// deterministic on hardware), an ORDERED one as a SEQUENCE, which is the whole point of one.</summary>
    private static bool Parity(ExtenderLog log, Matrix4x4 vp, float* planes)
    {
        var check = new Checker(log, "CullPass.ParitySelfCheck");

        // Three batches, all inside the frustum the caller built: straight ahead at 10 units. Batch 2 is
        // the SORTED one -- #290's half, where the back-to-front order is produced rather than inherited.
        const int drawN = 10, groups = 3, compactBelow = 2;
        int* gStart = stackalloc int[groups] { 0, 3, 6 };
        int* gLen = stackalloc int[groups] { 3, 3, 4 };
        uint* src = stackalloc uint[drawN * SrcUints];
        float* sph = stackalloc float[drawN * 4];
        float* key = stackalloc float[drawN];
        var eye = Vector3.Zero;
        int n = 0;
        for (int g = 0; g < groups; g++)
            for (int d = 0; d < gLen[g]; d++, n++)
            {
                Pack(&src[(long)n * SrcUints], 30 + n, 2 * n, n, n, g, gStart[g]);
                float* s = &sph[(long)n * 4];
                s[0] = 0f; s[1] = 0f; s[2] = -10f; s[3] = 1f;
            }
        // ...and the three populations a cull has to get right: outside the frustum, exempt, sub-pixel.
        sph[1 * 4 + 0] = 500f;                                   // draw 1: far off to the side
        sph[4 * 4 + 3] = AlloyRenderer.NeverCull;                // draw 4: unknown bounds, never culled
        sph[5 * 4 + 3] = 0.001f;                                 // draw 5: a speck, for the LOD test
        // The sorted batch: near, far, equally far (the tie), and one the frustum drops.
        sph[6 * 4 + 2] = -10f;
        sph[7 * 4 + 2] = -30f;
        sph[8 * 4 + 2] = -30f;
        sph[9 * 4 + 0] = 500f;

        uint* cmd = stackalloc uint[drawN * CmdUints];
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
        uint* cmd2 = stackalloc uint[drawN * CmdUints];
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
    private static void Emulate(uint* src, float* sph, int drawN, int groups, float* planes, Matrix4x4 vp,
                                float lodPx, float lodScale, int compactBelow, Vector3 eye, float* key,
                                uint* cmd, int* outSlot, uint* cnt, bool scramble = false,
                                int rejMode = 0, uint* rej = null, bool* occ = null)
    {
        for (int g = 0; g < groups; g++) cnt[g] = 0;
        for (int q = 0; q < drawN; q++)
        {
            int i = scramble ? drawN - 1 - q : q;
            uint* b = &src[(long)i * SrcUints];
            bool sorted = b[4] >= (uint)compactBelow;
            if (rejMode == RejRead && (sorted || rej[i] == 0u)) continue;
            int slot = (int)b[3];
            float* s = &sph[(long)slot * 4];
            var c = new Vector3(s[0], s[1], s[2]);
            float r = s[3];
            bool keep = true;
            bool hiz = false;
            if (r < 1e29f)
            {
                for (int p = 0; p < 6 && keep; p++)
                    keep = !(planes[p * 4] * c.X + planes[p * 4 + 1] * c.Y + planes[p * 4 + 2] * c.Z
                             + planes[p * 4 + 3] < -r);
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
                if (keep) cnt[b[4]]++;
                continue;
            }
            if (!keep) continue;
            uint o = b[5] + cnt[b[4]]++;
            uint k = o * 5u;
            cmd[k] = b[0];
            cmd[k + 1u] = 1u;
            cmd[k + 2u] = b[1];
            cmd[k + 3u] = b[2];
            cmd[k + 4u] = 0u;
            outSlot[o] = slot;
        }
        // ...and the second dispatch: the rank sort.
        for (int q = 0; q < drawN; q++)
        {
            int i = scramble ? drawN - 1 - q : q;
            uint* b = &src[(long)i * SrcUints];
            uint batch = b[4];
            if (batch < (uint)compactBelow) continue;
            float k = key[i];
            if (k < 0f) continue;
            uint bas = b[5];
            uint rank = 0;
            for (uint j = bas; j < (uint)drawN; j++)
            {
                if (src[(long)j * SrcUints + 4] != batch) break;
                if (j == (uint)i) continue;
                float kj = key[j];
                if (kj < 0f) continue;
                if (kj > k || (kj == k && j < (uint)i)) rank++;
            }
            uint o = bas + rank;
            uint cw = o * 5u;
            cmd[cw] = b[0];
            cmd[cw + 1u] = 1u;
            cmd[cw + 2u] = b[1];
            cmd[cw + 3u] = b[2];
            cmd[cw + 4u] = 0u;
            outSlot[o] = (int)b[3];
        }
    }

    /// <summary>The submitted run really is back-to-front. Read off the SPHERES rather than the
    /// keys, so a key the emulator computed wrongly cannot agree with itself.</summary>
    private static bool Sorted(float* sph, int* outSlot, int start, int count, Vector3 eye, ExtenderLog log)
    {
        float prev = float.MaxValue;
        for (int o = start; o < start + count; o++)
        {
            float* s = &sph[(long)outSlot[o] * 4];
            Vector3 d = new Vector3(s[0], s[1], s[2]) - eye;
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
    private static bool Agrees(uint* src, int drawN, int groups, int* gStart, uint* cmd,
                              int* outSlot, uint* cnt, ExtenderLog log)
    {
        bool ok = true;
        for (int g = 0; g < groups; g++)
            for (int o = gStart[g]; o < gStart[g] + (int)cnt[g]; o++)
            {
                int found = -1;
                for (int d = 0; d < drawN; d++)
                    if ((int)src[(long)d * SrcUints + 3] == outSlot[o] && (int)src[(long)d * SrcUints + 4] == g)
                        found = d;
                if (found < 0)
                { log.Info($"CullPass.ParitySelfCheck FAIL: output {o} names a slot batch {g} never staged"); ok = false; continue; }
                uint* b = &src[(long)found * SrcUints];
                uint* k = &cmd[(long)o * CmdUints];
                if (k[0] != b[0] || k[1] != 1u || k[2] != b[1] || k[3] != b[2] || k[4] != 0u)
                { log.Info($"CullPass.ParitySelfCheck FAIL: output {o}'s command is not its staged draw's range"); ok = false; }
            }
        return ok;
    }

    /// <summary>The cull itself. One thread per draw; a survivor appends into its batch's own region with an
    /// atomicAdd, so the compacted list keeps every batch's boundaries and only its interior order moves. A
    /// radius of infinity is "do not cull me" -- skinned, dangly, a displacing feature, a degenerate bbox.
    /// Unknown bounds mean visible, always: the failure mode of this pass is invisible geometry.</summary>
    private static string CullCs => ShaderSource.Get("cx_cull_c.glsl");

    /// <summary>The back-to-front sort, as a RANK rather than a shuffle: a survivor counts how many
    /// survivors of its batch are FARTHER, and that count is its offset from the batch's output base. No key
    /// quantisation, so coplanar surfaces cannot tie into the wrong order, and the tie-break is the STAGED
    /// index. ponytail: O(n^2) compares per batch across n threads; a bitonic sort drops in if it ever shows.</summary>
    private static string SortCs => ShaderSource.Get("cx_cullsort_c.glsl");

    /// <summary>One level of the pyramid: the MAX of the source region the destination texel covers. A 3x3
    /// tap, not a 2x2, and that is a correctness fix: GL sizes mip <c>i</c> as <c>floor(size / 2^i)</c>, so a
    /// source level with an ODD dimension has a last row no 2x2 destination texel reads -- and a max missing
    /// texels fires the cull MORE readily. Over-covering by one texel only ever draws more.</summary>
    private static string HiZFs => ShaderSource.Get("cx_hiz_f.glsl");
}
