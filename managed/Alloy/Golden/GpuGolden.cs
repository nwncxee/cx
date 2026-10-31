using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The goldens that have to run on a GPU to mean anything: the lightmap encoder against the driver's BC6H/BC7 decoder,
/// the G-buffer lanes, the reflection join, the parallax march, the shading rate, the compute bloom against the fragment chain -- each the
/// shipped GLSL as a compute stage -- and the transfer queue's fills against hand-written bytes. Each compares what the
/// hardware answered with an oracle written here; the transfer queue's copy count is the backend's own, held to a literal.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The goldens on a device made and disposed here: a shell whose Vulkan loader has a driver behind
    /// it runs them with no game. In a session Alloy owns the one device and a player's first frame does not
    /// wait on them, so this skips.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        {
            check.Skip("in a session: the goldens run in run-demo, not on a player's first frame");
            return;
        }
        if (VkBackend.Refusal(log) is string why) { check.Skip($"no Vulkan device to run the goldens on ({why})"); return; }
        VkBackend? vk = VkBackend.TryCreate(log);
        if (vk == null) { check.Skip("the device the goldens would run on was refused"); return; }
        try { Run(vk, check, log); }
        finally { vk.Dispose(); }
        log.Info(check.Ok ? "GpuGolden.SelfCheck: passed" : "GpuGolden.SelfCheck: FAILED");
    }

    /// <summary>Every golden on <paramref name="vk"/>, each cleaning up after itself.</summary>
    private static void Run(VkBackend vk, Checker check, ExtenderLog log)
    {
        ParallaxSelfCheck(vk, check, log);
        GbufSelfCheck(vk, check, log);
        SsrJoinSelfCheck(vk, check, log);
        LightmapSelfCheck(vk, check, log);
        VrsSelfCheck(vk, check, log);
        PostSelfCheck(vk, check, log);
        TransferSelfCheck(vk, check, log);
        TransferAfterMips(vk, check, log);
        TransferRefill(vk, check);
        VmaSelfCheck(vk, check, log);
        VmaMoreSelfCheck(vk, check, log);
        WhenDoneSelfCheck(vk, check, log);
    }

    /// <summary>The version line every golden stage opens with, and the one the shipped libraries open with.</summary>
    private const string Version = "#version 460 core\n";

    /// <summary>The storage block pair every case-per-group golden reads and writes, on the points
    /// <see cref="SsboSlots"/> names -- interpolated, so the GLSL and the bind are one number. Internal because
    /// the binding contract reads it as one of the stages composed in C#.</summary>
    internal static string CaseBlocks(int inFloats, int outFloats) =>
        $"layout(std430, binding = {SsboSlots.GoldenIn}) readonly buffer CxGoldenIn {{ float uIn[]; }};\n"
      + $"layout(std430, binding = {SsboSlots.GoldenOut}) writeonly buffer CxGoldenOut {{ float uOut[]; }};\n"
      + $"const int CX_GOLD_IN = {inFloats}, CX_GOLD_OUT = {outFloats};\n";

    /// <summary><paramref name="pipe"/> over one group per case, <paramref name="input"/> on
    /// <see cref="SsboSlots.GoldenIn"/> and <paramref name="outFloats"/> floats read back off
    /// <see cref="SsboSlots.GoldenOut"/>. Null when a step was refused; <paramref name="extra"/> binds beside them.</summary>
    private static float[]? RunCases(VkBackend vk, ExtenderLog log, PipelineId pipe, int groups,
                                     ReadOnlySpan<float> input, int outFloats, string label,
                                     ReadOnlySpan<BufferBinding> extra = default)
    {
        BufferId inb = vk.CreateBuffer(new BufferDesc(Math.Max(input.Length, 1) * sizeof(float),
            BufferUse.Storage | BufferUse.CopyDst, MemoryKind.Device, label + " cases"));
        BufferId outb = vk.CreateBuffer(new BufferDesc(outFloats * sizeof(float),
            BufferUse.Storage | BufferUse.CopySrc, MemoryKind.Device, label + " answers"));
        float[]? got = null;
        if (!inb.IsNull && !outb.IsNull)
        {
            Span<BufferBinding> ssbo = stackalloc BufferBinding[2 + extra.Length];
            ssbo[0] = new BufferBinding(SsboSlots.GoldenIn, inb);
            ssbo[1] = new BufferBinding(SsboSlots.GoldenOut, outb);
            extra.CopyTo(ssbo[2..]);
            var cl = new CommandList(label, 1 << 16, log);
            cl.UpdateBuffer(inb, 0, MemoryMarshal.AsBytes(input));
            cl.ZeroBuffer(outb);
            cl.SetPipeline(pipe);
            cl.BindSet(0, new BindSet(default, ssbo, default, default));
            cl.Dispatch(groups);
            var answers = new float[outFloats];
            if (vk.Submit(cl) && vk.ReadBack(outb, MemoryMarshal.AsBytes(answers.AsSpan()))) got = answers;
        }
        vk.Destroy(inb);
        vk.Destroy(outb);
        return got;
    }

    /// <summary>The body of the one function <paramref name="signature"/> opens, from <paramref name="src"/>:
    /// through the first brace alone on a line, which is how every function in these libraries ends. Empty when
    /// the library no longer declares it, which each golden reports rather than passing.</summary>
    private static string Function(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        int end = src.IndexOf("\n}\n", at, StringComparison.Ordinal);
        return end < 0 ? "" : src[at..(end + 3)];
    }

    /// <summary>The whole line declaring <paramref name="name"/> in <paramref name="src"/>, empty when there is
    /// none: a constant a golden has to compile against the value the shipped library gives it.</summary>
    private static string Declaration(string src, string name)
    {
        int at = src.IndexOf(name, StringComparison.Ordinal);
        if (at < 0) return "";
        int open = src.LastIndexOf('\n', at) + 1, end = src.IndexOf(';', at);
        return end < 0 ? "" : src[open..(end + 1)] + "\n";
    }
}
