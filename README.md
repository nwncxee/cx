![NWNCX:EE: Neverwinter Nights: Enhanced Edition Client Extender](media/banner.webp)

# NWNCX:EE (CX)

CX is an optional add-on for the Neverwinter Nights: Enhanced Edition client. It adds features to the
game client (a modern renderer, a new chatbox, hotkeys, nameplates, combat text, positional audio and
more). It is a client mod: it changes nothing on the server and needs nothing from it.

See it in the game in the [gallery](media/README.md).

Run the stock client and you play vanilla. Uninstalling is deleting a few files.

CX is not affiliated with or endorsed by Beamdog. It loads into the game process, so only install a
build you got from the releases page or built yourself.

## Install

Download the zip for your system from the
[releases page](https://github.com/nwncxee/cx/releases).

### Windows

1. Unpack `cx-windows-x64-<version>.zip` into the folder that holds `nwmain.exe`:
   - Steam: `<Steam>\steamapps\common\Neverwinter Nights\bin\win32\`
   - GOG: `<GOG Games>\Neverwinter Nights Enhanced Edition\bin\win32\`
2. Start the game as usual. No launch options needed.

To uninstall, delete `version.dll`, `cx_openal.dll` and `phonon.dll` from `bin\win32\`.

Windows Defender sometimes quarantines `version.dll`. That is a false positive. Add it to Defender's
exclusions.

### Linux

1. Find the game folder (Steam: right-click *Neverwinter Nights* -> Manage -> Browse local files)
   and open `bin/linux-x86/`, the folder that holds `nwmain-linux`.
2. Unpack `cx-linux-x64-<version>.zip` there.
3. Steam: right-click *Neverwinter Nights* -> Properties -> General -> Launch Options, and paste
   exactly:

   ```
   ./cx-launch %command%
   ```

   Not on Steam: run `./cx-launch` from `bin/linux-x86/`.

To uninstall, clear the launch option.

### macOS

Not supported yet.

### Did it work?

The main menu shows an attribution line in the top-left corner.

### Checking a download is genuine

Only download CX from the releases page above. Each release carries `SHA256SUMS` and
`SHA256SUMS.sig`, a signature made with the maintainer's key. A copy of CX from anywhere else can
match its own checksums, but it cannot carry that signature. The public key is
[`packaging/allowed_signers`](packaging/allowed_signers). Fetch it from this repository, not from
the site you downloaded the zip from.

With the zip, `SHA256SUMS`, `SHA256SUMS.sig` and `allowed_signers` in one folder:

```sh
ssh-keygen -Y verify -f allowed_signers -I cx-releases -n file -s SHA256SUMS.sig < SHA256SUMS
sha256sum -c --ignore-missing SHA256SUMS
```

The first line must print `Good "file" signature for cx-releases`, and the second must print `OK` for
your zip. On Windows, run the first line in PowerShell (OpenSSH ships with Windows 10 and 11), then
compare the output of `Get-FileHash cx-windows-x64-<version>.zip` with the zip's line in `SHA256SUMS`.

Releases after v0.9.1 also carry a build attestation, which proves this repository's release workflow
built the zip on GitHub from the tag. With the [GitHub CLI](https://cli.github.com/) installed:

```sh
gh attestation verify cx-linux-x64-<version>.zip -R nwncxee/cx \
  --signer-workflow nwncxee/cx/.github/workflows/release.yml --source-ref refs/tags/<version>
```

It must print `Verification succeeded!`.

## Build from source

On Linux, `nix develop` gives you the pinned toolchain from `flake.nix`, the same one CI uses:

```sh
git clone --recursive https://github.com/nwncxee/cx.git
cd cx
nix develop
./build.sh       # -> cx-linux-x64/ and its zip
./run-demo.sh    # boots the framework without the game
```

Without Nix, install the .NET 10 SDK, clang 21, gcc 15, ld.lld, zlib, cmake, pkg-config and the
PipeWire, PulseAudio and ALSA headers yourself. `flake.nix` has the full list.

The Windows build is cross-compiled on Linux with `./build-win.sh`. `CLAUDE.md` covers the
architecture and how to add a feature.

## Contributing

Fork the repository and open a pull request against `main`. CI doesn't run on a pull request from a
fork until a maintainer approves it.

## Reporting a problem

CX is silent by default. To get a log, create `cx.cfg` next to `nwn.ini` (Windows:
`Documents\Neverwinter Nights\`, Linux: `~/.local/share/Neverwinter Nights/`) with the line

```
general.logs=1
```

Play again, then open an [issue](https://github.com/nwncxee/cx/issues)
with `cx.log` (next to `cx.cfg` on Windows, `~/cx.log` on Linux) and what you did just before
things went wrong.

## Contact

Michał Piasecki: mpiasecki720@protonmail.com

## License

MIT; see `LICENSE`. Third-party notices ship in each release as `LICENSES.txt`.
