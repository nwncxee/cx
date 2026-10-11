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

      shellEnv = {
        OPENAL_LIB = "${openal}/lib/libopenal.so.1";
        DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";
        DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        DOTNET_NOLOGO = "1";
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
      };
    in
    assert pinned;
    {
      packages.${system}.openal = openal;

      devShells.${system} = {
        gates = pkgs.mkShellNoCC { packages = gatesPackages; };

        ci = pkgs.mkShell (
          shellEnv
          // {
            packages = [ pythonBase ] ++ ciPackages;
            buildInputs = ciLibraries;
          }
        );

        default = pkgs.mkShell (
          shellEnv
          // {
            packages = [ pythonDev ] ++ ciPackages ++ devPackages;
            buildInputs = ciLibraries;
          }
        );
      };

      formatter.${system} = pkgs.nixfmt;
    };
}
