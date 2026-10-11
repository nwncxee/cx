# mdlcheck -- CX's MDL parser, off the device

Compiles `managed/Alloy/Mdl/*.cs` (the engine-free parser) into a plain `dotnet` console app and runs it over
the real corpora. No client, no GL, no engine.

    dotnet run -c Release --project tools/mdlcheck            # all five modes
    dotnet run -c Release --project tools/mdlcheck -- --selfcheck   # the embedded fixtures only
    dotnet run -c Release --project tools/mdlcheck -- oracle -v     # one mode, per-model lines
    dotnet run -c Release --project tools/mdlcheck -- dump --only tdm01_a01_01   # one pair, side by side

Exit 0 is a pass. Modes:

- `bifs` -- every `.mdl` in the base game's containers (`data/nwn_base.key`), read with the tool's own
  KEY/BIF reader (`KeyBif.cs`; `managed/Alloy/ResLocate.cs` cannot be reused -- it asks the live
  `CExoResMan`). Reports models, failures, nodes, vertices and throughput.
- `ascii` -- every `.mdl` under a content corpus (`CONTENT_MODELS`, see `local.env.example`), binary files in it included.
- `golden` -- one engine-compiled payload checked in under `fixtures/`, parsed and asserted against recorded
  values: a regression lock on the binary offsets that needs no corpus at all.
- `fuzz` -- truncations and bit flips of real models (both corpora), demanding a refusal rather than a throw,
  a hang or an allocation past 192 MiB. Exit non-zero on any of the three.
- `oracle` -- the real test. For each engine-compiled binary in
  `~/.local/share/Neverwinter Nights/cx-models/` (what `managed/Alloy/MdlCache.cs` keeps), find its ASCII
  source by resref, re-prove the pairing with `MdlCache`'s own content hash, parse BOTH with CX and compare
  node for node: kinds, names, hierarchy, node numbers, controllers, vertices, faces, UVs, normals, tangents,
  adjacency, bounds and surface area. Supermodel chains are resolved from the corpus and then the containers,
  so node numbers are comparable; `.mtr` render hints are resolved from the corpus, because the engine's
  tangent pass reads them.

Paths are overridable: `--key`, `--ascii`, `--cx`. `--only <text>` narrows to matching resrefs, `--limit N`
caps the count, `-v` adds per-item lines and `-v -v` prints every mismatch example.

The oracle needs a `cx-models` cache produced against the SAME content tree the `--ascii` corpus holds --
the engine's payload is keyed on the source bytes, so a cache from a different hak set pairs with nothing.
A fresh cache comes from playing (or from `tools/live/run-live.sh`, which writes into
the harness's throwaway user directory; point `--cx` at it).

The corpora are not in the repo and never should be: the BIFs are Beamdog's, `cx-models` is the engine's own
output on this machine, and the content corpus is the server's.
