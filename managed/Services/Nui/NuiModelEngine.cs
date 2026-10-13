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

    /// <summary>One slot's scratch target: the colour and depth the engine draws its scene into, and the size they were made at.</summary>
    private sealed class Scratch
    {
        public uint Fbo, Color, Depth;
        public int W, H;
    }

    private sealed class EngineGlState
    {
        public SceneRenderOnlyGobsFn Orig;
        public ExtenderLog? Log;
        public uint FboDst;
        public readonly Scratch[] Per = MakePer();
        public bool Armed, Said;
    }

    private static Scratch[] MakePer()
    {
        var a = new Scratch[NuiModelDoll.MaxViews];
        for (int i = 0; i < a.Length; i++) a[i] = new Scratch();
        return a;
    }

    /// <summary>The exact scene name <c>Scene::RenderOnlyGobs</c> compares (<c>strcasecmp</c>, whole string) to light a GUI scene.</summary>
    private static ReadOnlySpan<byte> LitName => "sceneGUI_guiModelView\0"u8;
    /// <summary>The bytes of a scene's name shown to the engine's compare and put back: a name is 63 characters at most.</summary>
    private const int NameSave = 64;   // not-an-offset: the engine's scene name array

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
        NuiModelDoll.ModelSlot? slot = NuiModelDoll.OfScene(scene);
        if (slot == null) { _e.Orig(scene); return; }
        NuiModelDoll.KeepShown(slot);
        uint tex = slot.TargetTex;
        if (tex == 0 || !NuiModelDoll.TargetSize(slot, out int w, out int h) || !Gl.Init(_e.Log!)) { Lit(scene); return; }
        EngineGlState e = _e;
        Scratch sc = e.Per[slot.Index];
        int fbo, bound, scissor, mask, rd, dr;
        Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fbo);
        Gl.GetIntegerv(ReadBinding, &rd);
        dr = fbo;
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &bound);
        Gl.GetIntegerv(Gl.SCISSOR_TEST, &scissor);
        Gl.GetIntegerv(Gl.DEPTH_WRITEMASK, &mask);
        Ensure(e, sc, slot.Index, w, h);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, sc.Fbo);
        Gl.Disable(Gl.SCISSOR_TEST);
        float* c = stackalloc float[4];
        NuiModelDoll.Background(slot, c);
        Gl.ClearBufferfv(Gl.COLOR, 0, c);
        Gl.DepthMask(1);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
        Gl.DepthMask((byte)mask);
        if (scissor != 0) Gl.Enable(Gl.SCISSOR_TEST);
        Lit(scene);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, e.FboDst);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, sc.Fbo);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, e.FboDst);
        Gl.BlitFramebuffer(0, 0, w, h, 0, h, w, 0, ColorBufferBit, (uint)Gl.NEAREST);
        if (scissor != 0) Gl.Enable(Gl.SCISSOR_TEST);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)rd);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, (uint)dr);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)fbo);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)bound);
        NuiModelDoll.MarkRendered(slot);
        if (!e.Said) { e.Said = true; e.Log?.Always($"NuiModelEngine: first frame drawn into texture {tex} ({w}x{h}), slot {slot.Index}"); }
    }

    /// <summary>The engine's own draw of <paramref name="scene"/>, with its name shown as the one name the engine lights. The engine
    /// makes scene names unique, so every doll after the first has a digit on the end and would draw unlit.</summary>
    private static void Lit(IntPtr scene)
    {
        byte* name = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new Scene(scene).Name);
        byte* saved = stackalloc byte[NameSave];
        Buffer.MemoryCopy(name, saved, NameSave, NameSave);
        LitName.CopyTo(new Span<byte>(name, LitName.Length));
        _e.Orig(scene);
        Buffer.MemoryCopy(saved, name, NameSave, NameSave);
    }

    /// <summary>The slot's scratch colour and depth the engine draws into, and the two framebuffers; remade when the size changes.</summary>
    private static void Ensure(EngineGlState e, Scratch k, int slot, int w, int h)
    {
        if (e.FboDst == 0) { uint d; Gl.GenFramebuffers(1, &d); e.FboDst = d; }
        if (k.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); k.Fbo = f; }
        if (k.Color != 0 && k.W == w && k.H == h) return;
        uint t;
        if (k.Color != 0) { uint[] old = { k.Color, k.Depth }; fixed (uint* o = old) Gl.DeleteTextures(2, o); }
        Gl.GenTextures(1, &t); k.Color = t;
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Rgba8, w, h, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.GenTextures(1, &t); k.Depth = t;
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.DEPTH24_STENCIL8, w, h, 0, Gl.DEPTH_STENCIL, Gl.UNSIGNED_INT_24_8, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, k.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, k.Color, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.TEXTURE_2D, k.Depth, 0);
        k.W = w; k.H = h;
        uint st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        e.Log?.Always($"NuiModelEngine: slot {slot} scratch target {w}x{h}: framebuffer status 0x{st:x}" + (st == Gl.FRAMEBUFFER_COMPLETE ? "" : " -- INCOMPLETE"));
    }
}
