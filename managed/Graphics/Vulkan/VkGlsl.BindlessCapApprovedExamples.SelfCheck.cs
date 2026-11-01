using System;
using CX.Framework;
using Vortice.ShaderCompiler;

namespace CX.Graphics.Rhi;

internal static partial class VkGlsl
{
    /// <summary>Approved-spec: a program's modules, compiled under one GPU's bindless seat count, serve a GPU that seats
    /// another without shaderc running again. Oracle: the toolchain's own count of fresh compiles, held to zero.</summary>
    internal static void BindlessCapSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkGlsl.BindlessCapSelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) { check.Skip("on-device: shaderc compiles nothing at boot in a game session"); return; }
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }
        uint cap = BindlessCap;
        // Given the shader cache a session left on a GPU that seats 4096 textures
        // When the next session opens on a GPU that seats 1,000,000
        // Then no shader is compiled again
        BindlessCap = 4096;
        bool first = CompileBindlessFixture(check, "4096");
        int fresh = VulkanToolchain.FreshCompiles;
        BindlessCap = 1_000_000;
        bool second = CompileBindlessFixture(check, "1,000,000");
        check.Expect(first && second, "the bindless fixture compiled under both seat counts");
        check.Expect(VulkanToolchain.FreshCompiles == fresh,
            $"a GPU seating 1,000,000 textures compiled {VulkanToolchain.FreshCompiles - fresh} stage(s) the 4096 one had cached");
        BindlessCap = cap;
        log.Info(check.Ok ? "VkGlsl.BindlessCapSelfCheck: passed" : "VkGlsl.BindlessCapSelfCheck: FAILED");
    }

    /// <summary>The bindless fixture translated and both stages compiled under the seat count now set.</summary>
    private static bool CompileBindlessFixture(Checker check, string seats)
    {
        VkProgramText t = Translate(BindlessVs.Replace("\r\n", "\n"), BindlessFsText.Replace("\r\n", "\n"), FixtureUnits);
        check.Expect(t.Error == null, $"the bindless fixture translates under {seats} seats ({t.Error})");
        if (t.Error != null) return false;
        byte[]? v = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"bindless cap {seats} vs", ShaderKind.VertexShader, out _);
        byte[]? f = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"bindless cap {seats} fs", ShaderKind.FragmentShader, out _);
        return v != null && f != null;
    }
}
