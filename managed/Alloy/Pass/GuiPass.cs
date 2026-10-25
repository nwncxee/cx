using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The GL side of Alloy's GUI path: <see cref="GuiCapture"/> turns every GUI scene into layers of draw
/// records and this issues them from the 4.6 context in ONE bracket per flush, one program, the records in one
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
        /// <summary>The model views' target: one framebuffer over whatever texture a layer names, and a depth for each slot (a view
        /// keeps its own so two sizes do not remake it every flush), both made in this context (framebuffers are not shared with the engine's).</summary>
        public uint ModelFbo;
        public readonly uint[] ModelDepth = new uint[CX.Services.Nui.NuiModelDoll.MaxViews];
        public readonly int[] ModelW = new int[CX.Services.Nui.NuiModelDoll.MaxViews], ModelH = new int[CX.Services.Nui.NuiModelDoll.MaxViews];
        public bool ModelChecked, WindowsDropSaid, FboChecked;
        /// <summary>Flushes that found a texture unit other than 0 active, and the first one's unit.</summary>
        public long StrayUnit;
        public int StrayUnitFirst;
    }

    private static GuiGlState _gl = new();
    private static int* _screenW, _screenH;
    private static NkSdlRenderFn* _nkSlot;
    private static long _flushes, _drawn, _noVao;

    /// <summary>Is the GUI Alloy's right now? Asked once per scene before it is listed; everything it names exists
    /// by then, so a scene is never listed into a flush that cannot draw it.</summary>
    internal static bool Capturing => _armed && !_failed && _gl.Prog != 0 && Renderer.Gui.V && GlContextService.Available;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        if (_screenW == null || _screenH == null)
        { log.Always("GuiPass: g_nScreenWidth/Height unresolved -- the game's GUI panels are NOT DRAWN"); return; }
        Db.DbGui.Install(log);
        Db.TextFontBake.Install(log);
        Db.TextSlots.Install(log);
        Db.TextQuads.Install(log);
        TextAtlas.Install(log);
        GuiCapture.Attach(log);
        ScreenOwner.Install(log, hooks);
        _nkSlot = hooks.ChainNkSdlRender(&OnNk, NkPriority);
        FrameRegistry.Global.Register(FramePhase.PrePresent, 5, OnPresent, "GuiPass.flush", log);
        _armed = true;
        log.Info("GuiPass: armed -- GUI scenes are listed and drawn by Alloy once the 4.6 context is up "
               + (_nkSlot != null ? "(flushed before NUI and at the present)" : "(nk_sdl_render unhooked: flushed at the present, above NUI)")
               + "; /alloy gui 0 turns the game's panels OFF, it does not hand them to the engine");
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

    /// <summary>Does this flush owe the engine's swallowed frame clear -- the first flush of a frame no world frame
    /// covered (the menus)? Asked once per frame: the second ask is false whoever made the first.</summary>
    internal static bool TakeFrameClear()
    {
        if (_frameColour) return false;
        _frameColour = true;
        return ScreenOwner.Owned && PostChain.ResolveSeq == _seqAtFrame;
    }

    /// <summary>The engine's swallowed frame clear, done here -- once, at the first flush of a frame that no world
    /// frame covered (the menus), in the engine's own clear colour. Unscissored, like <c>Update</c>'s <c>Clear(3)</c>.</summary>
    private static void FrameClear()
    {
        if (!TakeFrameClear()) return;
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

    /// <summary>The frame's closing flush: the first <paramref name="panels"/> listed layers, CX's below-NUI windows,
    /// the rest (NUI), then the above-NUI windows, in one bracket. A frame whose GUI cannot enter the bracket still
    /// shows the windows, replayed in the engine's context -- unless this is the render thread, which has no engine
    /// context to draw in.</summary>
    internal static void FlushWithWindows(int panels)
    {
        int n = GuiCapture.LayerN;
        bool drawn = _gl.Prog != 0 && !_failed && Draw(0, Math.Clamp(panels, 0, n), n, true);
        UiScene.Spent();
        GuiCapture.Reset();
        if (drawn) return;
        if (n > 0) Dropped();
        if (!GlContextService.HeldHere) { PresentTarget.HandBackIfOwed(); CX.Ui.UiHostService.ReplayWindowsHere(); }
        else if (!_gl.WindowsDropSaid)
        {
            _gl.WindowsDropSaid = true;
            _log?.Always("GuiPass: the render thread's GUI flush could not draw -- CX's own windows are NOT DRAWN on such frames");
        }
    }

    /// <summary>Issue every layer captured since the last flush, then forget them. A flush that cannot run drops what
    /// it holds -- the engine did not draw those scenes either -- and says so once.</summary>
    private static void Flush()
    {
        if (GuiCapture.LayerN == 0) return;
        if (_gl.Prog == 0 || _failed || !Draw(0, GuiCapture.LayerN, GuiCapture.LayerN, false)) Dropped();
        GuiCapture.Reset();
    }

    /// <summary>The screen the GUI draws into, from the engine's own globals: false until they resolve.</summary>
    internal static bool Screen(out int w, out int h)
    {
        w = h = 0;
        if (_screenW == null || _screenH == null) return false;
        w = *_screenW; h = *_screenH;
        return w > 0 && h > 0;
    }

    private static void Dropped()
    {
        if (_dropSaid) return;
        _dropSaid = true;
        _log?.Always("GuiPass: a GUI flush could not enter the 4.6 context -- that frame's GUI is NOT DRAWN");
    }

    /// <summary>Layers <paramref name="from"/>..<paramref name="mid"/>, then (with <paramref name="windows"/>) CX's below-NUI
    /// windows, layers <paramref name="mid"/>..<paramref name="to"/>, and the above-NUI windows, in one bracket.
    /// ponytail: one GL draw per captured record, state changes elided by comparison. A menu is a few hundred draws;
    /// if the GUI ever shows in the frame zones, sort each layer by (VAO, texture, blend) and multi-draw the runs.</summary>
    private static bool Draw(int from, int mid, int to, bool windows)
    {
        int w = *_screenW, h = *_screenH;
        if (PresentTarget.Width == w && PresentTarget.Height == h) PresentTarget.TakeBack();
        else Resize(w, h);
        if (!GlContextService.Enter()) return false;
        // On a frame with no world, THIS is the only 4.6 bracket there is -- and entering it stands
        // GlContextService's own health bracket down for the frame, so the deferred work runs here: at the main
        // menu, skipping it left the mesh arena's VAO unbuilt and the texture registry's uploads unrun.
        GlContextService.RunInBracketHere(); Db.MeshArena.Pump();
        CX.Services.Nui.NuiModelView.Pump();
        if (Renderer.OnVulkan && !GuiParity.Active) return VulkanFlush(from, mid, to, windows);
        if (Renderer.OnVulkan) GuiParity.DrawVulkan(from, mid, to, _log);
        if (to > from) Upload();
        bool gpuTimed = CX.Services.Perf.FrameProbe.GpuBegin();
        long modelSpan = 0;
        uint baseFbo = Target();
        Preamble(baseFbo);
        Issue(from, mid, baseFbo, ref modelSpan);
        if (windows) { Windows(CX.Ui.UiLayer.BelowNui, baseFbo); Preamble(baseFbo); }
        Issue(mid, to, baseFbo, ref modelSpan);
        if (windows) Windows(CX.Ui.UiLayer.AboveNui, baseFbo);
        CX.Services.Perf.FrameProbe.Lap(ref modelSpan, false);
        Gl.BindVertexArray(0);
        Gl.DepthRange(0.0, 1.0);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        GuiParity.ReadGlAndReport(_log);   // the A/B's GL half: the composite, in the context it was imported into
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (gpuTimed) CX.Services.Perf.FrameProbe.GpuEnd();
        GlContextService.Leave();
        PresentTarget.HandBack();
        _flushes++;
        if (!_live) { _live = true; _log?.Info($"GuiPass: LIVE -- {GuiCapture.LayerN} GUI scene(s), {GuiCapture.DrawN} draw(s) this flush, none reached the engine's GL"); }
        return true;
    }

    /// <summary>The flush under <c>alloy.backend vk</c>: <see cref="GuiPassVk"/> records and submits it, and this
    /// closes the bracket and hands the composite back as the GL path does.</summary>
    private static bool VulkanFlush(int from, int mid, int to, bool windows)
    {
        bool ok = _log != null && GuiPassVk.Flush(from, mid, to, windows, _log);
        GlContextService.Leave();
        PresentTarget.HandBack();
        if (!ok) return false;
        _flushes++;
        if (!_live) { _live = true; _log?.Info($"GuiPass: LIVE on Vulkan -- {GuiCapture.LayerN} GUI scene(s), {GuiCapture.DrawN} draw(s) this flush"); }
        return true;
    }

    /// <summary>The state every captured draw assumes, set at the flush's start and again after a window replay.</summary>
    private static void Preamble(uint baseFbo)
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, baseFbo);
        Gl.Disable(Gl.FRAMEBUFFER_SRGB);
        Gl.Disable(Gl.MULTISAMPLE);
        Gl.Disable(Gl.STENCIL_TEST);
        FrameClear();
        Gl.Enable(Gl.SCISSOR_TEST);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.FrontFace(Gl.CCW);
        Gl.CullFace(Gl.BACK);
        TextureUnitZero();
        Gl.UseProgram(_gl.Prog);
        Gl.Uniform1i(_gl.LocBoneStart, GuiCapture.DrawN * Vec4PerRec);
        Gl.Uniform1i(_gl.LocGamma, PostChain.GammaOff() ? 0 : 1);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindGui, _gl.Buf);
        // The master palette, for the PLT item icons a GUI control draws. A no-op until a palette exists, and the unit is nothing else's.
        Db.PltPalettes.Bind();
    }

    /// <summary>One layer of CX's recorded windows over the whole screen: display space, after the world's composite,
    /// so no tonemap or TAA history touches it. The replay is GL's on either backend, which is why the Vulkan flush
    /// calls it between its own submits -- the composite image is GL's again after each.</summary>
    internal static void WindowsOn(CX.Ui.UiLayer layer, uint baseFbo) => Windows(layer, baseFbo);

    /// <inheritdoc cref="WindowsOn"/>
    private static void Windows(CX.Ui.UiLayer layer, uint baseFbo)
    {
        CX.Ui.UiDrawList l = CX.Ui.UiHostService.Windows(layer);
        if (l.CmdN == 0) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, baseFbo);
        Gl.Viewport(0, 0, l.ViewW, l.ViewH);
        Gl.DepthRange(0.0, 1.0);
        Gl.FrontFace(Gl.CCW);
        CX.Ui.TextRig.Replay(l);
    }

    /// <summary>Captured layers <paramref name="from"/>..<paramref name="to"/>, each into its own target; ends on
    /// <paramref name="baseFbo"/>. Every state cache starts unknown, so a replay between two runs costs nothing here.</summary>
    private static void Issue(int from, int to, uint baseFbo, ref long modelSpan)
    {
        int envTex = 0;
        int blend = -1, depth = -1, write = -1, cull = -1;
        uint src = 0, dst = 0, vao = uint.MaxValue;
        int tex = -1;
        uint prog = _gl.Prog;
        bool gamma = !PostChain.GammaOff();
        bool released = false;
        uint curTarget = 0;
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
        if (curTarget != 0) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, baseFbo); Gl.FrontFace(Gl.CCW); }
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

    /// <summary>Select texture unit 0, which every per-draw bind and every GUI sampler assumes. Whatever ran before the
    /// flush may leave another active (a module edge has no world frame ahead of it); that is counted and said once.</summary>
    private static void TextureUnitZero()
    {
        int active;
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &active);
        if (active == (int)Gl.TEXTURE0) return;
        if (_gl.StrayUnit++ == 0)
        {
            _gl.StrayUnitFirst = active - (int)Gl.TEXTURE0;
            _log?.Always($"GuiPass: the flush found texture unit {_gl.StrayUnitFirst} active, not 0 -- every GUI part "
                       + "would have bound its texture there and sampled whatever sat on unit 0; unit 0 selected");
        }
        Gl.ActiveTexture(Gl.TEXTURE0);
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
        fixed (GuiRec* r = GuiCapture.Records) Gl.NamedBufferSubData(_gl.Buf, IntPtr.Zero, (IntPtr)rec, r);
        if (bones > 0) fixed (BonePalette* b = GuiCapture.Bones) Gl.NamedBufferSubData(_gl.Buf, (IntPtr)rec, (IntPtr)bones, b);
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
    internal const uint LocColor = 3;

    private static void Attr(uint vao, uint loc, int size, uint type, byte norm, int offset)
    {
        Gl.EnableVertexArrayAttrib(vao, loc);
        Gl.VertexArrayAttribFormat(vao, loc, size, type, norm, (uint)offset);
        Gl.VertexArrayAttribBinding(vao, loc, 0);
    }

    /// <summary>The GUI's depth attachment format -- Alloy's own choice, not the window's. Packed because the
    /// captured layers clear stencil beside depth; nothing in the pass tests it.</summary>
    private const uint GuiDepthFormat = Gl.DEPTH24_STENCIL8;

    /// <summary>The GUI framebuffer: the present texture for colour and Alloy's own depth beside it, rebuilt when
    /// the texture is. 0 = no present image, so the flush writes the default framebuffer instead.</summary>
    private static uint Target()
    {
        uint tex = PresentTarget.Tex;
        if (tex == 0) return 0;
        if (_gl.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f; }
        if (_gl.Depth == 0 || _gl.DepthW != PresentTarget.Width || _gl.DepthH != PresentTarget.Height)
        {
            RenderTarget.AllocDepth(ref _gl.Depth, GuiDepthFormat, PresentTarget.Width, PresentTarget.Height);
            _gl.DepthW = PresentTarget.Width; _gl.DepthH = PresentTarget.Height;
        }
        // Re-attached every flush, as PresentTarget does: GL reuses freed names across a resize.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        // DEPTH_STENCIL_ATTACHMENT, which is legal only for a packed format: the pair above is the one thing
        // a change to GuiDepthFormat has to keep, and an incomplete framebuffer draws NOTHING -- so it is
        // asked of the driver once rather than assumed.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.TEXTURE_2D, _gl.Depth, 0);
        if (!_gl.FboChecked)
        {
            _gl.FboChecked = true;
            uint st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            if (st != Gl.FRAMEBUFFER_COMPLETE)
                _log?.Always($"GuiPass: the GUI framebuffer is INCOMPLETE (0x{st:x}) over the {PresentTarget.Width}x"
                           + $"{PresentTarget.Height} present image -- the game's GUI panels are NOT DRAWN");
            else _log?.Info($"GuiPass: drawing into the present image, {PresentTarget.Width}x{PresentTarget.Height} "
                          + $"colour with a depth of Alloy's own (0x{GuiDepthFormat:x})");
        }
        return _gl.Fbo;
    }

    /// <summary>The present image is missing or the wrong size (a frame before any world frame, or a resize):
    /// build it in the 4.6 context, then take the screen back into it.</summary>
    private static void Resize(int w, int h)
    {
        if (!GlContextService.Enter()) return;
        PresentTarget.Ensure(w, h);
        GlContextService.Leave();
        PresentTarget.TakeBack();
    }

    /// <summary>GL names die with the context.</summary>
    internal static void ContextLost()
    {
        CX.Ui.TextRig.ContextLost();   // the windows' VAO and programs went with it
        UiScene.ContextLost();
        TextAtlas.ContextLost();   // CX-119: every font atlas went with the context
        GuiModelSolve.ContextLost();
        GuiPassVk.ContextLost();   // the Vulkan device went with the context, and every image and program on it
        _gl = new GuiGlState();
    }

    internal static string Stats() => !_armed ? "gui: not armed" : _failed ? "gui: NOT DRAWN (FAILED)"
        : !Renderer.Gui.V ? "gui: off (/alloy gui 0, the game's panels are NOT DRAWN)" : _gl.Prog == 0 ? "gui: waiting for the 4.6 context"
        : $"gui: Alloy -- {_flushes} flushes, {_drawn} draws" + (_noVao == 0 ? "" : $", {_noVao} with no VAO (NOT DRAWN)")
          + (_gl.StrayUnit == 0 ? "" : $", {_gl.StrayUnit} flush(es) found texture unit {_gl.StrayUnitFirst} active")
          + "; " + GuiCapture.Stats() + "; " + ScreenOwner.Stats() + "; " + PresentTarget.Stats();
}
