using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// One cold reading of what the Vulkan bake actually wrote, taken once a session after a real frame's bake and
/// said in one line: a baked layer of the cube array and one of the sun array, each counted for texels under the
/// clear, beside the view record and the indirect commands the layered draw consumed. A shadow that never appears
/// is either an empty raster, a wrong matrix or a list of zero draws, and these three numbers tell them apart.
/// </summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Commands the reading copies back: enough to see a batch's first draws and their packed views.</summary>
    private const int ProbeCmds = 16;

    /// <summary>Note the layered draw this frame's <paramref name="map"/> flush issued, for the reading after the
    /// submit. Only until the reading is taken.</summary>
    private static void VkProbeNote(CasterMap map, int layer, int vn, int size)
    {
        var s = _vk;
        if (s.ProbeDone || s.Dev == null) return;
        if (map == CasterMap.Cubes) { s.ProbeCubeLayer = layer; s.ProbeViews = vn; s.ProbeCubeSize = size; }
        else if (map == CasterMap.SunStatic) { s.ProbeSunLayer = layer; s.ProbeSunSize = size; }
        if (map is CasterMap.Cubes or CasterMap.SunStatic)
        {
            s.ProbeCmd = CasterGatherVk.LayerCommands;
            s.ProbeCnt = CasterGatherVk.LayerCounts;
            s.ProbeViewTbl = CasterGatherVk.Views;
            s.ProbeGroups = _gGroups;
            s.ProbeStart = _gGroups > 0 ? _gStart[0] : 0;
            s.ProbeLen = _gGroups > 0 ? _gLen[0] : 0;
            s.ProbeWant |= map == CasterMap.Cubes ? 1 : 2;
        }
    }

    /// <summary>The reading itself, after the frame's submit so the bake it describes is done. Once a session, and
    /// only when both arrays have been baked at least once.</summary>
    internal static void VkProbe(VkBackend vk, ExtenderLog log)
    {
        var s = _vk;
        if (s.ProbeDone || s.Dev != vk || s.ProbeWant != 3 || s.Cubes.IsNull || s.Cascades.IsNull) return;
        // the comparison wants GL's own bake of the same face beside Vulkan's, which only an A/B frame runs
        if (_gl.CubeTex == 0) return;
        s.ProbeDone = true;
        if (!VkProbeMake(vk, log)) return;
        string cube = VkProbeLayer(vk, s.Cubes, s.ProbeCubeLayer, s.ProbeCubeSize, "cube");
        string sun = VkProbeLayer(vk, s.Cascades, s.ProbeSunLayer, s.ProbeSunSize, "cascade");
        log.Always($"CasterPass: [vkbake] {cube}; {sun}; {VkProbeDraws(vk)}");
        // the sun array beside it: both bakes run on an A/B frame, so the cascade is the control that says
        // whether a difference in the cube faces is the two bakes disagreeing or this reading's own copy
        log.Always($"CasterPass: [vkbake] {VkProbeAgainstGl(vk, s.Cubes, _gl.CubeTex, s.ProbeCubeLayer, s.ProbeCubeSize, "cube")}");
        log.Always($"CasterPass: [vkbake] {VkProbeAgainstGl(vk, s.Cascades, _gl.CascadeTex, s.ProbeSunLayer, s.ProbeSunSize, "cascade")}");
    }

    /// <summary>GL's own bake of the layer the reading looked at, beside Vulkan's, texel for texel. Both arrays
    /// hold the same views of the same casters on an A/B frame, so a difference here is the two bakes disagreeing
    /// rather than the two receivers. Oracle: each side's own stored depth, compared against nothing else.</summary>
    private static string VkProbeAgainstGl(VkBackend vk, ImageId img, uint glTex, int layer, int size, string what)
    {
        if (size <= 0 || glTex == 0) return $"{what} layer {layer}: GL's own face was not read";
        var glz = new float[(long)size * size];
        if (!GpuReadback.DepthLayer(glTex, layer, size, size, glz)) return $"{what}: GL's own face was not read";
        if (!VkProbeRead(vk, img, layer, size, out uint[] vkw)) return $"{what}: Vulkan's face did not read back";
        long both = 0, glOnly = 0, vkOnly = 0, off = 0;
        float worst = 0f, sum = 0f, glSum = 0f, vkSum = 0f;
        for (long i = 0; i < glz.LongLength; i++)
        {
            float a = glz[i], b = BitConverter.UInt32BitsToSingle(vkw[i]);
            bool ga = a < 1f, gb = b < 1f;
            if (ga != gb) { if (ga) glOnly++; else vkOnly++; continue; }
            if (!ga) continue;
            both++;
            glSum += a; vkSum += b;
            float d = MathF.Abs(a - b);
            sum += d;
            if (d > 1e-3f) off++;
            worst = MathF.Max(worst, d);
        }
        float n = Math.Max(both, 1);
        return $"{what} layer {layer} GL vs Vulkan: {both} occluded on both, {glOnly} on GL alone, {vkOnly} on "
             + $"Vulkan alone, {off} past a thousandth (worst {worst:F4}, mean |d| {sum / n:F5}; GL mean "
             + $"{glSum / n:F4}, Vulkan mean {vkSum / n:F4})";
    }

    /// <summary>One layer of <paramref name="img"/> into <paramref name="words"/>, as <see cref="VkProbeLayer"/> reads it.</summary>
    private static bool VkProbeRead(VkBackend vk, ImageId img, int layer, int size, out uint[] words)
    {
        var s = _vk;
        CommandList cl = s.ProbeList!;
        cl.Reset();
        cl.CopyImage(new CopyImageCmd(img, new ImageRegion(0, 0, 0, layer), s.ProbeImg, new ImageRegion(0, 0, 0, 0), size, size, 1));
        cl.SetPipeline(s.ProbePipe);
        cl.BindSet(0, new BindSet(default, [new BufferBinding(SsboSlots.ParityOut, s.ProbeBuf)],
                                  [new ImageBinding(0, s.ProbeImg)], default));
        cl.Dispatch((size + 7) / 8, (size + 7) / 8);
        words = new uint[(long)size * size];
        return vk.Submit(cl) && vk.ReadBack(s.ProbeBuf, MemoryMarshal.AsBytes(words.AsSpan()));
    }

    /// <summary>The copy stage, its scratch image at the widest layer either array has, and the read-back buffer.</summary>
    private static bool VkProbeMake(VkBackend vk, ExtenderLog log)
    {
        var s = _vk;
        int size = Math.Max(s.ProbeCubeSize, s.ProbeSunSize);
        if (size <= 0) return false;
        const string Version = "#version 460 core\n";
        string src = ShaderSource.Get("cx_parity_c.glsl").Replace(Version, Version + "#define CX_PARITY_WORDS 1\n");
        var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, "caster bake reading")),
                                   Label = "caster bake reading" };
        s.ProbePipe = vk.CreatePipeline(in d);
        s.ProbeImg = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, size, size, 1, 1, 1, 1,
                                                  ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "caster bake reading"));
        s.ProbeBuf = vk.CreateBuffer(new BufferDesc((long)size * size * sizeof(uint),
                                                    BufferUse.Storage | BufferUse.CopySrc, MemoryKind.Device, "caster bake reading"));
        s.ProbeList = new CommandList("caster bake reading", 4096, log);
        if (!s.ProbePipe.IsNull && !s.ProbeImg.IsNull && !s.ProbeBuf.IsNull) return true;
        log.Always("CasterPass: [vkbake] the reading's own resources were REFUSED (logged above)");
        return false;
    }

    /// <summary>One layer of <paramref name="img"/> copied aside, read back, and counted: how many of its texels hold
    /// a depth under the clear (an occluder), and the nearest one. All at the clear means nothing rasterized.</summary>
    private static string VkProbeLayer(VkBackend vk, ImageId img, int layer, int size, string what)
    {
        var s = _vk;
        if (size <= 0) return $"{what} layer {layer}: not baked";
        if (!VkProbeRead(vk, img, layer, size, out uint[] words)) return $"{what} layer {layer}: did not read back";
        long under = 0, zero = 0;
        float near = 1f, far = 0f, sum = 0f;
        foreach (uint w in words)
        {
            float z = BitConverter.UInt32BitsToSingle(w);
            if (!(z < 1f)) continue;
            under++;
            sum += z;
            if (z <= 0f) zero++;
            near = MathF.Min(near, z); far = MathF.Max(far, z);
        }
        return $"{what} layer {layer} at {size}px: {under} of {words.Length} texels under the clear"
             + (under > 0 ? $", depth {near:F4}..{far:F4} mean {sum / under:F4}, {zero} at zero"
                          : " -- NOTHING RASTERIZED");
    }

    /// <summary>The list the layered draw consumed: the chunk's first view record, the first batch's survivor count
    /// and its first commands, each command's packed view index beside its instance count.</summary>
    private static string VkProbeDraws(VkBackend vk)
    {
        var s = _vk;
        if (s.ProbeCmd.IsNull || s.ProbeCnt.IsNull) return "no draw list was noted";
        var counts = new uint[Math.Max(s.ProbeGroups + CasterGather.CountExtra, 1)];
        var cmds = new DrawCmdRec[ProbeCmds];
        var views = new CasterViewRec[1];
        bool ok = vk.ReadBack(s.ProbeCnt, MemoryMarshal.AsBytes(counts.AsSpan()))
               && vk.ReadBack(s.ProbeCmd, MemoryMarshal.AsBytes(cmds.AsSpan()))
               && (s.ProbeViewTbl.IsNull || vk.ReadBack(s.ProbeViewTbl, MemoryMarshal.AsBytes(views.AsSpan())));
        if (!ok) return "the draw list did not read back";
        var sb = new System.Text.StringBuilder();
        sb.Append($"{s.ProbeViews} view(s), batch 0 start {s.ProbeStart} len {s.ProbeLen}, survivors ");
        for (int g = 0; g < Math.Min(s.ProbeGroups, 4); g++) sb.Append(counts[g]).Append(' ');
        sb.Append("| view 0 vp.m41..44 ")
          .Append($"({views[0].Vp.M41:F2},{views[0].Vp.M42:F2},{views[0].Vp.M43:F2},{views[0].Vp.M44:F2})")
          .Append($" layer {views[0].Layer:F0} point {views[0].Point:F0} set {views[0].Set:F0}");
        sb.Append(" | cmds");
        for (int i = 0; i < Math.Min(ProbeCmds, 6); i++)
            sb.Append($" [n {cmds[i].Count} inst {cmds[i].InstanceCount} base {cmds[i].BaseInstance} vi {cmds[i].BaseInstance >> CasterGather.ViewShift}]");
        return sb.ToString();
    }
}
