using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Rhi;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>One instance of the ray-traced scene: a bottom-level structure (<see cref="VkBackend.RtBlas"/>) placed by a
/// row-vector world matrix, with the id a shader reads back for it and whether its hits need an alpha test.</summary>
public struct RtInstance
{
    public Matrix4x4 World;
    public uint Custom;
    public int Blas;
    public bool Opaque;
}

/// <summary>
/// alloy.raytrace: hardware ray tracing through VK_KHR_acceleration_structure and VK_KHR_ray_query. Bottom-level
/// structures over arena ranges are built once, the top level over this frame's instances every frame, both recorded
/// by <see cref="CommandList.RayBuild"/>; shaders reach the top level by its device address, so no descriptor changes.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    private static ReadOnlySpan<byte> AsExt => "VK_KHR_acceleration_structure\0"u8;
    private static ReadOnlySpan<byte> RayQueryExt => "VK_KHR_ray_query\0"u8;
    private static ReadOnlySpan<byte> HostOpsExt => "VK_KHR_deferred_host_operations\0"u8;
    /// <summary>Top-level structures and instance buffers kept, one per frame that may still be reading its own.</summary>
    private const int RtRing = FramesInFlight + 1;
    /// <summary>The instance bytes one in-command update may carry (vkCmdUpdateBuffer's limit).</summary>
    private const int RtUpdateBytes = 65536;
    private const float RtGrow = 1.5f;

    /// <summary>One acceleration structure and the buffer it lives in.</summary>
    private sealed class RtAs
    {
        public VkAccelerationStructureKHR Handle;
        public VkBuffer Buffer;
        public VmaAllocation Alloc;
        public ulong Address, Bytes;
    }

    /// <summary>A bottom-level structure's triangles, kept until its build is recorded.</summary>
    private sealed class RtBlasDef
    {
        public RtAs? As;
        public ulong Vertices, Indices;
        public uint Stride, MaxVertex, Triangles;
        public bool Opaque, Built;
    }

    private sealed class RtState
    {
        public bool On;
        public string? Why;
        public uint ScratchAlign = 256;
        public readonly List<RtBlasDef?> Blas = new();
        public readonly Stack<int> FreeIds = new();
        public readonly List<int> Pending = new();
        public readonly List<(long Frame, RtAs As)> Grave = new();
        public long Frame;
        public RtAs? Scratch;
        /// <summary>The scratch bytes left past its aligned start.</summary>
        public ulong ScratchUsable;
        public readonly RtAs?[] Tlas = new RtAs?[RtRing];
        public readonly uint[] TlasCap = new uint[RtRing];
        public readonly RtAs?[] Inst = new RtAs?[RtRing];
        public int Slot = -1;
        public uint Count;
        public RtInst[] Staged = Array.Empty<RtInst>();
        public int Builds;
        /// <summary>Why the last bottom level was refused, for the scene's report.</summary>
        public string? Refused;
    }

    /// <summary>VkAccelerationStructureInstanceKHR's 64 bytes: a 3x4 row-major transform, then the packed words.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RtInst
    {
        public Vector4 Row0, Row1, Row2;
        public uint CustomMask, SbtFlags;
        public ulong Blas;
    }

    private readonly RtState _rt = new();

    /// <summary>Is hardware ray tracing up on this device, and why not where it is not.</summary>
    public bool RayTracing => _rt.On;
    public string? RayTracingWhy => _rt.Why;
    /// <summary>Bottom-level builds recorded so far this session.</summary>
    public int RayBuilds => _rt.Builds;
    /// <summary>Why the last bottom level was refused; null = none has been.</summary>
    public string? RayRefused => _rt.Refused;

    /// <summary>Does <paramref name="phys"/> offer what ray queries need: the three extensions, their features,
    /// device addresses and 64-bit integers. Null when it does; else why not.</summary>
    private string? RtProbe(VkPhysicalDevice phys, in VkPhysicalDeviceFeatures has)
    {
        if (!HasExt(_vki, phys, AsExt)) return "the driver lacks VK_KHR_acceleration_structure (an RTX-class GPU and a current driver are needed)";
        if (!HasExt(_vki, phys, RayQueryExt)) return "the driver lacks VK_KHR_ray_query";
        if (!HasExt(_vki, phys, HostOpsExt)) return "the driver lacks VK_KHR_deferred_host_operations";
        if (!BufferDeviceAddress(phys)) return "the driver lacks bufferDeviceAddress";
        if (!has.shaderInt64) return "the GPU lacks 64-bit shader integers";
        var rq = new VkPhysicalDeviceRayQueryFeaturesKHR();
        var asf = new VkPhysicalDeviceAccelerationStructureFeaturesKHR { pNext = &rq };
        var f2 = new VkPhysicalDeviceFeatures2 { pNext = &asf };
        _vki.vkGetPhysicalDeviceFeatures2(phys, &f2);
        if (!asf.accelerationStructure) return "the GPU offers no acceleration structures";
        if (!rq.rayQuery) return "the GPU offers no ray queries";
        return null;
    }

    /// <summary>The scratch alignment the device asks of a build, read once it is open.</summary>
    private void RtProps(VkPhysicalDevice phys)
    {
        var asp = new VkPhysicalDeviceAccelerationStructurePropertiesKHR();
        var p2 = new VkPhysicalDeviceProperties2 { pNext = &asp };
        _vki.vkGetPhysicalDeviceProperties2(phys, &p2);
        _rt.ScratchAlign = Math.Max(asp.minAccelerationStructureScratchOffsetAlignment, 1u);
    }

    /// <summary>The usage bits <see cref="BufferUse.Address"/> adds: none while ray tracing is off.</summary>
    private VkBufferUsageFlags RtUsage(BufferUse use)
        => _rt.On && (use & BufferUse.Address) != 0
           ? VkBufferUsageFlags.ShaderDeviceAddress | VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyKHR
           : 0;

    /// <summary>The device address of <paramref name="b"/> (created with <see cref="BufferUse.Address"/>); 0 where there is none.</summary>
    public ulong DeviceAddress(BufferId b)
    {
        if (!_rt.On || !_buffers.TryGet(b.Value, out VkBuf vb) || (vb.Desc.Use & BufferUse.Address) == 0) return 0;
        var info = new VkBufferDeviceAddressInfo { buffer = vb.Buffer };
        return _vkd.vkGetBufferDeviceAddress(&info);
    }

    /// <summary>A plain device buffer with an address, for a structure, its scratch or its instances.</summary>
    private RtAs? RtBuffer(ulong bytes, VkBufferUsageFlags use)
    {
        var bci = new VkBufferCreateInfo { size = bytes, usage = use | VkBufferUsageFlags.ShaderDeviceAddress, sharingMode = VkSharingMode.Exclusive };
        var aci = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice };
        VkBuffer b;
        VmaAllocation a;
        if (Vma.vmaCreateBuffer(_vma, &bci, &aci, &b, &a, null) != VkResult.Success) return null;
        var info = new VkBufferDeviceAddressInfo { buffer = b };
        return new RtAs { Buffer = b, Alloc = a, Bytes = bytes, Address = _vkd.vkGetBufferDeviceAddress(&info) };
    }

    /// <summary>An acceleration structure of <paramref name="bytes"/> and <paramref name="type"/>, in a buffer of its own.</summary>
    private RtAs? RtStructure(ulong bytes, VkAccelerationStructureTypeKHR type)
    {
        RtAs? s = RtBuffer(bytes, VkBufferUsageFlags.AccelerationStructureStorageKHR);
        if (s == null) return null;
        var ci = new VkAccelerationStructureCreateInfoKHR { buffer = s.Buffer, size = bytes, type = type };
        VkAccelerationStructureKHR h;
        if (_vkd.vkCreateAccelerationStructureKHR(&ci, null, &h) != VkResult.Success) { RtDestroy(s); return null; }
        s.Handle = h;
        var ai = new VkAccelerationStructureDeviceAddressInfoKHR { accelerationStructure = h };
        s.Address = _vkd.vkGetAccelerationStructureDeviceAddressKHR(&ai);
        return s;
    }

    private void RtDestroy(RtAs s)
    {
        if (s.Handle.IsNotNull) _vkd.vkDestroyAccelerationStructureKHR(s.Handle, null);
        Vma.vmaDestroyBuffer(_vma, s.Buffer, s.Alloc);
    }

    /// <summary>Retire <paramref name="s"/> once no frame in flight can still read it.</summary>
    private void RtBury(RtAs? s) { if (s != null) _rt.Grave.Add((_rt.Frame, s)); }

    private void RtSweep()
    {
        for (int k = _rt.Grave.Count - 1; k >= 0; k--)
        {
            if (_rt.Frame - _rt.Grave[k].Frame <= RtRing) continue;
            RtDestroy(_rt.Grave[k].As);
            _rt.Grave.RemoveAt(k);
        }
    }

    private static VkAccelerationStructureGeometryKHR RtTriangles(RtBlasDef d)
    {
        var g = new VkAccelerationStructureGeometryKHR
        {
            geometryType = VkGeometryTypeKHR.Triangles,
            flags = d.Opaque ? VkGeometryFlagsKHR.Opaque : VkGeometryFlagsKHR.NoDuplicateAnyHitInvocation,
        };
        g.geometry.triangles = new VkAccelerationStructureGeometryTrianglesDataKHR
        {
            vertexFormat = VkFormat.R32G32B32Sfloat, vertexStride = d.Stride, maxVertex = d.MaxVertex,
            indexType = VkIndexType.Uint16,
        };
        g.geometry.triangles.vertexData.deviceAddress = d.Vertices;
        g.geometry.triangles.indexData.deviceAddress = d.Indices;
        return g;
    }

    /// <summary>A bottom-level structure over <paramref name="indexCount"/> 16-bit indices at <paramref name="idxOffset"/> in
    /// <paramref name="ebo"/>, relative to the vertices at <paramref name="vtxOffset"/> in <paramref name="vbo"/> (positions
    /// first in each <paramref name="stride"/>-byte vertex). Built at the next <see cref="CommandList.RayBuild"/>; -1 refused.</summary>
    public int RtBlas(BufferId vbo, long vtxOffset, int stride, int vertexCount, BufferId ebo, long idxOffset, int indexCount, bool opaque)
    {
        ulong va = DeviceAddress(vbo), ia = DeviceAddress(ebo);
        if (va == 0 || ia == 0 || vertexCount <= 0 || indexCount < 3)
        { _rt.Refused = $"no address (vertices {va:x}, indices {ia:x}) or no triangles ({vertexCount} vertices, {indexCount} indices)"; return -1; }
        var d = new RtBlasDef
        {
            Vertices = va + (ulong)vtxOffset, Indices = ia + (ulong)idxOffset, Stride = (uint)stride,
            MaxVertex = (uint)(vertexCount - 1), Triangles = (uint)(indexCount / 3), Opaque = opaque,
        };
        VkAccelerationStructureGeometryKHR g = RtTriangles(d);
        var bi = new VkAccelerationStructureBuildGeometryInfoKHR
        {
            type = VkAccelerationStructureTypeKHR.BottomLevel, flags = VkBuildAccelerationStructureFlagsKHR.PreferFastTrace,
            mode = VkBuildAccelerationStructureModeKHR.Build, geometryCount = 1, pGeometries = &g,
        };
        uint tris = d.Triangles;
        var sizes = new VkAccelerationStructureBuildSizesInfoKHR();
        _vkd.vkGetAccelerationStructureBuildSizesKHR(VkAccelerationStructureBuildTypeKHR.Device, &bi, &tris, &sizes);
        d.As = RtStructure(sizes.accelerationStructureSize, VkAccelerationStructureTypeKHR.BottomLevel);
        if (d.As == null || !RtScratch(sizes.buildScratchSize))
        { _rt.Refused = $"the structure ({sizes.accelerationStructureSize} bytes) or its scratch ({sizes.buildScratchSize}) was not allocated"; return -1; }
        int id;
        if (_rt.FreeIds.Count > 0) { id = _rt.FreeIds.Pop(); _rt.Blas[id] = d; }
        else { id = _rt.Blas.Count; _rt.Blas.Add(d); }
        _rt.Pending.Add(id);
        return id;
    }

    /// <summary>Has bottom-level structure <paramref name="id"/> been recorded for build (and so may be placed)?</summary>
    public bool RtBlasReady(int id) => (uint)id < (uint)_rt.Blas.Count && _rt.Blas[id] is { Built: true };

    /// <summary>Retire bottom-level structure <paramref name="id"/>.</summary>
    public void RtFreeBlas(int id)
    {
        if ((uint)id >= (uint)_rt.Blas.Count || _rt.Blas[id] is not RtBlasDef d) return;
        RtBury(d.As);
        _rt.Blas[id] = null;
        _rt.Pending.Remove(id);
        _rt.FreeIds.Push(id);
    }

    /// <summary>The shared scratch, grown to <paramref name="bytes"/> plus the alignment a build address needs.</summary>
    private bool RtScratch(ulong bytes)
    {
        if (_rt.Scratch != null && _rt.ScratchUsable >= bytes) return true;
        RtBury(_rt.Scratch);
        ulong usable = (ulong)(bytes * RtGrow);
        _rt.Scratch = RtBuffer(usable + _rt.ScratchAlign, VkBufferUsageFlags.StorageBuffer);
        _rt.ScratchUsable = _rt.Scratch != null ? usable : 0;
        return _rt.Scratch != null;
    }

    private ulong ScratchAddress => (_rt.Scratch!.Address + _rt.ScratchAlign - 1) / _rt.ScratchAlign * _rt.ScratchAlign;

    /// <summary>This frame's top level over <paramref name="instances"/> (only those whose bottom level is built), recorded
    /// at the next <see cref="CommandList.RayBuild"/>. Its device address, which stays valid for the frame; 0 = none.</summary>
    public ulong RtTlas(ReadOnlySpan<RtInstance> instances)
    {
        if (!_rt.On) return 0;
        _rt.Frame++;
        RtSweep();
        int slot = (int)(_rt.Frame % RtRing);
        if (_rt.Staged.Length < instances.Length) _rt.Staged = new RtInst[Math.Max(instances.Length, 64) * 3 / 2];
        uint n = 0;
        foreach (ref readonly RtInstance i in instances)
        {
            if (!RtBlasReady(i.Blas)) continue;
            ref readonly Matrix4x4 m = ref i.World;
            _rt.Staged[n++] = new RtInst
            {
                Row0 = new Vector4(m.M11, m.M21, m.M31, m.M41), Row1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
                Row2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
                CustomMask = (i.Custom & 0xFFFFFFu) | (0xFFu << 24),
                // triangle facing cull off: a reflection meets back faces of open tile geometry; non-opaque goes to the alpha test
                SbtFlags = (uint)(VkGeometryInstanceFlagsKHR.TriangleFacingCullDisable
                                  | (i.Opaque ? VkGeometryInstanceFlagsKHR.ForceOpaque : VkGeometryInstanceFlagsKHR.ForceNoOpaque)) << 24,
                Blas = _rt.Blas[i.Blas]!.As!.Address,
            };
        }
        _rt.Count = n;
        if (n == 0) { _rt.Slot = -1; return 0; }
        uint cap = Math.Max(n, 1);
        if (_rt.Tlas[slot] == null || _rt.TlasCap[slot] < cap)
        {
            uint want = (uint)(cap * RtGrow);
            ulong instBytes = (ulong)want * (ulong)sizeof(RtInst);
            RtBury(_rt.Inst[slot]);
            _rt.Inst[slot] = RtBuffer(instBytes, VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyKHR | VkBufferUsageFlags.TransferDst);
            var g = RtInstancesGeometry(_rt.Inst[slot]?.Address ?? 0);
            var bi = new VkAccelerationStructureBuildGeometryInfoKHR
            {
                type = VkAccelerationStructureTypeKHR.TopLevel, flags = VkBuildAccelerationStructureFlagsKHR.PreferFastBuild,
                mode = VkBuildAccelerationStructureModeKHR.Build, geometryCount = 1, pGeometries = &g,
            };
            var sizes = new VkAccelerationStructureBuildSizesInfoKHR();
            _vkd.vkGetAccelerationStructureBuildSizesKHR(VkAccelerationStructureBuildTypeKHR.Device, &bi, &want, &sizes);
            RtBury(_rt.Tlas[slot]);
            _rt.Tlas[slot] = RtStructure(sizes.accelerationStructureSize, VkAccelerationStructureTypeKHR.TopLevel);
            _rt.TlasCap[slot] = want;
            if (_rt.Inst[slot] == null || _rt.Tlas[slot] == null || !RtScratch(sizes.buildScratchSize))
            { _rt.TlasCap[slot] = 0; _rt.Slot = -1; return 0; }
        }
        _rt.Slot = slot;
        return _rt.Tlas[slot]!.Address;
    }

    private static VkAccelerationStructureGeometryKHR RtInstancesGeometry(ulong address)
    {
        var g = new VkAccelerationStructureGeometryKHR { geometryType = VkGeometryTypeKHR.Instances };
        g.geometry.instances = new VkAccelerationStructureGeometryInstancesDataKHR { arrayOfPointers = false };
        g.geometry.instances.data.deviceAddress = address;
        return g;
    }

    /// <summary>A barrier over everything, between a build and whatever reads or feeds it.</summary>
    private void RtBarrier()
    {
        var mb = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllCommands, dstAccessMask = VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite,
        };
        var dep = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &mb };
        _vkd.vkCmdPipelineBarrier2(_cmd, &dep);
    }

    /// <summary><see cref="Op.RayBuild"/>: the pending bottom levels, then this frame's instances and top level.</summary>
    private bool RayBuild()
    {
        if (_inPass) return Fault("RayBuild inside a render pass");
        if (!_rt.On || (_rt.Pending.Count == 0 && _rt.Slot < 0)) return true;
        RtBarrier();
        foreach (int id in _rt.Pending)
        {
            if (_rt.Blas[id] is not RtBlasDef d || d.As == null) continue;
            VkAccelerationStructureGeometryKHR g = RtTriangles(d);
            var bi = new VkAccelerationStructureBuildGeometryInfoKHR
            {
                type = VkAccelerationStructureTypeKHR.BottomLevel, flags = VkBuildAccelerationStructureFlagsKHR.PreferFastTrace,
                mode = VkBuildAccelerationStructureModeKHR.Build, dstAccelerationStructure = d.As.Handle, geometryCount = 1, pGeometries = &g,
            };
            bi.scratchData.deviceAddress = ScratchAddress;
            var range = new VkAccelerationStructureBuildRangeInfoKHR { primitiveCount = d.Triangles };
            VkAccelerationStructureBuildRangeInfoKHR* pr = &range;
            _vkd.vkCmdBuildAccelerationStructuresKHR(_cmd, 1, &bi, &pr);
            // the scratch is shared: the next build waits for this one
            RtBarrier();
            d.Built = true;
            _rt.Builds++;
        }
        _rt.Pending.Clear();
        if (_rt.Slot >= 0 && _rt.Inst[_rt.Slot] is RtAs inst && _rt.Tlas[_rt.Slot] is RtAs tlas)
        {
            int per = RtUpdateBytes / sizeof(RtInst);
            for (int at = 0; at < (int)_rt.Count; at += per)
            {
                ReadOnlySpan<RtInst> chunk = _rt.Staged.AsSpan(at, Math.Min(per, (int)_rt.Count - at));
                fixed (RtInst* p = chunk)
                    _vkd.vkCmdUpdateBuffer(_cmd, inst.Buffer, (ulong)(at * sizeof(RtInst)), (ulong)(chunk.Length * sizeof(RtInst)), p);
            }
            RtBarrier();
            VkAccelerationStructureGeometryKHR g = RtInstancesGeometry(inst.Address);
            var bi = new VkAccelerationStructureBuildGeometryInfoKHR
            {
                type = VkAccelerationStructureTypeKHR.TopLevel, flags = VkBuildAccelerationStructureFlagsKHR.PreferFastBuild,
                mode = VkBuildAccelerationStructureModeKHR.Build, dstAccelerationStructure = tlas.Handle, geometryCount = 1, pGeometries = &g,
            };
            bi.scratchData.deviceAddress = ScratchAddress;
            var range = new VkAccelerationStructureBuildRangeInfoKHR { primitiveCount = _rt.Count };
            VkAccelerationStructureBuildRangeInfoKHR* pr = &range;
            _vkd.vkCmdBuildAccelerationStructuresKHR(_cmd, 1, &bi, &pr);
            RtBarrier();
            _rt.Slot = -1;
        }
        return true;
    }
}
