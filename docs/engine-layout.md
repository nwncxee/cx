# Engine layout: struct offsets and vtable slots across platforms

`docs/windows-symbols.md` answers *where is this function*. This file answers
*where is this field*, which the Windows port (#429) turned into a per-platform
question: the Linux client is built by GCC on the Itanium C++ ABI, the Windows
client by MSVC, and the two disagree about where a field sits inside a class.

`EngineLayout` (`managed/Framework/EngineLayout*.cs`, one partial class) declares every offset the extender reads, and the
running build's file in `managed/Framework/Builds/` gives its value (`ClientBuilds`).
The code says *what* is read, `Field("CNWCCreature", "plated", Expect.Byte01)`, and the
use site reads it as a plain field. The data says *where*, one line per row per build:

    offset	CNWCCreature	plated	0x468	0x448	# CreaturePlated

Vtable **slots** ride the same table; why they diverge, and the handler-signature audit
that goes with them, is `docs/engine-abi.md`.

## Same, Diverges, Unresolved: facts about a build

| Row in the build file | Means | Counted |
|---|---|---|
| `0x468  0x448` | measured on both, they differ (**Diverges**) | no |
| `0xc  0xc` | measured on both, they agree (**Same**) | no |
| `0x208  -` | Linux value; **nobody has looked on Windows** (**Unresolved**) | yes |

These three used to be declaration forms in C#. Now they are properties of a build's
values, but the point hasn't changed: "verified identical" and "never looked" are
never the same thing. A `-` is usable on Linux. On Windows it is **missing**: the row
reads 0 and `EngineLayout.Require` stands down every feature that names its class. So
does a row the file leaves out altogether, on either OS, and `ClientBuilds.SelfCheck`
reports that gap by name. The `-` count is the migration's completion counter,
counted on **both** platforms, because the migration is worked on Linux and a counter
that only moves on Windows is invisible where the work happens. `/knobs` prints it.
The history below still uses the three names for what was measured.

Every row names the **engine class** it belongs to. That is not decoration: it
is the key #431's Ghidra extractor pairs on, and the unit #430 measures. A class
diverges in 2–3 contiguous segments with class-specific boundaries, so a value
is anchored on a **function pair** — the ctor plus one accessor per segment —
never on a field read in isolation.

## Build key

Offsets are only valid for the build they were measured against, so the table is
keyed `(os, build)` and the key is the **linker's build identity**, not a content
hash: a rewrapped Steam/GOG/Beamdog copy of the same build is the same client.

The keys live in `managed/Framework/Builds/<name>.tsv` beside that build's values (`ClientBuilds`).
The first, `8193.37-17`, is revision `26c6e57364a45246e3b58e4f2c49366df72d15db` (Mon Oct 6 2025):
Linux key `3e2500a82e0c32008d24c3e74bbff3021da5e1b7`, Windows key
`29431F05E0F2461A8BECCFC9E80D8F27` age `1`.

Read at runtime from the running program: `/proc/self/exe` `PT_NOTE` on Linux,
the loaded image's `DataDirectory[6]` debug directory on Windows. A mismatch is
**rot**: the **whole extender** stands down, both platforms, with one
`ExtenderLog.Always` line naming the build that was actually loaded (`#457`).
The gate is `EngineLayout.KeyOk`, asked once in `ServiceHost.Run` **before
discovery** — not before `Initialize`, because constructing a service runs its
type initializer and a type initializer is enough to latch engine state. Zero
services constructed, zero hooks installed, the game runs vanilla. On Windows
the same text also goes to `SymbolMap.RotNotice`'s message box, because a
Steam-launched `version.dll` proxy has no log the player will read.

No partial behaviours: a feature that asks neither gate used to keep running
against a client it was never measured against, which is rot arriving as garbage
in some features instead of as a refusal. On Linux the lock itself is new
(`#432`), so a client patch now costs the chatbox, combat text and Alloy, not
just nameplates — the intended trade against rendering garbage off a moved
field, and the reason adding a tested build's key unblocks every feature at once.

Every build file is complete: one `offset` line per declared row, one `rva` line per
`At()` symbol. `ClientBuilds.SelfCheck` refuses a gap, a line naming no row, or a value
that did not land in its row. A later build's file comes from `tools/update/newbuild.py`,
which copies a row from the old file only when every function vouching for it is
byte-identical in the two builds, and comments out the rest as `# CHECK` for a human.
The procedure is `docs/update-day.md`.

**Two checks, one policy.** `SymbolMap` still checks the PE `TimeDateStamp` (the
build file's `stamp`) for its RVAs; this checks the build key. Since `#457` the stamp can never be the *first*
thing to notice rot — nothing gets as far as resolving a symbol — so the two
locks were settled without collapsing either: `KeyOk` is the policy, the stamp
stays the RVA-path guard.

## Probes

A resolved offset can carry an assertion about live memory, checked once at the
first sighting of an instance, so a wrong value **stands the feature down**
instead of rendering garbage — a probe that only logs is not a stand-down, so the
caller latches the failure and every read below it answers "nothing". `EngineLayout.Probe(cls, ptr, log)`; the caller holds its own
one-shot latch, because a name lookup does not belong in a frame loop.

| Predicate | Asserts |
|---|---|
| `Byte01` | the dword is 0 or 1 — a boolean field. A wrong offset lands in a pointer or a float. |
| `VtblSlot` | `*(*obj + off)` is a code address inside the client image. |

The vocabulary grows as offsets that can carry an assertion arrive; most cannot,
and a probe that cannot fail is not a probe. `EngineLayout.SelfCheck` proves the
failing case by feeding a `Byte01` offset a 2.

## Table

| Class | Field | Linux | Windows | How anchored |
|---|---|---|---|---|
| `CNWCObject` | `type` | `0x0c` | `0x0c` (same) | the byte both `ShowFeedbackBubbleText` overrides switch on |
| `CNWCObject` | `vtbl.GetPortrait` | `0x1d8` | `0x1d0` | #420: slot counted in the Windows class, not reused from Linux |
| `CNWCCreature` | `plated` | `0x468` | `0x448` | top of `ShowFeedbackBubbleText` — Linux @`0x71a81e`, Windows @`0x80b240` |
| `CNWCPlaceable` | `plated` | `0x218` | `0x1f8` | same, Linux @`0x7ea8b8`, Windows @`0x871b60` |
| `Part` | `vtbl.AsPartTriMesh` | `0x60` | `0x58` | #433: both vtables dumped, `tools/abi/vtable-slots.py` |
| `Part` | `vtbl.AsPartBeam` | `0x90` | `0x88` | same |
| `Part` | `vtbl.PostProcess` | `0xe0` | `0xd8` | same |
| `CAurTexture` | `vtbl.AsCAurTextureBasic` | `0x10` | `0x08` | same |
| `CAurTexture` | `vtbl.GetReferencedTexture` | `0x18` | `0x10` | same |
| `VertexPrimitive` | `vtbl.SetupArrays` | `0x38` | `0x30` | same |
| `CAurBehavior` | `vtbl.AsBehaviorCameraFollow` | `0x48` | `0x40` | same |
| `CycleTIDTextureController` | `vtbl.Control` | `0x10` | `0x08` | same |

### Alloy: the Scene walk (#436)

124 rows, 33 of them `Unresolved`. Measured 2026-09-11 by running
`tools/ghidra/FieldDump.java` over both programs and diffing with
`tools/ghidra/field-diff.py`; reproduce with the command block at the end of
the research on CX-173, class list

    Scene Gob Part PartTriMesh PartDanglyMesh PartSkin PartLight MdlNode MdlNodeTriMesh
    MdlNodeDanglyMesh MdlNodeSkin VertexPrimitive VertexPrimitiveDangly VertexDataObject
    Material SharedMaterial LightManager CAurTextureBasic TileData BSPNode Camera

Per-row provenance is the XML doc on the row itself. The verdict per class:

| Class | Verdict | Anchor |
|---|---|---|
| `Scene` | identical `0x0..0x1e0` | 32 offsets paired over 38 functions, no conflict. `0x208` is above it and stays `Unresolved` |
| `Gob` | identical `0x0..0x4b8` | 28 offsets over 50 functions |
| `Part` | identical `0x0..0x58` | 8 offsets; `GetWorld`/`GetLocal` touch the same eight on both, ordered differently |
| `MdlNode` | identical `0x58..0x98` | 6 offsets over 11 functions; `0x18` and `0x270` are outside it. `0x98` itself is the whole body of `MdlNode::AsMdlNodeTriMesh` (Linux @0x43adf0, Windows rva 0x8c360): same `& 0x21` test, same offset |
| `Camera` position/orientation | identical | Camera's OWN accessors, not Gob's — Linux has two overloads of each (`+0xb0`/`+0xa8`, `+0xbc`/`+0xb4`) and Windows folds each pair into one reading `+0xb0`/`+0xbc` |
| `Model` | **unresolved** | nothing in the sweep touches `+0x8e` on either build, and the class of `gob+0x68` is itself unconfirmed |
| `MdlNodeTriMesh` / `MdlNodeSkin` | identical where read | `0x118`, `0x278`, `0x280`, `0x284` all paired at delta 0; 2026-09-23 adds `0x248`/`0x260`/`0x26c` on the `SetupArrays` pair below |
| `MdlNodeDanglyMesh` | identical to `0x284` only | the dangly parameters at `0x2c8..0x2e0` are unmeasured |
| `PartTriMesh` | segmented: `+0x4` from `0x64`, `+0x8` from `0x88` | ctor block `0x64..0x93` → `0x68..0x97`; `WithinRange` reads the six bbox floats at `0x68..0x7c`; `0x88`/`0x96`/`0xa0`/`0xb0` paired at `+0x8` |
| `PartDanglyMesh` | **unresolved** | no validated pair on either build — and a base's delta need not carry into a subclass |
| `PartLight` | segmented: own block `+0x4` | `0x64`/`0x68`/`0x6c` paired; the Windows-only set `0x70 0x78 0x80 0xa8 0xb0 0xb8` is the Linux set shifted `+0x4` |
| `LightManager` | identical `0x0..0xc0` | 6 paired, plus #430's ten-function hand read; CX-85 added `frameData 0x40` / `frameCount 0x48` to the same range, measured on Windows `EnableLights` (rva `0x7e890`, `this + 0x60` / `this + 0x40` verbatim) and on `ControlLightsFade`'s the int at `+8` of its second argument |
| `Camera` | base region 0, `0x4c4..0x4e4` `+0x4`, above that `+0x8` | 18 offsets over 16 functions, including `0xe50` and `0xe64` at `+0x8` |
| `Material` | identical `0x0..0xd0` | `0x0 0x40 0x50 0xd0` paired |
| `SharedMaterial` | **unresolved** | `0xd8`/`0x1c0`/`0x1c8`/`0x1e8` pair at delta 0, yet Windows touches `0x190 0x1a1 0x1b2` — the resref stride `+0x8` from Linux's `0x188 0x199 0x1aa` — and Linux's `0x1f1` has no Windows counterpart. Unexplained, so nothing is resolved on it |
| `CAurTextureBasic` | identical `0x28..0xec` | #430's thirteen-function hand read; MSVC splitting Linux's SSE stores explains the Windows-only `0x90`/`0xa0` |
| `VertexDataObject` | `0x8` 0, `0x14..0x44` `+0x4`, `0x48` and up 0 | 7 offsets over 6 functions, both ends of each segment |
| `VertexPrimitive`, `VertexPrimitiveDangly`, `BSPNode`, `TileData` | **unresolved** | no validated pair on either build. 2026-09-23: `VertexPrimitiveFlat::SetupArrays` (Linux @0x458920, Windows `?SetupArrays@VertexPrimitiveFlat@@UEAAXXZ` rva 0xabca0) IS one, on its whole summing prefix -- `0x08`, `0x58`, `0x60`, `0x64`, `0x84`, `0x88`, `0x90` at delta 0. The class verdict stays unresolved: the pair covers the prefix, not the object |

Two rules this batch settled, because they are the rules the remaining batches
inherit:

- **A class verdict does not travel to a subclass.** `Part` is identical and
  `PartTriMesh` is not; `PartTriMesh` is `+0x8` above `0x88` and `PartDanglyMesh`'s
  own fields are unmeasured. A row's class is the class that DECLARES the field.
- **A vtable slot is cheap to measure and should be.** `tools/abi/vtable-slots.py`
  prints both tables in seconds, which turned `Scene::IsAnimating` (`0x288` → `0x280`)
  and `VertexPrimitive::AsVertexPrimitiveDangly` (`0x28` → `0x20`) from assumptions
  into rows. No `vtbl.*` row should be `Unresolved` without running it.
- **Set corroboration is not a resolution.** `SharedMaterial` is the reason: its
  offsets are nearly all shared, and the shape of what is not shared says the class
  moved.

### Alloy: the passes (#441)

111 more rows, 22 of them `Unresolved` — 235 rows and 55 unresolved in total.
Measured 2026-09-12 with the same `tools/ghidra/FieldDump.java` over both
programs (`.scratch/441`), class list

    PartEmitter MdlNodeEmitter Particle ChunkyParticle TileData CAurTriangleBin
    CAurTriangleList CAurGrassInfo PartBeam MdlNodeLight Scene PartLight Gob Part
    MdlNode Material aurMatrixStack MdlNodeTriMesh GLRender SharedMaterial

The method changed in one way, and it is the batch's most reusable finding:
**read the function pairs directly rather than through `field-diff.py`.** The
script drops a pair the moment the two decompilers split one 8-byte store
differently (`length-mismatch`, `shape-mismatch`), which is most of them — it
reported *three* validated pairs for `PartEmitter` and two mapped offsets for
`Particle`. The same dumps, read by hand, resolve both classes completely.

Roughly a quarter of the directory's raw hex needed no measurement at all: it was
already a row. `Beams` and `Particles` each carried their own copy of the matrix
stack, `SkyBox` its own `GobAlpha`/`GobPos`/`PartParent`, `GrassField` its own
`PartChildren`/`PartChildCount`/`Part+0x96`, `Halos` and `Particles` their own
`Scene+0x158`, `GrassField.Border` its own `MdlNodeTriMesh+0x280`.

| Class | Verdict | Anchor |
|---|---|---|
| `PartEmitter` | **five segments**: `0x0..0x50` 0, `0x64..0xd8` `+0x4`, `0xf8..0x158` `+0x8`, `0x188..0x1a8` 0, `0x1c0`+ `-0x8` | eight function pairs (`RenderEmitter`, `RenderNothing`, `GetTileAlpha`, `chkRandom`, `chkParticleLife`, `moveParticle`, `moveParticleP2PGrav`, `moveParticleP2PBezier`); the `-0x8` is corroborated by the object's own end (`0x1f8` → `0x1f0`) |
| `Particle` | identical `0x0..0x77` | the ctor touches the same thirteen offsets on both, `initialize` the same twenty-two |
| `PartBeam` | identical at `0xa0`/`0xa8` | its whole `Draw` is two reads, and they pair at delta 0 — it does **not** inherit `PartTriMesh`'s `+0x8` |
| `PartLight` | `+0x4` block confirmed at `0xa0` → `0xa4` | the ctor's run is `0x64..0xb7` → `0x68..0xbb`, uniform |
| `MdlNodeEmitter` | `+0x4` at `0xa4`/`0xb0` and at `0x170` | 22 Enable/Is accessors all pair the flags word `0x170` → `0x174`; the ctor brackets the UV grid. `0x164` stays **unresolved** — the ctor's map is order-preserving everywhere except there |
| `MdlNodeLight` | **unresolved** | none of its nine functions pairs on either build |
| `MdlNodeTriMesh` | identical at `0xb0`/`0xb8` | `InternalGenVertices` pairs both at delta 0 |
| `TileData` | identical `0x0..0x57` and `0x7c..0xcb` | the ctor writes `0x0..0xcb` on both and agrees store for store *outside* `0x58..0x7c`, where MSVC splits three of Linux's 8-byte stores — which is exactly where #436 left `TileFade`/`TilePendingFade` unresolved |
| `CAurTriangleBin` | identical | nine functions pair, every one at delta 0 |
| `CAurTriangleList` | identical `0x0..0x60` | seven paired functions, bracketing `0x40` between `0x3c` and `0x48` |
| `CAurGrassInfo` | identical `0x0..0x50` | its ctor is the class's ONLY function; one anchor rather than a pair, because there is no second function to be had. Its `0x54` stride is the batch's one ABI argument: last member a byte at `0x50`, largest a float, **no base class** — and base tail-padding reuse needs a base |
| `aurMatrixStack` | identical, and so is its SIZE | all seven functions pair, and the union of what they touch is `0x18..0x57` on both and nothing else — which measures the top and the `0x58` stride together |
| `Scene` | four more spans inside #436's identical run | `0x190` paired twice; `0x18`/`0x38`/`0x40`/`0x50`/`0x60` inside it. `0x1e8` is above `0x1e0` and stays **unresolved**, like `0x208` |
| `Gob` | `0x160`/`0x16c` paired by `GetBoundingBox`, `0x450` three times | and `Gob::GetAdjustedScale` `0x2d8` → `0x2d0`, from `tools/abi/vtable-slots.py` |
| `MaxFace`, `VertexPool`, `MaterialParameter`, `GLRender::PROG_FIXED` | **unresolved** | nothing takes one as `this`, so the sweep cannot reach them — the same shape as `DrawBucket`'s stride |
| `CNWCArea` | **unresolved** | no `CNWC*` class is name-exported on Windows; needs #420's RVA path |

One premise this batch came in with was **wrong**, and the correction is worth
keeping: `Halos` read `PartLight+0xa0` as a fade and the table had `LightFade` at
`+0x90`, which looked like one field written down twice. It is two fields.
`Scene::RenderLensFlares` @`0x471860` integrates `+0xa0` toward 1.0 while a light
is unoccluded and multiplies the flare colour by it;
`LightManager::ControlLightsFade` @`0x4c2050` scaled the area terms by `+0x90` (CX-85 ported both
integrators into `Alloy/Scene/LightFade.cs` and `Halos`, which write the same two fields);
and the `PartLight` constructor stores eight bytes at `0x8c` and again at `0x9c`,
so both dwords exist. The same shape repeated at `TileData`: `PartEmitter`'s fade
owner reads `+0xc8`, `HandleFadingInBucket` drives `+0x70`/`+0x74`, and those are
three distinct floats.

`Require` takes the classes a feature reads, so an unmeasured renderer class
cannot stand an unrelated feature down: `NameplateService` asks about
`CNWCObject`/`CNWCCreature`/`CNWCPlaceable`, `DialogService` about `CNWCObject`.
A caller that passes no class still asks about the whole table.

Anchors are handwritten, like `docs/windows-symbols.md`. There is no generator:
#431's extractor produces **candidates**, and a candidate becomes a row here only
once it clears the bar below.

## The bar: what a candidate has to survive to become a row

Settled in #445, after three batches had answered it by practice. A candidate is
an extractor's ranked guess; it tells you which function to open and is never
itself a resolution. A **row** needs all three of these:

1. **A same-role function on each build.** Same class, same job, identified by
   name or signature — not by ordinal position in a list, because MSVC reverses
   an overload set (#433, and #443 caught it firing on
   `CAurTexture::GetCompressedSize`).
2. **The field's role pinned by evidence other than its position.** What the
   caller does with it: a stride multiply, a constant it is compared against, a
   store run in a constructor, the argument slot it is passed in. Two fields that
   look alike at neighbouring offsets are two fields at least as often as they
   are one mistake (#441's `PartLight` fades, #443's two `CAurTextureBasic` names).
3. **A probe where a predicate exists.** `Expect.Byte01` and `Expect.VtblSlot`
   cost nothing to declare. Where neither fits, the row is still a row — the
   probe is a check on the reading, not the reading itself.

"Function pair" means a pair **you read**, off the raw `FN` lines of both
decompilations. It does not mean a pair a tool validated: a strict
length-and-shape filter rejects a pair the moment the two decompilers split one
store differently, which is why `field-diff.py`'s pairing half was retired in
#445 and only its class histogram kept.

Three things that are never a resolution, each of them a mistake already made
here:

- **Interpolating across a segment boundary.** A class diverges in 2-3 contiguous
  segments (#430); a row that falls in the gap between two measured segments is
  unresolved, full stop. `PartTriMesh.selfIllum` is deliberately unresolved for
  this reason, and the one wrong top-1 answer in #431's ground truth
  (`Camera+0x4d4`) is this mistake.
- **Set corroboration.** "The other six fields of this class paired at delta 0,
  so this one does too" is not a measurement (#436).
- **A verdict inherited from a base or passed to a subclass.** The cause of the
  divergence is base tail-padding reuse, which predicts that sibling subclasses
  get *different* deltas (#430). A class verdict does not travel in either
  direction.


### Alloy: the renderer, textures, buffers and shim (#443)

The last of #429 section 2: **73 new rows, 5 of them `Unresolved`, and six of
#441's `SharedMaterial` rows plus its two `Material`, two `SharedMaterial` and
four `MaterialParameter` rows resolved** — 308 rows and 46 unresolved in total.
Measured 2026-09-12. `build.sh` now refuses a bare hex integer constant under
`managed/Alloy/`, which is what closes the section.

Four instruments, and which rows came off which:

| instrument | what it answered |
|---|---|
| `tools/ghidra/FieldDump.java`, one cycle, classes `CNWCAnimBase CRes CAurTexture Material` (`.scratch/443`) | `CRes` (0x20/0x30, one-line accessors on both), `Material` (0x118/0x120 via `InitSharedMaterial`), `CAurTexture` (`GetReferencedTexture` reads `+0x18` on both), and `CNWCAnimBase` — which dumps **empty** on Windows, so its three channel rows stay unresolved |
| the #436 / #441 dumps, re-read | every `CAurTextureBasic` row, `Scene`'s highlight triple, `Gob+0x1c`, `MdlNodeTriMesh` 0xe8/0xf4, `Material`'s 0x10 slot stride, `SharedMaterial`'s 0xd8 and 0x11 |
| `tools/ghidra/DecompOne.java` on both builds | the three functions no class sweep reaches: `SharedMaterial::BindShaderParameters` (@`0x4c5480` / @`0x82aa0`) for the whole `MaterialParameter` record; `GLRender::UnlockPool` (@`0x4cfc80` / @`0xbbad0`, which forwards to a helper @`0x140bbaf0`) for the pool descriptor; `GLRender::SetUniforms` (@`0x4ccbf0` / @`0xb8380`) for `m_shaderExposedRuntimeData` |
| `tools/abi/vtable-slots.py` | all twenty vtable rows — ten `CAurTexture` accessors, eight `Scene` slots, `LightManager::EnableLights`, `Gob::GetAttachedGobs` |

Three things the batch learned that the offset hunt (#429 section 3) inherits:

- **The overload swap can cancel the −8.** Every vtable row in this batch is −8
  on Windows for #433's virtual-destructor reason *except*
  `CAurTexture::GetCompressedSize()`, which is `0xa8` on **both**: MSVC emits the
  overload set `(int,int)`-then-`()` where Itanium emits `()`-then-`(int,int)`,
  and the swap gives back exactly the pointer the destructor took away. The rule
  is a hypothesis; the table is the measurement.
- **Pair a struct reached through a global by SYMBOL ADDRESS, not by slot.**
  `GLRender::SetUniforms` compares each `PROG_FIXED` cache slot against one field
  of `m_shaderExposedRuntimeData`, and in the scalar block `0x10..0x5c` the slot
  index happens to equal the field offset — above it, it does not. The first
  attempt here paired a `DAT_01aaf6xx` cluster that looked like the struct and is
  a different global. Resolve the exported symbol first (Linux `0x19e1ea0`,
  Windows rva `0x128ea10`; Ghidra's Linux addresses are `nm + 0x100000`), then
  ask whether `base + off` is touched on both.
- **MSVC splits a 17-byte copy into 8 + 8 + 1.** That is the whole of #441's
  unexplained `SharedMaterial` "+0x8": the stores at `0x190`/`0x1a1`/`0x1b2` are
  the second halves of the resrefs at `0x188`/`0x199`/`0x1aa`, whose stride is
  `0x11` on both builds. With that explained the constructor is a pair, and six
  rows #441 refused are now measured.

What is still `Unresolved` here, and why it is the honest answer:

| row | why |
|---|---|
| `CNWCAnimBase` `hiliteActive` / `hiliteGlow` / `gob` (then `hiliteGob`) | no `CNWC*` class is name-exported on Windows; needs #420's RVA path. **Resolved in #446** — an export is not an anchor |
| `PartTriMesh` `selfIllum` `+0x7c` | it falls in the GAP between PartTriMesh's two measured segments (`+0x4` across the bbox, `+0x8` from `0x88` up). Windows `WithinRange` reads the bbox at `0x68..0x7c`, so `+0x7c` is bbox there, and nothing measures where the boundary's four bytes land. Interpolating across a segment boundary is the Camera `+0x4d4` mistake. **Resolved in #448 by measuring instead of interpolating** — `PartTriMesh::Mat` reads it |
| `GLRender` `poolTableAnchorOffset` `0x236c` | Windows reaches the `{base, count}` pair as its own static (rva `0x128ead8`) passed *by pointer* into the pool helper, not at a fixed distance from any symbol. What Windows needs is its own anchor in `SymbolMap._win`, not a Windows value for this row |

### The seven CNWC* rows (#446)

Section 3, T1. Seven `Unresolved` rows whose owning class has **no Windows name
export**, so #431's class sweep dumps it empty: `CNWCAnimBase` ×3 (the highlight
channels), `CNWCArea` ×3 (flags, tile width/height), `CNWCModule` ×1 (current area).
All seven are now `Same` — 46 unresolved → 39 — and the three anchors they were read
from are in `docs/windows-symbols.md`.

The instrument was #420's, unchanged: anchor the Windows RVA of a function that
touches the field, `DecompOne` it on both builds, read the offset off it. What made it
cheap was that **`nwmain.exe` keeps its `__FUNCSIG__` and `ExoLog` strings even for
classes it does not export** — `"int __cdecl CNWCArea::LoadBorder(class
CGuiAreaLoadscreen *,float)"` sits in the tracy source-location struct of the one
function that is `LoadBorder`. So "not name-exported" is not the same as "not
anchorable", and T1 was the shortest of the three section-3 tickets rather than the
longest the ticket predicted.

| Class | Verdict | Anchor |
|---|---|---|
| `CNWCAnimBase` | identical at `0x60`/`0x9c`/`0xb0`/`0xc8` | `RefreshHilite`, one body per build, paired on its own `nwcanimbase.cpp:0x123a` `ExoLog` line. Role by the loop: flags stride 4 over 5 channels, colour stride 12, gob the guarded pointer |
| `CNWCArea` | identical at `0x8`, `0xc`, `0x10`, `0x248`, `0x298` | `LoadArea` for `+0x8` (wire order), `LoadBorder` for the grid (`(height-1)*width`), `ReloadBorder` for the scene pointer |
| `CNWCModule` | identical at `0xf8` | `CNWCCreature::UpdatePopUpBubble` dereferences it and compares `area+0x118` |

Three things worth keeping:

- **A string anchor survives a missing export.** The whole "CNWC* is unreachable on
  Windows" premise (`#441`, `#443`, and this ticket's own framing) was about the *class
  sweep*, and it silently became a claim about the binary. It is not: three of the four
  anchors here came straight out of the string table.
- **Wire order is a role pin.** `CNWCArea+0x8` has no arithmetic to read it by — it is
  a flags DWORD that `LoadArea` only stores. What pins it is the *sequence* of
  `CNWMessage::Read*` calls around it, which is the message format and therefore
  identical by construction on any build that talks to the same server.
- **The −8 vtable rule fired three more times, on three classes** — and naming them
  right matters, because `RefreshHilite`'s `this+0xc8` is the **Gob**, not the scene.
  Gob `0x180`/`0x188`/`0x220` → `0x178`/`0x180`/`0x218`; Scene
  `AddHighlightGob`/`RemoveHighlightGob` `0x120`/`0x128` → `0x118`/`0x120`; Scene
  `DeleteTile`/`SpawnTile` `0x58`/`0x38` → `0x50`/`0x30`. All measured in passing, none
  a row here; the two `Scene` readings agree with the eight `Scene` `vtbl.*` rows that
  are.


### The camera layer rows (#536)

Eighteen `Same` rows, measured by decompiling both builds directly (no pairer: the
behavior class is name-exported on both, and `CNWCModule`'s camera functions hang off
the anchored `HandleServerToPlayerMessage` dispatch). Provenance is
the research on CX-137 §9.4; the Windows entry points it names are the
anchors.

| Class | Rows | Anchor |
|---|---|---|
| `CAurBehavior` | `owner 0x18` | both ctors store the argument there |
| `CAurBehaviorCameraFollow` | `focus 0x30`, `dist 0x48`, `yaw 0x54`, `pitch 0x5c`, `yawClamp 0x64`, `pitchClamp 0x70`, `distClamp 0x7c`, `yawMin 0x68`, `pitchMin 0x74`, `distMin 0x80` (max = min + 4) | `Control` (Linux @`0x4a2290`, Windows @`0x140055100`), one body per build reading every field |
| `CNWCModule` | `camera 0xe8`, `cameraAttached 0x124`, `findClearView 0x140` (probed 0/1), `dialogCamera 0x258`, `cutsceneRate 0x298` | `SetCameraFollow` rva `0x7cf6f0`, `SetDialogCamera` rva `0x7cfa90`, `SetCutsceneCamera` rva `0x7cfa00`, reached from the camera (`0x77a7f0`) and dialog (`0x7962f0`) message handlers |
| `CClientExoAppInternal` / `CClientOptions` | `clientOptions 0x0` / `cameraMode 0x1d` | `GetClientOptions` is `**(app+8)` on both (`0x637ea0` / rva `0x6ab9b0`); the byte is read at `+0x1d` by `SetCameraFollow` and by the lock arms of the Windows camera handler |

### The drive rows (#540)

One function per build, `CClientExoAppInternal::UpdateDriveMode` (Linux @`0x65f780`, Windows rva
`0x6f1e40`, found by its cadence constants -- `docs/windows-symbols.md`), decompiled on both and
read side by side. The `CClientExoAppInternal` fields are identical; the two `CNWCCreature` gates
carry the class's known `-0x20` Windows shift (the same delta as `plated`).

| Class | Rows | Anchor |
|---|---|---|
| `CClientExoAppInternal` | `driveChanged 0x1b0` (probed 0/1), `driveWalk 0x1b4` (probed; the walk-forward key, #637), `driveKeys 0x1b8` (probed; six ints: forward, back, turn L/R, strafe L/R), `driveLastSend 0x1a0` | the function's own reads on both builds: the latch tested first, the seven key ints in the keep-alive OR, `+0x1a0` stamped on every send and compared against 499999 |
| `CNWCObject` | `orientation 0x44` | both builds `atan2(+0x48, +0x44)` for the wire angle and copy `+0x44..+0x4c` into the TurnOnSpot rotation |
| `CNWCCreature` | `aiFlags 0x39c / 0x37c` (bit 2), `driveEnabled 0x43c / 0x41c` | the two early-outs at the top of the function on each build (`& 2 == 0` then `== 0`), in the same order |
| `CNWCCreature` | `facingTarget 0x2c8 / 0x2a8`, `facingTargetB 0x2d4 / 0x2b4` | the pair both builds write with the quantized direction right after the DriveControl send and in the TurnOnSpot arm; `DrivePlayer` writes the first alone for its ±30° turn |
| `CNWCModule` | `chaseSuppressed 0x13c` (probed 0/1), `chaseWasIdle 0x27c` (probed) | `UpdateCameraModeChaseCam` / `EQ` (Linux @`0x7dddd0` / `0x7de660`, Windows rva `0x7d0d00` / `0x7d1c90`, found by `ScanOffsets 134 138 13c 27c 144 100`): `+0x13c` set with `+0x138 = 0` when `+0x134` (camera moved) holds, cleared with `+0x27c` on the idle-to-locomotion edge, both builds the same four fields |

### The recenter rows (#541)

No new function: the same two chase drivers and the one solve. Read on both builds.

| Class | Rows | Anchor |
|---|---|---|
| `CAurBehaviorCameraFollow` | `yawVel 0x58`, `yawImpulse 0x88`, `pitchVel 0x60`, `pitchImpulse 0x8c` | `TurnCamera(deg, 0/1)` (Linux @`0x7d8960`, Windows rva `0x7cfe60`) adds into `+0x58` / `+0x88`; the chase drivers write `+0x58`; `Control` integrates and zeroes all four at the same offsets on both builds (the pitch pair: `+0x60*dt + +0x8c` into `+0x5c`) |
| `CNWCObject` | `animation 0x5c` (short) | `GetCurrentAnimation` is `*(short*)(this+0x5c)` on both (Linux @`0x7e3f70`, Windows rva `0x7630d0` -- the chase driver's own callee, nine times per call for the locomotion test) |

### The free-camera rows (#543)

| Class | Rows | Anchor |
|---|---|---|
| `CNWCCreature` | `combatState 0x1f4 / 0x1d4` | `SetCombatState(int)` (Linux @`0x722160`, Windows rva `0x80a230`): the first store in both bodies; the Windows body is the callee right after the `+0x37c/+0x37e/+0x380/+0x388/+0x384` cluster in the Windows `HandleServerToPlayerCreatureUpdate_Update` (`0x781e80`, `ScanOffsets 37c 37e 380 388 384` = 1 hit), the same cluster Linux writes at `+0x39c..` before its call |
| `CNWCObject` | `position 0x38` | `SetPosition(Vector)` Linux @`0x7e9180` writes `+0x38..+0x40`; the orientation row at the same `+0x44` on both builds pins it |
| `CNWCModule` | `cameraMaxDist 0x2b8` | `SetCameraLimits(float,float,float,float)` Linux @`0x7def00` / Windows rva `0x7cf9d0`: four consecutive stores `+0x2ac..+0x2b8`, the ctor seeds `-1` |
| `CClientExoAppInternal` | `inGameGui 0x90` | `CClientExoApp::GetInGameGui` is `*(*(app+8)+0x90)` on both (Linux @`0x637c70`, Windows rva `0x6abbc0`) |
| `CGuiInGame` | `toggledOff 0x3d4 / 0x374` (probed 0/1) | `ToggleOff` / `UnToggleOff` (Linux @`0x66dda0` / `0x66e930`, Windows rva `0x6fc6f0` / `0x6fc900`) test and write it first; reached from the anchored message dispatch, case 0x33 -> cutscene handler -> minor 7 |

### The dialog-master row (#591)

| Class | Rows | Anchor |
|---|---|---|
| `CGuiInGame` | `dialog 0x70` | the stock `CGuiInGameChatDialog*`: `ShowDialogReplies` (Linux @`0x770c30`, Windows rva `0x6fb8e0`) is `if (*(this+0x70)) ...` on both, `CloseDialog` (Linux @`0x770320`, rva `0x6f6d80`) virtual-deletes it, stores 0 to `+0x70` and calls `ResetGuiState(this, 0x298)` on both |

### The vanilla-prompt rows (#580)

Read off `CGuiInGameChatPrompt::HandleEnter` on both builds (Linux nm `0x679c50`, Windows rva
`0x87b0d0`, the At row the hotkey observer already hooks) and `CGuiEdit::GetText` (exported on both,
Linux nm `0x592680`, Windows `0x1e93b0`), `tools/ghidra/DecompOne.java` on each program.

| Class | Rows | Anchor |
|---|---|---|
| `CGuiInGameChatPrompt` | `edit 0x100 / 0xf8` | HandleEnter hands `this+off` to `GetText`, `SetCaretPos` and `LoseFocus` on both builds (the `+0xf8` the #419 table already had from `SetFocus`) |
| `CGuiEdit` | `string 0x90` | `GetText` reads `m_pString` at `this+0x90` on both, and HandleEnter's inlined `SetText` reads the same word through the prompt (`+0x190` / `+0x188` = edit + `0x90`) |
| `CAurString` | `vtbl.SetText 0xd8 / 0xd0` | the clear call in HandleEnter: `(*m_pString)[0xd8](m_pString, "")` on Linux, `[0xd0]` on Windows; the Linux slot is the `StringGob` secondary-base thunk `_ZThn1216_N9StringGob7SetTextEPKc` in `_ZTV9StringGob` (offset-to-top -1216), so `m_pString` is the `CAurString` interface pointer and the slot is called through it, never on the `StringGob` primary table |
### The stock-settings reader rows (#571)

Three `Same` rows for `StockConfig`, measured by decompiling both builds directly
(`CExoConfig` and `CExoBase` are name-exported on both):

| Class | Rows | Anchor |
|---|---|---|
| `CExoBase` | `config 0x0` | both ctors store the freshly built `CExoConfig` in the first word (Linux `CExoBase::CExoBase`, Windows @`0x140114b00`); every engine `Get` caller loads `**g_pExoBase` for `this` (Linux `CGuiInGame::InitializeUserOptions` @`0x66e24a`, Windows `CAurora::ComputeDesiredGUIScale` @`0x140074f70`) |
| `CExoConfig` | `initializing 0x0`, `workingTable 0x28` (committed table at `0x38`, not a row) | `Get<bool>` on both builds (Linux @`0x4efce0`, Windows @`0x140119650`): `*this != 0 \|\| committed` picks `+0x38`, else `+0x28`; `CExoConfig::Initialize` @`0x4e2800` sets the byte for its duration and clones `+0x38` into `+0x28` before clearing it -- the ctor leaves `+0x28` null, which is the reader's "not yet" test |

The `std::string` argument is not a row: it is a header the reader forges in the running
platform's layout, and the callee reads are the two library comparators (libstdc++
`basic_string::compare` @`0x4318d0` reads `ptr@0`/`len@8`; MSVC `std::less<string>` @`0x140130ad0`
reads `size@0x10`, `res@0x18`, and the pointer at `+0` when `res > 15`).

### The main-menu rows (#593)

Two `Same` rows for the main-menu gate, read off `CClientExoAppInternal::DisplayMainMenu` on both builds
(Linux nm `0x63c320`, Windows rva `0x6d74f0` -- the caller of the `CMainMenuPanel` ctor, itself the only
referrer of `"PRE_MAIN"`; the research on CX-149), by objdump of each:

| Class | Rows | Anchor |
|---|---|---|
| `CClientExoAppInternal` | `mainMenuPanel 0x98` | `DisplayMainMenu` returns when `+0x98` is non-null, else `new`s the panel (`0x840` bytes on Linux, `0x758` on Windows) and stores it there before `CGuiPanel::Activate`; `DestroyMainMenuPanel` (Linux nm `0x63a650`) deactivates, deletes and nulls the same word |
| `CClientExoAppInternal` | `reachedMainMenu 0x5f0` (probed 0/1) | the same function's first write, `mov dword [this+0x5f0], 1`, on both builds; `CClientExoApp::GetReachedMainMenu` (Linux nm `0x638cf0`) reads it through `+0x8` |

### The in-game Options rows (#593 follow-up: the gear under the ESC menu)

The frame gate's raw constants became rows, plus the two the in-game gear needs. Read by `DecompOne` on both
builds; the Windows `CGuiInGame::HandleOptionsButton` (rva `0x6f86c0`) is the caller of the `COptionsPanel` ctor
(rva `0x822ce0`, the only referrer of `"OPTIONS_MAIN"`) that also stores the panel's close callback.

| Class | Rows | Anchor |
|---|---|---|
| `CClientExoAppInternal` | `optionsPanelOpen 0x604` (probed 0/1) | `CClientExoApp::SetOptionsPanelOpen(int)` (Linux nm `0x638d30`, Windows rva `0x6acbf0`) is the one store, `*(*(this+8)+0x604) = arg` on both; `HandleOptionsButton` passes 1, `HandleOptionsPanelClose` (Linux @`0x76ea50`, rva `0x6f8820`) passes 0; the ctor zeroes it |
| `CGuiInGame` | `chatWindow 0x8` | `AppendToMsgBuffer` (Linux @`0x76d550`, Windows rva `0x6f68d0`) loads `this[1]` first thing on both and hands it to `AppendToChatBuffer`; `PrimaryMsgWindowIsVisible` (Linux @`0x76e380`) forwards it to `IsVisible` |
| `CGuiInGame` | `optionsPanel 0xd8` | `HandleOptionsButton` (Linux @`0x76de30`, rva `0x6f86c0`) toggles a non-null `+0xd8` closed via `HandleIngameExit`, else `new`s the panel (`0xc00` bytes Linux, `0xac0` Windows) into `+0xd8`, sets its owner/callback at `+0x80/+0x88` and activates; `HandleOptionsPanelClose` nulls `+0xd8` first thing on both, before `CGuiPanel::Deactivate` deletes the panel |
| `CGuiPanel` | `state 0xb8 / 0xb0` | exported `Hide` / `UnHide` / `Activate` (Linux @`0x687160` / `0x687740` / `0x6878b0`, Windows rva `0x1fc800` / `0x1fd0d0` / `0x1fbe70`): `Hide` ORs 2, `UnHide` clears 2, `Activate` ORs 1 and picks its event on `== 1`, `ForceVisible` clears 6; the scene view behind it sits at `+0xc0` / `+0xb8` (the `UnToggleOff` bodies read it through the HUD panel) |

`toggledOff 0x3d4 / 0x374` (#543's row) is what the gate reads for the H key now; the raw `0x3d4` it read
before was wrong on Windows.

### The head-tracking rows

One function pair carries all five: `LookAtAnimation::Control` (Linux @`0x4973c0`, Windows rva `0x50650`,
name-exported as `?Control@LookAtAnimation@@UEAAXXZ`) reads every field, and the constructor (Linux
@`0x49d480`, Windows rva `0x4d0a0`, exported) writes them in the same order on both builds. The class is
laid out identically end to end, which is what `Same` is for.

| Class | Rows | Anchor |
|---|---|---|
| `LookAtAnimation` | `finished 0x08`, `gob 0x30`, `headPart 0x40`, `targetKey 0x58` | both bodies, side by side: the finished byte gates the aim block against the release latch at `+0x68`, `+0x30` is the looker's gob the distance check reads `+0xa8` through, `+0x40` is the head part `Part::GetWorld` is called on and whose local rotation (`+0x1c`) goes into the first keyframe, and `+0x58` is the `float*` both builds write as `(x, y, z, w)`. The clamps at `+0x48`/`+0x4c` and the reach at `+0x54` pair too but are not read from this side |
| `CNWCCreature` | `vtbl.GetModel 0x148 / 0x140` (probed) | `CNWCCreature::LookAt` (Linux @`0x726890`, Windows rva `0x803f20`) calls it four times on each build -- `+0x148` there, `+0x140` here, the -8 a virtual destructor costs MSVC (#433). It answers the creature's Gob (`*(animBase + 0xc8)`, the `CNWCAnimBase.gob` row (`hiliteGob` until #672)), so head tracking needs no `CNWCCreature+0x78` row of its own |

### The action-camera targeting rows

Aim assist, auto-target and the contextual crosshair read fifteen fields, and **four functions carry all of
them** -- each decompiled on both programs rather than shifted by a class delta
(the research on CX-125):

| function | Linux | Windows RVA |
|---|---|---|
| `CNWCArea::HiliteAreaObjects(int)` | `0x7119f0` | `0x7db2f0` |
| `CClientExoAppInternal::UpdateMouseCursorAppearance(int)` | `0x6629b0` | `0x6f2830` |
| `CActionTarget::HandleLMouseDown()` | `0x965840` | `0x83be00` |
| `CCachedMouseCursorManager::SetMouseCursor(int,int,int,int,int)` | `0x6b72a0` | `0x83b3e0` |

| Class | Rows | Anchor |
|---|---|---|
| `CNWCObject` | `visible 0xa8` (a **byte**) | the highlight walk's first filter: Linux `0x711a6d` is `cmpb $0x0,0xa8(%rax)` and the Windows body reads `(char)obj[0x15]` -- one byte on both, **not** a model pointer. `CNWCObject::FadeObject` (`0x7e7720`) is the writer: it eases the alpha toward the desired state at `+0xa9` and stores 0/1 here on arrival, so 1 = faded in. Reading it as a pointer would fold `+0xa9` in and call a fading-out object visible |
| `CNWCObject` | `highlightable 0x11c` | the same walk, `Same` on both. The type-5/6/0xa arms test it on the CNWCObject; the **type-9 (placeable) arm does not** -- see the vtable row below |
| `CNWCArea` | `objectIds 0x318`, `objectCount 0x320` | the walk's loop header: `*(uint*)(*(area+0x318) + i*4)` for `*(int*)(area+0x320)` entries, identical on both |
| `CGameObject` | `vtbl.AsNWCObject 0x20 / 0x18` (probed) | what the walk calls on every id it resolves; the usual Itanium two-destructor-slot shift |
| `CGameObject` | `vtbl.AsNWCPlaceable 0xa8 / 0xa0` (probed) | the placeable arm reads `highlightable` through it: Linux `0x711b17` calls `vtbl+0xa8` and tests `+0x11c` on the ANSWER, Windows `vtbl+0xa0`. The base returns null, `CNWCPlaceable`'s override is an identity `return this` (`0x7ecc20`), so it is a type test that hands the same address back -- mirrored so a non-placeable carrying type byte 9 cannot slip through |
| `CNWCCreature` | `hoveredOid 0x448 / 0x428` | `UpdateMouseCursorAppearance` is the only writer: it stores the newly picked id on the PLAYER creature and puts `0x7f000000` back when the hover drops. The same -0x20 `CNWCCreature` delta the nameplate rows carry |
| `CClientExoAppInternal` | `cursorManager 0xc8` | every `SetMouseCursor` call site in that function passes `*(internal + 0xc8)`, on both |
| `CClientExoAppInternal` | `actionTarget 0x108` | CX-636: `RegisterActionTargetGUI` is a one-line store on both (Linux `0x645530`, rva `0x6de8e0`, reached from `ActivateTargetMode` `0x83b9e0` -> `CClientExoApp::RegisterActionTargetGUI` `0x6ac650`); `Cleanup` stores null through the same call |
| `CNWCCreature` | `attitude 0x3a8 / 0x388` | CX-636: the three-way enemy/friend/neutral colour branch of `HiliteAreaObjects` on both (Linux `0x7119f0` reads `+0x3a8`, Windows rva `0x7db2f0` reads `+0x388`); also the `< 2` talk-or-attack test of `PerformDefaultActionOnCreature` |
| `CNWCObject` | `hiliteColour 0x10c`, `id 0x08` | CX-636: every `HiliteAreaObjects` branch tests `*(float*)(obj+0x10c) < 0` before the type global, on both; `obj+8` is the OID every sender passes |
| `CNWCObject` | `vtbl.Hilite 0x160 / 0x158`, `vtbl.UnHilite 0x168 / 0x160` | CX-636: `HiliteAreaObjects` calls them on every arm (Linux `call *0x160(%rax)` with `(xmm0/xmm1 colour, esi 2, xmm2 0.0, edx 1)`, `*0x168` with `(this, 2)`; Windows `+0x158` with `(this, &colour, 2, xmm3, 1)`, `+0x160` with `(this, 2)`). The Vector is an ARGUMENT by value (SysV two SSE eightbytes, Win64 a pointer to a copy), not a return: no sret order |
| `CCachedMouseCursorManager` | `currentCursor 0x1cb0` | `SetMouseCursor`'s own early-out and store, under the `id < 0x126` ceiling; `Same` |
| `CActionTarget` | `allowed 0x148 / 0x108`, `allowedCount 0x150 / 0x110` | `HandleLMouseDown` scans pairs of `{objectType, cursorId}` (stride 8) there on both; MSVC packs the `CGuiCallback` members in 16 bytes where Itanium needs 24, so everything past `+0x80` shifts |

### The crosshair-feedback rows

"What is the player doing right now", measured the same way (the research on CX-123):

| Class | Rows | Anchor |
|---|---|---|
| `CGuiInGame` | `actionQueuePanel 0xa8` | both `HandleServerToPlayerUpdate_PlayerInfo` bodies (Linux `0x8a9f90`, Windows rva `0x7b4f70`) null-check `GetInGameGui()+0xa8` and hand exactly that to `ClearAllActions` / `AddAction` |
| `CPanelActionQueue` | `current 0x158 / 0x150`, `queuedCount 0x100 / 0xf8` | `AddAction` (Linux `0x7761b0`, rva `0x873df0`) fills the Current slot first and only then overflows into the list; `ClearAllActions` / `DeleteAction` (Linux `0x776100` / `0x776020`, rva `0x874670` / `0x8748b0`) drain `while (0 < count)` and early-out on `count < 1` |
| `CNWCCreature` | `aiState 0x39e / 0x37e`, `aiTarget 0x3a4 / 0x384` | two independent call sites per build: `WalkPlayerToPoint` (Linux `0x763950`, rva `0x6f3650`) gates its "already close to my target" test on `aiState == 2` and resolves `GetGameObject(aiTarget)` inside that branch, and `CPanelQuickBar::UpdateAttackButtons` paints IR_DISENGAGE on the same `== 2`. Only **2 = attacking** is established; the rest of the enumeration is not |

### The 38 DecompOne rows (#448)

Section 3, T2 — the biggest and most mechanical of the three. All 38 are now
`Diverges` or `Same`: **39 unresolved → 1**, the one left being #447's
`poolTableAnchorOffset`, which wants a symbol anchor rather than a value. 308 rows in
the table. Measured 2026-09-12 against build `5fe6e02d…`.

The ticket sorted the 38 into three shapes — a measured class with the row above its
measured run (D), a class that pairs on nothing by tool (C), and a record nothing takes
as `this` (B). **All three collapsed into the same instrument**: name the one function
that touches the field, `DecompOne` it on both builds, read the offset. Three headless
runs covered everything — 32 functions, then 9, then 4 — plus one `FieldDump` cycle
over six classes for the constructor store runs.

| rows | verdict | the function that measured them |
|---|---|---|
| `Scene` `drawBuckets` `0x208`, `envMap` `0x1e8` | `Same` | `Scene::Scene` constructs `DrawBucketManager` in place at `this+0x208` on both, with an identical `0x178..0x1f8` store run below it; `Scene::Render` assigns the `SafePointer<CAurTexture>` at `0x1e8` from `GetEnvMapResRef` on both |
| `VertexPrimitive` `matrix` `0x18`, `flags` `0x80`; `MdlNode` `primType` `0x270` | `Same` | `Scene::RenderDrawBucket` reads `part+0x88` (the part's `VertexPrimitive`) into the register it then derefs -- `& 0x1e` on `+0x80`, `+0x18` handed to `SetModelMatrix` -- and takes the `MdlNode` at `part+0x8` separately two instructions later; `RenderFlat` passes `node+0x270` as argument 3 of `RenderVertexData`. CX-39 re-keyed the first two from `MdlNode` to `VertexPrimitive` with the values unchanged, which also retires the `0x80` collision with `MdlNode.controllerCount` |
| `MdlNodeSkin` `bindRot` `0x2f0`, `bindPos` `0x300` | `Same` | `PartSkin::PostProcess` — the gather loop adds to both lists at those offsets |
| `MdlNodeSkin` `boneMap` `0x320` | `Same` | `ExecuteSkinGPU`, a **free function**: 64 iterations, stride 2, from `node+800` |
| `MdlNodeDanglyMesh` ×5 (`0x2c8`…`0x2e0`) | `Same` | `MdlNodeDanglyMesh::InternalParseField`, each field pinned by its own `.mdl` keyword string; `PartDanglyMesh::Animate` reads all four back. `constraintCount` is the `List<T>`'s own `+8`, measured on `ExecuteSkinGPU`'s typed `List<Vector>*` parameter on both builds |
| `PartDanglyMesh` `prevPos`, `prevRot` | `Diverges` `0xb4`/`0xc0` → `0xc0`/`0xcc` | `PartDanglyMesh::Animate` — same three-float read, same x/y/z-against-zero test, same store-back |
| `VertexPrimitive` `vdo` `0x58`, `item` `0x60` | `Same` | `RenderFlat` passes both as the `RenderVertexData` arguments; `Animate` reads the pair again |
| `VertexPrimitiveDangly` `velocity` `0xa0` | `Same` | `Animate` null-checks it, calls `GenerateVelocityArray`, then indexes it |
| `BSPNode` `data` `0x70`, `triMesh` `0x20`, `triMeshCount` `0x28` | `Same` | `SceneBSPAddPart` pairs on its whole body — seven bucket writes, same sub-offsets. The count is `CExoArrayList`'s own `+8`, read once per build on a container the decompiler types: `GetLensFlareLights`' list on Linux, `CExoArrayList<Vector>::Allocate` at `node+0x300` on Windows |
| `TileData` `fade` `0x70`, `pendingFade` `0x74` | `Same` | `ManageSceneBSP`'s commit is one statement, `tile+0x70 = tile+0x74`, in the same loop over `scene+0x1a8` |
| `DrawBucket` `stride` `0x10` | `Same` | `GetDrawBucketByItemType` and `AddPartToBucket`, one line each, same `*0x10` |
| `CAurBehaviorCameraFollow` `target` `0x20` | `Same` | `GetTargetWorldPosition`, one line each |
| `MdlNodeLight` ×5 (flares + `fadingLight`) | `Diverges`, **two segments** | `Scene::RenderLensFlares`: radius `0x9c`→`0xa0` (+4), count/sizes/shifts `0xa8`/`0xc0`/`0xd0`→`0xb0`/`0xc8`/`0xd8` (+8). CX-85 read `fadingLight 0x108`→`0x110` off `LightManager::ControlLightsFade` (rva `0x7ddb0`), the same upper segment — the fifth row on a class that pairs on none of its own nine functions |
| `MdlNodeEmitter` `noCull` | `Diverges` `0x164` → `0x168` | `PartEmitter::RenderEmitter` and `RenderStandard` both branch on it into `Enable(3)`/`Disable(3)` |
| `Model` `ownerFlags` | `Diverges` `0x8e` → `0x92` | `PartTriMesh::Mat`'s `& 8` test on `gob+0x68`; the class pinned by `Model::Model`'s store run and `Model::AddRef`'s count (`0x90`→`0x94`) |
| `PartTriMesh` `selfIllum` | `Diverges` `0x7c` → `0x80` | `PartTriMesh::Mat` loads three floats into the emission scratch and hands them to `SetMaterialv(0, 3)` |
| `VertexDataObject` `vao` `0xc` | `Same` | `GLRender::SetVertexDataObject`: `glGenVertexArrays` when `vdo+0xc` is zero, store back, bind |
| `MaxFace` ×4 (`0x1a`/`0x1c`/`0x1e`, stride `0x20`) | `Same` | `MdlNodeTriMesh::BuildVertexIndices` walks `node+0xb0` in `0x20` steps, three `u16`s per face |
| `VertexPool` `stride` `0xc` | `Same` | the tangent-basis worker locks `node+0x280` and indexes `* 0xc` on both, and resizes to twelve bytes a vertex on each — Linux `count * 0xc`, Windows the strength-reduced `(count + count * 2) * 4` |
| `GLRender::PROG_FIXED` `programId` `0x64` | `Same` | `GLRender::SetProgram` hands `prog+0x64` to `glUseProgram` |
| `CExoMoviePlayerInternal` `texture` `0x78` | `Same` | CX-99. `CExoMoviePlayer::GetTexture` is the same two loads on both — Windows export `?GetTexture@CExoMoviePlayer@@QEBAIXZ` (rva `0x2321a0`) is `48 8b 01  8b 40 78  c3`, Linux @0x1165930 `mov rax,[rdi]; mov eax,[rax+0x78]`. `GetWidth` `0x68` and `GetHeight` `0x6c` pair on the same build and are also identical (read, not needed: `ShowFrame` takes both as arguments) |

Five findings the rest of the port inherits:

- **"No validated pair" was never a property of the binary, and neither is "no method
  of its own".** Sixteen Kind-C rows were filed because `field-diff.py` paired the class
  on nothing; every one of them is measured by a function that takes some *other* class
  as `this` — `SceneBSPAddPart` for `BSPNode`, `ManageSceneBSP` for `TileData`,
  `Scene::RenderLensFlares` for `MdlNodeLight`, `PartDanglyMesh::Animate` for
  `VertexPrimitive`. **The unit that pairs is the FUNCTION, not the class.** Kind B
  (records nothing takes as `this`) is the same observation one step further, and it is
  the shape that reached `MaxFace`, `DrawBucket` and `PROG_FIXED`.
- **The three rows the ticket said only #431's unbuilt call-result lever could reach —
  `VertexDataObject.vao`, `PartDanglyMesh.prevPos`/`prevRot` — all came off a named
  function.** The lever stays unbuilt; nothing now wants it.
- **A gap between two segments can be measured rather than interpolated, and then it
  explains itself.** `PartTriMesh.selfIllum` is `+0x4` with the bbox segment; the extra
  four bytes the `+0x8` boundary adds are the **alignment pad** Windows needs between a
  float block ending at `0x8c` and the 8-aligned `node` pointer at `0x90`. The
  prohibition on interpolating stands — what is new is that the boundary itself is
  readable, so a gap row is a *waypoint*, not an exit.
- **Base tail padding is visible in a constructor's initializer VALUES.**
  `VertexDataObject`'s two ctors write the same `3` and `-1` pattern four bytes apart
  from Linux `0x14` up and not below it, which places the boundary exactly at the end of
  `VertexDataObjectBase` — Itanium reuses its tail padding, MSVC rounds the base to its
  alignment. That is #430's mechanism caught in the act, and it is why `vao` at `0xc` is
  `Same` while everything above it in the same class is `+4`.
- **A one-line accessor is a whole measurement.** `GetTargetWorldPosition`,
  `GetDrawBucketByItemType` and `AddPartToBucket` are single expressions on both builds;
  they need no corroboration and they cost one `DecompOne` each.

**No probes.** `Expect.Byte01` fits none of the 38 — the two flag-ish rows are a flags
byte read with `& 8` (`Model.ownerFlags`) and an `int` compared to zero
(`MdlNodeEmitter.noCull`), neither of which a first-sighting `0-or-1` check would
distinguish from a wrong offset — and none of the 38 is a vtable slot. Bar item 3 is
"a probe **where a predicate exists**"; here none does, and saying so is the reading.

**No plaza A/B is owed.** `Same` returns the Linux value unchanged and `Diverges`
returns its `linux:` arm there, so the Linux build is bit-identical: verified
mechanically — every one of the 309 rows resolves to the same Linux value before and
after, and `run-demo.sh` reports `308 offset(s), 1 unresolved`. Alloy still stands down
under Proton, so not one of the 38 is read on Windows yet; that is the map's standing
fog entry, not this ticket's.

### The static-placeable rows (#672, research #663)

Three `Same` rows and one rename, all read off decompiled bodies on both builds
(the research on CX-170 §7.2 has the full table, including the
`Gob` transform fields it chose *not* to declare).

| Row | Linux / Windows | Anchor |
|---|---|---|
| `CNWCArea.staticPlaceables` | `0x338` / `0x338` | `AddPlaceableObjectStatic` (Linux `0x7091c0`, Windows rva `0x7d95a0`, the call inside `LoadArea`'s `"Static Placeables"` tracy zone): `CExoArrayList<unsigned int>::Add((… *)(area + 0x338), id)` on both |
| `CNWCArea.staticPlaceableCount` | `0x340` / `0x340` | the research left this one **inferred** (the Windows `RespawnStaticPlaceablesForTile` was never located). Closed here without a live probe: the Windows `CExoArrayList<unsigned int>::Add` (rva `0x8ae10`, exported `?Add@?$CExoArrayList@I@@QEAAXI@Z`) decompiles as data `+0`, count `+8`, alloc `+0xc` -- the same layout Linux has -- so the count of the list at `+0x338` is at `+0x340` by the list's own class, not by analogy |
| `CNWCObject.animBase` | `0x78` / `0x78` | `CNWCObject::SetPosition` (Linux `0x7e9180`, Windows rva `0x766760`): the object's word `0xf` (`+0x78`) then the anim-base vcall |
| `CNWCAnimBase.gob` (was `hiliteGob`) | `0xc8` / `0xc8` | already `Same` from #446; re-read off `CNWCAnimBase::SetPosition` (Windows rva `0x9680a0`, the pointer at `+200`). Renamed because it is the anim base's one `Gob*`, which the highlight, head tracking and now the static-placeable walk all read |

Not declared: the `Gob` visual-transform block (`+0x1c0`/`+0x280`/`+0x340`/`+0x400`,
`+0x49c`) and the orientation quaternion at `+0xb4`. The rule the research settles on
reads the composed 16 floats at `MdlNode+0x18` (`NodeMatrix`, already a row) off the
enrolled part, so those rows have no reader yet.

**No probes.** Two pointers and a `CExoArrayList` pair; `Expect.Byte01` fits none.
`CNWCPlaceable+0x1b4` (`0x194` Windows) is **plot**, not Static -- there is no
client-side Static flag on a placeable, which is why the list exists.

### The step-guard rows (CX-682)

`CNWCCreature::WalkUpdateLocation` decompiled on both builds (Linux @`0x725830`, Windows rva
`0x8114d0`, `docs/windows-symbols.md`) and read side by side; the `TestDirectLine` argument pair off
`ClientSideWalkCommand`'s disassembly (Linux `0x7203ea..0x72043e`) and the Windows `WalkPlayerToPoint`.

| Class | Rows | Anchor |
|---|---|---|
| `CNWCCreature` | `desiredPos 0x2e0 / 0x2c0` | the store right after the second `WalkUpdateLocationDistance` on both (the store to word `0x58` on Windows), and the origin `WalkUpdateLocationDistance` steps from |
| `CNWCCreature` | `driving 0x318 / 0x2f8` | the `!= 0` branch before the animation bookkeeping and the `= 0` at the walk's end, both builds (word `0x5f`). Not probed: no ctor zero found |
| `CNWCCreature` | `pathfind 0x320 / 0x300` | the pointer whose `+0xcc` ushort indexes `creaturespeed.2da` in every `GetFLOATEntry` call of the function; `WalkPlayerToPoint` reads the same pointer at `creature+0x300` for the line test |
| `CPathfindInformation` | `spaceArgs 0xe0` (two floats) | `xmm4 = [pf+0xe0]`, `xmm5 = [pf+0xe4]` before Linux `call TestDirectLine` @`0x72043e`; Windows `WalkPlayerToPoint` passes `*(pf+0xe0), *(pf+0xe4)` in the same slots |
| `CNWCCreature` | `vtbl.SetPosition 0x130 / 0x128` (probed in-image) | `MoveCreatureToPosition` Linux @`0x71cd70` calls `+0x130`, Windows rva `0x804840` calls `+0x128`; the Linux vtable (`_ZTV12CNWCCreature+0x10+0x130`) resolves to `CNWCCreature::SetPosition(Vector)` |


### The radial rows (CX-689)

The `CRadialMenu` surface is name-exported on Windows, so every base-class row was read off
the same function on both builds: the ctor (Linux @`0x583460`, Windows rva `0x1f7950`),
`SetEntryx` (`0x5837f0` / `0x1fa250`), `SetSubMenuCreatorx` (`0x583f50` / `0x1fa9e0`),
`SetSubMenu` (`0x583b20` / `0x1fa490`), `CreateSubMenu` (`0x584790` / `0x1f8960`),
`DisableEntry` (`0x5840d0` / `0x1f8b30`), `HandleMenuAction` (`0x585530` / `0x1f9350`),
`RecursiveAutoDelete` (`0x583680` / `0x1f9da0`), `Activate` (`0x585400` / `0x1f7e80`) and
`BuildIcon` (`0x5831d0` / `0x1f8400`). The research note's §1 Windows column survived every
body unchanged. The subclasses are not exported; each ctor was found as the function that
stores the class's `vftable` label (`Callers.java` on the `At` rows in `SymbolMap`) and paired
with the Linux ctor of the same signature.

| Class | Rows | Anchor |
|---|---|---|
| `CRadialMenu` | `text 0xf8` (Same) | `SetEntryx`: `this + i*8 + 0xf8` on both, the `CGuiTextBubble` it `new`s (`0x118` bytes on Windows) |
| `CRadialMenu` | `entry 0x138` (Same) | the ctor's first loop: 8 × `CGuiCallbackl` ctor stepping `0x18` / `0x10` from `0x138` |
| `CGuiCallback` | `obj 0x0` (Same), `fn 0x8` (Same), `stride 0x18 / 0x10` | `SetEntryx` stores `obj` at `+0x138+i*stride`, `fn` at `+0x140+i*stride`; Itanium's pair carries a this-adjust word after `fn`, MSVC's does not |
| `CRadialMenu` | `param 0x1f8 / 0x1b8` | `SetEntryx` and `SetSubMenuCreatorx` both store the `long` at `i*8` from it; `CreateSubMenu` and `HandleMenuAction` pass it as the callback's argument |
| `CRadialMenu` | `flags 0x238 / 0x1f8` | `DisableEntry` ORs 1 into `i*4` from it (with the `nMenuPosition < RM_NUM` warning at 8 on both); `HandleMenuAction` and `BuildIcon` test the same bit |
| `CRadialMenu` | `creator 0x258 / 0x218` | the ctor's second loop (8 × `CGuiCallbackpRadialMenu_l` from `0x218`); `SetSubMenuCreatorx` stores `obj`/`fn` at `+0x218`/`+0x220`; `CreateSubMenu`'s self-pointer sentinel test |
| `CRadialMenu` | `subMenu 0x318 / 0x298` | `CreateSubMenu` early-out and store (`(i + 0x53) * 8`); `RecursiveAutoDelete` walks 8 from it; `SetSubMenu` stores its argument there |
| `CRadialMenu` | `parent 0x358 / 0x2d8` | `HandleMenuAction` writes `child + 0x2d8 = this` on descent and reads `this + 0x2d8` in the centre-cancel arm |
| `CRadialMenu` | `icon 0x760 / 0x6e0` | `SetEntryx` / `SetSubMenu` store the `CGuiIcon` (`0x48` bytes on both) at `i*8` from it; `BuildIcon` reads it |
| `CRadialMenu` | `menuX/Y 0x85c/0x860 / 0x7dc/0x7e0` | `Activate`: `CGuiMan::GetMousePos(g_pGuiMan, this+0x7dc, this+0x7e0)`; `HandleMenuAction` copies the origin pair (`0x7e8`/`0x7ec`) into the child's |
| `CRadialMenu` | `autoDelete 0x871 / 0x7f1` (asserted == 1 on the first root, no `Expect`) | the ctor zeroes the byte, `RecursiveAutoDelete` tests it; the creature, placeable and door ctors set it 1 on both builds, `CGuiItemMenu::ActivateRadialMenu` does it for the item root |
| `CRadialMenu` | `refCount 0x874 / 0x7f4` | `CreateSubMenu` and `SetSubMenu` `++` the child's; `RecursiveAutoDelete` `--` and virtual-deletes at `< 1` |
| `CRadialMenu` | **sizeof** `0x878 / 0x7f8` (not a row) | each build's scalar deleting destructor: Linux `operator_delete(this, 0x878)` @`0x583120`, Windows `FUN_1401f7d40` (the third `vftable` writer beside ctor and dtor) ends in the delete of `0x7f8` bytes |
| `CRadialMenu` subclasses | `targetId 0x878 / 0x7f8` | `CGuiCreatureMenu` (both ctors: `@0x73f2e0` / rva `0x98e070` store the first `uint` argument; `@0x73f110` / `0x98e140` store `creature+0x8`), `CGuiPlaceableMenu` (`@0x7ecf70` / `0x9b1950`), `CGuiDoorMenu` (`@0x756d00` / `0x9b0060`), `CClientTrapMenu` (`@0x9c7660` / `0x9b11d0`): the `uint` argument's store, first thing after the base ctor. `CGuiItemMenu`'s no-arg ctor (`@0x7d1e70` / `0x9a5940`) seeds `0x7f000000` there and `ActivateRadialMenu` (`@0x7d3720` / `0x9a59d0`) stores the item id. One row because every pair landed on the same offset; each pair is its own measurement |
| `CClientSpellMenu` | `targetId 0x90c / 0x88c` | the ctor pair (`@0x6f1650` / rva `0x9a10e0`): the `uint` argument's store after the target `Vector` (`+0x900` / `+0x880`, `+0x908` / `+0x888`) and before the `CGuiQuickButton*` (`+0x910` / `+0x890`). Role pinned by `RadialMenuCast` (`@0x6efb10` / `0x9a4540`), which passes it as `PerformCastSpell`'s target on both |
| `CGuiIcon` | `resref 0x8` (Same) | `Initialize` (`@0x58cc20` / rva `0x1e4430`): `CResRef::operator=(this+8, arg)` then the three size ints at `+0x1c/+0x20/+0x24` on both; `ReplacePrimaryIconTexture` (`@0x58cd30` / `0x1e4690`) copies the string out of `+0x8` |
| `CGuiIcon` | `countLabel 0x40` (Same) | `SetCountText` (`@0x58d130` / rva `0x1e4720`, CX-690): loads `this+0x40`, virtual-deletes it on an empty string, else feeds it through the CAurString vtable or stores `CreateCountLabel(text)` there (`@0x58ce80`: `NewCAurString(1)` + `SetText`), then `AttachCountLabel(this, this+0x30 model, label)` -- same shape on both builds |
| `CLayeredIcon` | `schemeCount 0x48`, `scheme 0x50` (Same) | the ctor pair (`@0x58dcb0` / rva `0x1e3000`): `int` count stored, `new ushort[count]` copied in; `CreateCopyIconModel` (`@0x58c8b0` / `0x1e3c80`) hands both to the PLT `ReplaceTexture` vcall (CX-693) |
| `CCompositeIcon` | `resref2 0x48`, `resref3 0x59` (Same) | the ctor pair (`@0x58d9d0` / `0x1e2e40`): three `CResRef::operator=` at `+0x8`, `+0x48`, `+0x59`; `CreateCopyIconModel` (`@0x58d320` / `0x1e3930`) binds them to `gui_replace01/02/03` in that order |
| `CArmorIcon` | `resrefs 0x48` (×6, stride `0x11`), `schemeCount 0xb0`, `scheme 0xb8`, `layerSchemes 0xc0` (Same) | the ctor pair (`@0x58d5c0` / `0x1e2ca0`): zero loop to `+0xae`, count at `+0xb0`, `new[count]` at `+0xb8`, optional `new[count*6]` at `+0xc0`, then the six 17-byte copies; `LoadArmorLayeredIconModel` (`@0x58d7c0` / `0x1e4470`) indexes `+0xc0` by `count * compactedLayer * 2` |
| `CNWCItem` | `guiIcon 0x258 / 0x238` | `CGuiItemMenu::ActivateRadialMenu` (`@0x7d3720` / `0x9a59d0`): `SetCenter(*(item + off), 2)` right after the target-id store; `CNWCItem::UpdateIcons` (`@0x7cbc50`) is the Linux writer (virtual-deletes the old, stores the `new`) |
| `CNWCItem` | `vtbl.Delete 0x8 / 0x0` | the primary vtable's deleting destructor: Itanium D0 `_ZN8CNWCItemD0Ev` after the complete D1 (`tools/abi/vtable-slots.py CNWCItem`); MSVC's scalar deleting destructor, the one-slot primary vftable's only entry (rva `0xe4ceb0` -> `0x83c8a0`, which tests `flags & 1` before `operator delete(this, 0x2b8)`). Different argument lists, so `NuiItemIcons.Destroy` calls it per OS |
| `CGameObject` | `type 0xc` (Same), `vtbl.AsNWCItem 0x70 / 0x68` | `CClientExoAppInternal::GetItemByGameObjectID` (`@0x63f3a0` / rva `0x6d82a0`): `GetGameObject` into a local, `go[0xc] == 6`, then the vcall -- identical bodies, the slot one destructor-slot apart |

The subclass delta is `-0x80` at every field read, not just the target id -- which is the
base's own size delta (`0x878 - 0x7f8`) with nothing added, because MSVC puts the subclass
fields at `sizeof(base)` on every one of these classes. Read but **not declared** (no reader
yet, the #672 rule): `CGuiCreatureMenu` associate master `+0x884 / +0x804`, associate type
`+0x888 / +0x808`, portrait `CResRef +0x895 / +0x815`, quickbutton `+0x8a8 / +0x828`, target
`Vector +0x8b0 / +0x830`; `CGuiItemMenu` quickbutton `+0x880 / +0x800`; `CClientSpellMenu`
`Vector +0x900 / +0x880`, quickbutton `+0x910 / +0x890`; `CRadialSpellMenu`'s four per-slot
int arrays `+0x878.. / +0x7f8..`. Windows sizes off the deleting destructors: `CGuiItemMenu
0x808`, `CClientSpellMenu 0x8a0`, `CClientTrapMenu` / `CGuiQuickbarMenu` / `CGuiChatWindowMenu`
`0x800`.

The two ABI facts the research note asserted are confirmed by the bodies. The Windows
callback invoke is a plain two-word call with no adjust word and no virtual-thunk decode --
`CreateSubMenu` calls the function pointer at `+0x220` with the object at `+0x218` and the
entry at `+0x1b8`, and `HandleMenuAction` calls the pointer at `+0x140` (stride `0x10`) with the
object at `+0x138` and the same `+0x1b8` entry, where Linux reads `obj + adj`, tests `fn & 1` and indexes the vtable first. Both keep the
`s_pCurrentEventObject` bracket. `GetCountText_FirstLineOnly` shows the sret order in its own
decompile: Linux writes through `RDI` with `this` in `RSI`, Windows writes through `RDX`
with `this` in `RCX`. The creature ctors call `GetPortrait` through vtable `+0x1d8` / `+0x1d0`,
the same pair `docs/windows-symbols.md` already lists.

**One probe.** `CGameObject.vtbl.AsNWCItem` is a `VtblSlot` (CX-693). `autoDelete` carries no
`Expect`: the ctor never writes its two neighbours, so the dword a `Byte01` probe reads is heap
garbage -- `RadialService` asserts the byte itself on the first root instead. An object id and
a `CResRef` fit neither predicate. The `CNWCObject` slot `+0x150` (`ActivateRadialMenu`,
research §4) is not a row: CX never calls it.

### The foot-placement rows (CX-29)

`Gob::Animate` decompiled on both builds (Linux @`0x49d8c0`, Windows @`0x14004dbd0`) and read side by side.

| Class | Rows | Anchor |
|---|---|---|
| `Gob` | `scene 0x88 / 0x88` | `this+0x88` then `+0x160` (the wind manager) before `AddPointSourceWind`, both builds; `Scene::Animate` animates every gob of `Scene+0x130` with `currentscene` set to that scene |
| `Gob` | `animRuns 0xe0`, `animRunCount 0xe8` (both builds) | the `CExoArrayList<AnimRun*>` whose last entry gets `sprintf("start%s", ...)`; `DelIndex` on `this+0xe0` for the look-at run |
| `AnimRun` | `animation 0x0` | `**(runs + (n-1)*8) + 0x10` is the name argument, i.e. the run's first word is the `Animation*` |
| `Animation` | `name 0x10` (inline `char[]`) | the `"start%s"`, `"loop%s"`, `"done%s"` arguments, both builds |

**No probes.** Pointers, a count and a string; `Expect.Byte01` fits none.

### The pose-layer rows (stride, aim, hit reactions)

Read from the decompiles in the research on CX-159 and `procedural-anim-combat.md`.

| Class | Rows | Anchor |
|---|---|---|
| `AnimRun` | `rate 0x8`, `flags 0xc`, `time 0x14`, `blend 0x18`, `blendTime 0x1c` (both builds) | `Gob::Animate`: `time += dt * rate`, `blend += dt / blendDur`; `Gob::PlayAnimation` writes `rate` = `Lerped(Gob+0x400) * param2` on both |
| `Gob` | `externalData 0x490 / 0x490` | `Gob::GetExternalData` (Linux @`0x444700`, Windows rva `0x58d00`) is one read of `+0x490`, beside `externalType` |
| `CNWCCreature` | `lockTarget 0x3c8 / 0x3a8` | the id `SetDesiredOrientationForLock` resolves to face the victim; the usual `-0x20` above `0x300` |
| `CNWCCombatAttackData` | `attacker 0x4`, `damage 0xa`, `result 0xc` (both builds) | the creature update handler's wire order; `SpawnHitVisuals` compares `+0xa` against max HP / 10, `SwingHitEvent` resolves `+0x4` as the attacker |

**No probes.** An id, a signed short, a byte and two floats; the attack-data class has no field with a known
ctor value, so its rows rest on the wire order alone.

### The last private-const rows (CX-40)

Three files still read engine memory through their own `private const int` / `IsWindows() ? win : linux`
pairs: `NameplateNative`, `HotkeyConsent` and `PoseService`. All 21 are rows now. Half of them the
`#420`/`#446` tables had already measured; each was **re-read** here, by decompiling the same function on
both programs of `$GHIDRA_PROJECT/nwn-linux.rep` (`tools/ghidra/DecompOne.java`), because a table entry is a prior
measurement and not a substitute for one. Linux addresses are `nm` (Ghidra shows `nm + 0x100000`), Windows
are RVAs off the `0x140000000` image base.

Nine of the constants were already rows under another name and are simply gone: `ObjId`,
`ObjPosition`, `CreatureAttitude` (the nameplate's "reaction"), `CreatureStats`, `ModuleCurrentArea`,
`ModuleCamera`, `CamPos`, `CamRot` and `ExoAppInternal`. Every one agreed with the constant it replaced;
`CamViewW` / `CamViewH` also turned out to be the third and fourth float of the viewport rect, so that rect
needed two new rows rather than four.

| Class | Rows | Anchor |
|---|---|---|
| `CNWCObject` | `bubble 0xb8`, `retired 0x104` (both builds) | `CNWCObject::SetPopUpBubbleText` (Linux nm `0x7e8050`, Windows rva `0x766450`): `*(int*)(this + 0x104) == 1` is the first test in each body, then `this[0x17]` is read and `new`ed into (`operator new` `0x160` Linux, `0x158` Windows) |
| `CNWCObject` | `overrideMode 0x124`, `overrideText 0x128`, `overrideLen 0x130` (both builds) | `CNWCCreature::ShowFeedbackBubbleText` (Linux nm `0x71a800`, Windows rva `0x80b240`) reads the triple in the same order on both; `CNWCItem::ShowFeedbackBubbleText` reads the SAME triple through the base subobject (Linux nm `0x7cea90` at `item+0x16c/0x170/0x178`, Windows rva `0x83f230` at `0x124/0x128/0x130` of the subobject it is handed) |
| `CNWCObject` | `itemPlated 0x284` / `0x264` | CNWCItem's flag, measured **from the CNWCObject base subobject** — the only item pointer CX holds, which is why the row's class is the subobject's and not the declaring `CNWCItem`. The base is `CNWCItem+0x48` on both (Linux's `lea 0x48(%rbx)` into `SetPopUpBubbleText` and the `_ZThn72_` thunk's `sub $0x48,%rdi`; Windows' `this-0x48` for `CNWCItem::GetName`), so Linux's `item+0x2cc` is `base+0x284` and MSVC's `0x264` is already base-relative. First store in each body |
| `CNWCCreature` | `area 0x478` / `0x458` | `CNWCCreature::UpdatePopUpBubble` (Linux nm `0x72e600`, Windows rva `0x8107a0`): two reads per build, `this+area == module[currentArea][id]`. The class's usual Windows `-0x20` |
| `CNWCArea` | `id 0x118` (both builds) | the right-hand side of that same comparison |
| `CNWCCreatureStats` | `overrideName 0x40`, `overrideNameLen 0x48`, `damage 0x8e` (both builds) | `CNWCCreatureStats::GetFullName` (Linux nm `0x74db80`, Windows rva `0x7f3f00`) tests `+0x48` first and returns `+0x40` alone when it is non-zero — the "a disguise wins outright" rule, same shape and same order on both. The damage byte is `*(byte*)(stats + 0x8e)` in `ShowFeedbackBubbleText`, picking the injury line's `damagelevels.2da` colour |
| `CNWCCreatureStats` | `curHp 0x88`, `tempHp 0x86`, `maxHp 0x8a` (both builds) | `HandleServerToPlayerCreatureUpdate_Update` (Linux nm `0x7a1550`, Windows rva `0x781e80`), update flag `0x400`: four `ReadSHORT`s stored through `creature->stats` (Linux `+0x3d0`, Windows `plVar[0x76]` = `+0x3b0`) at `+0x88 / +0x84 / +0x86 / +0x8a` in the same order on both, next to `CPanelPartyBar::SetMemberHealth(id, cur + temp, max)`. The constructor zeroes the block (`movq $0, 0x84(%rbx)`, Linux nm `0x75189a`) |
| `CGuiInGamePopUpBubble` | `anchorX 0x154` / `0x14c`, `anchorY 0x158` / `0x150` | `CGuiInGamePopUpBubble::Show(Vector)` (Linux nm `0x6a05a0`, Windows rva `0x6e9820`) stores x and y as one 8-byte store and z after it |
| `CGuiInGamePopUpBubble` | `liveText 0x14c` / `0x144` | cleared through `obj->bubble` by every `ShowFeedbackBubbleText` (Linux nm `0x71a800` / `0x7cea90`, Windows rva `0x80b240` / `0x83f230`), one field below the mode `SetChatBubbleMode` writes (Linux nm `0x6a0be0` `+0x150`, Windows rva `0x6e90e0` `+0x148`) |
| `Camera` | `viewX 0x4d4` / `0x4d8`, `viewY 0x4d8` / `0x4dc` | `Camera::SetViewPort(float,float,float,float)` (Linux nm `0x4a38a0`, Windows rva `0x5b810`, name-exported `?SetViewPort@Camera@@UEAAXMMMM@Z`): four consecutive stores `0x4d4..0x4e0` / `0x4d8..0x4e4`, each clamping w and h up to 1.0. The upper two ARE the existing `viewW`/`viewH` rows, which is the cross-check — and the `+0x4` is Camera's measured `0x4c4..0x4e4` block, not an interpolation across it |
| `CClientExoAppInternal` | `feedbackWord 0x180` (both builds) | the `ui.mouseover-feedback` setter is one store on each build, `*(uint*)(*(g_pAppManager) + 8) + 0x180) = *arg` — Linux nm `0x8ca7f0`, Windows rva `0x6bdb20` |
| `Gob` | `inView 0x1b` (both builds) | the whole body of `Gob::IsInViewVolume` is `return *(this + 0x1b)` (Linux nm `0x444780`, Windows rva `0x49110`, name-exported `?IsInViewVolume@Gob@@UEAA_NXZ`) |
| `Gob` | `lookAt 0x1b0` (both builds) | `Gob::EndLookAtAnimate` (Linux nm `0x4979e0`, Windows rva `0x50eb0`, name-exported `?EndLookAtAnimate@Gob@@UEAAXXZ`) reads it and calls a virtual on it at the same offset on each |
| `CPanelOkCancel` | **sizeof** `0x3a0` / `0x360` | the `operator new` argument of the engine's own spawn path: Linux `COptionsPanel::HandleQuitClick` nm `0x6b9800`, the Windows ExitButton twins rva `0x7103a0` and `0x84f480` (found via `Callers.java` on the exported `??0CPanelOkCancel@@QEAA@XZ`, rva `0x1ffa80`) — two independent Windows sites, both `0x360` |
| `CGuiPanel` | `closeCallback 0x80` (both builds), an embedded `CGuiCallback` | `CGuiPanel::CGuiPanel` (Linux nm `0x586fb0`, Windows rva `0x1fbb80`) seeds `obj = this + 0x80` — the unset sentinel `CGuiCallback.IsSet` tests — and `fn = CGuiCallbackv1<void,int>::HandleUninitializedCallback`, same offsets on both |

**The `+0x90` "pad" was the Itanium this-adjust, and that is why it is not a row.** `HotkeyConsent` used to
zero `panel+0x90` on Linux only, with no Windows value and no explanation. `CGuiPanel::Deactivate(int)`
(Linux nm `0x587cd0`, Windows rva `0x1fc470`) names it: Linux invokes
`fn(*(this+0x90) + *(this+0x80), result)` after testing `fn & 1` for a vtable offset, Windows invokes
`fn(*(this+0x80), result)` flat. So `+0x80` is a `CGuiCallbackv1<void,int>` whose Itanium form carries the
adjust word and whose MSVC form does not — the `CallbackStride` `0x18` / `0x10` difference `#689` already
measured, and exactly what `CGuiCallback.Adj` encodes (a write is dropped on Windows rather than landing on
the next member, which the Windows constructor does use). One embedded row replaces three constants and the
OS branch. It also explains `CGuiPanel.state`'s own `0xb8` / `0xb0`: everything declared after the callback
shifts by that same pointer.

**No probes.** Pointers, ids, floats, a size and a callback record; `Expect.Byte01` fits none of them.
`CNWCObject.retired` is the near miss — both builds compare it against 1 — but its semantics beyond its
writer set are unknown (CX-179 §8.3b), so 0/1 is not something these rows may assert.
`Gob.inView` is a *byte*: the dword a `Byte01` reads there spans `propagateHilite` at `+0x1c`, so the
predicate would be judging two fields.

### The tileset row (CX-55)

| class | row | provenance |
| --- | --- | --- |
| `CNWTileSet` | `tileCount 0x4c` / `0x54` | the whole body of `CNWTileSet::GetTileData(int)` is the bound test and the index (Linux nm `0x5e10d0`, Windows rva `0x2c67d0`, name-exported `?GetTileData@CNWTileSet@@QEAAPEAVCNWTileData@@H@Z`): `if (id < *(int*)(this + count)) return *(char**)(this + array) + id * 0xb8`. The record array (`0x58` / `0x60`) and its stride (`0xb8` on both) stay off the table on purpose — the exported getter does that arithmetic, so the only thing CX has to know is where to stop |

## CX-55 step 2: the message cursor and placeables.2da

Five rows, every one `Same` and every one read off a decompiled body on both
builds -- no interpolation, no analogy.

| Row | Linux / Windows | Anchor |
|---|---|---|
| `CNWMessage.buffer` | `0x38` / `0x38` | `CNWMessage::ReadDWORD(int)` (Linux nm `0x5e8450`, Windows rva `0x2d9bc0`, name-exported `?ReadDWORD@CNWMessage@@QEAAIH@Z`): `*(uint *)(cursor + *(longlong *)(this + 0x38))`, identical on both. `ReadWORD` (`0x5e8720` / `0x2da450`) and `ExpectType` (`0x5e8300` / `0x2d5560`) read the same three |
| `CNWMessage.length` | `0x40` / `0x40` | the bound in the same three bodies: a read that would pass `*(uint*)(this+0x40)` advances the cursor and yields 0 |
| `CNWMessage.cursor` | `0x44` / `0x44` | the byte offset those bodies load, use and then advance by the width read |
| `CNWRules.twoDimArrays` | `0x130` / `0x130` | `CNWCArea::AddPlaceableObjectStatic` (Linux `0x7091c0`, Windows rva `0x7d95a0`): `*(C2DA **)(*(longlong *)(g_pRules + 0x130) + 0x140)` on both. The Linux disassembly spells the global's own deref out -- `lea g_pRules(%rip),%rcx; mov (%rcx),%rdx; mov 0x130(%rdx),%rdx` -- so the hops are off the `CNWRules*`, not off the global's address |
| `CTwoDimArrays.placeables` | `0x140` / `0x140` | the second hop of that same expression, in `AddPlaceableObjectStatic` and `RespawnStaticPlaceablesForTile` (`0x708aa0`) alike |

**Why the message is readable ahead.** Every typed read of 8, 16 or 32 bits is a
plain little-endian load at the cursor; only sub-byte widths go through
`ReadBits` (a separate bit cursor at `+0x50`/`+0x54`/`+0x5c`), and
`ReadOBJECTIDClient` (`0x7d7870`) is four raw bytes with no type tag at all. So a
fixed-shape record array still ahead of the cursor -- which is what `LoadArea`'s
static-placeable block is -- can be read without touching the engine's parse.
The one variable is `CNWMessage::ExpectType`: it prefixes one type byte to every
typed read unless a global says otherwise, which makes the static-placeable
record either 30 or 37 bytes. That global is a `.bss` neighbour of
`g_pAppManager`, not a class field, so it is not a row: `ModelLoad.Objects` picks
the shape by which one reads the appearance the engine just handed it back at the
cursor, and stands down if neither does.

### The exosound rows (CX-57)

Read off `CExoSoundInternal::PlaySourceOn3DVoice` (Linux `0x677dc0` in Ghidra, Windows rva `0x1e0050`),
`CExoSoundSourceInternal::SetPriorityGroup` (`0x676a70` / rva `0x1e1610`), `SetVolume` (`0x676900` / rva
`0x1e1990`) and `SetPosition` (`0x676860` / rva `0x1e15a0`) on both builds. MSVC shifts the middle of the class by
four bytes; from `+0x50` on the two layouts agree.

| row | Linux / Windows | read off |
|---|---|---|
| `CExoSoundSourceInternal.position` | `0x30` / `0x34` | `SetPosition` stores the vector there; `PlaySourceOn3DVoice` hands the same three floats to `alSource3f(AL_POSITION)` |
| `CExoSoundSourceInternal.variance` | `0x40` / `0x44` | `SetPriorityGroup` stores the 2DA's `PlaybackVariance`, clamped to [0, 1] |
| `CExoSoundSourceInternal.group` | `0x48` / `0x4c` | `SetPriorityGroup`'s byte store; `SetVolume` tests it for the dialog/music routing |
| `CExoSoundSourceInternal.voice3d` | `0x50` / `0x50` | `PlaySourceOn3DVoice` stores the voice there; `SetVolume` reads `+8` off it on the 3D branch |
| `CExoSoundSourceInternal.voice2d` | `0x58` / `0x58` | `PlaySourceOn2DVoice` / `SetVolume`'s 2D branch, the same way |
| `CExoSoundSourceInternal.nearDist` | `0x64` / `0x64` | `SetPriorityGroup` (`MaxVolumeDist`) and `CExoSoundSource::SetDistance` (a placed sound's UTS minimum) |
| `CExoSoundSourceInternal.farDist` | `0x68` / `0x68` | the same two writers; `PlaySourceOn3DVoice` sends it as `AL_MAX_DISTANCE` |
| `CExoSoundSourceInternal.sampleOffset` | `0x84` / `0x84` | `PlaySourceOn3DVoice`'s `alSourcei(AL_SAMPLE_OFFSET)` operand |
| `C3DVoice.source`, `C2DVoice.source` | `0x8` / `0x8` | the `alGenSources(1, voice + 8)` in both play functions |

### The NUI cull rows

Read off `Nui::Layout::Layoutable::Render` (Linux nm `0x84c6b0`, Windows rva `0x951260`), the parent's
per-child loop, on both builds (CX-154 §9).

| row | Linux / Windows | read off |
|---|---|---|
| `Nui::Layout::Layoutable.noPush` | `0x50` / `0x50` | `cmpb $0,0x50(%r15)` guards the `nk_layout_space_push` of the child's rect (Linux `0x84c9f8` -> `0x84dab5`, Windows `0x1409515d6` -> `0x1409515fa`) |
| `Nui::Layout::Layoutable.vtbl.Render` | `0x28` / `0x20` | the child call (Linux `0x84ce7e` `call *0x28(%rax)`, Windows `0x140951cfe` `call *0x20(%rax)`); both builds' `Widget` and `StatefulWidget` vtables fill it with `Widget::Render` |

The nuklear `nk_context`/`nk_panel` offsets `NuiService` reads are not rows: nuklear is C, and
`nk_widget`, `nk_layout_widget_space` and `nk_layout_space_push` read the same values on both builds.

### The kiwi solver rows

What `NuiSolver` reads of the vendored kiwi solver every NUI `LayoutGroup` embeds at `+0x5a8`
(CX-154 §2, §12). Read off `SolverImpl::substitute` (Linux nm `0x85b890`, Windows rva
`0x3458e0`), `addWithArtificialVariable` (`0x85daa0` / `0x340410`) and the reset `LayoutGroup::SetChildren` inlines
(`0x853db0`). Windows sits further out: MSVC keeps each `Loki::AssocVector`'s empty comparator as an 8-byte member, so
every `SolverImpl` field after `m_cns` moves, and so does `Row`'s constant (`+0x18` / `+0x20`, unread).

| row | Linux / Windows | read off |
|---|---|---|
| `kiwi::impl::SolverImpl.rows` / `.rowsEnd` | `0x18`, `0x20` / `0x20`, `0x28` | substitute's walk bounds (`mov 0x18(%rdi)` / `mov 0x20(%rcx),%rbx; mov 0x28(%rcx),%rdi`) |
| `kiwi::impl::SolverImpl.infeasible` / `.infeasibleEnd` | `0x60`, `0x68` / `0x80`, `0x88` | the negative-row push inside substitute (Linux end `+0x68`, cap `+0x70`; Windows `lea 0x80(%rbp)`, end `+8`, cap `+0x10`) |
| `kiwi::impl::SolverImpl.artificial` | `0x80` / `0xa0` | substitute's last block and addWithArtificialVariable's store of the copied row |
| `kiwi::impl::SolverImpl.idTick` | `0x88` / `0xa8` | read then incremented at the head of addWithArtificialVariable; set to 1 by SetChildren's reset |
| `kiwi::impl::Row.cells` / `.cellsEnd` | `0x0`, `0x8` / same | every cell binary search |
| `kiwi::impl::SymbolPair.id` / `.type` / `.value` / `.stride` | `0x0` / `0x8` / `0x10` / `0x18`, same | both maps' `(Symbol, 8-byte value)` pairs: the `/3` (24-byte) walks, `cmpl $0x1,0x8(entry)` for External, the `Row*` at `+0x10` |

`NuiLayoutErrors` reads more of the same solver, and of the NUI tree around it, when a layout fails
(CX-153). Every row was read off both binaries; the evidence is in that document's
§0 and §8.

| row | Linux / Windows | read off |
|---|---|---|
| `kiwi::impl::SolverImpl.cns` / `.cnsEnd` | `0x0`, `0x8` / same | `addConstraint`'s duplicate `lower_bound` and its final `m_cns[constraint] = tag` insert |
| `kiwi::impl::Row.constant` | `0x18` / `0x20` | createRow's store and addConstraint's `nearZero` read of the new row |
| `kiwi::impl::CnPair.data` / `.marker` / `.other` / `.stride` | `0x0` / `0x8` / `0x18` / `0x28`, same | the m_cns record: `{ConstraintData*, Tag{marker{id, type}, other{id, type}}}` |
| `kiwi::ConstraintData.terms` / `.termsEnd` / `.constant` / `.strength` / `.op` | `0x8` / `0x10` / `0x20` / `0x28` / `0x30`, same | the `Constraint(Expression, op, strength)` ctor (Linux `0x85a5f0`, Windows rva `0x334470`) |
| `kiwi::Term.variable` / `.coefficient` / `.stride` | `0x0` / `0x8` / `0x10`, same | the same ctor's term copy |
| `Nui::Layout::Layoutable.children` / `.childrenEnd` | `0x8`, `0x10` / same | `Span::BuildConstraintDependants`' child loop (shared_ptr records, 16 bytes) |
| `Nui::Layout::Layoutable.vars` (12) | `0x2c8` / `0x3d8` | the ctor's twelve `Variable(Context*)` and its three LayoutExpressions: x, y, w, h, padding l t r b, margin l t r b |
| `Nui::Layout::Span.direction` | `0x5a8` / `0x6c0` | the row / col makers store 0 / 1 (Linux `0x864b10` / `0x87b4b1`, Windows `0x741658` / `0x741773`) |
| `Nui::Layout::LayoutGroup.solver` | `0x5a8` / `0x6c0` | `lea` before every `addEditVariable` / `addConstraint` in SetChildren |
| `Nui::Layout::LayoutGroup.editWidth` / `.editHeight` | `0x638`, `0x640` / `0x770`, `0x778` | SetChildren's two `addEditVariable` calls; also the only inputs of the parent rect it hands its children |
| `Nui::Layout::LayoutGroup.scaleConstraints` | `0x668` / `0x7a0` | SetChildren's vector holding its one `guiScale == nk_get_gui_scale()` constraint |
| `Nui::JSON::JsonWindow.idsHead` | `0x418` / `0x468` | Linux: the id map's `_M_before_begin._M_nxt` (the hashtable `clear()` HandleError calls); Windows: the MSVC list head sentinel (`GetElementById`'s `end()` compare) |
| `Nui::JSON::IdNode.key` / `.element` | `0x8`, `0x28` / `0x10`, `0x30` | the node's key `std::string` and its `weak_ptr<Layoutable>` element pointer (`GetElementById` on both) |
