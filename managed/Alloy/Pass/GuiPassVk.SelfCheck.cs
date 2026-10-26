using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

internal static unsafe partial class GuiPassVk
{
    /// <summary>A content pair as the engine's prologue leaves it, declaring the five terms
    /// <see cref="GuiContent.Feed"/> writes, in that order. Hand-written: it is the fixture the std140 oracle below
    /// is laid out against, not a copy of anything the code produces.</summary>
    private const string FixtureVs =
        "#version 330 core\n#define attribute in\n#define varying out\n"
      + "uniform mat4 m_mvp;\nuniform mat4 m_mv;\nuniform mat4 m_texture;\nuniform vec4 globalColor;\n"
      + "uniform vec2 screenParams;\nattribute vec3 vPos;\nattribute vec2 vTcIn;\nvarying vec2 vTc;\n"
      + "void main()\n{\n    vTc = (m_texture * vec4(vTcIn, 0.0, 1.0)).xy / screenParams;\n"
      + "    gl_Position = m_mvp * m_mv * vec4(vPos, 1.0) * globalColor.a;\n}\n";

    private const string FixtureFs =
        "#version 330 core\n#define varying in\n#define texture2D texture\n"
      + "uniform sampler2D texUnit0;\nuniform vec4 globalColor;\nvarying vec2 vTc;\nout vec4 oColor;\n"
      + "void main() { oColor = texture2D(texUnit0, vTc) * globalColor; }\n";

    /// <summary>The std140 offsets the fixture's five terms take, worked out by hand from the layout rules: three
    /// mat4 of four 16-byte columns, then a vec4, then a vec2 on the next 8-byte boundary.</summary>
    private static readonly (string Name, int Offset, int Bytes)[] FixtureBlock =
    [
        ("m_mvp", 0, 64), ("m_mv", 64, 64), ("m_texture", 128, 64), ("globalColor", 192, 16), ("screenParams", 208, 8),
    ];

    /// <summary>The GUI path's translations, headless. Oracles: the hand-laid std140 offsets of
    /// <see cref="FixtureBlock"/> against what the compiled module reflects (two artifacts), the attribute locations
    /// the mesh arena's layout feeds, written out by hand, and the two terms <c>cx_gui_v/f.glsl</c> declare as
    /// loose -- read from the embedded GLSL, not from the pass that sets them.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GuiPassVk.SelfCheck");
        string located = GuiContent.Located(FixtureVs);
        check.Expect(located.Contains("layout(location = 0) in vec3 vPos;", StringComparison.Ordinal),
            "the engine's vPos takes the position location the arena layout feeds");
        check.Expect(located.Contains("layout(location = 1) in vec2 vTcIn;", StringComparison.Ordinal),
            "...and vTcIn the UV location");
        check.Expect(!located.Contains("attribute vec3 vPos;", StringComparison.Ordinal),
            "...with no unlocated declaration left behind");
        check.Expect(GuiContent.Vulkan(FixtureFs).StartsWith("#version 460 core\n", StringComparison.Ordinal)
                  && GuiContent.Vulkan(FixtureFs).Contains("#define texture2D texture", StringComparison.Ordinal),
            "the engine's version line is raised and its defines kept");

        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) { check.Skip("on-device: the GUI pass builds its programs at its first flush"); return; }
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }

        Block(check, log, "the content fixture", GuiContent.Vulkan(located), GuiContent.Vulkan(FixtureFs),
              new Dictionary<string, uint>(StringComparer.Ordinal) { ["texUnit0"] = 0 }, FixtureBlock);
        // uBoneStart is the vertex stage's and uGamma the fragment stage's, and the translator gathers the vertex
        // stage first, so two ints land at 0 and 4 -- the offsets the capture writes at.
        Block(check, log, "the GUI program", ShaderSource.Get("cx_gui_v.glsl"), ShaderSource.Get("cx_gui_f.glsl"),
              new Dictionary<string, uint>(StringComparer.Ordinal), [("uBoneStart", 0, 4), ("uGamma", 4, 4)]);
        log.Info(check.Ok ? "GuiPassVk.SelfCheck: passed" : "GuiPassVk.SelfCheck: FAILED");
    }

    /// <summary>One pair translated and compiled, its loose block reflected, and each <paramref name="want"/> member
    /// held at the offset and size the hand-written oracle gives it.</summary>
    private static void Block(Checker check, ExtenderLog log, string what, string vs, string fs,
                              IReadOnlyDictionary<string, uint> units, (string Name, int Offset, int Bytes)[] want)
    {
        VkProgramText t = VkGlsl.Translate(vs, fs, units, glDepth: false);
        if (t.Error != null) { check.Expect(false, $"{what} translates ({t.Error})"); return; }
        byte[]? vb = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"{what} vs", ShaderKind.VertexShader, out string ve);
        byte[]? fb = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"{what} fs", ShaderKind.FragmentShader, out string fe);
        check.Expect(vb != null, $"{what}'s vertex stage compiles ({ve})");
        check.Expect(fb != null, $"{what}'s fragment stage compiles ({fe})");
        if (vb == null || fb == null) return;
        // the compiled modules' own reflection, which is the second artifact the hand-written offsets are held against
        if (VkUniformShim.Layout(t, vb, fb) is not { } l)
        { check.Expect(false, $"{what} declares the loose block"); return; }
        foreach ((string name, int off, int bytes) in want)
            check.Expect(l.Reflected.TryGetValue(name, out var m) && m.Offset == off && m.Size == bytes,
                $"{what}'s {name} sits at {off} over {bytes} bytes"
              + (l.Reflected.TryGetValue(name, out var g) ? $" (got {g.Offset}, {g.Size})" : " (absent)"));
        log.Info($"GuiPassVk.SelfCheck: {what} -- {t.Loose.Count} loose member(s) in {l.Bytes} bytes, {t.Opaques.Count} declaration(s)");
    }
}
