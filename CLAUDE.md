# CLAUDE.md

**NWNCX:EE** (**CX** for short): an optional, drop-in **client** extender for NWN:EE, feature logic in C# (NativeAOT)
behind a thin native bootstrap. This is the client (`nwmain`), never the server: [`DESIGN.md`](DESIGN.md) says why.

The Vulkan renderer is called **Alloy** (`managed/Alloy/`, namespace
`CX.Alloy`) -- one of the family: Anvil on the server backend, Crucible the
module builder, Alloy the renderer. "Alloy" is the term for what it draws, and
**vanilla** is the contrast: an Alloy part is one Alloy submits, a vanilla one is
one the engine draws. The product is **NWNCX:EE** (full name) / **CX** (shorthand):
`cx.cfg`, `cx.log`, `cx-launch`, `cx_managed.so`, and the `cx_*.shd` stage names,
which are the override contract with content (a hak or NWSync), and the C# namespaces are
`CX.*`. The `CX*`/`CX_*` identifiers in C# and GLSL are internal glue -- not
user-visible, not renamed (#530).

## Where things are

Read the file for the area before working in it.

| File | Holds |
| --- | --- |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | The load path, adding a service, engine memory reads, the binary, the server side, status |
| [`DESIGN.md`](DESIGN.md) | Optional and client-only by design, the tuning knobs |
| [`BUILDING.md`](BUILDING.md) | Build and run in full: CI, the staged runtime, `where.sh`, the Windows leg, version-bound numbers |
| [`CODING_STANDARDS.md`](CODING_STANDARDS.md) | The coding rules, the comment rule and the file-size cap |
| [`TESTING_STANDARDS.md`](TESTING_STANDARDS.md) | How a change is tested |
| [`tools/gates/README.md`](tools/gates/README.md) | Every gate and the rule it refuses |
| [`docs/reverse-engineering.md`](docs/reverse-engineering.md) | `sym.sh` and Ghidra |

## Agent skills

- Tickets: internal work in Plane, project `CX`, through `bin/plane`; user reports arrive on GitHub Issues and Discord. See `docs/agents/issue-tracker.md`.
- Documentation: the repo's GitHub wiki. See `docs/agents/domain.md`.
- Domain: single-context, one `GLOSSARY.md` and one `docs/adr/` at the root. See `docs/agents/domain.md`.
- Testing: `TESTING_STANDARDS.md`. See `docs/agents/testing.md`.

## Build & run

    nix develop         # flake.nix: the pinned toolchain (.#ci is what CI builds in, .#gates the gate jobs)
    ./build.sh          # enters `nix develop` itself where nix exists; sizes the AOT threads to free memory. Else needs: .NET 10 SDK, clang 21, gcc 15, ld.lld, zlib, cmake, pkg-config, audio headers, glslang, ssh-keygen
    ./run-demo.sh       # boots the framework in a stub host + proves the rot refusal (no game needed)
    tools/selfcheck/run.sh  # the same self-checks from an IL compile in a JIT host, inside a 2G cap; adds to
                            # run-demo.sh, never replaces it. tools/selfcheck/mutants.py: hand mutants, one compile
    tools/stryker.sh        # Stryker.NET over the core lines changed since the merge-base; by hand, never a CI gate
    ./sym.sh [-m] <pat> # search client symbols; -m = mangled (for Install)
    tools/live/run-live.sh  # boots the REAL client headless into a playable area and grades the session
                            # PASS/FAIL + screenshots; nothing reaches the desktop, nothing of the user's
                            # is touched. Runbook + limits: docs/live-harness.md

    nix develop .#win -c ./build-win.sh  # the Windows build, cross-built (about a minute): run it for a new hook or offset

Every report of work in a worktree or a branch the user has not checked out ends with the output of
`tools/where.sh <checkout>`. Rebuild first when it says anything but CURRENT.

Per-machine paths, CI, the staged runtime and the Windows leg: [`BUILDING.md`](BUILDING.md).

## Coding standards (ADR 0031)

`CODING_STANDARDS.md` holds the coding rules: what fails the build, how a place in memory is named, threads and
the host. A rule the compiler can check is a line in `managed/BannedSymbols.txt` (RS0030) or a rule in
`tools/analyzers/` (CX0001 and on), and `managed/.editorconfig` makes both errors. Add a ban the day its helper
exists, and give every "should have gone through X" review finding its ban in the change that fixes it. The
`standards-reviewer` agent (`.claude/agents/`) reviews a diff against the standards.

- Read engine fields through the typed views in `CX.Engine`, never `*(T*)(ptr + EngineLayout.X)`: [`ARCHITECTURE.md`](ARCHITECTURE.md#engine-memory-reads).
- Every declaration that needs explaining gets a one-to-three-line XML `<summary>`, and a method body has no comment but a footgun line: [`CODING_STANDARDS.md`](CODING_STANDARDS.md#comments-and-file-size).
- A setting is one `Knob<T>` declaration, never a `CX_*` environment variable: [`DESIGN.md`](DESIGN.md#tuning-knobs-adr-0025-amending-0013).
- New mutable state goes in its lifetime owner, a `static` field holding a `...State` class: [`ARCHITECTURE.md`](ARCHITECTURE.md#adding-a-service).

## Gates (CX-192)

A rule counts once a check refuses it. Each gate is `tools/gates/<rule>.sh`, run by `tools/gates/run.sh` from
`build.sh`, CI and a Claude Code `PostToolUse` hook (`.claude/settings.json`), so a violation shows the moment
it is written. The gates run side by side; the hook passes the edited file (`run.sh --for <path>`), which keeps
the gates whose first `# reaches: <globs>` line matches it, and a gate with no such line reaches every path.
`run.sh --list` names the gates it would run. An unfinished cleanup is a `<rule>.baseline` ratchet (`tools/gates/ratchet.sh`): it may only
shrink, and a row naming a file that is no longer in the tree fails -- an allowance nothing can spend hides
the cleanup that earned it. Adding a rule means adding its gate in the same change, and one line in `tools/gates/README.md`.

## Hard rules

- **No decompiled code in the repository, ever.** Read the dump on your own
  machine; write what it shows as prose, symbol names, addresses and constants. A quoted listing,
  pseudo-C or Ghidra's `param_N`/`iVarN` names in a doc or a comment is the violation, and
  `tools/no-decompiled.sh` refuses the usual shapes in CI.
- **Research lives on its work item**: post the findings as a comment on the `CX` item it
  answers, or file one titled "Research: ..." and close it. Never a `docs/` note, never a branch.
- **A retired name goes on the retired list in the same change.** Retiring a pipeline stage, a mode or a
  knob means adding its name to `tools/gates/retired-terms.txt` (with the ticket that retired it) in that
  change, not as a follow-up -- and removing it means removing its tools, comments and messages too, so no
  log line, help text or doc comment still describes something that is gone.
- **NEVER GUESS.** Always verify with a Ghidra decompilation.
- **Hook with funchook, NOT NativeHookRegistry.** The server's hook registry does not exist
  here. Take its Before/After/SkipOriginal semantics, not its C# API.
- **Hot hooks allocate nothing.** NativeAOT GC runs on whatever thread the hook
  fired on; the render/main loop stutters if it pauses there. Input/network hooks
  are cold -- fine. Frame, render and GL-stub hooks are hot -- be careful.
- **Reuse an existing message opcode; don't invent one.** That is what keeps the
  extender optional (unknown packets are ignored server-side).
- **Windows is a supported OS, not a follow-up.** A new hook or engine-memory read
  ships with its Windows resolution or it is not PR-ready: a symbol gets its `_win`
  entry (`Nm` for a name export, `At()` plus an `rva` line in the build file for an
  anchored RVA), an offset gets a `Field` row in `EngineLayout` and an `offset` line with
  both values in the build file (`managed/Framework/Builds/`), and **a call that returns a class by value
  gets its `sret` argument order** -- Itanium passes `(sret, this, ...)`, Win64 passes
  `(this, sret, ...)`, so one order declared for both hands the engine your stack frame
  as `this` (#456). `tools/abi/check-win-symbols.py` finds these; `build-win.sh` runs it. Deliberately Linux-only work is allowed
  and says so on its own line -- what is forbidden is the silent absence, where
  "never looked" and "decided" are the same missing key.
- **Every bug report is checked on Windows too.** Diagnosing a user report means reading the Windows
  path as well (`nwmain.exe` in the Ghidra project, the `_win` symbols, the build file's Windows column): `nwmain.exe` has
  called a different function, inlined the one CX hooks or taken the other `sret` order, and a feature that
  worked on Linux did nothing there. The findings say what Windows does, or say `unverified` with what was tried.
- **A pass holds the one program field it draws with and no diagnostic-only one.** `/alloy bench` steps knobs,
  never a pass's program: a measurement that needs its own variant of a stage does not get a field on the pass.
- **Shaders for what shaders reach; extender for what they can't.** Lighting/color
  = `.shd` overrides via hak/NWSync (content, not this repo). Shadows, render bugs,
  draw state = engine-internal: an Alloy pass or a hook, never content.
