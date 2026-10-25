using System;
using System.Collections.Generic;
using CX.Framework;
using Vortice.ShaderCompiler;

namespace CX.Graphics.Rhi;

internal static partial class VkGlsl
{
    private static readonly Dictionary<string, uint> FixtureUnits = new() { ["uTex"] = 0, ["uNrmTex"] = 3, ["uSpecTex"] = 4 };

    private const string LooseVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uVP;
        uniform int uDrawBase;
        uniform vec4 uPlanes[6], uTint;   // a comma list with an array
        out vec2 vUV;
        flat out int vDraw;
        void main() { vDraw = gl_DrawID + uDrawBase; vUV = uPlanes[0].xy + uTint.xy; gl_Position = uVP * vec4(aPos, 1.0); }
        """;

    private const string LooseFs = """
        #version 460 core
        flat in int vDraw;
        in vec2 vUV;
        uniform float uAlphaCut;
        uniform vec4 uTint;
        uniform float uGain = 2.0;
        uniform sampler2D uTex;
        uniform sampler2D uNrmTex, uSpecTex;
        layout(binding = 26) uniform usampler2D uLightRec;
        out vec4 frag;
        void main() { frag = texture(uTex, vUV) * uTint * uGain + texture(uNrmTex, vUV) + texture(uSpecTex, vUV); if (frag.a < uAlphaCut) discard; }
        """;

    private const string BindlessVs = """
        #version 460 core
        flat out int vDraw;
        void main() { vDraw = gl_InstanceID; gl_Position = vec4(0.0); }
        """;

    private const string BindlessFsText = """
        #version 460 core
        #define CX_BINDLESS 1
        #extension GL_ARB_bindless_texture : require
        layout(std430, binding = 3) readonly buffer CxTexH { uvec2 uTexH[]; };
        #define uTex sampler2D(uTexH[(vDraw << 3) + 0])
        flat in int vDraw;
        out vec4 frag;
        void main() { frag = texture(uTex, vec2(0.5)); }
        """;

    /// <summary>A block whose std140 offsets were worked out by hand: a 0, b 16 (a vec3 aligns to 16), m 32 (64 bytes),
    /// arr 96 (stride 16, 48 bytes), c 144, d 152 (a vec2 aligns to 8), 160 bytes in all.</summary>
    private const string LayoutFs = """
        #version 460
        layout(std140, binding = 4) uniform CxLoose { float a; vec3 b; mat4 m; vec4 arr[3]; float c; vec2 d; };
        layout(location = 0) out vec4 o;
        void main() { o = vec4(a) + vec4(b, 1.0) + m[0] + arr[2] + vec4(c, d, 0.0); }
        """;

    /// <summary>The translator's rules on hand-written fixtures. Oracle: the literal declarations each rule must emit,
    /// written out here by hand; off-device, shaderc's verdict on the fixtures' output and the std140 offsets of a
    /// fixture block computed by hand from the specification's alignment rules.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkGlsl.SelfCheck");
        VkProgramText loose = VkGlsl.Translate(LooseVs.Replace("\r\n", "\n"), LooseFs.Replace("\r\n", "\n"), FixtureUnits);
        Rules(check, loose);
        VkProgramText bl = VkGlsl.Translate(BindlessVs.Replace("\r\n", "\n"), BindlessFsText.Replace("\r\n", "\n"), FixtureUnits);
        Bindless(check, bl);
        Errors(check);
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) { check.Skip("on-device: shaderc compiles nothing at boot in a game session"); return; }
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }
        Compiles(check, "the loose fixture", loose);
        Compiles(check, "the bindless fixture", bl);
        Layout(check);
        log.Info(check.Ok ? "VkGlsl.SelfCheck: passed" : "VkGlsl.SelfCheck: FAILED");
    }

    private static void Rules(Checker check, VkProgramText r)
    {
        check.Expect(r.Error == null, $"the loose fixture translates ({r.Error})");
        const string Block = "layout(std140, binding = 4) uniform CxLoose {\n    mat4 uVP;\n    vec4 uPlanes[6];\n    vec4 uTint;\n    float uGain;\n};";
        const string PushBlock = "layout(push_constant) uniform CxBatch { int uDrawBase; float uAlphaCut; int uSsaoOn; float uAoDirect; int uDecal; int uSolid; int uOitGob; };";
        check.Expect(r.Vs.Contains(Block) && r.Fs.Contains(Block), "both stages declare the one loose block: the union in vertex-stage order, the comma list and its array kept, deduped");
        check.Expect(r.Vs.Contains(PushBlock) && r.Fs.Contains(PushBlock), "both stages declare the fixed seven-value push block");
        check.Expect(!r.Vs.Contains("\nuniform ") && !r.Fs.Contains("\nuniform "), "no loose declaration is left at file scope");
        check.Expect(r.Loose.Count == 4 && r.Loose[1] == new VkLoose("vec4", "uPlanes", "[6]"), "four loose members, the array sized [6]; the push values are not among them");
        check.Expect(r.Initializers.Contains("uGain = 2.0"), "a GL initializer is reported for the caller to write");
        check.Expect(r.Vs.Contains("layout(location = 0) out vec2 vUV;") && r.Vs.Contains("layout(location = 1) flat out int vDraw;"),
            "vertex outputs take locations in declaration order, qualifiers kept");
        check.Expect(r.Fs.Contains("layout(location = 1) flat in int vDraw;") && r.Fs.Contains("layout(location = 0) in vec2 vUV;"),
            "fragment inputs take the vertex stage's location by name, whatever their own order");
        check.Expect(r.Fs.Contains("layout(location = 0) out vec4 frag;"), "the only unlocated fragment output takes location 0");
        check.Expect(r.Fs.Contains("layout(binding = 0) uniform sampler2D uTex;") && r.Fs.Contains("layout(binding = 3) uniform sampler2D uNrmTex;")
                  && r.Fs.Contains("layout(binding = 4) uniform sampler2D uSpecTex;"), "unbound samplers take the table's units, a comma list split");
        check.Expect(r.Fs.Contains("layout(binding = 26) uniform usampler2D uLightRec;")
                  && r.Opaques.Contains(new VkOpaque("uLightRec", "usampler2D", 0, 26, VkBindKind.Sampled)), "a declared binding is kept and reported");
        check.Expect(r.Vs.StartsWith("#version 460 core\n#extension GL_ARB_shader_draw_parameters : require\n", StringComparison.Ordinal)
                  && r.Vs.Contains("vDraw = gl_DrawIDARB + uDrawBase;"), "gl_DrawID is the draw-parameters spelling, its extension right after #version");
        check.Expect(r.Vs.Contains("void cxGlMain()") && r.Vs.Contains("gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;"),
            "the vertex stage's clip depth moves from GL's -w..w to Vulkan's 0..w after its own main");
    }

    private static void Bindless(Checker check, VkProgramText r)
    {
        check.Expect(r.Error == null, $"the bindless fixture translates ({r.Error})");
        check.Expect(r.Fs.Contains("#define uTex cxTex2D[nonuniformEXT((uTexH[(vDraw << 3) + 0]).x)]"), "a handle construction indexes set 1's array by the handle's low word");
        check.Expect(!r.Fs.Contains("GL_ARB_bindless_texture") && r.Fs.Contains("#extension GL_EXT_nonuniform_qualifier : require")
                  && r.Fs.Contains("layout(set = 1, binding = 0) uniform sampler2D cxTex2D[];"), "the ARB extension goes, the array and its qualifier's extension come");
        check.Expect(r.Opaques.Contains(new VkOpaque("CxTexH", "CxTexH", 0, 3, VkBindKind.StorageBlock)), "a storage block's binding is reported");
        check.Expect(!r.Vs.Contains("CxLoose") && !r.Fs.Contains("CxLoose"), "a program with no loose uniform declares no loose block");
        check.Expect(r.Vs.Contains("vDraw = (gl_InstanceIndex - gl_BaseInstanceARB);"), "GL's instance id is Vulkan's index less the base instance");
    }

    private static void Errors(Checker check)
    {
        const string vs = "#version 460 core\nvoid main() { gl_Position = vec4(0.0); }\n";
        VkProgramText a = VkGlsl.Translate(vs, "#version 460 core\nuniform sampler2D uMystery;\nvoid main() { }\n", FixtureUnits);
        check.Expect(a.Error != null && a.Error.Contains("uMystery", StringComparison.Ordinal), $"a sampler the table does not name is refused by name ({a.Error})");
        VkProgramText b = VkGlsl.Translate(vs, "#version 460 core\nuniform float uDrawBase;\nvoid main() { }\n", FixtureUnits);
        check.Expect(b.Error != null && b.Error.Contains("uDrawBase", StringComparison.Ordinal), $"a push value declared with another type is refused ({b.Error})");
        VkProgramText c = VkGlsl.Translate(vs, "#version 460 core\nuniform uvec2 h;\nvoid main() { vec4 x = texture(sampler3D(h), vec3(0.0)); }\n", FixtureUnits);
        check.Expect(c.Error != null && c.Error.Contains("sampler3D", StringComparison.Ordinal), $"a bindless construction of another sampler type is refused ({c.Error})");
    }

    /// <summary>Both translated stages compile for Vulkan 1.3. Oracle: shaderc, an independent compiler.</summary>
    internal static bool Compiles(Checker check, string what, VkProgramText r, Dictionary<ShaderKind, byte[]>? spirv = null)
    {
        if (r.Error != null) { check.Expect(false, $"{what} translates ({r.Error})"); return false; }
        bool ok = true;
        foreach ((ShaderKind kind, string text) in (ReadOnlySpan<(ShaderKind, string)>)[(ShaderKind.VertexShader, r.Vs), (ShaderKind.FragmentShader, r.Fs)])
        {
            byte[]? b = VulkanToolchain.Compile(text, what, kind, out string error);
            check.Expect(b != null, $"{what}'s {(kind == ShaderKind.VertexShader ? "vertex" : "fragment")} stage compiles for Vulkan ({Clip(error)})");
            ok &= b != null;
            if (b != null && spirv != null) spirv[kind] = b;
        }
        return ok;
    }

    private static string Clip(string s) => s.Length > 600 ? s[..600] + "..." : s;

    private static void Layout(Checker check)
    {
        byte[]? b = VulkanToolchain.Compile(LayoutFs.Replace("\r\n", "\n"), "vkglsl layout fixture", ShaderKind.FragmentShader, out string error);
        check.Expect(b != null, $"the layout fixture compiles ({error})");
        if (b == null) return;
        var blk = VkSpirv.Block(b, 0, VulkanToolchain.UniformBase + LooseBinding, ["a", "b", "m", "arr", "c", "d"]);
        check.Expect(blk != null, "the module binds a six-member block where the fixture declares it");
        if (blk is not { } l) return;
        var m = l.Members;
        check.Expect(m.Count == 6 && m["a"] == (0, 4) && m["b"] == (16, 12) && m["m"] == (32, 64) && m["arr"] == (96, 48)
                  && m["c"] == (144, 4) && m["d"] == (152, 8) && l.Bytes == 160,
            $"the block reads back at the hand-computed std140 offsets (got {string.Join(", ", m)}; {l.Bytes} bytes)");
    }
}
