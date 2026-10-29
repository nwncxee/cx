// VK_LAYER_CX_passtime: GPU time per debug-utils label on live frames.
//
// A timestamp (bottom of pipe: every earlier command done) is written at each vkCmdBeginDebugUtilsLabelEXT and
// vkCmdEndDebugUtilsLabelEXT; a region's time is end minus begin, so siblings partition their parent and a
// "(gaps)" row shows the parent's time no child label claims. Results are read when the app records the command
// buffer again (it has waited the buffer's fence by then), so the layer never stalls a frame.
//
// Environment: PASSTIME_OUT (report path, required), PASSTIME_PROCESS (process to measure, default
// nwmain-linux; others pass through untouched), PASSTIME_FRAMES (window, default 1000 presents),
// PASSTIME_EVERY (rewrite the report every N presents, default 300).
// Linux only, deliberately: it is a measuring tool for the headless harness, not part of the shipped client.
#define _GNU_SOURCE
#include <errno.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vulkan/vk_layer.h>
#include <vulkan/vulkan.h>

#define EXPORT __attribute__((visibility("default")))

enum { MAX_PATHS = 512, MAX_DEPTH = 32, RANGE = 256, RANGES = 64, MAX_REC = 256, RING = 4096 };

/* ---- label paths and samples ------------------------------------------------------------------------------ */

typedef struct {
    char path[256];
    double ns[RING];
    uint64_t frame[RING];
    uint32_t n; /* samples ever added; the ring keeps the last RING */
} Path;

static Path *g_paths;
static int g_npaths;
static uint64_t g_dropped_paths, g_dropped_queries, g_dropped_depth;

/** The id of "parent/name" (parent -1 for a root), interned on first sight; -1 past MAX_PATHS. */
static int intern(int parent, const char *name)
{
    char buf[256];
    if (parent >= 0) snprintf(buf, sizeof buf, "%s/%s", g_paths[parent].path, name);
    else snprintf(buf, sizeof buf, "%s", name);
    for (int i = 0; i < g_npaths; i++)
        if (strcmp(g_paths[i].path, buf) == 0) return i;
    if (g_npaths == MAX_PATHS) { g_dropped_paths++; return -1; }
    Path *p = &g_paths[g_npaths];
    memset(p, 0, sizeof *p);
    memcpy(p->path, buf, sizeof buf);
    return g_npaths++;
}

static void add_sample(int path, double ns, uint64_t frame)
{
    if (path < 0) return;
    Path *p = &g_paths[path];
    p->ns[p->n % RING] = ns;
    p->frame[p->n % RING] = frame;
    p->n++;
}

/* ---- one command buffer's events, and turning its timestamps into regions --------------------------------- */

typedef struct { uint32_t query; int16_t path; uint8_t begin; } Ev;

/** Regions from one command buffer's events and their timestamps (ns): every closed region becomes a sample of
 *  its path, and a region with labelled children also yields "<path>/(gaps)" = itself minus its children. A
 *  region still open at the end, or an end with nothing open, is skipped. */
static void walk(const Ev *ev, int n, const uint64_t *ts, double period, uint64_t frame)
{
    int stack[MAX_DEPTH];
    uint64_t start[MAX_DEPTH];
    double kids[MAX_DEPTH];
    int nkids[MAX_DEPTH];
    int depth = 0;
    for (int i = 0; i < n; i++) {
        if (ev[i].begin) {
            if (depth == MAX_DEPTH) { g_dropped_depth++; continue; }
            stack[depth] = ev[i].path;
            start[depth] = ts[ev[i].query];
            kids[depth] = 0;
            nkids[depth] = 0;
            depth++;
            continue;
        }
        if (depth == 0) continue;
        depth--;
        double ns = (double)(ts[ev[i].query] - start[depth]) * period;
        add_sample(stack[depth], ns, frame);
        if (nkids[depth] > 0 && stack[depth] >= 0) add_sample(intern(stack[depth], "(gaps)"), ns - kids[depth], frame);
        if (depth > 0) { kids[depth - 1] += ns; nkids[depth - 1]++; }
    }
}

/* ---- report ------------------------------------------------------------------------------------------------- */

static int cmp_d(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

/** The q-quantile (0..1) of n sorted values, nearest rank. */
static double quantile(const double *s, int n, double q)
{
    int i = (int)(q * (n - 1) + 0.5);
    return s[i];
}

static void write_report(const char *out, uint64_t frames, uint64_t window, double period)
{
    char tmp[4096];
    snprintf(tmp, sizeof tmp, "%s.tmp", out);
    FILE *f = fopen(tmp, "w");
    if (!f) return;
    uint64_t from = frames > window ? frames - window : 0;
    fprintf(f, "# VK_LAYER_CX_passtime: GPU ms per label over presents %llu..%llu (timestamp period %.3f ns)\n",
            (unsigned long long)from, (unsigned long long)frames, period);
    fprintf(f, "# dropped: %llu paths past %d, %llu command buffers out of queries, %llu labels past depth %d\n",
            (unsigned long long)g_dropped_paths, MAX_PATHS, (unsigned long long)g_dropped_queries,
            (unsigned long long)g_dropped_depth, MAX_DEPTH);
    fprintf(f, "%-44s %7s %9s %9s %9s %9s\n", "label", "n", "median", "p90", "mean", "max");
    static double s[RING];
    for (int i = 0; i < g_npaths; i++) {
        Path *p = &g_paths[i];
        int n = 0;
        uint32_t have = p->n < RING ? p->n : RING;
        double sum = 0;
        for (uint32_t k = 0; k < have; k++)
            if (p->frame[k] >= from) { s[n++] = p->ns[k] / 1e6; sum += p->ns[k] / 1e6; }
        if (n == 0) continue;
        qsort(s, n, sizeof *s, cmp_d);
        int depth = 0;
        const char *leaf = p->path;
        for (const char *c = p->path; *c; c++)
            if (*c == '/') { depth++; leaf = c + 1; }
        char name[64];
        snprintf(name, sizeof name, "%*s%s", depth * 2, "", leaf);
        fprintf(f, "%-44s %7d %9.3f %9.3f %9.3f %9.3f\n", name, n, quantile(s, n, 0.5), quantile(s, n, 0.9),
                sum / n, s[n - 1]);
    }
    fclose(f);
    rename(tmp, out);
}

#ifndef PASSTIME_TEST

/* ---- dispatch ----------------------------------------------------------------------------------------------- */

static void *key(const void *h) { return *(void *const *)h; }

typedef struct {
    void *key;
    VkInstance instance;
    PFN_vkGetInstanceProcAddr gipa;
    PFN_vkDestroyInstance DestroyInstance;
    PFN_vkGetPhysicalDeviceProperties GetPhysicalDeviceProperties;
    PFN_vkCmdBeginDebugUtilsLabelEXT CmdBeginLabel;
    PFN_vkCmdEndDebugUtilsLabelEXT CmdEndLabel;
} Inst;

typedef struct Dev Dev;

typedef struct {
    VkCommandBuffer cb;
    Dev *dev;
    int range;         /* -1: none this recording */
    uint32_t used;
    int submitted;
    uint64_t frame;
    int stack[MAX_DEPTH];
    int depth;
    int nev;
    Ev ev[RANGE];
} Rec;

struct Dev {
    void *key;
    VkDevice device;
    Inst *inst;
    int active;
    double period;
    PFN_vkGetDeviceProcAddr gdpa;
    PFN_vkDestroyDevice DestroyDevice;
    PFN_vkCreateQueryPool CreateQueryPool;
    PFN_vkDestroyQueryPool DestroyQueryPool;
    PFN_vkGetQueryPoolResults GetQueryPoolResults;
    PFN_vkCmdResetQueryPool CmdResetQueryPool;
    PFN_vkCmdWriteTimestamp CmdWriteTimestamp;
    PFN_vkBeginCommandBuffer BeginCommandBuffer;
    PFN_vkQueueSubmit QueueSubmit;
    PFN_vkQueueSubmit2 QueueSubmit2;
    PFN_vkQueuePresentKHR QueuePresentKHR;
    VkQueryPool pool;
    uint8_t range_used[RANGES];
    Rec recs[MAX_REC];
    int nrecs;
    uint64_t frames;
};

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static Inst g_insts[8];
static int g_ninsts;
static Dev *g_devs[8];
static int g_ndevs;
static const char *g_out;
static uint64_t g_window = 1000, g_every = 300;

static Inst *inst_of(const void *h)
{
    void *k = key(h);
    for (int i = 0; i < g_ninsts; i++)
        if (g_insts[i].key == k) return &g_insts[i];
    return g_ninsts ? &g_insts[g_ninsts - 1] : NULL; /* a physical device may not share its instance's key */
}

static Dev *dev_of(const void *h)
{
    void *k = key(h);
    for (int i = 0; i < g_ndevs; i++)
        if (g_devs[i]->key == k) return g_devs[i];
    return NULL;
}

static Rec *rec_of(Dev *d, VkCommandBuffer cb, int make)
{
    for (int i = 0; i < d->nrecs; i++)
        if (d->recs[i].cb == cb) return &d->recs[i];
    if (!make || d->nrecs == MAX_REC) return NULL;
    Rec *r = &d->recs[d->nrecs++];
    memset(r, 0, sizeof *r);
    r->cb = cb;
    r->dev = d;
    r->range = -1;
    return r;
}

/** Read a finished recording's timestamps into samples and give its query range back. */
static void resolve(Rec *r)
{
    Dev *d = r->dev;
    if (r->range >= 0 && r->submitted && r->used > 0) {
        static uint64_t ts[RANGE];
        VkResult res = d->GetQueryPoolResults(d->device, d->pool, (uint32_t)r->range * RANGE, r->used, sizeof ts, ts,
                                              sizeof ts[0], VK_QUERY_RESULT_64_BIT | VK_QUERY_RESULT_WAIT_BIT);
        if (res == VK_SUCCESS) walk(r->ev, r->nev, ts, d->period, r->frame);
    }
    if (r->range >= 0) d->range_used[r->range] = 0;
    r->range = -1;
    r->used = 0;
    r->nev = 0;
    r->depth = 0;
    r->submitted = 0;
}

/* ---- hooks -------------------------------------------------------------------------------------------------- */

static VKAPI_ATTR VkResult VKAPI_CALL BeginCommandBuffer(VkCommandBuffer cb, const VkCommandBufferBeginInfo *bi)
{
    Dev *d = dev_of(cb);
    VkResult res = d->BeginCommandBuffer(cb, bi);
    if (res != VK_SUCCESS || !d->active) return res;
    pthread_mutex_lock(&g_lock);
    Rec *r = rec_of(d, cb, 1);
    if (r) {
        resolve(r);
        for (int i = 0; i < RANGES; i++)
            if (!d->range_used[i]) { d->range_used[i] = 1; r->range = i; break; }
        if (r->range >= 0) d->CmdResetQueryPool(cb, d->pool, (uint32_t)r->range * RANGE, RANGE);
        else g_dropped_queries++;
    }
    pthread_mutex_unlock(&g_lock);
    return res;
}

static void stamp(Rec *r, int path, int begin)
{
    if (r->range < 0 || r->used >= RANGE) return;
    Dev *d = r->dev;
    uint32_t q = (uint32_t)r->range * RANGE + r->used;
    d->CmdWriteTimestamp(r->cb, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, d->pool, q);
    r->ev[r->nev++] = (Ev){ r->used, (int16_t)path, (uint8_t)begin };
    r->used++;
}

static VKAPI_ATTR void VKAPI_CALL CmdBeginLabel(VkCommandBuffer cb, const VkDebugUtilsLabelEXT *l)
{
    Dev *d = dev_of(cb);
    if (d->active) {
        pthread_mutex_lock(&g_lock);
        Rec *r = rec_of(d, cb, 0);
        if (r && r->depth < MAX_DEPTH) {
            int path = intern(r->depth ? r->stack[r->depth - 1] : -1, l->pLabelName ? l->pLabelName : "?");
            r->stack[r->depth++] = path;
            stamp(r, path, 1);
        } else if (r) {
            r->depth++;
            g_dropped_depth++;
        }
        pthread_mutex_unlock(&g_lock);
    }
    d->inst->CmdBeginLabel(cb, l);
}

static VKAPI_ATTR void VKAPI_CALL CmdEndLabel(VkCommandBuffer cb)
{
    Dev *d = dev_of(cb);
    d->inst->CmdEndLabel(cb);
    if (!d->active) return;
    pthread_mutex_lock(&g_lock);
    Rec *r = rec_of(d, cb, 0);
    if (r && r->depth > 0) {
        r->depth--;
        if (r->depth < MAX_DEPTH) stamp(r, r->stack[r->depth], 0);
    }
    pthread_mutex_unlock(&g_lock);
}

static void mark_submitted(Dev *d, VkCommandBuffer cb)
{
    Rec *r = rec_of(d, cb, 0);
    if (r) { r->submitted = 1; r->frame = d->frames; }
}

static VKAPI_ATTR VkResult VKAPI_CALL QueueSubmit(VkQueue q, uint32_t n, const VkSubmitInfo *s, VkFence fence)
{
    Dev *d = dev_of(q);
    if (d->active) {
        pthread_mutex_lock(&g_lock);
        for (uint32_t i = 0; i < n; i++)
            for (uint32_t j = 0; j < s[i].commandBufferCount; j++) mark_submitted(d, s[i].pCommandBuffers[j]);
        pthread_mutex_unlock(&g_lock);
    }
    return d->QueueSubmit(q, n, s, fence);
}

static VKAPI_ATTR VkResult VKAPI_CALL QueueSubmit2(VkQueue q, uint32_t n, const VkSubmitInfo2 *s, VkFence fence)
{
    Dev *d = dev_of(q);
    if (d->active) {
        pthread_mutex_lock(&g_lock);
        for (uint32_t i = 0; i < n; i++)
            for (uint32_t j = 0; j < s[i].commandBufferInfoCount; j++)
                mark_submitted(d, s[i].pCommandBufferInfos[j].commandBuffer);
        pthread_mutex_unlock(&g_lock);
    }
    return d->QueueSubmit2(q, n, s, fence);
}

static VKAPI_ATTR VkResult VKAPI_CALL QueuePresentKHR(VkQueue q, const VkPresentInfoKHR *p)
{
    Dev *d = dev_of(q);
    if (d->active) {
        pthread_mutex_lock(&g_lock);
        if (++d->frames % g_every == 0) write_report(g_out, d->frames, g_window, d->period);
        pthread_mutex_unlock(&g_lock);
    }
    return d->QueuePresentKHR(q, p);
}

static VKAPI_ATTR void VKAPI_CALL DestroyDevice(VkDevice device, const VkAllocationCallbacks *a)
{
    Dev *d = dev_of(device);
    pthread_mutex_lock(&g_lock);
    if (d->active) {
        write_report(g_out, d->frames, g_window, d->period);
        d->DestroyQueryPool(device, d->pool, NULL);
    }
    for (int i = 0; i < g_ndevs; i++)
        if (g_devs[i] == d) { g_devs[i] = g_devs[--g_ndevs]; break; }
    pthread_mutex_unlock(&g_lock);
    PFN_vkDestroyDevice next = d->DestroyDevice;
    free(d);
    next(device, a);
}

static int wanted(void)
{
    const char *want = getenv("PASSTIME_PROCESS");
    g_out = getenv("PASSTIME_OUT");
    if (getenv("PASSTIME_FRAMES")) g_window = strtoull(getenv("PASSTIME_FRAMES"), NULL, 10);
    if (getenv("PASSTIME_EVERY")) g_every = strtoull(getenv("PASSTIME_EVERY"), NULL, 10);
    if (g_every == 0) g_every = 300;
    return g_out && strcmp(program_invocation_short_name, want ? want : "nwmain-linux") == 0;
}

static VKAPI_ATTR VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice phys, const VkDeviceCreateInfo *ci,
                                                   const VkAllocationCallbacks *a, VkDevice *out)
{
    VkLayerDeviceCreateInfo *link = (VkLayerDeviceCreateInfo *)ci->pNext;
    while (link && !(link->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && link->function == VK_LAYER_LINK_INFO))
        link = (VkLayerDeviceCreateInfo *)link->pNext;
    if (!link) return VK_ERROR_INITIALIZATION_FAILED;
    PFN_vkGetInstanceProcAddr gipa = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr gdpa = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    pthread_mutex_lock(&g_lock);
    Inst *in = inst_of(phys);
    pthread_mutex_unlock(&g_lock);
    PFN_vkCreateDevice create = (PFN_vkCreateDevice)gipa(in ? in->instance : VK_NULL_HANDLE, "vkCreateDevice");
    VkResult res = create(phys, ci, a, out);
    if (res != VK_SUCCESS) return res;

    Dev *d = calloc(1, sizeof *d);
    d->key = key(*out);
    d->device = *out;
    d->inst = in;
    d->gdpa = gdpa;
#define DF(name) d->name = (PFN_vk##name)gdpa(*out, "vk" #name)
    DF(DestroyDevice); DF(CreateQueryPool); DF(DestroyQueryPool); DF(GetQueryPoolResults); DF(CmdResetQueryPool);
    DF(CmdWriteTimestamp); DF(BeginCommandBuffer); DF(QueueSubmit); DF(QueueSubmit2); DF(QueuePresentKHR);
#undef DF
    VkPhysicalDeviceProperties props;
    in->GetPhysicalDeviceProperties(phys, &props);
    d->period = props.limits.timestampPeriod;
    d->active = wanted() && in->CmdBeginLabel && in->CmdEndLabel;
    if (d->active) {
        VkQueryPoolCreateInfo qci = { .sType = VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO,
                                      .queryType = VK_QUERY_TYPE_TIMESTAMP, .queryCount = RANGE * RANGES };
        d->active = d->CreateQueryPool(*out, &qci, NULL, &d->pool) == VK_SUCCESS;
        fprintf(stderr, "[passtime] %s: measuring labels into %s\n", d->active ? "LIVE" : "REFUSED (query pool)", g_out);
    }
    pthread_mutex_lock(&g_lock);
    if (!g_paths) g_paths = calloc(MAX_PATHS, sizeof *g_paths);
    if (g_ndevs < 8) g_devs[g_ndevs++] = d;
    else d->active = 0;
    pthread_mutex_unlock(&g_lock);
    return res;
}

static VKAPI_ATTR VkResult VKAPI_CALL CreateInstance(const VkInstanceCreateInfo *ci, const VkAllocationCallbacks *a,
                                                     VkInstance *out)
{
    VkLayerInstanceCreateInfo *link = (VkLayerInstanceCreateInfo *)ci->pNext;
    while (link && !(link->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && link->function == VK_LAYER_LINK_INFO))
        link = (VkLayerInstanceCreateInfo *)link->pNext;
    if (!link) return VK_ERROR_INITIALIZATION_FAILED;
    PFN_vkGetInstanceProcAddr gipa = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    PFN_vkCreateInstance create = (PFN_vkCreateInstance)gipa(VK_NULL_HANDLE, "vkCreateInstance");
    VkResult res = create(ci, a, out);
    if (res != VK_SUCCESS) return res;
    pthread_mutex_lock(&g_lock);
    if (g_ninsts < 8) {
        Inst *in = &g_insts[g_ninsts++];
        in->key = key(*out);
        in->instance = *out;
        in->gipa = gipa;
        in->DestroyInstance = (PFN_vkDestroyInstance)gipa(*out, "vkDestroyInstance");
        in->GetPhysicalDeviceProperties = (PFN_vkGetPhysicalDeviceProperties)gipa(*out, "vkGetPhysicalDeviceProperties");
        in->CmdBeginLabel = (PFN_vkCmdBeginDebugUtilsLabelEXT)gipa(*out, "vkCmdBeginDebugUtilsLabelEXT");
        in->CmdEndLabel = (PFN_vkCmdEndDebugUtilsLabelEXT)gipa(*out, "vkCmdEndDebugUtilsLabelEXT");
    }
    pthread_mutex_unlock(&g_lock);
    return res;
}

static VKAPI_ATTR void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks *a)
{
    pthread_mutex_lock(&g_lock);
    Inst *in = inst_of(instance);
    PFN_vkDestroyInstance next = in->DestroyInstance;
    for (int i = 0; i < g_ninsts; i++)
        if (&g_insts[i] == in) { g_insts[i] = g_insts[--g_ninsts]; break; }
    pthread_mutex_unlock(&g_lock);
    next(instance, a);
}

static PFN_vkVoidFunction device_hook(const char *name)
{
#define H(fn, ours) if (strcmp(name, fn) == 0) return (PFN_vkVoidFunction)ours
    H("vkGetDeviceProcAddr", vkGetDeviceProcAddr);
    H("vkDestroyDevice", DestroyDevice);
    H("vkBeginCommandBuffer", BeginCommandBuffer);
    H("vkCmdBeginDebugUtilsLabelEXT", CmdBeginLabel);
    H("vkCmdEndDebugUtilsLabelEXT", CmdEndLabel);
    H("vkQueueSubmit", QueueSubmit);
    H("vkQueueSubmit2", QueueSubmit2);
    H("vkQueueSubmit2KHR", QueueSubmit2);
    H("vkQueuePresentKHR", QueuePresentKHR);
#undef H
    return NULL;
}

EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice device, const char *name)
{
    PFN_vkVoidFunction ours = device_hook(name);
    if (ours) return ours;
    Dev *d = dev_of(device);
    return d ? d->gdpa(device, name) : NULL;
}

EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char *name)
{
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)vkGetInstanceProcAddr;
    if (strcmp(name, "vkCreateInstance") == 0) return (PFN_vkVoidFunction)CreateInstance;
    if (strcmp(name, "vkDestroyInstance") == 0) return (PFN_vkVoidFunction)DestroyInstance;
    if (strcmp(name, "vkCreateDevice") == 0) return (PFN_vkVoidFunction)CreateDevice;
    PFN_vkVoidFunction ours = device_hook(name);
    if (ours) return ours;
    if (!instance) return NULL;
    pthread_mutex_lock(&g_lock);
    Inst *in = inst_of(instance);
    pthread_mutex_unlock(&g_lock);
    return in ? in->gipa(instance, name) : NULL;
}

EXPORT VKAPI_ATTR VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface *v)
{
    if (v->loaderLayerInterfaceVersion > 2) v->loaderLayerInterfaceVersion = 2;
    v->pfnGetInstanceProcAddr = vkGetInstanceProcAddr;
    v->pfnGetDeviceProcAddr = vkGetDeviceProcAddr;
    v->pfnGetPhysicalDeviceProcAddr = NULL;
    return VK_SUCCESS;
}

#endif
