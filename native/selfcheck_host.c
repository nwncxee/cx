// The fast self-check host (tools/selfcheck/run.sh): the demo's stub host, carrying a known client build-id so
// the rot gate passes, that runs the JIT build of the managed sources through hostfxr instead of preloading the
// NativeAOT image. Linux-only, like the rest of the developer tooling.
// Usage: selfcheck-host <libhostfxr.so> <runtimeconfig.json> <assembly.dll>
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>

typedef void *hostfxr_handle;
typedef int32_t (*init_fn)(const char *config, const void *params, hostfxr_handle *handle);
typedef int32_t (*delegate_fn)(hostfxr_handle handle, int type, void **delegate);
typedef int32_t (*close_fn)(hostfxr_handle handle);
typedef int (*load_fn)(const char *assembly, const char *type, const char *method, const char *delegate_type,
                       void *reserved, void **fn);

/// hostfxr's hdt_load_assembly_and_get_function_pointer.
#define HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER 5
/// hostfxr's UNMANAGEDCALLERSONLY_METHOD: the target carries [UnmanagedCallersOnly], so no delegate type.
#define UNMANAGEDCALLERSONLY_METHOD ((const char *)-1)

int main(int argc, char **argv)
{
    if (argc != 4) {
        fprintf(stderr, "usage: %s <libhostfxr.so> <runtimeconfig.json> <assembly.dll>\n", argv[0]);
        return 2;
    }
    void *fxr = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!fxr) { fprintf(stderr, "selfcheck-host: dlopen(%s): %s\n", argv[1], dlerror()); return 2; }
    init_fn init = (init_fn)dlsym(fxr, "hostfxr_initialize_for_runtime_config");
    delegate_fn get = (delegate_fn)dlsym(fxr, "hostfxr_get_runtime_delegate");
    close_fn done = (close_fn)dlsym(fxr, "hostfxr_close");
    if (!init || !get || !done) { fprintf(stderr, "selfcheck-host: %s is not hostfxr\n", argv[1]); return 2; }

    hostfxr_handle h = 0;
    int32_t rc = init(argv[2], 0, &h);
    if (rc != 0 || !h) { fprintf(stderr, "selfcheck-host: runtime init failed: 0x%x\n", (unsigned)rc); return 2; }
    load_fn load = 0;
    rc = get(h, HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER, (void **)&load);
    done(h);
    if (rc != 0 || !load) { fprintf(stderr, "selfcheck-host: no load delegate: 0x%x\n", (unsigned)rc); return 2; }

    void (*cx_init)(void) = 0;
    rc = load(argv[3], "CX.Entry, cx_managed", "CxInit", UNMANAGEDCALLERSONLY_METHOD, 0, (void **)&cx_init);
    if (rc != 0 || !cx_init) { fprintf(stderr, "selfcheck-host: CX.Entry.CxInit not found: 0x%x\n", (unsigned)rc); return 2; }
    cx_init();
    return 0;
}
