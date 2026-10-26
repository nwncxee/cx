using System;
using System.Runtime.InteropServices;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy.Db;

public static unsafe partial class MeshArena
{
    /// <summary>The arena on Vulkan: one vertex buffer per format and the shared index buffer, filled from the same
    /// CPU mirrors GL's are, on their own upload marks. Replaced whole when the device is.</summary>
    private sealed class VkArenaState
    {
        public VkBackend? Dev;
        public readonly BufferId[] Vbo = new BufferId[Formats];
        public readonly int[] Uploaded = new int[Formats], Capacity = new int[Formats];
        public BufferId Ebo;
        public int IUploaded, ICapacity;
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

    /// <summary>Bring Vulkan's copies up to the mirrors: the appended tail, or the whole mirror into a new buffer
    /// when it outgrew the old (which dies after the frames in flight). Inside <see cref="Pump"/>, under the lock.</summary>
    private static void PumpVk()
    {
        if (!Renderer.OnVulkan || _log == null || AlloyDevice.Vulkan(_log) is not VkBackend vk) return;
        if (_vk.Dev != vk) _vk = new VkArenaState { Dev = vk };
        VkArenaState v = _vk;
        for (int f = 0; f < Formats; f++)
        {
            Section s = _sect[f];
            int stride = StrideOf[f];
            if (s.Used == 0 || v.Uploaded[f] == s.Used) continue;
            if (v.Vbo[f].IsNull || v.Capacity[f] < s.Used)
            {
                vk.Destroy(v.Vbo[f]);
                v.Capacity[f] = s.Mirror.Length / stride;
                v.Vbo[f] = vk.CreateBuffer(new BufferDesc((long)s.Mirror.Length * 4, BufferUse.Vertex, MemoryKind.Device, $"arena {(Fmt)f} vertices"));
                v.Uploaded[f] = 0;
                if (v.Vbo[f].IsNull) { v.Capacity[f] = 0; continue; }
            }
            int from = Math.Min(v.Uploaded[f], s.Used);
            vk.Upload(v.Vbo[f], (long)from * stride * 4,
                      MemoryMarshal.AsBytes(s.Mirror.AsSpan((int)((long)from * stride), (s.Used - from) * stride)));
            v.Uploaded[f] = s.Used;
        }
        if (_iUsed == 0 || v.IUploaded == _iUsed) return;
        if (v.Ebo.IsNull || v.ICapacity < _iUsed)
        {
            vk.Destroy(v.Ebo);
            v.ICapacity = _iMirror.Length;
            v.Ebo = vk.CreateBuffer(new BufferDesc((long)_iMirror.Length * 2, BufferUse.Index, MemoryKind.Device, "arena indices"));
            v.IUploaded = 0;
            if (v.Ebo.IsNull) { v.ICapacity = 0; return; }
        }
        int i0 = Math.Min(v.IUploaded, _iUsed);
        vk.Upload(v.Ebo, (long)i0 * 2, MemoryMarshal.AsBytes(_iMirror.AsSpan(i0, _iUsed - i0)));
        v.IUploaded = _iUsed;
    }

    /// <summary>A dynamic range's moved vertices into Vulkan's copy (<see cref="DynUpload"/>); false when the
    /// range's section has not reached the device yet, whose append upload carries the bytes anyway.</summary>
    private static bool DynUploadVk(in Range rr, int stride)
    {
        VkArenaState v = _vk;
        int f = (int)rr.Format;
        if (v.Dev == null || v.Vbo[f].IsNull || v.Uploaded[f] < rr.Base + rr.NV) return false;
        v.Dev.Upload(v.Vbo[f], (long)rr.Base * stride * 4,
                     MemoryMarshal.AsBytes(_sect[f].Mirror.AsSpan((int)((long)rr.Base * stride), rr.NV * stride)));
        return true;
    }

    /// <summary>The name a format publishes in place of a GL VAO on Vulkan: nonzero once the device holds its vertices
    /// and the shared indices, so every reader's "uploaded yet" gate reads the same. No GL object stands behind it.</summary>
    private static uint VkReadyName(Fmt f)
    {
        VkArenaState v = _vk;
        return v.Vbo[(int)f].IsNull || v.Ebo.IsNull || v.Uploaded[(int)f] == 0 ? 0u : VkReadyBase + (uint)f;
    }

    /// <summary>The first of <see cref="VkReadyName"/>'s names, far above any name GL hands out.</summary>
    private const uint VkReadyBase = 1u << 30;

    /// <summary>A compaction rewrote the mirrors in place: Vulkan's copies go back up whole at the next pump.</summary>
    private static void VkCompacted()
    {
        Array.Clear(_vk.Uploaded);
        _vk.IUploaded = 0;
    }
}
