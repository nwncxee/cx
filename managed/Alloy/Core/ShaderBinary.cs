using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>Precompiled stage modules: a content author may ship <c>&lt;resref&gt;.shd</c> as SPIR-V instead of
/// GLSL, and this is where one is checked against the stage Alloy would otherwise have compiled and then taken
/// as that stage's module. Cold: a program builds once per session.</summary>
internal static class ShaderBinary
{
    /// <summary>SPIR-V's magic word, little-endian. A module that reads byte-swapped was produced for the other
    /// endianness, so it is refused rather than converted.</summary>
    private const uint Magic = 0x07230203;   // not-an-offset: the SPIR-V file magic

    /// <summary>The words ahead of the first instruction: magic, version, generator, id bound, schema.</summary>
    private const int HeaderWords = 5;

    /// <summary>The opcodes and operands read here, from the SPIR-V specification's tables. <c>Shader</c> is the
    /// capability every graphics and compute module declares.</summary>
    private const int OpCapability = 17, OpEntryPoint = 15, CapShader = 1;

    /// <summary>The newest SPIR-V the Vulkan 1.3 environment accepts (1.6), as <c>major &lt;&lt; 16 | minor &lt;&lt; 8</c>
    /// in the header's version word.</summary>
    private const int MaxMajor = 1, MaxMinor = 6;

    /// <summary>True when <paramref name="b"/> opens with SPIR-V's magic word -- the one test that tells a module
    /// from GLSL text, which is what keeps every <c>.shd</c> of source on the translate-and-compile path.</summary>
    internal static bool IsModule(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24)) == Magic;

    /// <summary>The module for one overridable stage: the author's SPIR-V <c>&lt;resref&gt;.shd</c> when the chain
    /// supplies one that passes <see cref="Validate"/>, else <paramref name="source"/> through shaderc. Null with
    /// <paramref name="error"/> set only when that compile failed; a refused module builds the stock stage.</summary>
    internal static byte[]? Stage(string? resref, ShaderStage stage, IReadOnlyList<VkOpaque> binds, string source,
                                  string label, ExtenderLog? log, out string error)
    {
        if (resref != null && Module(resref, stage, binds, label, log) is { } m) { error = ""; return m; }
        return VulkanToolchain.Compile(source, label, Kind(stage), out error);
    }

    /// <summary>The validated module a content author shipped for <paramref name="resref"/>, or null -- nothing
    /// supplied one, it is GLSL text, the <c>alloy.spirv</c> gate is off, or it was refused (logged, and the stock
    /// stage then compiles, exactly as a GLSL override that will not compile does).</summary>
    private static byte[]? Module(string resref, ShaderStage stage, IReadOnlyList<VkOpaque> binds, string label,
                                  ExtenderLog? log)
    {
        if (ShaderResource.OverrideSpirv(resref, log) is not { } b) return null;
        if (!Validate(b, stage, binds, out string why))
        {
            log?.Always($"shader override: {resref}.shd is a SPIR-V module Alloy REFUSES -- {why}. {label} builds "
                      + "from the stage this extender ships; see docs/shader-overrides.md");
            return null;
        }
        log?.Always($"shader override: {resref}.shd is a SPIR-V module Alloy accepts ({b.Length} bytes) -- it IS "
                  + $"{label}, and none of the text machinery ran for it");
        return b;
    }

    /// <summary>Everything about a module that can be read off the module itself: a well-formed header of a version
    /// this environment takes, a <c>main</c> entry point for <paramref name="stage"/>, and no set-0 binding outside
    /// those <paramref name="binds"/> names -- a descriptor the host never writes hangs the device.</summary>
    internal static bool Validate(byte[] module, ShaderStage stage, IReadOnlyList<VkOpaque> binds, out string why)
    {
        why = "";
        if (!IsModule(module)) { why = "it does not open with SPIR-V's magic word"; return false; }
        if (module.Length % 4 != 0 || module.Length < (HeaderWords + 1) * 4)
        { why = $"it is {module.Length} bytes -- a module is a whole number of words, header and all"; return false; }
        ReadOnlySpan<uint> w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(module);
        int major = (int)(w[1] >> 16) & 0xFF, minor = (int)(w[1] >> 8) & 0xFF;
        if (major != MaxMajor || minor > MaxMinor)
        { why = $"it is SPIR-V {major}.{minor}, past the {MaxMajor}.{MaxMinor} Vulkan 1.3 accepts"; return false; }
        if (w[3] == 0 || w[4] != 0) { why = "its header's id bound is zero or its schema word is not"; return false; }

        bool shader = false, entry = false;
        for (int i = HeaderWords; i < w.Length;)
        {
            int op = (int)(w[i] & 0xFFFF), n = (int)(w[i] >> 16);
            if (n == 0 || i + n > w.Length) { why = $"its instruction stream is malformed at word {i}"; return false; }
            if (op == OpCapability && n >= 2 && w[i + 1] == CapShader) shader = true;
            if (op == OpEntryPoint && n >= 4)
            {
                if (w[i + 1] != (uint)Model(stage))
                { why = $"its entry point is execution model {w[i + 1]}, not the {stage} stage's {Model(stage)}"; return false; }
                if (Name(w.Slice(i + 3, n - 3)) != "main")
                { why = $"its entry point is named \"{Name(w.Slice(i + 3, n - 3))}\", and Alloy enters at main"; return false; }
                entry = true;
            }
            i += n;
        }
        if (!shader) { why = "it declares no Shader capability"; return false; }
        if (!entry) { why = "it declares no entry point"; return false; }
        foreach (uint bound in VkPrograms.Bindings(module))
            if (!Named(binds, bound))
            { why = $"it decorates set 0 binding {bound}, which no opaque the host binds names"; return false; }
        return true;
    }

    /// <summary>True when one of <paramref name="binds"/> sits at set 0, <paramref name="bound"/>, as the toolchain
    /// lays the one set out.</summary>
    private static bool Named(IReadOnlyList<VkOpaque> binds, uint bound)
    {
        foreach (VkOpaque o in binds) if (o.Set == 0 && VkPrograms.Shifted(o) == bound) return true;
        return false;
    }

    /// <summary>A literal string operand: four bytes a word, little-endian, to its first NUL.</summary>
    private static string Name(ReadOnlySpan<uint> w)
    {
        Span<char> c = stackalloc char[w.Length * 4];
        int n = 0;
        foreach (uint word in w)
            for (int k = 0; k < 4; k++)
            {
                char ch = (char)((word >> (k * 8)) & 0xFF);
                if (ch == '\0') return new string(c[..n]);
                c[n++] = ch;
            }
        return new string(c[..n]);
    }

    /// <summary>SPIR-V's execution model for one pipeline stage, from the specification's table.</summary>
    private static int Model(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => 0,
        ShaderStage.Geometry => 3,
        ShaderStage.Fragment => 4,
        _ => 5,
    };

    private static ShaderKind Kind(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderKind.VertexShader,
        ShaderStage.Geometry => ShaderKind.GeometryShader,
        ShaderStage.Compute => ShaderKind.ComputeShader,
        _ => ShaderKind.FragmentShader,
    };

    /// <summary>A minimal GLCompute module whose every word is hand-written from the SPIR-V specification's
    /// binary layout and opcode tables -- the <see cref="SelfCheck"/> oracle, independent of anything here, and
    /// of shaderc. Ids 1..4, so the header's bound is 5.</summary>
    private static uint[] Fixture() =>
    [
        Magic, 1 << 16 | 3 << 8, 0, 5, 0,            // magic, SPIR-V 1.3, generator, id bound, schema
        2 << 16 | 17, 1,                             // OpCapability Shader
        3 << 16 | 14, 0, 1,                          // OpMemoryModel Logical GLSL450
        5 << 16 | 15, 5, 1, 0x6E69616DU, 0,          // not-an-offset: OpEntryPoint GLCompute %1 "main"
        6 << 16 | 16, 1, 17, 1, 1, 1,                // OpExecutionMode %1 LocalSize 1 1 1
        2 << 16 | 19, 2,                             // %2 = OpTypeVoid
        3 << 16 | 33, 3, 2,                          // %3 = OpTypeFunction %2
        5 << 16 | 54, 2, 1, 0, 3,                    // %1 = OpFunction %2 None %3
        2 << 16 | 248, 4,                            // %4 = OpLabel
        1 << 16 | 253,                               // OpReturn
        1 << 16 | 56,                                // OpFunctionEnd
    ];

    /// <summary>The GLSL the check feeds both as text that must NOT be read as a module and, through shaderc, as
    /// a module that must be.</summary>
    private const string Source = "#version 450\nlayout(local_size_x = 1) in;\nvoid main() { }\n";

    private static byte[] Bytes(uint[] words)
    {
        byte[] b = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, b, 0, b.Length);
        return b;
    }

    /// <summary>What <see cref="Validate"/> reads off a module, against two oracles that know nothing of it: the
    /// hand-written <see cref="Fixture"/> transcribed from the SPIR-V specification, and (where the staged shaderc
    /// loads) a module shaderc produced from GLSL text. Each refusal is the fixture with ONE word wrong.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ShaderBinary.SelfCheck");
        var none = Array.Empty<VkOpaque>();
        byte[] good = Bytes(Fixture());
        check.Expect(Validate(good, ShaderStage.Compute, none, out string why), $"the hand-written GLCompute fixture is taken ({why})");
        check.Expect(!Validate(good, ShaderStage.Fragment, none, out _), "the same fixture is refused as a fragment stage");

        foreach ((string what, int at, uint word) in (ReadOnlySpan<(string, int, uint)>)
                 [("its magic word", 0, Magic + 1), ("its version word", 1, 2 << 16), ("its id bound", 3, 0),
                  ("its schema word", 4, 1), ("an instruction's word count", 5, 17), ("its entry point's name", 13, 0x6E69616EU)])
        {
            uint[] w = Fixture();
            w[at] = word;
            check.Expect(!Validate(Bytes(w), ShaderStage.Compute, none, out _), $"a module with {what} wrong is refused");
        }
        check.Expect(!Validate(good[..^1], ShaderStage.Compute, none, out _), "a module of a part-word length is refused");

        // GLSL text must never be taken for a module: the magic word is the whole test, and every .shd of source
        // stays on the translate-and-compile path
        byte[] glsl = System.Text.Encoding.UTF8.GetBytes(Source);
        check.Expect(!IsModule(glsl), "GLSL source text is not read as a SPIR-V module");
        check.Expect(!Validate(glsl, ShaderStage.Compute, none, out _), "GLSL source text is refused as a module");

        // a binding the host does not bind is the hang this check exists for: one OpDecorate pair, nothing else
        uint[] bound = Fixture();
        int n = bound.Length;
        Array.Resize(ref bound, n + 8);
        bound[n] = 4 << 16 | 71; bound[n + 1] = 4; bound[n + 2] = 33; bound[n + 3] = VulkanToolchain.StorageBase;
        bound[n + 4] = 4 << 16 | 71; bound[n + 5] = 4; bound[n + 6] = 34; bound[n + 7] = 0;
        check.Expect(!Validate(Bytes(bound), ShaderStage.Compute, none, out _),
                     $"a module decorating set 0 binding {VulkanToolchain.StorageBase} is refused where no opaque names it");
        var named = new[] { new VkOpaque("CxFixture", "CxFixture", 0, 0, VkBindKind.StorageBlock) };
        check.Expect(Validate(Bytes(bound), ShaderStage.Compute, named, out string bw),
                     $"...and taken where one does ({bw})");

        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        string ce = "the staged shaderc did not load";
        byte[]? real = staged ? VulkanToolchain.Compile(Source, "spirv override fixture", ShaderKind.ComputeShader, out ce) : null;
        if (real != null)
            check.Expect(Validate(real, ShaderStage.Compute, none, out string rw), $"shaderc's own module is taken ({rw})");
        else
            log.Always($"ShaderBinary.SelfCheck: the shaderc oracle stood down -- {ce}");

        log.Info(check.Ok ? "ShaderBinary.SelfCheck: passed (the fixture, six one-word corruptions, a part word, the wrong "
                          + "stage, GLSL text, the binding check)" : "ShaderBinary.SelfCheck: FAILED");
    }
}
