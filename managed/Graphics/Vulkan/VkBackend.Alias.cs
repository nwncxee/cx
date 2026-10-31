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
        /// <summary>The alignment the allocation was made for: the largest any member asked for.</summary>
        public ulong Align;
        public uint TypeBits;
        /// <summary>The member used last, whose contents the memory holds; null when none is known to.</summary>
        public VkImg? Owner;
        /// <summary>A member was destroyed and the allocation may now be larger than the rest need.</summary>
        public bool Shrink;
    }

    private readonly Dictionary<string, AliasGroup> _alias = new();
    /// <summary>A frame ended, so the next graphics submit moves the groups that lost a member.</summary>
    private bool _shrinkDue;

    /// <summary>A group member of <paramref name="d"/>: a fresh image bound into its group's allocation. The group
    /// goes with its last member, whether that member was made or refused.</summary>
    private ImageId CreateAliased(in ImageDesc d, string group)
    {
        if ((d.Use & ImageUse.Async) != 0)
            return Refused<ImageId>($"image {d.Label}: alias group {group} takes no async image -- the queues would order the shared memory apart");
        if (!_alias.TryGetValue(group, out AliasGroup? g)) _alias[group] = g = new AliasGroup(group);
        ImageId id = Attach(d, g);
        if (id.IsNull && g.Members.Count == 0) _alias.Remove(group);
        return id;
    }

    /// <summary>A fresh image for <paramref name="d"/> bound into <paramref name="g"/>'s allocation, which is made,
    /// or remade with every member rebound, when this one does not fit its size, alignment or memory types. Null id
    /// (logged) when refused.</summary>
    private ImageId Attach(in ImageDesc d, AliasGroup g)
    {
        var img = new VkImg { Desc = d, Group = g };
        if (!MakeUnbound(img))
            return Refused<ImageId>($"image {d.Label}: vkCreateImage refused {d.Kind} {d.Fmt} {d.W}x{d.H}");
        VkMemoryRequirements req;
        _vkd.vkGetImageMemoryRequirements(img.Image, &req);
        uint bits = g.Members.Count == 0 ? req.memoryTypeBits : g.TypeBits & req.memoryTypeBits;
        if (bits == 0)
        {
            _vkd.vkDestroyImage(img.Image);
            return Refused<ImageId>($"image {d.Label} shares no memory type with alias group {g.Name}'s members -- "
                                  + $"give it a lane of its own, or no alias at all");
        }
        g.Members.Add(img);
        ulong size = Math.Max(req.size, g.Size);
        bool fits = g.Alloc.IsNotNull && req.size <= g.Size && req.alignment <= g.Align && bits == g.TypeBits;
        if (!(fits ? BindInto(img, g) : Rehome(g, size, bits, img)))
        {
            g.Members.Remove(img);
            if (img.Image.IsNotNull) _vkd.vkDestroyImage(img.Image);
            return Refused<ImageId>($"image {d.Label}: alias group {g.Name} could not take it at {size} bytes");
        }
        if (img.Group == null)
            return Refused<ImageId>($"image {d.Label}: alias group {g.Name} dropped it rebinding its members (logged above)");
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
    /// A member the device refuses an image or a binding for leaves the group (<see cref="Orphan"/>). Cold: a resize
    /// or a pass's first target.</summary>
    private bool Rehome(AliasGroup g, ulong size, uint bits, VkImg? fresh)
    {
        var req = new VkMemoryRequirements { size = size, alignment = 1, memoryTypeBits = bits };
        foreach (VkImg m in g.Members)
        {
            VkMemoryRequirements r;
            _vkd.vkGetImageMemoryRequirements(m.Image, &r);
            req.alignment = Math.Max(req.alignment, r.alignment);
            if (m.Live)
                Fault($"alias group {g.Name}: {m.Desc.Label} was written this frame and the group's memory moves under "
                    + "it -- its contents are gone, and the read that wanted them is refused too");
        }
        // memory of its own: a lane slot carved out of a shared block leaves its old slot as slack the driver keeps
        var aci = new VmaAllocationCreateInfo { preferredFlags = VkMemoryPropertyFlags.DeviceLocal, flags = VmaAllocationCreateFlags.DedicatedMemory };
        if (Vma.vmaAllocateMemory(_vma, &req, &aci, out VmaAllocation a, out _) != VkResult.Success) return false;
        Vma.vmaSetAllocationName(_vma, a, "alias " + g.Name);
        VmaAllocation old = g.Alloc;
        g.Alloc = a; g.Size = size; g.Align = req.alignment; g.TypeBits = bits; g.Owner = null; g.Shrink = false;
        _sets.Stale = true;
        for (int i = g.Members.Count - 1; i >= 0; i--)
        {
            VkImg m = g.Members[i];
            if (m != fresh)
            {
                VkImage was = m.Image;
                VkImageView view = m.View;
                VkImageView[]? mips = m.MipViews, layers = m.LayerViews;
                LaterThanBindless(() => { FreeViews(view, mips, layers); _vkd.vkDestroyImage(was); });
                m.MipViews = null; m.LayerViews = null; m.Image = default; m.View = default;
                if (!MakeUnbound(m)) { Orphan(g, i, "could not be remade"); continue; }
            }
            if (!BindInto(m, g)) Orphan(g, i, "could not be bound");
        }
        if (old.IsNotNull) LaterThanBindless(() => Vma.vmaFreeMemory(_vma, old));
        return true;
    }

    /// <summary>The member at <paramref name="at"/> leaves <paramref name="g"/> with no image and no view, loudly:
    /// the device refused it one, and every later use of it is refused rather than reaching a null handle.</summary>
    private void Orphan(AliasGroup g, int at, string why)
    {
        VkImg m = g.Members[at];
        g.Members.RemoveAt(at);
        if (m.Image.IsNotNull) _vkd.vkDestroyImage(m.Image);
        if (g.Owner == m) g.Owner = null;
        m.Group = null;
        m.Image = default; m.View = default; m.MipViews = null; m.LayerViews = null;
        m.Live = false;
        Fault($"alias group {g.Name}: {m.Desc.Label} {why} -- it is out of the group, and every use of it is refused");
    }

    /// <summary>An image's views; the image and its memory are left alone.</summary>
    private void FreeViews(VkImageView view, VkImageView[]? mips, VkImageView[]? layers)
    {
        if (view.IsNotNull) _vkd.vkDestroyImageView(view);
        if (mips != null) foreach (VkImageView v in mips) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        if (layers != null) foreach (VkImageView v in layers) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
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

    /// <summary>One frame's use of the lanes is over: no member holds contents into the next frame, and a group that
    /// lost a member moves onto the size the rest need at the next submit. A present ends a frame; a harness that
    /// submits without showing a frame ends its own.</summary>
    internal void EndAliasFrame()
    {
        _shrinkDue = true;
        foreach (AliasGroup g in _alias.Values)
        {
            g.Owner = null;
            foreach (VkImg m in g.Members) m.Live = false;
        }
    }

    /// <summary>Groups that lost a member move onto an allocation the size the rest need, so a smaller render size
    /// gives its memory back. Only at the head of the first graphics submit of a frame: the move remakes every
    /// member's image, so between two submits of one frame it would take a written member's bytes away.</summary>
    private void ShrinkGroups()
    {
        if (!_shrinkDue) return;
        _shrinkDue = false;
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

    /// <summary>Make <paramref name="img"/> its group's owner: from a claim its contents are undefined, and the
    /// barrier that moves it waits on everything before, whichever member it was. A member is LIVE from the use that
    /// writes it until one that reads it or the frame's end, and two members live at once is a counted refusal -- a
    /// claim whose use reads bytes the group no longer holds, and one that discards a live member's, are both that.</summary>
    private bool Claim(VkImg img, VkImageLayout to, bool writes)
    {
        AliasGroup? g = img.Group;
        if (g == null) return false;
        bool overwrites = to switch
        {
            VkImageLayout.ShaderReadOnlyOptimal or VkImageLayout.TransferSrcOptimal
                or VkImageLayout.FragmentShadingRateAttachmentOptimalKHR => false,
            VkImageLayout.General => writes,
            _ => true,
        };
        bool claim = g.Owner != img;
        if (claim)
        {
            if (!overwrites && (g.Owner != null || img.Live)) LaneFault(g, img, "is read after its memory went to", g.Owner);
            else if (g.Owner is VkImg held && held.Live) LaneFault(g, img, "discards the unread write of", held);
            g.Owner = img;
            img.Layout = VkImageLayout.Undefined;
            img.Written = false;
        }
        img.Live = overwrites;
        return claim;
    }

    /// <summary>Two members of <paramref name="g"/> live at once, counted always and worded only while refusals are
    /// still logged: a barrier is hot, and the message costs an interpolation.</summary>
    private void LaneFault(AliasGroup g, VkImg img, string what, VkImg? other)
    {
        if (!Logging) { Fault(); return; }
        Fault($"alias group {g.Name}: {img.Desc.Label} {what} {other?.Desc.Label ?? "a move of the group's memory"}"
            + " -- the two are live at once");
    }
}
