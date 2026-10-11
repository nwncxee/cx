NWNCX:EE (CX) -- client extender for Linux -- @VERSION@

Thanks for trying CX! It's an optional add-on for the Neverwinter Nights:
Enhanced Edition client. Run the stock client and you play vanilla.


INSTALL
  1. Find the game folder.
       Steam: right-click "Neverwinter Nights" -> Manage -> Browse local files.
       GOG:   wherever you unpacked the game.
     Open "bin/linux-x86" inside it. It holds nwmain-linux.
  2. Unpack this archive into that folder, beside nwmain-linux.
  3. Steam: right-click "Neverwinter Nights" -> Properties -> General ->
     Launch Options, and paste this line exactly as written:

       ./cx-launch %command%

     Not on Steam: run ./cx-launch from bin/linux-x86. With no arguments it
     starts the nwmain-linux beside it.
  4. Start the game.

  The launch line is the same for everyone. Steam starts the game from
  bin/linux-x86, so the relative path always works.

DID IT WORK?
  The main menu shows a line of credits in the top-left corner.

FIRST STEPS
  Type /settings in chat, or click the gear in the lower-left corner of the
  main menu, to switch features on and off. Each setting's help line says
  what turning it off does.

UNINSTALL
  Clear the Launch Options line. The files do nothing without it. To remove
  them too, delete cx-launch, libcx.so, cx_managed.so, libopenal.so.1,
  libphonon.so, README.txt and LICENSES.txt from bin/linux-x86.

REPORTING A PROBLEM
  CX is silent by default. To get a log, create a file named cx.cfg next to
  nwn.ini (usually ~/.local/share/Neverwinter Nights/) holding one line:

     general.logs=1

  Play again, then open an issue at https://github.com/nwncxee/cx/issues
  with ~/cx.log and what you did right before things went wrong.
