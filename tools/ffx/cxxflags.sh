# The compile flags of every FSR 3.1 object (sourced by build.sh, and by tools/selfcheck/run.sh for the one it
# rebuilds with its API visible). A 4-byte wchar_t outgrows the SDK's default context size; FFX_GCC drops its dllexport.
F=build/ffx/sdk
FFX_CXXFLAGS=(-std=c++17 -O2 -fPIC -fno-exceptions -fno-rtti -w -DNDEBUG -include native/ffx/compat.h -DFFX_GCC
  '-DFFX_SDK_DEFAULT_CONTEXT_SIZE=(1024 * 256)' -DFFX_FSR3UPSCALER -DFFX_DOF -Ibuild/deps/Vulkan-Headers-vulkan-sdk-1.4.363.0/include
  -I$F/include -I$F/include/FidelityFX/host -I$F/src/shared -I$F/src/backends/shared -I$F/src/components -Ibuild/ffx/shaders)
