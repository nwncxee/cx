// NVIDIA DLSS Super Resolution behind a flat C API for cx_managed (DirectPInvoke "cxdlss", Windows only): the NGX
// device extensions to ask for, one context per render/display size, and its dispatch. Mirrors native/ffx/cxfsr.cpp.
// NGX reaches Vulkan only through the gipa/gdpa it is handed, so like cxfsr this links no Vulkan loader.
#include <vulkan/vulkan.h>
#include <nvsdk_ngx_vk.h>
#include <nvsdk_ngx_helpers_vk.h>
#include <stdlib.h>

// One NGX instance per host device; a second device would need these per context.
static VkDevice s_device;
static NVSDK_NGX_Parameter* s_caps;
static int s_contexts;

// A project id NVIDIA's guide allows for a build without an application id; any stable GUID string.
static const char kProjectId[] = "6f1c2b9e-5d0a-4c7e-9a43-2b8e7f0d4c11";

struct CxDlssDevice
{
    VkInstance instance;
    PFN_vkGetInstanceProcAddr gipa;
    VkPhysicalDevice physical;
    VkDevice device;
    PFN_vkGetDeviceProcAddr gdpa;
};

// One image of a dispatch: inputs in SHADER_READ_ONLY_OPTIMAL, the output in GENERAL.
struct CxDlssImage
{
    VkImage image;
    VkImageView view;
    VkFormat format;
    uint32_t width, height;
};

struct CxDlssDispatch
{
    VkCommandBuffer cmd;
    CxDlssImage color, depth, motion, output;
    float jitterX, jitterY, mvScaleX, mvScaleY;
    uint32_t renderW, renderH;
    float frameMs;
    uint32_t reset;
};

struct CxDlss
{
    NVSDK_NGX_Handle* handle;
};

static NVSDK_NGX_Resource_VK Res(const CxDlssImage& i, bool write)
{
    VkImageSubresourceRange r = {};
    r.aspectMask = (i.format == VK_FORMAT_D32_SFLOAT || i.format == VK_FORMAT_D24_UNORM_S8_UINT || i.format == VK_FORMAT_D32_SFLOAT_S8_UINT)
                   ? VK_IMAGE_ASPECT_DEPTH_BIT : VK_IMAGE_ASPECT_COLOR_BIT;
    r.levelCount = 1;
    r.layerCount = 1;
    return NVSDK_NGX_Create_ImageView_Resource_VK(i.view, i.image, r, i.format, i.width, i.height, write);
}

// The perf-quality mode whose scale band holds renderW / displayW: DLSS refuses a render size outside its mode's range.
static NVSDK_NGX_PerfQuality_Value Mode(uint32_t renderW, uint32_t displayW)
{
    float s = displayW ? (float)renderW / (float)displayW : 1.0f;
    if (s >= 0.99f) return NVSDK_NGX_PerfQuality_Value_DLAA;   // DLAA takes the full frame only
    if (s >= 0.62f) return NVSDK_NGX_PerfQuality_Value_MaxQuality;
    if (s >= 0.54f) return NVSDK_NGX_PerfQuality_Value_Balanced;
    if (s >= 0.42f) return NVSDK_NGX_PerfQuality_Value_MaxPerf;
    return NVSDK_NGX_PerfQuality_Value_UltraPerformance;
}

extern "C" {

// The instance and device extensions NGX asks for. Static lists; safe before any instance exists.
int cxdlss_required(unsigned* instN, const char*** inst, unsigned* devN, const char*** dev)
{
    NVSDK_NGX_Result r = NVSDK_NGX_VULKAN_RequiredExtensions(instN, inst, devN, dev);
    return NVSDK_NGX_SUCCEED(r) ? 0 : (int)r;
}

// NGX up on the host's device and DLSS asked for: 0 when available, else the NGX result or a CX code
// (1 = DLSS not available on this GPU/driver, 2 = the driver needs an update).
int cxdlss_init(const CxDlssDevice* d, const wchar_t* dataPath)
{
    if (s_device == d->device && s_caps) return 0;
    NVSDK_NGX_Result r = NVSDK_NGX_VULKAN_Init_with_ProjectID(kProjectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0", dataPath,
                                                              d->instance, d->physical, d->device, d->gipa, d->gdpa,
                                                              nullptr, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(r)) return (int)r;
    s_device = d->device;
    r = NVSDK_NGX_VULKAN_GetCapabilityParameters(&s_caps);
    if (NVSDK_NGX_FAILED(r)) { s_caps = nullptr; NVSDK_NGX_VULKAN_Shutdown1(s_device); s_device = VK_NULL_HANDLE; return (int)r; }
    int needsDriver = 0, available = 0;
    NVSDK_NGX_Parameter_GetI(s_caps, NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &needsDriver);
    NVSDK_NGX_Parameter_GetI(s_caps, NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    if (needsDriver) return 2;
    return available ? 0 : 1;
}

// A DLSS context for render frames up to maxW x maxH into displayW x displayH, recorded into cmd (the caller submits
// it and waits before the first dispatch).
int cxdlss_create(VkCommandBuffer cmd, uint32_t maxW, uint32_t maxH, uint32_t displayW, uint32_t displayH, void** out)
{
    *out = nullptr;
    if (!s_caps) return 3;
    NVSDK_NGX_DLSS_Create_Params p = {};
    p.Feature.InWidth = maxW;
    p.Feature.InHeight = maxH;
    p.Feature.InTargetWidth = displayW;
    p.Feature.InTargetHeight = displayH;
    p.Feature.InPerfQualityValue = Mode(maxW, displayW);
    // HDR scene colour, motion at render size and not jittered, reversed depth, and no exposure texture of our own
    p.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_IsHDR | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes
                           | NVSDK_NGX_DLSS_Feature_Flags_DepthInverted | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
    // preset K, the transformer model NVIDIA recommends for image quality, pinned so a driver update cannot swap it
    NVSDK_NGX_Parameter_SetUI(s_caps, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, NVSDK_NGX_DLSS_Hint_Render_Preset_K);
    NVSDK_NGX_Parameter_SetUI(s_caps, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality, NVSDK_NGX_DLSS_Hint_Render_Preset_K);
    NVSDK_NGX_Parameter_SetUI(s_caps, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, NVSDK_NGX_DLSS_Hint_Render_Preset_K);
    CxDlss* c = (CxDlss*)calloc(1, sizeof(CxDlss));
    if (!c) return 4;
    NVSDK_NGX_Result r = NGX_VULKAN_CREATE_DLSS_EXT1(s_device, cmd, 1, 1, &c->handle, s_caps, &p);
    if (NVSDK_NGX_FAILED(r)) { free(c); return (int)r; }
    s_contexts++;
    *out = c;
    return 0;
}

int cxdlss_dispatch(void* ctx, const CxDlssDispatch* d)
{
    CxDlss* c = (CxDlss*)ctx;
    if (!c || !s_caps) return 3;
    NVSDK_NGX_Resource_VK color = Res(d->color, false), depth = Res(d->depth, false), motion = Res(d->motion, false),
                          output = Res(d->output, true);
    NVSDK_NGX_VK_DLSS_Eval_Params e = {};
    e.Feature.pInColor = &color;
    e.Feature.pInOutput = &output;
    e.pInDepth = &depth;
    e.pInMotionVectors = &motion;
    e.InJitterOffsetX = d->jitterX;
    e.InJitterOffsetY = d->jitterY;
    e.InRenderSubrectDimensions.Width = d->renderW;
    e.InRenderSubrectDimensions.Height = d->renderH;
    e.InReset = d->reset ? 1 : 0;
    e.InMVScaleX = d->mvScaleX;
    e.InMVScaleY = d->mvScaleY;
    e.InFrameTimeDeltaInMsec = d->frameMs;
    NVSDK_NGX_Result r = NGX_VULKAN_EVALUATE_DLSS_EXT(d->cmd, c->handle, s_caps, &e);
    return NVSDK_NGX_SUCCEED(r) ? 0 : (int)r;
}

// The context goes; the last one out shuts NGX down on its device.
void cxdlss_destroy(void* ctx)
{
    CxDlss* c = (CxDlss*)ctx;
    if (!c) return;
    if (c->handle) NVSDK_NGX_VULKAN_ReleaseFeature(c->handle);
    free(c);
    s_contexts--;
}

// NGX off the device: the caller has waited for the device to idle and released every context.
void cxdlss_shutdown(void)
{
    if (s_caps) { NVSDK_NGX_VULKAN_DestroyParameters(s_caps); s_caps = nullptr; }
    if (s_device) { NVSDK_NGX_VULKAN_Shutdown1(s_device); s_device = VK_NULL_HANDLE; }
    s_contexts = 0;
}

}
