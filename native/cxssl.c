/* The dlopen every call in cx_managed.so goes through (linked with -Wl,--wrap=dlopen). nwmain-linux exports
   its own static OpenSSL 1.1.1, and the executable comes first in symbol lookup, so a libssl.so.3 opened in
   the game's namespace calls the engine's OPENSSL_init_crypto and crashes. .NET's OpenSSL shim opens libssl
   here; a namespace of its own (dlmopen) sees its own libcrypto and nothing the game exports. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <string.h>

void* __real_dlopen(const char* name, int flags);

void* __wrap_dlopen(const char* name, int flags)
{
    if (name != NULL && strncmp(name, "libssl.so", 9) == 0)
        return dlmopen(LM_ID_NEWLM, name, flags & ~RTLD_GLOBAL);
    return __real_dlopen(name, flags);
}
