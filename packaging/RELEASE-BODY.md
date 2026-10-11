NWNCX:EE (CX) @VERSION@. Optional add-on for Neverwinter Nights: Enhanced Edition.

**Windows** (`cx-windows-x64-@VERSION@.zip`): unpack into `bin\win32\` beside nwmain.exe and start the game.

**Linux** (`cx-linux-x64-@VERSION@.zip`): unpack into `bin/linux-x86/` beside nwmain-linux, then set the Steam launch option to exactly `./cx-launch %command%`. Not on Steam: run `./cx-launch`.

**Uninstall**: Windows, delete version.dll. Linux, clear the launch option.

It worked if you see an attribution text in top left corner in the main menu.

**Verify**: `SHA256SUMS.sig` signs `SHA256SUMS` with the key in the repository's `packaging/allowed_signers`, and `gh attestation verify <zip> -R nwncxee/cx` proves GitHub built the zip from this tag. Steps in the README.
