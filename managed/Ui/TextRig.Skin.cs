using System.Numerics;
using CX.Graphics;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>Skin program: the icon program keeps texture alpha but drops the vertex colour, so it cannot tint;
    /// this one multiplies both, and the batch alpha.</summary>
    private static uint _progSkin;
    private static int _uProjSkin, _uTexSkin, _uAlphaSkin;
    private static bool _progSkinUp;
    private const string FSrcSkin =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nuniform float uAlpha;\nout vec4 o;\n" +
        "void main(){ o = texture(uTex, vUV) * vec4(vCol, vA * uAlpha); }\n";

    private static bool EnsureSkinProgram()
    {
        if (_progSkinUp) return _progSkin != 0;
        _progSkinUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrcSkin);
        if (vs == 0 || fs == 0) return false;
        _progSkin = Gl.CreateProgram();
        Gl.AttachShader(_progSkin, vs); Gl.AttachShader(_progSkin, fs); Gl.LinkProgram(_progSkin);
        int ok; Gl.GetProgramiv(_progSkin, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: skin program link failed"); _progSkin = 0; return false; }
        _uProjSkin = UniformOf(_progSkin, "uProj"); _uTexSkin = UniformOf(_progSkin, "uTex"); _uAlphaSkin = UniformOf(_progSkin, "uAlpha");
        _log?.Info("TextRig: skin shader program ready");
        return true;
    }

    /// <summary>Level-0 size of a live GL texture, 0x0 when <paramref name="tid"/> is 0. Binds it on unit 0 (the
    /// frame host resyncs the engine on hand-back). Render thread only.</summary>
    public static void TexSize(int tid, out int w, out int h)
    {
        w = h = 0;
        if (tid == 0 || _log == null || !Gl.Init(_log) || Gl.GetTexLevelParameteriv == null) return;
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        int a, b;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &a);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &b);
        w = a; h = b;
    }

    /// <summary>One textured, tinted batch over the engine texture <paramref name="tid"/>: the skin pass's one draw
    /// call per sheet. UVs follow <see cref="DrawIconTid"/>'s convention (engine textures are bottom-up: v = 1 is the
    /// image's top row). Sets its own full GL preamble, taking no state snapshot.</summary>
    public static void DrawSkin(int tid, System.ReadOnlySpan<float> verts, int count, float alpha = 1f,
                                bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (tid == 0 || count == 0 || _sheet == null || _log == null || alpha <= 0f || ViewportW <= 0) return;
        if (!Gl.Init(_log) || !EnsureSkinProgram()) return;

        Gl.ActiveTexture(Gl.TEXTURE0);
        if (!_pvaoUp) SetupPortraitVao();
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        int vh = ViewportH;
        if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, vh - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
        else Gl.Disable(Gl.SCISSOR_TEST);

        Matrix4x4 m = Ortho(ViewportW, vh);
        Gl.UseProgram(_progSkin);
        Gl.UniformMatrix4fv(_uProjSkin, 1, (byte)Gl.FALSE, (float*)&m);
        Gl.Uniform1i(_uTexSkin, 0);
        Gl.Uniform1f(_uAlphaSkin, alpha < 1f ? alpha : 1f);
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        fixed (float* p = verts)
            Gl.BufferData(Gl.ARRAY_BUFFER, (System.IntPtr)(count * Floats * sizeof(float)), p, Gl.DYNAMIC_DRAW);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        bool samp = EnsurePortraitSampler();
        if (samp) Gl.BindSampler(0, _psamp);
        Gl.DrawArrays(Gl.TRIANGLES, 0, count);
        // Unbind is load-bearing: a sampler left on unit 0 would override the glyph atlas's own filter/wrap state.
        if (samp) Gl.BindSampler(0, 0);
        if (clip) Gl.Disable(Gl.SCISSOR_TEST);
    }
}
