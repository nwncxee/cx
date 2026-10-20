# Missing parts under Alloy: what to do when you see one

A creature with an arm, a shin or a head gone; a bench that was there and is not (#317). It is random,
it heals when the creature moves or is re-created (out of the server's render range and back), and the
bench version comes and goes while you stand still. The bench half is fixed (a geometry-less part
poisoned its whole gob, the research on CX-130); the creature half has a named
mechanism and a fix as of CX-92, below. The protocol is how one incident becomes a log worth reading.
Every step is a live chat knob: no restart, no `cx.cfg` edit.

## Root cause (CX-92): a re-shown subtree has no notice, and nothing draws an unowned part

`part+0x34` is the engine's own per-subtree draw gate: `Scene::AddPartsTreeToDrawBuckets` @0x460880 and
`Gob::PartDraw` @0x451d50 both `return` on a zero byte **before** recursing into children, so a part
with the byte clear -- and its whole subtree -- is drawn from no engine path. `SceneWalk.Tree` has
pruned on that byte since #397, and `Sweep` released what it pruned.

The byte has four writers in this client. `Part::Part` @0x43c800 sets it once. `Gob::Append`'s tail sets
the anchor's ancestor chain to 1. `TreeSetGeometryFlags` @0x43e620 (a register store,
`mov %r14b,0x34(%rbx)` at 0x43e675/0x43e68e) only ever ANDs it with "a child has geometry", so it can hide a
geometry-less subtree but never show one; it runs from `Gob::ProcessGob` whenever the gob's process flag is
dirty, and `Scene::AddPartsToDrawBuckets` @0x4751d0 calls that every frame. The dynamic show/hide writer
is `Gob::SetSubtreeEnabled` @0x43c670 (`43c695: setne 0x34(%rax)`), which has **no call site in the
text** -- it is reached only through a name-dispatch table (eight data references to 0x43c670) -- and it
bumps nothing of the engine's own: no `Scene::Sequence`, no `SetProcessFlag`, no ctor, no dtor. The fix
below does not depend on which writer moved the byte: it re-reads it. So:

- hide: the walk prunes the subtree and Alloy stops drawing it. Correct, and vanilla agrees.
- show: the engine announces nothing, so Alloy hooks the writer. **`Gob::SetSubtreeEnabled` is hooked**
  (`SceneWalk.OnGobSetSubtreeEnabled`): a non-zero return means the byte moved on a part of that gob's
  active tree, and the gob is queued for a per-gob re-walk at the next frame anchor. The engine-side
  silence is unchanged; what is gone is Alloy's dependence on it.

**Before the hook** this was the one membership edge with no notice at all, and it read as: the record stays
released, enrolment never sees the part, and since #264 an unowned part is drawn by **nobody** -- random per
part, per-part rather than per-gob, hits NPCs, survives `/alloy poll 1` (there is no record to invalidate),
leaves **no `D` row** in the frame dump, and heals on re-creation. It was masked by the engine's global epoch
moving every 11-20 frames and forcing a full walk; the per-gob deltas removed that accident, which is why the
edge had to be hooked rather than left to it. The other edges that used to ask for a whole-area walk name their
gob now too: `Part::~Part` calls `SceneWalk.NoteGobDirty` (`DrawFunnelService.OnPartDtor`, for the in-place
armour rebuild) and a repack queues its stale entries by index.

**The fix, in two halves.** A pruned subtree root is HELD as a `PartTable.StHidden` entry instead of released
(`SceneWalk.HoldHidden`), remembering whose tree held it; and the show edge is told. `SceneWalk.Tick` still
re-reads `part+0x34` over the held set on the frames that would otherwise walk nothing (`HiddenShown`) as the
backstop, and a byte that came back asks for that gob's delta, which releases the entry so the part re-enrols
from scratch (`ShowHidden`). Holding rather than releasing is what makes the re-read lifetime-safe:
`Part::~Part` marks a held entry freed through the table's own tombstone, only a freed slot is ever reclaimed,
and the held list is maintained on every exit from `StHidden` (`UnHold`, from `Release`, the death notice and
the table wipe). A held entry is exempt from #128's all-owned-or-none refusal for the geometry-less case's
reason -- vanilla draws nothing for it either, so its siblings keep drawing.

**What an operator reads.** On the `[enum]` line: `subtree=N` is the hook firing (hides and shows together);
`hidden=N (held=H shown=S re-shows=R)` is the held population and the re-shows the backstop caught, and
`hold-refused=` must never appear; `epoch delta=/full=` says how many membership moves were answered per gob
rather than by the whole-area walk; `audit misses=M (added=A swept gob=G tile=T)` is the full walk, run every
`alloy.walkaudit` frames (300), finding what the deltas missed, and **it must read 0** -- a nonzero `added` is
a part no edge announced, which is this defect's shape. `H held hidden on part+0x34` is in the census
`instances:` clause as before. The bisect is `/alloy walkdelta 0`: it puts every epoch bump back on the full
walk, so a limb that comes back with it off is a missed delta edge and one that does not is something else.
Live-unverified.

### `/alloy holes` -- the per-instance hole dump

For every gob in the scene with at least one part Alloy is not drawing, one `[holes]` line per part of
that gob: entry state (including `hidden(part+0x34)`), `OwnFlag`, ownership slot and class, the
per-slot gates the route walk refuses on (`inactive-lod`, `record-stale`, `tex-stale`,
`fading`, `posed`), and `PartForensic` -- node name, the ancestor chain of visible bytes, the gob and
its append roots. Read it **column-wise**: what differs between the limb that drew and the limb that
did not is the diagnosis. Capped at 32 gobs and 16 parts each.

## The incident protocol (~30 s)

1. **Do not move.** Note which creature and which part.
2. `/alloy framedump` -- writes `cx_frame_dump/frame.txt` (in the user directory beside `cx.cfg`): one
   `D` row per draw Alloy SUBMITTED this frame, texture names on the row. A body part's texture is its
   PLT name (`pmh0_forel001` = human male left forearm, `..._shinl..`, `..._head..`); a bench is its
   diffuse.
3. `/alloy poll 1` -- and watch: **did the part come back at once?** This rewrites every record
   (`InvalidateRecords`) without touching enrolment or the engine, so an instant heal means a stale
   per-slot record or chase cache and nothing else.
4. Leave `poll` on. The next `[alloy]` period line carries
   `POLL AUDIT misses (must be 0): matrix N keyhole N fade N box N alpha N` -- the cache that disagreed
   with a live read is the one that lost the part. Does the bug recur at all with `poll` on?
5. `/alloy poll 0` when done. Keep `~/cx.log` and `frame.txt`.

The bench plaza is the higher-rate repro: stand beside them with `poll 1` on from the start and see
whether they still vanish.

## Reading it

| observation | it is |
|---|---|
| part returns on step 3 | a stale record/cache; the audit clause names which (matrix / fade / alpha / box) |
| no return, its `D` row IS in the dump | GPU-side: hi-Z occlusion or the shader. Next A/B: `/alloy cull 1` (frustum only, no hi-Z); if it stops, hi-Z |
| no return, its `D` row is NOT in the dump | CPU-side: an enrolment gate or engine state. First `/alloy holes` (one line per part of the creature, with the gate); then `alloy.dumptex=<texture>` in `cx.cfg` + restart, read the `[dumpframe] ENROLL ... -> <gate>` line |
| `/alloy holes` shows the missing part `state=hidden(part+0x34)` | the engine has switched that subtree off. Vanilla draws nothing for it either -- not our hole. If it is missing while the byte reads 1, the re-show notice failed: report `shown=`/`re-shows=` from the `[enum]` line |
| `NOT DRAWN ... fog-out 0 tile + N gob` with N > 0 | the `TileFade <= 0` route skip took a creature part (`AlloyRenderer.Batches.cs`, `RouteParts`) -- its filed tile is wrong |

## Hypotheses, ranked (2026-09-16; re-ranked 2026-09-30 by CX-92)

0. **The part+0x34 re-show with no notice** -- above. Ranked first because it is a proven code path
   rather than a prediction, and it is the only one that explains a hole with no `D` row, no `poll`
   heal and no enrolment-gate counter. Read `shown=` on the `[enum]` line before anything below.
1. **Stale chase cache on an idle creature.** #330 reads matrices / spheres / alpha on a budget and
   re-reads what moved; a part whose bone matrix stops being re-read keeps a stale sphere and fails the
   CPU route gate or the GPU hi-Z test, or a stale alpha routes it blended at 0. Predicts: step 3 heals,
   no recurrence with `poll` on, audit misses nonzero. Fits "heals on movement" and benches returning.
2. **Hi-Z occlusion false positive**: a small limb sphere inside/behind the torso from a rear camera.
   Predicts: `D` row present while invisible; `/alloy cull 1` stops it; benches go as the camera drifts.
3. **`TileFade <= 0` skip** at route -- was the one hole with no counter; `fog-out` on the `NOT DRAWN`
   clause is that counter now, split gob/tile. Predicts: `gob` nonzero beside a missing limb.
4. **Enrolment gate** (`dup-sibling`, `unrenderable`, the +0x34 hidden byte): model-static, so it does
   not fit random-and-healing. Only if 1--3 read empty. It DID fit the 2026-09-18 door: `dup-sibling=4`
   on the census `rejects:` line, no `D` row for the leaf's near face, `poll`/`cull` no help. The #397
   gate was refusing a door leaf's two coincident faces as twins; it compares vertex positions now.
   `tools/mdlcensus/dupsib.py <hak>... --class DOOR` is the offline twin of that gate: exit 1 names every
   part it would still refuse (`--old` shows the pre-#317 rule).
   CX-91 audited the whole reject list and wrote each verdict down in `docs/alloy-refusals.md`: these two
   are the correct skips that remain, `item`/`item-range` became one `empty-item`, and the two that WERE
   holes (`stale-identity`, a material with no diffuse) now re-enrol and draw. So a nonzero
   `stale-identity=` or `vdo=` on the census line is itself a lead now, not background.

## What is refuted, so nobody re-chases it

- **A stale ALPHA on a creature limb.** `PartAlphaCached` polls `part+0xa0` live for every part with a
  gob or a matrix class, so no cadence can leave a creature limb at a stale alpha; the cache serves only
  still, gobless tile geometry.
- **An under-bound cull sphere on a creature limb.** `PartTriMesh::PartTriMesh` @0x53cb50 zeroes
  +0x64..+0x78, and `ComputePartBBox` @0x561370 is reached only from `ProcessStaticParts` @0x570970 --
  so a gob part has no engine bbox, `_oR` comes from `MeshRadius` (max vertex distance from the node
  origin) and `WriteSphere` centres it on the node's world translation. That pair bounds the mesh; a
  failed read leaves `_oR` at 0, which is `NeverCull`, not a tight sphere.
- **The scenery predicate.** The enrolment-time one is gone with the engine slot path; a database part's
  static verdict is its record's `ObjFlag.Static` (or a tile part). (The `gpu.static` freeze this used to
  name is gone -- CX-188.)

- **Texture / a diffuse with no GL id.** That population no longer goes missing: a tid-0 diffuse draws on
  the white stand-in (vanilla parity, the structural analysis's divergence A) and reads as
  `null-tex N drawn white/frame`, outside the NOT DRAWN list. A `null-tex` count is "wrong colour", never
  "absent" -- if the part is absent, it is another term.
- **The per-instance sweep** (`revoked by the per-instance sweep`) drops a gob WHOLE, so it cannot take
  the hands and leave the robe.
- **`[unbound=N]`** on the census line is the structural whole-creature figure #317 already called a
  red herring.
- **The `SetupArrays` retry window**, which used to be eight attempts four walk generations apart and
  then silence for the life of the area. It is gone: the re-ask condition is the engine's own, the
  vertex/index totals `VertexPrimitiveFlat::SetupArrays` @0x458920 sums, and a record is re-asked
  whenever they move. Read `waiting-on-geometry=N re-asked=M` on the `[enum]` line instead -- `N` is
  the population still carrying empty totals (each costs a totals read, no engine call), `M` the rate
  at which models are becoming resident. A part that stays invisible with `N` flat and `M` at 0 is
  **not** waiting on geometry, so look elsewhere in this list.
