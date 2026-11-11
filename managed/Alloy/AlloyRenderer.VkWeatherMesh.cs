using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// Falling rain and snow through task and mesh stages where the device offers them: one draw culls, lights and emits
/// every kept drop, with no record buffer and no indirect command between. The task stage makes the compute cull's own
/// decision (<c>CxWxPlace</c>), so either path draws the same drops in the same places.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The mesh path's programs on one device, by the rain and snow slots <see cref="VkWxState.Prog"/> uses.</summary>
    private sealed class VkWxMeshState
    {
        public readonly VkProg?[] Prog = new VkProg?[2];
        public readonly PipelineId[] Pipe = new PipelineId[2], LatePipe = new PipelineId[2];
        public readonly Weather.Loc[] L = new Weather.Loc[2];
        public readonly bool[] Tried = new bool[2];
    }

    /// <summary>Lattice points one task workgroup walks: <c>CX_WX_GROUP</c> in <c>cx_wx_load</c>.</summary>
    private const int VkWxTaskGroup = 32;

    /// <summary>The line the weather pass says when it starts drawing through <paramref name="path"/>, or null when it
    /// already said it for that path.</summary>
    private static string? VkWxPathLine(VkWxState s, VkBackend vk, Weather.WxPath path)
    {
        if (s.SaidPath == path) return null;
        s.SaidPath = path;
        return path == Weather.WxPath.Mesh
            ? "AlloyRenderer: the Vulkan precipitation draws through mesh shaders -- task stages cull and light each drop, mesh stages emit the kept ones, one draw"
            : $"AlloyRenderer: the Vulkan precipitation is culled and lit in compute and drawn indirect -- {vk.MeshRefusal ?? "mesh shaders sit out while rain writes its own motion"}";
    }

    /// <summary>Whether this frame's <paramref name="k"/> (0 rain, 1 snow) and its <paramref name="drips"/> draw through the
    /// mesh path: the device offers it, its programs built, and the frame writes no rain motion (<paramref name="motion"/>).</summary>
    private static bool VkWxMeshPath(VkBackend vk, VkWxState s, int k, int drips, bool motion, ExtenderLog log)
        => !motion && VkWxMeshEnsure(vk, s, k, log) && (drips <= 0 || VkWxMeshEnsure(vk, s, 0, log));

    /// <summary>For the mesh path golden: the path a rain frame with roof drips takes on <paramref name="vk"/>, writing rain's
    /// own motion or not, and the line the pass says once about it; <paramref name="again"/> is whether a second ask would say it again.</summary>
    internal static Weather.WxPath VkWxPath(VkBackend vk, ExtenderLog log, out string line, out bool again, bool motion = false)
    {
        var s = new VkWxState { Dev = vk };
        Weather.WxPath path = VkWxMeshPath(vk, s, 0, 1, motion, log) ? Weather.WxPath.Mesh : Weather.WxPath.Compute;
        line = VkWxPathLine(s, vk, path) ?? "";
        again = VkWxPathLine(s, vk, path) != null;
        return path;
    }

    /// <summary>Program <paramref name="k"/> (0 rain and drips, 1 snow) as task, mesh and fragment stages, and its
    /// pipeline, made once per device; false on a device without mesh shaders, or when one was refused (said once, and the
    /// compute path draws instead).</summary>
    private static bool VkWxMeshEnsure(VkBackend vk, VkWxState s, int k, ExtenderLog log)
    {
        if (!vk.MeshShaders) return false;
        VkWxMeshState m = s.MeshState ??= new VkWxMeshState();
        if (m.Tried[k]) return m.Prog[k] != null;
        m.Tried[k] = true;
        (string ts, string ms, string fs) = Weather.VkMeshTexts(k == 1, log);
        string label = $"{VkWxLabel(k)} mesh";
        VkProgramText t = VkGlsl.TranslateMesh(ShaderResource.Tokens(ts), ShaderResource.Tokens(ms), ShaderResource.Tokens(fs), VkPrograms.Units);
        VkProg? p = VkBuildMesh(vk, t, label, log);
        if (p != null)
        {
            LooseBlock.Resolve(p.Names, () => m.L[k] = Weather.VkLocs());
            PipelineDesc d = VkWxMeshPipeDesc(p, $"vk {label}", true);
            m.Pipe[k] = vk.CreatePipeline(in d);
            if (m.Pipe[k].IsNull) p = null;
        }
        m.Prog[k] = p;
        if (p == null)
            log.Always($"AlloyRenderer: the Vulkan {label} program was REFUSED (logged above) -- the precipitation is culled in compute instead");
        return p != null;
    }

    /// <summary><see cref="VkWxPipeDesc"/> over a mesh program's task and mesh stages.</summary>
    private static PipelineDesc VkWxMeshPipeDesc(VkProg p, string label, bool scene)
    {
        PipelineDesc d = VkWxPipeDesc(p, label, scene);
        d.Vs = default; d.Ts = p.Ts; d.Ms = p.Ms;
        return d;
    }

    /// <summary>A mesh program: translated, its three stages compiled, its loose block laid out and the lit table resolved.
    /// Null, logged, when refused.</summary>
    private static VkProg? VkBuildMesh(VkBackend vk, VkProgramText t, string label, ExtenderLog log)
    {
        if (t.Error != null) { log.Always($"AlloyRenderer: the Vulkan {label} program did not translate -- {t.Error}"); return null; }
        byte[]? ts = VulkanToolchain.Compile(t.Ts, $"vk {label} ts", ShaderKind.TaskShader, out string te);
        byte[]? ms = VulkanToolchain.Compile(t.Ms, $"vk {label} ms", ShaderKind.MeshShader, out string me);
        byte[]? fs = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"vk {label} fs", ShaderKind.FragmentShader, out string fe);
        if (ts == null || ms == null || fs == null)
        {
            log.Always($"AlloyRenderer: the Vulkan {label} program did not compile -- {(ts == null ? te : ms == null ? me : fe)}");
            return null;
        }
        var p = new VkProg { Lit = true, Label = label, Opaques = t.Opaques.ToArray() };
        p.Ts = vk.CreateShader(ShaderStage.Task, ts, $"vk {label} ts");
        p.Ms = vk.CreateShader(ShaderStage.Mesh, ms, $"vk {label} ms");
        p.Fs = vk.CreateShader(ShaderStage.Fragment, fs, $"vk {label} fs");
        if (p.Ts.IsNull || p.Ms.IsNull || p.Fs.IsNull) return null;
        if (LooseBlock.Layout(t, ts, fs) is { } l) { p.Members = l.Members; p.Block = LooseBlock.Block(l.Bytes); }
        p.Names = LooseBlock.Index(p.Members);
        LooseBlock.Resolve(p.Names, () => p.L = Programs.LitLocs());
        return p;
    }

    /// <summary>Program <paramref name="k"/>'s depth-less mesh pipeline, made once per device; null when refused, which is
    /// said once and draws the precipitation in the scene from the next frame.</summary>
    private static PipelineId VkWxMeshLatePipe(VkBackend vk, VkWxMeshState m, VkWxState s, int k, ExtenderLog log)
    {
        if (!m.LatePipe[k].IsNull) return m.LatePipe[k];
        PipelineDesc d = VkWxMeshPipeDesc(m.Prog[k]!, $"vk {VkWxLabel(k)} mesh after the resolve", false);
        m.LatePipe[k] = vk.CreatePipeline(in d);
        if (m.LatePipe[k].IsNull) VkWxLateRefused(s, $"its {VkWxLabel(k)} mesh pipeline was REFUSED (logged above)", log);
        return m.LatePipe[k];
    }

    /// <summary><paramref name="count"/> lattice points of <paramref name="kind"/> (1 rain, 2 snow, 3 drips) through mesh
    /// program <paramref name="k"/>: in the scene, or <paramref name="late"/> after the resolve. False only when the arena is full.</summary>
    private static bool VkWxMeshDraw(VkBackend vk, CommandList side, VkWxState s, VkColourState c, int k, int kind, int count,
                                     bool late, Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far, bool lit,
                                     ExtenderLog log, ReadOnlySpan<ImageBinding> own = default)
    {
        VkWxMeshState m = s.MeshState!;
        VkProg p = m.Prog[k]!;
        PipelineId pipe = late ? VkWxMeshLatePipe(vk, m, s, k, log) : m.Pipe[k];
        if (pipe.IsNull || count <= 0) return true;
        side.SetPipeline(pipe);
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            Weather.WriteVk(in m.L[k], kind, view, proj, _eyePos, lit, viewport[3]);
            Weather.WriteCullVk(in m.L[k], count, 0, s.Step);
            LooseBlock.Set(m.L[k].Screen, [(float)viewport[2], viewport[3]]);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        BufferId lights = VkDeferredLights();
        Span<BufferBinding> storage = stackalloc BufferBinding[4];
        int n = 0;
        if (!lights.IsNull) storage[n++] = new BufferBinding(SsboSlots.Lights, lights);
        if (!VkClusterCounts.IsNull) storage[n++] = new BufferBinding(SsboSlots.ClusterCount, VkClusterCounts);
        if (!VkClusterIdx.IsNull) storage[n++] = new BufferBinding(SsboSlots.ClusterIdx, VkClusterIdx);
        if (!VkMovers.IsNull) storage[n++] = new BufferBinding(SsboSlots.Movers, VkMovers);
        VkBindSet(side, p, c, loose, pass, c.Slots, 0, default, storage[..n], own);
        side.DrawMeshTasks((count + VkWxTaskGroup - 1) / VkWxTaskGroup);
        return true;
    }
}
