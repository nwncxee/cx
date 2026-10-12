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
    private static void UploadRuns(uint buf, void* data, int recBytes)
    {
        for (int d = 0; d < _recDirtyN;)
        {
            d = NextRun(_recDirty, _recDirtyN, d, out int first, out int last);
            long off = (long)first * recBytes;
            Gl.NamedBufferSubData(buf, (IntPtr)off,
                             (IntPtr)((long)(last - first + 1) * recBytes), (byte*)data + off);
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

    /// <summary>A wrong run uploads a stale record over a live slot, silently, so this joins the disarm
    /// chain: duplicates collapse, adjacent slots merge, a gap splits.</summary>
    internal static bool RunsSelfCheck(ExtenderLog log)
    {
        int[] dirty = { 5, 3, 4, 9, 3, 11, 10 };
        Array.Sort(dirty);
        int d = NextRun(dirty, dirty.Length, 0, out int f0, out int l0);
        d = NextRun(dirty, dirty.Length, d, out int f1, out int l1);
        bool ok = f0 == 3 && l0 == 5 && f1 == 9 && l1 == 11 && d == dirty.Length;
        if (!ok) log.Info("AlloyRenderer.RunsSelfCheck: FAILED -- the dirty-slot runs do not merge as sorted");
        return ok;
    }

    /// <summary>Push the records that MOVED into the persistent per-draw SSBO, then stream the draw-to-slot
    /// index array this pass's order needs. Past a quarter of the table a wholesale upload is cheaper, as a
    /// reallocation or a dirty-list overflow also are. A persistent buffer cannot orphan, so a SubData over a
    /// buffer the GPU may still be reading can sync. False = the slot array did not stream, frame sits out.</summary>
    internal static bool UploadDraws()
    {
        int cap = _recOwnCap + MaxSkyDraws;
        bool grew = false;
        if (_drawBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _drawBuf = b; }
        if (cap > _recCapGpu)
        {
            Gl.NamedBufferData(_drawBuf,
                          (IntPtr)((long)cap * DrawFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
            grew = true;
        }
        // Against what this frame DRAWS, not against the high-water capacity: _recOwnCap latches at the
        // largest area of the session and never shrinks, so thresholding on it would leave a small interior
        // issuing thousands of scattered SubData calls into a buffer that can no longer orphan -- one
        // enrollment's worth of hitch, once a cadence, in exactly the "just left the city" case.
        bool whole = grew || _recDirtyAll || _recDirtyN > _drawN / 4;
        if (whole) { WholeUploads++; WholeBytes += (long)cap * DrawFloats * sizeof(float); }
        if (!whole) Array.Sort(_recDirty, 0, _recDirtyN);
        fixed (float* p = _dData)
        {
            if (whole)
                Gl.NamedBufferSubData(_drawBuf, IntPtr.Zero,
                                      (IntPtr)((long)cap * DrawFloats * sizeof(float)), p);
            else UploadRuns(_drawBuf, p, DrawFloats * sizeof(float));
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, _drawBuf);
        StaleMirrorRecords(cap, whole, _recDirty, _recDirtyN);
        if (_bindless) UploadHandles(cap, whole);
        if (grew) _recCapGpu = cap;
        _recDirtyN = 0; _recDirtyAll = false;
        // ...and the one array that IS per draw and per pass, rewritten whole every pass: the ring's shape.
        fixed (int* p = _dSlot)
            return StreamRing.Stream(StreamRing.Site.DrawSlots, BindDrawIdx, p, (long)_drawN * sizeof(int), out _slotRange);
    }

    /// <summary>This period's whole-table record uploads and their bytes: every one is a SubData over the whole
    /// capacity into a buffer the GPU may still be reading.</summary>
    internal static long WholeUploads, WholeBytes;

    /// <summary>The staged draw-to-slot array's ring slice, for <see cref="BindStagedSlots"/>.</summary>
    internal static StreamRing.Range _slotRange;

    /// <summary>Context loss: forget the ring slice and the handle table's capacity, both of which describe
    /// buffers that died with it, so the next upload sizes the new buffer before writing into it.</summary>
    internal static void ContextLost() { _slotRange = default; _texHCapGpu = 0; }

    /// <summary>The handle table alone. Its capacity is tracked apart from the draw records' so the bake can
    /// bring it up to date on a frame the colour bracket has not reached (or refused).</summary>
    /// <summary>The handle table as the GPU sees it: <see cref="AlloyRenderer._dTexH"/> with every zero
    /// replaced by a stand-in's handle. ponytail: rewritten whole per upload, run-wise if it ever shows up.</summary>
    private static readonly ulong[] _gpuTexH = new ulong[MaxRecords * 8];

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
        long n = (long)cap * 8;
        for (long k = 0; k < n; k++)
        {
            ulong v = _dTexH[k];
            _gpuTexH[k] = v != 0 ? v : (k & 7) == 0 ? whiteH : nullH;
        }
        fixed (ulong* p = _gpuTexH)
        {
            if (whole)
                Gl.NamedBufferSubData(_texHBuf, IntPtr.Zero, (IntPtr)(n * sizeof(ulong)), p);
            else
                UploadRuns(_texHBuf, p, 8 * sizeof(ulong));
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindTexH, _texHBuf);
        StaleMirrorHandles(cap, whole, _recDirty, _recDirtyN);
        _texHDirty = false;
    }
    private static int _texHCapGpu;

    /// <summary>Called by the caster bake, inside its bracket, before its first view: the standing staging
    /// samples the GPU handle table by record slot, so a handle dropped since the last colour upload must
    /// not still be there. Leaves the dirty list for the colour bracket, which uploads the records too.</summary>
    internal static void UploadHandlesForBake()
    {
        if (!_bindless || !_texHDirty) return;
        int cap = _recOwnCap + MaxSkyDraws;
        bool whole = _recDirtyAll || _recDirtyN > _drawN / 4 || cap > _texHCapGpu;
        if (!whole) Array.Sort(_recDirty, 0, Math.Min(_recDirtyN, _recDirty.Length));
        UploadHandles(cap, whole);
        BakeHandleUploads++;
    }

    /// <summary>Put the CPU-staged per-draw index array back on <see cref="BindDrawIdx"/>. The cull binds its
    /// own compacted one for the passes it drives; every other pass reads the staged order.</summary>
    internal static bool BindStagedSlots() => StreamRing.Rebind(StreamRing.Site.DrawSlots, BindDrawIdx, in _slotRange);

    /// <summary>Push this pass's bone snapshots into the bone block. Nothing to upload is the common case (no
    /// skinmesh on screen), and the block stays bound EMPTY rather than unbound -- a stage that declares it
    /// must find something there even on a frame where no draw reads it. Not on <see cref="StreamRing"/>: the
    /// next frame's caster bake reads it when the shadow bracket did not pose.</summary>
    internal static void UploadBones()
    {
        if (_skinning) UploadBoneBlock(ref _boneBuf, _boneStage, BindBones);
    }

    /// <summary>One bone block, <c>_boneSlots</c> palettes from <paramref name="stage"/>, onto
    /// <paramref name="binding"/>.</summary>
    internal static void UploadBoneBlock(ref uint buf, float* stage, uint binding)
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
        IssueBatches(from, to, (float*)vp, viewport, near, far, indirect);
        if (indirect) { CullPass.UnbindForDraw(); BindStagedSlots(); }
    }

    internal static void IssueBatches(int from, int groups, float* vpRaw, int* viewport, float near, float far,
                                     bool indirect = false)
    {
        long tIssue = FrameZones.Now;
        try { IssueBatchesInner(from, groups, vpRaw, viewport, near, far, indirect); }
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
    private static int AoWhy(int g)
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
    internal static void IssueBatchesInner(int from, int groups, float* vpRaw, int* viewport, float near, float far,
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
        bool boundEqual = false;
        bool boundTest = true;
        bool boundOffset = false;
        int boundOitGob = -1;
        // #292: face culling, per batch. -1 rather than a guess at the preamble's state: the sky, grass,
        // particle and beam passes all leave their own culling behind them, and a probe capture re-enters
        // this loop six more times, so the first batch of every entry issues the call rather than trusting
        // what someone else set.
        int boundCull = -1;
        float boundCut = float.NaN;   // per PROGRAM: reset on every UseProgram below
        int litBase = -1, litCut = -1, litOitGob = -1; // the bound lit program's, whichever of the two it is
        int litSsaoOn = -1, litAoDirect = -1;
        if (boundProg is 1 or 3 or 4 or 5 or 6)
        {
            ref LitLoc H = ref LitLocOf(boundProg);
            litBase = H.Base; litCut = H.Cut; litOitGob = H.OitGob;
            litSsaoOn = H.SsaoOn; litAoDirect = H.AoDirect;
        }
        // #379 follow-up: SSAO is per BATCH, not per program bind. The visibility buffer is computed from
        // the DEPTH PREPASS, and a batch the prepass skipped (cutout, an out-of-prepass feature, the whole
        // blended range) has no depth of its own in it -- its fragments would read the occlusion of whatever
        // stands BEHIND them. Live: the far corridor's occlusion showing through a closed door.
        int boundSsao = -1;
        int* boundSlot = stackalloc int[MapSlots];
        int drawn = 0;
        for (int s = 0; s < MapSlots; s++) boundSlot[s] = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = from; g < groups; g++)
        {
            if (_onlyGobSet && _gGob[g] != _onlyGob) continue;
            bool water = _gCls[g] == (byte)AlloyClass.Water;
            bool lit = _gLit[g];
            // #350: a hosted feature draws through the second lit program (3); everything else lit through
            // the first (1). Batches are cut on the feature id, so this is one switch per hosted batch.
            bool hosted = lit && !water && Programs._progLitHost != 0 && ShaderFeatures.Hosted(_gFeat[g]);
            // A hosted content program declares no revealage output: under OIT it would leave that buffer
            // undefined (NaN, which the resolve and the bloom then spread as white patches and blue blotches).
            // Those batches sit the accumulation out and draw after the resolve the plain-blend way.
            // ...and so does any pair that is not SrcAlpha/OneMinusSrcAlpha: weighted blending can represent
            // only that one. A content veil (a modulating fog dome around the player) fed through it composites
            // the veil's colour over the whole view instead of darkening by it.
            bool oitOut = hosted || _gSrc[g] != AuroraSrcAlpha || _gDst[g] != AuroraOneMinusSrcAlpha;
            if (_oitOn && oitOut) { _oitDeferred++; continue; }
            if (_onlyHosted && !oitOut) continue;
            bool lists = lit && !water && !hosted && _oitOn && OitPass.Linked;   // the append twin, bracket only
            // CX-188 (ADR 0029): the DEFERRED twin (5) takes the opaque and cutout lit batches of the main
            // view -- the only draws whose pixels the deferred compute pass reads. Everything else keeps the
            // forward loop: a capture or the mirror (no G-buffer to read back), any /alloy viz but the
            // light-source one (so every debug view stays truthful), a hosted body, and the blended range through the OIT twin.
            bool deferred = lit && !water && !hosted && !lists && (viz == 0 || viz == AlloyRenderer.VizLightFrom) && !_capturing
                            && g < _gOpaqueEnd && DeferredLights.Active && !_forwardOnly;
            // CX-189: the coverage layer draws forward, through the mask variant when it shapes its own
            // samples. A hosted batch keeps its own program (3) and so has no mask variant -- the same
            // CoverageLayer.FromDiffuse seam the caster reads, and where CX-223 plugs the hosted source in.
            int wantProg = water ? 2 : hosted ? 3 : lists ? 4 : deferred ? 5
                         : lit ? (_coverPass && CoverageLayer.Masked ? 6 : 1) : 0;
            if (boundProg != wantProg)
            {
                boundProg = wantProg;
                boundCut = float.NaN;
                // CX-188: only the twin declares outputs on draw buffers 4..7, and a draw buffer no output
                // reaches is UNDEFINED rather than untouched -- so every other program in these passes draws
                // with those four masked off and leaves whatever record is already there.
                if (GBuffer.Armed) GBuffer.MaskColour(wantProg == 5);
                // CX-51: the probe's alpha is 1 / distance, and only the lit stage writes one; the rest keep the clear's "far"
                if (_capturing && !_planar) Gl.ColorMask(1, 1, 1, (byte)(wantProg == 1 ? 1 : 0));
                if (water)
                {
                    Gl.UseProgram(Programs._progWater);
                    Gl.UniformMatrix4fv(Programs._watVp, 1, 0, vpRaw);
                    Gl.UniformMatrix4fv(Programs._watView, 1, 0, _waterView);
                    Gl.UniformMatrix4fv(Programs._watProj, 1, 0, _waterProj);
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
                    Gl.UseProgram(hosted ? Programs._progLitHost : lists ? Programs._progLitList
                                  : deferred ? Programs._progLitDef : wantProg == 6 ? Programs._progLitMask : Programs._progLit46);
                    if (wantProg == 6) Gl.Uniform1i(Programs._litMaskSamples, CoverageLayer.Samples);
                    Gl.Uniform1i(L.Tex, 0);
                    Gl.UniformMatrix4fv(L.Vp, 1, 0, vpRaw);
                    AlloyLights.BindForDraw();
                    Gl.Uniform1i(L.Viz, viz);
                    Gl.Uniform1f(L.AoCutout, Ao.Cutout.V);
                    Gl.Uniform1f(L.AlphaMip, Renderer.CutMip.V);
                    OitPass.Upload(L.Oit, L.OitDim, _oitOn);
                    Gl.Uniform1i(L.LightRecOn, _lightRec ? 1 : 0);
                    Gl.Uniform1i(L.Capture, _capturing ? (_planar ? 2 : 1) : 0);
                    Gl.Uniform1f(L.MirrorZ, _mirrorZ);
                    LightmapBake.BindForDraw(L.LmPages, L.LmMovers);
                    UploadFeatureTerms(L.Feat, viewport);
                    BindPass(L.CubeCount, viewport, near, far);
                    BindProbe();
                    // not inside the OIT bracket: the scene depth is that framebuffer's depth attachment there, and
                    // no blended batch reads SSAO (AoBlended) -- a bound, attached depth is a driver decompress
                    SsaoPass.BindForDraw(SsaoPass.Live && !_capturing && !_oitOn, PostChain.DepthTex);
                    Gl.Uniform1i(L.SsaoHalf, SsaoPass.Half ? 1 : 0);
                    Gl.Uniform2f(L.LightOcc, Lighting.HiddenLights.V ? 1f : 0f, GlossyRough);
                    UploadMaterialModel(L.Material);
                    Weather.UploadLit(L.Weather, L.WeatherLook, L.CoverVp, L.CoverLayer, L.SnowLook, L.SnowLight, L.SnowPom, L.WxEdge, L.WxRun, L.WxCache, _capturing);
                    Weather.BindEsm(L.CoverEsm, _oitOn || lists);
                    Weather.UploadMap(L.WxMapXf, L.WxMapP, _capturing);
                    // The master PLT palette on its own unit; a no-op until a master palette exists.
                    Db.PltPalettes.Bind();
                    litBase = L.Base; litCut = L.Cut; litOitGob = L.OitGob; boundOitGob = -1;
                    litSsaoOn = L.SsaoOn; litAoDirect = L.AoDirect;
                    boundSsao = -1;
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
                    OitPass.Upload(Programs._locOit, Programs._locOitDim, _oitOn); boundOitGob = -1;
                    Gl.UniformMatrix4fv(Programs._locVp, 1, 0, vpRaw);
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
            }
            float cut = _coverPass ? CoverageLayer.Cut : AlloyClasses.Discard(_gCls[g]);
            if (cut != boundCut) { Gl.Uniform1f(water ? Programs._watCut : lit ? litCut : Programs._locCut, cut); boundCut = cut; }
            // ...and the blend state, per batch, because the opaque PASS now mixes both: a plain opaque batch
            // draws with GL_BLEND off and a cutout batch with it on and the material's own pair, which is
            // exactly the pair of states the engine draws buckets 0 and 1 in. Batches are class-grouped, so
            // this toggles about once per pass.
            bool blend = AlloyClasses.Blends(_gCls[g]);
            if (blend != boundBlend)
            {
                boundBlend = blend;
                if (blend) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
            }
            // Under OIT the pair is per draw buffer and set once by OitPass.Begin; glBlendFunc would clobber both.
            if (blend && !_oitOn)
            {
                uint src = BlendGl(_gSrc[g]), dst = BlendGl(_gDst[g]);
                if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            }
            bool wash = blend && _gWash[g] != 0;
            bool equal = _matchDepth && _gPrepassed[g];
            // A wash takes the pass's own depth rule, like any other blended draw: it is a cutout Alloy chose
            // to BLEND for its fringe, and vanilla writes that part's depth in bucket 1. Holding the mask off
            // here left it with no depth anywhere -- the blended class is never prepassed -- and water and
            // particles painted over the creature heads the reroute catches.
            byte mask = equal ? (byte)0 : _passDepthMask;
            if (mask != boundMask) { Gl.DepthMask(mask); boundMask = mask; }
            if (equal != boundEqual) { Gl.DepthFunc(equal ? Gl.EQUAL : Gl.LEQUAL); boundEqual = equal; }
            bool test = !(wash && !_washTest);
            if (test != boundTest) { if (test) Gl.Enable(Gl.DEPTH_TEST); else Gl.Disable(Gl.DEPTH_TEST); boundTest = test; }
            // A wash is routinely a decal lying ON an opaque surface (a floor lattice at +0.005), and its
            // depth comes from a different program than the prepass wrote the floor's with: LEQUAL alone
            // z-fights at range. Pull it toward the eye by the same offset the highlight mask uses.
            if (wash != boundOffset)
            {
                if (wash && Gl.PolygonOffset != null) { Gl.PolygonOffset(WashOffsetFactor, WashOffsetUnits); Gl.Enable(Gl.POLYGON_OFFSET_FILL); }
                else Gl.Disable(Gl.POLYGON_OFFSET_FILL);
                boundOffset = wash;
            }
            if (_oitOn)
            {
                int gid = OitPass.GobId(_gGob[g]);
                if (gid != boundOitGob) { Gl.Uniform1i(lit ? litOitGob : Programs._locOitGob, gid); boundOitGob = gid; }
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
                Gl.MultiDrawElementsBaseVertex(prim, counts + s, GlUnsignedShort,
                                               (void**)(offs + s), _gLen[g], bases + s);
            Counters._batches++;
            Counters._draws += _gLen[g];
            Counters._drawsByClass[_gCls[g]] += _gLen[g];
            // CX-188: a twin draw went out, so the G-buffer holds pixels and the deferred pass has work
            if (deferred) GBuffer.Written = true;
        }
        if (BlendProfile.Recording) BlendProfile.Close();
        // Hand the per-batch toggling back to the pass level: the next pass reads this rather than assuming
        // what the last batch left bound.
        _blendOn = boundBlend;
        _heldProg = boundProg;
        if (boundMask != _passDepthMask) Gl.DepthMask(_passDepthMask);
        if (boundEqual) Gl.DepthFunc(Gl.LEQUAL);
        if (!boundTest) Gl.Enable(Gl.DEPTH_TEST);
        if (boundOffset) Gl.Disable(Gl.POLYGON_OFFSET_FILL);
    }

    /// <summary>The uniform locations of one of the five lit programs, keyed by the issue loop's
    /// <c>wantProg</c>: 1 plain, 3 hosted, 4 the OIT-lists twin, 5 the deferred twin, 6 the A2C mask variant.
    /// One place, because two sites in the loop pick from the same five.</summary>
    private static ref LitLoc LitLocOf(int which)
        => ref (which == 3 ? ref Programs._litH
              : ref (which == 4 ? ref Programs._litL
                   : ref (which == 5 ? ref Programs._litD
                        : ref (which == 6 ? ref Programs._litM : ref Programs._lit))));

    /// <summary>The batches being issued are <see cref="CoverageLayer"/>'s, cut at the layer's threshold rather
    /// than the class's (and through the mask variant when it is on). Set around the layer's one issue call.</summary>
    internal static bool _coverPass;
    /// <summary>The batches draw after the deferred pass (the coverage tail, layered or not), so never through
    /// the deferred twin -- its point lights would go to a G-buffer nobody reads any more.</summary>
    internal static bool _forwardOnly;

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

    /// <summary>Polygon offset for wash batches (see the wash note in the issue loop).</summary>
    private const float WashOffsetFactor = -1f, WashOffsetUnits = -2f;

    /// <summary><c>/alloy washtest 0</c>: wash batches draw with the depth TEST off too. A/B instrument, not a look.</summary>
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
    /// <summary>The post-resolve sweep for the batches OIT skipped: hosted programs only.</summary>
    internal static bool _onlyHosted;
    /// <summary>Batches the accumulation pass stepped over, i.e. exactly what the <see cref="_onlyHosted"/>
    /// sweep has to draw. Zero means that sweep has no work and the whole walk of the blended range can be
    /// skipped; written by the accumulation pass, read straight after it.</summary>
    internal static int OitDeferred => _oitDeferred;
    private static int _oitDeferred;
    /// <summary>Aurora blend-mode indices (see <see cref="BlendGl"/>) of the one pair OIT accumulates.</summary>
    private const byte AuroraSrcAlpha = 0, AuroraOneMinusSrcAlpha = 1;

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
    internal static void WaterOwnedPass(int from, int to, Matrix4x4* vp, float* view, float* proj,
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
