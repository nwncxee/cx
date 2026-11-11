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
        layout(std430, binding = 3) readonly buffer CxTexH { uint uTexH[]; };
        uvec2 CxTexHandle(uint i) { return uvec2(i, 0u); }
        #define uTex sampler2D(CxTexHandle(uTexH[(vDraw << 3) + 0]))
        #define uPal(h) sampler2D(h)
        flat in int vDraw;
        out vec4 frag;
        void main() { frag = texture(uTex, vec2(0.5)) + texture(uPal(uvec2(2u, 0u)), vec2(0.5)); }
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
        uint cap = VkGlsl.BindlessCap;
        VkGlsl.BindlessCap = 11;
        VkProgramText bl = VkGlsl.Translate(BindlessVs.Replace("\r\n", "\n"), BindlessFsText.Replace("\r\n", "\n"), FixtureUnits);
        VkGlsl.BindlessCap = cap;
        Bindless(check, bl);
        Tail(check);
        VkProgramText viz = VkGlsl.Translate(VizVs, VizFs.Replace("\r\n", "\n"), FixtureUnits);
        Viz(check, viz);
        Errors(check);
        OpaqueList(check);
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) { check.Skip("on-device: shaderc compiles nothing at boot in a game session"); return; }
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }
        Compiles(check, "the loose fixture", loose);
        Compiles(check, "the bindless fixture", bl);
        Compiles(check, "the push-tail fixture", TailText(TailSound()));
        var vizSpirv = new Dictionary<ShaderKind, byte[]>();
        if (Compiles(check, "the viz fixture", viz, vizSpirv)) VizReflects(check, viz, vizSpirv);
        Compiles(check, "the viz fixture's own-define variant",
                 VkGlsl.Translate(VizVs, VizFs.Replace("\r\n", "\n").Replace("#version 460 core\n", "#version 460 core\n#define CX_DECAL_WRITE 1\n",
                                                                             StringComparison.Ordinal), FixtureUnits));
        Layout(check);
        Utf8(check);
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
        check.Expect(r.Fs.Contains("#define uTex cxTex2D[nonuniformEXT(cxTex2DAt((CxTexHandle(uTexH[(vDraw << 3) + 0])).x))]"),
            "a handle construction indexes set 1's array by the handle's low word, through the parentheses of the table's widener");
        check.Expect(r.Fs.Contains("#define uPal(h) cxTex2D[nonuniformEXT(cxTex2DAt((h).x))]"),
            "a construction over a uvec2 the stage hands it -- what a content stage writes -- is rewritten the same way");
        check.Expect(r.Fs.Contains("layout(constant_id = 1000) const uint cxBindlessCap = 1u;")
                  && r.Fs.Contains("uint cxTex2DAt(uint i) { return i < cxBindlessCap ? i : 0u; }") && !r.Fs.Contains("11u"),
            "an index past the descriptors the device seats samples the zero texel, the seat count a specialization constant, not text");
        check.Expect(!r.Fs.Contains("GL_ARB_bindless_texture") && r.Fs.Contains("#extension GL_EXT_nonuniform_qualifier : require")
                  && r.Fs.Contains("layout(set = 1, binding = 0) uniform sampler2D cxTex2D[];"), "the ARB extension goes, the array and its qualifier's extension come");
        check.Expect(r.Opaques.Contains(new VkOpaque("CxTexH", "CxTexH", 0, 3, VkBindKind.StorageBlock)), "a storage block's binding is reported");
        check.Expect(!r.Vs.Contains("CxLoose") && !r.Fs.Contains("CxLoose"), "a program with no loose uniform declares no loose block");
        check.Expect(r.Vs.Contains("vDraw = (gl_InstanceIndex - gl_BaseInstanceARB);"), "GL's instance id is Vulkan's index less the base instance");
    }

    /// <summary>The push-tail fixture's staging struct: the two scalars its tail declares and nothing more.</summary>
    private struct TailProbe
    {
        public int Bias;
        public float Dither;
    }

    private const string TailVs = "#version 460 core\nvoid main() { gl_Position = vec4(0.0); }\n";

    private const string TailFs = """
        #version 460 core
        uniform int uBias;
        uniform float uDither;
        uniform vec4 uTint;
        layout(location = 0) out vec4 o;
        void main() { o = uTint * uDither * float(uBias); }
        """;

    /// <summary>The fixture's sound tail: its two scalars, each taking its offset and its filling type from the
    /// field of <see cref="TailProbe"/> that stages it.</summary>
    private static VkPushTail TailSound()
    {
        var p = new TailProbe();
        return VkPushTail.Of<TailProbe>(VkPush.Of("int", "uBias", ref p, ref p.Bias),
                                        VkPush.Of("float", "uDither", ref p, ref p.Dither));
    }

    /// <summary>The fixture translated with <paramref name="tail"/> as its program's own push members.</summary>
    private static VkProgramText TailText(VkPushTail tail)
        => Translate(TailVs, TailFs.Replace("\r\n", "\n"), FixtureUnits, pushTail: tail);

    /// <summary>A program's own push members: the two scalars of the fixture land in the push block rather than the
    /// loose one, and seven unsound tails are refused. Oracle: the declarations written out here by hand.</summary>
    private static void Tail(Checker check)
    {
        VkPush[] sound = TailSound().All;
        VkPush bias = sound[0], dither = sound[1];
        VkProgramText r = TailText(TailSound());
        check.Expect(r.Error == null, $"a program with a push tail translates ({r.Error})");
        check.Expect(r.Fs.Contains("int uOitGob; int uBias; float uDither; };", StringComparison.Ordinal),
            "the tail's members follow the fixed seven in the one push block");
        check.Expect(r.Loose.Count == 1 && r.Loose[0] == new VkLoose("vec4", "uTint", ""),
            $"a pushed scalar is no loose member: the block is the fixture's one vec4 ({string.Join(", ", r.Loose)})");
        Refused(check, "a tail member that is not a scalar", "uTint",
                new VkPushTail([new VkPush("vec4", "uTint", 0, typeof(System.Numerics.Vector4))], 16));
        Refused(check, "a tail member the struct fills with another type", "uBias",
                new VkPushTail([new VkPush("float", "uBias", 0, typeof(int)), dither], 8));
        Refused(check, "a tail member the fixed seven already name", "uSolid",
                new VkPushTail([new VkPush("int", "uSolid", 0, typeof(int))], 4));
        Refused(check, "a tail that declares a member twice", "twice",
                new VkPushTail([bias, new VkPush("int", "uBias", 4, typeof(int))], 8));
        Refused(check, "a tail member whose field is not where the block puts it", "uDither",
                new VkPushTail([bias, new VkPush("float", "uDither", 8, typeof(float))], 8));
        Refused(check, "a staging struct that is not the tail's members", "struct", new VkPushTail([bias, dither], 12));
        var over = new VkPush[26];
        for (int i = 0; i < over.Length; i++) over[i] = new VkPush("float", $"uOver{i}", i * 4, typeof(float));
        Refused(check, "a tail past the push range the layout declares", "past", new VkPushTail(over, over.Length * 4));
    }

    /// <summary><paramref name="tail"/> refused, the message naming <paramref name="names"/>.</summary>
    private static void Refused(Checker check, string what, string names, VkPushTail tail)
    {
        VkProgramText r = TailText(tail);
        check.Expect(r.Error != null && r.Error.Contains(names, StringComparison.Ordinal),
            $"{what} is refused, naming it ({r.Error})");
    }

    private const string VizVs = "#version 460 core\nvoid main() { gl_Position = vec4(0.0); }\n";

    /// <summary>A stage shaped like the lit chain's: the view selector declared as a plain uniform, a term only a view
    /// reads, a ladder over it, and -- as the decal writer does -- a variant that bakes the selector to 0 itself.</summary>
    private const string VizFs = """
        #version 460 core
        uniform int uViz;
        uniform vec4 uTint;
        layout(location = 0) out vec4 frag;
        #ifdef CX_DECAL_WRITE
        #define uViz 0
        #endif
        void main()
        {
            float hot = 0.0;
            for (int i = 0; i < 4; i++) hot += 1.0;
            if (uViz == 51) { frag = vec4(hot, 0.0, 0.0, 1.0); return; }
            frag = uTint;
        }
        """;

    /// <summary>The view selector: a block member under a name of its own, a macro over a specialization constant in
    /// its place, and a stage that defines the name itself undefining the macro first. Oracle: the declarations
    /// written out here by hand, and off-device shaderc's verdict on both variants of the fixture.</summary>
    private static void Viz(Checker check, VkProgramText r)
    {
        check.Expect(r.Error == null, $"the viz fixture translates ({r.Error})");
        check.Expect(r.Fs.Contains($"    int {VizMember};\n", StringComparison.Ordinal) && !r.Fs.Contains($"int {VizUniform};", StringComparison.Ordinal),
            "the block carries the selector under the name the macro reads, not the stage's own");
        check.Expect(r.Loose.Contains(new VkLoose("int", VizUniform, "")),
            $"the member is still reported as {VizUniform}, so the host writes it by the name the stage declared");
        check.Expect(r.Fs.Contains($"layout(constant_id = {VizOnConstant}) const bool {VizOn} = false;\n#define {VizUniform} ({VizOn} ? {VizMember} : 0)\n",
                                   StringComparison.Ordinal),
            "the selector is a macro over a specialization constant that is false by default, so the ladder folds out");
        check.Expect(r.Fs.Contains($"#undef {VizUniform}\n#define {VizUniform} 0", StringComparison.Ordinal),
            "a stage that bakes the selector to 0 itself undefines the macro first");
        VkProgramText none = Translate(VizVs, "#version 460 core\nlayout(location = 0) out vec4 frag;\n#define uViz 0\nvoid main() { frag = vec4(float(uViz)); }\n", FixtureUnits);
        check.Expect(none.Error == null && !none.Fs.Contains(VizOn, StringComparison.Ordinal) && !none.Fs.Contains($"#undef {VizUniform}", StringComparison.Ordinal),
            $"a program that declares no selector gets neither the constant nor an undef ({none.Error})");
        VkProgramText cs = TranslateCompute("#version 460\nlayout(local_size_x = 1) in;\nuniform int uViz;\nvoid main() { if (uViz == 1) { } }\n", FixtureUnits);
        check.Expect(cs.Error != null && cs.Error.Contains(VizUniform, StringComparison.Ordinal),
            $"a compute stage declaring the selector is refused: it is not in the cache the backend rebuilds ({cs.Error})");
    }

    /// <summary>The selector's member is found in the compiled module and lands in the capture table under the name
    /// the stage declared: a host writing it by that name reaches the bytes the block reserves for it. Oracle: the
    /// module's own reflection, read back by <see cref="VkSpirv"/>.</summary>
    private static void VizReflects(Checker check, VkProgramText r, Dictionary<ShaderKind, byte[]> spirv)
    {
        var l = CX.Graphics.Rhi.LooseBlock.Layout(r, spirv[ShaderKind.VertexShader], spirv[ShaderKind.FragmentShader]);
        check.Expect(l != null, "the viz fixture's loose block reflects out of its module");
        if (l is not { } got) return;
        // the fixture's block by hand: the int at 0, four bytes, the vec4 that follows it aligned to 16
        int at = Array.FindIndex(got.Members, m => m.Name == VizUniform);
        int tint = Array.FindIndex(got.Members, m => m.Name == "uTint");
        check.Expect(at >= 0 && got.Members[at] == new CX.Graphics.Rhi.LooseBlock.Member(VizUniform, 0, 4, 0, true),
            $"the capture table carries the selector under {VizUniform} at the offset the module gives it, so a host "
          + $"writes it by the name the stage declared (got {string.Join(", ", got.Members)})");
        check.Expect(tint >= 0 && got.Members[tint].Offset == 16 && got.Bytes == 32,
            $"the member beside it keeps its own place, so the rename moved nothing ({string.Join(", ", got.Members)}; {got.Bytes} bytes)");
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

    /// <summary><see cref="Opaques"/> against the fixture's declarations as written: three unbound samplers, one on
    /// binding 26, no loose uniform, and nothing from a commented-out line.</summary>
    private static void OpaqueList(Checker check)
    {
        var got = Opaques(LooseFs.Replace("\r\n", "\n") + "/* uniform sampler2D uGone; */\n// layout(binding = 9) uniform image2D uGoneToo;\n");
        string seen = string.Join(", ", got);
        check.Expect(got.Count == 4 && got[0] == ("sampler2D", "uTex", null, VkBindKind.Sampled)
                  && got[1] == ("sampler2D", "uNrmTex", null, VkBindKind.Sampled)
                  && got[2] == ("sampler2D", "uSpecTex", null, VkBindKind.Sampled)
                  && got[3] == ("usampler2D", "uLightRec", 26u, VkBindKind.Sampled),
            $"the opaque list is the fixture's four samplers, its one binding kept and no comment read (got {seen})");
    }

    /// <summary>Valid GLSL whose comments hold multi-byte characters still compiles. Oracle: the hand-written fixture,
    /// which glslc accepts as it stands.</summary>
    private static void Utf8(Checker check)
    {
        const string Cs = "#version 460\nlayout(local_size_x = 1) in;\n// dawn — dusk — noon — night\nvoid main()\n{\n}\n";
        byte[]? b = VulkanToolchain.Compile(Cs, "vkglsl utf-8 fixture", ShaderKind.ComputeShader, out string error);
        check.Expect(b != null, $"a shader with em dashes in a comment compiles whole ({error})");
    }

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
