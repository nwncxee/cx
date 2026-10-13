using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The GL side of Alloy's GUI path: <see cref="GuiCapture"/> turns every GUI scene into layers of captured
/// draws and this issues them from the 4.6 context in ONE bracket per flush, one program, the records in one
/// SSBO read through <c>gl_BaseInstance</c>. The gate is its own, not <see cref="AlloyGate"/>'s world verdict
/// -- the GUI has no world frame to wait for, main menu included. ponytail: two full-screen blits per flush.
/// </summary>
internal static unsafe class GuiPass
{
    private const uint BindGui = SsboSlots.Gui;
    /// <summary>Ahead of <c>UiHostService</c>'s window roll-call (20) on the <c>nk_sdl_render</c> chain: the panels sit
    /// under the CX windows and NUI, as they do in the engine's order.</summary>
    private const int NkPriority = 40;
    private const int Vec4PerRec = GuiCapture.RecFloats / 4;

    private static ExtenderLog? _log;
    private static bool _armed, _failed, _live, _dropSaid;
    /// <summary>Everything the GL context owns, in one object: <see cref="ContextLost"/> replaces it.</summary>
    private sealed class GuiGlState
    {
        public bool ProgTried;
        public uint Prog, Buf, VtxBuf, IdxBuf, Fbo, Depth;
        public long BufCap, VtxCap, IdxCap;
        public int LocBoneStart, LocGamma, DepthW, DepthH;
        public readonly Dictionary<uint, uint> StreamVao = new(16);
        /// <summary>Content programs by key, built on first draw; null = refused, said once by the build.</summary>
        public readonly Dictionary<int, GuiContent.Prog?> Content = new();
        /// <summary>Windows: the default framebuffer's depth format, probed once in the engine context; the GUI
        /// depth is allocated in it so the two can be blitted into each other.</summary>
        public uint DepthFmt, FboEngDepth;
        public bool DepthProbed;
        /// <summary>Layers of the current bank already issued by <see cref="FlushLayers"/>; the closing flush starts here.</summary>
        public int Issued;
        /// <summary>The model views' target: one framebuffer over whatever texture a layer names, and a depth for each slot (a view
        /// keeps its own so two sizes do not remake it every flush), both made in this context (framebuffers are not shared with the engine's).</summary>
        public uint ModelFbo;
        public readonly uint[] ModelDepth = new uint[CX.Services.Nui.NuiModelDoll.MaxViews];
        public readonly int[] ModelW = new int[CX.Services.Nui.NuiModelDoll.MaxViews], ModelH = new int[CX.Services.Nui.NuiModelDoll.MaxViews];
        public bool ModelChecked;
    }

    private static GuiGlState _gl = new();
    private static int* _screenW, _screenH;
    private static NkSdlRenderFn* _nkSlot;
    private static long _flushes, _drawn, _noVao;

    /// <summary>Is the GUI Alloy's right now? Asked by the capture hook per scene; everything it names exists before
    /// the first scene is captured, so a scene is never captured into a flush that cannot draw it.</summary>
    internal static bool Capturing => _armed && !_failed && _gl.Prog != 0 && Renderer.Gui.V && GlContextService.Available;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        if (_screenW == null || _screenH == null)
        { log.Always("GuiPass: g_nScreenWidth/Height unresolved -- the GUI stays the ENGINE's draw"); return; }
        Db.DbGui.Install(log);
        Db.TextFontBake.Install(log);
        Db.TextSlots.Install(log);
        Db.TextQuads.Install(log);
        TextAtlas.Install(log);
        if (!GuiCapture.Install(log, hooks)) return;
        ScreenOwner.Install(log, hooks);
        _nkSlot = hooks.ChainNkSdlRender(&OnNk, NkPriority);
        FrameRegistry.Global.Register(FramePhase.PrePresent, 5, OnPresent, "GuiPass.flush", log);
        _armed = true;
        log.Info("GuiPass: armed -- GUI scenes are captured and drawn by Alloy once the 4.6 context is up "
               + (_nkSlot != null ? "(flushed before NUI and at the present)" : "(nk_sdl_render unhooked: flushed at the present, above NUI)")
               + "; /alloy gui 0 hands them back");
    }

    /// <summary>The panels, just before nuklear flushes (and before the CX windows chained under it).</summary>
    [UnmanagedCallersOnly]
    private static void OnNk(float aa)
    {
        Flush();
        (*_nkSlot)(aa);
    }

    /// <summary>The overlay views drawn after NUI, and the one-time program build.</summary>
    private static void OnPresent()
    {
        if (_armed && !_gl.ProgTried && GlContextService.Available && GlContextService.Enter())
        {
            _gl.ProgTried = true;
            BuildProgram();
            GlContextService.Leave();
        }
        // Cold: a font's atlas lands on the frames after it is first named -- and inside ALLOY's bracket,
        // never here. TextAtlas.Upload is 4.5 DSA (glCreateTextures/glTextureStorage2D) and its sweep is
        // glDeleteTextures; this site runs ahead of GlContextService's bracket, i.e. in the ENGINE's 3.3 core
        // context, which is not the context those names belong to (review #1).
        if (TextAtlas.Due && GlContextService.Enter()) { TextAtlas.Pump(); GlContextService.Leave(); }
        // NUI's layers wait for SeamDraw's flush, which puts them over the panels; flushed here, its frame clear
        // erased them.
        _frameColour = false;
        _seqAtFrame = PostChain.ResolveSeq;
    }

    /// <summary>Has this frame's colour been cleared, and the world-resolve count at the last present -- a
    /// frame whose count moved had a world frame paint the whole screen, so it needs no clear.</summary>
    private static bool _frameColour;
    private static int _seqAtFrame;

    /// <summary>The engine's swallowed frame clear, done here -- once, at the first flush of a frame that no world
    /// frame covered (the menus), in the engine's own clear colour. Unscissored, like <c>Update</c>'s <c>Clear(3)</c>.</summary>
    private static void FrameClear()
    {
        if (_frameColour) return;
        _frameColour = true;
        if (!ScreenOwner.Owned || PostChain.ResolveSeq != _seqAtFrame) return;
        Gl.Disable(Gl.SCISSOR_TEST);
        fixed (float* c = ScreenOwner.ClearRgba) Gl.ClearBufferfv(Gl.COLOR, 0, c);
        Gl.DepthMask(1);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
    }

    private static void BuildProgram()
    {
        if (Gl.DrawElementsInstancedBaseVertexBaseInstance == null || Gl.DrawArraysInstancedBaseInstance == null
            || Gl.CreateBuffers == null || Gl.VertexArrayAttribFormat == null)
        { Fail("glDrawElementsInstancedBaseVertexBaseInstance or DSA is missing"); return; }
        _gl.Prog = GlProgram.Graphics(ShaderSource.Get("cx_gui_v.glsl"), ShaderSource.Get("cx_gui_f.glsl"), _log, "Alloy GUI");
        if (_gl.Prog == 0) { Fail("the GUI program did not build"); return; }
        _gl.LocBoneStart = GlProgram.Loc(_gl.Prog, "uBoneStart");
        _gl.LocGamma = GlProgram.Loc(_gl.Prog, "uGamma");
        uint b;
        Gl.CreateBuffers(1, &b); _gl.Buf = b;
        Gl.CreateBuffers(1, &b); _gl.VtxBuf = b;
        Gl.CreateBuffers(1, &b); _gl.IdxBuf = b;
        _log?.Info("GuiPass: program built -- the GUI is Alloy's from this frame");
    }

    /// <summary>Our own mechanism failed: said once. Nothing draws the game's panels after this -- the scene
    /// database has no engine traversal underneath -- so it is a hard failure naming the way out.</summary>
    private static void Fail(string why)
    {
        _failed = true;
        _log?.Always($"GuiPass: {why} -- the game's GUI panels are NOT DRAWN for the rest of the session");
        AlloyGate.Fail($"the interface could not be drawn ({why})");
    }

    /// <summary>Flush now -- a caller whose draws must land before the next foreign draw (nuklear's own site).</summary>
    internal static void FlushNow() => Flush();

    /// <summary>Issue the first <paramref name="n"/> captured layers and keep the bank, so something else can draw over
    /// them before <see cref="FlushNow"/> issues the rest. The closing flush uploads nothing again.</summary>
    internal static void FlushLayers(int n)
    {
        n = Math.Min(n, GuiCapture.LayerN);
        if (n <= 0 || _gl.Issued != 0) return;
        if (_gl.Prog == 0 || _failed || !Draw(0, n)) Dropped();
        else _gl.Issued = n;
    }

    /// <summary>Issue every layer captured since the last flush that is still owed, then forget them. A flush that
    /// cannot run drops what it holds -- the engine did not draw those scenes either -- and says so once.</summary>
    private static void Flush()
    {
        if (GuiCapture.LayerN == 0) return;
        if (_gl.Issued >= GuiCapture.LayerN) { _gl.Issued = 0; GuiCapture.Reset(); return; }
        if (_gl.Prog == 0 || _failed || !Draw(_gl.Issued, GuiCapture.LayerN)) Dropped();
        _gl.Issued = 0;
        GuiCapture.Reset();
    }

    private static void Dropped()
    {
        if (_dropSaid) return;
        _dropSaid = true;
        _log?.Always("GuiPass: a GUI flush could not enter the 4.6 context -- that frame's GUI is NOT DRAWN");
    }

    /// <summary>ponytail: one GL draw per captured record, state changes elided by comparison. A menu is a few hundred
    /// draws; if the GUI ever shows in the frame zones, sort each layer by (VAO, texture, blend) and multi-draw the runs.</summary>
    private static bool Draw(int from, int to)
    {
        int w = *_screenW, h = *_screenH;
        bool win = PresentTarget.Needed;
        if (win) ProbeDepth();
        if (win && PresentTarget.Width == w && PresentTarget.Height == h) { PresentTarget.TakeBack(); Depth(fromScreen: true); }
        else if (win) Resize(w, h);
        if (!GlContextService.Enter()) return false;
        // CX-120 (gui): on a frame with no world, THIS is the only 4.6 bracket there is -- and entering it
        // sets GlContextService's `_worked`, which makes its own health bracket stand down for the frame. At
        // the main menu that starved both halves of the DB GUI: the mesh arena's VAO was never built (every
        // model part refused `no-geometry`, so the menu drew its button text and not one pixel of its art)
        // and the texture registry's uploads never ran (every part that did draw came out white). Both are
        // queue tests when there is nothing to do.
        GlContextService.RunInBracketHere(); Db.MeshArena.Pump();
        if (from == 0) Upload();
        bool gpuTimed = CX.Services.Perf.FrameProbe.GpuBegin();
        long modelSpan = 0;
        uint baseFbo = win ? Target() : 0u;
        uint curTarget = 0;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, baseFbo);
        Gl.Disable(Gl.FRAMEBUFFER_SRGB);
        Gl.Disable(Gl.MULTISAMPLE);
        Gl.Disable(Gl.STENCIL_TEST);
        FrameClear();
        Gl.Enable(Gl.SCISSOR_TEST);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.FrontFace(Gl.CCW);
        Gl.CullFace(Gl.BACK);
        Gl.UseProgram(_gl.Prog);
        Gl.Uniform1i(_gl.LocBoneStart, GuiCapture.DrawN * Vec4PerRec);
        Gl.Uniform1i(_gl.LocGamma, PostChain.GammaOff() ? 0 : 1);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindGui, _gl.Buf);
        // CX-117's master palette, for the PLT item icons a GUI control draws. A no-op until a palette
        // exists (DB mode only), and the unit is nothing else's.
        Db.PltPalettes.Bind();
        int envTex = 0;
        int blend = -1, depth = -1, write = -1, cull = -1;
        uint src = 0, dst = 0, vao = uint.MaxValue;
        int tex = -1;
        uint prog = _gl.Prog;
        bool gamma = !PostChain.GammaOff();
        bool released = false;
        int li = from, solve = 0, runStart = 0, runEnd = 0;
        while (li < to || solve != 0)
        {
            if (solve != 0 && li == runEnd)
            {
                ref GuiCapture.Layer last = ref GuiCapture.Layers[runStart];
                if (solve == 1)
                {
                    solve = 2; li = runStart;
                    Gl.BindFramebuffer(Gl.FRAMEBUFFER, ModelTarget(GuiModelSolve.Scratch(1, last.W, last.H), last.W, last.H, SlotOf(curTarget)));
                    ClearModelTarget(null, 1f);
                    write = 1;
                }
                else
                {
                    solve = 0;
                    Gl.BindFramebuffer(Gl.FRAMEBUFFER, ModelTarget(curTarget, last.W, last.H, SlotOf(curTarget)));
                    GuiModelSolve.Resolve(last.W, last.H, CX.Services.Nui.NuiModelDoll.OfTexture(curTarget), _log);
                    CX.Services.Perf.FrameProbe.Lap(ref modelSpan, false);
                    Gl.UseProgram(_gl.Prog);
                    blend = depth = write = cull = tex = -1; src = dst = 0; vao = uint.MaxValue; prog = _gl.Prog;
                    Gl.Enable(Gl.SCISSOR_TEST);
                    curTarget = uint.MaxValue;
                    if (li >= to) break;
                }
            }
            ref GuiCapture.Layer l = ref GuiCapture.Layers[li++];
            if (l.Target != curTarget)
            {
                curTarget = l.Target;
                CX.Services.Perf.FrameProbe.Lap(ref modelSpan, curTarget != 0);
                CX.Services.Nui.NuiModelDoll.ModelSlot? owner = CX.Services.Nui.NuiModelDoll.OfTexture(curTarget);
                // A released view's texture may be a reused GL name: draw nothing into it.
                released = curTarget != 0 && owner == null;
                if (released) continue;
                if (curTarget != 0 && GuiModelSolve.Wanted(li - 1, to, owner, out runEnd))
                {
                    solve = 1; runStart = li - 1;
                    Gl.BindFramebuffer(Gl.FRAMEBUFFER, ModelTarget(GuiModelSolve.Scratch(0, l.W, l.H), l.W, l.H, SlotOf(curTarget)));
                    ClearModelTarget(null, 0f);
                }
                else
                {
                    Gl.BindFramebuffer(Gl.FRAMEBUFFER, curTarget != 0 ? ModelTarget(curTarget, l.W, l.H, SlotOf(curTarget)) : baseFbo);
                    if (curTarget != 0) ClearModelTarget(owner);
                }
                Gl.FrontFace(curTarget != 0 ? Gl.CW : Gl.CCW);
                write = -1;
            }
            else if (released) continue;
            // The run's depth clear rides on its FIRST view, drawn or not: skipped with an empty layer, it left last
            // frame's text quads (0.001 nearer) in the buffer, and everything behind them failed there (live, holes).
            if (l.Clear)
            {
                Gl.Disable(Gl.SCISSOR_TEST);
                Gl.DepthMask(1); write = 1;
                Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
                Gl.Enable(Gl.SCISSOR_TEST);
            }
            if (l.Count == 0 || l.W <= 0 || l.H <= 0) continue;
            Gl.Viewport(l.X, l.Y, l.W, l.H);
            Gl.Scissor(l.SX, l.SY, l.SW, l.SH);
            Gl.DepthRange(l.Near, l.Far);
            for (int di = l.First; di < l.First + l.Count; di++)
            {
                ref GuiCapture.Draw d = ref GuiCapture.Draws[di];
                // CX-120: a DB draw brings its own VAO (the mesh arena's, one per vertex format). Nothing else
                // changes: the record, the SSBO and the per-rect viewport are the captured path's.
                uint v = d.RawVao != 0 ? d.RawVao
                       : d.Stream ? StreamVao(d.Format, d.Stride) : 0u;
                if (v == 0) { _noVao++; continue; }
                if (v != vao) { Gl.BindVertexArray(v); vao = v; }
                Toggle(Gl.BLEND, d.Blend, ref blend);
                if (d.Blend && (d.Src != src || d.Dst != dst)) { Gl.BlendFunc(d.Src, d.Dst); src = d.Src; dst = d.Dst; }
                Toggle(Gl.DEPTH_TEST, d.DepthTest, ref depth);
                if ((d.DepthWrite ? 1 : 0) != write) { write = d.DepthWrite ? 1 : 0; Gl.DepthMask((byte)write); }
                Toggle(Gl.CULL_FACE, d.Cull, ref cull);
                if (d.Tex != tex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)d.Tex); tex = d.Tex; }
                if (d.EnvTex != 0 && d.EnvTex != envTex)
                {
                    Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GuiEnv);
                    Gl.BindTexture(Gl.TEXTURE_2D, (uint)d.EnvTex);
                    Gl.ActiveTexture(Gl.TEXTURE0);
                    envTex = d.EnvTex;
                }
                GuiContent.Prog? cp = d.Content == 0 ? null : ContentProgram(d.Content);
                uint want = cp?.Id ?? _gl.Prog;
                if (want != prog) { Gl.UseProgram(want); prog = want; }
                if (cp != null) GuiContent.Feed(cp, di, l.W, l.H, gamma);
                if (d.Indexed)
                    Gl.DrawElementsInstancedBaseVertexBaseInstance(d.Prim, d.Count, Gl.UNSIGNED_SHORT, (void*)d.Offset, 1,
                                                                   d.BaseVertex, (uint)di);
                else
                    Gl.DrawArraysInstancedBaseInstance(d.Prim, (int)d.Offset, d.Count, 1, (uint)di);
                _drawn++;
            }
        }
        CX.Services.Perf.FrameProbe.Lap(ref modelSpan, false);
        if (curTarget != 0) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, baseFbo); Gl.FrontFace(Gl.CCW); }
        Gl.BindVertexArray(0);
        Gl.DepthRange(0.0, 1.0);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (gpuTimed) CX.Services.Perf.FrameProbe.GpuEnd();
        GlContextService.Leave();
        if (win) { PresentTarget.HandBack(); Depth(fromScreen: false); }
        _flushes++;
        if (!_live) { _live = true; _log?.Info($"GuiPass: LIVE -- {GuiCapture.LayerN} GUI scene(s), {GuiCapture.DrawN} draw(s) this flush, none reached the engine's GL"); }
        return true;
    }

    /// <summary>The index of the model view slot that draws into <paramref name="tex"/>, or 0.</summary>
    private static int SlotOf(uint tex) => CX.Services.Nui.NuiModelDoll.OfTexture(tex)?.Index ?? 0;

    /// <summary>The framebuffer a model view's layer draws into: <paramref name="tex"/> as colour, a depth the size of the
    /// layer. Re-attached every flush, since GL reuses freed names.</summary>
    private static uint ModelTarget(uint tex, int w, int h, int k)
    {
        if (_gl.ModelFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.ModelFbo = f; }
        if (_gl.ModelDepth[k] == 0 || _gl.ModelW[k] != w || _gl.ModelH[k] != h)
        {
            RenderTarget.AllocDepth(ref _gl.ModelDepth[k], Gl.DEPTH24_STENCIL8, w, h);
            _gl.ModelW[k] = w; _gl.ModelH[k] = h;
        }
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.ModelFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.TEXTURE_2D, _gl.ModelDepth[k], 0);
        if (!_gl.ModelChecked)
        {
            _gl.ModelChecked = true;
            uint st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            _log?.Always($"GuiPass: model view target {w}x{h} on texture {tex}: framebuffer status 0x{st:x}" + (st == Gl.FRAMEBUFFER_COMPLETE ? "" : " -- INCOMPLETE, the view draws nothing"));
        }
        return _gl.ModelFbo;
    }

    /// <summary>The slot's background, or with <paramref name="solid"/> from 0 to 1 that grey, opaque, then a fresh depth.</summary>
    private static void ClearModelTarget(CX.Services.Nui.NuiModelDoll.ModelSlot? slot, float solid = -1f)
    {
        Gl.Disable(Gl.SCISSOR_TEST);
        float* c = stackalloc float[4];
        if (slot != null) CX.Services.Nui.NuiModelDoll.Background(slot, c);
        if (solid >= 0f) { c[0] = c[1] = c[2] = solid; c[3] = 1f; }
        Gl.ClearBufferfv(Gl.COLOR, 0, c);
        Gl.DepthMask(1);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
        Gl.Enable(Gl.SCISSOR_TEST);
    }

    /// <summary>Key <paramref name="key"/>'s content program, built on its first draw (in the 4.6 bracket).</summary>
    private static GuiContent.Prog? ContentProgram(int key)
    {
        if (_gl.Content.TryGetValue(key, out GuiContent.Prog? cp)) return cp;
        cp = GuiContent.Build(key, _log);
        _gl.Content[key] = cp;
        return cp;
    }

    private static void Toggle(uint cap, bool on, ref int cur)
    {
        int want = on ? 1 : 0;
        if (want == cur) return;
        if (on) Gl.Enable(cap); else Gl.Disable(cap);
        cur = want;
    }

    /// <summary>Records then palettes into one SSBO, stream vertices and indices into their own buffers -- each
    /// orphaned per flush. ponytail: two orphans a frame at a few hundred KiB; a persistent ring if the GUI upload
    /// ever shows in the zones.</summary>
    private static void Upload()
    {
        long rec = (long)GuiCapture.DrawN * GuiCapture.RecFloats * sizeof(float);
        long bones = (long)GuiCapture.SkinN * GuiCapture.BoneFloats * sizeof(float);
        Orphan(_gl.Buf, ref _gl.BufCap, rec + bones);
        fixed (float* r = GuiCapture.Records) Gl.NamedBufferSubData(_gl.Buf, IntPtr.Zero, (IntPtr)rec, r);
        if (bones > 0) fixed (float* b = GuiCapture.Bones) Gl.NamedBufferSubData(_gl.Buf, (IntPtr)rec, (IntPtr)bones, b);
        if (GuiCapture.VtxN > 0)
        {
            Orphan(_gl.VtxBuf, ref _gl.VtxCap, GuiCapture.VtxN);
            fixed (byte* v = GuiCapture.Vtx) Gl.NamedBufferSubData(_gl.VtxBuf, IntPtr.Zero, (IntPtr)GuiCapture.VtxN, v);
        }
        if (GuiCapture.IdxN > 0)
        {
            Orphan(_gl.IdxBuf, ref _gl.IdxCap, GuiCapture.IdxN * 2L);
            fixed (ushort* i = GuiCapture.Idx) Gl.NamedBufferSubData(_gl.IdxBuf, IntPtr.Zero, (IntPtr)(GuiCapture.IdxN * 2L), i);
        }
    }

    private static void Orphan(uint buf, ref long cap, long bytes)
    {
        if (bytes > cap) cap = Math.Max(bytes, Math.Max(cap * 2, 64 * 1024));
        Gl.NamedBufferData(buf, (IntPtr)cap, null, Gl.DYNAMIC_DRAW);
    }

    /// <summary>One VAO per stream format, over the stream buffer at the engine's interleave. A draw addresses its run
    /// by base vertex / first vertex, which is why the capture aligns every run to its stride.</summary>
    private static uint StreamVao(uint fmt, int stride)
    {
        if (_gl.StreamVao.TryGetValue(fmt, out uint v)) return v;
        if (!GuiStream.Layout(fmt, stride, out GuiStream.Offsets o)) return 0;
        Gl.CreateVertexArrays(1, &v);
        Gl.VertexArrayVertexBuffer(v, 0, _gl.VtxBuf, IntPtr.Zero, stride);
        Gl.VertexArrayElementBuffer(v, _gl.IdxBuf);
        Attr(v, AlloyRenderer.LocPos, o.PosN, Gl.FLOAT, 0, o.Pos);
        if (o.Uv >= 0) Attr(v, AlloyRenderer.LocUv, 2, Gl.FLOAT, 0, o.Uv);
        if (o.Normal >= 0) Attr(v, AlloyRenderer.LocNrm, 3, Gl.FLOAT, 0, o.Normal);
        if (o.Color >= 0) Attr(v, LocColor, 4, Gl.UNSIGNED_BYTE, 1, o.Color);
        _gl.StreamVao[fmt] = v;
        return v;
    }

    /// <summary>The GUI program's vertex-colour location (<c>cx_gui_v.glsl</c>); nothing else in Alloy binds it.</summary>
    private const uint LocColor = 3;

    private static void Attr(uint vao, uint loc, int size, uint type, byte norm, int offset)
    {
        Gl.EnableVertexArrayAttrib(vao, loc);
        Gl.VertexArrayAttribFormat(vao, loc, size, type, norm, (uint)offset);
        Gl.VertexArrayAttribBinding(vao, loc, 0);
    }

    /// <summary>Windows: the GUI framebuffer -- the present texture for colour and a depth of its own, rebuilt when
    /// the texture is.</summary>
    private static uint Target()
    {
        uint tex = PresentTarget.Tex;
        if (tex == 0) return 0;
        if (_gl.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f; }
        if (_gl.Depth == 0 || _gl.DepthW != PresentTarget.Width || _gl.DepthH != PresentTarget.Height)
        {
            RenderTarget.AllocDepth(ref _gl.Depth, _gl.DepthFmt != 0 ? _gl.DepthFmt : Gl.DEPTH24_STENCIL8, PresentTarget.Width, PresentTarget.Height);
            _gl.DepthW = PresentTarget.Width; _gl.DepthH = PresentTarget.Height;
        }
        // Re-attached every flush, as PresentTarget does: GL reuses freed names across a resize.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, DepthAttachment, Gl.TEXTURE_2D, _gl.Depth, 0);
        return _gl.Fbo;
    }

    /// <summary>Windows, the present texture missing or the wrong size (a frame before any world frame, or a resize):
    /// build it in the 4.6 context, then take the screen back into it.</summary>
    private static void ProbeDepth()
    {
        if (_gl.DepthProbed) return;
        _gl.DepthProbed = true;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _gl.DepthFmt = RenderTarget.ProbeDepthFormat(0);
        if (_gl.DepthFmt == 0) _log?.Always("GuiPass: the default framebuffer reports no depth -- on Windows a content-shader GUI draw "
                                       + "will not depth-test against Alloy's GUI draws");
    }

    private static void Resize(int w, int h)
    {
        if (!GlContextService.Enter()) return;
        PresentTarget.Ensure(w, h);
        GlContextService.Leave();
        PresentTarget.TakeBack();
    }

    private static bool HasStencil => _gl.DepthFmt == Gl.DEPTH24_STENCIL8 || _gl.DepthFmt == Gl.DEPTH32F_STENCIL8;
    private static uint DepthAttachment => HasStencil ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT;

    /// <summary>Windows, in the ENGINE context: carry the GUI's depth between the screen and Alloy's GUI target, beside the
    /// colour <see cref="PresentTarget"/> carries. On Linux both contexts share the drawable's one depth buffer; here a
    /// content-shader draw the engine makes (<see cref="GuiCapture"/>'s hand-off) lands in the screen's, so without this
    /// its depth and Alloy's GUI depth would never meet.</summary>
    private static void Depth(bool fromScreen)
    {
        if (_gl.Depth == 0 || _gl.DepthFmt == 0) return;
        if (_gl.FboEngDepth == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.FboEngDepth = f; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.FboEngDepth);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, DepthAttachment, Gl.TEXTURE_2D, _gl.Depth, 0);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, fromScreen ? 0u : _gl.FboEngDepth);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, fromScreen ? _gl.FboEngDepth : 0u);
        uint mask = Gl.DEPTH_BUFFER_BIT | (HasStencil ? Gl.STENCIL_BUFFER_BIT : 0u);
        Gl.BlitFramebuffer(0, 0, _gl.DepthW, _gl.DepthH, 0, 0, _gl.DepthW, _gl.DepthH, mask, (uint)Gl.NEAREST);
        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>GL names die with the context.</summary>
    internal static void ContextLost()
    {
        TextAtlas.ContextLost();   // CX-119: every font atlas went with the context
        GuiModelSolve.ContextLost();
        _gl = new GuiGlState();
    }

    internal static string Stats() => !_armed ? "gui: engine (not armed)" : _failed ? "gui: engine (FAILED)"
        : !Renderer.Gui.V ? "gui: engine (/alloy gui 0)" : _gl.Prog == 0 ? "gui: waiting for the 4.6 context"
        : $"gui: Alloy -- {_flushes} flushes, {_drawn} draws" + (_noVao == 0 ? "" : $", {_noVao} with no VAO (NOT DRAWN)")
          + "; " + GuiCapture.Stats() + "; " + ScreenOwner.Stats();
}
