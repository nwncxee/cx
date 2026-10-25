{
  description = "NWNCX:EE build, CI and dev toolchain";

  # flake.lock pins the exact revision. The asserts below fail evaluation when a lock bump moves a
  # compiler or runtime off the major version the build scripts expect.
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs =
    { nixpkgs, ... }:
    let
      system = "x86_64-linux";
      pkgs = nixpkgs.legacyPackages.${system};
      inherit (pkgs) lib;
      llvm = pkgs.llvmPackages_21;

      # census.py imports compression.zstd, which is new in Python 3.14.
      pythonBase = pkgs.python314;
      pythonDev = pythonBase.withPackages (ps: [
        ps.xlib
        ps.numpy
        ps.pillow
      ]);

      expect =
        what: want: have:
        lib.assertMsg (lib.versions.major have == want || lib.versions.majorMinor have == want)
          "${what} is ${have}, the build expects ${want}. Fix the pin in flake.lock or update the build first.";

      pinned =
        assert expect ".NET SDK" "10" pkgs.dotnet-sdk_10.version;
        # cc and c++ are clang. build.sh calls gcc by name for OpenAL Soft, which clang 21 fails to build
        # against libstdc++ 15.
        assert expect "gcc" "15" pkgs.stdenv.cc.cc.version;
        assert expect "clang" "21" llvm.clang.version;
        assert expect "Python" "3.14" pythonBase.version;
        true;

      # tools/gates/run.sh and tools/no-decompiled*.sh
      gatesPackages = [
        pythonBase
        pkgs.git
        pkgs.actionlint
        pkgs.shellcheck
        pkgs.zizmor
      ];

      # build.sh and run-demo.sh. The libraries go in buildInputs so pkg-config and CMake find
      # OpenAL Soft's PipeWire, PulseAudio and ALSA backends, and the run-demo stub links sqlite and zstd.
      ciPackages = [
        pkgs.dotnet-sdk_10
        llvm.clang
        llvm.lld
        pkgs.cmake
        pkgs.gnumake
        pkgs.pkg-config
        pkgs.git
        # build.sh runs tools/gates/run.sh, and its workflows gate needs all three.
        pkgs.actionlint
        pkgs.shellcheck
        pkgs.zizmor
      ];
      ciLibraries = [
        pkgs.zlib
        pkgs.pipewire
        pkgs.libpulseaudio
        pkgs.alsa-lib
        pkgs.sqlite
        pkgs.zstd
      ];

      # build-win.sh (clang-cl comes with llvm.clang), check-shaders.py, check-win-symbols.py, the release
      # publish step and the tools/ scripts.
      devPackages = [
        llvm.llvm
        pkgs.glslang
        pkgs.spirv-tools
        pkgs.jq
        pkgs.curl
        pkgs.zip
        pkgs.unzip
        pkgs.file
        pkgs.sqlite
        pkgs.zstd
      ];

      # CX-320: the Vulkan side of the toolchain, dev shells only -- none of it reaches cx-linux-x64/ or the
      # zip. The headers are for a future native translation unit. The LOADER and vulkaninfo are the
      # machine's, deliberately: a nixpkgs loader dlopens the ICD's driver library by soname and nix's glibc
      # does not read /etc/ld.so.cache, so on a foreign distro it finds the ICD JSON and then no driver
      # behind it (measured here: ERROR_INCOMPATIBLE_DRIVER, where /usr/bin/vulkaninfo reports 1.4.363).
      vulkanPackages = [
        pkgs.vulkan-headers
      ];

      pins = lib.importJSON ./tools/deps.json;

      # build.sh's libopenal.so.1, built by the same script from the same pinned source. Nix keeps it in the
      # store, so it compiles once per pin and toolchain. CI pulls it from the binary cache, which a
      # maintainer refills by hand (docs/binary-cache.md). No fixup: the file stays byte-for-byte what
      # tools/build-openal.sh links.
      openal = pkgs.stdenv.mkDerivation {
        pname = "cx-openal-soft";
        version = lib.head (builtins.match "openal-soft-(.*)\\.tar\\.gz" pins.openal-src.file);
        src = pkgs.fetchurl { inherit (pins.openal-src) url sha256; };
        nativeBuildInputs = [
          pkgs.cmake
          pkgs.pkg-config
        ];
        buildInputs = [
          pkgs.pipewire
          pkgs.libpulseaudio
          pkgs.alsa-lib
        ];
        dontUseCmakeConfigure = true;
        buildPhase = "JOBS=$NIX_BUILD_CORES bash ${./tools/build-openal.sh} . build $out/lib/libopenal.so.1";
        dontInstall = true;
        dontFixup = true;
      };

      # build-win.sh's Windows 10 SDK and MSVC libraries, fetched from Microsoft by xwin and pinned by hash.
      # Building it accepts Microsoft's licence for whoever builds it, so it stays out of the binary cache.
      winSdkVersion = "10.0.19041";
      winMsvcVersion = "14.44.17.14";
      winsdk = pkgs.stdenvNoCC.mkDerivation {
        name = "cx-winsdk-${winSdkVersion}-msvc-${winMsvcVersion}";
        nativeBuildInputs = [ pkgs.xwin ];
        SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
        buildCommand = ''
          xwin --accept-license --cache-dir "$TMPDIR/xwin" --arch x86_64 --manifest-version 17 \
            --sdk-version ${winSdkVersion} --crt-version ${winMsvcVersion} \
            splat --copy --use-winsysroot-style --preserve-ms-arch-notation --output "$out"
        '';
        outputHashMode = "recursive";
        outputHashAlgo = "sha256";
        outputHash = "sha256-fA6dZyonH/Yydb1r9nFjhcTbSMICFTMLDjStI8/2vkc=";
      };

      shellEnv = {
        OPENAL_LIB = "${openal}/lib/libopenal.so.1";
        DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";
        DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        DOTNET_NOLOGO = "1";
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
      };

      devShell = shellEnv // {
        packages = [ pythonDev ] ++ ciPackages ++ devPackages ++ vulkanPackages;
        buildInputs = ciLibraries;
        # CX-320: the validation layers are a DEBUGGING tool, never a shipped one -- ADD, so the system
        # loader keeps whatever layers the box already has, and nothing is enabled until a run asks for it
        # (VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation, or the layer named in the instance's own list).
        # The machine's loader loads this nix-built layer fine; only the loader itself has to be the box's.
        VK_ADD_LAYER_PATH = "${pkgs.vulkan-validation-layers}/share/vulkan/explicit_layer.d";
      };
    in
    assert pinned;
    {
      packages.${system} = { inherit openal winsdk; };

      devShells.${system} = {
        gates = pkgs.mkShellNoCC { packages = gatesPackages; };

        ci = pkgs.mkShell (
          shellEnv
          // {
            packages = [ pythonBase ] ++ ciPackages;
            buildInputs = ciLibraries;
          }
        );

        default = pkgs.mkShell devShell;

        # The default shell plus the Windows SDK and MSVC tree build-win.sh needs.
        win = pkgs.mkShell (
          devShell
          // {
            SDK = "${winsdk}/Windows Kits/10";
            SDKVER = winSdkVersion;
            MSVC = "${winsdk}/VC/Tools/MSVC/${winMsvcVersion}";
          }
        );
      };

      formatter.${system} = pkgs.nixfmt;
    };
}
