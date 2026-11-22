#!/usr/bin/env bash
# FSR 3.1's and FidelityFX DoF's sources for the native builds: AMD's SDK 1.1.4 fetched (tools/deps.json), patched
# (native/ffx/sdk-1.1.4.patch) into build/ffx/sdk, and its Vulkan shader headers generated into
# build/ffx/shaders. Each step reruns only when the content of its inputs changed (tools/ffx/stamp.sh).
# The caller compiles: tools/ffx/build-lib.sh.
# Usage: tools/ffx/prepare.sh <python> <glslang>
set -euo pipefail
cd "$(dirname "$0")/../.."
. tools/ffx/stamp.sh
PY=$1; GLSLANG=$2
"$PY" tools/deps.py ffx-sdk build/deps
"$PY" tools/deps.py vulkan-headers build/deps
patched=$(inputs_hash native/ffx/sdk-1.1.4.patch build/deps/.ffx-sdk.sha256)
if stale build/ffx/sdk/.patched "$patched"; then
  rm -rf build/ffx/sdk && mkdir -p build/ffx
  cp -r build/deps/FidelityFX-SDK-1.1.4/sdk build/ffx/sdk
  (cd build/ffx && patch -p1 -s < ../../native/ffx/sdk-1.1.4.patch)
  echo "$patched" > build/ffx/sdk/.patched
fi
shaders=$(inputs_hash tools/ffx/ffx_shaders.py build/ffx/sdk/.patched)
if stale build/ffx/shaders/.stamp "$shaders"; then
  echo ">> generating FSR 3.1's and DoF's shader permutations (once)..."
  rm -rf build/ffx/shaders
  "$PY" tools/ffx/ffx_shaders.py "$GLSLANG" build/ffx/sdk build/ffx/shaders
  echo "$shaders" > build/ffx/shaders/.stamp
fi
