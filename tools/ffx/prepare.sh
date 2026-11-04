#!/usr/bin/env bash
# FSR 3.1's and FidelityFX DoF's sources for the native builds: AMD's SDK 1.1.4 fetched (tools/deps.json), patched
# (native/ffx/sdk-1.1.4.patch) into build/ffx/sdk, and its Vulkan shader headers generated into
# build/ffx/shaders. Each step reruns only when its inputs are newer. The caller compiles.
# Usage: tools/ffx/prepare.sh <python> <glslang>
set -euo pipefail
cd "$(dirname "$0")/../.."
PY=$1; GLSLANG=$2
"$PY" tools/deps.py ffx-sdk build/deps
"$PY" tools/deps.py vulkan-headers build/deps
if [ ! -f build/ffx/sdk/.patched ] || [ native/ffx/sdk-1.1.4.patch -nt build/ffx/sdk/.patched ]; then
  rm -rf build/ffx/sdk && mkdir -p build/ffx
  cp -r build/deps/FidelityFX-SDK-1.1.4/sdk build/ffx/sdk
  (cd build/ffx && patch -p1 -s < ../../native/ffx/sdk-1.1.4.patch)
  touch build/ffx/sdk/.patched
fi
if [ ! -f build/ffx/shaders/.stamp ] || [ tools/ffx/ffx_shaders.py -nt build/ffx/shaders/.stamp ] \
   || [ build/ffx/sdk/.patched -nt build/ffx/shaders/.stamp ]; then
  echo ">> generating FSR 3.1's and DoF's shader permutations (once)..."
  rm -rf build/ffx/shaders
  "$PY" tools/ffx/ffx_shaders.py "$GLSLANG" build/ffx/sdk build/ffx/shaders
  touch build/ffx/shaders/.stamp
fi
