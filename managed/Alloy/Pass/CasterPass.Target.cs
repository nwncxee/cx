using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The depth targets and their programs: creation and resize of the sun, cube and layered arrays, the
/// layered queue's flush, and the teardown every context loss runs.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Every name the 4.6 context owns, in one object: <see cref="ContextLost"/> replaces it, so no
    /// program, target or location from a dead context survives. The disarm latches are not here: a disarm
    /// is for the session.</summary>
    private sealed class CasterGlState
    {
        public uint Prog, Fbo, DepthTex;
        public int LocVp = -1;
        public uint ProgLayer, ProgLayerMask;
        public int LocLayerMaskTex = -1, LocLayerMaskCut = -1, LocLayerMaskDither = -1, LocLayerMaskEnv = -1;
        public bool LayerProgFailed;
        /// <summary>The masked (alpha-tested) depth program and its uniforms. Zero = it could not build, and
        /// masked casters then stay out of every bake.</summary>
        public uint ProgMask;
        public int LocMaskVp = -1, LocMaskTex = -1, LocMaskCut = -1, LocMaskDither = -1, LocMaskEnv = -1;
        /// <summary><c>uAlphaMip</c> on the four masked caster programs (<c>alloy.cutmip</c>).</summary>
        public int LocMaskMip = -1, LocMaskHMip = -1, LocLayerMaskMip = -1, LocLayerMaskHMip = -1;
        /// <summary>The masked depth program that samples the mask through the record's bindless handle
        /// (<c>uTexH</c> slot 0) instead of a bound unit, so masked batches do not split per texture. Built only
        /// once the renderer's bindless path is armed; <c>/shadow maskh 0</c> is the A/B.</summary>
        public uint ProgMaskH, ProgLayerMaskH;
        public int LocMaskHVp = -1, LocMaskHCut = -1, LocLayerMaskHCut = -1, LocMaskHDither = -1, LocLayerMaskHDither = -1, LocMaskHEnv = -1, LocLayerMaskHEnv = -1;
        public bool MaskHFailed;
        /// <summary>The published depth arrays (0 = none yet): sun cascades, sun movers, static cubes, mover pool.</summary>
        public uint CascadeTex, SunDynTex, CubeTex, CubeDynTex;
        /// <summary>The overhead sky view's own one-layer depth target, compare mode off.</summary>
        public uint SkyDepth;
        /// <summary>The target textures <see cref="BeginTarget"/> has verified complete, ring-replaced when
        /// full: the sun array, the static cubes and the dyn pool interleave per view, so one latch would
        /// re-check nearly every attach.</summary>
        public readonly uint[] FboCheckedRing = new uint[4];
        public int FboCheckedAt;
        /// <summary>GPU time of the whole shadow bracket; the CPU stopwatch only sees submission.</summary>
        public GpuQuery QShadow;
        public bool QShadowOn;
    }

    private static CasterGlState _gl = new();
    private static bool _glFailed, _progFailed;

    // --- #307: the layered bake. With #302/#303 a view is a dispatch and a draw; this collapses the loop:
    // every stale view is QUEUED instead of submitted, and one flush per target texture runs one layered
    // dispatch (all views at once, commands tagged with their view) and one indirect-count draw per batch
    // into a layered framebuffer -- the vertex stage routes by gl_Layer out of the view table. The per-view
    // FBO/clear loop stays (a clear must touch only the stale layers), but the per-view uniform upload, the
    // per-view dispatch and the views x batches MDI storm all go. ARB_shader_viewport_layer_array is NOT
    // core in 4.6, so it is probed at runtime and the per-view path is kept as the fallback -- and as the
    // A/B (/shadow layered 0), which must produce identical shadows.

    /// <summary><c>/shadow layered 1|0</c>: the A/B. Off = one dispatch + one MDI set per view, as before.</summary>
    private static bool _layerDial => Shadows.Layered.V;
    /// <summary>0 = not yet probed, 1 = the extension is present, -1 = absent (said once; per-view path).</summary>
    private static int _layerProbe;
    /// <summary>The frame's queued stale views. 1024 covers the ceiling population (4 + 4 cascades and
    /// 64 x 6 x 2 cube faces); an overflow renders inline through the per-view path rather than dropping.</summary>
    private const int MaxBakeViews = 1024;
    /// <summary>Commands the layered buffer may hold per rotation slot (x20 B). Past it the flush chunks its
    /// views -- only a blanket-invalidate frame in a huge area ever needs more than one chunk.</summary>
    private const int MaxLayerCmds = 512 * 1024;
    private static readonly uint[] _qTex = new uint[MaxBakeViews];
    private static readonly int[] _qSize = new int[MaxBakeViews], _qLayer = new int[MaxBakeViews],
                                  _qSet = new int[MaxBakeViews];
    private static readonly bool[] _qPoint = new bool[MaxBakeViews];
    /// <summary>The queued view's target as a key both flushes read: a GL name means nothing to Vulkan, and the
    /// per-target flush groups on this. Resolved once, where the view is queued.</summary>
    private static readonly CasterMap[] _qMap = new CasterMap[MaxBakeViews];
    /// <summary>This queued view is re-baked under THIS frame's camera, so the receiver-frustum cull
    /// may apply. Never set on a held (static-set) view: it would be culled against a frustum the camera
    /// has since left.</summary>
    private static readonly bool[] _qRx = new bool[MaxBakeViews];
    private static readonly Matrix4x4[] _qVp = new Matrix4x4[MaxBakeViews];
    private static readonly Vector3[] _qFocus = new Vector3[MaxBakeViews], _qDir = new Vector3[MaxBakeViews];
    private static readonly float[] _qReach = new float[MaxBakeViews], _qAlong = new float[MaxBakeViews];
    /// <summary>The queued view's two detail thresholds, resolved from its texel size where every other
    /// per-view term is -- so the layered and per-view paths pick the same level for the same caster.</summary>
    private static readonly float[] _qLodR1 = new float[MaxBakeViews], _qLodR2 = new float[MaxBakeViews];
    private static int _qN;
    /// <summary>One chunk's staged view table.</summary>
    private static readonly CasterViewRec[] _viewStage = new CasterViewRec[MaxBakeViews];
    private static long _layerFlushFails;

    /// <summary>Is the layered path taking this frame's views? On Vulkan it is the only path: the device carries
    /// <c>shaderOutputLayer</c> or no Vulkan backend exists at all.</summary>
    private static bool LayeredActive
        => Renderer.OnVulkan ? VkArmed : _layerDial && _layerProbe > 0 && _gl.ProgLayer != 0;

    /// <summary>Has the frame's staging a non-empty source the views can select out of, on whichever backend
    /// bakes them? False bakes a clear, which for an empty world IS the bake.</summary>
    private static bool GatherActive => Renderer.OnVulkan ? CasterGatherVk.Active : CasterGather.Active;
    private static readonly StandDown _down = new("CasterPass DISARMED");

    /// <summary>Context loss: every GL name goes at once. Run on the loss edge, not each frame after it.</summary>
    private static void ContextLost() => _gl = new CasterGlState();

    private static void Release(string why)
    {
        // The VAO cache keys on VDO pointers that may now be freed; the GL objects can only be deleted inside
        // the bracket, so the rebuild happens there (EnsureVao). Retiring the slots is also what makes this
        // IDEMPOTENT: a lost 4.6 context disables its service permanently, so Lifecycle calls Release every
        // frame thereafter, and a guard on the caster count alone would re-log forever.
        // The texture-alpha verdicts go with it. They are keyed on GL texture IDS, and GL recycles the
        // names of deleted objects -- the same trap the VAO cache re-checks every pass for. A recycled id
        // inheriting the previous texture's verdict is a wasted fetch in one direction and, in the other, a
        // foliage texture classified as having no alpha: the solid slab back again, intermittently and only
        // after an area transition. That is not a bug anyone would trace.
        Array.Clear(_texKey); Array.Clear(_texHoles); Array.Clear(_texGraded);
        _texQueried = 0; _texWithAlpha = 0;
        // #241/#302: the recorded faces describe a world that just went away, and the previous-frame slot
        // list names ownership slots the next enrollment is free to hand to different parts. Both have to go,
        // or the first frame in the new area finds every view fresh -- shadows frozen from the old module.
        Array.Clear(_cubeBaked); Array.Clear(_cubeHadMover); Array.Clear(_cubeKeyId);
        Array.Clear(_sunBaked); Array.Clear(_sunHadMover); Array.Clear(_sunFitValid);
        CoverRelease();
        SkyRelease();
        // #305: the pool pages name lights that just went away, and the published page indices with them.
        Array.Clear(_cubeCombined); Array.Clear(CubeDynPage);
        // #306: the sun's half of the same wipe, degrade window included.
        Array.Clear(SunDynMask); Array.Clear(_sunCombined); Array.Clear(_sunWinBaked); Array.Clear(_sunDynValid);
        _sunWinFrames = 0;
        Array.Fill(_dynPageOf, -1);
        if (_dynPageSlot.Length > 0) Array.Fill(_dynPageSlot, -1);
        // ...and the log is once per RELEASE, not once per frame: a lost 4.6 context disables its service
        // permanently, so Lifecycle calls this every frame thereafter and an unguarded line would fill the log.
        bool any = _prevCount > 0 || _liveCount > 0 || CascadeCount > 0 || CubeCount > 0;
        _invalidate = true; _moverCount = 0; _ghostCount = 0;
        _prevCount = 0;
        _liveCount = 0; _liveCreatures = 0; _liveGen = -1;
        CasterGather.Stand();
        CascadeCount = 0; CubeCount = 0;   // the maps describe a world that just went away; stop the receiver sampling them
        if (any) _log?.Info($"CasterPass: caster set released ({why})");
    }

    /// <summary>One-shot 4.6-side setup shared by both views: the depth program and the FBO every layer is
    /// attached into. Any failure disarms the pass by name rather than leaving it silently drawing nothing.</summary>
    private static bool EnsureCommon()
    {
        if (_glFailed || _progFailed) return false;
        if (_gl.Prog == 0)
        {
            if (Gl.MultiDrawElementsIndirect == null || Gl.BindBufferBase == null
                || Gl.GenFramebuffers == null || Gl.CopyImageSubData == null)
            { Disarm("a required GL 4.4+ entry point is missing (multidraw-indirect / image copy)"); return false; }
            uint p = GlProgram.Graphics(ShdCastVs, ShaderStages.Source(ShdCastVs),
                                        ShdCastFs, ShaderStages.Source(ShdCastFs),
                                        _log, "CasterPass depth program",
                                        out string castVs, out string castFs);
            if (p != 0 && !(CastVsSafe(castVs) && CastFsSafe(castFs)))
            {
                Gl.DeleteProgram(p);
                _log?.Info("CasterPass: the caster depth override is refused -- it no longer reads Alloy's "
                         + "record through uDraw[gl_BaseInstance], has lost the skinning "
                         + "branch, or its fragment stage is not inert (a discard or a written gl_FragDepth "
                         + "silently removes or displaces every shadow). Rebuilding from the embedded stages "
                         + "(regenerate the override with /alloy shaderdump to get it back).");
                p = GlProgram.Graphics(CastVs, CastFs, _log, "CasterPass depth program (embedded)", content: false);
            }
            if (p == 0) { _progFailed = true; Disarm("the caster depth program failed to build"); return false; }
            _gl.Prog = p;
            _gl.LocVp = GlProgram.Loc(p, "uVP");
            // The cutout stage is optional in the sense that everything else still bakes without it: a
            // failure leaves masked casters out of the shadow maps entirely (see the collect), which is a
            // missing leaf shadow rather than the solid quad that made this necessary.
            _gl.ProgMask = GlProgram.Graphics(CastMaskVs, CastMaskFs, _log, "CasterPass masked depth program");
            if (_gl.ProgMask != 0)
            {
                _gl.LocMaskVp = GlProgram.Loc(_gl.ProgMask, "uVP");
                _gl.LocMaskTex = GlProgram.Loc(_gl.ProgMask, "uTex");
                _gl.LocMaskCut = GlProgram.Loc(_gl.ProgMask, "uAlphaCut"); _gl.LocMaskDither = GlProgram.Loc(_gl.ProgMask, "uDither"); _gl.LocMaskEnv = GlProgram.Loc(_gl.ProgMask, "uEnvAlpha"); _gl.LocMaskMip = GlProgram.Loc(_gl.ProgMask, "uAlphaMip");
            }
            else _log?.Info("CasterPass: the masked depth program failed to build -- cutout casters cast no " +
                            "shadow at all this session (they would otherwise cast their whole quad)");
            uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f;
        }
        return true;
    }

    private static bool EnsureSun()
    {
        if (!EnsureCommon()) return false;
        if (_gl.DepthTex == 0)
        {
            _gl.DepthTex = DepthArray(Gl.TEXTURE_2D_ARRAY, _mapSize, _cascades + CoverLayers);   // #617: the cover map rides the last layer
            if (_gl.DepthTex == 0) return false;   // DepthArray already disarmed by name
            Array.Clear(_sunBaked);   // #282: fresh storage holds no bake, whatever the keys say
            _coverBaked = false;
        }
        // #306: the sun's dynamic half, allocated whenever configured (the /shadow split dial is live).
        if (_sunSplitCfg && _gl.SunDynTex == 0)
        {
            _gl.SunDynTex = DepthArray(Gl.TEXTURE_2D_ARRAY, _mapSize, _cascades);
            if (_gl.SunDynTex == 0) return false;   // DepthArray already disarmed by name
        }
        return true;
    }

    private static bool EnsureCubes()
    {
        if (!EnsureCommon()) return false;
        if (_gl.CubeTex == 0)
        {
            _gl.CubeTex = DepthArray(Gl.TEXTURE_CUBE_MAP_ARRAY, _cubeSize, _cubeLights * CubeFaces);
            if (_gl.CubeTex == 0) return false;   // DepthArray already disarmed by name
            Array.Clear(_cubeBaked);   // #241: fresh storage holds no bake, whatever the keys say
            // Global state, not a texture parameter: filtering blends across face borders, which is what
            // makes the receiver's hardware-PCF fetch legitimate right up to a face edge. The interim cubes
            // bought the same thing with an over-scanned face frustum and a matching uv shrink in the shader.
            Gl.Enable(Gl.TEXTURE_CUBE_MAP_SEAMLESS);
        }
        // #305: the dynamic pool, allocated whenever it is configured (the /shadow split dial is live, so
        // the array must exist even on a frame the dial is off). A refused allocation disarms by name.
        if (_cubeDynPages > 0 && _gl.CubeDynTex == 0)
        {
            _gl.CubeDynTex = DepthArray(Gl.TEXTURE_CUBE_MAP_ARRAY, _cubeDynSize, _cubeDynPages * CubeFaces);
            if (_gl.CubeDynTex == 0) return false;   // DepthArray already disarmed by name
        }
        return true;
    }

    /// <summary>Probe and arm the layered bake, inside the bracket. The extension is a property of the
    /// 4.6 context, asked once; a driver without it (or a program that will not build) takes the per-view
    /// path and says so once. Nothing disarms here -- the fallback IS the shipping path.</summary>
    private static void EnsureLayered()
    {
        if (!_layerDial || _gl.LayerProgFailed) return;
        if (_layerProbe == 0)
        {
            _layerProbe = Gl.FramebufferTexture != null
                          && AlloyRenderer.HasExt("GL_ARB_shader_viewport_layer_array"u8) ? 1 : -1;
            if (_layerProbe < 0)
                _log?.Info("CasterPass: ARB_shader_viewport_layer_array is not in this context -- the " +
                           "layered bake is unavailable, every view submits on the per-view path");
        }
        if (_layerProbe < 0 || _gl.ProgLayer != 0) return;
        if (!CasterGather.EnsureLayer())
        {
            _gl.LayerProgFailed = true;
            _log?.Info("CasterPass: the layered gather dispatch failed to build -- per-view path");
            return;
        }
        uint p = GlProgram.Graphics(CastLayerVs, CastFs, _log, "CasterPass layered depth program");
        uint pm = GlProgram.Graphics(CastLayerMaskVs, CastMaskFs, _log, "CasterPass layered masked depth program");
        // Both or neither: a layered pass whose masked batches silently fell back to solid quads would
        // differ from the per-view A/B exactly where the cutout fix lives.
        if (p == 0 || pm == 0)
        {
            if (p != 0) Gl.DeleteProgram(p);
            if (pm != 0) Gl.DeleteProgram(pm);
            _gl.LayerProgFailed = true;
            _log?.Info("CasterPass: a layered depth program failed to build -- per-view path");
            return;
        }
        _gl.ProgLayer = p; _gl.ProgLayerMask = pm;
        _gl.LocLayerMaskTex = GlProgram.Loc(pm, "uTex");
        _gl.LocLayerMaskCut = GlProgram.Loc(pm, "uAlphaCut"); _gl.LocLayerMaskDither = GlProgram.Loc(pm, "uDither"); _gl.LocLayerMaskEnv = GlProgram.Loc(pm, "uEnvAlpha"); _gl.LocLayerMaskMip = GlProgram.Loc(pm, "uAlphaMip");
        _log?.Info("CasterPass: layered bake armed -- every stale view in one command buffer per target, " +
                   "gl_Layer from the view table (/shadow layered 0 is the per-view A/B)");
    }

    /// <summary>Render the frame's queued views, one layered pass per distinct target texture.
    /// False = the flush could not complete; the caller must publish NOTHING this frame.</summary>
    private static bool FlushLayered()
    {
        int n = _qN; _qN = 0;
        if (n == 0) return true;
        // on Vulkan the views are only QUEUED here: the colour frame flushes them once its own uploads have
        // published the buffers the bake's binds name
        if (Renderer.OnVulkan) { _vk.Queued = n; return true; }
        // distinct targets in first-seen order -- at most four exist (sun static/dynamic, cube static/pool).
        // A fifth-plus target would be a queue writer this flush does not know about; failing the flush
        // (publish nothing, blanket next frame) is the honest answer, not silently never baking it.
        uint* seen = stackalloc uint[8];
        int nt = 0;
        for (int k = 0; k < n; k++)
        {
            bool dup = false;
            for (int t = 0; t < nt && !dup; t++) dup = seen[t] == _qTex[k];
            if (dup) continue;
            if (nt >= 8) return false;
            seen[nt++] = _qTex[k];
        }
        for (int t = 0; t < nt; t++)
            if (!FlushTarget(seen[t], n)) return false;
        return true;
    }

    private static bool FlushTarget(uint tex, int n)
    {
        int size = 0;
        bool first = true;
        // The stale layers clear FIRST, one attach per view: a layered attachment clears every layer at
        // once, fresh ones included, so the per-layer loop here is correctness rather than thrift. The
        // target's state (FBO, viewport, buffers, depth state) is set once on the first view; the rest
        // attach and clear, two calls each instead of a dozen.
        for (int k = 0; k < n; k++)
        {
            if (_qTex[k] != tex) continue;
            size = _qSize[k];
            if (first) { if (!BeginTarget(tex, size, _qLayer[k])) return false; first = false; continue; }
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, _qLayer[k]);
            Gl.Clear(Gl.DEPTH_BUFFER_BIT);
        }
        Gl.FramebufferTexture(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            Disarm("the layered caster depth framebuffer is incomplete");
            return false;
        }
        Gl.Viewport(0, 0, size, size);
        // ponytail: chunking only exists for the blanket-invalidate frame in a huge area -- a settled frame
        // is one chunk. Growing MaxLayerCmds is the upgrade if the chunk count ever shows on the period line.
        int chunk = Math.Max(1, MaxLayerCmds / Math.Max(_gDrawN, 1));
        int k2 = 0;
        while (true)
        {
            int vn = 0, sets = 0;
            fixed (CasterViewRec* vs = _viewStage)
                while (k2 < n && vn < chunk)
                { if (_qTex[k2] == tex) { sets |= 1 << _qSet[k2]; StageView(vs, vn++, k2); } k2++; }
            if (vn == 0) break;
            bool ok;
            fixed (CasterViewRec* vs = _viewStage)
                ok = CasterGather.DispatchLayer(vs, vn, sets == 1 << CasterGather.SetDynamic);
            if (!ok) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); return false; }
            DrawLayered(vn, sets, size);
            CasterGather.End();
        }
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
    }

    /// <summary>One queued view into the chunk's staged table, in the layout the dispatch and the layered
    /// vertex stage both read (mat4 vp; focus/reach; dir/along; receiver/point/set/layer; detail).</summary>
    private static void StageView(CasterViewRec* dst, int vi, int k)
    {
        ref CasterViewRec r = ref dst[vi];
        r.Vp = _qVp[k];
        r.Focus = _qFocus[k]; r.Reach = _qReach[k];
        r.Dir = _qDir[k]; r.Along = _qAlong[k];
        r.Receiver = _qRx[k] ? 1f : 0f;   // #488: the receiver cull applies to this view
        r.Point = _qPoint[k] ? 1f : 0f;
        r.Set = _qSet[k];
        r.Layer = _qLayer[k];
        r.LodR1 = _qLodR1[k];
        r.LodR2 = _qLodR2[k];
    }

    /// <summary>The layered pass's batch loop: <see cref="GatherSubmit"/>'s state walk minus the per-view
    /// uniforms, one indirect-count draw per batch for ALL the chunk's views.</summary>
    /// <param name="sets">The union of the chunk's view SETS, a bit per <c>CasterGather.Set*</c>: a batch none
    /// of them can admit a caster from is not issued at all (SL2).</param>
    private static void DrawLayered(int vn, int sets, int size)
    {
        Gl.ActiveTexture(Gl.TEXTURE0);
        int boundVao = -1, boundMode = -1, boundTex = -1;
        int boundCut = -1;
        for (int g = 0; g < _gGroups; g++)
        {
            if (BatchIdle(g, sets)) { _mdiSkipped++; continue; }
            int cut0 = _gTex[g], mode = StageOf(g);
            if (mode != boundMode)
            {
                boundMode = mode;
                if (mode >= ContentMode) BindContentCaster(mode - ContentMode, layered: true, null, size);
                else
                {
                    Gl.UseProgram(mode == 0 ? _gl.ProgLayer : mode == 1 ? _gl.ProgLayerMask : _gl.ProgLayerMaskH);
                    if (mode == 1) Gl.Uniform1i(_gl.LocLayerMaskTex, 0);
                }
                boundCut = -1;
            }
            if (mode >= ContentMode && (int)_gCut[g] != boundCut)
            {
                boundCut = (int)_gCut[g];
                ContentCasterCut(mode - ContentMode, true, _gCut[g]);
            }
            else if (mode != 0 && mode < ContentMode && (int)_gCut[g] != boundCut)
            {
                CasterCut kind = _gCut[g]; boundCut = (int)kind;
                Gl.Uniform1f(mode == 1 ? _gl.LocLayerMaskCut : _gl.LocLayerMaskHCut, Threshold(kind));
                Gl.Uniform1f(mode == 1 ? _gl.LocLayerMaskMip : _gl.LocLayerMaskHMip, Renderer.CutMip.V);
                Gl.Uniform1i(mode == 1 ? _gl.LocLayerMaskDither : _gl.LocLayerMaskHDither, DitherFor(kind));
                Gl.Uniform1i(mode == 1 ? _gl.LocLayerMaskEnv : _gl.LocLayerMaskHEnv, kind == CasterCut.EnvAlpha ? 1 : 0);
            }
            if (mode == 1 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(AlloyRenderer.CasterVao(_gVao[g])); boundVao = _gVao[g]; }
            CasterGather.DrawLayer(BatchMode(g), g, _gStart[g], _gLen[g], vn);
            _mdiCalls++;
        }
    }

    /// <summary>A depth array sampled with hardware PCF: LINEAR + depth-compare IS the filter, one bilinear
    /// 2x2 comparison per fetch at no more cost than a plain sample. Same setup for the 2D cascade array and
    /// the cube array -- only the target and the layer count differ.</summary>
    /// <summary>0 = the storage was refused, and the pass has already disarmed itself by name.</summary>
    private static uint DepthArray(uint target, int size, int layers)
    {
        uint t; Gl.CreateTextures(target, 1, &t);
        Gl.DrainErrors();   // an earlier pass's error must not be read as this allocation's
        Gl.TextureStorage3D(t, 1, Gl.DEPTH_COMPONENT24, size, size, layers);
        if (Gl.GetError() != 0)
        {
            Gl.DeleteTextures(1, &t);
            Disarm($"a {size}x{size}x{layers} depth array could not be allocated (out of video memory?)");
            return 0;
        }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        return t;
    }

    private static void Disarm(string why)
    {
        _disarmed = true;
        _glFailed = true;
        _down.Fail(_log, why, "no Alloy caster submission this session");
        // BOTH counts, and this is the only place the distinction bites: OnPassSlot returns on _disarmed before
        // it ticks _frameSeq, so the freshness clock stops dead and every stamp stays equal to it forever.
        // A count left standing is then permanently "fresh" -- the receiver samples a frozen map for the rest
        // of the session, shadows pinned where the geometry no longer is.
        CascadeCount = 0; CubeCount = 0;
        Array.Clear(_cubeBaked); Array.Clear(CubeDynPage); Array.Clear(SunDynMask);
    }

    /// <summary>Bake one view (a cascade layer, or one face of one cube) from what it sees this frame: the
    /// gather dispatch selects out of the frame's staging and the survivors draw by indirect count, with
    /// <paramref name="point"/> picking which view test it runs. False = nothing usable, so the caller must
    /// not publish. The layer is cleared even when nothing survives -- uncleared is LAST frame's depth.</summary>
    private static bool RenderView(CasterMap map, uint tex, int size, int layer,
                                   Matrix4x4* vp, Vector3 lightDir, Vector3 focus, float reach, float margin,
                                   bool point, int set = CasterGather.SetAll, bool receiver = false,
                                   bool detail = true)
    {
        // zeroed ahead of every early-out: a view that bakes nothing staged no creatures, and a stale count
        // would be re-added per cube face
        _submitCreatures = 0;
        if (!_gStaged) return false;
        LastViews++;
        if (!GatherActive) return ClearLayer(map, tex, size, layer);   // an EMPTY world still bakes: the clear is the bake
        // CX-50: this view's two detail thresholds, where every other per-view term of the gather is resolved
        // -- one funnel, so the layered and per-view paths cannot pick different levels for one caster. An
        // ORTHO view's texel is its fit over the map (CascadeTexel's own expression, and a pair tile's);
        // a 90deg cube face spans 2d at depth d, so its texel is per unit of DISTANCE and the dispatch
        // divides the caster's bound by that distance.
        // /shadow detail 0 is the A/B for the whole of it: thresholds of 0 bake every caster's base run,
        // which is what the pass did before, with the colour path's own detail left alone. A view that is
        // not a shadow passes detail: false and takes that same base run -- see the cover pass.
        MeshLod.ShadowThresholds(detail && Shadows.Detail.V ? point ? 2f / size : 2f * reach / size : 0f,
                                 out float lodR1, out float lodR2);
        // #307: under the layered bake a stale view is QUEUED, not submitted -- the flush after the pass
        // loop renders every queued view of a target in one layered pass. The optimistic true is covered by
        // the flush-failure handling, which publishes nothing for the frame. An overflowing queue renders
        // inline rather than dropping a view.
        if (LayeredActive && _qN < MaxBakeViews)
        {
            // a target the Vulkan bake has no array for -- the sky view's own, a pair tile -- is refused rather than
            // queued: its caller must not publish a map nothing baked
            if (Renderer.OnVulkan && map == CasterMap.Other) return VkNoTarget();
            _submitCreatures = _gCreatures;
            _qMap[_qN] = map;
            _qTex[_qN] = tex; _qSize[_qN] = size; _qLayer[_qN] = layer; _qVp[_qN] = *vp;
            _qFocus[_qN] = focus; _qDir[_qN] = lightDir; _qReach[_qN] = reach; _qAlong[_qN] = reach + margin;
            _qPoint[_qN] = point; _qSet[_qN] = set; _qRx[_qN] = receiver;
            _qLodR1[_qN] = lodR1; _qLodR2[_qN] = lodR2;
            _qN++;
            return true;
        }
        // the per-view path is GL's alone: a Vulkan frame past the queue's ceiling refuses the view, which stands
        // its map down for the frame rather than baking it with GL's programs into GL's targets
        if (Renderer.OnVulkan) return VkQueueFull();
        return GatherSubmit(tex, size, layer, vp, lightDir, focus, reach, margin, point, lodR1, lodR2, set, receiver);
    }

    private static bool FboChecked(uint tex)
    {
        for (int i = 0; i < _gl.FboCheckedRing.Length; i++) if (_gl.FboCheckedRing[i] == tex) return true;
        _gl.FboCheckedRing[_gl.FboCheckedAt] = tex; _gl.FboCheckedAt = (_gl.FboCheckedAt + 1) % _gl.FboCheckedRing.Length;
        return false;
    }

    /// <summary>Clear one depth layer to "nothing casts" without a bake: the static faces of a roaming light.</summary>
    private static bool ClearLayer(CasterMap map, uint tex, int size, int layer)
    {
        if (Renderer.OnVulkan && map != CasterMap.Other) return VkClearLayer(map, layer);
        if (!BeginTarget(tex, size, layer)) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
    }

    /// <summary>Attach one depth layer of <paramref name="tex"/> and clear it. Shared by both submission
    /// paths: a view that culls out entirely still clears, because an uncleared layer is not "empty", it is
    /// LAST frame's depth.</summary>
    private static bool BeginTarget(uint tex, int size, int layer)
    {
        // A context loss recreates the record buffer at the next colour build; a bracket that runs first
        // would bind name 0 and the depth stages would read an unbound SSBO. Nothing to bake against yet.
        if (AlloyRenderer.DrawRecordBuf == 0) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, layer);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);
        // Completeness is a property of the attachment's texture, not of the layer: checked on the first
        // attach of each target (and every view under alloy.gldebug=1), not ~300 times a frame. Mesa charged
        // 'about twelve calls per view including CheckFramebufferStatus' (mesa-amd-render-defects.md).
        if ((!FboChecked(tex) || GlContextService.Debug)
            && Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); Disarm("the caster depth framebuffer is incomplete"); return false; }
        Gl.Viewport(0, 0, size, size);
        // clamp only the directional maps: a point cube's near plane IS the self-shadow skip the lightmap bake clips at
        if (tex == _gl.DepthTex || tex == _gl.SunDynTex || tex == _gl.SkyDepth) Gl.Enable(Gl.DEPTH_CLAMP); else Gl.Disable(Gl.DEPTH_CLAMP);
        // #302: the depth stages read ALLOY's per-draw records and ALLOY's bone block, at Alloy's own binding
        // points. Re-bound per view (per flush target on the layered path) rather than once per frame because
        // the colour pass is entitled to rebind both between frames, and a bake reading a buffer that has
        // since been re-pointed is a world baked from someone else's matrices -- which renders, and renders wrong.
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, AlloyRenderer.DrawRecordBuf);
        if (AlloyRenderer.BoneBlockBuf != 0)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindBones, AlloyRenderer.BoneBlockBuf);
        if (AlloyRenderer.TexHandleBuf != 0)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, AlloyRenderer.BindTexH, AlloyRenderer.TexHandleBuf);
        // depth state BEFORE the clear: a clear runs through the write mask and the scissor, so clearing
        // first would leave stale depth wherever a previous pass left either of them set
        // #292: cull OFF, deliberately DIFFERENT from the colour pass, and this is the disposition the
        // ticket asks for. A shadow map is depth from the LIGHT's camera, so "which face is the front one"
        // is the light's question and not the eye's: culling back faces here would drop a single-sided wall
        // whenever the light stands behind it, and the light would pour straight through. Casting from every
        // face is a superset -- for a closed mesh the nearest surface is a front face either way, and for an
        // open sheet the sheet is what should occlude. So the mismatch is one-directional and safe, whereas
        // matching the colour pass would open leaks.
        new PassState(depth: true, depthWrite: true, cull: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT);
        return true;
    }
}
