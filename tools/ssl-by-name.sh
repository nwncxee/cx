#!/usr/bin/env bash
# Refuses a managed library whose OpenSSL shim opens libssl at an absolute path. .NET's shim asks the loader for
# libssl.so.3 by name; a toolchain that patched it to one store path aborts the client wherever that file is
# missing or binds to another libcrypto. Usage: ssl-by-name.sh <cx_managed.so>
set -uo pipefail
lib=${1:?usage: ssl-by-name.sh <library>}
[ -f "$lib" ] || { echo "!! ssl-by-name: $lib does not exist" >&2; exit 2; }
hits=$(LC_ALL=C grep -aoE '/[[:graph:]]*/libssl\.so[.0-9]*' "$lib" | sort -u)
[ -z "$hits" ] || {
  printf '!! %s opens libssl at a fixed path, so the client aborts on a machine without it:\n%s\n' "$lib" "$hits" >&2
  echo "!! link the NativeAOT runtime pack from nuget.org (managed/cx_managed.csproj NetCoreTargetingPackRoot)" >&2
  exit 1
}
LC_ALL=C grep -aq 'libssl\.so\.3' "$lib" || { echo "!! ssl-by-name: $lib names no libssl.so.3 at all" >&2; exit 1; }
