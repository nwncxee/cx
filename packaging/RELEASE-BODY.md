NWNCX:EE (CX) @VERSION@, a free add-on for the Neverwinter Nights: Enhanced Edition client. See [what you get](https://github.com/nwncxee/cx#what-you-get).

**Windows** (`cx-windows-x64-@VERSION@.zip`): unpack into `bin\win32\` beside nwmain.exe and start the game.

**Linux** (`cx-linux-x64-@VERSION@.zip`): unpack into `bin/linux-x86/` beside nwmain-linux, then set the Steam launch option to exactly `./cx-launch %command%`. Not on Steam: run `./cx-launch`.

**Did it work?** The main menu shows a line of credits in the top-left corner. Type `/settings` in chat, or click the gear on the main menu, to set things up.

**Uninstall**: Windows, delete `version.dll`, `cx_openal.dll` and `phonon.dll`. Linux, clear the launch option.

**Verify**: `SHA256SUMS.sig` signs `SHA256SUMS` with the key in the repository's `packaging/allowed_signers`, and a build attestation proves GitHub built each zip from this tag. Steps in the README.
