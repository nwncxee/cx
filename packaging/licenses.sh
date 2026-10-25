#!/usr/bin/env bash
# Writes LICENSES.txt ($1) from the repo's LICENSE plus the verbatim notices of every
# third-party library linked into the shipped binaries.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
{
  echo "NWNCX:EE -- licenses"
  echo "==============================="
  echo
  echo "The extender itself is MIT licensed. It links funchook (GPLv2+ with a linking"
  echo "exception), diStorm3 (BSD 3-clause) and xatlas (MIT), and embeds the SMAA lookup"
  echo "tables (MIT) and the DejaVu Sans font (Bitstream Vera license). It ships, as separate"
  echo "unmodified libraries, OpenAL Soft 1.25.2 (LGPL-2.1: libopenal.so.1 / cx_openal.dll,"
  echo "source at https://github.com/kcat/openal-soft/releases/tag/1.25.2 -- replace the file"
  echo "with any compatible build of your own), Steam Audio 4.8.1 (Apache-2.0:"
  echo "libphonon.so / phonon.dll, https://github.com/ValveSoftware/steam-audio), Google"
  echo "shaderc (Apache-2.0: libshaderc_shared.so / shaderc_shared.dll, which carries glslang"
  echo "and SPIRV-Tools inside it) and the Vulkan Memory Allocator (MIT: libvma.so / vma.dll)."
  echo "It links the Vortice.Vulkan, Vortice.ShaderCompiler and Vortice.VulkanMemoryAllocator"
  echo "bindings (MIT). Their notices follow in full."
  echo
  echo "----- NWNCX:EE (MIT) -----"; echo
  cat "$here/LICENSE"; echo
  echo "----- funchook (https://github.com/kubo/funchook) -----"; echo
  cat "$here/third_party/funchook/LICENSE"; echo
  echo "----- diStorm3 (https://github.com/gdabah/distorm) -----"; echo
  cat "$here/third_party/funchook/distorm/COPYING"; echo
  echo "----- xatlas (https://github.com/jpcy/xatlas) -----"; echo
  cat "$here/native/xatlas/LICENSE"; echo
  echo "----- SMAA (https://github.com/iryoku/smaa) -----"; echo
  cat "$here/third_party/smaa/LICENSE.txt"; echo
  echo "----- DejaVu fonts (https://dejavu-fonts.github.io) -----"; echo
  cat "$here/third_party/dejavu/LICENSE.txt"; echo
  echo "----- OpenAL Soft (https://github.com/kcat/openal-soft) -----"; echo
  cat "$here/build/deps/openal-soft-1.25.2/COPYING"; echo
  echo "OpenAL Soft carries these inside it:"; echo
  cat "$here/build/deps/openal-soft-1.25.2/LICENSE-pffft"; echo
  cat "$here/build/deps/openal-soft-1.25.2/BSD-3Clause"; echo
  cat "$here/build/deps/openal-soft-1.25.2/fmt-11.2.0/LICENSE"; echo
  echo "----- Steam Audio (https://github.com/ValveSoftware/steam-audio) -----"; echo
  cat "$here/third_party/steamaudio/LICENSE.md"; echo
  cat "$here/third_party/steamaudio/THIRDPARTY.md"; echo
  echo "----- Vortice.Vulkan (https://github.com/amerkoleci/Vortice.Vulkan) -----"; echo
  cat "$here/third_party/vortice/LICENSE.txt"; echo
  echo "----- shaderc (https://github.com/google/shaderc) -----"; echo
  cat "$here/third_party/shaderc/LICENSE.txt"; echo
  echo "shaderc carries these inside it:"; echo
  echo "glslang (https://github.com/KhronosGroup/glslang):"; echo
  cat "$here/third_party/shaderc/LICENSE-glslang.txt"; echo
  echo "SPIRV-Tools (https://github.com/KhronosGroup/SPIRV-Tools):"; echo
  cat "$here/third_party/shaderc/LICENSE-spirv-tools.txt"; echo
  echo "SPIRV-Headers (https://github.com/KhronosGroup/SPIRV-Headers):"; echo
  cat "$here/third_party/shaderc/LICENSE-spirv-headers.txt"; echo
  echo "----- Vulkan Memory Allocator (https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator) -----"; echo
  cat "$here/third_party/vma/LICENSE.txt"
} > "$1"
