using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>One host-visible buffer uploads are copied into ahead of the submit that moves them to the device.</summary>
    private sealed class UploadChunk
    {
        public VkBuffer Buffer;
        public VmaAllocation Alloc;
        public ulong Size, Used;
        /// <summary>The submit that last copied out of it; reusable once that one is past the frames in flight.</summary>
        public long At;
    }

    /// <summary>A copy waiting for the next submit: staged bytes into a buffer range, into one mip and layer of an
    /// image, or (<see cref="Mips"/>) an image's chain blitted down from its base level.</summary>
    private readonly record struct PendingCopy(ImageId Image, BufferId Buffer, int Mip, int Layer, long DstOffset,
                                               VkBuffer Src, ulong SrcOffset, ulong Bytes, bool Mips);

    /// <summary>Uploads made between submits: their staging and their copies, recorded at the head of the next
    /// submit; a chunk goes back to <see cref="Pool"/> after it and is reused once that submit is past.
    /// ponytail: the pool never shrinks -- an area load's high water stays mapped; trim it at an area edge if it shows.</summary>
    private sealed class UploadState
    {
        public readonly List<UploadChunk> Chunks = new();
        public readonly List<UploadChunk> Pool = new();
        public readonly List<PendingCopy> Copies = new();
        public readonly HashSet<uint> Touched = new();
    }

    private readonly UploadState _upload = new();

    /// <summary>The first staging chunk an upload opens: past a typical area's textures in a frame, so a load is a
    /// handful of chunks rather than one per texture.</summary>
    private const ulong UploadChunkBytes = 64ul << 20;
    /// <summary>Each staged copy starts on this boundary: a BC block, a texel of every format, and the device's
    /// optimalBufferCopyOffsetAlignment floor.</summary>
    private const ulong UploadAlign = 16;

    /// <summary>Bindless descriptors the table holds at most, clamped to the device's update-after-bind limit.</summary>
    private const uint BindlessWant = 65536;
    private readonly uint _bindlessCap, _maxDrawIndirect;

    /// <summary>The bindless table's free indices, and indices released but still visible to submits in flight.
    /// Index 0 is never handed out: a zero handle means "none" to every reader.</summary>
    private sealed class BindlessState
    {
        public readonly Stack<uint> Free = new();
        public readonly List<(long At, uint Index)> Dying = new();
        public uint Next = 1;
    }

    private readonly BindlessState _bindless = new();

    /// <summary>The seam's format as Vulkan's; Undefined for one the backend does not carry.</summary>
    internal static VkFormat Of(Format f) => f switch
    {
        Format.R8 => VkFormat.R8Unorm,
        Format.Rg8 => VkFormat.R8G8Unorm,
        Format.Rgba8 => VkFormat.R8G8B8A8Unorm,
        Format.Rgba8Srgb => VkFormat.R8G8B8A8Srgb,
        Format.R16F => VkFormat.R16Sfloat,
        Format.Rg16F => VkFormat.R16G16Sfloat,
        Format.Rgba16F => VkFormat.R16G16B16A16Sfloat,
        Format.R32F => VkFormat.R32Sfloat,
        Format.Rg32F => VkFormat.R32G32Sfloat,
        Format.Rgba32F => VkFormat.R32G32B32A32Sfloat,
        Format.R32Ui => VkFormat.R32Uint,
        Format.Rgba32Ui => VkFormat.R32G32B32A32Uint,
        Format.R11G11B10F => VkFormat.B10G11R11UfloatPack32,
        Format.D16 => VkFormat.D16Unorm,
        Format.D24 => VkFormat.X8D24UnormPack32,
        Format.D24S8 => VkFormat.D24UnormS8Uint,
        Format.D32F => VkFormat.D32Sfloat,
        Format.D32FS8 => VkFormat.D32SfloatS8Uint,
        Format.Bc1 => VkFormat.Bc1RgbUnormBlock,
        Format.Bc1A => VkFormat.Bc1RgbaUnormBlock,
        Format.Bc2 => VkFormat.Bc2UnormBlock,
        Format.Bc3 => VkFormat.Bc3UnormBlock,
        Format.Bc4 => VkFormat.Bc4UnormBlock,
        Format.Bc4S => VkFormat.Bc4SnormBlock,
        Format.Bc5 => VkFormat.Bc5UnormBlock,
        Format.Bc5S => VkFormat.Bc5SnormBlock,
        Format.Bc6U => VkFormat.Bc6hUfloatBlock,
        Format.Bc6S => VkFormat.Bc6hSfloatBlock,
        Format.Bc7 => VkFormat.Bc7UnormBlock,
        _ => VkFormat.Undefined,
    };

    private static VkImageAspectFlags Aspect(Format f)
        => !f.IsDepth() ? VkImageAspectFlags.Color
         : f.HasStencil() ? VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil : VkImageAspectFlags.Depth;

    /// <summary>An image of any kind the seam names: 2D, arrays, cubes and cube arrays, 3D; any mip count; colour,
    /// depth or block-compressed. Its contents are undefined until a render pass or an <see cref="Upload"/> writes them.</summary>
    public ImageId CreateImage(in ImageDesc d)
    {
        if (!OnThread("CreateImage")) return default;
        if (d.Samples > 1 || Of(d.Fmt) == VkFormat.Undefined || d.W <= 0 || d.H <= 0 || d.Mips < 1 || d.Layers < 1)
            return Refused<ImageId>($"image {d.Label}: {d.Kind} {d.Fmt} {d.W}x{d.H} x{d.Samples} with {d.Mips} mips, {d.Layers} layers");
        VkImageCreateInfo ici = ImageInfo(d, null);
        var aci = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice };
        VkImage image;
        VmaAllocation a;
        if (Vma.vmaCreateImage(_vma, &ici, &aci, &image, &a, null) != VkResult.Success)
            return Refused<ImageId>($"image {d.Label}: VMA refused {d.Kind} {d.Fmt} {d.W}x{d.H}");
        return new ImageId(_images.Add(new VkImg { Image = image, Alloc = a, View = View(image, d, -1), Desc = d }));
    }

    /// <summary><paramref name="d"/> as Vulkan creates it, optimally tiled. A cube's layers are its faces, six per cube.</summary>
    private static VkImageCreateInfo ImageInfo(in ImageDesc d, void* next)
    {
        bool cube = d.Kind is ImageKind.Cube or ImageKind.CubeArray;
        return new VkImageCreateInfo
        {
            pNext = next, imageType = d.Kind == ImageKind.Tex3D ? VkImageType.Image3D : VkImageType.Image2D,
            flags = cube ? VkImageCreateFlags.CubeCompatible : VkImageCreateFlags.None, format = Of(d.Fmt),
            extent = new VkExtent3D { width = (uint)d.W, height = (uint)d.H, depth = d.Kind == ImageKind.Tex3D ? (uint)Math.Max(d.D, 1) : 1 },
            mipLevels = (uint)d.Mips, arrayLayers = (uint)(d.Kind == ImageKind.Tex3D ? 1 : cube ? d.Layers * 6 : d.Layers),
            samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
            usage = Usage(d.Use, d.Fmt), sharingMode = VkSharingMode.Exclusive, initialLayout = VkImageLayout.Undefined,
        };
    }

    private static VkImageUsageFlags Usage(ImageUse use, Format f)
    {
        VkImageUsageFlags u = VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst;
        if ((use & ImageUse.ColorTarget) != 0 && !f.IsDepth()) u |= VkImageUsageFlags.ColorAttachment;
        if ((use & ImageUse.DepthTarget) != 0 || f.IsDepth()) u |= VkImageUsageFlags.DepthStencilAttachment;
        if ((use & ImageUse.Storage) != 0) u |= VkImageUsageFlags.Storage;
        return u;
    }

    /// <summary>A view of every mip (<paramref name="mip"/> -1) or of one, typed as the shader declares the kind:
    /// a single-layer 2D image as 2D, more layers as an array, a cube as a cube. A grey image reads red into all
    /// three colours and alpha as 1.</summary>
    private VkImageView View(VkImage image, in ImageDesc d, int mip)
    {
        VkImageViewType type = d.Kind switch
        {
            ImageKind.Tex3D => VkImageViewType.Image3D,
            ImageKind.Cube when d.Layers == 1 => VkImageViewType.ImageCube,
            ImageKind.Cube or ImageKind.CubeArray => VkImageViewType.ImageCubeArray,
            ImageKind.Tex2D when d.Layers == 1 => VkImageViewType.Image2D,
            _ => VkImageViewType.Image2DArray,
        };
        bool cube = d.Kind is ImageKind.Cube or ImageKind.CubeArray;
        var grey = new VkComponentMapping { r = VkComponentSwizzle.R, g = VkComponentSwizzle.R, b = VkComponentSwizzle.R, a = VkComponentSwizzle.One };
        var vci = new VkImageViewCreateInfo
        {
            image = image, viewType = type, format = Of(d.Fmt), components = (d.Use & ImageUse.Grey) != 0 ? grey : default,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = d.Fmt.IsDepth() ? VkImageAspectFlags.Depth : VkImageAspectFlags.Color,
                baseMipLevel = (uint)Math.Max(mip, 0), levelCount = mip < 0 ? (uint)d.Mips : 1,
                layerCount = d.Kind == ImageKind.Tex3D ? 1 : (uint)(cube ? d.Layers * 6 : d.Layers),
            },
        };
        VkImageView view;
        _vkd.vkCreateImageView(&vci, &view);
        return view;
    }

    /// <summary>The view binding one mip alone, made on first ask; mip -1 or a single-mip image is the whole view.</summary>
    private VkImageView ViewOf(VkImg img, int mip)
    {
        if (mip < 0 || img.Desc.Mips == 1) return img.View;
        img.MipViews ??= new VkImageView[img.Desc.Mips];
        if (img.MipViews[mip].IsNull) img.MipViews[mip] = View(img.Image, img.Desc, mip);
        return img.MipViews[mip];
    }

    /// <summary>Bytes one mip of one layer holds tightly packed: block rows for a compressed format.</summary>
    internal static long LevelBytes(in ImageDesc d, int mip)
    {
        int w = Math.Max(d.W >> mip, 1), h = Math.Max(d.H >> mip, 1), z = d.Kind == ImageKind.Tex3D ? Math.Max(d.D >> mip, 1) : 1;
        if (d.Fmt.IsCompressed()) return (long)((w + 3) / 4) * ((h + 3) / 4) * d.Fmt.BlockBytes();
        return (long)w * h * z * TexelBytes(d.Fmt);
    }

    private static int TexelBytes(Format f) => f switch
    {
        Format.R8 => 1,
        Format.Rg8 or Format.R16F or Format.D16 => 2,
        Format.Rg16F or Format.R32F or Format.R32Ui or Format.Rgba8 or Format.Rgba8Srgb or Format.R11G11B10F
            or Format.D24 or Format.D24S8 or Format.D32F => 4,
        Format.Rgba16F or Format.Rg32F or Format.D32FS8 => 8,
        _ => 16,
    };

    /// <summary>Copy <paramref name="bytes"/> -- one mip of one layer, tightly packed -- into <paramref name="i"/> at
    /// the head of the next submit. Cold: an area load. False (logged) when the size is wrong or staging is refused.</summary>
    internal bool Upload(ImageId i, int mip, int layer, ReadOnlySpan<byte> bytes)
    {
        if (!OnThread("Upload") || !_images.TryGet(i.Value, out VkImg img)) return Fault("Upload: stale image");
        if (mip < 0 || mip >= img.Desc.Mips || bytes.Length != LevelBytes(img.Desc, mip))
            return Fault($"Upload into {img.Desc.Label}: {bytes.Length} bytes for mip {mip}, which holds {LevelBytes(img.Desc, Math.Clamp(mip, 0, img.Desc.Mips - 1))}");
        if (!Stage(bytes, out VkBuffer src, out ulong at)) return false;
        _upload.Copies.Add(new PendingCopy(i, default, mip, layer, 0, src, at, (ulong)bytes.Length, false));
        return true;
    }

    /// <summary>Fill <paramref name="i"/>'s mips past the base from it, linearly downsampled, after the uploads before it.</summary>
    internal void GenerateMips(ImageId i)
    {
        if (OnThread("GenerateMips")) _upload.Copies.Add(new PendingCopy(i, default, 0, 0, 0, default, 0, 0, true));
    }

    /// <summary>Copy <paramref name="bytes"/> into <paramref name="b"/> at <paramref name="offset"/> at the head of the
    /// next submit: a large, cold write (a mesh arena's vertices) that a recorded update would carry inline.</summary>
    internal bool Upload(BufferId b, long offset, ReadOnlySpan<byte> bytes)
    {
        if (!OnThread("Upload") || !_buffers.TryGet(b.Value, out VkBuf buf)) return Fault("Upload: stale buffer");
        if (offset < 0 || offset + bytes.Length > buf.Desc.Bytes) return Fault($"Upload of {bytes.Length} bytes at {offset} into {buf.Desc.Label}'s {buf.Desc.Bytes}");
        if (bytes.IsEmpty) return true;
        if (!Stage(bytes, out VkBuffer src, out ulong at)) return false;
        _upload.Copies.Add(new PendingCopy(default, b, 0, 0, offset, src, at, (ulong)bytes.Length, false));
        return true;
    }

    /// <summary>Copy <paramref name="bytes"/> into the open upload chunk, opening one when it is full.</summary>
    private bool Stage(ReadOnlySpan<byte> bytes, out VkBuffer src, out ulong at)
    {
        ulong len = (ulong)bytes.Length;
        UploadChunk? c = _upload.Chunks.Count > 0 ? _upload.Chunks[^1] : null;
        if (c == null || c.Used + len > c.Size)
        {
            c = null;
            List<UploadChunk> pool = _upload.Pool;
            for (int k = 0; k < pool.Count && c == null; k++)
                if (pool[k].Size >= len && _submits - pool[k].At >= DeadAfter) { c = pool[k]; pool.RemoveAt(k); }
            if (c == null)
            {
                c = new UploadChunk { Size = Math.Max(UploadChunkBytes, len) };
                if (!HostBuffer(c.Size, VkBufferUsageFlags.TransferSrc, VmaAllocationCreateFlags.HostAccessSequentialWrite, out c.Buffer, out c.Alloc))
                {
                    src = default; at = 0;
                    return Fault($"upload staging: VMA refused {c.Size} bytes");
                }
            }
            c.Used = 0;
            _upload.Chunks.Add(c);
        }
        src = c.Buffer; at = c.Used;
        fixed (byte* p = bytes)
            if (Vma.vmaCopyMemoryToAllocation(_vma, p, c.Alloc, at, len) != VkResult.Success)
                return Fault("upload staging: the copy into the mapped allocation was refused");
        c.Used = (at + len + UploadAlign - 1) & ~(UploadAlign - 1);
        return true;
    }

    /// <summary>Record every pending upload at the head of the submit, each image left readable, and hand the staging
    /// to the deaths queue. An image or buffer destroyed since its upload is skipped.</summary>
    private void FlushUploads()
    {
        UploadState u = _upload;
        if (u.Copies.Count == 0) return;
        foreach (PendingCopy p in u.Copies)
        {
            if (!p.Buffer.IsNull)
            {
                if (!_buffers.TryGet(p.Buffer.Value, out VkBuf b)) continue;
                var region = new VkBufferCopy { srcOffset = p.SrcOffset, dstOffset = (ulong)p.DstOffset, size = p.Bytes };
                _vkd.vkCmdCopyBuffer(_cmd, p.Src, b.Buffer, 1, &region);
                continue;
            }
            if (!_images.TryGet(p.Image.Value, out VkImg img)) continue;
            if (p.Mips) { BlitMips(img); continue; }
            if (img.Layout != VkImageLayout.TransferDstOptimal) Transition(img, VkImageLayout.TransferDstOptimal);
            u.Touched.Add(p.Image.Value);
            var r = new VkBufferImageCopy
            {
                bufferOffset = p.SrcOffset,
                imageSubresource = new VkImageSubresourceLayers { aspectMask = Aspect(img.Desc.Fmt), mipLevel = (uint)p.Mip, baseArrayLayer = (uint)p.Layer, layerCount = 1 },
                imageExtent = new VkExtent3D
                {
                    width = (uint)Math.Max(img.Desc.W >> p.Mip, 1), height = (uint)Math.Max(img.Desc.H >> p.Mip, 1),
                    depth = img.Desc.Kind == ImageKind.Tex3D ? (uint)Math.Max(img.Desc.D >> p.Mip, 1) : 1,
                },
            };
            _vkd.vkCmdCopyBufferToImage(_cmd, p.Src, img.Image, VkImageLayout.TransferDstOptimal, 1, &r);
        }
        foreach (uint id in u.Touched)
            if (_images.TryGet(id, out VkImg img)) Transition(img, VkImageLayout.ShaderReadOnlyOptimal);
        FullBarrier();
        foreach (UploadChunk c in u.Chunks) { c.At = _submits; u.Pool.Add(c); }
        u.Chunks.Clear();
        u.Copies.Clear();
        u.Touched.Clear();
    }

    /// <summary>Each mip from the one above it by a linear blit, every layer at once; the image ends shader-readable.</summary>
    private void BlitMips(VkImg img)
    {
        if (img.Layout != VkImageLayout.TransferDstOptimal) Transition(img, VkImageLayout.TransferDstOptimal);
        uint layers = (uint)(img.Desc.Kind is ImageKind.Cube or ImageKind.CubeArray ? img.Desc.Layers * 6 : img.Desc.Layers);
        for (int m = 1; m < img.Desc.Mips; m++)
        {
            MipBarrier(img, m - 1, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);
            var b = new VkImageBlit
            {
                srcSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, mipLevel = (uint)(m - 1), layerCount = layers },
                dstSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, mipLevel = (uint)m, layerCount = layers },
            };
            b.srcOffsets[1] = new VkOffset3D { x = Math.Max(img.Desc.W >> (m - 1), 1), y = Math.Max(img.Desc.H >> (m - 1), 1), z = 1 };
            b.dstOffsets[1] = new VkOffset3D { x = Math.Max(img.Desc.W >> m, 1), y = Math.Max(img.Desc.H >> m, 1), z = 1 };
            _vkd.vkCmdBlitImage(_cmd, img.Image, VkImageLayout.TransferSrcOptimal, img.Image, VkImageLayout.TransferDstOptimal, 1, &b, VkFilter.Linear);
            MipBarrier(img, m - 1, VkImageLayout.TransferSrcOptimal, VkImageLayout.TransferDstOptimal);
        }
        img.Layout = VkImageLayout.TransferDstOptimal;
        Transition(img, VkImageLayout.ShaderReadOnlyOptimal);
    }

    private void MipBarrier(VkImg img, int mip, VkImageLayout from, VkImageLayout to)
    {
        var b = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllCommands, dstAccessMask = VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite,
            oldLayout = from, newLayout = to, srcQueueFamilyIndex = QueueFamilyIgnored, dstQueueFamilyIndex = QueueFamilyIgnored,
            image = img.Image,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color, baseMipLevel = (uint)mip, levelCount = 1,
                layerCount = Vortice.Vulkan.Vulkan.VK_REMAINING_ARRAY_LAYERS,
            },
        };
        var di = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &b };
        _vkd.vkCmdPipelineBarrier2(_cmd, &di);
    }

    /// <summary>The bindless table's layout, pool and one set: a variable-length, partially bound, update-after-bind
    /// array of combined samplers, so a texture joins or leaves it between submits without a rebind.</summary>
    private (VkDescriptorSetLayout, VkDescriptorPool, VkDescriptorSet, uint) MakeBindless()
    {
        var v12 = new VkPhysicalDeviceVulkan12Properties();
        var props = new VkPhysicalDeviceProperties2 { pNext = &v12 };
        _vki.vkGetPhysicalDeviceProperties2(_phys, &props);
        uint cap = Math.Min(BindlessWant, Math.Min(v12.maxDescriptorSetUpdateAfterBindSampledImages,
                                                   v12.maxPerStageDescriptorUpdateAfterBindSampledImages));
        var binding = new VkDescriptorSetLayoutBinding
        {
            binding = 0, descriptorType = VkDescriptorType.CombinedImageSampler, descriptorCount = cap, stageFlags = VkShaderStageFlags.All,
        };
        VkDescriptorBindingFlags flags = VkDescriptorBindingFlags.PartiallyBound | VkDescriptorBindingFlags.UpdateAfterBind
                                       | VkDescriptorBindingFlags.VariableDescriptorCount;
        var bf = new VkDescriptorSetLayoutBindingFlagsCreateInfo { bindingCount = 1, pBindingFlags = &flags };
        var lci = new VkDescriptorSetLayoutCreateInfo
        {
            pNext = &bf, flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPool, bindingCount = 1, pBindings = &binding,
        };
        VkDescriptorSetLayout layout;
        Check(_vkd.vkCreateDescriptorSetLayout(&lci, &layout), "vkCreateDescriptorSetLayout (bindless)");
        var size = new VkDescriptorPoolSize { type = VkDescriptorType.CombinedImageSampler, descriptorCount = cap };
        var pci = new VkDescriptorPoolCreateInfo { flags = VkDescriptorPoolCreateFlags.UpdateAfterBind, maxSets = 1, poolSizeCount = 1, pPoolSizes = &size };
        VkDescriptorPool pool;
        Check(_vkd.vkCreateDescriptorPool(&pci, &pool), "vkCreateDescriptorPool (bindless)");
        var count = new VkDescriptorSetVariableDescriptorCountAllocateInfo { descriptorSetCount = 1, pDescriptorCounts = &cap };
        var ai = new VkDescriptorSetAllocateInfo { pNext = &count, descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
        VkDescriptorSet set;
        Check(_vkd.vkAllocateDescriptorSets(&ai, &set), "vkAllocateDescriptorSets (bindless)");
        return (layout, pool, set, cap);
    }

    /// <summary>A bindless handle for <paramref name="i"/> sampled through <paramref name="s"/> (null: nearest), the
    /// descriptor index in the low word; the null handle (logged) when the table is full. The image must be readable
    /// by the time a draw indexes it -- an upload leaves it so.</summary>
    internal BindlessHandle Bindless(ImageId i, SamplerId s)
    {
        if (!OnThread("Bindless") || !_images.TryGet(i.Value, out VkImg img)) return Refused<BindlessHandle>("Bindless: stale image");
        VkSampler sampler = _nearest;
        if (!s.IsNull && !_samplers.TryGet(s.Value, out sampler)) return Refused<BindlessHandle>("Bindless: stale sampler");
        BindlessState t = _bindless;
        for (int k = t.Dying.Count - 1; k >= 0; k--)
            if (_submits - t.Dying[k].At >= DeadAfter) { t.Free.Push(t.Dying[k].Index); t.Dying.RemoveAt(k); }
        uint index = t.Free.Count > 0 ? t.Free.Pop() : t.Next < _bindlessCap ? t.Next++ : 0;
        if (index == 0) return Refused<BindlessHandle>($"Bindless: all {_bindlessCap} descriptors are in use");
        var info = new VkDescriptorImageInfo { sampler = sampler, imageView = img.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var w = new VkWriteDescriptorSet
        {
            dstSet = _bindlessSet, dstBinding = 0, dstArrayElement = index, descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler, pImageInfo = &info,
        };
        _vkd.vkUpdateDescriptorSets(1, &w, 0, null);
        return new BindlessHandle(index);
    }

    /// <summary>Give <paramref name="h"/>'s index back once no submit in flight can still index it.</summary>
    internal void Unbind(BindlessHandle h)
    {
        if (OnThread("Unbind") && h.Value != 0 && h.Value < _bindlessCap) _bindless.Dying.Add((_submits, (uint)h.Value));
    }

    /// <summary>The bindless table on set 1 of both bind points, once at the head of a submit: every pipeline shares
    /// the layout, so no later bind disturbs it.</summary>
    private void BindBindless()
    {
        VkDescriptorSet set = _bindlessSet;
        _vkd.vkCmdBindDescriptorSets(_cmd, VkPipelineBindPoint.Graphics, _pipeLayout, VulkanToolchain.BindlessSet, 1, &set, 0, null);
        _vkd.vkCmdBindDescriptorSets(_cmd, VkPipelineBindPoint.Compute, _pipeLayout, VulkanToolchain.BindlessSet, 1, &set, 0, null);
    }
}
