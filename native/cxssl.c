/* The dlopen every call in cx_managed.so goes through (linked with -Wl,--wrap=dlopen). nwmain-linux exports
   its own static OpenSSL 1.1.1, and the executable comes first in symbol lookup, so a libssl.so.3 opened in
   the game's namespace calls the engine's OPENSSL_init_crypto and crashes. .NET's OpenSSL shim opens libssl
   here; RTLD_DEEPBIND makes libssl and the libcrypto it brings look in themselves before anything the game
   exports. A namespace of its own (dlmopen) would isolate it too, but carries a second libc per open, and the
   GPU driver's GL init fails in a process mapping more than one. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <string.h>

void* __real_dlopen(const char* name, int flags);

void* __wrap_dlopen(const char* name, int flags)
{
    if (name != NULL && strncmp(name, "libssl.so", 9) == 0)
        return __real_dlopen(name, (flags & ~RTLD_GLOBAL) | RTLD_DEEPBIND);
    return __real_dlopen(name, flags);
}
