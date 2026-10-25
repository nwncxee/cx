using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>What the context actually offers: an extension is proved by STRING because GLX hands back pointers for
/// ones it does not support, and VRAM is read through whichever vendor query answers.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Does this context expose ARB_bindless_texture? By STRING -- GLX returns non-null function
    /// pointers for unsupported extensions, so the pointers alone prove nothing.</summary>
    internal static bool HasBindless()
        => Gl.GetTextureHandleARB != null && Gl.MakeTextureHandleResidentARB != null
           && HasExt("GL_ARB_bindless_texture"u8);

    /// <summary>Is this extension string in the 4.6 CORE context's list? By enumeration, allocation-free.
    /// Must be called inside the bracket -- it is asking the context Alloy draws in, which is not the
    /// engine's 3.3 one and does not necessarily advertise the same set.</summary>
    internal static bool HasExt(ReadOnlySpan<byte> want) => Gl.HasExt(want);

    /// <summary>What the driver will say about VRAM, probed once inside the bracket. The client has no VRAM
    /// query at all (<c>GLRender::GetDeviceInfo_i</c> @0x4d1d10 answers
    /// <c>UNIFORM_BUFFER_OFFSET_ALIGNMENT</c> for query 0 and <c>0xFFFFFFFF</c> for everything else), so the
    /// two vendor extensions are the only route and neither is guaranteed: measured and printed, in KiB.</summary>
    private static void ProbeVram()
    {
        _vramProbed = true;
        // The DISCRIMINANT is which extension answered, never the total -- an NVX driver that reports 0 for
        // its dedicated pool would otherwise be queried with the ATI enum.
        _vramNvx = HasExt("GL_NVX_gpu_memory_info"u8);
        if (_vramNvx) { _vramExt = "NVX_gpu_memory_info"; _vramTotalKib = GetInt(GpuMemTotalNvx); }
        else if (HasExt("GL_ATI_meminfo"u8)) _vramExt = "ATI_meminfo";
        _log?.Info(_vramExt == null
            ? "AlloyRenderer: [vram] no GPU memory query in this context (NVX_gpu_memory_info / ATI_meminfo absent) -- residency is reported as Alloy's own bytes only"
            : $"AlloyRenderer: [vram] {_vramExt} present" + (_vramTotalKib > 0 ? $", {_vramTotalKib / 1024}MiB total" : ""));
        if (_vramExt != null) SampleVram();   // the texture-cache budget reads the free pool at the probe, not a frame later
        // #267's open question, answered from the context that would use them rather than from glxinfo.
        _log?.Info("AlloyRenderer: [vram] arena-relevant extensions in this context: "
            + $"ARB_sparse_texture={(HasExt("GL_ARB_sparse_texture"u8) ? "yes" : "no")}, "
            + $"ARB_sparse_texture2={(HasExt("GL_ARB_sparse_texture2"u8) ? "yes" : "no")}, "
            + $"EXT_memory_object={(HasExt("GL_EXT_memory_object"u8) ? "yes" : "no")}");
    }

    /// <summary>Probe once, then keep the free-VRAM figure fresh. Called from the bracket -- the only place
    /// a GL query may be issued at all -- so <c>/knobs</c> can read a number rather than call into the wrong
    /// context from the chat thread. One <c>glGetIntegerv</c> a frame against a frame's worth of GL traffic.
    /// <c>GL_ATI_meminfo</c>'s query is an array whose FIRST element is the free pool.</summary>
    internal static void SampleVram()
    {
        if (!_vramProbed) ProbeVram();
        if (_vramExt == null) return;
        int* v = stackalloc int[4] { 0, 0, 0, 0 };
        Gl.GetIntegerv(_vramNvx ? GpuMemCurrentNvx : TexFreeMemoryAti, v);
        _vram.FreeKib = v[0];
        if (!_vramNvx) return;
        int evicted = GetInt(GpuMemEvictedNvx);
        if (evicted > 0 && _vram.EvictedKib == 0)
            _log?.Always($"AlloyRenderer: [vram] the driver has evicted {evicted / 1024}MiB of this context to host memory "
                       + $"({_vram.FreeKib / 1024}MiB free): the card is oversubscribed, and a pass that reads an evicted "
                       + "texture runs several times slower -- frame times are not comparable to a session that fits");
        _vram.EvictedKib = evicted;
    }

    /// <summary>The device's memory figures as last sampled, KiB.</summary>
    private sealed class VramState
    {
        public int FreeKib;
        /// <summary>What the driver has paged out of this context to host memory, cumulative; 0 without NVX.</summary>
        public int EvictedKib;
    }

    private static readonly VramState _vram = new();
    /// <summary>What the driver has paged out of this context to host memory, MiB, cumulative; 0 without NVX.</summary>
    internal static long VramEvictedMib => _vram.EvictedKib / 1024;
    /// <summary>The free pool read now, from whichever context is current (the query is per device); -1 before
    /// the probe or without a memory query.</summary>
    internal static long VramFreeKibNow()
    {
        if (!_vramProbed || _vramExt == null) return -1;
        int* v = stackalloc int[4] { 0, 0, 0, 0 };
        Gl.GetIntegerv(_vramNvx ? GpuMemCurrentNvx : TexFreeMemoryAti, v);
        return v[0];
    }

    /// <summary>GL_GPU_MEMORY_INFO_DEDICATED_VIDMEM_NVX / _CURRENT_AVAILABLE_VIDMEM_NVX / _EVICTED_MEMORY_NVX /
    /// GL_TEXTURE_FREE_MEMORY_ATI.</summary>
    private const uint GpuMemTotalNvx = 0x9047, GpuMemCurrentNvx = 0x9049, GpuMemEvictedNvx = 0x904B, TexFreeMemoryAti = 0x87FC;   // not-an-offset: GL vendor-extension query enums

    private static bool _vramProbed, _vramNvx;
    private static string? _vramExt;
    private static int _vramTotalKib;
    /// <summary>The probe's total, MiB; 0 before the probe or when the device answers nothing.</summary>
    internal static long VramTotalMib => _vramTotalKib / 1024;
    /// <summary>Is the bindless path armed this session (probed at the first lit program build)?</summary>
    internal static bool Bindless => _bindless;
    /// <summary>ATI_meminfo (Mesa) answers FREE, never total; at probe time that is most of the card.</summary>
    internal static long VramFreeMib => _vram.FreeKib / 1024;
    internal static bool VramProbed => _vramProbed;

    /// <summary>One <c>glGetIntegerv</c>, for a query that answers a single value.</summary>
    private static int GetInt(uint pname) { int v = 0; Gl.GetIntegerv(pname, &v); return v; }

    /// <summary>The <c>/knobs</c> residency line: what Alloy is holding, and what the device will say about
    /// the pool it is holding it in. Null until the bracket has probed, which is the honest answer before a
    /// frame has run.</summary>
    internal static string? VramNote()
    {
        if (!_vramProbed) return null;
        string device = _vramExt == null
            ? "the device answers no memory query in this context (NVX_gpu_memory_info / ATI_meminfo absent)"
            : $"{_vramExt}: " + (_vramTotalKib > 0 ? $"{_vramTotalKib / 1024}MiB total, " : "")
              + $"{_vram.FreeKib / 1024}MiB free";
        return $"[vram] Alloy holds {OwnedTextures.PeakMib}MiB of textures at high water; {device}";
    }
}
