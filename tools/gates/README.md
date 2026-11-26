# Gates


A rule counts once a check refuses it. Each gate is `tools/gates/<rule>.sh`, run by `tools/gates/run.sh` from
`build.sh`, CI and a Claude Code `PostToolUse` hook (`.claude/settings.json`), so a violation shows the moment
it is written. The gates run side by side; the hook passes the edited file (`run.sh --for <path>`), which keeps
the gates whose first `# reaches: <globs>` line matches it, and a gate with no such line reaches every path.
`run.sh --list` names the gates it would run. An unfinished cleanup is a `<rule>.baseline` ratchet (`tools/gates/ratchet.sh`): it may only
shrink, and a row naming a file that is no longer in the tree fails -- an allowance nothing can spend hides
the cleanup that earned it. Adding a rule means adding its gate in the same change, and one line below:


- `approved-lines.sh`: an approved-examples script under `tools/` with no Given, When and Then comment lines, in that order, fails;
  the lines are the example a test proves, so a script that lost them proves nothing a reader can name.
  Proven by: tools/gate_runner_regression.sh
- `comments.sh`: a `///` block over 4 text lines, a `///` line citing a ticket (`#n`, `CX-n`, `!n`, `issue n`, `ticket n`), or one marking a ceiling `shortcut:` where the word is `ponytail:`, under `managed/` or in `native/`'s own sources, fails. No baseline.
- `doc-stack.sh`: a `///` block holding two `<summary>` elements under `managed/` (a doc left behind when its declaration
  moved or went away) counts against `doc-stack.baseline`, which may only shrink; `python3 tools/gates/doc-stack.py -v` lists each.
- `file-size.sh`: a `.cs` file under `managed/` over 1,500 lines fails -- past the cap, split by
  responsibility the way `AlloyRenderer`'s partials are. `*.g.cs` and `SymbolMap.cs` (a flat symbol table
  that grows with every hook) are exempt and `file-size.baseline` holds what is still over. `EngineLayout` is
  one partial class over `EngineLayout*.cs`, and every tool that reads its rows reads all of them: a new block
  of rows goes in a part of its own once `EngineLayout.cs` would grow past its baseline.
- `retired-terms.sh`: a name listed in `tools/gates/retired-terms.txt` appearing under `managed/` or `docs/`, or in
  `check-shaders.py`, fails. Not scanned: that file, `docs/adr/` and `docs/handoff/` -- dated records of what was true then.
- `knob-refs.sh`: a `<group>.<knob>` key in a string literal under `managed/` naming no declared `Knob<T>`
  fails; a key `Settings.RetiredKeys` lists (cx.cfg drops it on read) is named on purpose and passes.
- `knob-spec-rows.sh`: a declared knob with no row in `docs/spec/unified-settings.md`, or a key the spec rows
  that nothing declares, fails. The spec is the settings tree's one description, so a knob with no row is a
  feature nobody can read about. A ChatOnly family may share one summary row (`group.a / b / c`), a
  `group.prefix<...>` row stands for the shape it spells (`chat.w<N>.*` covers `chat.w3.rect`, not a future
  `chat.wrap`) plus the bag key it is named after, and `<mode>.x` for all three camera modes;
  `python3 tools/gates/knob-spec-rows.py -v` prints the whole census.
- `knob-effect.sh`: a knob declared `Effect.Live` whose every reader takes `Boot`, or one declared
  `Effect.Restart` read through `V` outside the file it is declared in, fails. The declared effect is what the
  panel badge and the chat reply promise, so a reader on the wrong one is a promise nothing keeps. A read is
  attributed to its declaration by qualifier, class or file; a bare read of a field several classes declare
  (`Size.V`, `Fov.V`) names no one declaration and counts against `knob-effect.baseline`, which may only
  shrink. `python3 tools/gates/knob-effect.py -v` lists each such read.
- `engine-views.sh`: outside `Framework/`, `ptr +/- EngineLayout.X` fails when row X has a view member; a
  `not-an-offset:` marker on a line that dereferences `ptr + 0x...` fails.
- `statics.sh`: a file under `managed/Alloy` or `managed/Services` with more non-readonly `static` fields
  than `statics.baseline` allows fails; a static auto-property with a `set` or `init` accessor counts as one. Free:
  `static readonly`, `const`, static methods and computed properties, and a lifetime owner -- a plain field whose
  type is a `...State` class declared under `managed/`.
- `gl-confined.sh`: a file named in CX0003's `Allowed` that is not in the tree fails. The RULE -- GL only in
  the files that keep it -- is CX0003 (`tools/analyzers/EngineGl.cs`), which the compiler checks because it can
  tell a constant from an entry point and an `Init` guard from a call; this gate does the one thing the compiler
  cannot, and refuses a permission that outlived its file. The list is read out of `EngineGl.cs`, never restated.
  The only GL CX keeps draws CX's own windows on the engine's renderer in a session Vulkan refused, plus the
  stubs for the entry points the engine itself imports; new work records on the seam (`managed/Rhi/`, `CX.Rhi`).
  GL enum numbers the engine's own data speaks are `EngineTokens`, not an entry point, and need no permission.
- `vma-confined.sh`: a file named in CX0005's `Allowed` that is not in the tree fails. The RULE -- the device is
  the backend's -- is CX0005 (`tools/analyzers/VulkanDevice.cs`), which the compiler checks because it can tell a
  `vk*`/`vma*` entry point from an enum member: outside `managed/Graphics/` a Vulkan or VMA call is refused, and
  the one exception is VMA's virtual-block family, which touches no device, in the files that keep books on it
  (the mesh arena). This gate refuses a permission that outlived its file; the list is read out of
  `VulkanDevice.cs`, never restated. Vulkan's enums, structs and handle types are data, and are free anywhere.
- `alias-lanes.sh`: an alias argument under `managed/` that is a string literal rather than an `AliasLanes`
  member fails, as does `Alias = "..."`. Equal alias strings make two render targets SHARE one allocation, so a
  lane is a declaration in `Alloy/Core/AliasLanes.cs` (which says which targets are in which lane, in frame
  order) and a typo must not buy a second allocation in silence. Any parameter named `alias` declares one, so
  `ImageDesc`'s 11th positional and each pass's own `Target` helper are covered by the one rule;
  `managed/Alloy/Golden/` is exempt, its goldens inventing lanes to test the sharing itself.
- `selfcheck.sh`: inside a `*SelfCheck*` or `*ApprovedExamples*` file or a `SelfCheck` method, an `Expect` that cannot fail fails --
  a compile-time-constant condition, a `const` compared to its own kind of literal (over `==` `!=` `<` `>`
  `<=` `>=`, the const named bare or as `Type.CONST` from any file), or the same text on both sides of a
  comparison -- and so does a hand-written `SKIPPED` log line, and an `if (...) return true;` among the statements of a `bool`
  method in a `*SelfCheck*` file that an `Expect` there reads (the check passes unasserted whenever the condition holds;
  `tools/selfcheck_gate_regression.sh` plants it). The const-against-literal shape's standing
  instances are `selfcheck.baseline`, which may only shrink; every other shape fails outright.

  The independent-oracle rule and the rest of the testing rules live in `TESTING_STANDARDS.md`.
- `server-neutral.sh`: `managed/` naming a specific server or its content (SoW, Westgate, Arelith) fails;
  describe it generically. `Framework/Product.cs` (the hosting URLs) is exempt.
- `media-size.sh`: a file under `media/` over 300 KB fails; compress it to WebP (a clip to animated WebP: GitHub cannot show a WebM).
- `workflows.sh`: a workflow under `.github/workflows/` that actionlint or zizmor (offline) refuses, or a composite action under `.github/actions/` that zizmor refuses, fails --
  bad YAML or expressions, an unknown runner label, a shellcheck warning in a `run:`, a cache that feeds a
  release, a checkout that keeps its token.
  Proven by: tools/workflows_regression.sh
- `ssbo-bindings.sh`: an `std430` or `scalar` STORAGE block's binding, a `SsboSlots` constant at or past `SsboSlots.Budget` (16, the
  storage-buffer descriptors `VkBackend` allocates per set -- it reads the budget, so the two are one number),
  or a numeric SSBO index passed straight to `BindBufferBase`/`BindBufferRange` under `managed/` fails.
  Every point lives in `Alloy/Core/SsboSlots.cs`: 0..7 persistent and exclusive, 8..15 shared by readers that
  bind before they draw; `SsboSlots.Check` and `check-shaders.py` pin the GLSL to it, and `SsboSlots.CheckDevice`
  stands Alloy down on a device that seats fewer than the budget rather than finding out at the first bind. The
  qualifier alone does not say which budget a block answers to, so each gate matches on the declaration kind.
- `ubo-bindings.sh`: a `std140` or `scalar` UNIFORM block's binding (or one on the layout macro a block whose layout
  follows the device takes its qualifier from), or a `UboSlots` constant, at or past `UboSlots.Budget` (12, Vulkan's
  guaranteed `maxPerStageDescriptorUniformBuffers`, which `VkBackend` allocates per set), or a numeric
  uniform-block index passed straight to `BindBufferBase`/`BindBufferRange` under `managed/` fails. Every point lives in `Alloy/Core/UboSlots.cs`: 0..2 persistent and exclusive, 3..11 shared by
  passes that bind before they draw; `UboSlots.Check` pins the GLSL to it and `Std140.Pin` pins each block's
  members to the struct that stages them.
- `uniform-blocks.sh`: a loose `Gl.Uniform*` call site under `managed/` counts against
  `uniform-blocks.baseline`, which may only shrink. Alloy writes the Vulkan uniform block itself
  (`LooseBlock`), so every site left is the vanilla CX-UI path drawing on the engine's own renderer, where
  sampler units are opaque and count too; `python3 tools/gates/uniform-blocks.py -v` lists each site.
- `gpu-frees.sh`: an immediate `Gl.Delete{Textures,Buffers,Framebuffers,VertexArrays,Renderbuffers,Samplers}`
  call site under `managed/` fails. Alloy's objects are Vulkan's, destroyed through the backend, which holds a
  freed one past its submit ring, and the GL that stays owns its objects for the session -- so
  `gpu-frees.baseline` is empty and the ratchet now refuses every site;
  `python3 tools/gates/gpu-frees.py -v` lists any it finds.
- `span-ptr.sh`: a `new Span<T>(p, n)` / `new ReadOnlySpan<T>(p, n)` outside `managed/Framework/` counts against
  `span-ptr.baseline`, which may only shrink. Two arguments is always the pointer constructor (the array one takes
  one or three), and a caller-supplied length checks nothing -- `.Slice(i * size, size)` on it is the pointer plus
  an offset ADR 0031 refuses. A view builds its span from its count row and a `NativeBuffer<T>` from its
  allocation; `python3 tools/gates/span-ptr.py -v` lists each site.
- `lane-index.sh`: an `a[i * N + k]` read outside `managed/Framework/` counts against `lane-index.baseline`,
  which may only shrink. A record flattened into lanes asks every reader to know the stride and which lane is
  which, and a wrong `k` stays inside the array; a record type with named fields (`Vector3`, `Vector4`,
  `CullPlane`) and an array of it says the layout once, and `MemoryMarshal.Cast` reinterprets a byte buffer
  that arrives from outside. The stride is what makes it a lane read, so lane 0 (`a[i * N]`) and the
  transposed `a[at + k * N]` count too -- one read spelled three ways must not count three ways. An
  allocation length is a size, not a lane; `python3 tools/gates/lane-index.py -v` lists each site.
- `compiler-rules.sh`: a `#pragma warning disable` or `SuppressMessage` of a `CX` rule, an RS0030 pragma outside
  `managed/Framework/` or with no `// <reason>` on its line, or a `NoWarn`/severity override lowering either in build config, fails.
- `local-paths.sh`: a home directory, mount point or personal checkout path in a tracked file fails
  (`docs/adr/`, `docs/handoff/` exempt); a machine's paths go through `tools/paths.py`.
- `compute-pipes.sh`: a `ShaderDesc(ShaderStage.Compute ...)` or a `CreateShader(ShaderStage.Compute ...)` under
  `managed/` outside `VkBackend.Pipeline.cs` fails; a compute pipeline, from GLSL or from SPIR-V, is
  `VkBackend.CreateCompute`, which frees the module the pipeline no longer needs.
- `piece-links.sh`: a raw `ObjSeamAppendHead`/`ObjSeamAppendNext`/`ObjSeamAppendOwner` read or write under
  `managed/` outside `SceneDb.Pieces.cs` and its self-check fails. The columns are keyed by record INDEX, so a
  second link of one index rings the list and every walk over it then runs forever on the game thread:
  `SceneDb.PieceLink` refuses that shape, a second owner and a link that closes a cycle, and `SceneDb.Pieces`
  walks with a cap, a ring check and each piece's next link latched before its body runs.
- `version-line.sh`: a string literal under `managed/` that IS the version line (`#version 460 core`), outside
  `VkGlsl.cs` and the self-checks whose hand-written literal is their oracle, fails: `VkGlsl.Version` is its one
  declaration, so a stage composed in C# and a stage spliced into a shipped library open with the same line. The
  first line of a stage's own text -- a `.glsl` file, or a multi-line GLSL literal -- is GLSL, not a declaration.
- `pipe-features.sh`: a string literal under `managed/` that starts with a pipeline feature prefix the tally counts by
  (`vk grass `, `vk particles `, `vk probe `, `vk CX window `, `CX window `) fails outside `Rhi/PipeFeatures.cs`;
  a label takes its prefix from `PipeFeatures`, so a rename cannot leave the by-feature count at zero.
- `adr.sh`: a decision record under `docs/adr/` that `bin/check-adr` refuses (its shape, backticks, its word limit) fails, and so does
  a record numbered past ADR-0060 in an older shape: a new record takes decision-record's current one. A Superseded
  line must name a record that exists or one a Retires line names, since a kept record is never edited.
  Proven by: tools/adr_regression.sh
- `world-depth.sh`: a world depth read decoded as GL's (`d * 2.0 - 1.0`) or tested against GL's far plane (1.0) fails --
  in a `.glsl` under `managed/Alloy/Shaders/`, in a helper a world depth reaches through a parameter, or in a GLSL string
  in C# -- and so does a `.glsl` linearisation written anywhere but `cx_depthlin.glsl`'s `CxDepthViewZ`, or C# working the decode's
  terms from the planes anywhere but `WorldDepth.Decode`. The world depth samplers are a list in `world-depth.py`; a new one goes on it. Its approved examples are
  `world_depth_approved_examples.sh` beside it, which `build.sh` runs.
- `depth-confined.sh`: a file named in CX0006's `Allowed` that is not in the tree fails. The RULE -- GL's depth test and
  clear only in the files that keep GL depth (casters, shadow cascades, the GUI), and no depth test without its
  comparison -- is CX0006 (`tools/analyzers/DepthConvention.cs`); its approved examples need the SDK, so `build.sh` runs them.
- `sampler-regex.sh`: a file named in CX0008's `Allowed` that is not in the tree fails. The RULE -- no regex reads
  a GLSL sampler or image declaration -- is CX0008 (`tools/analyzers/SamplerRegex.cs`), which the compiler checks
  because it can tell a regex pattern argument from any other string and can fold a pattern held in a `const`: a
  pattern naming `uniform` or `binding` beside `sampler` or `image` is refused outside `VkGlsl` (whose `Opaques`
  lexes the stage and answers with each declaration's name, binding and bind kind) and the files that REWRITE
  every declaration or read the general global ones. A `Contains` pin on CX's own embedded stage text is not a
  read of a declaration and is not refused (`CODING_STANDARDS.md` says why). This gate refuses a permission that
  outlived its file; the list is read out of `SamplerRegex.cs`, never restated.
- `cache-io-confined.sh`: a file named in CX0009's `Allowed` that is not in the tree fails. The RULE -- a file under
  `managed/Alloy/` or `managed/Graphics/` read whole or written goes through `CacheFile`, which checks each cache file's
  header on read and writes through a temporary name -- is CX0009 (`tools/analyzers/CacheIo.cs`); self-checks are exempt,
  and the frame snapshot, a diagnostic and not a cache, is allowed. The list is read out of `CacheIo.cs`, never restated.
- `content-fixtures.sh`: a `cx_fix*` stage the C# self-checks translate that `tools/content_stage.py`'s `FIXTURE` lacks, or the reverse, fails: the two translations answer to one set of stages.
- `face-normal.sh`: a `.glsl` under `managed/Alloy/Shaders/` that flips a back face's normal inline
  (`gl_FrontFacing ? n : -n`, its negated spelling, or the sign a caller multiplies by) fails, and so does a lit
  main that stops taking its shading normal through `CxFaceNormal`. The flip is one declaration, in
  `cx_lightfrag.glsl`, which a GPU golden runs on the device; a second copy compiles fine and answers to nothing.
  `ALLOWED` in `face-normal.py` names that file and the content stand-in `cx_content_f.glsl`, which the GUI twin
  builds without the light library and so cannot reach the function, and `FLIP_CALLERS` names the lit mains.
- `golden-glsl.sh`: a golden under `managed/Alloy/Golden/`, or in any file under `managed/Alloy/` that extends `GpuGolden`, declaring a `CX_*` constant a shipped shader declares (splice it
  with `GpuGolden.Declaration`), indexing `uIn`/`uOut` with a literal stride (use `CX_GOLD_IN`/`CX_GOLD_OUT`), or a shader
  comparing a light record's `w` with 128 (ask `CxLightIsStatic`) counts against `golden-glsl.baseline`, which may only shrink.
