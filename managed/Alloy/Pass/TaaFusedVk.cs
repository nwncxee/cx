using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The taa stage on Vulkan as one compute dispatch (<c>cx_taa_cs</c>): the accumulate into the new history
/// and the <c>post.taasharp</c> copy-out into the stage's target, which the stack otherwise draws as two passes.</summary>
internal static class TaaFusedVk
{
    /// <summary>One frame's inputs: the stack's current image, the frame depth and velocity, the history pair, and
    /// the 8-bit target with the display rect it is written at. <see cref="W"/> x <see cref="H"/> is the history's size.</summary>
    internal readonly record struct Frame(ImageId Cur, ImageId Depth, ImageId Vel, ImageId Hist, ImageId HistOut,
                                          ImageId Into, int X, int Y, int W, int H,
                                          float Blend, Vector2 JitterPx, bool Upsample, float Sharp, float Dither);

    /// <summary><c>cx_taa_cs</c>'s push block, member for member.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public Vector2 JitterPx;
        public int OutX, OutY;
        public float Blend, Sharp, Dither;
        public int Upsample;
    }

    /// <summary>The stage on one device, replaced whole with the device: its pipeline and samplers.</summary>
    private sealed class TaaFusedState
    {
        public VkBackend? Dev;
        public PipelineId Pipe;
        public bool Tried;
        public SamplerId Linear, Nearest;
    }

    private static TaaFusedState _s = new();

    /// <summary>The tile edge one group resolves.</summary>
    private const int Tile = 16;

    /// <summary>The dispatch into <paramref name="cl"/>; false (logged once) when its stage was refused.</summary>
    internal static bool Record(VkBackend vk, CommandList cl, in Frame f, ExtenderLog log)
    {
        PipelineId pipe = Pipe(vk, log);
        if (pipe.IsNull) return false;
        TaaFusedState s = _s;
        var p = new Push
        {
            JitterPx = f.JitterPx, OutX = f.X, OutY = f.Y, Blend = f.Blend, Sharp = f.Sharp, Dither = f.Dither,
            Upsample = f.Upsample ? 1 : 0,
        };
        ReadOnlySpan<ImageBinding> sampled =
        [
            new(0, f.Cur, s.Linear), new(1, f.Hist, s.Linear), new(2, f.Vel, s.Linear), new(3, f.Depth, s.Nearest),
        ];
        ReadOnlySpan<ImageBinding> storage = [new(0, f.HistOut), new(1, f.Into)];
        cl.SetPipeline(pipe);
        cl.BindSet(0, new BindSet(default, default, sampled, storage));
        cl.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<Push>(in p)));
        cl.Dispatch((f.W + Tile - 1) / Tile, (f.H + Tile - 1) / Tile);
        return true;
    }

    /// <summary>The stage's pipeline, made on first ask.</summary>
    private static PipelineId Pipe(VkBackend vk, ExtenderLog log)
    {
        TaaFusedState s = _s;
        if (s.Dev != vk)
        {
            _s = s = new TaaFusedState { Dev = vk };
            s.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Nearest, Wrap.Clamp));
            s.Nearest = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.Nearest, Wrap.Clamp));
        }
        if (s.Tried) return s.Pipe;
        s.Tried = true;
        ShaderId cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, ShaderSource.Get("cx_taa_cs.glsl"), "taa fused"));
        var d = new PipelineDesc { Cs = cs, Label = "taa fused" };
        s.Pipe = cs.IsNull ? default : vk.CreatePipeline(in d);
        if (s.Pipe.IsNull) log.Always("TaaFusedVk: the fused taa stage was REFUSED (logged above) -- the taa stage draws as two passes");
        return s.Pipe;
    }
}
