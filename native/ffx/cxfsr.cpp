// FSR 3.1 upscaling behind a flat C API for cx_managed (DirectPInvoke "cxfsr"): a context with its three shared
// resources, and the dispatch. Also the Vulkan entry points AMD's backend calls by name, forwarded to the host's
// instance and device -- cx_managed links no Vulkan loader. They report no device extension, so the backend keeps
// to its wave32, fp32 permutations, the only ones tools/ffx/ffx_shaders.py compiles.
#include <FidelityFX/host/ffx_fsr3upscaler.h>
#include <FidelityFX/host/backends/vk/ffx_vk.h>
#include <stdlib.h>

// ponytail: one host device at a time; a second device would need these per context.
static PFN_vkGetInstanceProcAddr s_gipa;
static VkInstance s_instance;
static PFN_vkGetDeviceProcAddr s_gdpa;

template <typename T> static T Inst(const char* name) { return (T)s_gipa(s_instance, name); }

extern "C" {
VKAPI_ATTR void VKAPI_CALL vkGetPhysicalDeviceProperties(VkPhysicalDevice p, VkPhysicalDeviceProperties* o)
{ Inst<PFN_vkGetPhysicalDeviceProperties>("vkGetPhysicalDeviceProperties")(p, o); }
VKAPI_ATTR void VKAPI_CALL vkGetPhysicalDeviceProperties2(VkPhysicalDevice p, VkPhysicalDeviceProperties2* o)
{ Inst<PFN_vkGetPhysicalDeviceProperties2>("vkGetPhysicalDeviceProperties2")(p, o); }
VKAPI_ATTR void VKAPI_CALL vkGetPhysicalDeviceFeatures(VkPhysicalDevice p, VkPhysicalDeviceFeatures* o)
{ Inst<PFN_vkGetPhysicalDeviceFeatures>("vkGetPhysicalDeviceFeatures")(p, o); }
VKAPI_ATTR void VKAPI_CALL vkGetPhysicalDeviceFeatures2(VkPhysicalDevice p, VkPhysicalDeviceFeatures2* o)
{ Inst<PFN_vkGetPhysicalDeviceFeatures2>("vkGetPhysicalDeviceFeatures2")(p, o); }
VKAPI_ATTR void VKAPI_CALL vkGetPhysicalDeviceMemoryProperties(VkPhysicalDevice p, VkPhysicalDeviceMemoryProperties* o)
{ Inst<PFN_vkGetPhysicalDeviceMemoryProperties>("vkGetPhysicalDeviceMemoryProperties")(p, o); }
VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateDeviceExtensionProperties(VkPhysicalDevice, const char*, uint32_t* n, VkExtensionProperties*)
{ *n = 0; return VK_SUCCESS; }
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice d, const char* name) { return s_gdpa(d, name); }
VKAPI_ATTR VkResult VKAPI_CALL vkCreateBuffer(VkDevice d, const VkBufferCreateInfo* i, const VkAllocationCallbacks* a, VkBuffer* b)
{ return ((PFN_vkCreateBuffer)s_gdpa(d, "vkCreateBuffer"))(d, i, a, b); }

// The frame-interpolation swapchain is not built; the backend still names this.
FfxErrorCode ffxSetFrameGenerationConfigToSwapchainVK(FfxFrameGenerationConfig const*) { return FFX_ERROR_INVALID_ARGUMENT; }
}

#if defined(__GLIBCXX__)
// std::mutex::lock's failure path, the one libstdc++ symbol left once the SDK builds without exceptions: cx_managed
// links no C++ runtime, and only the render thread takes FSR's lock.
namespace std { void __throw_system_error(int) { abort(); } }
#endif

// Alloy's colour is linear HDR and its world depth reversed-Z, finite. tools/ffx/ffx_shaders.py compiles only the
// permutations these flags reach; change them together.
#define CXFSR_FLAGS (FFX_FSR3UPSCALER_ENABLE_HIGH_DYNAMIC_RANGE | FFX_FSR3UPSCALER_ENABLE_DEPTH_INVERTED)

struct CxFsr
{
    FfxFsr3UpscalerContext ctx;
    FfxInterface backend;
    void* scratch;
    FfxResourceInternal shared[3];
};

// The host's device, as cxfsr_create takes it.
struct CxFsrDevice
{
    VkInstance instance;
    PFN_vkGetInstanceProcAddr gipa;
    VkPhysicalDevice physical;
    VkDevice device;
    PFN_vkGetDeviceProcAddr gdpa;
};

// One image of a dispatch. The inputs are in SHADER_READ_ONLY_OPTIMAL and the output (uav) in GENERAL, and the
// backend leaves each in that layout again.
struct CxFsrImage
{
    VkImage image;
    VkFormat format;
    uint32_t width, height, uav;
};

struct CxFsrDispatch
{
    VkCommandBuffer cmd;
    CxFsrImage color, depth, motion, exposure, output;
    float jitterX, jitterY, mvScaleX, mvScaleY;
    uint32_t renderW, renderH;
    float frameMs, cameraNear, cameraFar, fovY;
    uint32_t reset;
};

static FfxResource Wrap(const CxFsrImage& i, const wchar_t* name)
{
    if (!i.image) return FfxResource{};
    VkImageCreateInfo c = {};
    c.imageType = VK_IMAGE_TYPE_2D;
    c.format = i.format;
    c.extent = { i.width, i.height, 1 };
    c.mipLevels = 1;
    c.arrayLayers = 1;
    c.usage = VK_IMAGE_USAGE_SAMPLED_BIT | (i.uav ? VK_IMAGE_USAGE_STORAGE_BIT : 0);
    return ffxGetResourceVK((void*)i.image, ffxGetImageResourceDescriptionVK(i.image, c), name,
                            i.uav ? FFX_RESOURCE_STATE_UNORDERED_ACCESS : FFX_RESOURCE_STATE_COMPUTE_READ);
}

extern "C" {

// An FfxErrorCode: 0 is a context in *out, ready to dispatch.
int cxfsr_create(const CxFsrDevice* dev, uint32_t maxRenderW, uint32_t maxRenderH, uint32_t displayW, uint32_t displayH,
                 CxFsr** out)
{
    *out = nullptr;
    s_instance = dev->instance; s_gipa = dev->gipa; s_gdpa = dev->gdpa;
    CxFsr* f = (CxFsr*)calloc(1, sizeof(CxFsr));
    if (!f) return FFX_ERROR_OUT_OF_MEMORY;
    size_t size = ffxGetScratchMemorySizeVK(dev->physical, 1);
    // zeroed: the backend reads a reference count out of it before it writes one
    f->scratch = calloc(1, size);
    VkDeviceContext vk = { dev->device, dev->physical, dev->gdpa };
    FfxErrorCode e = f->scratch ? ffxGetInterfaceVK(&f->backend, ffxGetDeviceVK(&vk), f->scratch, size, 1) : FFX_ERROR_OUT_OF_MEMORY;
    if (e == FFX_OK)
    {
        FfxFsr3UpscalerContextDescription d = {};
        d.flags = CXFSR_FLAGS;
        d.maxRenderSize = { maxRenderW, maxRenderH };
        d.maxUpscaleSize = { displayW, displayH };
        d.backendInterface = f->backend;
        e = ffxFsr3UpscalerContextCreate(&f->ctx, &d);
        if (e == FFX_OK)
        {
            FfxFsr3UpscalerSharedResourceDescriptions s = {};
            e = ffxFsr3UpscalerGetSharedResourceDescriptions(&f->ctx, &s);
            const FfxCreateResourceDescription* rs[3] = { &s.dilatedDepth, &s.dilatedMotionVectors, &s.reconstructedPrevNearestDepth };
            int made = 0;
            for (; made < 3 && e == FFX_OK; made++) e = f->backend.fpCreateResource(&f->backend, rs[made], 0, &f->shared[made]);
            if (e != FFX_OK)
            {
                for (int i = 0; i < made - 1; i++) f->backend.fpDestroyResource(&f->backend, f->shared[i], 0);
                ffxFsr3UpscalerContextDestroy(&f->ctx);
            }
        }
    }
    if (e != FFX_OK)
    {
        free(f->scratch);
        free(f);
        return e;
    }
    *out = f;
    return FFX_OK;
}

// One upscale recorded into d->cmd; an FfxErrorCode.
int cxfsr_dispatch(CxFsr* f, const CxFsrDispatch* d)
{
    FfxFsr3UpscalerDispatchDescription x = {};
    x.commandList = ffxGetCommandListVK(d->cmd);
    x.color = Wrap(d->color, L"cx colour");
    x.depth = Wrap(d->depth, L"cx depth");
    x.motionVectors = Wrap(d->motion, L"cx velocity");
    x.exposure = Wrap(d->exposure, L"cx exposure");
    x.output = Wrap(d->output, L"cx upscaled");
    x.dilatedDepth = f->backend.fpGetResource(&f->backend, f->shared[0]);
    x.dilatedMotionVectors = f->backend.fpGetResource(&f->backend, f->shared[1]);
    x.reconstructedPrevNearestDepth = f->backend.fpGetResource(&f->backend, f->shared[2]);
    x.jitterOffset = { d->jitterX, d->jitterY };
    x.motionVectorScale = { d->mvScaleX, d->mvScaleY };
    x.renderSize = { d->renderW, d->renderH };
    x.upscaleSize = { d->output.width, d->output.height };
    x.frameTimeDelta = d->frameMs;
    x.preExposure = 1.0f;
    x.reset = d->reset != 0;
    x.cameraNear = d->cameraNear;
    x.cameraFar = d->cameraFar;
    x.cameraFovAngleVertical = d->fovY;
    x.viewSpaceToMetersFactor = 1.0f;
    return ffxFsr3UpscalerContextDispatch(&f->ctx, &x);
}

// The context's device memory, in bytes; 0 when the backend cannot say.
uint64_t cxfsr_vram(CxFsr* f)
{
    FfxEffectMemoryUsage u = {};
    return ffxFsr3UpscalerContextGetGpuMemoryUsage(&f->ctx, &u) == FFX_OK ? u.totalUsageInBytes : 0;
}

// The dispatches FSR's backend rings its constants and descriptor sets over.
int cxfsr_ring() { return FFX_MAX_QUEUED_FRAMES; }

// FSR's jitter sequence length.
int cxfsr_phase_count(int renderW, int displayW) { return ffxFsr3UpscalerGetJitterPhaseCount(renderW, displayW); }

// Free a context once no submit can still reach it.
void cxfsr_destroy(CxFsr* f)
{
    if (!f) return;
    for (int i = 0; i < 3; i++) f->backend.fpDestroyResource(&f->backend, f->shared[i], 0);
    ffxFsr3UpscalerContextDestroy(&f->ctx);
    free(f->scratch);
    free(f);
}
}
