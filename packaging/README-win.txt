NWNCX:EE (CX) -- client extender for Windows -- @VERSION@, for client build(s) @BUILDS@

Thanks for trying CX! It's an optional add-on for the Neverwinter Nights: Enhanced Edition client.
Run the stock client and you play vanilla.

INSTALL
  1. Unpack this archive into the folder holding nwmain.exe:
       Steam: <Steam>\steamapps\common\Neverwinter Nights\bin\win32\
       GOG:   <GOG Games>\Neverwinter Nights Enhanced Edition\bin\win32\
  2. Start the game as usual (Steam, GOG Galaxy or nwmain.exe). No launch options needed.

  The game loads version.dll. Beside it are the runtime libraries CX uses: cx_openal.dll (OpenAL
  Soft) and phonon.dll (Steam Audio) for sound, shaderc_shared.dll and vma.dll for the renderer.
  None of the game's own files are touched.

  Some antivirus (Defender, Kaspersky) flags version.dll as an injector. Check the zip against SHA256SUMS on the release page, then add version.dll to your
  antivirus exclusions. When in doubt, please contact xtul_ on Discord.

DID IT WORK?
  The main menu shows a line of credits in the top-left corner.

FIRST STEPS
  Type /settings in chat, or click the gear in the lower-left corner of the main menu, to switch
  features on and off. Each setting's help line says what turning it off does.

UNINSTALL
  Delete version.dll, cx_openal.dll, phonon.dll, shaderc_shared.dll and vma.dll from
  bin\win32\. README.txt and LICENSES.txt can go too.

REPORTING A PROBLEM
  CX is silent by default. To get a log, create a file named cx.cfg next to nwn.ini (usually
  Documents\Neverwinter Nights\) holding these two lines:

     general.logs=1
     general.hookdebug=1

  Play again, then open an issue at https://github.com/nwncxee/cx/issues with cx.log and
  funchook.log from Documents\Neverwinter Nights\, and what you did right before things went wrong.
