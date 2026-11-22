#!/usr/bin/env bash
# FSR 3.1 upscaling -- AMD's FidelityFX SDK 1.1.4 upscaler and Vulkan backend, patched, its shaders compiled by
# glslang (tools/ffx/prepare.sh, run first), and native/ffx/cxfsr.cpp's C API in front: static and PIC like xatlas
# (DirectPInvoke "cxfsr"). Rebuilds build/libcxfsr.a only when the content of its inputs changed.
set -euo pipefail
cd "$(dirname "$0")/../.."
. tools/ffx/stamp.sh
# shellcheck disable=SC1091 # tools/ffx/cxxflags.sh only sets F and FFX_CXXFLAGS, read below
. tools/ffx/cxxflags.sh
key=$(inputs_hash native/ffx/cxfsr.cpp native/ffx/compat.h tools/ffx/sources.txt tools/ffx/cxxflags.sh \
  tools/ffx/build-lib.sh build/ffx/shaders/.stamp)
[ -f build/libcxfsr.a ] && ! stale build/libcxfsr.a.key "$key" && exit 0
echo ">> building FSR 3.1 (libcxfsr.a)..."
mkdir -p build/ffx/obj && rm -f build/ffx/obj/*.o
pids=()
for src in $(sed "s|^|$F/src/|" tools/ffx/sources.txt) native/ffx/cxfsr.cpp; do
  c++ "${FFX_CXXFLAGS[@]}" -fvisibility=hidden -c "$src" -o "build/ffx/obj/$(basename "$src" .cpp).o" & pids+=($!)
done
for p in "${pids[@]}"; do wait "$p" || { echo "!! FSR 3.1 failed to compile (above)" >&2; exit 1; }; done
rm -f build/libcxfsr.a build/libcxfsr.a.key && ar rcs build/libcxfsr.a build/ffx/obj/*.o
echo "$key" > build/libcxfsr.a.key
