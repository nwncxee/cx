# Reverse engineering the client


Two tools, two jobs:

- **`./sym.sh` (nm)** -- fast path for *addresses and signatures*. "What's the
  symbol? Does it exist? What's its ABI?" This is enough for most hooks.
- **Ghidra** -- for *logic and layout*: decompiled function bodies, struct field
  offsets, call graphs, xrefs. Reach for it when you need to know what a function
  *does* or where a field sits in a struct (raw-pointer reads).

The client is already imported and analyzed:

- Project: `$GHIDRA_PROJECT/nwn-linux.rep`, one project holding `nwmain-linux`, `nwmain.exe` and `nwserver-linux`
- GUI: `ghidra`. Headless: `tools/ghidra/headless.sh <program> <Script.java> [args]` -- read-only, picks the
  project from the program, queues on a lock; the scripts in `tools/ghidra/` say their args on line 2
  (`DecompOne.java out.c Name @addr` decompiles named functions).
- **Read-only dump:** a full dump from `tools/ghidra/decomp-all.sh` (hours) holds every function
  (`src/<Class>.c`), `symbols.tsv`, `callgraph.tsv` and a README; addresses are Ghidra's (nm + 0x100000).
  Grep it before opening Ghidra. `ls $NWN_DECOMP` first: a `-targeted` folder is ad hoc output with
  no `src/`, so decompile what you need with `DecompOne.java` instead. Never committed (Beamdog's code).
  The binary + the Ghidra project stay the source of truth. State uncertainty from decompilation honestly; confirm against
  runtime behavior when it matters.
- **Stock shaders:** the client runs the CURRENT set, `data/ovr.bif` (`nwn_retail.key`, the same files as
  github.com/Finaldeath/NWNEEGameData/shaders), over the stale `data/base_shaders.bif` (`nwn_base.key`, which answers
  only for what ovr.bif lacks). Transcribe and cite the current files; `tools/content_stage.py` reads both in that order.
