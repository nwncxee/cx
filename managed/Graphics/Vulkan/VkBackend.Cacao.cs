using System.Runtime.InteropServices;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>One dispatch as native/ffx/cxfsr.cpp takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CacaoDispatch
    {
        public VkCommandBuffer Cmd;
        public FsrImage Depth, Output;
        public System.Numerics.Matrix4x4 Proj;
        public float Radius, Multiplier, Power, Detail;
        public uint Quality, BlurPasses;
    }

    [DllImport("cxfsr")] private static extern int cxcacao_create(FsrDevice* dev, uint w, uint h, uint half, nint* ctx);
    [DllImport("cxfsr")] private static extern int cxcacao_dispatch(nint ctx, CacaoDispatch* d);
    [DllImport("cxfsr")] private static extern void cxcacao_destroy(nint ctx);

    /// <summary>The device's CACAO context and what it was made for; a refusal holds for the device's life.</summary>
    private sealed class CacaoContextState
    {
        public nint Ctx;
        public int W, H;
        public bool Half;
        public string? Refused;
        /// <summary>As <see cref="FsrState.Ring"/>: its backend rings its constants and descriptor sets the same way.</summary>
        public readonly long[] Ring = new long[cxffx_ring()];
        public long Dispatches;
    }

    private readonly CacaoContextState _cacao = new();

    /// <summary>Is FidelityFX CACAO ready for <paramref name="w"/> x <paramref name="h"/> frames? Null when it is; otherwise
    /// why not. A new size replaces the context, the old one freed once no submit can reach it.</summary>
    internal string? CacaoReady(int w, int h, bool half)
    {
        CacaoContextState s = _cacao;
        if (s.Refused != null) return s.Refused;
        if (s.Ctx != 0 && s.W == w && s.H == h && s.Half == half) return null;
        if (s.Ctx != 0)
        {
            nint old = s.Ctx;
            Later(() => cxcacao_destroy(old));
            s.Ctx = 0;
        }
        FsrDevice dev = FfxDevice();
        nint ctx;
        int e = cxcacao_create(&dev, (uint)w, (uint)h, half ? 1u : 0u, &ctx);
        if (e != 0) return s.Refused = $"creating its context returned FfxErrorCode {e}";
        (s.Ctx, s.W, s.H, s.Half) = (ctx, w, h, half);
        _log.Always($"VkBackend: CACAO ready at {w}x{h}{(half ? ", half size inside" : "")}");
        return null;
    }

    /// <summary>The device's CACAO context goes with it.</summary>
    private void DisposeCacao()
    {
        if (_cacao.Ctx != 0) cxcacao_destroy(_cacao.Ctx);
        _cacao.Ctx = 0;
    }

    /// <summary>One CACAO dispatch, recorded straight into the frame's command buffer: the depth to SHADER_READ_ONLY and the
    /// output to GENERAL first, which is where AMD's backend leaves them again.</summary>
    private bool Cacao(CacaoCmd c)
    {
        if (_inPass) return Fault("Cacao inside a render pass");
        if (_onAsync) return Fault("Cacao on the async compute queue");
        if (_cacao.Ctx == 0) return Fault("Cacao with no context -- CacaoReady was not asked, or refused");
        if (!_images.TryGet(c.Depth.Value, out VkImg depth) || !_images.TryGet(c.Output.Value, out VkImg output))
            return Fault("Cacao: a stale image");
        if ((output.Desc.Use & ImageUse.Storage) == 0) return Fault($"Cacao into {output.Desc.Label}, which is not a storage image");
        Transition(depth, VkImageLayout.ShaderReadOnlyOptimal, false);
        Transition(output, VkImageLayout.General);
        long slot = _cacao.Dispatches % _cacao.Ring.Length;
        if (!FfxRingWait(_cacao.Ring, slot)) return Fault("Cacao: the dispatch whose constants this one reuses never finished");
        var d = new CacaoDispatch
        {
            Cmd = _cmd, Depth = Image(depth, 0), Output = Image(output, 1), Proj = c.Proj,
            Radius = c.Radius, Multiplier = c.Multiplier, Power = c.Power, Detail = c.Detail,
            Quality = (uint)c.Quality, BlurPasses = (uint)c.BlurPasses,
        };
        int e = cxcacao_dispatch(_cacao.Ctx, &d);
        _cacao.Ring[slot] = Next;
        _cacao.Dispatches++;
        output.Written = true;
        // AMD's backend bound its own pipelines and sets on the compute point, which disturbs the bindless set there
        BindBindless();
        return e == 0 || Fault($"Cacao: AMD's dispatch returned FfxErrorCode {e}");
    }
}
