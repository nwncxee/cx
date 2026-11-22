using System;
using CX.Framework;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The image's memory sits on a heap that is not device-local: system RAM the GPU reads across the bus.
    /// An alias member answers for its group's allocation. False for a stale id or the window's own image.</summary>
    internal bool InSystemRam(ImageId i)
        => _images.TryGet(i.Value, out VkImg img) && !img.Window && OffDevice(img.Group != null ? img.Home : img.Alloc);

    /// <summary>The buffer's memory sits on a heap that is not device-local. False for a stale id.</summary>
    internal bool InSystemRam(BufferId b) => _buffers.TryGet(b.Value, out VkBuf buf) && OffDevice(buf.Alloc);

    private bool OffDevice(VmaAllocation a)
    {
        if (a.IsNull) return false;
        VmaAllocationInfo info;
        Vma.vmaGetAllocationInfo(_vma, a, &info);
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        uint heap = mp.memoryTypes[(int)info.memoryType].heapIndex;
        return !DeviceLocal(mp, (int)heap);
    }

    /// <summary>The marker of the line that warns an allocation landed in system RAM.</summary>
    internal const string SystemRamTag = "[vram] in system RAM";

    /// <summary>Bytes of this device's allocations in system RAM, upload and readback buffers left out. Each one that
    /// was not there at the last call is warned about once on <paramref name="log"/>, by label and size. Cold: a walk
    /// of every live allocation.</summary>
    internal long SystemRamBytes(ExtenderLog log)
    {
        if (!OnThread("SystemRamBytes")) return 0;
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        _inSystemRamNow.Clear();
        long sum = 0;
        foreach (VkImg g in _images.Live()) sum += Spilled(mp, g, g.Desc.Label, g.Alloc, log);
        foreach (AliasGroup g in _alias.Values) sum += Spilled(mp, g, "alias " + g.Name, g.Alloc, log);
        foreach (VkBuf b in _buffers.Live()) if (b.Desc.Memory == MemoryKind.Device) sum += Spilled(mp, b, b.Desc.Label, b.Alloc, log);
        _inSystemRam.IntersectWith(_inSystemRamNow);
        return sum;
    }

    /// <summary>The bytes of <paramref name="a"/> when it sits off the device-local heaps, else 0 (and for no allocation:
    /// an alias member's or the window's); warned about when <paramref name="owner"/> was not in system RAM at the last walk.</summary>
    private long Spilled(in VkPhysicalDeviceMemoryProperties mp, object owner, string label, VmaAllocation a, ExtenderLog log)
    {
        if (a.IsNull) return 0;
        VmaAllocationInfo info;
        Vma.vmaGetAllocationInfo(_vma, a, &info);
        if (DeviceLocal(mp, (int)mp.memoryTypes[(int)info.memoryType].heapIndex)) return 0;
        long bytes = (long)info.size;
        _inSystemRamNow.Add(owner);
        if (_inSystemRam.Add(owner))
            log.Always($"VkBackend: {SystemRamTag}: {label} {bytes >> 20}MiB -- device memory had no room, so the GPU reads it across the bus until it moves back");
        return bytes;
    }

    /// <summary>The images, alias groups and buffers the last <see cref="SystemRamBytes"/> found in system RAM, and the
    /// ones the walk under way has found.</summary>
    private readonly System.Collections.Generic.HashSet<object> _inSystemRam = new(ReferenceEqualityComparer.Instance),
                                                                _inSystemRamNow = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every device-local memory type is also host-visible: an integrated GPU, whose one heap is system RAM.</summary>
    internal bool UnifiedMemory
    {
        get
        {
            VkPhysicalDeviceMemoryProperties mp;
            _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
            for (int i = 0; i < mp.memoryTypeCount; i++)
            {
                VkMemoryPropertyFlags f = mp.memoryTypes[i].propertyFlags;
                if ((f & VkMemoryPropertyFlags.DeviceLocal) != 0 && (f & VkMemoryPropertyFlags.HostVisible) == 0) return false;
            }
            return true;
        }
    }

    /// <summary>The priority of what draws every frame: render targets, shadow arrays, alias groups, the mesh arena.</summary>
    private const float HighPriority = 1f;

    /// <summary>The priority of a sampled-only image, a texture: the driver pages these out first.</summary>
    private const float LowPriority = 0.25f;

    /// <summary>The memory priority of an image made for <paramref name="use"/>; ignored without the extension.</summary>
    private static float PriorityOf(ImageUse use)
        => (use & (ImageUse.ColorTarget | ImageUse.DepthTarget | ImageUse.Storage)) != 0 ? HighPriority : LowPriority;

    /// <summary>The memory priority of a buffer in <paramref name="m"/>: every device buffer draws every frame.</summary>
    private static float PriorityOf(MemoryKind m) => m == MemoryKind.Device ? HighPriority : LowPriority;

    /// <summary>Frames between two move-back tries: a refused move waits this long before the next.</summary>
    internal const int MoveBackPeriod = 300;

    /// <summary>The move-back check every frame runs once: an image in system RAM goes back to device memory once the
    /// device-local blocks VMA holds fall under 80% of those heaps' size, at most one try a period. With nothing in
    /// system RAM it only counts.</summary>
    internal void MoveBackCheck()
    {
        _moveBackChecks++;
        Residents?.FrameEnd();
        if (_spilled.Count == 0 && !_groupsSpilled) return;
        if (Frame < _nextMoveBack || !RoomToMoveBack()) return;
        _nextMoveBack = Frame + MoveBackPeriod;
        _moveBackTries++;
        for (int i = _spilled.Count - 1; i >= 0; i--)
            if (!_images.TryGet(_spilled[i], out VkImg img) || MoveBack(img)) _spilled.RemoveAt(i);
        _groupsSpilled = false;
        foreach (AliasGroup g in _alias.Values)
        {
            if (!OffDevice(g.Alloc)) continue;
            g.MoveBack = true;
            _groupsSpilled = true;
            _shrinkDue = true;
        }
    }

    /// <summary>The share of the device-local heaps under which a spilled allocation is tried back in device memory.</summary>
    private const double MoveBackShare = 0.8;

    /// <summary>The blocks this device holds in device-local heaps are under <see cref="MoveBackShare"/> of their size.</summary>
    private bool RoomToMoveBack()
    {
        DeviceLocalBytes(out _, out _, out long size, out _);
        return DeviceBlockBytes() < size * MoveBackShare;
    }

    /// <summary><paramref name="img"/> remade in device memory under the same id, its old image freed once no submit
    /// reaches it; false (the old one kept, drawing) when device memory still has no room. Its contents start over.</summary>
    private bool MoveBack(VkImg img)
    {
        ImageDesc d = img.Desc;
        VkImageCreateInfo ici = ImageInfo(d, null);
        uint* fams = stackalloc uint[3];
        if ((d.Use & ImageUse.Async) != 0) Share(ref ici.sharingMode, ref ici.queueFamilyIndexCount, ref ici.pQueueFamilyIndices, fams, transfer: false);
        var aci = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice, priority = PriorityOf(d.Use) };
        VkImage image;
        VmaAllocation a;
        if (PlaceImage(&ici, &aci, ImageBytes(d), Overflow.Refuse, &image, &a, out _) != VkResult.Success) return false;
        Name(VkObjectType.Image, image.Handle, d.Label);
        Vma.vmaSetAllocationName(_vma, a, d.Label);
        var old = new VkImg { Image = img.Image, Alloc = img.Alloc, View = img.View, MipViews = img.MipViews, LayerViews = img.LayerViews };
        LaterThanBindless(() => Free(old));
        img.Image = image; img.Alloc = a; img.View = View(image, d, -1); img.MipViews = null; img.LayerViews = null;
        img.Layout = VkImageLayout.Undefined; img.Written = false;
        img.Moves++;
        _sets.Stale = true;
        return true;
    }

    /// <summary>Times image <paramref name="i"/> moved back to device memory: an owner that sees it change remakes the
    /// contents the move started over. Zero for a stale id.</summary>
    internal int MovesOf(ImageId i) => _images.TryGet(i.Value, out VkImg img) ? img.Moves : 0;

    /// <summary>One of these images moved back since <paramref name="seen"/> was latched, which it now is: their owner
    /// remakes what they held. A null id counts no moves.</summary>
    internal bool MovedBack(ref int seen, ImageId a, ImageId b = default, ImageId c = default, ImageId d = default)
    {
        int moves = MovesOf(a) + MovesOf(b) + MovesOf(c) + MovesOf(d);
        if (moves == seen) return false;
        seen = moves;
        return true;
    }

    /// <summary>Image <paramref name="i"/> remade in device memory now, as a move-back period remakes a spilled one;
    /// false for a stale id or no room.</summary>
    internal bool MoveBackForCheck(ImageId i) => _images.TryGet(i.Value, out VkImg img) && MoveBack(img);

    /// <summary>Images placed in system RAM that have not moved back yet; a stale id is dropped at the next try.</summary>
    private readonly System.Collections.Generic.List<uint> _spilled = new();

    /// <summary>An alias group's allocation was placed in system RAM and has not moved back yet.</summary>
    private bool _groupsSpilled;

    private long _nextMoveBack;
    private int _moveBackTries;

    /// <summary>Move-back checks run on this device.</summary>
    internal long MoveBackChecks => _moveBackChecks;

    /// <summary>Frames ended on this device: the move-back check counts one each, so it is the residency clock.</summary>
    internal long Frame => _moveBackChecks;

    /// <summary>The log this device writes to: what an owner says about one of its allocations goes beside it.</summary>
    internal ExtenderLog Log => _log;

    private long _moveBackChecks;

    /// <summary>What gives this device room on demand and takes a turn at every frame's end: the texture table.</summary>
    internal interface IResidents
    {
        /// <summary>Free idle allocations worth about <paramref name="bytes"/> now; false when nothing was idle.</summary>
        bool Evict(long bytes);

        /// <summary>A frame ended: whatever waits for room tries again. Allocates nothing while nothing waits.</summary>
        void FrameEnd();
    }

    /// <summary>The table that lends this device room; null when nothing does.</summary>
    internal IResidents? Residents;

    /// <summary>Where an allocation goes once device memory has no room for it after idle textures are evicted.</summary>
    private enum Overflow { Refuse, SystemRam }

    /// <summary>A texture is refused there and draws its stand-in, as is an image that waits for room; what draws
    /// every frame lands in system RAM.</summary>
    private static Overflow OverflowOf(ImageUse use)
        => (use & ImageUse.NoSpill) == 0 && PriorityOf(use) == HighPriority ? Overflow.SystemRam : Overflow.Refuse;

    /// <summary>A mesh buffer is refused there and its meshes keep their old buffer; any other buffer lands in system RAM.</summary>
    private static Overflow OverflowOf(BufferUse use) => (use & (BufferUse.Vertex | BufferUse.Index)) != 0 ? Overflow.Refuse : Overflow.SystemRam;

    /// <summary>A refusal for want of room that its owner names with its size: counted and kept as the last fault, no line here.</summary>
    private T Unplaced<T>(string why) where T : struct { _lastFault = why; _lastNoRoom = true; Fault(); return default; }

    private static bool NoRoom(VkResult r) => r is VkResult.ErrorOutOfDeviceMemory or VkResult.ErrorOutOfHostMemory;

    /// <summary><paramref name="r"/> found no room and idle textures worth about <paramref name="bytes"/> were evicted
    /// for it: the allocation is worth another try.</summary>
    private bool EvictedFor(VkResult r, long bytes) => NoRoom(r) && Residents?.Evict(bytes) == true;

    /// <summary>Bytes every mip and layer of <paramref name="d"/> holds tightly packed: what placing it asks of a heap.</summary>
    internal static long ImageBytes(in ImageDesc d)
    {
        long n = 0;
        for (int m = 0; m < d.Mips; m++) n += LevelBytes(d, m);
        return n * Layers(d) * Math.Max(1, d.Samples);
    }

    /// <summary>The image in device memory, evicting idle textures between tries; then system RAM when
    /// <paramref name="o"/> lets it.</summary>
    private VkResult PlaceImage(VkImageCreateInfo* ici, VmaAllocationCreateInfo* aci, long bytes, Overflow o, VkImage* image, VmaAllocation* a,
                                out bool spilled)
    {
        spilled = false;
        VkMemoryPropertyFlags had = aci->requiredFlags;
        aci->requiredFlags = had | VkMemoryPropertyFlags.DeviceLocal;
        VkResult r;
        while (EvictedFor(r = Vma.vmaCreateImage(_vma, ici, aci, image, a, null), bytes)) { }
        aci->requiredFlags = had;
        if (r == VkResult.Success || o == Overflow.Refuse || !NoRoom(r)) return r;
        r = Vma.vmaCreateImage(_vma, ici, aci, image, a, null);
        spilled = r == VkResult.Success && OffDevice(*a);
        return r;
    }

    /// <summary>The buffer in device memory, evicting idle textures between tries; then system RAM when
    /// <paramref name="o"/> lets it.</summary>
    private VkResult PlaceBuffer(VkBufferCreateInfo* bci, VmaAllocationCreateInfo* aci, long bytes, Overflow o, VkBuffer* buffer, VmaAllocation* a)
    {
        VkMemoryPropertyFlags had = aci->requiredFlags;
        aci->requiredFlags = had | VkMemoryPropertyFlags.DeviceLocal;
        VkResult r;
        while (EvictedFor(r = Vma.vmaCreateBuffer(_vma, bci, aci, buffer, a, null), bytes)) { }
        aci->requiredFlags = had;
        if (r == VkResult.Success || o == Overflow.Refuse || !NoRoom(r)) return r;
        return Vma.vmaCreateBuffer(_vma, bci, aci, buffer, a, null);
    }

    /// <summary>Free image <paramref name="i"/> and give back <paramref name="h"/> now, once every submit has landed: an
    /// eviction's room is for the allocation asking for it. The index reads the zero texel until it is handed out again.</summary>
    internal void DestroyNow(ImageId i, BindlessHandle h)
    {
        if (!OnThread("DestroyNow") || !WaitSubmitted() || !_images.Remove(i.Value, out VkImg g)) return;
        _sets.Stale = true;
        if (h.Value != 0 && h.Value < _bindlessCap)
        {
            WriteBindless((uint)h.Value, _bindless.Zero, _nearest);
            _bindless.Dying.Add((Next, (uint)h.Value));
        }
        Free(g);
    }

    /// <summary>Move-back tries this device has made: one a period at most, and only while something is in system RAM.</summary>
    internal int MoveBackTries => _moveBackTries;
}
