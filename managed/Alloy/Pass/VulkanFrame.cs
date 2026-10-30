using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The world frame on Vulkan: Vulkan culls, draws the colour bracket into its own scene
/// targets and encodes them into the composite, which the GUI then draws over and Vulkan presents.</summary>
internal static class VulkanFrame
{
    private sealed class FrameState
    {
        public CommandList? Cmds;
        public bool Live;
        public int Frames;
        public readonly System.Collections.Generic.List<VkBackend.UploadTop> Uploads = [];
    }

    /// <summary>Frames between pipeline-cache writes: each is a no-op unless a pipeline was made since the last.</summary>
    private const int CacheSavePeriod = 600;

    private static readonly FrameState _s = new();

    /// <summary>The frame list's first arena: past the cull's source and sphere uploads in a typical area, so the
    /// growth line is the exception.</summary>
    private const int FrameArenaBytes = 4 << 20;

    /// <summary>Frames between two GPU-time lines.</summary>
    private const int GpuPeriod = 300;

    /// <summary>Render this frame into the composite. A frame that cannot -- no backend, no composite, a refused
    /// submit -- fails Alloy loudly: the screen would otherwise hold a stale frame.</summary>
    internal static unsafe void Run(ExtenderLog? log, in Matrix4x4 vp, in Matrix4x4 vpClean, in Matrix4x4 view, in Matrix4x4 proj,
                                    int* viewport, int* display, float near, float far,
                                    bool lightingOn, bool perspective, int groups)
    {
        // a failed Alloy records nothing more: every submit after it would stage uploads no recording ever commits
        if (log == null || AlloyGate.Failed) return;
        if (AlloyDevice.Vulkan(log) is not VkBackend vk || vk.Composite.IsNull)
        {
            AlloyGate.Fail("the Vulkan frame has no backend or no composite image (logged above)");
            return;
        }
        CommandList cl = _s.Cmds ??= new CommandList("vulkan frame", FrameArenaBytes, log);
        cl.Reset();
        vk.TimeFrame = true;
        cl.BeginLabel("cull"u8);
        if (!CullPassVk.Record(vk, cl, log))
        {
            AlloyGate.Fail("the Vulkan cull did not record (logged above) -- Alloy has no visibility test");
            return;
        }
        cl.EndLabel();
        if (!AlloyRenderer.VkRecordFrame(vk, cl, vk.Composite, vp, vpClean, view, proj, viewport, display, near, far,
                                         lightingOn, perspective, groups, log))
        {
            AlloyGate.Fail("the Vulkan colour bracket did not record (logged above)");
            return;
        }
        if (!vk.Submit(cl))
        {
            AlloyGate.Fail("the Vulkan frame's submit was refused (logged above)");
            return;
        }
        vk.FrameTimed();
        Matrix4x4 vpN = vp, projN = proj;
        UiScene.NoteVk(&vpN, &projN, viewport, AlloyRenderer.VkSceneDepth);
        AlloyRenderer.VkFrameTap(vk);
        if (++_s.Frames % CacheSavePeriod == 0) vk.SavePipelineCache();
        if (_s.Frames % GpuPeriod == 0 && vk.TakeGpuMs(out int n) is var ms && n > 0)
        {
            vk.TakeWaitMs(out double gpu, out double acquire);
            double translate = vk.TakeTranslateMs(out int sets, out int made, out int retired, out int draws, out int lists, out int barriers);
            log.Always($"VulkanFrame: [vkgpu] {ms:F2} ms GPU per frame over {n} frames on the graphics queue (bake, cull, prepass, "
                     + $"hi-Z, AO, decals, G-buffer, opaque, cutout, sky, the deferred dispatches, blended; not the async froxel build "
                     + $"and fog); the CPU waited "
                     + $"{gpu / GpuPeriod:F2} ms/frame on the GPU timeline and {acquire / GpuPeriod:F2} ms/frame on the swapchain acquire, "
                     + $"and spent {translate / GpuPeriod:F2} ms/frame translating {(float)draws / GpuPeriod:F0} draws and "
                     + $"{(float)sets / GpuPeriod:F0} bind sets ({(float)made / GpuPeriod:F1} written, the rest reused; {retired} pool(s) "
                     + $"retired this period) over {(float)lists / GpuPeriod:F1} lists into vkCmd calls, "
                     + $"{(float)barriers / GpuPeriod:F0} of them barriers");
            log.Always($"VulkanFrame: [vkgpu] translation by op, ms/frame: {TopOps(vk)}; {Uploads(vk, _s.Uploads)}");
        }
        PostChain.Painted();
        if (!_s.Live)
        {
            _s.Live = true;
            log.Always("VulkanFrame: LIVE -- Vulkan bakes the shadow maps, culls, draws the prepass with its vis-ID "
                     + "target, the AO estimate, the decal layer, the G-buffer, the sky and the opaque, cutout and blended "
                     + "batches, and shades the deferred point lights; Vulkan presents the composite itself");
        }
    }

    /// <summary>Ops shown on the by-op line, the costliest first; the rest are summed after them.</summary>
    private const int TopOpCount = 6;
    /// <summary>Buffer labels shown on the upload half of that line.</summary>
    private const int TopUploadCount = 4;

    /// <summary>The ops that cost the most translation time since the last take, each with its commands per frame.</summary>
    private static string TopOps(VkBackend vk)
    {
        Span<double> ms = stackalloc double[VkBackend.OpCount];
        Span<int> n = stackalloc int[VkBackend.OpCount];
        vk.TakeOps(ms, n);
        var order = new int[VkBackend.OpCount];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        double[] cost = ms.ToArray();
        Array.Sort(order, (a, b) => cost[b].CompareTo(cost[a]));
        var sb = new System.Text.StringBuilder();
        double rest = 0;
        for (int k = 0; k < order.Length; k++)
        {
            int i = order[k];
            if (k >= TopOpCount) { rest += cost[i]; continue; }
            if (n[i] == 0) break;
            if (k > 0) sb.Append(", ");
            sb.Append($"{(Op)i} {cost[i] / GpuPeriod:F3} ({(float)n[i] / GpuPeriod:F0})");
        }
        return sb.Append($", the rest {rest / GpuPeriod:F3}").ToString();
    }

    /// <summary>The upload bytes per frame since the last take, inline and staged, and the labels that wrote the most.</summary>
    private static string Uploads(VkBackend vk, System.Collections.Generic.List<VkBackend.UploadTop> top)
    {
        vk.TakeUploads(out long inl, out long staged, out int calls, top);
        var sb = new System.Text.StringBuilder($"uploads {inl / 1024.0 / GpuPeriod:F0} KB/frame inline and "
                                             + $"{staged / 1024.0 / GpuPeriod:F0} KB staged (updates and multi-draws) over "
                                             + $"{(float)calls / GpuPeriod:F0} updates, most:");
        for (int k = 0; k < top.Count && k < TopUploadCount; k++)
            sb.Append($"{(k > 0 ? "," : "")} {top[k].Label} {top[k].Bytes / 1024.0 / GpuPeriod:F0} KB ({(float)top[k].Calls / GpuPeriod:F1})");
        return sb.ToString();
    }
}
