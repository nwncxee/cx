// FSR 3.1 upscaling and FidelityFX DoF behind a flat C API for cx_managed (DirectPInvoke "cxfsr"): each effect's context
// and its dispatch. Also the Vulkan entry points AMD's backend calls by name, forwarded to the host's
// instance and device -- cx_managed links no Vulkan loader. They report no device extension, so the backend keeps
// to its wave32, fp32 permutations, the only ones tools/ffx/ffx_shaders.py compiles.
#include <FidelityFX/host/ffx_fsr3upscaler.h>
#include <FidelityFX/host/ffx_dof.h>
#include <FidelityFX/host/ffx_sssr.h>
#include <FidelityFX/host/backends/vk/ffx_vk.h>
#include <stdlib.h>
#include <string.h>

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
#define CXDOF_FLAGS FFX_DOF_REVERSE_DEPTH
#define CXSSSR_FLAGS FFX_SSSR_ENABLE_DEPTH_INVERTED

// AMD's backend for one effect: its interface and the scratch memory it keeps its state in.
struct CxBackend
{
    FfxInterface iface;
    void* scratch;
};

struct CxFsr
{
    FfxFsr3UpscalerContext ctx;
    CxBackend backend;
    FfxResourceInternal shared[3];
};

struct CxDof
{
    FfxDofContext ctx;
    CxBackend backend;
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

struct CxDofDispatch
{
    VkCommandBuffer cmd;
    CxFsrImage color, depth, output;
    float cocScale, focusNear, focusFar, depthA, depthB, depthMin, depthMax;
};

struct CxSssr
{
    FfxSssrContext ctx;
    CxBackend backend;
};

struct CxSssrDispatch
{
    VkCommandBuffer cmd;
    CxFsrImage color, depth, motion, normal, env, output;
    uint32_t envMips;
    float invViewProjection[16], projection[16], invProjection[16], view[16], invView[16], prevViewProjection[16];
    float mvScaleX, mvScaleY, thickness, stability, roughnessCap, variance;
    uint32_t maxIterations, minOccupancy, samplesPerQuad;
    float probeAt[3];
};

struct CxFsrDispatch
{
    VkCommandBuffer cmd;
    CxFsrImage color, depth, motion, exposure, reactive, output;
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

// The device taken and AMD's backend made over zeroed scratch memory, for one effect's context.
static FfxErrorCode BackendCreate(const CxFsrDevice* dev, CxBackend* b, size_t contexts = 1)
{
    s_instance = dev->instance; s_gipa = dev->gipa; s_gdpa = dev->gdpa;
    size_t size = ffxGetScratchMemorySizeVK(dev->physical, contexts);
    // zeroed: the backend reads a reference count out of it before it writes one
    b->scratch = calloc(1, size);
    if (!b->scratch) return FFX_ERROR_OUT_OF_MEMORY;
    VkDeviceContext vk = { dev->device, dev->physical, dev->gdpa };
    FfxErrorCode e = ffxGetInterfaceVK(&b->iface, ffxGetDeviceVK(&vk), b->scratch, size, contexts);
    if (e != FFX_OK) free(b->scratch);
    return e;
}

extern "C" {

// An FfxErrorCode: 0 is a context in *out, ready to dispatch.
int cxfsr_create(const CxFsrDevice* dev, uint32_t maxRenderW, uint32_t maxRenderH, uint32_t displayW, uint32_t displayH,
                 CxFsr** out)
{
    *out = nullptr;
    CxFsr* f = (CxFsr*)calloc(1, sizeof(CxFsr));
    if (!f) return FFX_ERROR_OUT_OF_MEMORY;
    FfxErrorCode e = BackendCreate(dev, &f->backend);
    if (e != FFX_OK)
    {
        free(f);
        return e;
    }
    FfxFsr3UpscalerContextDescription d = {};
    d.flags = CXFSR_FLAGS;
    d.maxRenderSize = { maxRenderW, maxRenderH };
    d.maxUpscaleSize = { displayW, displayH };
    d.backendInterface = f->backend.iface;
    e = ffxFsr3UpscalerContextCreate(&f->ctx, &d);
    if (e == FFX_OK)
    {
        FfxFsr3UpscalerSharedResourceDescriptions s = {};
        e = ffxFsr3UpscalerGetSharedResourceDescriptions(&f->ctx, &s);
        const FfxCreateResourceDescription* rs[3] = { &s.dilatedDepth, &s.dilatedMotionVectors, &s.reconstructedPrevNearestDepth };
        int made = 0;
        for (; made < 3 && e == FFX_OK; made++) e = f->backend.iface.fpCreateResource(&f->backend.iface, rs[made], 0, &f->shared[made]);
        if (e != FFX_OK)
        {
            for (int i = 0; i < made - 1; i++) f->backend.iface.fpDestroyResource(&f->backend.iface, f->shared[i], 0);
            ffxFsr3UpscalerContextDestroy(&f->ctx);
        }
    }
    if (e != FFX_OK)
    {
        free(f->backend.scratch);
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
    // The reactive mask the accumulate reads unconditionally -- no permutation of CXFSR_FLAGS switches it on, so
    // tools/ffx/ffx_shaders.py compiles nothing new for it. transparencyAndComposition stays empty: CX marks what
    // the beam, particle, halo and weather passes changed, which is this input's own job.
    x.reactive = Wrap(d->reactive, L"cx reactive");
    x.output = Wrap(d->output, L"cx upscaled");
    x.dilatedDepth = f->backend.iface.fpGetResource(&f->backend.iface, f->shared[0]);
    x.dilatedMotionVectors = f->backend.iface.fpGetResource(&f->backend.iface, f->shared[1]);
    x.reconstructedPrevNearestDepth = f->backend.iface.fpGetResource(&f->backend.iface, f->shared[2]);
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

// The dispatches AMD's backend rings its constants and descriptor sets over, for either effect.
int cxffx_ring() { return FFX_MAX_QUEUED_FRAMES; }

// FSR's jitter sequence length.
int cxfsr_phase_count(int renderW, int displayW) { return ffxFsr3UpscalerGetJitterPhaseCount(renderW, displayW); }

// Free a context once no submit can still reach it.
void cxfsr_destroy(CxFsr* f)
{
    if (!f) return;
    for (int i = 0; i < 3; i++) f->backend.iface.fpDestroyResource(&f->backend.iface, f->shared[i], 0);
    ffxFsr3UpscalerContextDestroy(&f->ctx);
    free(f->backend.scratch);
    free(f);
}

// An FfxErrorCode: 0 is a context in *out for w x h frames, with rings kernel rings and the blur's limit a fraction of h.
int cxdof_create(const CxFsrDevice* dev, uint32_t w, uint32_t h, uint32_t rings, float cocLimitFactor, CxDof** out)
{
    *out = nullptr;
    CxDof* c = (CxDof*)calloc(1, sizeof(CxDof));
    if (!c) return FFX_ERROR_OUT_OF_MEMORY;
    FfxErrorCode e = BackendCreate(dev, &c->backend);
    if (e != FFX_OK)
    {
        free(c);
        return e;
    }
    FfxDofContextDescription d = {};
    d.flags = CXDOF_FLAGS;
    d.quality = rings;
    d.resolution = { w, h };
    d.backendInterface = c->backend.iface;
    d.cocLimitFactor = cocLimitFactor;
    e = ffxDofContextCreate(&c->ctx, &d);
    if (e != FFX_OK)
    {
        free(c->backend.scratch);
        free(c);
        return e;
    }
    *out = c;
    return FFX_OK;
}

// One depth of field recorded into d->cmd; an FfxErrorCode.
int cxdof_dispatch(CxDof* c, const CxDofDispatch* d)
{
    FfxDofDispatchDescription x = {};
    x.commandList = ffxGetCommandListVK(d->cmd);
    x.color = Wrap(d->color, L"cx dof colour");
    x.depth = Wrap(d->depth, L"cx dof depth");
    x.output = Wrap(d->output, L"cx dof output");
    x.cocScale = d->cocScale;
    x.cocFocusNear = d->focusNear;
    x.cocFocusFar = d->focusFar;
    x.cocDepthA = d->depthA;
    x.cocDepthB = d->depthB;
    x.cocDepthMin = d->depthMin;
    x.cocDepthMax = d->depthMax;
    return ffxDofContextDispatch(&c->ctx, &x);
}

// An FfxErrorCode: 0 is a context in *out for w x h frames, its denoiser beside it on the same backend.
int cxsssr_create(const CxFsrDevice* dev, uint32_t w, uint32_t h, CxSssr** out)
{
    *out = nullptr;
    CxSssr* c = (CxSssr*)calloc(1, sizeof(CxSssr));
    if (!c) return FFX_ERROR_OUT_OF_MEMORY;
    FfxErrorCode e = BackendCreate(dev, &c->backend, FFX_SSSR_CONTEXT_COUNT);
    if (e != FFX_OK)
    {
        free(c);
        return e;
    }
    FfxSssrContextDescription d = {};
    d.flags = CXSSSR_FLAGS;
    d.renderSize = { w, h };
    d.normalsHistoryBufferFormat = FFX_SURFACE_FORMAT_R16G16B16A16_FLOAT;
    d.backendInterface = c->backend.iface;
    e = ffxSssrContextCreate(&c->ctx, &d);
    if (e != FFX_OK)
    {
        free(c->backend.scratch);
        free(c);
        return e;
    }
    *out = c;
    return FFX_OK;
}

// One SSSR recorded into d->cmd; an FfxErrorCode. No BRDF texture: no pass of the effect binds it, and the record's
// weight already carries the split-sum the composite would have read it for.
int cxsssr_dispatch(CxSssr* c, const CxSssrDispatch* d)
{
    FfxSssrDispatchDescription x = {};
    x.commandList = ffxGetCommandListVK(d->cmd);
    x.color = Wrap(d->color, L"cx sssr colour");
    x.depth = Wrap(d->depth, L"cx sssr depth");
    x.motionVectors = Wrap(d->motion, L"cx sssr velocity");
    x.normal = Wrap(d->normal, L"cx sssr normal");
    x.materialParameters = Wrap(d->normal, L"cx sssr material");
    VkImageCreateInfo cube = {};
    cube.flags = VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT;
    cube.imageType = VK_IMAGE_TYPE_2D;
    cube.format = d->env.format;
    cube.extent = { d->env.width, d->env.height, 1 };
    cube.mipLevels = d->envMips;
    cube.arrayLayers = 6;
    cube.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
    x.environmentMap = ffxGetResourceVK((void*)d->env.image, ffxGetImageResourceDescriptionVK(d->env.image, cube), L"cx sssr probe",
                                        FFX_RESOURCE_STATE_COMPUTE_READ);
    x.brdfTexture = FfxResource{};
    x.output = Wrap(d->output, L"cx sssr output");
    memcpy(x.invViewProjection, d->invViewProjection, sizeof(float) * 16);
    memcpy(x.projection, d->projection, sizeof(float) * 16);
    memcpy(x.invProjection, d->invProjection, sizeof(float) * 16);
    memcpy(x.view, d->view, sizeof(float) * 16);
    memcpy(x.invView, d->invView, sizeof(float) * 16);
    memcpy(x.prevViewProjection, d->prevViewProjection, sizeof(float) * 16);
    x.renderSize = { d->output.width, d->output.height };
    x.motionVectorScale = { d->mvScaleX, d->mvScaleY };
    x.iblFactor = 1.0f;
    x.normalUnPackMul = 1.0f;
    x.normalUnPackAdd = 0.0f;
    x.roughnessChannel = 3;
    x.isRoughnessPerceptual = false;
    x.temporalStabilityFactor = d->stability;
    x.depthBufferThickness = d->thickness;
    x.roughnessThreshold = d->roughnessCap;
    x.varianceThreshold = d->variance;
    x.maxTraversalIntersections = d->maxIterations;
    x.minTraversalOccupancy = d->minOccupancy;
    x.mostDetailedMip = 0;
    x.samplesPerQuad = d->samplesPerQuad;
    x.temporalVarianceGuidedTracingEnabled = 1;
    memcpy(x.probeAt, d->probeAt, sizeof(x.probeAt));
    return ffxSssrContextDispatch(&c->ctx, &x);
}

// Free a context once no submit can still reach it.
void cxsssr_destroy(CxSssr* c)
{
    if (!c) return;
    ffxSssrContextDestroy(&c->ctx);
    free(c->backend.scratch);
    free(c);
}

// Free a context once no submit can still reach it.
void cxdof_destroy(CxDof* c)
{
    if (!c) return;
    ffxDofContextDestroy(&c->ctx);
    free(c->backend.scratch);
    free(c);
}
}
