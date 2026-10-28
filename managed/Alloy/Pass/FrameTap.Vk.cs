using System;
using System.IO;
using System.Runtime.InteropServices;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class FrameTap
{
    /// <summary>Words a texel the scene colour reads back as: two halves to a word, as <c>cx_readback_c.glsl</c> packs
    /// <see cref="Format.Rgba16F"/> and <see cref="WriteColour"/> unpacks it.</summary>
    private const int ColourWords = 2;
    /// <summary>The copy stage's local size, as <c>cx_readback_c.glsl</c> declares it.</summary>
    private const int Group = 8;

    /// <summary>The read-back's copy stage, list and buffer, on one device.</summary>
    private sealed class TapVkState
    {
        public VkBackend? Dev;
        public CommandList? Cmds;
        public PipelineId Copy;
        public BufferId Out;
        public long OutCap;
    }

    private static TapVkState _vkTap = new();

    /// <summary>The Vulkan frame's finished scene colour -- past the batches and the sky, before the encode into the
    /// composite -- as this set's <c>{seq}_vkscene.pfm</c>. The Vulkan frame records one list and submits it once, so
    /// there is no mid-frame tap to take: this reads the target back after that submit.</summary>
    internal static void TakeVk(VkBackend vk, ImageId hdr, int w, int h)
    {
        if (!Armed || _log == null || hdr.IsNull || w <= 0 || h <= 0) return;
        Armed = false;   // one frame: the arm is spent whether or not the read-back lands
        uint[]? colour = ReadColour(vk, hdr, w, h);
        if (colour == null)
        {
            _log.Always("[snap] the Vulkan scene colour did NOT read back -- the set has no vkscene.pfm");
            return;
        }
        string path = Path.Combine(Dir, $"{_seq}_vkscene.pfm");
        try
        {
            Directory.CreateDirectory(Dir);
            WriteColour(path, colour, w, h);
            _log.Always($"[snap] wrote the Vulkan scene colour to {path} ({w}x{h})");
        }
        catch (IOException e) { _log.Always($"[snap] {path} was not written -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { _log.Always($"[snap] {path} was not written -- {e.Message}"); }
    }

    /// <summary>An RGBA16F image as two half pairs a texel, row 0 first, through the copy stage and one submit. Null
    /// when the device refused a step.</summary>
    private static uint[]? ReadColour(VkBackend vk, ImageId img, int w, int h)
    {
        TapVkState s = _vkTap;
        if (s.Dev != vk)
        {
            _vkTap = s = new TapVkState { Dev = vk, Cmds = new CommandList("vulkan frame tap", 4096, _log!) };
            const string Version = "#version 460 core\n";
            string src = ShaderSource.Get("cx_readback_c.glsl").Replace(Version, Version + $"#define CX_READBACK_WORDS {ColourWords}\n");
            var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, "frame tap copy")), Label = "frame tap copy" };
            s.Copy = vk.CreatePipeline(in d);
        }
        if (s.Copy.IsNull) return null;
        var words = new uint[(long)w * h * ColourWords];
        long bytes = words.LongLength * sizeof(uint);
        if (bytes > s.OutCap)
        {
            vk.Destroy(s.Out);
            s.Out = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.Storage | BufferUse.CopySrc, MemoryKind.Device, "frame tap read-back"));
            s.OutCap = s.Out.IsNull ? 0 : bytes;
            if (s.Out.IsNull) return null;
        }
        CommandList cl = s.Cmds!;
        cl.Reset();
        cl.SetPipeline(s.Copy);
        cl.BindSet(0, new BindSet(default, [new BufferBinding(SsboSlots.ReadbackOut, s.Out)], [new ImageBinding(0, img)], default));
        cl.Dispatch((w + Group - 1) / Group, (h + Group - 1) / Group);
        return vk.Submit(cl) && vk.ReadBack(s.Out, MemoryMarshal.AsBytes(words.AsSpan())) ? words : null;
    }

    /// <summary>Colour as a float RGB <c>.pfm</c>, whose rows run bottom-up as the read-back's do.</summary>
    private static void WriteColour(string path, uint[] colour, int w, int h)
    {
        using var f = File.Create(path);
        f.Write(System.Text.Encoding.ASCII.GetBytes($"PF\n{w} {h}\n-1.0\n"));
        var row = new float[w * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                long i = ((long)y * w + x) * ColourWords;
                row[x * 3] = (float)BitConverter.UInt16BitsToHalf((ushort)colour[i]);
                row[x * 3 + 1] = (float)BitConverter.UInt16BitsToHalf((ushort)(colour[i] >> 16));
                row[x * 3 + 2] = (float)BitConverter.UInt16BitsToHalf((ushort)colour[i + 1]);
            }
            f.Write(MemoryMarshal.AsBytes(row.AsSpan()));
        }
    }
}
