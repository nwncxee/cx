using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>BATCH ISSUE: upload the staged per-draw records and bone snapshots, then submit the built groups
/// as one multidraw per (program, VDO, texture, primitive) cut -- plus the blended and water variants that
/// ride the same records under different GL state. Hot: nothing on this path allocates. An ADR 0014 module,
/// not a partial: it reaches the renderer's tables through <c>using static</c>, the same static load.</summary>
internal static unsafe class Issue
{
    /// <summary>Forget every cached handle, the 4.6 context having gone and taken them with it. Cleared
    /// unconditionally, and nothing is made non-resident -- that would be a call into a dead context.</summary>
    internal static void ForgetHandles()
    {
        InvalidateRecords();   // #242: the persistent records hold the handles this forgets
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hVal, 0, _hVal.Length);
        Array.Clear(_hRes, 0, _hRes.Length);
    }

    /// <summary>Forget the cached handles at an enrollment. Only meaningful while the cache holds ENGINE names,
    /// which get recycled behind us; with <c>ownedtex</c> armed the names are Alloy's own and are invalidated
    /// precisely by <c>OwnedTextures.Drain</c> -&gt; <see cref="DropHandle"/>, so a wipe here would only make
    /// the next mint re-resident an already-resident handle (GL_INVALID_OPERATION).</summary>
    internal static void WipeHandles()
    {
        if (OwnedTextures.Armed) return;
        InvalidateRecords();   // #242: same reason as ForgetHandles -- a record outlives the wipe otherwise
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hRes, 0, _hRes.Length);
    }

    /// <summary>The sorted dirty list as runs of consecutive slots, one <c>BufferSubData</c> per run. Per
    /// slot was one call each, and radeonsi stages, copies and barriers EVERY call into a buffer the GPU is
    /// still reading (<c>si_buffer_subdata</c> forces DISCARD_RANGE), so the call count is the cost there.
    /// Duplicates collapse. Caller sorts once; both record buffers walk the same list.</summary>
    private static void UploadRuns<T>(uint buf, T[] data, int recBytes) where T : unmanaged
    {
        for (int d = 0; d < _recDirtyN;)
        {
            d = NextRun(_recDirty, _recDirtyN, d, out int first, out int last);
            int off = first * recBytes, len = (last - first + 1) * recBytes;
            fixed (byte* p = MemoryMarshal.AsBytes(data.AsSpan()).Slice(off, len))
                Gl.NamedBufferSubData(buf, (IntPtr)off, (IntPtr)len, p);
        }
    }

    /// <summary>The run of consecutive (or repeated) slots starting at sorted <c>_recDirty[d]</c>; returns
    /// the index past it.</summary>
    private static int NextRun(int[] dirty, int n, int d, out int first, out int last)
    {
        first = dirty[d]; last = first;
        while (++d < n && dirty[d] <= last + 1) last = dirty[d];
        return d;
    }

    /// <summary>The runs <see cref="NextRun"/> walks in the first <paramref name="n"/> of sorted
    /// <paramref name="dirty"/>.</summary>
    private static int CountRuns(int[] dirty, int n)
    {
        int runs = 0;
        for (int d = 0; d < n; runs++) d = NextRun(dirty, n, d, out _, out _);
        return runs;
    }

    /// <summary>The most runs one delta upload sends; past it the whole table goes up in one call. A delta costs
    /// its call count (<see cref="UploadRuns"/>), so the budget is in runs, not in a share of the view's draws.</summary>
    // ponytail: a guessed break-even; tune it from the period line's runs, or scatter the deltas from the
    // stream ring on the GPU, which makes every delta one call and leaves only growth whole.
    internal const int RunBudget = 512;

    /// <summary>Sort the dirty list for <see cref="UploadRuns"/> and count its runs.</summary>
    private static int SortedRuns()
    {
        int n = Math.Min(_recDirtyN, _recDirty.Length);
        Array.Sort(_recDirty, 0, n);
        return CountRuns(_recDirty, n);
    }

    /// <summary>A wrong run uploads a stale record over a live slot, silently, so this joins the disarm
    /// chain: duplicates collapse, adjacent slots merge, a gap splits, and a prefix counts only its own runs.
    /// Oracle: the hand-counted runs of the literal lists below.</summary>
    internal static bool RunsSelfCheck(ExtenderLog log)
    {
        int[] dirty = { 5, 3, 4, 9, 3, 11, 10 };
        Array.Sort(dirty);
        int d = NextRun(dirty, dirty.Length, 0, out int f0, out int l0);
        d = NextRun(dirty, dirty.Length, d, out int f1, out int l1);
        int[] three = { 3, 3, 4, 5, 9, 10, 11, 20 };
        bool ok = f0 == 3 && l0 == 5 && f1 == 9 && l1 == 11 && d == dirty.Length
               && CountRuns(three, three.Length) == 3 && CountRuns(three, 4) == 1 && CountRuns(three, 0) == 0;
        if (!ok) log.Info("AlloyRenderer.RunsSelfCheck: FAILED -- the dirty-slot runs do not merge or count as sorted");
        return ok;
    }

    /// <summary>Hand the moved records and this pass's draw order to the Vulkan frame. False = the frame sits out.</summary>
    internal static bool UploadDraws() => VkHandOff(_recOwnCap + MaxSkyDraws);

    /// <summary><see cref="UploadDraws"/> on Vulkan: no GL record table, handle table or stream ring -- the moved
    /// records go to the Vulkan table's dirty list, which uploads them in its own frame.</summary>
    private static bool VkHandOff(int cap)
    {
        bool whole = _recDirtyAll || cap > _recCapGpu;
        if (cap > _recCapGpu) _recCapGpu = cap;
        StaleMirrorRecords(cap, whole, _recDirty, _recDirtyN);
        VkNoteDirty(_recDirty, Math.Min(_recDirtyN, _recDirty.Length), whole);
        _recDirtyN = 0; _recDirtyAll = false; _texHDirty = false;
        return true;
    }

    /// <summary>One period's record-table uploads: the whole ones by reason, the bytes the whole record and handle
    /// tables sent (each one SubData into a buffer the GPU may still be reading), the deltas by their runs, and the
    /// most runs any upload had. <c>EpochSent</c> spans periods: a whole upload on a newer epoch is a restage.</summary>
    internal sealed class UploadState
    {
        internal long Growth, Overflow, Restage, OverBudget, RecordBytes, HandleBytes, Deltas, DeltaRuns;
        internal int MaxRuns, EpochSent;
        internal long Whole => Growth + Overflow + Restage + OverBudget;
    }

    /// <summary>This period's upload tallies; <see cref="ResetPeriod"/> replaces it.</summary>
    internal static UploadState Uploads = new();

    /// <summary>Start a period's tallies, keeping the record epoch the last upload sent.</summary>
    internal static void ResetPeriod() => Uploads = new() { EpochSent = Uploads.EpochSent };

    /// <summary>The staged draw-to-slot array's ring slice, for <see cref="BindStagedSlots"/>.</summary>
    internal static StreamRing.Range _slotRange;

    /// <summary>Context loss: forget the ring slice and the handle table's capacity, both of which describe
    /// buffers that died with it, so the next upload sizes the new buffer before writing into it.</summary>
    internal static void ContextLost() { _slotRange = default; _texHCapGpu = 0; }

    /// <summary>The handle table alone. Its capacity is tracked apart from the draw records' so the bake can
    /// bring it up to date on a frame the colour bracket has not reached (or refused).</summary>
    /// <summary>The handle table as the GPU sees it: <see cref="AlloyRenderer._dTexH"/> with every zero
    /// replaced by a stand-in's handle; a partial upload rewrites only the dirty records it sends.</summary>
    private static readonly TexHandles[] _gpuTexH = new TexHandles[MaxRecords];

    internal static void UploadHandles(int cap, bool whole)
    {
        if (_texHBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _texHBuf = b; }
        if (cap > _texHCapGpu)
        {
            Gl.NamedBufferData(_texHBuf, (IntPtr)((long)cap * 8 * sizeof(ulong)), null, Gl.DYNAMIC_DRAW);
            _texHCapGpu = cap; whole = true;
        }
        // The GPU copy carries no zero handle: a null handle samples zeros on NVIDIA and Mesa but is a GPU fault
        // on AMD's driver. Slot 0 (the diffuse) goes to the white stand-in the record means by it, every map
        // slot to the transparent-black texel; the CPU table keeps its zeros, which ZeroHandle and the stale
        // audit read.
        ulong whiteH = HandleFor(OwnedTextures.WhiteTid()), nullH = HandleFor(OwnedTextures.NullTid());
        long n = (long)cap * TexHandles.MaterialSlots;
        if (whole)
            for (int rec = 0; rec < cap; rec++) GpuRecordHandles(rec, whiteH, nullH);
        else
            for (int d = 0; d < _recDirtyN; d++)
                if (_recDirty[d] < cap) GpuRecordHandles(_recDirty[d], whiteH, nullH);
        fixed (TexHandles* p = _gpuTexH)
        {
            BindlessLedger.CheckTable((ulong*)p, n, "the record handle table");
            if (whole)
            {
                Gl.NamedBufferSubData(_texHBuf, IntPtr.Zero, (IntPtr)(n * sizeof(ulong)), p);
                Uploads.HandleBytes += n * sizeof(ulong);
            }
            else
                UploadRuns(_texHBuf, _gpuTexH, sizeof(TexHandles));
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindTexH, _texHBuf);
        StaleMirrorHandles(cap, whole, _recDirty, _recDirtyN);
        _texHDirty = false;
    }
    private static int _texHCapGpu;

    /// <summary>Record <paramref name="rec"/>'s handles as the GPU must see them: no zero handle.</summary>
    private static void GpuRecordHandles(int rec, ulong whiteH, ulong nullH)
    {
        for (int s = 0; s < TexHandles.MaterialSlots; s++)
        {
            ulong v = _dTexH[rec][s];
            _gpuTexH[rec][s] = v != 0 ? v : s == 0 ? whiteH : nullH;
        }
    }


    /// <summary>Put the CPU-staged per-draw index array back on <see cref="BindDrawIdx"/>. The cull binds its
    /// own compacted one for the passes it drives; every other pass reads the staged order.</summary>
    internal static bool BindStagedSlots() => StreamRing.Rebind(StreamRing.Site.DrawSlots, BindDrawIdx, in _slotRange);


    /// <summary>One bone block, <c>_boneSlots</c> palettes from <paramref name="stage"/>, onto
    /// <paramref name="binding"/>.</summary>
    internal static void UploadBoneBlock(ref uint buf, BonePalette* stage, uint binding)
    {
        if (buf == 0) { uint b; Gl.CreateBuffers(1, &b); buf = b; }
        var size = (IntPtr)((long)Math.Max(_boneSlots, 1) * BoneFloats * sizeof(float));
        Gl.NamedBufferData(buf, size, null, Gl.DYNAMIC_DRAW);
        if (_boneSlots > 0)
            Gl.NamedBufferSubData(buf, IntPtr.Zero,
                                  (IntPtr)((long)_boneSlots * BoneFloats * sizeof(float)), stage);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, binding, buf);
    }

    /// <summary>The blended pass's per-creature filter: when set, only batches tagged with this run gob
    /// issue; when zero, only UNTAGGED ones. Zero-valued and unset (<see cref="_onlyGobSet"/> false) issues
    /// everything, which is every other pass.</summary>
    internal static IntPtr _onlyGob;
    internal static bool _onlyGobSet;

    /// <summary>The program the last issue loop left bound (-1 none). <see cref="_holdProg"/> lets the next
    /// loop start from it instead of re-sending the rig; set only when nothing binds between the two.</summary>
    internal static int _heldProg = -1;
    internal static bool _holdProg;

    /// <summary>One colour-bracket range, from the cull's compacted list where the cull ran. The indirect
    /// buffer is bound around the RANGE, not the whole bracket: the sky, grass, beam and particle passes bind
    /// buffers of their own in between, and one left bound under them shows up as a wrong batch, not an
    /// error.</summary>
    internal static void IssueColour(int from, int to, Matrix4x4* vp, int* viewport, float near, float far)
    {
        bool indirect = CullPass.Active;
        if (indirect) CullPass.BindForDraw(CullPass.PhaseColour);
        IssueBatches(from, to, in *vp, viewport, near, far, indirect);
        if (indirect) { CullPass.UnbindForDraw(); BindStagedSlots(); }
    }

    internal static void IssueBatches(int from, int groups, in Matrix4x4 vp, int* viewport, float near, float far,
                                     bool indirect = false)
    {
        long tIssue = FrameZones.Now;
        try { IssueBatchesInner(from, groups, vp, viewport, near, far, indirect); }
        finally { FrameZones.Add(FrameZones.Zone.Issue, tIssue); }
    }

    /// <summary>Did a batch write the depth <see cref="SsaoPass"/> read, and if not, WHICH of the prepass's
    /// exclusions caught it? A batch the prepass skipped must not consume the visibility buffer at all. The
    /// reason travels to the lit stage as the SIGN of <c>uSsaoOn</c> (which tests <c>&gt; 0</c>), so
    /// <c>/alloy viz 35</c> can tell "has no AO" from "CANNOT have AO" on the pixel.</summary>
    internal const int AoIn = 1, AoCutout = -1, AoOutOfPrepass = -2, AoBlended = -3, AoKeyhole = -4;

    /// <summary>Membership is MEASURED (<see cref="_gPrepassed"/>, written by the prepass as it walks); only
    /// the reason a batch is out is inferred, and only to colour <c>/alloy viz 35</c>. Predicting membership
    /// from the class was the bug this replaces.</summary>
    internal static int AoWhy(int g)
        => _gPrepassed[g] ? AoIn
         : g >= _gOpaqueEnd ? AoBlended
         : _gCls[g] == (byte)AlloyClass.Cutout ? AoCutout
         : ShaderFeatures.OutOfPrepass(_gFeat[g]) ? AoOutOfPrepass
         : _gDis[g] ? AoKeyhole
         : AoOutOfPrepass;

    /// <summary>Phase B: one bind set + one <c>glMultiDrawElementsBaseVertex</c> per batch. uDrawBase is
    /// re-sent for every batch, and it is a DIFFERENT location per program, so a program switch cannot leave a
    /// stale one behind; the stages read record uDrawBase + gl_DrawID for the per-part model matrix, texture
    /// rotation and material colours.</summary>
    internal static void IssueBatchesInner(int from, int groups, in Matrix4x4 vp, int* viewport, float near, float far,
                                          bool indirect)
    {
        // A probe capture must not carry the debug viz through: /alloy viz would bake flat cyan (or a
        // cluster heat map) into the cube and leave it there for every frame that samples it afterwards.
        int viz = _capturing ? 0 : _lightViz;
        // Which program is bound, -1 for neither yet. In the opaque range this switches once (lit batches
        // sort last inside a class -- #292 put the face-culling bit BELOW the lit one precisely so that
        // stays true); in the blended range it can alternate, because there the order is the camera's and
        // nothing else. ponytail: a lit rebind still re-sends the feature, fog and lit-only terms (the pass
        // block covers the receiver and light curve, CX-43), so an alternating blended set pays for them per
        // switch. The set is the small remainder by construction; if it ever is not, move those terms into
        // CxPass or add a second sort digit inside the distance order.
        // #129: three programs now, not two -- fullbright (0), clustered lit (1) and water (2). Keyed on the
        // batch's CLASS for water and on _gLit for the other two, because water is a class the whole way
        // down: its stage samples the framebuffer copy, and no lit batch ever should.
        int boundProg = _holdProg ? _heldProg : -1;
        _holdProg = false;
        uint boundVao = 0; int boundTex = -1;
        uint boundSrc = 0, boundDst = 0;
        // GL_BLEND as this pass found it: DrawOwned's preamble disables it, and TransparentPass enables it
        // before its own range, so the first batch of either kind agrees with the state already set.
        bool boundBlend = _blendOn;
        byte boundMask = _passDepthMask;
        uint boundFunc = Gl.LEQUAL;
        bool boundTest = true;
        bool boundOffset = false;
        // #292: face culling, per batch. -1 rather than a guess at the preamble's state: the sky, grass,
        // particle and beam passes all leave their own culling behind them, and a probe capture re-enters
        // this loop six more times, so the first batch of every entry issues the call rather than trusting
        // what someone else set.
        int boundCull = -1;
        float boundCut = float.NaN;   // per PROGRAM: reset on every UseProgram below
        int litBase = -1, litCut = -1;   // the bound lit program's, whichever of the two it is
        int litSsaoOn = -1, litAoDirect = -1;
        // CX-273: the decal layer's mode for this loop, and the bound lit program's uDecal (-1 = not yet sent)
        int decalMode = DecalLayer.Mode, litDecal = -1, boundDecal = -1;
        bool decalWrite = decalMode == DecalLayer.ModeWrite;
        int litSolid = -1, boundSolid = -1;
        bool interior = _split.Interior;
        if (boundProg is 1 or 4 or 5 or 6 or 8 || ContentPrograms.IsProg(boundProg))
        {
            ref LitLoc H = ref LitLocOf(boundProg);
            litBase = H.Base; litCut = H.Cut;
            litSsaoOn = H.SsaoOn; litAoDirect = H.AoDirect; litDecal = H.Decal; litSolid = H.Solid;
        }
        // #379 follow-up: SSAO is per BATCH, not per program bind. The visibility buffer is computed from
        // the DEPTH PREPASS, and a batch the prepass skipped (cutout, an out-of-prepass feature, the whole
        // blended range) has no depth of its own in it -- its fragments would read the occlusion of whatever
        // stands BEHIND them. Live: the far corridor's occlusion showing through a closed door.
        int boundSsao = -1;
        int* boundSlot = stackalloc int[MapSlots];
        int drawn = 0;
        int wasStage = Submit.At(_onlyHosted ? Submit.Stage.Hosted : Submit.Stage.Colour);
        for (int s = 0; s < MapSlots; s++) boundSlot[s] = -1;
        for (int g = from; g < groups; g++)
        {
            if (_onlyGobSet && _gGob[g] != _onlyGob) continue;
            bool water = _gCls[g] == (byte)AlloyClass.Water;
            bool lit = _gLit[g];
            // CX-303: a content pair draws through a program of its own, deferred twin included; everything else lit
            // through the plain one (1). Batches are cut on the feature id, so this is one switch per content batch.
            bool content = !water && HostedBody(lit, _gFeat[g]);
            bool oitOut = SitsOitOut(content, _gSrc[g], _gDst[g]);
            if (_oitOn && oitOut) { _oitDeferred++; continue; }
            if (_onlyHosted && !oitOut) continue;
            // a split wash: WashInteriors draws its solid interior with the cutouts, and the blended pass the graded rest
            // CX-303: a content wash splits too -- its interior through its own program, its edge after the resolve
            bool split = _gWash[g] == WashSplit && lit && g >= _gOpaqueEnd
                         && (!oitOut || content && _gSrc[g] == AuroraSrcAlpha && _gDst[g] == AuroraOneMinusSrcAlpha);
            if (interior && !split) continue;
            // CX-188 (ADR 0029): the DEFERRED twin (5) takes the opaque and cutout lit batches of the main
            // view -- the only draws whose pixels the deferred compute pass reads. Everything else keeps the
            // forward loop: a capture or the mirror (no G-buffer to read back), any /alloy viz but the
            // light-source one (so every debug view stays truthful), a content pair with no twin, and the blended range through the OIT twin.
            bool deferred = lit && !water && (!content || ContentPrograms.HasDeferred(_gFeat[g]))
                            && (viz == 0 || viz == AlloyRenderer.VizLightFrom) && !_capturing
                            && (g < _gOpaqueEnd || interior) && DeferredLights.Active;
            int wantProg = decalWrite && lit ? 8 : water ? 2
                         : content ? ContentPrograms.ProgId(_gFeat[g], deferred)
                         : deferred ? 5 : lit ? 1 : 0;
            if (boundProg != wantProg)
            {
                boundProg = wantProg;
                boundCut = float.NaN;
                // CX-188: only the twin declares outputs on draw buffers 4..5, and a draw buffer no output
                // reaches is UNDEFINED rather than untouched -- so every other program in these passes draws
                // with those masked off and leaves whatever record is already there.
                if (GBuffer.Armed) GBuffer.MaskColour(wantProg == 5 || ContentPrograms.IsDeferred(wantProg));
                // CX-51: the probe's alpha is 1 / distance, and only the lit stage writes one; the rest keep the clear's "far"
                if (_capturing && !_planar) Gl.ColorMask(1, 1, 1, (byte)(wantProg == 1 ? 1 : 0));
                if (water)
                {
                    Gl.UseProgram(Programs._progWater);
                    fixed (Matrix4x4* vpm = &vp) Gl.UniformMatrix4fv(Programs._watVp, 1, 0, (float*)vpm);
                    fixed (Matrix4x4* wv = &_waterView) Gl.UniformMatrix4fv(Programs._watView, 1, 0, (float*)wv);
                    fixed (Matrix4x4* wp = &_waterProj) Gl.UniformMatrix4fv(Programs._watProj, 1, 0, (float*)wp);
                    Gl.Uniform2f(Programs._watNearFar, _waterNear, _waterFar);
                    AlloyLights.BindForDraw();
                    Gl.Uniform1i(Programs._watViz, viz);
                    Gl.Uniform1i(Programs._watEnvOk, FrameSnapshot.EnvOk);
                    Gl.Uniform1i(Programs._watCapture, _capturing ? 1 : 0);
                    Gl.Uniform1f(Programs._watSplash, Weather.SplashRate(_capturing));
                    Weather.BindRipple();
                    UploadFeatureTerms(Programs._watFeat, viewport);
                    BindPass(Programs._watCubeCount, viewport, near, far);
                    // The cascade and cube binds each left their own unit current for a moment; the copy
                    // pair and the noise texture have to be back on their units before the first water draw
                    // samples them.
                    FrameSnapshot.Bind();
                }
                else if (lit)
                {
                    ref LitLoc L = ref LitLocOf(wantProg);
                    Gl.UseProgram(wantProg == 8 ? Programs.ProgDecal
                                  : content ? ContentPrograms.Program(wantProg)
                                  : deferred ? Programs._progLitDef : Programs._progLit46);
                    Gl.Uniform1i(L.Tex, 0);
                    fixed (Matrix4x4* vpm = &vp) Gl.UniformMatrix4fv(L.Vp, 1, 0, (float*)vpm);
                    AlloyLights.BindForDraw();
                    Gl.Uniform1i(L.Viz, viz);
                    Gl.Uniform1f(L.AoCutout, Ao.Cutout.V);
                    Gl.Uniform1f(L.AlphaMip, Renderer.CutMip.V);
                    Gl.Uniform1i(L.LightRecOn, _lightRec ? 1 : 0);
                    Gl.Uniform1i(L.Capture, _capturing ? (_planar ? 2 : 1) : 0);
                    Gl.Uniform1f(L.MirrorZ, _mirrorZ);
                    LightmapBake.BindForDraw(L.LmPages, L.LmMovers);
                    UploadFeatureTerms(L.Feat, viewport);
                    BindPass(L.CubeCount, viewport, near, far);
                    // not inside the OIT bracket: the scene depth is that framebuffer's depth attachment there, and
                    // no blended batch reads SSAO (AoBlended) -- a bound, attached depth is a driver decompress
                    SsaoPass.BindForDraw(SsaoPass.Live && !_capturing && !_oitOn, PostChain.DepthTex, depthToo: wantProg == 8);
                    Gl.Uniform2f(L.DecalZ, DecalLayer.Z0, DecalLayer.Z1);
                    Gl.Uniform1i(L.SsaoHalf, SsaoPass.Half ? 1 : 0);
                    Gl.Uniform2f(L.LightOcc, Lighting.HiddenLights.V ? 1f : 0f, GlossyRough);
                    UploadMaterialModel(L.Material);
                    Weather.UploadLit(L.Weather, L.WeatherLook, L.CoverVp, L.CoverLayer, L.SnowLook, L.SnowLight, L.SnowPom, L.WxEdge, L.WxRun, L.WxCache, _capturing);
                    // Not in the probe capture: CxAmbientShape reads the probe back, so the term would land twice.
                    SkyView.UploadLit(L.SkyVp, L.SkyTerm, _capturing && !_planar);
                    Weather.BindEsm(L.CoverEsm, _oitOn);
                    Weather.UploadMap(L.WxMapXf, L.WxMapP, _capturing);
                    // The master PLT palette on its own unit; a no-op until a master palette exists.
                    Db.PltPalettes.Bind();
                    // CX-223: ...and the palette tint's side table, whose binding point is a shared one --
                    // a reader that trusted an earlier bind would read the particle index block.
                    Db.ContentTable.Bind();
                    if (content) ContentPrograms.BindExtras(wantProg);
                    litBase = L.Base; litCut = L.Cut;
                    litSsaoOn = L.SsaoOn; litAoDirect = L.AoDirect;
                    boundSsao = -1;
                    litDecal = L.Decal; boundDecal = -1;
                    litSolid = L.Solid; boundSolid = -1;
                }
                else
                {
                    Gl.UseProgram(Programs._prog46);
                    Gl.Uniform1i(Programs._locTex, 0);
                    float* white = stackalloc float[3] { 1f, 1f, 1f };
                    Gl.Uniform3fv(Programs._locTint, 1, white);    // ...and white, which is Mat()'s ordinary branch
                    Gl.Uniform1f(Programs._locSkyFog, 0f);
                    Gl.Uniform1f(Programs._locAlphaMip, Renderer.CutMip.V);
                    Gl.Uniform1i(Programs._locViz, viz);
                    fixed (Matrix4x4* vpm = &vp) Gl.UniformMatrix4fv(Programs._locVp, 1, 0, (float*)vpm);
                    UploadFeatureTerms(Programs._locFeat, viewport);
                }
            }
            int s = _gStart[g];
            Gl.Uniform1i(water ? Programs._watBase : lit ? litBase : Programs._locBase, s);
            // #126: the alpha cutout, per batch because it is per class -- the engine's own
            // fAlphaDiscardValue, which is a global there and a uniform here.
            if (lit)
            {
                int ssao = SsaoPass.Live && !_capturing ? AoWhy(g) : 0;
                if (ssao != boundSsao)
                {
                    boundSsao = ssao;
                    Gl.Uniform1i(litSsaoOn, ssao);
                    Gl.Uniform1f(litAoDirect, ssao > 0 ? SsaoPass.Direct : 0f);
                }
                // CX-273: fold the layer (the opaque pass), or drop a decal batch's fragments it already took
                int dm = decalMode == DecalLayer.ModeFold ? 1 : decalMode == DecalLayer.ModeSkip && _gDecal[g] ? 2 : 0;
                if (dm != boundDecal) { Gl.Uniform1i(litDecal, dm); boundDecal = dm; }
                int sm = interior ? 1 : 0;
                if (sm != boundSolid) { Gl.Uniform1i(litSolid, sm); boundSolid = sm; }
            }
            // the decal layer takes the whole graded edge: its 0.2 discard is what drew the edge as a band
            float cut = decalWrite ? -1f : AlloyClasses.Discard(_gCls[g]);
            if (cut != boundCut) { Gl.Uniform1f(water ? Programs._watCut : lit ? litCut : Programs._locCut, cut); boundCut = cut; }
            // ...and the blend state, per batch, because the opaque PASS now mixes both: a plain opaque batch
            // and a cutout batch both draw with GL_BLEND off (AlloyClasses.Blends); a blended batch takes the
            // material's own pair. Batches are class-grouped, so this toggles about once per pass.
            bool blend = AlloyClasses.Blends(_gCls[g]) && !interior;
            if (blend != boundBlend)
            {
                boundBlend = blend;
                if (blend) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
            }
            // Under OIT the pair is per draw buffer and set by the pass itself; glBlendFunc would clobber both.
            if (blend && !_oitOn && !decalWrite)
            {
                uint src = BlendGl(_gSrc[g]), dst = BlendGl(_gDst[g]);
                if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            }
            bool equal = _matchDepth && _gPrepassed[g];
            // never under EQUAL: a prepassed batch has to reproduce its prepass depth exactly
            bool wash = _gWash[g] != 0 && !equal;
            // A wash takes the pass's own depth rule: a cutout one writes its depth like any cutout, a blended
            // one like any blended draw.
            byte mask = equal ? (byte)0 : _passDepthMask;
            if (mask != boundMask) { Gl.DepthMask(mask); boundMask = mask; }
            // the edge pass: an interior fragment meets its own depth there, and LESS drops it before it is shaded
            uint func = equal ? Gl.EQUAL : split && _split.Ran && !interior ? Gl.LESS : Gl.LEQUAL;
            if (func != boundFunc) { Gl.DepthFunc(func); boundFunc = func; }
            bool test = !(blend && wash && !_washTest);
            if (test != boundTest) { if (test) Gl.Enable(Gl.DEPTH_TEST); else Gl.Disable(Gl.DEPTH_TEST); boundTest = test; }
            // A wash is a sheet lying ON another surface (a decal, a rug, a floor lattice at +0.005), and its
            // depth comes from a different program and mesh than the prepass wrote the floor's with: LEQUAL
            // alone z-fights. Pull it toward the eye by the same offset the highlight mask uses.
            if (wash != boundOffset)
            {
                if (wash && Gl.PolygonOffset != null) { Gl.PolygonOffset(WashOffsetFactor, WashOffsetUnits); Gl.Enable(Gl.POLYGON_OFFSET_FILL); }
                else Gl.Disable(Gl.POLYGON_OFFSET_FILL);
                boundOffset = wash;
            }
            BindBatchState(g, ref boundCull, ref boundVao);
            // #153: a merged lit batch spans many textures; its samplers come off the handle block per draw,
            // and binding here would be dead state. Unlit and water batches still bind.
            bool handles = _bindless && lit && !water;
            if (!handles && _gTex[g] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
            // #120: slots 1..5 on units 3..7. Unbound slots get texture 0 rather than being left holding a
            // previous batch's map -- the shader's slot mask says not to sample them, but a stale binding is
            // one override away from being sampled anyway.
            for (int t = 0; !handles && t < MapSlots; t++)
            {
                int want = _gTexN[g * MapSlots + t];
                if (want == boundSlot[t]) continue;
                boundSlot[t] = want;
                // #151: one DSA call instead of ActiveTexture+Bind+ActiveTexture -- this loop is a third of
                // the pass's GL traffic.
                Gl.BindTextureUnit(PbrUnit0 + (uint)t, (uint)want);
            }
            // #243: the same batch, from a list the GPU compacted. The bind set above is unchanged (the
            // cull moves WHICH draws are in the batch, never what state they draw under), and the record
            // lookup is unchanged too -- uDrawBase is still this batch's start, because the cull compacts
            // each batch into its own source region rather than into one global run.
            if (Ablation.DrawCap > 0 && drawn++ >= Ablation.DrawCap) break;
            if (BlendProfile.Recording) BlendProfile.Stamp(g, boundProg);
            uint prim = _gMode[g];
            if (indirect) CullPass.Draw(prim, g, s, _gLen[g]);
            else
                AlloyRenderer.MultiDrawRange(prim, s, _gLen[g]);
            Counters._batches++;
            Counters._draws += _gLen[g];
            if (!interior) Counters._drawsByClass[_gCls[g]] += _gLen[g];
            // CX-188: a twin draw went out, so the G-buffer holds pixels and the deferred pass has work
            if (deferred) GBuffer.Written = true;
        }
        if (BlendProfile.Recording) BlendProfile.Close();
        Submit.Back(wasStage);
        // Hand the per-batch toggling back to the pass level: the next pass reads this rather than assuming
        // what the last batch left bound.
        _blendOn = boundBlend;
        _heldProg = boundProg;
        if (boundMask != _passDepthMask) Gl.DepthMask(_passDepthMask);
        if (boundFunc != Gl.LEQUAL) Gl.DepthFunc(Gl.LEQUAL);
        if (!boundTest) Gl.Enable(Gl.DEPTH_TEST);
        if (boundOffset) Gl.Disable(Gl.POLYGON_OFFSET_FILL);
    }

    /// <summary>The uniform locations of one of the lit programs, keyed by the issue loop's <c>wantProg</c>: 1 plain,
    /// 4 the OIT-lists twin, 5 the deferred twin, 6 the A2C mask variant, 8 the decal layer's writer,
    /// <see cref="ContentPrograms.ProgBase"/> and up a content pair's. One place, because two sites in the loop pick
    /// from them.</summary>
    private static ref LitLoc LitLocOf(int which)
    {
        if (ContentPrograms.IsProg(which)) return ref ContentPrograms.Loc(which);
        return ref (which == 4 ? ref Programs._litL
                   : ref (which == 5 ? ref Programs._litD
                        : ref (which == 6 ? ref Programs._litM
                             : ref (which == 8 ? ref Programs.DecalLoc : ref Programs._lit))));
    }

    /// <summary>The blended class's lit fragments may take the deferred pass's light record (set by the frame
    /// around the blended pass only, with the record bound on <see cref="TextureSlots.LightRec"/>).</summary>
    internal static bool _lightRec;

    /// <summary>Arm <see cref="_lightRec"/> for the blended pass when this frame's deferred pass wrote the record;
    /// the held program is dropped so the next lit bind uploads the gate.</summary>
    internal static void LightRecBegin()
    {
        _lightRec = GBuffer.LightLive && !_capturing && Lighting.LightRec.V;
        if (!_lightRec) return;
        Gl.BindTextureUnit(TextureSlots.LightRec, GBuffer.TexLight);
        _holdProg = false;
    }

    /// <summary>Disarm it: every lit bind after the blended pass uploads the gate off again.</summary>
    internal static void LightRecEnd()
    {
        if (!_lightRec) return;
        _lightRec = false;
        _holdProg = false;
    }

    /// <summary>The wash split's latches: the interior draw is being issued, and it was issued this frame, so the
    /// blended pass draws those washes' edges alone.</summary>
    private sealed class WashSplitState { public bool Interior, Ran; }
    private static readonly WashSplitState _split = new();

    /// <summary>The split washes' solid interiors in [<paramref name="from"/>, <paramref name="to"/>), drawn in the
    /// cutout pass's state through the deferred twin: only fragments at full coverage draw (<c>uSolid</c>).</summary>
    internal static void WashInteriors(int from, int to, Matrix4x4* vp, int* viewport, float near, float far)
    {
        _split.Interior = true;
        try { IssueColour(from, to, vp, viewport, near, far); }
        finally { _split.Interior = false; }
        _split.Ran = true;
    }

    /// <summary>The blended pass is done with this frame's interiors.</summary>
    internal static void WashesDone() => _split.Ran = false;

    /// <summary>Polygon offset for wash batches (see the wash note in the issue loop).</summary>
    internal const float WashOffsetFactor = -1f, WashOffsetUnits = -2f;

    /// <summary><c>/alloy washtest 0</c>: blended wash batches draw with the depth TEST off too. A/B instrument, not a look.</summary>
    private static bool _washTest => Renderer.WashTest.V;

    /// <summary>The depth mask the current pass set before <see cref="IssueColour"/>; a wash batch drops it
    /// to 0 for its own draws and the loop restores it. 1 everywhere but the transparent passes.</summary>
    private static byte _passDepthMask = 1;
    /// <summary>The opaque and cutout passes only: a batch the prepass laid depth for draws at <c>GL_EQUAL</c>
    /// with the mask off. Its keyhole and alpha discards already shaped that depth, so every hidden or holed
    /// fragment fails the test before shading, and with no depth write the driver keeps early-Z despite the
    /// stage's <c>discard</c>. Callers set it around the pass and must restore false.</summary>
    internal static bool _matchDepth;
    /// <summary>The blended range is being drawn into OitPass's accumulation pair: programs write the pair,
    /// and the per-batch blend pair stays off the state.</summary>
    internal static bool _oitOn;
    /// <summary>The post-resolve sweep for the batches OIT skipped: content pairs and the other blend pairs.</summary>
    internal static bool _onlyHosted;
    /// <summary>Batches the accumulation pass stepped over, i.e. exactly what the <see cref="_onlyHosted"/>
    /// sweep has to draw. Zero means that sweep has no work and the whole walk of the blended range can be
    /// skipped; written by the accumulation pass, read straight after it.</summary>
    internal static int OitDeferred => _oitDeferred;
    private static int _oitDeferred;
    /// <summary>Aurora blend-mode indices (see <see cref="BlendGl"/>) of the one pair OIT accumulates.</summary>
    private const byte AuroraSrcAlpha = 0, AuroraOneMinusSrcAlpha = 1;

    /// <summary>Does a lit, non-water batch draw a content pair's program -- built, and so its halves running?</summary>
    internal static bool HostedBody(bool lit, int feat) => lit && ShaderFeatures.IsContent(feat) && ContentPrograms.AskReady(feat);

    /// <summary>Does a blended batch sit the OIT accumulation out and draw after the resolve, the plain-blend way? A
    /// content pair's program does -- its stage, not Alloy, decides the alpha it blends at -- and weighted blending
    /// represents only the SrcAlpha / OneMinusSrcAlpha pair: a modulating veil would paint its colour.</summary>
    internal static bool SitsOitOut(bool content, int src, int dst)
        => content || src != AuroraSrcAlpha || dst != AuroraOneMinusSrcAlpha;

    /// <summary>The blending classes (ADR 0011): blended draws sorted back-to-front, then additive unsorted,
    /// because addition commutes. Depth TESTING stays on against the opaque set's depth, and the per-batch
    /// <c>glBlendFunc</c> comes off the material in <see cref="IssueBatches"/>. <paramref name="depthWrite"/>
    /// is the engine's rule (<c>Material::SetBlendingMode</c> @0x4c5c50): on for every pair but additive.</summary>
    internal static void TransparentPass(int from, int to, Matrix4x4* vp, int* viewport, float near, float far,
                                        bool depthWrite = false)
    {
        if (to <= from) return;
        if (_oitOn && !_onlyHosted) _oitDeferred = 0;
        _passDepthMask = depthWrite ? (byte)1 : (byte)0;
        Gl.DepthMask(_passDepthMask);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        // The same compacted list the opaque pass drew from, whose batches in THIS range were placed by the
        // sort dispatch -- back-to-front from this pass's eye, measured on the GPU.
        IssueColour(from, to, vp, viewport, near, far);
        _passDepthMask = 1;
    }

    /// <summary>ADR 0011 <see cref="PassId.WaterOwned"/>: the water class, drawn from the one framebuffer copy
    /// taken immediately before it. Depth writes go back ON -- the one piece of state separating this pass
    /// from the blended one, and what lets additive draw after water and stand in FRONT of it, as vanilla
    /// arranges it. Stands down whole when the copy was refused or the program did not build.</summary>
    internal static void WaterOwnedPass(int from, int to, Matrix4x4* vp, in Matrix4x4 view, in Matrix4x4 proj,
                                       int* viewport, float near, float far)
    {
        if (to <= from || !FrameSnapshot.Ready || !_waterArmed) return;
        _waterView = view; _waterProj = proj; _waterNear = near; _waterFar = far;
        Gl.DepthMask(1);
        FrameSnapshot.Bind();
        IssueColour(from, to, vp, viewport, near, far);
        FrameSnapshot.Unbind();
        // Leave the state the additive pass expects to find: it enables blending itself, but the depth mask
        // is pass-level state and additive must not write depth.
        Gl.DepthMask(0);
    }

    /// <summary>One <c>Aurora::AuroraBlendModes</c> value as its GL blend factor -- the engine's own
    /// translation table at 0x12f5040, which <c>GLRender::SetBlendFunc</c> @0x4cb0c0 indexes. Out-of-range
    /// values fall back to the pair the engine's own default is, rather than to whatever GL enum 0 means.</summary>
    internal static uint BlendGl(int aurora) => aurora switch
    {
        0 => GlSrcAlpha,
        1 => GlOneMinusSrcAlpha,
        2 => GlOne,
        3 => GlZero,
        4 => GlDstColor,
        5 => GlSrcColor,
        // Entry 6 is 0x0305, ONE_MINUS_DST_ALPHA -- read out of blendTranslationTable @0x12f5040 rather than
        // inferred from the name beside it.
        6 => GlOneMinusDstAlpha,
        _ => GlSrcAlpha,
    };
}
