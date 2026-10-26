// The Windows half of the load path. Unlike bootstrap.c this is NOT a separate module: it is compiled to
// an .obj and linked INTO cx_managed.dll, which then ships as `version.dll`, beside nwmain.exe (with the
// CX-57 audio runtime, cx_openal.dll and phonon.dll, next to it).
//
// Windows has no LD_PRELOAD, so we get loaded as a PROXY DLL: nwmain.exe imports VERSION.dll
// (GetFileVersionInfoA/SizeA, VerQueryValueA), so a version.dll beside the exe is loaded before the real
// one. We forward every version.dll export to the system copy so the game's version calls still work, and
// start the managed extender on the way past -- exactly what the LD_PRELOAD ctor does.
//
// Why a CRT initializer and not DllMain: the NativeAOT image already owns DllMain (it initializes the
// runtime there), and a second definition would not link. The .CRT$XCU table runs during that same
// DLL_PROCESS_ATTACH, which is early enough to be sure we run and late enough to have a thread to start.
//
// ponytail: no <windows.h>/<stdlib.h>. The handful of kernel32 calls are declared inline so this compiles
// with clang-cl on Linux -- no MSVC VCTools/CRT headers needed (Arch has the Win10 SDK but not vcruntime.h).
// Add the real headers back only if this ever grows past a dozen Win32 calls.
//
// ponytail: forwards hard-code System32\version.<fn>. Correct for a 64-bit DLL in the 64-bit client; if we
// ever ship 32-bit, forward to SysWOW64 instead.

// Forward all of version.dll to the real one so the host's version queries still work. These must stay
// #pragma directives: routed through an MSBuild <LinkerArg> instead, the backslashes come out as forward
// slashes and the NT loader cannot resolve the forward.
#pragma comment(linker, "/export:GetFileVersionInfoA=C:\\Windows\\System32\\version.GetFileVersionInfoA")
#pragma comment(linker, "/export:GetFileVersionInfoW=C:\\Windows\\System32\\version.GetFileVersionInfoW")
#pragma comment(linker, "/export:GetFileVersionInfoExA=C:\\Windows\\System32\\version.GetFileVersionInfoExA")
#pragma comment(linker, "/export:GetFileVersionInfoExW=C:\\Windows\\System32\\version.GetFileVersionInfoExW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeExA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeExW")
#pragma comment(linker, "/export:GetFileVersionInfoByHandle=C:\\Windows\\System32\\version.GetFileVersionInfoByHandle")
#pragma comment(linker, "/export:VerFindFileA=C:\\Windows\\System32\\version.VerFindFileA")
#pragma comment(linker, "/export:VerFindFileW=C:\\Windows\\System32\\version.VerFindFileW")
#pragma comment(linker, "/export:VerInstallFileA=C:\\Windows\\System32\\version.VerInstallFileA")
#pragma comment(linker, "/export:VerInstallFileW=C:\\Windows\\System32\\version.VerInstallFileW")
#pragma comment(linker, "/export:VerLanguageNameA=C:\\Windows\\System32\\version.VerLanguageNameA")
#pragma comment(linker, "/export:VerLanguageNameW=C:\\Windows\\System32\\version.VerLanguageNameW")
#pragma comment(linker, "/export:VerQueryValueA=C:\\Windows\\System32\\version.VerQueryValueA")
#pragma comment(linker, "/export:VerQueryValueW=C:\\Windows\\System32\\version.VerQueryValueW")

// --- Minimal Win32 surface (see ponytail note above) ------------------------
#define WINAPI __stdcall
typedef int                 BOOL;
typedef unsigned long       DWORD;
typedef void               *HANDLE;
typedef void               *LPVOID;
typedef unsigned long long  SIZE_T;
typedef DWORD (WINAPI *LPTHREAD_START_ROUTINE)(LPVOID);

__declspec(dllimport) HANDLE WINAPI CreateThread(LPVOID, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, DWORD *);
__declspec(dllimport) BOOL   WINAPI CloseHandle(HANDLE);
__declspec(dllimport) HANDLE WINAPI GetModuleHandleW(const unsigned short *);
__declspec(dllimport) DWORD  WINAPI GetModuleFileNameW(HANDLE, unsigned short *, DWORD);
__declspec(dllimport) HANDLE WINAPI LoadLibraryExW(const unsigned short *, HANDLE, DWORD);
__declspec(dllimport) void * WINAPI GetProcAddress(HANDLE, const char *);
__declspec(dllimport) BOOL   WINAPI VirtualProtect(LPVOID, SIZE_T, DWORD, DWORD *);
__declspec(dllimport) HANDLE WINAPI CreateEventW(LPVOID, BOOL, BOOL, const unsigned short *);
__declspec(dllimport) BOOL   WINAPI SetEvent(HANDLE);
__declspec(dllimport) DWORD  WINAPI WaitForSingleObject(HANDLE, DWORD);
#define LOAD_WITH_ALTERED_SEARCH_PATH 0x8   /* resolve the DLL's own imports from its directory */
#define PAGE_READWRITE 0x04
extern char __ImageBase;                     /* this module, as the linker places it */
// ---------------------------------------------------------------------------

/// The managed entry point, exported by the NativeAOT half of this same DLL.
extern void CxInit(void);

/// CX-57: nwmain.exe imports OpenAL32.dll, and the copy beside it is the game's own (OpenAL Soft 1.20.1), which
/// Steam's verify would restore if we replaced it. So the import table is pointed at CX's pinned build instead
/// (cx_openal.dll beside this DLL), here, before the game's first instruction: the loader has bound every
/// import by the time DLL initializers run, and nwmain's entry point runs after them. Every OpenAL32.dll entry
/// the game imports is redirected or none is -- a half-redirected table would hand one library's handles to the
/// other. The managed side reads the outcome through cx_openal_redirect.
static HANDLE cx_openal;
static int cx_openal_patched = -1;   /* -1 not tried, 0 failed (and why in cx_openal_why), else entries patched */
static const char *cx_openal_why = "not tried";

static int cx_ieq(const char *a, const char *b)
{
    for (; *a && *b; a++, b++)
        if ((*a | 0x20) != (*b | 0x20)) return 0;
    return *a == *b;
}

static void cx_redirect_openal(void)
{
    unsigned short path[1024];
    DWORD n = GetModuleFileNameW((HANDLE)&__ImageBase, path, 1024 - 16);
    if (n == 0 || n >= 1024 - 16) { cx_openal_patched = 0; cx_openal_why = "own path unreadable"; return; }
    while (n > 0 && path[n - 1] != '\\' && path[n - 1] != '/') n--;
    const char *leaf = "cx_openal.dll";
    for (int i = 0; leaf[i]; i++) path[n++] = (unsigned short)leaf[i];
    path[n] = 0;
    cx_openal = LoadLibraryExW(path, 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!cx_openal) { cx_openal_patched = 0; cx_openal_why = "cx_openal.dll missing or refused to load"; return; }

    unsigned char *base = (unsigned char *)GetModuleHandleW(0);
    unsigned char *nt = base + *(unsigned int *)(base + 0x3c);
    unsigned int impRva = *(unsigned int *)(nt + 24 + 0x78);   /* PE32+ optional header, DataDirectory[IMPORT] */
    if (!impRva) { cx_openal_patched = 0; cx_openal_why = "nwmain.exe has no import directory"; return; }
    for (unsigned int *d = (unsigned int *)(base + impRva); d[3]; d += 5)
    {
        if (!cx_ieq((const char *)(base + d[3]), "OpenAL32.dll")) continue;
        if (!d[0]) { cx_openal_patched = 0; cx_openal_why = "OpenAL32.dll imports carry no name table"; return; }
        unsigned long long *names = (unsigned long long *)(base + d[0]), *iat = (unsigned long long *)(base + d[4]);
        void *to[64];
        int count = 0;
        for (; names[count]; count++)
        {
            if (count == 64 || (names[count] >> 63)) { cx_openal_patched = 0; cx_openal_why = "an OpenAL32.dll import by ordinal"; return; }
            to[count] = GetProcAddress(cx_openal, (const char *)(base + (unsigned int)names[count] + 2));
            if (!to[count]) { cx_openal_patched = 0; cx_openal_why = "cx_openal.dll lacks an entry the game imports"; return; }
        }
        DWORD old;
        if (!VirtualProtect(iat, sizeof(*iat) * count, PAGE_READWRITE, &old)) { cx_openal_patched = 0; cx_openal_why = "VirtualProtect refused the import table"; return; }
        for (int i = 0; i < count; i++) iat[i] = (unsigned long long)to[i];
        VirtualProtect(iat, sizeof(*iat) * count, old, &old);
        cx_openal_patched = count;
        cx_openal_why = "ok";
        return;
    }
    cx_openal_patched = 0;
    cx_openal_why = "nwmain.exe does not import OpenAL32.dll";
}

/// For SoundService: entries patched (0 = failed), the pinned module, and why it failed.
int cx_openal_redirect(void **module, const char **why)
{
    if (module) *module = cx_openal;
    if (why) *why = cx_openal_why;
    return cx_openal_patched;
}

/// The game's main thread waits at SDL_Init until CxInit has run: the managed extender starts on its own thread (below),
/// and the hooks it installs on SDL_CreateWindow and SDL_GL_CreateContext -- the choice of a session with no GL context --
/// must be in place before main reaches them. funchook is linked into this image for the managed side; one more
/// instance here patches only SDL_Init, which nothing else hooks. A timeout lets the game go on if CxInit never returns.
extern void *funchook_create(void);
extern int funchook_prepare(void *fh, void **target, void *hook);
extern int funchook_install(void *fh, int flags);

#define CX_READY_TIMEOUT_MS 30000
static HANDLE cx_ready;
static int (*cx_sdl_init)(unsigned int);

static int cx_sdl_init_gate(unsigned int flags)
{
    if (cx_ready) WaitForSingleObject(cx_ready, CX_READY_TIMEOUT_MS);
    return cx_sdl_init(flags);
}

static void cx_gate_sdl_init(void)
{
    cx_sdl_init = (int (*)(unsigned int))GetProcAddress(GetModuleHandleW(0), "SDL_Init");
    cx_ready = CreateEventW(0, 1, 0, 0);
    void *fh = cx_ready && cx_sdl_init ? funchook_create() : 0;
    if (!fh || funchook_prepare(fh, (void **)&cx_sdl_init, (void *)cx_sdl_init_gate) != 0 || funchook_install(fh, 0) != 0)
        cx_ready = 0;
}

static DWORD WINAPI cx_start(LPVOID unused)
{
    (void) unused;
    // Off the loader lock: NativeAOT runtime startup and the funchook installs cannot run inside
    // DLL_PROCESS_ATTACH without risking a loader-lock deadlock. A thread created there does not run
    // until the loader releases, which is also after the image's own DllMain has started the runtime.
    CxInit();
    if (cx_ready) SetEvent(cx_ready);
    return 0;
}

static int cx_boot(void)
{
    cx_redirect_openal();
    cx_gate_sdl_init();
    HANDLE t = CreateThread(0, 0, cx_start, 0, 0, 0);
    if (t) CloseHandle(t);
    return 0;
}

// The CRT initializer table. `read` + the allocate pragma is the documented way to place a function
// pointer there without a CRT; the linker walks .CRT$XC* in name order during process attach.
#pragma section(".CRT$XCU", long, read)
__declspec(allocate(".CRT$XCU")) int (*cx_boot_)(void) = cx_boot;
