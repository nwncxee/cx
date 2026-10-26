using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Rhi;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>The seam on Vulkan 1.3: one instance, one device matched to GL's by UUID, one queue, VMA, no surface. A
/// pass's images are Vulkan's alone; the one thing GL sees is the composite the frame lands in
/// (<see cref="CompositeBack"/>), which the engine's context presents. Made, used and dropped on the thread holding
/// Alloy's context.</summary>
internal sealed unsafe partial class VkBackend : IDevice
{
    /// <summary>One image: a VMA allocation, or (<see cref="Ext"/> set) a composite image GL imports.
    /// <see cref="Layout"/> is the layout the last recorded barrier left it in, across submits.</summary>
    private sealed class VkImg
    {
        public VkImage Image;
        public VmaAllocation Alloc;
        public VkImageView View;
        /// <summary>One view per mip, made the first time a mip is bound alone (a storage write down a chain).</summary>
        public VkImageView[]? MipViews;
        /// <summary>One single-layer 2D view per layer of an array or cube array, made the first time that layer is
        /// attached alone (the caster bake's per-layer clear).</summary>
        public VkImageView[]? LayerViews;
        public ImageDesc Desc;
        public VkImageLayout Layout;
        /// <summary>The recording that last noted this image's layout for its undo (<see cref="Remember"/>).</summary>
        public long UndoAt = -1;
        public Exported? Ext;
    }

    private readonly record struct VkBuf(VkBuffer Buffer, VmaAllocation Alloc, BufferDesc Desc);
    private readonly record struct VkShader(VkShaderModule Module, ShaderStage Stage);
    /// <summary>A pipeline, and the depth and raster state its desc named, which a bind restates dynamically.</summary>
    private readonly record struct VkPipe(VkPipeline Pipeline, bool Compute, DepthState Depth, RasterState Raster);

    /// <summary>One submit's recording resources; two alternate, so a submit waits on the one two back.</summary>
    private sealed class Slot
    {
        public VkCommandBuffer Cmd;
        public VkFence Fence;
        public VkDescriptorPool Pool;
        /// <summary>Host-visible staging an update too large to record inline is copied through; reused from
        /// <see cref="StageUsed"/> 0 once the slot's fence has passed, grown when one submit outgrows it.</summary>
        public VkBuffer Stage;
        public VmaAllocation StageAlloc;
        public ulong StageBytes, StageUsed;
    }

    /// <summary>Why this machine cannot run the backend, asked once: null until asked, "" when it can.</summary>
    private sealed class ProbeState
    {
        public string? Refusal;
        public bool LoaderUp;
    }

    private static readonly ProbeState _probe = new();
    private static readonly bool Win = OperatingSystem.IsWindows();

    /// <summary>The floor: dynamic rendering and synchronization2 are core, and it fixes the SPIR-V target.</summary>
    private static readonly VkVersion ApiFloor = VkVersion.Version_1_3;
    private const int LoggedFaults = 16;
    private const int Slots = 2;
    /// <summary>Submits a freed object waits out: past the slot ring, and past the graveyard's hold on a composite's GL texture.</summary>
    private const int DeadAfter = GpuGraveyard.FramesInFlight * 2;
    /// <summary>Descriptor sets one submit may bind; a full pool refuses the bind, logged.</summary>
    private const uint SetsPerSubmit = 4096;
    /// <summary>The one set's shape: each kind of binding, its count, at its <see cref="VulkanToolchain"/> base.</summary>
    private const uint SampledSlots = 48, UniformSlots = 12, StorageSlots = 16, ImageSlots = 8;
    private const ulong FenceTimeoutNs = 2_000_000_000;

    private readonly ExtenderLog _log;
    private readonly int _thread;
    private readonly VkInstanceApi _vki;
    private readonly VkDeviceApi _vkd;
    private readonly VkPhysicalDevice _phys;
    private readonly VkDevice _device;
    private readonly VkQueue _queue;
    private readonly uint _family;
    private readonly VmaAllocator _vma;
    private readonly VkCommandPool _cmdPool;
    private readonly Slot[] _slots = new Slot[Slots];
    private readonly VkDescriptorSetLayout _setLayout, _bindlessLayout;
    private readonly VkDescriptorPool _bindlessPool;
    private readonly VkDescriptorSet _bindlessSet;
    private readonly VkPipelineLayout _pipeLayout;
    private readonly VkSampler _nearest;
    private readonly VkPhysicalDeviceMemoryProperties _mem;
    private readonly bool _depthClamp;

    private readonly HandleTable<VkBuf> _buffers = new();
    private readonly HandleTable<VkImg> _images = new();
    private readonly HandleTable<VkSampler> _samplers = new();
    private readonly HandleTable<VkShader> _shaders = new();
    private readonly HandleTable<VkPipe> _pipelines = new();
    private readonly HandleTable<VertexLayoutDesc> _layouts = new();
    /// <summary>Deaths waiting out the frames in flight: the Vulkan half, and a composite's GL half run first.</summary>
    private readonly List<(long At, Action Free, Action? Gl)> _dead = new();
    private long _submits;
    private int _faults;

    public DeviceCaps Caps { get; }

    /// <summary>Why the backend cannot run here, as a constant string, or null when it can. Asked once, from the
    /// verdict, with the ENGINE's context current: the interop extensions and the device UUID are the driver's.</summary>
    internal static string? Refusal(ExtenderLog log)
    {
        if (_probe.Refusal == null)
        {
            _probe.Refusal = Open(log, out VkInstance inst, out _, out _, out _) ?? "";
            if (inst.IsNotNull) new VkInstanceApi(in inst).vkDestroyInstance();
            log.Always(_probe.Refusal.Length == 0
                ? "VkBackend: probe PASSED -- a Vulkan 1.3 device shares GL's UUID and both sides carry the interop extensions"
                : $"VkBackend: probe REFUSED -- {_probe.Refusal}");
        }
        return _probe.Refusal.Length == 0 ? null : _probe.Refusal;
    }

    /// <summary>The instance and the physical device GL runs on, or why not. The instance is returned live
    /// (null on the first refusals) for the caller to keep or destroy.</summary>
    private static string? Open(ExtenderLog log, out VkInstance inst, out VkPhysicalDevice phys, out uint family,
                                out VkPhysicalDeviceFeatures feats)
    {
        inst = default; phys = default; family = 0; feats = default;
        if (GlInterop.Refusal() is string gl) return gl;
        foreach (string leaf in VulkanToolchain.Libraries)
            if (!VulkanToolchain.Load(leaf)) return "the staged shaderc or VMA library did not load from the install folder";
        if (!_probe.LoaderUp)
        {
            VkResult init;
            try { init = vkInitialize(); }
            catch (DllNotFoundException) { return "this machine has no Vulkan loader"; }
            if (init != VkResult.Success) return "the Vulkan loader would not initialize";
            _probe.LoaderUp = true;
        }
        if (vkEnumerateInstanceVersion() < ApiFloor) return "the Vulkan loader is older than 1.3";
        var app = new VkApplicationInfo { apiVersion = ApiFloor };
        var ci = new VkInstanceCreateInfo { pApplicationInfo = &app };
        if (vkCreateInstance(&ci, null, out inst) != VkResult.Success) return "vkCreateInstance refused a Vulkan 1.3 instance";
        var vki = new VkInstanceApi(in inst);
        uint n = 0;
        vki.vkEnumeratePhysicalDevices(&n, null);
        if (n == 0) return "the Vulkan loader lists no device";
        VkPhysicalDevice* list = stackalloc VkPhysicalDevice[(int)n];
        vki.vkEnumeratePhysicalDevices(&n, list);
        string why = "no Vulkan device has GL's device UUID";
        for (int i = 0; i < n; i++)
        {
            var id = new VkPhysicalDeviceIDProperties();
            var props = new VkPhysicalDeviceProperties2 { pNext = &id };
            vki.vkGetPhysicalDeviceProperties2(list[i], &props);
            if (!GlInterop.OnDevice(new ReadOnlySpan<byte>(id.deviceUUID, GlInterop.UUID_SIZE_EXT))) continue;
            if (props.properties.apiVersion < ApiFloor) return "the Vulkan device GL runs on is below Vulkan 1.3";
            if (!HasExt(vki, list[i], "VK_EXT_depth_clip_control\0"u8))
                return "the Vulkan device lacks VK_EXT_depth_clip_control (GL's -1..1 clip depth)";
            if (!HasExt(vki, list[i], Win ? "VK_KHR_external_memory_win32\0"u8 : "VK_KHR_external_memory_fd\0"u8)
                || !HasExt(vki, list[i], Win ? "VK_KHR_external_semaphore_win32\0"u8 : "VK_KHR_external_semaphore_fd\0"u8))
                return Win ? "the Vulkan device lacks VK_KHR_external_memory_win32/VK_KHR_external_semaphore_win32"
                           : "the Vulkan device lacks VK_KHR_external_memory_fd/VK_KHR_external_semaphore_fd";
            var clip = new VkPhysicalDeviceDepthClipControlFeaturesEXT();
            var f11 = new VkPhysicalDeviceVulkan11Features { pNext = &clip };
            var f12 = new VkPhysicalDeviceVulkan12Features { pNext = &f11 };
            var f13 = new VkPhysicalDeviceVulkan13Features { pNext = &f12 };
            var f2 = new VkPhysicalDeviceFeatures2 { pNext = &f13 };
            vki.vkGetPhysicalDeviceFeatures2(list[i], &f2);
            if (!f13.dynamicRendering || !f13.synchronization2) return "the Vulkan device lacks dynamic rendering or synchronization2";
            if (Lacks(in f2.features, in f11, in f12) is string lack) return $"the Vulkan device lacks {lack}";
            if (!clip.depthClipControl) return "the Vulkan device lacks depthClipControl";
            uint qn = 0;
            vki.vkGetPhysicalDeviceQueueFamilyProperties(list[i], &qn, null);
            VkQueueFamilyProperties* q = stackalloc VkQueueFamilyProperties[(int)qn];
            vki.vkGetPhysicalDeviceQueueFamilyProperties(list[i], &qn, q);
            for (uint k = 0; k < qn; k++)
            {
                if ((q[k].queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute)) != (VkQueueFlags.Graphics | VkQueueFlags.Compute)) continue;
                phys = list[i]; family = k; feats = f2.features;
                log.Info($"VkBackend: device '{new string((sbyte*)props.properties.deviceName)}' Vulkan {Version(props.properties.apiVersion)}, queue family {k}");
                return null;
            }
            why = "the Vulkan device GL runs on has no graphics+compute queue";
        }
        return why;
    }

    /// <summary>The first feature the batch pass needs that the device does not have, or null: draw parameters for the
    /// draw index, indirect counts for the cull's lists, descriptor indexing for bindless textures, BC textures, cube
    /// arrays, robust buffer access so a stand-in buffer shorter than a stage's reach reads zeros, and a vertex stage's
    /// <c>gl_Layer</c> for the caster bake, which renders every stale view of one array in one layered pass.</summary>
    private static string? Lacks(in VkPhysicalDeviceFeatures f, in VkPhysicalDeviceVulkan11Features f11, in VkPhysicalDeviceVulkan12Features f12)
    {
        if (!f11.shaderDrawParameters) return "shaderDrawParameters";
        if (!f12.drawIndirectCount) return "drawIndirectCount";
        if (!f12.shaderOutputLayer) return "shaderOutputLayer (the caster bake's layered pass)";
        if (!f12.runtimeDescriptorArray || !f12.descriptorBindingPartiallyBound || !f12.descriptorBindingVariableDescriptorCount
            || !f12.shaderSampledImageArrayNonUniformIndexing || !f12.descriptorBindingSampledImageUpdateAfterBind)
            return "the descriptor indexing bindless textures need";
        if (!f.multiDrawIndirect) return "multiDrawIndirect";
        if (!f.textureCompressionBC) return "BC texture compression";
        if (!f.imageCubeArray) return "cube-array images";
        if (!f.independentBlend) return "independent blend";
        if (!f.robustBufferAccess) return "robust buffer access";
        if (!f.geometryShader) return "geometryShader (a lit fragment's gl_PrimitiveID)";
        return null;
    }

    /// <summary>major.minor.patch: the version's own formatting leads with the variant field.</summary>
    private static string Version(VkVersion v) => $"{v.Major}.{v.Minor}.{v.Patch}";

    private static bool HasExt(VkInstanceApi vki, VkPhysicalDevice d, ReadOnlySpan<byte> name)
    {
        uint n = 0;
        vki.vkEnumerateDeviceExtensionProperties(d, null, &n, null);
        var props = new VkExtensionProperties[n];
        fixed (VkExtensionProperties* p = props) vki.vkEnumerateDeviceExtensionProperties(d, null, &n, p);
        for (int i = 0; i < n; i++)
            fixed (byte* e = props[i].extensionName)
                if (new ReadOnlySpan<byte>(e, name.Length).SequenceEqual(name)) return true;
        return false;
    }

    /// <summary>The device, or null -- logged -- when the probe refuses or a creation step fails. Made with Alloy's
    /// 4.6 context current: the semaphores are imported into it.</summary>
    public static VkBackend? TryCreate(ExtenderLog log)
    {
        if (Refusal(log) is string why) { log.Always($"VkBackend: REFUSED -- {why}"); return null; }
        if (Open(log, out VkInstance inst, out VkPhysicalDevice phys, out uint family, out VkPhysicalDeviceFeatures feats) is string late)
        {
            if (inst.IsNotNull) new VkInstanceApi(in inst).vkDestroyInstance();
            log.Always($"VkBackend: REFUSED in Alloy's context -- {late}");
            return null;
        }
        try { return new VkBackend(log, inst, phys, family, feats); }
        catch (InvalidOperationException e)
        {
            log.Always($"VkBackend: REFUSED -- {e.Message}");
            return null;
        }
    }

    private VkBackend(ExtenderLog log, VkInstance inst, VkPhysicalDevice phys, uint family, VkPhysicalDeviceFeatures has)
    {
        _log = log;
        _thread = Environment.CurrentManagedThreadId;
        _vki = new VkInstanceApi(in inst);
        _phys = phys;
        _family = family;
        _depthClamp = has.depthClamp;

        float prio = 1f;
        var qci = new VkDeviceQueueCreateInfo { queueFamilyIndex = family, queueCount = 1, pQueuePriorities = &prio };
        var clip = new VkPhysicalDeviceDepthClipControlFeaturesEXT { depthClipControl = true };
        var f11 = new VkPhysicalDeviceVulkan11Features { pNext = &clip, shaderDrawParameters = true };
        var f12 = new VkPhysicalDeviceVulkan12Features
        {
            pNext = &f11, drawIndirectCount = true, runtimeDescriptorArray = true, descriptorBindingPartiallyBound = true,
            descriptorBindingVariableDescriptorCount = true, shaderSampledImageArrayNonUniformIndexing = true,
            descriptorBindingSampledImageUpdateAfterBind = true, shaderOutputLayer = true,
        };
        var f13 = new VkPhysicalDeviceVulkan13Features { pNext = &f12, dynamicRendering = true, synchronization2 = true };
        var feats = new VkPhysicalDeviceFeatures2 { pNext = &f13 };
        feats.features.depthClamp = has.depthClamp;
        feats.features.samplerAnisotropy = has.samplerAnisotropy;
        feats.features.multiDrawIndirect = true;
        feats.features.textureCompressionBC = true;
        feats.features.imageCubeArray = true;
        feats.features.independentBlend = true;
        feats.features.robustBufferAccess = true;
        feats.features.fragmentStoresAndAtomics = has.fragmentStoresAndAtomics;
        feats.features.geometryShader = true;
        ReadOnlySpan<byte> mem = Win ? "VK_KHR_external_memory_win32\0"u8 : "VK_KHR_external_memory_fd\0"u8;
        ReadOnlySpan<byte> sem = Win ? "VK_KHR_external_semaphore_win32\0"u8 : "VK_KHR_external_semaphore_fd\0"u8;
        VkDevice device;
        fixed (byte* m = mem) fixed (byte* s = sem) fixed (byte* c = "VK_EXT_depth_clip_control\0"u8)
        {
            byte** exts = stackalloc byte*[3] { m, s, c };
            var dci = new VkDeviceCreateInfo
            {
                pNext = &feats, queueCreateInfoCount = 1, pQueueCreateInfos = &qci,
                enabledExtensionCount = 3, ppEnabledExtensionNames = exts,
            };
            Check(_vki.vkCreateDevice(phys, &dci, null, out device), "vkCreateDevice");
        }
        _device = device;
        _vkd = new VkDeviceApi(_vki, in device);
        _vkd.vkGetDeviceQueue(family, 0, out _queue);
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(phys, &mp);
        _mem = mp;

        _vma = CreateAllocator(inst);
        var pci = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = family };
        VkCommandPool pool;
        Check(_vkd.vkCreateCommandPool(&pci, &pool), "vkCreateCommandPool");
        _cmdPool = pool;
        for (int i = 0; i < Slots; i++) _slots[i] = MakeSlot();

        VkPhysicalDeviceProperties props;
        _vki.vkGetPhysicalDeviceProperties(phys, &props);
        ref VkPhysicalDeviceLimits l = ref props.limits;
        OpenPipelineCache(in props);
        OpenTiming(in props);
        _setLayout = MakeSetLayout();
        (_bindlessLayout, _bindlessPool, _bindlessSet, _bindlessCap) = MakeBindless();
        VkDescriptorSetLayout* sl = stackalloc VkDescriptorSetLayout[2] { _setLayout, _bindlessLayout };
        var push = new VkPushConstantRange { stageFlags = VkShaderStageFlags.All, offset = 0, size = PushConstants.Bytes };
        var plci = new VkPipelineLayoutCreateInfo { setLayoutCount = 2, pSetLayouts = sl, pushConstantRangeCount = 1, pPushConstantRanges = &push };
        VkPipelineLayout pl;
        Check(_vkd.vkCreatePipelineLayout(&plci, &pl), "vkCreatePipelineLayout");
        _pipeLayout = pl;
        _nearest = Sampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Clamp));
        MakeBindlessZero();
        Caps = new DeviceCaps((int)l.maxImageDimension2D, (int)l.maxImageArrayLayers, (int)l.maxColorAttachments, 1,
            (int)l.maxPerStageDescriptorSampledImages, (int)l.maxDescriptorSetSampledImages,
            (int)l.maxPerStageDescriptorStorageBuffers, (int)l.maxPerStageDescriptorStorageBuffers,
            (int)l.minUniformBufferOffsetAlignment, (int)l.minStorageBufferOffsetAlignment, has.samplerAnisotropy ? l.maxSamplerAnisotropy : 1f);
        _maxDrawIndirect = l.maxDrawIndirectCount;
        log.Always($"VkBackend: LIVE -- {Caps}, {_bindlessCap} bindless textures; the composite is the only image GL imports");
    }

    private VmaAllocator CreateAllocator(VkInstance inst)
    {
        // VMA resolves the rest of Vulkan itself from the two loaders it is handed
        delegate* unmanaged<void> gipa = vkGetInstanceProcAddr(inst, "vkGetInstanceProcAddr"u8);
        delegate* unmanaged<void> gdpa = vkGetInstanceProcAddr(inst, "vkGetDeviceProcAddr"u8);
        var fns = new VmaVulkanFunctions
        {
            vkGetInstanceProcAddr = (delegate* unmanaged<VkInstance, byte*, PFN_vkVoidFunction>)gipa,
            vkGetDeviceProcAddr = (delegate* unmanaged<VkDevice, byte*, PFN_vkVoidFunction>)gdpa,
        };
        var ci = new VmaAllocatorCreateInfo
        {
            physicalDevice = _phys, device = _device, instance = inst, vulkanApiVersion = ApiFloor, pVulkanFunctions = &fns,
        };
        Check(Vma.vmaCreateAllocator(&ci, out VmaAllocator a), "vmaCreateAllocator");
        return a;
    }

    private Slot MakeSlot()
    {
        var s = new Slot();
        var ai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cb;
        Check(_vkd.vkAllocateCommandBuffers(&ai, &cb), "vkAllocateCommandBuffers");
        s.Cmd = cb;
        var fci = new VkFenceCreateInfo { flags = VkFenceCreateFlags.Signaled };
        VkFence f;
        Check(_vkd.vkCreateFence(&fci, &f), "vkCreateFence");
        s.Fence = f;
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[4]
        {
            new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = SetsPerSubmit * SampledSlots },
            new() { type = VkDescriptorType.UniformBuffer, descriptorCount = SetsPerSubmit * UniformSlots },
            new() { type = VkDescriptorType.StorageBuffer, descriptorCount = SetsPerSubmit * StorageSlots },
            new() { type = VkDescriptorType.StorageImage, descriptorCount = SetsPerSubmit * ImageSlots },
        };
        var dpci = new VkDescriptorPoolCreateInfo { maxSets = SetsPerSubmit, poolSizeCount = 4, pPoolSizes = sizes };
        VkDescriptorPool p;
        Check(_vkd.vkCreateDescriptorPool(&dpci, &p), "vkCreateDescriptorPool");
        s.Pool = p;
        return s;
    }

    /// <summary>The one descriptor set every pipeline shares: GL's four binding spaces laid end to end, so a pass's
    /// slot numbers carry over unchanged and no pipeline needs its own layout.</summary>
    private VkDescriptorSetLayout MakeSetLayout()
    {
        const int n = (int)(SampledSlots + UniformSlots + StorageSlots + ImageSlots);
        VkDescriptorSetLayoutBinding* b = stackalloc VkDescriptorSetLayoutBinding[n];
        int k = 0;
        void Run(VkDescriptorSetLayoutBinding* b, ref int k, uint at, uint count, VkDescriptorType t)
        {
            for (uint i = 0; i < count; i++)
                b[k++] = new VkDescriptorSetLayoutBinding { binding = at + i, descriptorType = t, descriptorCount = 1, stageFlags = VkShaderStageFlags.All };
        }
        Run(b, ref k, VulkanToolchain.SampledBase, SampledSlots, VkDescriptorType.CombinedImageSampler);
        Run(b, ref k, VulkanToolchain.UniformBase, UniformSlots, VkDescriptorType.UniformBuffer);
        Run(b, ref k, VulkanToolchain.StorageBase, StorageSlots, VkDescriptorType.StorageBuffer);
        Run(b, ref k, VulkanToolchain.ImageBase, ImageSlots, VkDescriptorType.StorageImage);
        var ci = new VkDescriptorSetLayoutCreateInfo { bindingCount = n, pBindings = b };
        VkDescriptorSetLayout l;
        Check(_vkd.vkCreateDescriptorSetLayout(&ci, &l), "vkCreateDescriptorSetLayout");
        return l;
    }

    /// <summary>A binary semaphore exported once and imported into the current GL context: an opaque handle has
    /// reference transference, so the one pair serves every submit.</summary>
    private (VkSemaphore, uint) SharedSemaphore()
    {
        var ex = new VkExportSemaphoreCreateInfo { handleTypes = SemHandle };
        var ci = new VkSemaphoreCreateInfo { pNext = &ex };
        VkSemaphore s;
        Check(_vkd.vkCreateSemaphore(&ci, &s), "vkCreateSemaphore");
        IntPtr h;
        if (Win)
        {
            var gi = new VkSemaphoreGetWin32HandleInfoKHR { semaphore = s, handleType = SemHandle };
            nint wh;
            Check(_vkd.vkGetSemaphoreWin32HandleKHR(&gi, &wh), "vkGetSemaphoreWin32HandleKHR");
            h = wh;
        }
        else
        {
            var gi = new VkSemaphoreGetFdInfoKHR { semaphore = s, handleType = SemHandle };
            int fd;
            Check(_vkd.vkGetSemaphoreFdKHR(&gi, &fd), "vkGetSemaphoreFdKHR");
            h = fd;
        }
        return (s, GlInterop.ImportSemaphore(h));
    }

    private static VkExternalMemoryHandleTypeFlags MemHandle =>
        Win ? VkExternalMemoryHandleTypeFlags.OpaqueWin32 : VkExternalMemoryHandleTypeFlags.OpaqueFD;
    private static VkExternalSemaphoreHandleTypeFlags SemHandle =>
        Win ? VkExternalSemaphoreHandleTypeFlags.OpaqueWin32 : VkExternalSemaphoreHandleTypeFlags.OpaqueFD;

    private static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new InvalidOperationException($"{what} returned {r}");
    }

    public BufferId CreateBuffer(in BufferDesc d)
    {
        if (!OnThread("CreateBuffer") || d.Bytes <= 0) return Refused<BufferId>($"buffer {d.Label}: {d.Bytes} bytes");
        VkBufferUsageFlags use = VkBufferUsageFlags.TransferDst;
        if ((d.Use & BufferUse.Uniform) != 0) use |= VkBufferUsageFlags.UniformBuffer;
        if ((d.Use & BufferUse.Storage) != 0) use |= VkBufferUsageFlags.StorageBuffer;
        if ((d.Use & BufferUse.Vertex) != 0) use |= VkBufferUsageFlags.VertexBuffer;
        if ((d.Use & BufferUse.Index) != 0) use |= VkBufferUsageFlags.IndexBuffer;
        if ((d.Use & BufferUse.Indirect) != 0) use |= VkBufferUsageFlags.IndirectBuffer;
        if ((d.Use & BufferUse.CopySrc) != 0) use |= VkBufferUsageFlags.TransferSrc;
        var bci = new VkBufferCreateInfo { size = (ulong)d.Bytes, usage = use, sharingMode = VkSharingMode.Exclusive };
        // ponytail: Upload buffers are device-local too and written by recorded UpdateBuffer; a mapped ring when a pass streams
        var aci = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice };
        VkBuffer b;
        VmaAllocation a;
        if (Vma.vmaCreateBuffer(_vma, &bci, &aci, &b, &a, null) != VkResult.Success)
            return Refused<BufferId>($"buffer {d.Label}: VMA refused {d.Bytes} bytes");
        return new BufferId(_buffers.Add(new VkBuf(b, a, d)));
    }

    private bool Exportable(VkFormat f, VkImageUsageFlags usage)
    {
        var info = new VkPhysicalDeviceExternalImageFormatInfo { handleType = MemHandle };
        var fi = new VkPhysicalDeviceImageFormatInfo2
        {
            pNext = &info, format = f, type = VkImageType.Image2D, tiling = VkImageTiling.Optimal, usage = usage,
        };
        var ext = new VkExternalImageFormatProperties();
        var props = new VkImageFormatProperties2 { pNext = &ext };
        return _vki.vkGetPhysicalDeviceImageFormatProperties2(_phys, &fi, &props) == VkResult.Success
            && (ext.externalMemoryProperties.externalMemoryFeatures & VkExternalMemoryFeatureFlags.Exportable) != 0;
    }

    private IntPtr ExportMemory(VkDeviceMemory memory)
    {
        if (Win)
        {
            var gi = new VkMemoryGetWin32HandleInfoKHR { memory = memory, handleType = MemHandle };
            nint h;
            return _vkd.vkGetMemoryWin32HandleKHR(&gi, &h) == VkResult.Success ? h : IntPtr.Zero;
        }
        var fi = new VkMemoryGetFdInfoKHR { memory = memory, handleType = MemHandle };
        int fd;
        return _vkd.vkGetMemoryFdKHR(&fi, &fd) == VkResult.Success ? fd : IntPtr.Zero;
    }

    private int MemoryType(uint bits, VkMemoryPropertyFlags want)
    {
        for (int i = 0; i < _mem.memoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (_mem.memoryTypes[i].propertyFlags & want) == want) return i;
        return -1;
    }

    public SamplerId CreateSampler(in SamplerDesc d) => OnThread("CreateSampler") ? new SamplerId(_samplers.Add(Sampler(d))) : default;

    private VkSampler Sampler(in SamplerDesc d)
    {
        VkSamplerAddressMode wrap = d.Wrap == Wrap.Clamp ? VkSamplerAddressMode.ClampToEdge : VkSamplerAddressMode.Repeat;
        VkSamplerAddressMode wrapT = (d.WrapT ?? d.Wrap) == Wrap.Clamp ? VkSamplerAddressMode.ClampToEdge : VkSamplerAddressMode.Repeat;
        var ci = new VkSamplerCreateInfo
        {
            magFilter = d.Mag == Filter.Linear ? VkFilter.Linear : VkFilter.Nearest,
            minFilter = d.Min == Filter.Linear ? VkFilter.Linear : VkFilter.Nearest,
            mipmapMode = d.Mip == MipMode.Linear ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest,
            addressModeU = wrap, addressModeV = wrapT, addressModeW = wrapT,
            mipLodBias = d.LodBias,
            anisotropyEnable = d.Aniso > 1 && Caps.MaxAniso > 1, maxAnisotropy = Math.Clamp(d.Aniso, 1, Math.Max(1, Caps.MaxAniso)),
            compareEnable = d.Compare != null, compareOp = d.Compare is CompareOp c ? Compare(c) : VkCompareOp.Always,
            minLod = 0, maxLod = d.Mip == MipMode.None ? 0.25f : 1000f,
        };
        VkSampler s;
        Check(_vkd.vkCreateSampler(&ci, &s), "vkCreateSampler");
        return s;
    }

    /// <summary>One stage compiled to SPIR-V now: the content override when the resref has one, GL's vertex index
    /// spelled Vulkan's, GL's binding spaces shifted apart (<see cref="VulkanToolchain.Compile"/>).</summary>
    public ShaderId CreateShader(in ShaderDesc d)
    {
        if (!OnThread("CreateShader")) return default;
        string text = Translate((d.Ref != null ? CX.Alloy.ShaderResource.Override(d.Ref, _log) : null) ?? d.Source);
        ShaderKind kind = d.Stage switch
        {
            ShaderStage.Vertex => ShaderKind.VertexShader,
            ShaderStage.Geometry => ShaderKind.GeometryShader,
            ShaderStage.Compute => ShaderKind.ComputeShader,
            _ => ShaderKind.FragmentShader,
        };
        byte[]? spirv = VulkanToolchain.Compile(text, d.Label, kind, out string error);
        if (spirv == null) return Refused<ShaderId>($"shader {d.Label}: shaderc refused it -- {error}");
        VkShaderModule m;
        fixed (byte* p = spirv)
        {
            var ci = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)p };
            if (_vkd.vkCreateShaderModule(&ci, &m) != VkResult.Success) return Refused<ShaderId>($"shader {d.Label}: vkCreateShaderModule refused");
        }
        return new ShaderId(_shaders.Add(new VkShader(m, d.Stage)));
    }

    /// <summary>A stage from SPIR-V compiled elsewhere -- a caller that reflected the module first. Null id (logged)
    /// when the driver refuses it.</summary>
    internal ShaderId CreateShader(ShaderStage stage, byte[] spirv, string label)
    {
        if (!OnThread("CreateShader")) return default;
        VkShaderModule m;
        fixed (byte* p = spirv)
        {
            var ci = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)p };
            if (_vkd.vkCreateShaderModule(&ci, &m) != VkResult.Success) return Refused<ShaderId>($"shader {label}: vkCreateShaderModule refused");
        }
        return new ShaderId(_shaders.Add(new VkShader(m, stage)));
    }

    /// <summary>The device's uniform-buffer offset alignment, for a caller packing many blocks into one buffer.</summary>
    internal int UniformAlign => Caps.UniformAlign;

    /// <summary>GL GLSL as Vulkan GLSL: the one built-in the SPIR-V environment renames.</summary>
    internal static string Translate(string glsl) => glsl.Replace("gl_VertexID", "gl_VertexIndex", StringComparison.Ordinal);

    public void Destroy(BufferId b)
    {
        if (OnThread("Destroy") && _buffers.Remove(b.Value, out VkBuf g)) Later(() => Vma.vmaDestroyBuffer(_vma, g.Buffer, g.Alloc));
    }

    public void Destroy(ImageId i)
    {
        if (!OnThread("Destroy") || !_images.Remove(i.Value, out VkImg g)) return;
        if (g.Ext != null) GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, g.Ext.GlTex);
        Exported? e = g.Ext;
        Later(() => Free(g), e == null ? null : () =>
        {
            GlInterop.DeleteMemory(e.GlMem);
            GlInterop.DeleteSemaphore(e.GlVkDone);
            GlInterop.DeleteSemaphore(e.GlGlDone);
        });
    }

    /// <summary>The Vulkan half of an image: its view, the image and its memory, and a composite's semaphores.</summary>
    private void Free(VkImg g)
    {
        _vkd.vkDestroyImageView(g.View);
        if (g.MipViews != null) foreach (VkImageView v in g.MipViews) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        if (g.LayerViews != null) foreach (VkImageView v in g.LayerViews) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        if (g.Ext == null) { Vma.vmaDestroyImage(_vma, g.Image, g.Alloc); return; }
        _vkd.vkDestroyImage(g.Image);
        _vkd.vkFreeMemory(g.Ext.Memory);
        _vkd.vkDestroySemaphore(g.Ext.VkDone);
        _vkd.vkDestroySemaphore(g.Ext.GlDone);
    }

    public void Destroy(SamplerId s)
    {
        if (OnThread("Destroy") && _samplers.Remove(s.Value, out VkSampler v)) Later(() => _vkd.vkDestroySampler(v));
    }

    public void Destroy(ShaderId s)
    {
        if (OnThread("Destroy") && _shaders.Remove(s.Value, out VkShader v)) _vkd.vkDestroyShaderModule(v.Module);
    }

    public void Destroy(PipelineId p)
    {
        if (OnThread("Destroy") && _pipelines.Remove(p.Value, out VkPipe v)) Later(() => _vkd.vkDestroyPipeline(v.Pipeline));
    }

    /// <summary>A vertex layout to name in a <see cref="PipelineDesc"/>; held as declared, resolved per pipeline.</summary>
    internal VertexLayoutId CreateVertexLayout(in VertexLayoutDesc d)
    {
        if (!OnThread("CreateVertexLayout")) return default;
        foreach (VertexAttrib a in d.Attribs)
            if (AttribFormat(a) == VkFormat.Undefined || !VertexFormatOk(AttribFormat(a)))
                return Refused<VertexLayoutId>($"vertex layout {d.Label}: location {a.Location} as {a.Components} x {a.Type} cannot be a vertex attribute on this device");
        return new VertexLayoutId(_layouts.Add(d));
    }

    /// <summary>Free once no submit in flight and no retired GL texture can still reach it. Cold: a resize or a rebuild.</summary>
    private void Later(Action free, Action? gl = null) => _dead.Add((_submits, free, gl));

    private void DrainDead()
    {
        for (int i = _dead.Count - 1; i >= 0; i--)
            if (_submits - _dead[i].At >= DeadAfter)
            {
                _dead[i].Gl?.Invoke();
                _dead[i].Free();
                _dead.RemoveAt(i);
            }
    }

    /// <summary>Context loss or shutdown: wait the queue out and free every Vulkan object. The GL imports went with
    /// the context, so a death queued for a composite runs its Vulkan half only.</summary>
    internal void Dispose()
    {
        _vkd.vkDeviceWaitIdle();
        SavePipelineCache();
        foreach (var d in _dead) d.Free();
        _dead.Clear();
        foreach (VkImg g in _images.Live()) Free(g);
        foreach (VkBuf b in _buffers.Live()) Vma.vmaDestroyBuffer(_vma, b.Buffer, b.Alloc);
        foreach (VkSampler v in _samplers.Live()) _vkd.vkDestroySampler(v);
        foreach (VkShader v in _shaders.Live()) _vkd.vkDestroyShaderModule(v.Module);
        foreach (VkPipe v in _pipelines.Live()) _vkd.vkDestroyPipeline(v.Pipeline);
        foreach (Slot s in _slots)
        {
            _vkd.vkDestroyFence(s.Fence);
            _vkd.vkDestroyDescriptorPool(s.Pool);
            if (s.Stage.IsNotNull) Vma.vmaDestroyBuffer(_vma, s.Stage, s.StageAlloc);
        }
        _vkd.vkDestroyCommandPool(_cmdPool);
        _vkd.vkDestroySampler(_nearest);
        foreach (UploadChunk c in _upload.Chunks) Vma.vmaDestroyBuffer(_vma, c.Buffer, c.Alloc);
        foreach (UploadChunk c in _upload.Pool) Vma.vmaDestroyBuffer(_vma, c.Buffer, c.Alloc);
        _vkd.vkDestroyPipelineLayout(_pipeLayout);
        if (_pipeCache.IsNotNull) _vkd.vkDestroyPipelineCache(_pipeCache);
        if (_timePool.IsNotNull) _vkd.vkDestroyQueryPool(_timePool);
        _vkd.vkDestroyDescriptorPool(_bindlessPool);
        _vkd.vkDestroyDescriptorSetLayout(_bindlessLayout);
        _vkd.vkDestroyDescriptorSetLayout(_setLayout);
        Vma.vmaDestroyAllocator(_vma);
        _vkd.vkDestroyDevice();
        _vki.vkDestroyInstance();
        _log.Info("VkBackend: disposed -- the device and every object on it");
    }

    private bool OnThread(string what)
        => Environment.CurrentManagedThreadId == _thread
        || Fault($"{what} on thread {Environment.CurrentManagedThreadId}; the device belongs to thread {_thread}");

    private T Refused<T>(string why) where T : struct { Fault(why); return default; }

    private bool Fault(string why)
    {
        if (++_faults <= LoggedFaults)
            _log.Always($"VkBackend: REFUSED {why}" + (_faults == LoggedFaults ? " -- further refusals are only counted" : ""));
        return false;
    }

    private static VkCompareOp Compare(CompareOp c) => c switch
    {
        CompareOp.Less => VkCompareOp.Less,
        CompareOp.Equal => VkCompareOp.Equal,
        CompareOp.Greater => VkCompareOp.Greater,
        CompareOp.Always => VkCompareOp.Always,
        _ => VkCompareOp.LessOrEqual,
    };
}
