using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>Proves that <b>the GLSL that ships</b> resolves a PLT the way the CPU does, which comparing C#
/// against C# cannot: renders <c>cx_plt.glsl</c>'s own <c>CxPltResolve</c>, reads the pixels back through
/// <see cref="GpuReadback"/> (never <c>glGetTexImage</c>: NVIDIA waits out the whole queued frame) and
/// compares them to <see cref="PltImage.Resolve"/>. Once per session behind <c>alloy.pltverify</c>.</summary>
internal static unsafe class PltGpuCheck
{
    /// <summary>Side of the readback target: the first 16x16 texels of the PLT, 256 comparisons, one cold
    /// draw. The target is square and the shader takes <c>uv = gl_FragCoord.xy / vec2(pltDim)</c>, so pixel
    /// (x, y) lands on the CENTRE of texel (x, y) -- bilinear weights 0, LOD 0 -- which makes any difference
    /// one of indexing, wrap, rounding or palette row, never filtering.</summary>
    private const int Side = 16;

    private enum Phase : byte { Idle, Armed, Rendered, Done }

    private static ExtenderLog? _log;
    private static Phase _phase;
    private static TexId _tex;
    private static readonly ushort[] _rows = new ushort[SceneDb.PltLayers];
    private static string _name = "";
    private static uint _prog, _fbo, _target, _pbo, _vao;
    private static GpuReadback _ring;
    private static int _slot = -1, _waited;
    /// <summary>Brackets the check will wait for its inputs before giving up and saying so. A texture load is
    /// a file read and an upload budget away, so a couple of hundred frames is patience, not a hang.</summary>
    private const int WaitFrames = 240;
    /// <summary>The record slot the check stages its rows into: the top of the block, past every ownership
    /// slot and past the sky's reserved draws, so it cannot collide with a real record.</summary>
    private static int _recSlot;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Arm the check on a PLT <see cref="PltVerify"/> has just proved on the CPU, with the scheme rows
    /// it read off the engine. Takes a reference on the texture; the check releases it when it is done.
    /// Game thread.</summary>
    internal static void Arm(string resref, ReadOnlySpan<ushort> rows)
    {
        if (_phase != Phase.Idle || !PltVerify.On) return;
        _tex = TexRegistry.Acquire(resref, TexKind.Plt);
        if (_tex.IsNull)
        {
            _log?.Always($"PltGpuCheck: the registry would not take \"{resref}\" -- the GPU resolve is not checked");
            _phase = Phase.Done;
            return;
        }
        rows[..Math.Min(rows.Length, _rows.Length)].CopyTo(_rows);
        _name = resref;
        _recSlot = AlloyRenderer.MaxRecords - 1;
        _phase = Phase.Armed;
        _log?.Info($"PltGpuCheck: armed on \"{resref}\" -- the GPU's own CxPltResolve will be compared against "
                 + "PltImage.Resolve once it is resident");
    }

    /// <summary>In Alloy's bracket, after <c>TexRegistry.Drain</c> and the palette upload: render when the
    /// texture is there, read back on a later frame. Never waits.</summary>
    internal static void Tick()
    {
        if (Renderer.OnVulkan && _phase is Phase.Armed or Phase.Rendered)
        {
            _phase = Phase.Done;
            _log?.Always("PltGpuCheck: NOT RUN on Vulkan -- its resolve draw and read-back are GL's; the GPU resolve is not checked");
            return;
        }
        switch (_phase)
        {
            case Phase.Armed: Render(); break;
            case Phase.Rendered: Read(); break;
        }
    }

    private static void Render()
    {
        if (!TexRegistry.TryResident(_tex, out uint plt, out _))
        {
            if (TexRegistry.StateOf(_tex) == TexState.Failed)
            { Fail($"the registry could not load \"{_name}\""); return; }
            if (++_waited > WaitFrames) Fail($"\"{_name}\" was still not resident after {_waited} frames");
            return;
        }
        // The master palette is the probe's other input, and when Alloy is off nothing else needs it -- so
        // build it here. PltVerify has already read the seven slots on the game thread, and this is the
        // context the upload belongs in.
        if (!PltPalettes.Ready) PltPalettes.Create();
        if (!PltPalettes.Ready)
        {
            if (++_waited > WaitFrames)
                Fail(PltPalettes.Loaded ? "the master palette would not upload"
                                        : "no palette slot was ever loaded, so there is nothing to resolve against");
            return;
        }
        if (!GpuReadback.EntryPoints || Gl.ReadPixels == null || Gl.NamedBufferStorage == null)
        { Fail("the device has no persistent mapping or no glReadPixels"); return; }

        int w = 0, h = 0;
        Gl.GetTextureLevelParameteriv(plt, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTextureLevelParameteriv(plt, 0, Gl.TEXTURE_HEIGHT, &h);
        if (w < Side || h < Side) { Fail($"\"{_name}\" is {w}x{h}, smaller than the {Side}x{Side} probe"); return; }

        if (_prog == 0)
        {
            _prog = GlProgram.Graphics(ShaderSource.Get("cx_fullscreen_v.glsl"), Fs(), _log, "plt gpu check",
                                       host: false, content: false);
            if (_prog == 0) { Fail("the probe program did not build"); return; }
        }
        if (_target == 0)
        {
            uint t; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
            if (t == 0) { Fail("glCreateTextures gave no probe target"); return; }
            _target = t;
            Gl.TextureStorage2D(_target, 1, Gl.RGBA8, Side, Side);
            Gl.TextureParameteri(_target, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
            Gl.TextureParameteri(_target, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
            uint f; Gl.GenFramebuffers(1, &f);
            _fbo = f;
            int svBindForAttach = 0;
            Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &svBindForAttach);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _target, 0);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)svBindForAttach);
            uint b; Gl.CreateBuffers(1, &b);
            _pbo = b;
            Gl.NamedBufferData(_pbo, (IntPtr)(Side * Side * 4), null, Gl.DYNAMIC_DRAW);
            if (!_ring.Ensure(Side * Side * 4)) { Fail("the readback ring would not map"); return; }
            uint v; Gl.CreateVertexArrays(1, &v);
            _vao = v;
        }

        // The rows the record carries, and the flag that says this draw is a PLT: staged through the real
        // column and the real packing, so the check exercises what a creature exercises.
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) { Fail("the object table would not allocate a probe record"); return; }
        var ints = new int[SceneDb.PltLayers];
        for (int l = 0; l < ints.Length; l++) ints[l] = _rows[l];
        PltColors.SetRows(obj, ints);
        PltColors.Stage(_recSlot, obj, clamp: TexRegistry.Clamps(_tex));
        PltColors.Upload();
        PltPalettes.Bind();

        int svFbo = 0, svVp0 = 0, svVp1 = 0, svVp2 = 0, svVp3 = 0;
        Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &svFbo);
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        svVp0 = vp[0]; svVp1 = vp[1]; svVp2 = vp[2]; svVp3 = vp[3];
        Gl.DrainErrors(8);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.Viewport(0, 0, Side, Side);
        Gl.UseProgram(_prog);
        Gl.BindTextureUnit(0, plt);
        Gl.Uniform1i(GlProgram.Loc(_prog, "uPltTex"), 0);
        Gl.Uniform1i(GlProgram.Loc(_prog, "uDraw"), _recSlot);
        Gl.Uniform2i(GlProgram.Loc(_prog, "uDim"), w, h);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Disable(Gl.BLEND);
        Gl.BindVertexArray(_vao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        // glReadPixels INTO a pixel-pack buffer is a GPU-side copy, not a stall; the ring then takes it
        // buffer-to-buffer and a fence says when it has landed (no glGetBufferSubData -- see the memory note).
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, _pbo);
        Gl.ReadPixels(0, 0, Side, Side, Gl.RGBA_, Gl.UNSIGNED_BYTE, null);
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        _slot = 0;
        _ring.Copy(_pbo, 0, _slot, 0, Side * Side * 4);
        _ring.Fence(_slot);
        uint err = Gl.GetError();
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)svFbo);
        Gl.Viewport(svVp0, svVp1, svVp2, svVp3);
        PltColors.Clear(_recSlot);
        SceneDb.Objects.Free(obj);
        if (err != 0) { Fail($"the probe draw raised 0x{err:x}"); return; }
        _waited = 0;
        _phase = Phase.Rendered;
    }

    private static void Read()
    {
        byte* px = _ring.Ready(_slot);
        if (px == null) { if (++_waited > WaitFrames) Fail("the readback fence never signalled"); return; }
        if (!TexRegistry.TryResident(_tex, out uint plt, out _)) { Fail("the texture went while the probe was in flight"); return; }

        int w = 0;
        Gl.GetTextureLevelParameteriv(plt, 0, Gl.TEXTURE_WIDTH, &w);
        // The pairs the GPU sampled, read once at the small end: Side*Side texels of one level, which is the
        // only readback in this check that is not through the ring and is 256 texels wide.
        byte* pairs = stackalloc byte[Side * Side * 2];
        Gl.GetTextureSubImage(plt, 0, 0, 0, 0, Side, Side, 1, Gl.RG, Gl.UNSIGNED_BYTE, Side * Side * 2, pairs);
        ReadOnlySpan<byte> master = PltPalettes.Master;

        int bad = 0, firstAt = -1;
        uint gotFirst = 0, wantFirst = 0;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int i = y * Side + x;
                uint want = PltImage.Resolve(master, _rows, pairs[i * 2], pairs[i * 2 + 1]);
                // The framebuffer's row 0 is the bottom one, and gl_FragCoord.y counts from there too, so the
                // readback row order matches the texel order the shader used.
                uint got = (uint)(px[i * 4] | (px[i * 4 + 1] << 8) | (px[i * 4 + 2] << 16) | (px[i * 4 + 3] << 24));
                if (got == want) continue;
                if (bad++ == 0) { firstAt = i; gotFirst = got; wantFirst = want; }
            }
        if (bad == 0)
            _log?.Always($"PltGpuCheck: PASS \"{_name}\" -- cx_plt.glsl's CxPltResolve matches PltImage.Resolve "
                       + $"on all {Side * Side} probed texels, on the GPU");
        else
            _log?.Always($"PltGpuCheck: FAIL \"{_name}\" -- {bad} of {Side * Side} probed texels differ; first at "
                       + $"{firstAt} (pair {pairs[firstAt * 2]},{pairs[firstAt * 2 + 1]}): GPU 0x{gotFirst:x8}, "
                       + $"CPU 0x{wantFirst:x8}. The shipped GLSL and the CPU reference disagree");
        Done();
    }

    private static void Fail(string why)
    {
        _log?.Always($"PltGpuCheck: SKIPPED -- {why}");
        Done();
    }

    private static void Done()
    {
        _phase = Phase.Done;
        if (_prog != 0 && Gl.DeleteProgram != null) Gl.DeleteProgram(_prog);
        if (_fbo != 0 && Gl.DeleteFramebuffers != null) { uint f = _fbo; Gl.DeleteFramebuffers(1, &f); }
        if (_target != 0 && Gl.DeleteTextures != null) { uint t = _target; Gl.DeleteTextures(1, &t); }
        if (_pbo != 0 && Gl.DeleteBuffers != null) { uint b = _pbo; Gl.DeleteBuffers(1, &b); }
        if (_vao != 0 && Gl.DeleteVertexArrays != null) { uint v = _vao; Gl.DeleteVertexArrays(1, &v); }
        _ring.Release();
        _prog = _fbo = _target = _pbo = _vao = 0;
        TexRegistry.Release(_tex);
        _tex = default;
    }

    internal static void ContextLost()
    {
        _ring.ContextLost();
        _prog = _fbo = _target = _pbo = _vao = 0;
        if (_phase == Phase.Rendered) _phase = Phase.Armed;
    }

    /// <summary>The probe's fragment stage: the shipped library, called the way the lit stage calls it.</summary>
    private static string Fs()
        => "#version 460 core\n"
         + "out vec4 frag;\n"
         + "uniform sampler2D uPltTex;\n"
         + "uniform int uDraw;\n"
         + "uniform ivec2 uDim;\n"
         + ShaderSource.Get("cx_plt.glsl") + "\n"
         + "void main() { frag = CxPltResolve(uPltTex, gl_FragCoord.xy / vec2(uDim), 0.0, uDraw); }\n";
}
