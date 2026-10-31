using System;
using System.Runtime.InteropServices;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy.Db;

public static unsafe partial class MeshArena
{
    /// <summary>The arena on Vulkan: one vertex buffer per format and the shared index buffer, filled from the
    /// CPU mirrors span by dirty span. Replaced whole when the device is.</summary>
    private sealed class VkArenaState
    {
        public VkBackend? Dev;
        public readonly BufferId[] Vbo = new BufferId[Formats];
        /// <summary>Vertices each buffer has room for, and whether it has been filled at least once -- which
        /// is the section's residency gate.</summary>
        public readonly int[] Capacity = new int[Formats];
        /// <inheritdoc cref="Capacity"/>
        public readonly bool[] Filled = new bool[Formats];
        public BufferId Ebo;
        public int ICapacity;
        public bool IFilled;
    }

    private static VkArenaState _vk = new();

    /// <summary>Vulkan's vertex buffer for <paramref name="f"/>, null before its first pump.</summary>
    internal static BufferId VkVbo(Fmt f) => _vk.Vbo[(int)f];

    /// <summary>Vulkan's index buffer, shared by every format; null before its first pump.</summary>
    internal static BufferId VkEbo => _vk.Ebo;

    /// <summary>The layout range <paramref name="range"/> lives in, from the published box rather than the range
    /// table, so a caller inside a frame reads it without the lock. <see cref="Fmt.Static"/> for no such range.</summary>
    internal static Fmt PubFormat(int range)
    {
        Pub?[] pub = _pub;
        return (uint)range < (uint)pub.Length && pub[range] is { } r ? r.Format : Fmt.Static;
    }

    /// <summary>The vertex layout of format <paramref name="f"/> as the arena's VAOs bind it. Every location any
    /// arena stage declares is fed in every format, because Vulkan has no constant attribute: a format without
    /// bones feeds 8 and 9 from its position (read only behind a bone base, which such a mesh never has), and one
    /// without UV sets 1..3 feeds them from UV 0. ponytail: GL reads (0,0) for an absent set there.</summary>
    internal static VertexLayoutDesc VkLayout(Fmt f)
    {
        int uvSet = CarriesUvSets(f) ? UvSetWord(f) * 4 : 24;
        bool skin = IsSkinned(f);
        return new VertexLayoutDesc(
        [
            new(AlloyRenderer.LocPos, 3, VertexType.Float, 0),
            new(AlloyRenderer.LocNrm, 3, VertexType.Float, 12),
            new(AlloyRenderer.LocUv, 2, VertexType.Float, 24),
            new(MeshChart.LocChartUv, 2, VertexType.Float, 32),
            new(AlloyRenderer.LocTangent, 4, VertexType.Packed1010102Norm, TanWord(f) * 4),
            new(AlloyRenderer.LocPaint, 4, VertexType.U8Norm, PaintWord(f) * 4),
            new(AlloyRenderer.LocBoneIdx, 4, VertexType.U8, skin ? 40 : 0),
            new(AlloyRenderer.LocBoneW, 4, VertexType.Float, skin ? 44 : 0),
            new(AlloyRenderer.LocUvSet1, 2, VertexType.Float, uvSet),
            new(AlloyRenderer.LocUvSet1 + 1, 2, VertexType.Float, CarriesUvSets(f) ? uvSet + 8 : 24),
            new(AlloyRenderer.LocUvSet1 + 2, 2, VertexType.Float, CarriesUvSets(f) ? uvSet + 16 : 24),
        ], StrideBytes(f), $"arena {f}");
    }

    /// <summary>Bring Vulkan's copies up to the mirrors: every span a placement, a reused gap or a lost
    /// context marked dirty -- a high-water mark would not do, since a mesh placed in a freed gap changes
    /// bytes BELOW it -- or the whole mirror into a new buffer when it outgrew the old (which dies after the
    /// frames in flight). Inside <see cref="Pump"/>, under the lock.</summary>
    private static void PumpVk()
    {
        // never makes the device: a pump at boot runs before anything can name the GPU the device is picked by
        if (!Renderer.OnVulkan || AlloyDevice.VulkanMade is not VkBackend vk) return;
        if (_vk.Dev != vk)
        {
            _vk = new VkArenaState { Dev = vk };
            for (int f = 0; f < Formats; f++) _sect[f].Dirty.All(_sect[f].High);
            _idx.Dirty.All(_idx.High);
        }
        VkArenaState v = _vk;
        for (int f = 0; f < Formats; f++)
        {
            Section s = _sect[f];
            int stride = StrideOf[f];
            if (s.High == 0) continue;
            if (v.Vbo[f].IsNull || v.Capacity[f] < s.High)
            {
                vk.Destroy(v.Vbo[f]);
                v.Capacity[f] = s.Mirror.Length / stride;
                v.Vbo[f] = vk.CreateBuffer(new BufferDesc((long)s.Mirror.Length * 4, BufferUse.Vertex, MemoryKind.Device, $"arena {(Fmt)f} vertices"));
                v.Filled[f] = false;
                if (v.Vbo[f].IsNull) { v.Capacity[f] = 0; continue; }
                s.Dirty.All(s.High);   // a new buffer holds nothing of the old one
            }
            for (int k = 0; k < s.Dirty.Count; k++)
            {
                (int from, int to) = s.Dirty.At(k);
                if (to > v.Capacity[f]) to = v.Capacity[f];
                if (to <= from) continue;
                vk.Upload(v.Vbo[f], (long)from * stride * 4,
                          MemoryMarshal.AsBytes(s.Mirror.AsSpan((int)((long)from * stride), (to - from) * stride)));
                v.Filled[f] = true;
            }
            s.Dirty.Clear();
        }
        if (_idx.High == 0) return;
        if (v.Ebo.IsNull || v.ICapacity < _idx.High)
        {
            vk.Destroy(v.Ebo);
            v.ICapacity = _idx.Mirror.Length;
            v.Ebo = vk.CreateBuffer(new BufferDesc((long)_idx.Mirror.Length * 2, BufferUse.Index, MemoryKind.Device, "arena indices"));
            v.IFilled = false;
            if (v.Ebo.IsNull) { v.ICapacity = 0; return; }
            _idx.Dirty.All(_idx.High);
        }
        for (int k = 0; k < _idx.Dirty.Count; k++)
        {
            (int from, int to) = _idx.Dirty.At(k);
            if (to > v.ICapacity) to = v.ICapacity;
            if (to <= from) continue;
            vk.Upload(v.Ebo, (long)from * 2, MemoryMarshal.AsBytes(_idx.Mirror.AsSpan(from, to - from)));
            v.IFilled = true;
        }
        _idx.Dirty.Clear();
    }

    /// <summary>A dynamic range's moved vertices into Vulkan's copy (<see cref="DynUpload"/>); false when the
    /// range's section has not reached the device yet, whose own upload carries the bytes anyway.</summary>
    private static bool DynUploadVk(in Range rr, int stride)
    {
        VkArenaState v = _vk;
        int f = (int)rr.Format;
        if (v.Dev == null || v.Vbo[f].IsNull || !v.Filled[f] || v.Capacity[f] < rr.Base + rr.NV) return false;
        v.Dev.Upload(v.Vbo[f], (long)rr.Base * stride * 4,
                     MemoryMarshal.AsBytes(_sect[f].Mirror.AsSpan((int)((long)rr.Base * stride), rr.NV * stride)));
        return true;
    }

    /// <summary>What a freed place must outlive, on the backend's own free stamp -- the same one it defers its
    /// object frees on. -1 when nothing can be drawing those bytes: no Vulkan device, or not the one the
    /// arena's buffers live on, so nothing of the arena has been recorded against it.</summary>
    private static long VkFlightStamp()
        => Renderer.OnVulkan && AlloyDevice.VulkanMade is VkBackend vk && _vk.Dev == vk ? vk.FreeStamp : -1;

    /// <summary><see cref="OnFlightPassed"/>, made once: an ask allocates no delegate.</summary>
    private static readonly Action<int, bool> FlightPassed = OnFlightPassed;

    /// <summary>Ask the arena's device to say when <paramref name="stamp"/> has passed, as ask <paramref name="gen"/>.
    /// A stamp is only ever taken off that device (<see cref="VkFlightStamp"/>).</summary>
    private static void VkAskPassed(long stamp, int gen)
    {
        if (_vk.Dev is VkBackend vk) vk.WhenPassed(stamp, FlightPassed, gen);
    }

    /// <summary>The device is gone: drop every buffer id with it, so no reader short-circuits on a name that
    /// died and nothing is destroyed through a dead device. Under <see cref="_gate"/>.</summary>
    private static void VkForget() => _vk = new VkArenaState();

    /// <summary>Is a format's geometry on the device? True once its vertex buffer and the shared index buffer
    /// both hold an upload, so every reader's "uploaded yet" gate reads the same.</summary>
    private static bool VkResident(Fmt f)
    {
        VkArenaState v = _vk;
        return !v.Vbo[(int)f].IsNull && v.Filled[(int)f] && !v.Ebo.IsNull && v.IFilled;
    }
}
