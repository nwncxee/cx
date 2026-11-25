// The AI texture models (alloy.upscaleai, alloy.ainormals) through ONNX Runtime's DirectML provider, behind a flat C API
// for cx_managed (DirectPInvoke "cxai", Windows only). onnxruntime.dll is loaded at runtime from the install folder, so
// a missing runtime or GPU only turns the AI work off -- version.dll never fails to load for it. One session per slot:
// 0 the upscaler, 1 the normal-map model.
#include <windows.h>
#include <onnxruntime_c_api.h>
#include <dml_provider_factory.h>
#include <string.h>

static const int Slots = 2;
static const OrtApi* s_api;
static OrtEnv* s_env;
static OrtSession* s_session[Slots];
static OrtMemoryInfo* s_mem;
static SRWLOCK s_run = SRWLOCK_INIT;   // one Run at a time: the DirectML provider does not take concurrent calls

// 0 = ok, else a stage code with the ORT status released.
static int Fail(OrtStatus* st, int code)
{
    if (st) s_api->ReleaseStatus(st);
    return code;
}

extern "C" {

// Open <model> in <slot> with onnxruntime.dll from <dir>: 0 when the DirectML session is up.
// 1 no onnxruntime.dll, 2 no OrtGetApiBase, 3 the API version is too old, 4 env, 5 options,
// 6 no DirectML provider (no D3D12 GPU), 7 the session refused the model, 8 memory info, 9 a bad slot.
int cxai_open(int slot, const wchar_t* dir, const wchar_t* model)
{
    if (slot < 0 || slot >= Slots) return 9;
    if (s_session[slot]) return 0;
    if (!s_api)
    {
        wchar_t path[MAX_PATH * 2];
        wcsncpy(path, dir, MAX_PATH);
        path[MAX_PATH] = 0;
        wcscat(path, L"\\onnxruntime.dll");
        HMODULE lib = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!lib) return 1;
        auto base = (const OrtApiBase*(ORT_API_CALL*)())GetProcAddress(lib, "OrtGetApiBase");
        if (!base) return 2;
        s_api = base()->GetApi(ORT_API_VERSION);
        if (!s_api) return 3;
    }
    if (!s_env)
        if (OrtStatus* st = s_api->CreateEnv(ORT_LOGGING_LEVEL_ERROR, "cx", &s_env)) return Fail(st, 4);
    OrtSessionOptions* opts = nullptr;
    if (OrtStatus* st = s_api->CreateSessionOptions(&opts)) return Fail(st, 5);
    // the DirectML provider's own requirements: no memory pattern, sequential execution
    s_api->DisableMemPattern(opts);
    s_api->SetSessionExecutionMode(opts, ORT_SEQUENTIAL);
    const OrtDmlApi* dml = nullptr;
    if (OrtStatus* st = s_api->GetExecutionProviderApi("DML", ORT_API_VERSION, (const void**)&dml); st || !dml)
    { s_api->ReleaseSessionOptions(opts); return Fail(st, 6); }
    if (OrtStatus* st = dml->SessionOptionsAppendExecutionProvider_DML(opts, 0))
    { s_api->ReleaseSessionOptions(opts); return Fail(st, 6); }
    OrtStatus* st = s_api->CreateSession(s_env, model, opts, &s_session[slot]);
    s_api->ReleaseSessionOptions(opts);
    if (st) { s_session[slot] = nullptr; return Fail(st, 7); }
    if (!s_mem)
        if (OrtStatus* m = s_api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &s_mem)) return Fail(m, 8);
    return 0;
}

// <slot>'s model released, so another can be opened there; the runtime and its environment stay.
void cxai_close(int slot)
{
    if (slot < 0 || slot >= Slots) return;
    AcquireSRWLockExclusive(&s_run);
    if (s_session[slot]) { s_api->ReleaseSession(s_session[slot]); s_session[slot] = nullptr; }
    ReleaseSRWLockExclusive(&s_run);
}

// One tile through <slot>: <in> is 1 x <c> x <h> x <w> float, <out> receives up to <outCount> floats of the output.
// 0 when it ran.
int cxai_run(int slot, const float* in, int c, int w, int h, float* out, int outCount)
{
    if (slot < 0 || slot >= Slots || !s_session[slot]) return 1;
    AcquireSRWLockExclusive(&s_run);
    int64_t shape[4] = { 1, c, h, w };
    size_t n = (size_t)c * w * h;
    OrtValue* x = nullptr;
    OrtValue* y = nullptr;
    int rc = 0;
    OrtStatus* st = s_api->CreateTensorWithDataAsOrtValue(s_mem, (void*)in, n * sizeof(float), shape, 4,
                                                          ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &x);
    if (st) rc = Fail(st, 2);
    else
    {
        const char* inName = "input";
        const char* outName = "output";
        st = s_api->Run(s_session[slot], nullptr, &inName, (const OrtValue* const*)&x, 1, &outName, 1, &y);
        if (st) rc = Fail(st, 3);
        else
        {
            float* data = nullptr;
            OrtTensorTypeAndShapeInfo* info = nullptr;
            size_t count = 0;
            st = s_api->GetTensorMutableData(y, (void**)&data);
            if (!st) st = s_api->GetTensorTypeAndShape(y, &info);
            if (!st) st = s_api->GetTensorShapeElementCount(info, &count);
            if (info) s_api->ReleaseTensorTypeAndShapeInfo(info);
            if (st) rc = Fail(st, 4);
            else memcpy(out, data, (count < (size_t)outCount ? count : (size_t)outCount) * sizeof(float));
        }
    }
    if (y) s_api->ReleaseValue(y);
    if (x) s_api->ReleaseValue(x);
    ReleaseSRWLockExclusive(&s_run);
    return rc;
}

}
