using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Services.Nui;

/// <summary>
/// The model view with Alloy off: the engine draws the doll's scene itself, so this wraps <c>Scene::RenderOnlyGobs</c> for that one
/// scene -- the same call character creation's preview goes through -- with a framebuffer of ours bound, then copies the
/// result, flipped, into the NUI image's texture. GL state the engine caches (viewport, scissor, depth mask) is put back.
/// </summary>
internal static unsafe class NuiModelEngine
{
    private const uint Rgba8 = 0x8058;   // not-an-offset: GL enums
    private const uint ColorBufferBit = 0x4000, ReadBinding = 0x8CAA;   // not-an-offset: GL enums

    private sealed class EngineGlState
    {
        public SceneRenderOnlyGobsFn Orig;
        public ExtenderLog? Log;
        public uint Fbo, FboDst, Color, Depth;
        public int W, H;
        public bool Armed, Said;
    }

    private static readonly EngineGlState _e = new();

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _e.Log = log;
        if (CX.Alloy.Renderer.On) return;
        _e.Orig = hooks.InstallSceneRenderOnlyGobs(&OnRender);
        _e.Armed = _e.Orig != null;
        log.Always(_e.Armed ? "NuiModelEngine: armed -- Scene::RenderOnlyGobs is wrapped for the model view's scene (Alloy is off)"
                            : "NuiModelEngine: Scene::RenderOnlyGobs did not hook -- no model views with Alloy off");
    }

    [UnmanagedCallersOnly]
    private static void OnRender(IntPtr scene)
    {
        if (scene == NuiModelDoll.Doll.Scene) NuiModelDoll.KeepShown();
        uint tex = NuiModelDoll.Redirect;
        if (tex == 0 || scene != NuiModelDoll.Doll.Scene || !NuiModelDoll.TargetSize(out int w, out int h) || !Gl.Init(_e.Log!)) { _e.Orig(scene); return; }
        EngineGlState e = _e;
        int fbo, bound, scissor, mask, rd, dr;
        Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fbo);
        Gl.GetIntegerv(ReadBinding, &rd);
        dr = fbo;
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &bound);
        Gl.GetIntegerv(Gl.SCISSOR_TEST, &scissor);
        Gl.GetIntegerv(Gl.DEPTH_WRITEMASK, &mask);
        Ensure(e, w, h);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, e.Fbo);
        Gl.Disable(Gl.SCISSOR_TEST);
        float* c = stackalloc float[4];
        NuiModelDoll.Background(c);
        Gl.ClearBufferfv(Gl.COLOR, 0, c);
        Gl.DepthMask(1);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
        Gl.DepthMask((byte)mask);
        if (scissor != 0) Gl.Enable(Gl.SCISSOR_TEST);
        e.Orig(scene);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, e.FboDst);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, e.Fbo);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, e.FboDst);
        Gl.BlitFramebuffer(0, 0, w, h, 0, h, w, 0, ColorBufferBit, (uint)Gl.NEAREST);
        if (scissor != 0) Gl.Enable(Gl.SCISSOR_TEST);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)rd);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, (uint)dr);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)fbo);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)bound);
        NuiModelDoll.MarkRendered();
        if (!e.Said) { e.Said = true; e.Log?.Always($"NuiModelEngine: first frame drawn into texture {tex} ({w}x{h})"); }
    }

    /// <summary>The scratch colour and depth the engine draws into, and the two framebuffers; remade when the size changes.</summary>
    private static void Ensure(EngineGlState e, int w, int h)
    {
        if (e.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); e.Fbo = f; Gl.GenFramebuffers(1, &f); e.FboDst = f; }
        if (e.Color != 0 && e.W == w && e.H == h) return;
        uint t;
        if (e.Color != 0) { uint[] old = { e.Color, e.Depth }; fixed (uint* o = old) Gl.DeleteTextures(2, o); }
        Gl.GenTextures(1, &t); e.Color = t;
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Rgba8, w, h, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.GenTextures(1, &t); e.Depth = t;
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.DEPTH24_STENCIL8, w, h, 0, Gl.DEPTH_STENCIL, Gl.UNSIGNED_INT_24_8, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, e.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, e.Color, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.TEXTURE_2D, e.Depth, 0);
        e.W = w; e.H = h;
        uint st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        e.Log?.Always($"NuiModelEngine: scratch target {w}x{h}: framebuffer status 0x{st:x}" + (st == Gl.FRAMEBUFFER_COMPLETE ? "" : " -- INCOMPLETE"));
    }
}
