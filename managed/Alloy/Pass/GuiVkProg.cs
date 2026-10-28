using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>One of the GUI path's programs on Vulkan: the text GL compiles, translated by <see cref="VkGlsl"/>, its
/// loose uniforms gathered into the <c>CxLoose</c> std140 block and GL's location tables resolved against that block
/// through <see cref="LooseBlock"/>. The GUI binds a texture per draw, so its pipelines are keyed by state rather
/// than cached on one.</summary>
internal sealed class GuiVkProg
{
    public string Label = "";
    public ShaderId Vs, Fs;
    public LooseBlock.Member[] Members = [];
    public Dictionary<string, int> Names = new();
    public byte[] Block = [];
    public VkOpaque[] Opaques = [];
    /// <summary>Pipelines by <see cref="GuiPassVk"/>'s state key.</summary>
    public readonly Dictionary<long, PipelineId> Pipes = new();
    /// <summary>The native stage a post program is built from (-1: none), its declarations that are that stage's
    /// images, and where its frame uniforms sit.</summary>
    public int Stage = -1;
    public CX.Alloy.StageResources.VkBind[] StageBinds = [];
    public CX.Alloy.FrameLoc StageFrame;

    /// <summary>Program <paramref name="label"/> from the text of its two stages: translated, compiled to SPIR-V
    /// for both the module and the reflection, its loose block laid out, then <paramref name="locate"/> run with
    /// locations answered from that block. Null (logged) when a step refused.</summary>
    internal static GuiVkProg? Build(VkBackend vk, string vs, string fs, IReadOnlyDictionary<string, uint> units,
                                     string label, Action<GuiVkProg> locate, ExtenderLog log)
    {
        // the device maps GL's -1..1 clip depth itself (VK_EXT_depth_clip_control), so the stages keep GL's clip z
        VkProgramText t = VkGlsl.Translate(vs, fs, units, glDepth: false);
        if (t.Error != null) { log.Always($"GuiVkProg: {label} did not translate -- {t.Error}"); return null; }
        byte[]? vb = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"vk {label} vs", ShaderKind.VertexShader, out string ve);
        byte[]? fb = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"vk {label} fs", ShaderKind.FragmentShader, out string fe);
        if (vb == null || fb == null) { log.Always($"GuiVkProg: {label} did not compile -- {(vb == null ? ve : fe)}"); return null; }
        var p = new GuiVkProg { Label = label, Opaques = t.Opaques.ToArray() };
        p.Vs = vk.CreateShader(ShaderStage.Vertex, vb, $"vk {label} vs");
        p.Fs = vk.CreateShader(ShaderStage.Fragment, fb, $"vk {label} fs");
        if (p.Vs.IsNull || p.Fs.IsNull) { log.Always($"GuiVkProg: {label}'s modules were REFUSED (logged above)"); return null; }
        if (LooseBlock.Layout(t, vb, fb) is { } l) { p.Members = l.Members; p.Block = LooseBlock.Block(l.Bytes); }
        p.Names = LooseBlock.Index(p.Members);
        LooseBlock.Resolve(p.Names, () => locate(p));
        return p;
    }

    /// <summary>Member <paramref name="name"/>'s index, or -1 where this program does not read it.</summary>
    internal int Loc(string name) => Names.TryGetValue(name, out int i) ? i : -1;
}
