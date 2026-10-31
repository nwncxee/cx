using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// Alias groups: the images that name one <see cref="ImageDesc.Alias"/> are bound to one VMA allocation sized to
/// the largest of them. The group's owner is the member used last; a use of any other member starts it from layout
/// UNDEFINED behind a barrier on everything before, so no member keeps contents past another's use.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>One group's allocation and the images bound to it.</summary>
    private sealed class AliasGroup(string name)
    {
        public readonly string Name = name;
        public readonly List<VkImg> Members = new();
        public VmaAllocation Alloc;
        public ulong Size;
        public uint TypeBits;
        /// <summary>The member used last, whose contents the memory holds; null when none is known to.</summary>
        public VkImg? Owner;
        /// <summary>A member was destroyed and the allocation may now be larger than the rest need.</summary>
        public bool Shrink;
    }

    private readonly Dictionary<string, AliasGroup> _alias = new();

    /// <summary>A group member of <paramref name="d"/>: a fresh image bound into its group's allocation, which is
    /// made, or remade larger with every member rebound, when this one does not fit. Null id (logged) when refused.</summary>
    private ImageId CreateAliased(in ImageDesc d, string group)
    {
        if ((d.Use & ImageUse.Async) != 0)
            return Refused<ImageId>($"image {d.Label}: alias group {group} takes no async image -- the queues would order the shared memory apart");
        if (!_alias.TryGetValue(group, out AliasGroup? g)) _alias[group] = g = new AliasGroup(group);
        var img = new VkImg { Desc = d, Group = g };
        if (!MakeUnbound(img))
        {
            if (g.Members.Count == 0) _alias.Remove(group);
            return Refused<ImageId>($"image {d.Label}: vkCreateImage refused {d.Kind} {d.Fmt} {d.W}x{d.H}");
        }
        VkMemoryRequirements req;
        _vkd.vkGetImageMemoryRequirements(img.Image, &req);
        uint bits = g.Members.Count == 0 ? req.memoryTypeBits : g.TypeBits & req.memoryTypeBits;
        if (bits == 0)
        {
            _vkd.vkDestroyImage(img.Image);
            _log.Always($"VkBackend: image {d.Label} shares no memory type with alias group {group}'s members -- "
                      + $"made with memory of its own, {req.size >> 20} MiB the group does not save");
            return CreateImage(d with { Alias = null });
        }
        g.Members.Add(img);
        if (g.Alloc.IsNull || req.size > g.Size || bits != g.TypeBits)
        {
            if (!Rehome(g, Math.Max(req.size, g.Size), bits, img))
            {
                g.Members.Remove(img);
                _vkd.vkDestroyImage(img.Image);
                if (g.Members.Count == 0) _alias.Remove(group);
                return Refused<ImageId>($"image {d.Label}: VMA refused alias group {group} {Math.Max(req.size, g.Size)} bytes");
            }
        }
        else if (!BindInto(img, g))
        {
            g.Members.Remove(img);
            _vkd.vkDestroyImage(img.Image);
            return Refused<ImageId>($"image {d.Label}: binding into alias group {group} was refused");
        }
        Name(VkObjectType.Image, img.Image.Handle, d.Label);
        return new ImageId(_images.Add(img));
    }

    /// <summary>A VkImage for <paramref name="img"/>'s desc, no memory bound yet.</summary>
    private bool MakeUnbound(VkImg img)
    {
        VkImageCreateInfo ici = ImageInfo(img.Desc, null);
        VkImage image;
        if (_vkd.vkCreateImage(&ici, &image) != VkResult.Success) return false;
        img.Image = image;
        return true;
    }

    /// <summary>Bind <paramref name="img"/> at offset 0 of its group's allocation and make its view.</summary>
    private bool BindInto(VkImg img, AliasGroup g)
    {
        if (Vma.vmaBindImageMemory(_vma, g.Alloc, img.Image) != VkResult.Success) return false;
        img.View = View(img.Image, img.Desc, -1);
        img.Layout = VkImageLayout.Undefined;
        img.Written = false;
        img.BarBatch = -1;
        return true;
    }

    /// <summary>A new allocation of <paramref name="size"/> bytes for <paramref name="g"/>; every member but
    /// <paramref name="fresh"/> gets a new image on it, the old images and memory freed once no submit can reach them.
    /// Cold: a resize or a pass's first target.</summary>
    private bool Rehome(AliasGroup g, ulong size, uint bits, VkImg? fresh)
    {
        var req = new VkMemoryRequirements { size = size, alignment = 1, memoryTypeBits = bits };
        foreach (VkImg m in g.Members)
        {
            VkMemoryRequirements r;
            _vkd.vkGetImageMemoryRequirements(m.Image, &r);
            req.alignment = Math.Max(req.alignment, r.alignment);
        }
        // memory of its own: a lane slot carved out of a shared block leaves its old slot as slack the driver keeps
        var aci = new VmaAllocationCreateInfo { preferredFlags = VkMemoryPropertyFlags.DeviceLocal, flags = VmaAllocationCreateFlags.DedicatedMemory };
        if (Vma.vmaAllocateMemory(_vma, &req, &aci, out VmaAllocation a, out _) != VkResult.Success) return false;
        Vma.vmaSetAllocationName(_vma, a, "alias " + g.Name);
        VmaAllocation old = g.Alloc;
        g.Alloc = a; g.Size = size; g.TypeBits = bits; g.Owner = null; g.Shrink = false;
        _sets.Stale = true;
        foreach (VkImg m in g.Members)
        {
            if (m != fresh)
            {
                VkImage was = m.Image;
                VkImageView view = m.View;
                VkImageView[]? mips = m.MipViews, layers = m.LayerViews;
                LaterThanBindless(() => FreeViews(was, view, mips, layers));
                m.MipViews = null; m.LayerViews = null;
                if (!MakeUnbound(m)) { m.Image = default; m.View = default; Fault($"alias group {g.Name}: {m.Desc.Label} could not be remade"); continue; }
            }
            if (!BindInto(m, g)) Fault($"alias group {g.Name}: {m.Desc.Label} could not be bound");
        }
        if (old.IsNotNull) LaterThanBindless(() => Vma.vmaFreeMemory(_vma, old));
        return true;
    }

    /// <summary>An image's views and the image, its memory left alone.</summary>
    private void FreeViews(VkImage image, VkImageView view, VkImageView[]? mips, VkImageView[]? layers)
    {
        if (view.IsNotNull) _vkd.vkDestroyImageView(view);
        if (mips != null) foreach (VkImageView v in mips) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        if (layers != null) foreach (VkImageView v in layers) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        if (image.IsNotNull) _vkd.vkDestroyImage(image);
    }

    /// <summary>A member destroyed: the group's memory goes with its last member.</summary>
    private void Leave(VkImg img)
    {
        AliasGroup g = img.Group!;
        g.Members.Remove(img);
        if (g.Owner == img) g.Owner = null;
        if (g.Members.Count > 0) { g.Shrink = true; return; }
        _alias.Remove(g.Name);
        VmaAllocation a = g.Alloc;
        if (a.IsNotNull) LaterThanBindless(() => Vma.vmaFreeMemory(_vma, a));
    }

    /// <summary>Groups that lost a member move onto an allocation the size the rest need, so a smaller render size
    /// gives its memory back. At a graphics submit's head, before anything records.</summary>
    private void ShrinkGroups()
    {
        foreach (AliasGroup g in _alias.Values)
        {
            if (!g.Shrink) continue;
            g.Shrink = false;
            ulong need = 0;
            foreach (VkImg m in g.Members)
            {
                VkMemoryRequirements r;
                _vkd.vkGetImageMemoryRequirements(m.Image, &r);
                need = Math.Max(need, r.size);
            }
            if (need < g.Size && !Rehome(g, need, g.TypeBits, null)) Fault($"alias group {g.Name}: shrinking to {need} bytes was refused");
        }
    }

    /// <summary>Make <paramref name="img"/> its group's owner: from here its contents are undefined, and the barrier
    /// that moves it waits on everything before, whichever member it was. A member READ straight after another
    /// owned the memory reads that one's bytes -- a lane holding two live images -- and is a counted refusal.</summary>
    private bool Claim(VkImg img, VkImageLayout to)
    {
        AliasGroup? g = img.Group;
        if (g == null || g.Owner == img) return false;
        if (g.Owner != null && to is VkImageLayout.ShaderReadOnlyOptimal or VkImageLayout.TransferSrcOptimal)
            Fault($"alias group {g.Name}: {img.Desc.Label} is read while {g.Owner.Desc.Label} holds its memory -- the two are live at once");
        g.Owner = img;
        img.Layout = VkImageLayout.Undefined;
        img.Written = false;
        return true;
    }
}
