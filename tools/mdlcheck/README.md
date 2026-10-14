# mdlcheck -- CX's MDL parser, off the device

Compiles `managed/Alloy/Mdl/*.cs` (the engine-free parser) into a plain `dotnet` console app and runs it over
the real corpora. No client, no GL, no engine.

    dotnet run -c Release --project tools/mdlcheck            # golden, bifs, ascii, fuzz
    dotnet run -c Release --project tools/mdlcheck -- --selfcheck   # the embedded fixtures only
    dotnet run -c Release --project tools/mdlcheck -- ascii -v      # one mode, per-model lines

Exit 0 is a pass. Modes:

- `bifs` -- every `.mdl` in the base game's containers (`data/nwn_base.key`), read with the tool's own
  KEY/BIF reader (`KeyBif.cs`; `managed/Alloy/ResLocate.cs` cannot be reused -- it asks the live
  `CExoResMan`). Reports models, failures, nodes, vertices and throughput.
- `ascii` -- every `.mdl` under a content corpus (`CONTENT_MODELS`, see `local.env.example`), binary files in it included.
- `golden` -- one engine-compiled payload checked in under `fixtures/`, parsed and asserted against recorded
  values: a regression lock on the binary offsets that needs no corpus at all.
- `fuzz` -- truncations and bit flips of real models (both corpora), demanding a refusal rather than a throw,
  a hang or an allocation past 192 MiB. Exit non-zero on any of the three.

Paths are overridable: `--key`, `--ascii`. `--only <text>` narrows to matching resrefs, `--limit N`
caps the count, `-v` adds per-item lines and `-v -v` per-node detail.

The corpora are not in the repo and never should be: the BIFs are Beamdog's and the content corpus is the server's.
