#!/usr/bin/env bash
# Given a host that exports its own OPENSSL_init_crypto, as nwmain-linux exports its static OpenSSL 1.1.1
# When it opens libssl.so.3 through the dlopen cx_managed.so links (native/cxssl.c) and calls OPENSSL_init_ssl
# Then libssl reaches its own libcrypto, never the host's copy
set -uo pipefail
cd "$(dirname "$0")/.."
fail() { echo "!! ssl-namespace regression: $*" >&2; exit 1; }
libdir=$(pkg-config --variable=libdir openssl 2>/dev/null) || fail "no openssl in pkg-config (run inside nix develop)"
[ -f "$libdir/libssl.so.3" ] || fail "no libssl.so.3 in $libdir"
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
cat > "$t/host.c" <<'C'
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
int OPENSSL_init_crypto(uint64_t opts, const void* settings)
{
    (void)opts; (void)settings;
    puts("host OPENSSL_init_crypto");
    exit(3);
}
int main(void)
{
    void* ssl = dlopen("libssl.so.3", RTLD_LAZY);
    if (!ssl) { printf("no libssl: %s\n", dlerror()); return 2; }
    int (*init)(uint64_t, const void*) = (int (*)(uint64_t, const void*))dlsym(ssl, "OPENSSL_init_ssl");
    if (!init) { puts("no OPENSSL_init_ssl"); return 2; }
    printf("OPENSSL_init_ssl returned %d\n", init(0, NULL));
    return 0;
}
C
cc -O1 -rdynamic -Wl,--wrap=dlopen "$t/host.c" native/cxssl.c -ldl -o "$t/host" || fail "the host did not build"
out=$(LD_LIBRARY_PATH="$libdir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" "$t/host" 2>&1); rc=$?
[ "$rc" -eq 0 ] && grep -q "OPENSSL_init_ssl returned 1" <<<"$out" || fail "libssl was captured by the host (exit $rc): $out"
echo "ssl-namespace regression: passed"
