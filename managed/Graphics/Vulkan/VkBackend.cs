using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Rhi;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>The seam on Vulkan 1.3: one instance, one device matched to GL's by UUID, one queue, VMA, and the game
/// window's swapchain. Every image is Vulkan's alone: the frame lands in <see cref="Composite"/>, which
/// <see cref="Present"/> puts on the window, and GL never touches it. Made, used and dropped on the thread holding
/// Alloy's context.</summary>
internal sealed unsafe partial class VkBackend : IDevice
{
    /// <summary>One image, a VMA allocation. <see cref="Layout"/> is the layout the last recorded barrier left it in,
    /// across submits.</summary>
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
        /// <summary>A command wrote it since its last barrier: the next use in the same layout still waits on one.</summary>
        public bool Written;
        /// <summary>The async recording that last reached it.</summary>
        public long AsyncRec = -1;
        /// <summary>Its queued barrier's place in the batch numbered <see cref="BarBatch"/>.</summary>
        public int BarAt;
        public long BarBatch = -1;
    }

    private sealed class VkBuf(VkBuffer buffer, VmaAllocation alloc, BufferDesc desc)
    {
        public readonly VkBuffer Buffer = buffer;
        public readonly VmaAllocation Alloc = alloc;
        public readonly BufferDesc Desc = desc;
        /// <summary>The buffer epoch it was last used in, and whether that epoch wrote it.</summary>
        public long Epoch = -1;
        public bool Written;
        /// <summary><see cref="Op.UpdateBuffer"/> bytes and calls into it since the last take.</summary>
        public long UpBytes;
        public int UpCalls;
    }
    private readonly record struct VkShader(VkShaderModule Module, ShaderStage Stage);
    /// <summary>A pipeline, and the depth and raster state its desc named, which a bind restates dynamically.</summary>
    private readonly record struct VkPipe(VkPipeline Pipeline, bool Compute, DepthState Depth, RasterState Raster, bool Rated = false);

    /// <summary>One submit's recording resources, reused once the timeline passes <see cref="Done"/>: the value its
    /// last submit signalled. <see cref="Index"/> is its place in the list and its timestamp pair.</summary>
    private sealed class Slot(int index)
    {
        public readonly int Index = index;
        public VkCommandBuffer Cmd;
        public long Done;
        /// <summary>Its last submit wrote a timestamp pair, read when the slot is next taken.</summary>
        public bool Timed;
        /// <summary>Host-visible staging an update too large to record inline is copied through; reused from
        /// <see cref="StageUsed"/> 0 once the slot's submit has finished, grown when one submit outgrows it.</summary>
        public VkBuffer Stage;
        public VmaAllocation StageAlloc;
        public ulong StageBytes, StageUsed;
    }

    /// <summary>Why this machine cannot run the backend, asked once: null until asked, "" when it can.</summary>
    private sealed class ProbeState
    {
        public string? Refusal;
        public bool LoaderUp;
        /// <summary>The device the last <see cref="Open"/> chose: its PCI vendor id and name.</summary>
        public uint VendorId;
        public string Device = "";
        /// <summary>The last <see cref="Open"/> enabled VK_EXT_debug_utils on its instance.</summary>
        public bool DebugUtils;
    }

    /// <summary>The chosen device's vendor, as GL names it -- what an engine with no GL context is told it runs on.</summary>
    internal static string ProbedVendor => _probe.VendorId switch
    {
        0x10DE => "NVIDIA Corporation", 0x1002 => "AMD", 0x8086 => "Intel", 0 => "",
        uint id => $"Vulkan vendor 0x{id:x4}",
    };

    /// <summary>The chosen device's name, empty before a probe.</summary>
    internal static string ProbedDevice => _probe.Device;

    private static readonly ProbeState _probe = new();
    private static readonly bool Win = OperatingSystem.IsWindows();

    /// <summary>The floor: dynamic rendering and synchronization2 are core, and it fixes the SPIR-V target.</summary>
    private static readonly VkVersion ApiFloor = VkVersion.Version_1_3;
    private const int LoggedFaults = 16;
    /// <summary>Frames the CPU runs ahead of the GPU before a present waits, as a GL driver queues them; one missed
    /// vblank then costs no frame. Counted per frame, whatever number of submits a frame makes.</summary>
    private const int FramesInFlight = 2;
    /// <summary>Recording slots at most: a frame's submits times the frames in flight, with room to spare. A submit that
    /// finds every one still running waits on the oldest.</summary>
    private const int MaxSlots = 16;
    /// <summary>Distinct descriptor sets one pool holds before it is retired for a fresh one.</summary>
    private const uint SetsPerPool = 4096;
    /// <summary>The one set's shape: each kind of binding, its count, at its <see cref="VulkanToolchain"/> base.
    /// The buffer counts ARE the slot tables' budgets rather than a second opinion on them, so a point the GLSL
    /// may declare always has a descriptor to land in.</summary>
    private const uint SampledSlots = 48, ImageSlots = 8;
    private const uint UniformSlots = CX.Alloy.UboSlots.Budget, StorageSlots = CX.Alloy.SsboSlots.Budget;
    private const ulong WaitTimeoutNs = 2_000_000_000;

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
    private readonly List<Slot> _slots = new();
    private readonly VkDescriptorSetLayout _setLayout, _bindlessLayout;
    /// <summary>The uniform slots from 0 that are dynamic, so a rebind at a new offset reuses its set: every one the
    /// device's dynamic-buffer limit seats.</summary>
    private readonly uint _dynUbos;
    private readonly VkDescriptorPool _bindlessPool;
    private readonly VkDescriptorSet _bindlessSet;
    private readonly VkPipelineLayout _pipeLayout;
    private readonly VkSampler _nearest;
    private readonly bool _depthClamp;

    private readonly HandleTable<VkBuf> _buffers = new();
    private readonly HandleTable<VkImg> _images = new();
    private readonly HandleTable<VkSampler> _samplers = new();
    private readonly HandleTable<VkShader> _shaders = new();
    private readonly HandleTable<VkPipe> _pipelines = new();
    private readonly HandleTable<VertexLayoutDesc> _layouts = new();
    /// <summary>Deaths waiting out the timeline value of the last submit that could reach them.</summary>
    private readonly List<(long At, Action Free)> _dead = new();
    private long _submits;
    private int _faults;

    public DeviceCaps Caps { get; }

    /// <summary>Why the backend cannot run here, or null when it can. Asked once, from the verdict.</summary>
    internal static string? Refusal(ExtenderLog log)
    {
        if (_probe.Refusal == null)
        {
            _probe.Refusal = Open(log, out VkInstance inst, out _, out _, out _) ?? "";
            if (inst.IsNotNull) new VkInstanceApi(in inst).vkDestroyInstance();
            log.Always(_probe.Refusal.Length == 0
                ? "VkBackend: probe PASSED -- a Vulkan 1.3 device can run the backend and present to a window"
                : $"VkBackend: probe REFUSED -- {_probe.Refusal}");
        }
        return _probe.Refusal.Length == 0 ? null : _probe.Refusal;
    }

    /// <summary>The instance and the best device that can run the backend -- discrete before integrated -- or why
    /// none can. The instance is returned live (null on the first refusals) for the caller to keep or destroy.</summary>
    private static string? Open(ExtenderLog log, out VkInstance inst, out VkPhysicalDevice phys, out uint family,
                                out VkPhysicalDeviceFeatures feats)
    {
        inst = default; phys = default; family = 0; feats = default;
        foreach (string leaf in VulkanToolchain.Libraries)
            if (!VulkanToolchain.Load(leaf)) return "the staged shaderc or VMA library did not load from the install folder";
        if (!_probe.LoaderUp)
        {
            VkResult init = VulkanToolchain.InitLoader();
            if (init == VkResult.ErrorInitializationFailed) return "this machine has no Vulkan loader";
            if (init != VkResult.Success) return "the Vulkan loader would not initialize";
            _probe.LoaderUp = true;
        }
        if (vkEnumerateInstanceVersion() < ApiFloor) return "the Vulkan loader is older than 1.3";
        if (!InstanceHas(SurfaceExt) || !InstanceHas(WindowExt))
            return Win ? "the Vulkan loader lacks VK_KHR_surface/VK_KHR_win32_surface" : "the Vulkan loader lacks VK_KHR_surface/VK_KHR_xlib_surface";
        var app = new VkApplicationInfo { apiVersion = ApiFloor };
        VkResult made;
        _probe.DebugUtils = InstanceHas(DebugUtilsExt);
        fixed (byte* se = SurfaceExt) fixed (byte* we = WindowExt) fixed (byte* du = DebugUtilsExt)
        {
            // debug-utils last: the count drops it alone when the instance does not offer it
            byte** exts = stackalloc byte*[3] { se, we, du };
            uint ne = _probe.DebugUtils ? 3u : 2u;
            var ci = new VkInstanceCreateInfo { pApplicationInfo = &app, enabledExtensionCount = ne, ppEnabledExtensionNames = exts };
            made = vkCreateInstance(&ci, null, out inst);
        }
        if (made != VkResult.Success) return "vkCreateInstance refused a Vulkan 1.3 instance with the window surface extensions";
        var vki = new VkInstanceApi(in inst);
        uint n = 0;
        vki.vkEnumeratePhysicalDevices(&n, null);
        if (n == 0) return "the Vulkan loader lists no device";
        VkPhysicalDevice* list = stackalloc VkPhysicalDevice[(int)n];
        vki.vkEnumeratePhysicalDevices(&n, list);
        string? why = null;
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            string? no = Suits(vki, list[i], out uint k, out VkPhysicalDeviceFeatures f, out VkPhysicalDeviceProperties props);
            if (no != null) { why ??= $"'{new string((sbyte*)props.deviceName)}' {no}"; continue; }
            int rank = Rank(props.deviceType);
            if (rank <= best) continue;
            best = rank; phys = list[i]; family = k; feats = f;
            _probe.VendorId = props.vendorID; _probe.Device = new string((sbyte*)props.deviceName);
            log.Info($"VkBackend: device '{new string((sbyte*)props.deviceName)}' ({props.deviceType}) Vulkan {Version(props.apiVersion)}, queue family {k}");
        }
        return best >= 0 ? null : why ?? "the Vulkan loader lists no usable device";
    }

    /// <summary>A device kind's preference: a discrete GPU over an integrated one over anything else.</summary>
    private static int Rank(VkPhysicalDeviceType t)
        => t switch { VkPhysicalDeviceType.DiscreteGpu => 3, VkPhysicalDeviceType.IntegratedGpu => 2, VkPhysicalDeviceType.VirtualGpu => 1, _ => 0 };

    /// <summary>Why <paramref name="d"/> cannot run the backend, or null with its graphics+compute queue family, features
    /// and properties.</summary>
    private static string? Suits(VkInstanceApi vki, VkPhysicalDevice d, out uint family, out VkPhysicalDeviceFeatures feats,
                                 out VkPhysicalDeviceProperties props)
    {
        family = 0; feats = default;
        var p2 = new VkPhysicalDeviceProperties2();
        vki.vkGetPhysicalDeviceProperties2(d, &p2);
        props = p2.properties;
        if (props.apiVersion < ApiFloor) return "is below Vulkan 1.3";
        if (!HasExt(vki, d, "VK_EXT_depth_clip_control\0"u8)) return "lacks VK_EXT_depth_clip_control (GL's -1..1 clip depth)";
        if (!HasExt(vki, d, SwapchainExt)) return "lacks VK_KHR_swapchain";
        var clip = new VkPhysicalDeviceDepthClipControlFeaturesEXT();
        var f11 = new VkPhysicalDeviceVulkan11Features { pNext = &clip };
        var f12 = new VkPhysicalDeviceVulkan12Features { pNext = &f11 };
        var f13 = new VkPhysicalDeviceVulkan13Features { pNext = &f12 };
        var f2 = new VkPhysicalDeviceFeatures2 { pNext = &f13 };
        vki.vkGetPhysicalDeviceFeatures2(d, &f2);
        if (!f13.dynamicRendering || !f13.synchronization2) return "lacks dynamic rendering or synchronization2";
        if (Lacks(in f2.features, in f11, in f12) is string lack) return $"lacks {lack}";
        if (!clip.depthClipControl) return "lacks depthClipControl";
        uint qn = 0;
        vki.vkGetPhysicalDeviceQueueFamilyProperties(d, &qn, null);
        VkQueueFamilyProperties* q = stackalloc VkQueueFamilyProperties[(int)qn];
        vki.vkGetPhysicalDeviceQueueFamilyProperties(d, &qn, q);
        for (uint k = 0; k < qn; k++)
        {
            if ((q[k].queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute)) != (VkQueueFlags.Graphics | VkQueueFlags.Compute)) continue;
            family = k; feats = f2.features;
            return null;
        }
        return "has no graphics+compute queue";
    }

    /// <summary>The first feature the batch pass needs that the device does not have, or null: draw parameters for the
    /// draw index, indirect counts for the cull's lists, descriptor indexing for bindless textures, BC textures, cube
    /// arrays, robust buffer access so a stand-in buffer shorter than a stage's reach reads zeros, and a vertex stage's
    /// <c>gl_Layer</c> for the caster bake, which renders every stale view of one array in one layered pass.</summary>
    private static string? Lacks(in VkPhysicalDeviceFeatures f, in VkPhysicalDeviceVulkan11Features f11, in VkPhysicalDeviceVulkan12Features f12)
    {
        if (!f11.shaderDrawParameters) return "shaderDrawParameters";
        if (!f12.drawIndirectCount) return "drawIndirectCount";
        if (!f12.timelineSemaphore) return "timelineSemaphore";
        if (!f12.shaderOutputLayer) return "shaderOutputLayer (the caster bake's layered pass)";
        if (!f12.runtimeDescriptorArray || !f12.descriptorBindingPartiallyBound || !f12.descriptorBindingVariableDescriptorCount
            || !f12.shaderSampledImageArrayNonUniformIndexing || !f12.descriptorBindingSampledImageUpdateAfterBind)
            return "the descriptor indexing bindless textures need";
        if (!f.multiDrawIndirect) return "multiDrawIndirect";
        if (!f.drawIndirectFirstInstance) return "drawIndirectFirstInstance (the caster bake's ownership slot)";
        if (!f.textureCompressionBC) return "BC texture compression";
        if (!f.imageCubeArray) return "cube-array images";
        if (!f.independentBlend) return "independent blend";
        if (!f.robustBufferAccess) return "robust buffer access";
        if (!f.geometryShader) return "geometryShader (a lit fragment's gl_PrimitiveID)";
        return null;
    }

    /// <summary>Does the device carry <c>shaderOutputViewportIndex</c>? Its only use here is to make enabling
    /// <c>VK_EXT_shader_viewport_index_layer</c> legal, which the specification allows only alongside it.</summary>
    private bool ViewportIndex(VkPhysicalDevice d)
    {
        var f12 = new VkPhysicalDeviceVulkan12Features();
        var f2 = new VkPhysicalDeviceFeatures2 { pNext = &f12 };
        _vki.vkGetPhysicalDeviceFeatures2(d, &f2);
        return f12.shaderOutputViewportIndex;
    }

    /// <summary>major.minor.patch: the version's own formatting leads with the variant field.</summary>
    private static string Version(VkVersion v) => $"{v.Major}.{v.Minor}.{v.Patch}";

    /// <summary>The instance extensions a window surface takes, and the device's swapchain; NUL-terminated.</summary>
    private static ReadOnlySpan<byte> SurfaceExt => "VK_KHR_surface\0"u8;
    private static ReadOnlySpan<byte> WindowExt => Win ? "VK_KHR_win32_surface\0"u8 : "VK_KHR_xlib_surface\0"u8;
    private static ReadOnlySpan<byte> SwapchainExt => "VK_KHR_swapchain\0"u8;
    /// <summary>Pass labels and object names for capture tools: offered by the loader or a capture layer.</summary>
    private static ReadOnlySpan<byte> DebugUtilsExt => "VK_EXT_debug_utils\0"u8;

    private static bool InstanceHas(ReadOnlySpan<byte> name)
    {
        uint n = 0;
        vkEnumerateInstanceExtensionProperties(null, &n, null);
        var props = new VkExtensionProperties[n];
        fixed (VkExtensionProperties* p = props) vkEnumerateInstanceExtensionProperties(null, &n, p);
        for (int i = 0; i < n; i++)
            fixed (byte* e = props[i].extensionName)
                if (new ReadOnlySpan<byte>(e, name.Length).SequenceEqual(name)) return true;
        return false;
    }

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

    /// <summary>The device, or null -- logged -- when the probe refuses or a creation step fails.</summary>
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
        _labels = _probe.DebugUtils;

        float prio = 1f;
        int asyncFamily = AsyncFamily(_vki, phys);
        VkDeviceQueueCreateInfo* qci = stackalloc VkDeviceQueueCreateInfo[2];
        qci[0] = new VkDeviceQueueCreateInfo { queueFamilyIndex = family, queueCount = 1, pQueuePriorities = &prio };
        if (asyncFamily >= 0) qci[1] = new VkDeviceQueueCreateInfo { queueFamilyIndex = (uint)asyncFamily, queueCount = 1, pQueuePriorities = &prio };
        var clip = new VkPhysicalDeviceDepthClipControlFeaturesEXT { depthClipControl = true };
        var f11 = new VkPhysicalDeviceVulkan11Features { pNext = &clip, shaderDrawParameters = true };
        var f12 = new VkPhysicalDeviceVulkan12Features
        {
            pNext = &f11, drawIndirectCount = true, runtimeDescriptorArray = true, descriptorBindingPartiallyBound = true,
            descriptorBindingVariableDescriptorCount = true, shaderSampledImageArrayNonUniformIndexing = true,
            descriptorBindingSampledImageUpdateAfterBind = true, timelineSemaphore = true,
            // the caster bake's vertex stages write gl_Layer. glslang may spell that as 1.2's own ShaderLayer or
            // as the EXT capability, so both features go on and the extension joins them where the device still
            // advertises it -- enabling it with only one of the pair is what the spec forbids.
            shaderOutputLayer = true, shaderOutputViewportIndex = ViewportIndex(phys),
        };
        var f13 = new VkPhysicalDeviceVulkan13Features { pNext = &f12, dynamicRendering = true, synchronization2 = true };
        var feats = new VkPhysicalDeviceFeatures2 { pNext = &f13 };
        feats.features.depthClamp = has.depthClamp;
        feats.features.samplerAnisotropy = has.samplerAnisotropy;
        feats.features.multiDrawIndirect = true;
        // the caster bake's commands carry the ownership slot as firstInstance, which the depth stages read as
        // gl_BaseInstance: an indirect draw may only name a non-zero one with this feature, and without it the
        // draw is undefined -- it rasterized nothing, and no validation message says so
        feats.features.drawIndirectFirstInstance = true;
        feats.features.textureCompressionBC = true;
        feats.features.imageCubeArray = true;
        feats.features.independentBlend = true;
        feats.features.robustBufferAccess = true;
        feats.features.fragmentStoresAndAtomics = has.fragmentStoresAndAtomics;
        feats.features.geometryShader = true;
        bool layerExt = ViewportIndex(phys) && HasExt(_vki, phys, "VK_EXT_shader_viewport_index_layer\0"u8);
        _memBudget = HasExt(_vki, phys, "VK_EXT_memory_budget\0"u8);
        _rateTexel = RateSupport(phys, out string? rateWhy);
        var fsr = new VkPhysicalDeviceFragmentShadingRateFeaturesKHR { pipelineFragmentShadingRate = true, attachmentFragmentShadingRate = true };
        if (_rateTexel > 0) clip.pNext = &fsr;
        VkDevice device;
        fixed (byte* sw = SwapchainExt) fixed (byte* c = "VK_EXT_depth_clip_control\0"u8)
        fixed (byte* vl = "VK_EXT_shader_viewport_index_layer\0"u8) fixed (byte* mb = "VK_EXT_memory_budget\0"u8)
        fixed (byte* sr = RateExt)
        {
            byte** exts = stackalloc byte*[5] { sw, c, null, null, null };
            uint n = 2;
            if (layerExt) exts[n++] = vl;
            if (_memBudget) exts[n++] = mb;
            if (_rateTexel > 0) exts[n++] = sr;
            var dci = new VkDeviceCreateInfo
            {
                pNext = &feats, queueCreateInfoCount = asyncFamily >= 0 ? 2u : 1u, pQueueCreateInfos = qci,
                enabledExtensionCount = n, ppEnabledExtensionNames = exts,
            };
            Check(_vki.vkCreateDevice(phys, &dci, null, out device), "vkCreateDevice");
        }
        _device = device;
        _vkd = new VkDeviceApi(_vki, in device);
        _vkd.vkGetDeviceQueue(family, 0, out _queue);
        _async = asyncFamily >= 0 ? OpenAsync((uint)asyncFamily) : null;

        _vma = CreateAllocator(inst);
        var pci = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = family };
        VkCommandPool pool;
        Check(_vkd.vkCreateCommandPool(&pci, &pool), "vkCreateCommandPool");
        _cmdPool = pool;
        VkPhysicalDeviceProperties props;
        _vki.vkGetPhysicalDeviceProperties(phys, &props);
        ref VkPhysicalDeviceLimits l = ref props.limits;
        _dynUbos = Math.Min(UniformSlots, l.maxDescriptorSetUniformBuffersDynamic);
        _timeline = MakeTimeline();
        Check(MakeSetPool(out _sets.Pool), "vkCreateDescriptorPool");
        for (int i = 0; i < FramesInFlight; i++) _acquired[i] = MakeSemaphore();

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
        Caps = new DeviceCaps((int)l.maxImageDimension2D, (int)l.maxImageArrayLayers, (int)l.maxColorAttachments,
            MaxCount(l.framebufferColorSampleCounts & l.framebufferDepthSampleCounts),
            (int)l.maxPerStageDescriptorSampledImages, (int)l.maxDescriptorSetSampledImages,
            (int)l.maxPerStageDescriptorStorageBuffers, (int)l.maxPerStageDescriptorStorageBuffers,
            (int)l.minUniformBufferOffsetAlignment, (int)l.minStorageBufferOffsetAlignment, has.samplerAnisotropy ? l.maxSamplerAnisotropy : 1f);
        _maxDrawIndirect = l.maxDrawIndirectCount;
        log.Always($"VkBackend: LIVE -- {Caps}, {_bindlessCap} bindless textures, debug labels "
                 + $"{(_labels ? "on" : "off")}, async compute {(_async != null ? $"on queue family {_async.Family}" : "on the graphics queue (no compute-only family)")}, "
                 + (_rateTexel > 0 ? $"shading-rate image in {_rateTexel} px tiles" : $"no shading-rate image ({rateWhy}): the G-buffer pass shades every pixel")
                 + "; Vulkan presents the frame itself");
    }

    /// <summary>The edge of a shading-rate image's texel in pixels, 0 when the device cannot read one.</summary>
    private readonly int _rateTexel;
    /// <inheritdoc cref="_rateTexel"/>
    internal int ShadingRateTexel => _rateTexel;

    private static ReadOnlySpan<byte> RateExt => "VK_KHR_fragment_shading_rate\0"u8;

    /// <summary>The square texel edge <paramref name="d"/> takes a shading-rate image in, or 0 with why not: the
    /// extension, its attachment feature, and a square size <see cref="RateTexel"/> accepts.</summary>
    private int RateSupport(VkPhysicalDevice d, out string? why)
    {
        why = null;
        if (!HasExt(_vki, d, RateExt)) { why = "lacks VK_KHR_fragment_shading_rate"; return 0; }
        var feats = new VkPhysicalDeviceFragmentShadingRateFeaturesKHR();
        var f2 = new VkPhysicalDeviceFeatures2 { pNext = &feats };
        _vki.vkGetPhysicalDeviceFeatures2(d, &f2);
        if (!feats.attachmentFragmentShadingRate || !feats.pipelineFragmentShadingRate) { why = "lacks attachmentFragmentShadingRate"; return 0; }
        var props = new VkPhysicalDeviceFragmentShadingRatePropertiesKHR();
        var p2 = new VkPhysicalDeviceProperties2 { pNext = &props };
        _vki.vkGetPhysicalDeviceProperties2(d, &p2);
        VkExtent2D lo = props.minFragmentShadingRateAttachmentTexelSize, hi = props.maxFragmentShadingRateAttachmentTexelSize;
        int t = RateTexel((int)Math.Max(lo.width, lo.height), (int)Math.Min(hi.width, hi.height));
        if (t == 0) why = $"no square texel between {lo.width}x{lo.height} and {hi.width}x{hi.height}";
        return t;
    }

    /// <summary>The device takes VK_EXT_memory_budget, so <see cref="DeviceLocalKib"/> reads the driver's own figures.</summary>
    private bool _memBudget;
    /// <inheritdoc cref="_memBudget"/>
    public bool MemoryBudget => _memBudget;

    /// <summary>The device-local heaps' size and what is left of their budget, KiB: the driver's with VK_EXT_memory_budget,
    /// else the allocator's estimate over its own blocks. Any thread that owns the device.</summary>
    public void DeviceLocalKib(out long freeKib, out long totalKib)
    {
        freeKib = 0; totalKib = 0;
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        VmaBudget* b = stackalloc VmaBudget[(int)VK_MAX_MEMORY_HEAPS];
        Vma.vmaGetHeapBudgets(_vma, b);
        for (int i = 0; i < mp.memoryHeapCount; i++)
        {
            if ((mp.memoryHeaps[i].flags & VkMemoryHeapFlags.DeviceLocal) == 0) continue;
            totalKib += (long)(mp.memoryHeaps[i].size / 1024);
            if (b[i].budget > b[i].usage) freeKib += (long)((b[i].budget - b[i].usage) / 1024);
        }
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
            flags = _memBudget ? VmaAllocatorCreateFlags.EXTMemoryBudget : VmaAllocatorCreateFlags.None,
        };
        Check(Vma.vmaCreateAllocator(&ci, out VmaAllocator a), "vmaCreateAllocator");
        return a;
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
        Run(b, ref k, VulkanToolchain.UniformBase, _dynUbos, VkDescriptorType.UniformBufferDynamic);
        Run(b, ref k, VulkanToolchain.UniformBase + _dynUbos, UniformSlots - _dynUbos, VkDescriptorType.UniformBuffer);
        Run(b, ref k, VulkanToolchain.StorageBase, StorageSlots, VkDescriptorType.StorageBuffer);
        Run(b, ref k, VulkanToolchain.ImageBase, ImageSlots, VkDescriptorType.StorageImage);
        var ci = new VkDescriptorSetLayoutCreateInfo { bindingCount = n, pBindings = b };
        VkDescriptorSetLayout l;
        Check(_vkd.vkCreateDescriptorSetLayout(&ci, &l), "vkCreateDescriptorSetLayout");
        return l;
    }

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
        if (d.Memory == MemoryKind.Readback)
        {
            if (!HostBuffer((ulong)d.Bytes, use, VmaAllocationCreateFlags.HostAccessRandom, out VkBuffer hb, out VmaAllocation ha))
                return Refused<BufferId>($"readback buffer {d.Label}: VMA refused {d.Bytes} bytes");
            Name(VkObjectType.Buffer, hb.Handle, d.Label);
            return new BufferId(_buffers.Add(new VkBuf(hb, ha, d)));
        }
        var bci = new VkBufferCreateInfo { size = (ulong)d.Bytes, usage = use, sharingMode = VkSharingMode.Exclusive };
        uint* fams = stackalloc uint[2];
        // either queue may reach any device buffer, and a buffer has no compression for concurrent sharing to cost
        if (_async != null) Share(ref bci.sharingMode, ref bci.queueFamilyIndexCount, ref bci.pQueueFamilyIndices, fams);
        // ponytail: Upload buffers are device-local too and written by recorded UpdateBuffer; a mapped ring when a pass streams
        var aci = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice };
        VkBuffer b;
        VmaAllocation a;
        if (Vma.vmaCreateBuffer(_vma, &bci, &aci, &b, &a, null) != VkResult.Success)
            return Refused<BufferId>($"buffer {d.Label}: VMA refused {d.Bytes} bytes");
        Name(VkObjectType.Buffer, b.Handle, d.Label);
        var id = new BufferId(_buffers.Add(new VkBuf(b, a, d)));
        _upload.Zero.Add(id);
        return id;
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
        if (OnThread("Destroy") && _buffers.Remove(b.Value, out VkBuf g)) { _sets.Stale = true; Later(() => Vma.vmaDestroyBuffer(_vma, g.Buffer, g.Alloc)); }
    }

    public void Destroy(ImageId i)
    {
        if (OnThread("Destroy") && _images.Remove(i.Value, out VkImg g)) { _sets.Stale = true; LaterThanBindless(() => Free(g)); }
    }

    /// <summary>An image's views, the image and its memory.</summary>
    private void Free(VkImg g)
    {
        _vkd.vkDestroyImageView(g.View);
        if (g.MipViews != null) foreach (VkImageView v in g.MipViews) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        if (g.LayerViews != null) foreach (VkImageView v in g.LayerViews) if (v.IsNotNull) _vkd.vkDestroyImageView(v);
        Vma.vmaDestroyImage(_vma, g.Image, g.Alloc);
    }

    public void Destroy(SamplerId s)
    {
        if (OnThread("Destroy") && _samplers.Remove(s.Value, out VkSampler v)) { _sets.Stale = true; LaterThanBindless(() => _vkd.vkDestroySampler(v)); }
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

    /// <summary>Free once no submit in flight can still reach it. Cold: a resize or a rebuild.</summary>
    private void Later(Action free) => _dead.Add((Next, free));

    private void DrainDead()
    {
        for (int i = _dead.Count - 1; i >= 0; i--)
            if (Passed(_dead[i].At))
            {
                _dead[i].Free();
                _dead.RemoveAt(i);
            }
    }

    /// <summary>Context loss or shutdown: wait the queue out and free every Vulkan object, the swapchain and the
    /// window's surface last but one.</summary>
    internal void Dispose()
    {
        _vkd.vkDeviceWaitIdle();
        SavePipelineCache();
        foreach (var d in _dead) d.Free();
        _dead.Clear();
        foreach (var d in _bindless.Behind) d.Free();
        _bindless.Behind.Clear();
        foreach (VkImg g in _images.Live()) Free(g);
        foreach (VkBuf b in _buffers.Live()) Vma.vmaDestroyBuffer(_vma, b.Buffer, b.Alloc);
        foreach (VkSampler v in _samplers.Live()) _vkd.vkDestroySampler(v);
        foreach (VkShader v in _shaders.Live()) _vkd.vkDestroyShaderModule(v.Module);
        foreach (VkPipe v in _pipelines.Live()) _vkd.vkDestroyPipeline(v.Pipeline);
        foreach (Slot s in _slots)
            if (s.Stage.IsNotNull) Vma.vmaDestroyBuffer(_vma, s.Stage, s.StageAlloc);
        _vkd.vkDestroySemaphore(_timeline);
        foreach (VkSemaphore a in _acquired) _vkd.vkDestroySemaphore(a);
        DropSwapchain();
        _vkd.vkDestroyCommandPool(_cmdPool);
        DisposeAsync();
        _vkd.vkDestroySampler(_nearest);
        foreach (UploadChunk c in _upload.Chunks) Vma.vmaDestroyBuffer(_vma, c.Buffer, c.Alloc);
        foreach (UploadChunk c in _upload.Pool) Vma.vmaDestroyBuffer(_vma, c.Buffer, c.Alloc);
        _vkd.vkDestroyPipelineLayout(_pipeLayout);
        if (_pipeCache.IsNotNull) _vkd.vkDestroyPipelineCache(_pipeCache);
        if (_timePool.IsNotNull) _vkd.vkDestroyQueryPool(_timePool);
        _vkd.vkDestroyDescriptorPool(_sets.Pool);
        foreach ((long _, VkDescriptorPool p) in _sets.Spare) _vkd.vkDestroyDescriptorPool(p);
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
