/* The dlopen every call in cx_managed.so goes through (linked with -Wl,--wrap=dlopen). */
#define _GNU_SOURCE
#include <dlfcn.h>

void* __real_dlopen(const char* name, int flags);

void* __wrap_dlopen(const char* name, int flags)
{
    return __real_dlopen(name, flags);
}
