# Testing standards

How testing is done in this repo. Every testing skill reads this file first, and it wins over their defaults. A line left as `ask` means the agent asks the user before it acts on it.

Test surface: the stub host that `./run-demo.sh` boots, with no GPU and no engine, for every self-check. The real client, run headless by `tools/live/run-live.sh` from a worktree's staged `cx-linux-x64/`, for anything only the game can show. The user's own client, saves and settings are never a test surface
Boot command: `./build.sh`, then `./run-demo.sh`. It worked when it ends with `>> <n> SelfCheck lines, none failed` and lists any SKIPPED suite. `tools/live/run-live.sh` boots the real client and exits 0 for PASS, 1 for FAIL and 2 for a setup problem
Test props: fixtures a self-check needs are embedded beside it under `managed/`. A throwaway script or capture goes in `.scratch/`, which git ignores
Never holds test props: `managed/` outside a self-check's own fixtures, `media/`, and the staged `cx-linux-x64/` and `cx-windows-x64/` folders, which ship
Commit test cruft: no
Test kinds: self-checks, `SelfCheck` methods run by `./run-demo.sh` in the stub host and again at client boot. Gates, `tools/gates/run.sh`, which `build.sh` runs too. Approved examples and regression tests in shell, `tools/approved-examples.sh`, which `build.sh` runs. Live, `tools/live/run-live.sh` (`--ladder` for the scripted ladder, `--stock` for a client without CX), one client at a time; its limits are in `docs/live-harness.md`. Windows, `./run-proton.sh` and `tools/live/run-proton-live.sh` under Proton
Validation: the Vulkan validation passes, `tools/vk/validate.sh` -- synchronization validation and GPU-assisted validation over the goldens, one GPU at a time. "No findings" and "the layer never ran" read alike, so each pass has its positive controls: `tools/vk/report.py` holds the log to the layer's own Active notice and the feature the pass asked for, and `tools/vk/mutants.sh` changes one line of the transfer or bindless path at a time to show the pass still sees a real fault. Both are by hand, outside CI; their runbook is `docs/live-harness.md`
Mutation: by hand, no tool reads `SelfCheck` methods. Make one mutant at a time in the changed lines (a flipped comparison, a deleted call, a changed constant), run `./run-demo.sh`, which rebuilds, and count the mutant killed when a self-check fails. A gate script is mutated the same way and run with `tools/gates/run.sh`. Revert each mutant before the next. The one scripted sweep is `tools/vk/mutants.sh`, whose mutants are a fixed list of lines and whose oracle is a validation pass, not a self-check
Live checks: 1. Run `./build.sh` in your worktree. 2. Run `tools/live/run-live.sh` with the `--module`, `--cfg` and `--keys` the change needs. 3. Read the PASS or FAIL line, `cx.log` and the screenshots it saves. 4. For a look or a feel, start the real client with `cx-launch` and check by hand. The `over-to-you` skill hands that step to a person as a checklist

## Self-checks

A self-check's `Expect` must be able to fail. `tools/gates/selfcheck.sh` refuses the shapes that cannot (see `CLAUDE.md`, Gates).

**The independent-oracle rule.** A self-check's expected value comes from an independent oracle: a
hand-written literal, golden data or a fixture, or a second artifact (the embedded GLSL text, a build
`.tsv`, engine bytes). Never from a call to the code under test, a copy of it, or the declaration it
restates -- an assertion that cannot fail is worse than none, because it adds to the "passed" count and
suggests coverage that does not exist. A guard that can skip assertions `Expect`s its precondition first;
a suite that cannot run on this host calls `Checker.Skip`, which logs `SKIPPED` and counts it, and never
goes on to log "passed". A self-check Claude writes names its oracle in the check's `<summary>`.

No test reads source text. A rule a test would grep for is a ban line in `managed/BannedSymbols.txt` or an analyzer rule in `tools/analyzers/` instead.

## Approved examples

A test built from approved Given / When / Then examples carries each example's lines as comments above it. The guard locks only those lines, so the code under them can change without a new sign-off.
