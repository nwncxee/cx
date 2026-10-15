using System;
using System.Collections.Generic;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>A GUI part whose <c>.mtr</c> names a content program (<c>customShaderVS</c> + <c>customShaderFS</c>) --
/// how a server's load screen resizes its art and moves its bar. The pair is interned to a key the recipe keeps,
/// built on its first draw in the 4.6 context the way the engine builds it, and fed the engine's uniforms per draw.</summary>
internal static unsafe class GuiContent
{
    private sealed class KeyState
    {
        public readonly List<(string Vs, string Fs)> Pairs = new() { ("", "") };
        public readonly Dictionary<(string, string), int> ByPair = new();
    }

    private static KeyState _keys = new();

    /// <summary>One built content program and the engine uniforms it declares (-1 = not declared).</summary>
    internal sealed class Prog
    {
        public uint Id;
        public int Mvp, Mv, Texture, Color, Screen, Unit0;
    }

    /// <summary>The engine's attribute locations (<c>GLRender::ConnectShaderData</c>) for the streams the mesh
    /// arena's VAO carries, at the locations it carries them. <c>vCustomColor</c> has no stream: location 4
    /// is never enabled, so it reads the GL default.</summary>
    private static readonly (string, uint)[] Attribs =
    {
        ("vPos", 0), ("vTcIn", 1), ("vNormal", 2), ("vColor", 3), ("vCustomColor", 4), ("vIndex", 8), ("vWeight", 9),
    };

    /// <summary><c>LightManager::m_nMaxLights</c>' static default. ponytail: the options can change it; a GUI program
    /// lights nothing, so it only sizes arrays nobody fills. Read the live value if a lit GUI shader ever shows up.</summary>
    private const int MaxLights = 32;

    /// <summary>The key for a material's content pair: 0 when it names no vertex AND fragment stage. Cold.</summary>
    internal static int Key(Db.MtrDef? m)
    {
        if (m == null || m.ShaderVs.Length == 0 || m.ShaderFs.Length == 0) return 0;
        var pair = (m.ShaderVs.ToLowerInvariant(), m.ShaderFs.ToLowerInvariant());
        lock (_keys)
        {
            if (_keys.ByPair.TryGetValue(pair, out int k)) return k;
            k = _keys.Pairs.Count;
            _keys.Pairs.Add(pair);
            _keys.ByPair[pair] = k;
            return k;
        }
    }

    /// <summary>The text <c>GLRender</c> prepends to every content stage before it compiles it, values and all.</summary>
    internal static string Prologue(bool fragment, bool fragColor, int gamma, int fragLighting, int quality, int keyhole,
                                    int debug, int post)
        => "#version 330 core\n#define mediump\n#define lowp\n#define highp\n"
         + $"#define MAX_NUM_LIGHTS {MaxLights}\n#define MAX_NUM_BONES 64\n#define GAMMA_CORRECTION {gamma}\n"
         + $"#define FRAGMENT_LIGHTING {fragLighting}\n#define SHADER_QUALITY_MODE {quality}\n"
         + $"#define KEYHOLING_ENABLED {keyhole}\n#define SHADER_DEBUG_MODE {debug}\n"
         + "#define BUILD_VERSION 8193\n#define BUILD_REVISION 37\n#define NO_DISCARD 0\n"
         + $"#define POSTPROCESSING_TYPES_ENABLED {post}\n#define varying {(fragment ? "in" : "out")}\n"
         + "#define attribute in\n#define texture2D texture\n#define textureCube texture\n"
         + (fragColor ? "#define gl_FragColor compat_glFragColor\nout vec4 compat_glFragColor;" : "") + "\n";

    /// <summary>Build key <paramref name="key"/>'s program in the current (4.6) context. Null when it cannot be built,
    /// said loudly once per pair: those parts then draw through Alloy's own GUI program, untransformed.</summary>
    internal static Prog? Build(int key, ExtenderLog? log)
    {
        (string vs, string fs) = Pair(key);
        string what = $"GuiContent {vs}/{fs}";
        string? v = VanillaShader.ContentStage(vs, log, what), f = VanillaShader.ContentStage(fs, log, what);
        if (v == null || f == null)
        {
            log?.Always($"{what}: {(v == null ? vs : fs)}.shd is not in the resource chain (or alloy.vanillabridge=0) -- "
                      + "these GUI parts draw WITHOUT their content shader");
            return null;
        }
        RiGetGlobalShaderParamiFn gp = EngineFn.RiGetGlobalShaderParami;
        int P(uint i) => gp == null ? 0 : gp(i);
        string pre(bool frag, string src) => Prologue(frag, frag && src.Contains("gl_FragColor", StringComparison.Ordinal),
                                                      P(0), P(1), P(7), P(8), P(10), P(2));
        uint id = GlProgram.Content(pre(false, v) + v, pre(true, f) + f, Attribs, log, what);
        if (id == 0)
        {
            log?.Always($"{what}: the content program did not build (the line above says why) -- these GUI parts draw "
                      + "WITHOUT their content shader");
            return null;
        }
        log?.Info($"{what}: built -- GUI parts on this material run the content's own stages");
        return new Prog
        {
            Id = id,
            Mvp = GlProgram.Loc(id, "m_mvp"), Mv = GlProgram.Loc(id, "m_mv"), Texture = GlProgram.Loc(id, "m_texture"),
            Color = GlProgram.Loc(id, "globalColor"), Screen = GlProgram.Loc(id, "screenParams"),
            Unit0 = GlProgram.Loc(id, "texUnit0"),
        };
    }

    private static (string, string) Pair(int key)
    {
        lock (_keys) return (uint)key < (uint)_keys.Pairs.Count ? _keys.Pairs[key] : ("", "");
    }

    /// <summary>Upload record <paramref name="di"/>'s engine terms to the bound program <paramref name="p"/>.
    /// <c>screenParams</c> is the view's size (<c>GLRender::m_viewport</c> w, h), as the engine feeds it. Hot.</summary>
    internal static void Feed(Prog p, int di, int viewW, int viewH, bool gamma)
    {
        fixed (float* r = &GuiCapture.Records[di * GuiCapture.RecFloats])
        {
            Gl.UniformMatrix4fv(p.Mvp, 1, 0, r);
            Gl.UniformMatrix4fv(p.Mv, 1, 0, &r[16]);
            float* t = stackalloc float[16];
            TextureMatrix(&r[32], t);
            Gl.UniformMatrix4fv(p.Texture, 1, 0, t);
            float* g = &r[36];
            Gl.Uniform4f(p.Color, Lin(g[0], gamma), Lin(g[1], gamma), Lin(g[2], gamma), g[3]);
        }
        Gl.Uniform2f(p.Screen, viewW, viewH);
        Gl.Uniform1i(p.Unit0, 0);
    }

    /// <summary>The record's texture 2x2 <c>(a, b, c, d)</c> -- <c>u' = a*u + b*v</c>, <c>v' = c*u + d*v</c> -- as
    /// the column-major <c>m_texture</c> whose <c>mat2</c> and <c>vec4(u, v, 0, 1)</c> products give the same.</summary>
    internal static void TextureMatrix(float* tm, float* m)
    {
        for (int i = 0; i < 16; i++) m[i] = 0f;
        m[0] = tm[0]; m[1] = tm[2]; m[4] = tm[1]; m[5] = tm[3]; m[10] = 1f; m[15] = 1f;
    }

    /// <summary>The engine's <c>GetColorSpaceFromSRGB</c> on a uniform colour while gamma correction is on.</summary>
    private static float Lin(float c, bool gamma) => gamma ? MathF.Sign(c) * MathF.Pow(MathF.Abs(c), 2.2f) : c;

    /// <summary>Headless. Oracles: the engine's prologue text as <c>GLRender</c>'s format string has it (transcribed
    /// by hand), the texture product worked by hand for a known 2x2, and a load screen's <c>.mtr</c> as shipped.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GuiContent.SelfCheck");
        string fsp = Prologue(true, true, 1, 1, 2, 1, 0, 3);
        check.Expect(fsp.StartsWith("#version 330 core\n#define mediump\n#define lowp\n#define highp\n", StringComparison.Ordinal),
                     "the prologue opens with the engine's version and precision lines");
        check.Expect(fsp.Contains("#define varying in\n#define attribute in\n#define texture2D texture\n#define textureCube texture\n",
                                  StringComparison.Ordinal), "a fragment stage reads its varyings in");
        check.Expect(fsp.Contains("#define gl_FragColor compat_glFragColor\nout vec4 compat_glFragColor;", StringComparison.Ordinal),
                     "a stage writing gl_FragColor gets the engine's compat output");
        check.Expect(fsp.Contains("#define SHADER_QUALITY_MODE 2\n", StringComparison.Ordinal), "the params land in their own lines");
        string vsp = Prologue(false, false, 0, 0, 0, 0, 0, 0);
        check.Expect(vsp.Contains("#define varying out\n", StringComparison.Ordinal), "a vertex stage writes its varyings out");
        check.Expect(!vsp.Contains("compat_glFragColor", StringComparison.Ordinal), "...and declares no fragment output");

        float* tm = stackalloc float[4] { 2f, 3f, 5f, 7f };
        float* m = stackalloc float[16];
        TextureMatrix(tm, m);
        float u = 11f, v = 13f;
        float ou = m[0] * u + m[4] * v + m[12], ov = m[1] * u + m[5] * v + m[13];
        check.Expect(ou == 61f && ov == 146f, "m_texture * (11, 13, 0, 1) is (2*11 + 3*13, 5*11 + 7*13)");

        static Db.MtrDef Mtr(string text) => Db.MtrDef.Parse(Encoding.ASCII.GetBytes(text), "selfcheck");
        check.Expect(Key(Mtr("customshadervs ls_vs_loader\n")) == 0,
                     "a material with a vertex stage and no fragment stage has no content key");
        int a = Key(Mtr("// a load screen's bar\ncustomshadervs LS_VS_Loader\ncustomshaderfs ls_fs_loader\n"));
        int b = Key(Mtr("customShaderVS ls_vs_loader\ncustomShaderFS LS_FS_LOADER\n"));
        check.Expect(a > 0 && a == b, "resrefs and keywords are case-blind: one pair, one key");
        log.Info(check.Ok ? "GuiContent.SelfCheck: passed" : "GuiContent.SelfCheck: FAILED");
    }
}
