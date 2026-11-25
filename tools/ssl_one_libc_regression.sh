#!/usr/bin/env bash
# Given a host linked with the dlopen cx_managed.so links (native/cxssl.c)
# When it opens libssl.so.3 twice, as UpdateService's probe and .NET's OpenSSL shim both do
# Then the process still maps one libc, so the GPU driver's libraries load beside the only C runtime there is
set -uo pipefail
cd "$(dirname "$0")/.."
fail() { echo "!! ssl-one-libc regression: $*" >&2; exit 1; }
libdir=$(pkg-config --variable=libdir openssl 2>/dev/null) || fail "no openssl in pkg-config (run inside nix develop)"
[ -f "$libdir/libssl.so.3" ] || fail "no libssl.so.3 in $libdir"
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
cat > "$t/host.c" <<'C'
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>
int main(void)
{
    for (int i = 0; i < 2; i++)
        if (!dlopen("libssl.so.3", RTLD_LAZY)) { printf("no libssl: %s\n", dlerror()); return 2; }
    FILE* maps = fopen("/proc/self/maps", "r");
    char line[4096];
    int libcs = 0;
    while (fgets(line, sizeof line, maps))
        if (strstr(line, " r-xp ") && strstr(line, "/libc.so.6")) libcs++;
    printf("libc text mappings: %d\n", libcs);
    return 0;
}
C
cc -O1 -Wl,--wrap=dlopen "$t/host.c" native/cxssl.c -ldl -o "$t/host" || fail "the host did not build"
out=$(LD_LIBRARY_PATH="$libdir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" "$t/host" 2>&1); rc=$?
[ "$rc" -eq 0 ] && grep -q "libc text mappings: 1$" <<<"$out" || fail "opening libssl loaded another C runtime (exit $rc): $out"
echo "ssl-one-libc regression: passed"
