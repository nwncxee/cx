# Owned-resource lifetimes

What creates each thing CX owns, what kills it, and what a lost GL context does to it. It is a
crash-prevention document: the gaps at the end of each part were found by writing the table, not by playing.

**There is one scene layer.** Alloy draws the CX scene database (ADR 0028, part 1): CX produces every record
through the Aurora seam, so it produces every lifetime edge and there is nothing to infer. Until 2026-10-07 the
tree also carried an engine-scene path that observed engine objects and inferred their lifetimes; part 2 is
the inventory its deletion worked from, kept as the record of why each piece went.
`git log --grep "Engine-scene path"` has the commits, and that path's contract is in this file's history.

Line references are to the commit that touched them; treat them as a starting point, not as an address.

---

# Part 1 — the scene database (ADR 0028)

## The edges, and who fires them

ADR 0028 says there are exactly three told edges plus context loss. As built there are **five** things that
kill something, and the two extra ones are worth naming because they are the ones a reader will not expect:

```
object destroy   CAurObject vtable slot 0/1 -> Seam/ObjectSlots.Destroy (-> ReleaseRecord)
                   or by handle: ObjectSlots.DestroyRecord, via Db/ObjectBridge.ObjectDestroy
                     -> PartShell.FreeAll      free the per-part CAurPart shells
                     -> PartTree.Free          part index chunk + MeshArena.Release per part
                     -> ModelCache.Release     the model and its two LOD siblings
                     -> SceneDb.Objects.Free   generation++ and Dirty.Destroyed
                     -> CallbackList.FreeAll, ObjectSlots.FreeEngineList
                     -> Facade.Retire          null the SafePointers, null ObjFacade, quarantine the shell
                   consumed next fold by DbFeed.Records.ObjectDied: DbRelease per seated slot,
                   DbInputs.PartDestroyed per part, DbGrid.Remove, DbGui.Left
scene destroy    CAurScene dtor slot -> Seam/SceneSlots.Die -> Db/DbScene.Destroy
                     -> one object destroy per member (backwards; ObjectDied unlinks as it goes)
                     -> SceneShell.Release (null +0x130/+0x138 FIRST, then free the mirror)
                     -> SceneDb.Scenes.Free
model release    ModelCache.Release -> DelRef -> Trim -> ModelCache.Evict
                     -> ModelCache.Evicting: MeshArena.Forget, TileBuild.Forget  (+= at Attach)
                        ...and MeshBvh.Evict and DrawRecipe.Forget, which never subscribe (see the gaps)
                     -> MeshArena.Release queues; the GL free lands in MeshArena.Pump (render side)
texture release  the same eviction, one hop further: DrawRecipe.Forget -> TexRegistry.Release
                     -> the slot goes idle on the LRU -> TexRegistry.Tick's EvictPastBudget -> Retire
                     -> the GL name goes to _graveyard -> deleted in TexRegistry.Drain (bracket)
world edge       AlloyRenderer.EndFrame: module unload / 4.6 context lost / part table wiped
                     -> AlloyRenderer.RevokeAll -> PassRegistry.Global.Revoke()
                     -> TileBuild.Revoke, PartTreeLive.Revoke, ModelCache.AreaEdge
context loss     AlloyRenderer.EndFrame -> PassRegistry.Global.ContextLost()
                     -> RenderThread.Shutdown (joined), MeshArena.ContextLost, TexRegistry.ContextLost,
                        PltPalettes/PltColors/PltGpuCheck.ContextLost, DbFeed.ContextLost
```

**The fourth edge the ADR folds into "model release" is really its own.** A texture does not die when a model
does; it dies six hops later, two of them on a different thread, and only if the byte budget says so. The
chain is `ModelCache.Evicting` → `DrawRecipe.Forget` (`Db/DrawRecipe.cs`) → `TexRegistry.Release` →
idle LRU → `TexRegistry.Tick`'s `EvictPastBudget` → `Retire` → `_graveyard` →
`TexRegistry.Drain`'s `glDeleteTextures`, inside Alloy's bracket. Any reader who expects a texture to go with
its model will look in the wrong place; and the first hop of that chain is **not wired** (see the gaps).

**The fifth is the world edge, which the ADR says the contract deletes.** It does not, in DB mode: the
`PassRegistry` Revoke walk is still driven from `AlloyRenderer.EndFrame`, which is a `FramePhase.PostPresent`
participant and runs in every mode. What it no longer has is the **area** trigger —
`AlloyRenderer.TakeAreaEdge` is called from `OnRenderSinglePass`, a hook the seam does not install, so in DB
mode the walk fires on a module unload, a context loss and a part-table wipe and never on an area change.
Three DB tables are wired to it: `TileBuild.Revoke` ("the world edge: an area change drops every placement"),
`ModelCache.AreaEdge` (the census line plus the read-ahead references) and `PartTreeLive.Revoke`. In DB mode
none of them sees an area change.

## Why a handle replaces a death notice

`DbId` is a 20-bit index plus a 12-bit generation, 0 = null (`Db/SceneDb.cs`, `DbId`). `DbTable.Free` bumps
the generation **at the free**, never at reuse, so every outstanding handle to that slot is dead the moment it
happens; `DbTable.Valid` is the one test, and it also checks the `1u << 31` allocated bit that `Alloc` sets.
That is the whole of ADR 0028's claim that "no table keyed on a `DbId` needs a death notice to stay correct",
and it holds: `SceneDb.SceneHighlights`, `SceneObjects`, `TileObj`, `LitObj`, `ObjAttachTo`, `CamObj` and the
facade columns all store handles, and every reader resolves through `Valid` before it dereferences anything.

**It stops holding wherever a table stores a bare index instead of a handle**, and there are five of those.
A part index, an ownership-slot number and an arena range id are all plain `int`s drawn from a recycling
allocator with no generation beside them:

| Table | Key | Guard that exists | What is left |
|---|---|---|---|
| `AlloyRenderer._oDbPart` ↔ `SceneDb.PartSlot` (the ownership table) | part index both ways | `DbSeat` is idempotent on `_oDbPart[held] == part`; `DbRelease` refuses unless `_oDbPart[i] == part` | told at the destroy edge, so closed |
| `TexRegistry._depPart` (the dependents list) | bare `int part` | `MarkDependents` tests `part < MaxParts && Parts.LiveAt(part)` | a recycled index gets a spurious `Dirty.Texture` — one extra restage, benign |
| `DbInputs._lightPart` / `_emitPart` | bare `int part` | `Light`/`Emitter` test `Parts.LiveAt(p)`; `PartDestroyed` is called from both folds | told, so closed (R1-11 fixed exactly this: "a reused part index revived a dead light") |
| `PltColors._words` | ownership-slot number | `Stage` validates the **object** handle and `Clear`s the slot on a dead one | the flag word persists on a slot until something restages or clears it |
| `MeshArena._ranges` range ids | recycled slot number, no generation | `Alive(r)` tests `Live`; `DrainFrees` only unmaps `_byContent` "when the key still names THIS range" | the holders (`SceneDb.PartMesh`, `DrawRecipe._range`) release before the free, so closed — but this is the recycled-address shape with no fingerprint |

The draw path keeps one generation check as a net rather than as the mechanism:
`AlloyRenderer.DbStage` (`Scene/AlloyRenderer.DbStage.cs`) tests `SceneDb.Objects.Valid(obj)` before it reads
the object's columns and counts `_dbOrphan` — "DB slots staged whose object had already been freed: a feed
bug, counted rather than drawn against object 0". A non-zero `orphaned parts` on the `dbpass:` clause of the
`[alloy]` line means a told edge was missed, and it names itself instead of misrendering.

## Terminality — still the invariant everything else leans on

`GlContextService.Fail` sets `_disabled = true` permanently and clears `Available`
(`managed/Graphics/GlContextService.cs`); `Ensure` short-circuits on `_disabled`, so `Create` is never
retried and `Available` is never set again. Context loss is therefore a **per-session terminal edge**, exactly
as it was before, and that is what makes the drop-only `ContextLost` handlers safe rather than leaks:

- `MeshArena.ContextLost` forgets `Vao`/`Vbo`/`_ebo` and zeroes the uploaded counts, deletes nothing, and
  **keeps both CPU mirrors** so a rebuilt context would re-upload from them rather than from engine memory.
  There is no `glDeleteBuffers` or `glDeleteVertexArrays` anywhere in `Db/MeshArena.cs`.
- `TexRegistry.ContextLost` clears `_graveyard` without issuing the deletes, forgets every name and handle,
  and retires the unreferenced slots (`Db/TexRegistry.Load.cs`).
- `PltPalettes.ContextLost` is `_tex = 0; _dirty = true` — correct for a dead context, and the reason
  `AurClearPalette` orphaning a live texture through it is a bug rather than a policy (see the gaps).
- `DbFeed.ContextLost` resets `DbGrid` and `DbInputs` and re-marks every live part `Dirty.Created` so the next
  fold rebuilds the side tables. The records themselves survive: they are CPU state and the game still owns
  the objects.
- `RenderThread.Shutdown` is the `scenedb` pass's `ContextLost` and is **joined, not signalled** — the game
  thread may take the context back the instant it returns, so the thread must have let go first.

If context recreation ever lands, this part is the checklist, and the first thing to re-check is that
`MeshArena.Pump` is reached at all on the far side (it lives in the world colour frame; see the gaps).

## Threading: who may touch what

**The game thread owns the database, by convention and by construction — nothing enforces it.** There is no
thread-id capture, no assert and no "game thread only" check anywhere in `Alloy/Db/` or `Alloy/Seam/`. The
invariant holds because every seam entry point is an `[UnmanagedCallersOnly]` vtable slot or a hooked engine
function, and the game is single-threaded through the Aurora API.

The frame boundary is the serialisation, not a lock. `Db/SeamDraw.OnBoundary` is a `FramePhase.PrePresent`
participant at priority 9, one ahead of `GlContextService`'s bracket, and the order per frame is:

```
SeamDraw.OnBoundary                     game thread
  RenderThread.StartWhenReady / SettleIfStandingDown
  with a render thread live:
     RenderSnapshot.AwaitDrained        <- THE FENCE: the renderer has released the previous frame
     UiHostService.RecordWindowsForDbFrame (the 2D UI as draw lists; the renderer replays them in its GUI flush)
  DbGui.NewFrame, DbCamera.NewFrame
  RenderSnapshot.Publish                game thread
     FrameHoist.Fold + FoldFog          <- the frame's ENGINE reads, FIRST and here only
     Fold = DbAnim.Settle, DbFeed.FoldFrame, TriggerHighlight.Enroll, SceneFeed.FoldWind
        DrawRecipe.Pump                    (may call MeshArena.Acquire, which takes the arena lock)
        DbFeed.TakeFrame -> FoldObjects, FoldParts
     DbTable.TakeDirty x4
     wait while the renderer still holds the previous frame   (a no-op after the fence)
     publish
  DbFeed.CloseViews
  if the render thread is live: it draws; else DrawSnapshot here, then the 2D UI recorded and flushed, in try/finally
SeamDraw.DrawSnapshot                   render thread, or the game thread on a stand-down
  per view: SceneFeed.TickFromView, then DbGui.Draw (GUI) or SeamDraw.Pass (world)
     Pass = AlloyRenderer.DbWorldPass -> DbOrder, LatchFrameTarget, then ONE 4.6 bracket (DbDrawView)
        the shadow bake, the particle/beam/halo enrolment, DrawOwned -> BeginOwnedFrame
        MeshArena.Pump, TexRegistry.Drain, PltPalettes.Create, PltColors.Upload, the passes
```

`DbFeed.Present()` still exists and still works, but **nothing calls it**: it folds *and* draws, and the fold
has to run inside `RenderSnapshot.Publish` ahead of `TakeDirty` or it sees an empty dirty list. `SeamDraw`
says so in its own summary.

**The snapshot carries views, not records.** `RenderSnapshot.Frame` holds a `ViewRec[]` (camera matrices,
viewport, depth range, the scene handle, the world/GUI discriminator) and four dirty counts, and nothing else.
The renderer reads `SceneDb`'s columns **live**, and what makes that safe is the one-frame-in-flight rule, not
a copy. ADR 0028 section 5 describes the snapshot as carrying "changed world matrices, materials, bone
palettes, visibility"; as built it carries none of those. `DbFeed.Records.cs`'s R1-17 remark is the
authoritative statement of the contract and is worth reading before touching either side of it.

**The render thread still does not run, and the knob still cannot make it (CX-188).** `RenderThread.Start`
refuses while any of five preconditions is unset — `GuiBuffersOwned`, `TextDocsPublished`, `UiOrderSettled`,
`FrameInputsPublished`, `ScreenHandoffSettled` — and names every unmet one. (There is no `GuiOffThread`; a
single flag would let whoever met one arm the others' races.) CX-188 met `TextDocsPublished` and
`UiOrderSettled`; the two structural ones left are `GuiCapture`'s single static bank, which `NuiFlush` writes
on the game thread while the renderer appends and resets it, and framebuffer 0, which has two writers in the
window the fence cannot cover. `FrameInputsPublished` is the audited ledger of engine reads still inside the
draw. `RenderThread.cs` carries all of it with each fix named. So `alloy.renderthread=1` logs a loud stand-down
and the single-threaded path takes the frame. Every "render thread" row below therefore means *inside the 4.6
bracket*, which today is the game thread.

Of the three latent races that only arm with that thread, two are closed by the fence, which runs ahead of the
fold; the third stands:

1. ~~**`RenderSnapshot.Publish` folds before it waits.**~~ `RenderSnapshot.AwaitDrained` is called first in
   `SeamDraw.OnBoundary`, so the fold for frame N runs with the renderer already parked instead of rewriting
   the persistent draw records while it still draws N-1. This is the "move the wait above the fold" fix, not a
   lock.
2. ~~**`DrawRecipe`'s table is unsynchronised.**~~ Its writers (`Intern`/`Fill`/`Pump` in the fold, and
   `DbAnim.Dyn`'s `InternDynamic` in the dangly solve, which runs from `DbAnim.Settle` inside the fold) are
   now inside the fence, and the 4.6 bracket that reads the table (`AlloyRenderer.Db.cs`, `DbStage.cs`,
   `Casters.cs`, `DbInputs.cs`) runs outside it. The two spans are disjoint by construction. A writer added
   OUTSIDE the fold would re-open this, which is what `MeshArena.Dyn.cs` is really warning about.
3. **`MeshArena`'s read API is lock-free against a locked, compacting writer.** `Draw`, `Runs`, `Bounds`,
   `RefsOf`, `Alive` read `_ranges` with no lock; `NewRange` reassigns the array under the lock and `Compact`
   rewrites every live range's `Base`/`Off*` in place.

Two things cross threads correctly today and must keep doing so: `MeshArena.Pump` (the only place frees land
and both mirrors go up, under `_gate`, inside the bracket) and `TexRegistry.Drain` (GL only — it deletes the
graveyard, uploads at most 8 finished jobs and publishes them; it reads no engine memory and writes no
`SceneDb`). The texture decode worker (`cx-tex`) and the model parse worker (`cx-model`) see only value
records copied for them at the offer, touch no engine memory and no GL, and are both background threads with
no stop token — process lifetime, deliberately.

One priority inversion worth knowing about: `MeshArena.Acquire` (from the fold, game thread) and
`MeshArena.Forget` (from an eviction, game thread) take `_gate`, which the bracket holds for the whole of
`Pump` including `Compact` and every upload. And `MeshSimplify.Run` — two calls per distinct mesh, from
`CutLods` inside `Build` — runs on the game thread *while holding `_gate`*.

## The tables

### Records, handles and seam state

| Resource | Created by | Killed by | Context loss |
|---|---|---|---|
| The six `DbTable`s — `Scenes` 256, `Objects` 65536, `Parts` 524288, `Cameras` 512, `Tiles` 16384, `Lights` 16384 (`Db/SceneDb.cs`) | `DbTable.Alloc`, which also marks `Dirty.Created` | `DbTable.Free`, generation++ and `Dirty.Destroyed`. Objects: `ObjectSlots.ReleaseRecord` / `TileBuild.FreeObject` / `TextObjects.Destroy`. Scenes: `DbScene.Destroy`. Cameras: `DbCamera.Destroy`. Tiles: `TileBuild.Drop`. Lights: `TileBuild.FreeLights` | none — CPU only |
| The dirty lists (`DbTable._dirty`/`_dirtyList`) | `DbTable.Mark`, appended once per slot per frame at the first bit | `DbTable.TakeDirty`, from `RenderSnapshot.Publish` after the fold | none |
| The ownership table's DB columns `_oDbPart`/`_oDbRecipe` and the back-reference `SceneDb.PartSlot` | `AlloyRenderer.DbSeat` (`Scene/AlloyRenderer.Db.cs`), from `DbFeed.FoldOnePart` on a dirty part | `AlloyRenderer.DbRelease`, from `ObjectDied`, `ReleaseRangeOutside` (a tree change) and the part fold's `Dirty.Destroyed` | the record epoch bump in `AlloyRenderer.ContextLost` |
| The per-draw records in `_drawBuf`/`_texHBuf` | `AlloyRenderer.DbStage` / `StageDbPart` writes them; `Issue.UploadDraws` sends them | re-written when the part is marked dirty; the slot is unstamped by `UnstampRecord` at a reseat | `AlloyRenderer.ContextLost` zeroes the buffers **and** bumps the record epoch (#242: a name drop alone would leave every slot believing it was still uploaded) |
| DB bone slots in the renderer's bone stage | `DbAnim.SkinObject` → `AlloyRenderer.DbAllocBone` (owner `-2`, so the engine path's two reclaimers skip it by construction) | `DbAnim.Unskin` → `DbFreeBone`, from the part going disabled/merged, a refused palette, and `DbAnim.OnRetire` walking the object's range | `_boneBuf` zeroed by `AlloyRenderer.ContextLost` |
| Bone CELLS inside those slots, for rigged dangly parts (`DbAnim.DanglyBones.cs`) | `DanglyBoneAlloc` from the dangly dispatch: a power-of-two run of 2..64 bones, cut from a slot `DbAllocBone` claimed; the part's `PartBoneBase` is the run's ABSOLUTE first bone | `DanglyBoneFree`, from the part switching to stock motion, its node turning out Static, and `DanglyBonesForget` over the whole part range on retire / tree change / LOD swap. The SLOT stays claimed — a DB bone slot is sticky by design, and the next rig of that size takes the cell | `DanglyBonesContextLost` asks for every one of them back; the GAME thread acts on it at the end of its next dangly walk, because that is where they are owned |
| The vtable block — all six tables in one `AlignedAlloc` | `Facade.Build` (`Seam/Facade.Vtables.cs`), once at boot | nothing: `Facade.DropTables` is self-check-only, and `IExtenderService` has no shutdown | n/a |
| The `CAurPart` vtable | `PartShell.Build`, via `ObjectFns.Register`'s `AfterTablesBuilt` | nothing | n/a |
| Facade shells — 0x4c0 object / 0x558 string / 0x518 scene / ≥0xe68 camera, each behind a `SeamHeader` | `Facade.Mint` → `SeamShell.Alloc` | `Facade.Retire` → `SeamShell.Retire` marks `StateRetired` and rings it; the `AlignedFree` only happens in the ring-eviction branch or in `SeamShell.DropAll` (self-check) | n/a — native |
| Part shells (`CAurPart*`), one per asked-for (object, part), chained off `SceneDb.ObjSeamPartShells` | `PartShell.For` | `PartShell.FreeAll(obj)` from `ObjectSlots.ReleaseRecord` — freed immediately, `Magic` zeroed first, **not** quarantined | n/a |
| `CallBack` records (0x30 each) and the `CExoArrayList` behind the shell's +0x460 | `CallbackList.Add` | `CallbackList.Remove` per record; `CallbackList.FreeAll(shell)` from `ObjectSlots.Destroy` | n/a |
| The game's SafePointer back-ref array at shell +0x08, and the attachment list at +0x4a8 | the **game** reallocs them in place (the shell is handed out with capacity 0 so the first `realloc(NULL)` is the game's) | `Facade.NullSafePointers` and `ObjectSlots.FreeEngineList`: zero each target, index −1, then free on Linux. On **Windows the array is leaked and counted** (`SeamShell.NoteForeignArray`) because nwmain statically links its CRT | n/a |
| The scene member mirror behind `Scene+0x130`/`+0x138` | `SceneShell.Flush` (`NativeMemory.Realloc`), lazily off the `GetAurSceneObjectCount` hook | `SceneShell.Release` from `DbScene.Destroy` — it nulls the shell's two words **before** freeing the array, because the shell outlives the record by its quarantine | n/a |
| The interned native name table | `NativeNames.Of` (`Marshal.StringToCoTaskMemUTF8`, under `lock (_byName)`) | nothing, by documented design: one copy per distinct name, never freed, tens of kilobytes for the session. It is **process-wide, not per `ModelDef`**, so an entry outlives its model | n/a |
| Text documents (`TextObjects.Docs[]`, one per object index) and their line/quad/run arrays | `TextObjects.Create` | nothing is freed: documents are **pooled by index and never released** (an explicit `ponytail:`). `TextObjects.Destroy` frees the record and `Reset` returns the doc to ctor state on reuse, re-stamping `d.Owner` with the new generation | none — CPU only |
| `TextSlots._external` (the game's opaque cookie, keyed on the object index) | `SetExternalData` | `TextSlots.ClearExternal` at **both** create and destroy — "a recycled index must not hand the previous object's pointer to the next one's `GetExternalData`". This is the model the other index-keyed columns should follow | none |

### Models and geometry

| Resource | Created by | Killed by | Context loss |
|---|---|---|---|
| The model table (`ModelCache._table`/`_bySlot`/`_lru`) | `ModelCache.InsertModel`, from `Publish` | `ModelCache.Evict`, from `Trim` (idle bytes past `models.budget`, default 192 MiB) or `Clear` (a source change / module unload). `AreaEdge` reports the census and drops the read-ahead references; it releases nothing held | none — it owns no GL name |
| Parsed `ModelDef`s | `MdlParser.Parse` on the `cx-model` worker | GC, once `Evict` has removed the slot and every `Evicting` subscriber has dropped its strong references | none |
| The parse queue and jobs | `ModelCache.Start`, which lazily mints the one `cx-model` background thread | jobs by `_flight.Remove`/`_done.Dequeue`; `Clear` bumps `_sourceGen` so in-flight work publishes stale and is dropped. The thread lives for the process | none |
| Arena ranges (`MeshArena._ranges`, `_byContent`, `_byMesh`) | `MeshArena.Acquire` → `Build`, `AcquireRaw`, `AcquireDynamic` | `MeshArena.Release` last-ref queues into `_pendingFree`; the kill is `DrainFrees` inside `Pump` — a render-side frame edge, "so no draw in flight holds its offsets". `Forget(ModelDef)` drops the `_byMesh` shortcuts only and **releases nothing**, counting `_heldPastEviction` for a part that outlived its model | `MeshArena.ContextLost` forgets the names, keeps the ranges and both mirrors |
| The arena's two CPU mirrors (`Section.Mirror`, `_iMirror`) | `GrowMirror` / `AppendRun` | nothing: grow-only, `Compact` compacts the contents but never shrinks the arrays. `ResetForCheck` is off-device only | kept, deliberately |
| Mesh LOD levels | `MeshArena.CutLods` — two `MeshSimplify.Run` calls per distinct mesh, on the game thread under `_gate`, each level kept only if it saves `MeshLod.MinSaving` | with the range | with the names |
| Dynamic (dangly/animmesh) ranges and their native vertex buffers | `DbAnim.TakeRange` → `MeshArena.AcquireDynamic` with a per-part splitmix key so nothing dedups onto it, plus `DbAnim.DynTake`'s `NativeMemory.Alloc` (cap `MaxDynParts` 2048, refused loudly past it) | `DbAnim.DanglyForget(obj)` from `DbAnim.OnRetire` — the object-destroy edge: free both native buffers, `MeshArena.Release` the range, swap-remove from `_dynList`. The private recipe row is only retired by `DrawRecipe.Forget` | with the arena |
| The dynamic patch queue | `MeshArena.WriteDynamic` (game thread, under `_gate`: patches positions in the mirror and recomputes the cull bounds so the sphere follows the pose) | `MeshArena.DynUpload` inside `Pump` — one `NamedBufferSubData` per dirty range, under the same lock. `_dynDirty` is a fixed 4096; overflow is counted and named as "a frozen cloak" | not cleared by `ContextLost` — frozen, inert |
| The part index space (`PartTree._free` — 17 size-class free lists, `_cursor`, `_cap`) | `PartTree.Alloc(n)`, rounded up to a power of two; refused past `SceneDb.MaxParts` | `PartTree.Release(first)` from `PartTree.Free(obj)`, which also `MeshArena.Release`s and `DropPlacement`s every part first. No compaction, at most 2× waste (an explicit `ponytail:`) | none |
| Draw recipes (`DrawRecipe`, max 16384), the stable half of a draw | `DrawRecipe.Intern(model, node)` → `Fill`, which takes one `MeshArena` reference and up to eight `TexRegistry` references | `DrawRecipe.Forget(ModelDef)` releases the range and each slot's `TexId` — **but its only producer, `DrawRecipe.Attach`, is never called** (see the gaps). Slots are never reused: `_n` only increments, and `Spilled` is the ceiling | the recipe is CPU state; the names it points at are the registry's |
| Placement / resident-mesh census (`ModelCache.Census`) | `NotePlacement` from `PartTree.Write`, `CombinePlan`, `MergeInto`; `NoteResidentMeshes(+1)` in `InsertModel` | `DropPlacement` from `PartTree.Free` and the two merge paths; `(−1)` in `Evict`. An unmatched drop is `_strayDrops` plus a named line | none |
| `MtrDef._cache` (parsed `.mtr`s, negative results memoised) | `MtrDef.Resolve`, under `lock (_cache)` | `MtrDef.Forget` — **no live caller**. Unbounded in distinct material names | none |
| Animation bindings and resolution memos (`ModelCache.Anim`) | `Resolve`, `Bind`, `Children`, lazily | `ModelCache.Forget(ModelDef)` from `Evict`; `ClearBindings` from `Clear` | none |

### Textures, PLT and text

| Resource | Created by | Killed by | Context loss |
|---|---|---|---|
| Texture registry slots (16384, SoA, open-address `_hash`) | `TexRegistry.Alloc` from `Acquire` or `Swap`. The key is **(lower-cased resref, atlas cell)** and nothing else — not the kind (asking for the same file as `Auto` and as `Plt` used to make two slots and two uploads) and **not the PLT scheme**, which lives per object and is resolved in the shader | `TexRegistry.Retire`, from exactly three places: `Release` dropping the last reference on a *failed* slot, `EvictPastBudget` off the idle LRU head, and `ContextLost` for an unreferenced slot. **No object, scene, model or area edge touches it** — the `texreg` pass is registered with no `Revoke` on purpose | `TexRegistry.ContextLost` |
| The registry's GL textures and bindless handles | `TexRegistry.Upload` (immutable storage, per-level sub-image) from `Finish` from `Drain`; `MintHandle` in the bracket only — a handle belongs to the share group | the name goes to `_graveyard` from `Retire`, from `Land`'s second-upload case, and from `Tick`'s stale-landing case; the `glDeleteTextures` happens in `Drain`'s first loop. The handle goes in `DropHandle` | names and handles forgotten, graveyard cleared, nothing deleted |
| A ripple's GL textures (`.txi proceduretype arturo`): its source image and its wave pair, plus the one warp program and turn table | `TexRegistry.ArturoBuffers` / `ArturoPrograms` in the bracket, on the ripple's first warp; the CPU side is `KeepArturo` on the decode worker | `BuryArturo` queues both names into `_graveyard` — from `ClearArturo` (every slot reset and retire), from `FileArturo` replacing a slot's ripple, from `StepArturo` dropping one whose generation moved, and from `ArturoStandDown`, which also deletes the program and the table. Otherwise those two live as long as the context. The eviction budget sees all of it: `KeepArturo` sizes `j.Bytes` from the forced RGBA8 level plus the ripple's own copy and wave grid | `ArturoContextLost`, called before `ContextLost` clears the graveyard: program, table and every ripple's names forgotten, nothing deleted, the next warp makes them again |
| The decode worker, its queues and job buffers | `TexRegistry.StartWorker` (one `cx-tex` background thread, lazily on the first `Offer`); `Job.Data` from `PltImage.Decode` / `RasterReader.PrepareTga` / `DdsReader.Prepare` | `Recycle` frees `Job.Data` with the right allocator per kind; `Drain` uses `try { Finish } finally { Recycle }` so an upload throw cannot leak the decode buffer. The stale-upload guard is the generation alone: `Finish` returns if `(_gen[i] & 0xFFF) != j.Gen` | the queues are left; nothing draws |
| The dependents list (`_depPart`/`_depNext`, 65536 edges) | `TexRegistry.AddDependent`, from `StageDbPart`'s bindless loop, and only while the slot is `Loading` — that is what bounds it | `MarkDependents(i)` once per slot at arrival or failure; `FreeDeps(i)` at `Retire`. Overflow is counted and said once: the part keeps the white stand-in until something else dirties it | — |
| The PLT master palette (1.75 MiB CPU) and its RGBA8 texture | `PltPalettes.SetPalette` → `ReadTga` → `Compose` fills the CPU side on the game thread; `PltPalettes.Create` makes the texture in the bracket | `Create` itself deletes the old name before making the new one. That is the only delete | `PltPalettes.ContextLost` zeroes `_tex` and sets `_dirty`, deletes nothing |
| Per-object PLT rows (`SceneDb.ObjPltRow`, 10 `ushort` per object) | `PltColors.SetRow` / `SetRows`, each marking `Dirty.Material` | **nothing** — see the gaps | — |
| The PLT row GPU block (`PltColors._words` + SSBO at `SsboSlots.Plt`) | `PltColors.Stage(slot, obj)` from `StageDbPart`; the SSBO on first `Upload` | `Clear(slot)` for a record whose diffuse stops being a PLT; `Stage` validates the object handle and `Clear`s on a dead one, counting `_badObj` | `PltColors.ContextLost` drops the SSBO; the CPU words survive and go up whole |
| Font atlases (`TextFonts`, `TextAtlas`) | `TextFontBake.Resolve` bakes a TTF with the client's own `stb_truetype` exports into an `AllocHGlobal` block; `TextAtlas.Upload` makes the GL texture and frees the staging block. A bitmap font instead borrows a **registry** name through `TexRegistry.Acquire` | **nothing.** `TextAtlas.ContextLost` exists and has no caller; the `texreg` pass's `ContextLost` covers the registry and the PLT tables and not `TextAtlas`. `TextFonts.Register` replaces a font by name without deleting the replaced atlas, and `TextAtlas`'s registry references are never released, so those slots can never go idle or be evicted | nothing happens — every `TextFont.Atlas` keeps a dead name with no re-bake |
| Text quad/run buffers | `TextQuads.Build`, grown on the document and reused; the cache key is (stale bit, font epoch, grid generation) | nothing — they die with the pooled document, i.e. never. Invalidated, not freed, by `Stale`, a font epoch bump or a `GridGen` bump | none |
| The markup colour ring (`TextMarkup.Stack`, `_index`) | static | nothing: process-wide mutable state, a faithful port of the engine's own leak, and written by both the draw path (`Process`) and the measure path (`Skip`) with no lock | none |
| The movie frame's Alloy texture (`MovieFrame`, RGBA8 immutable storage at the decoded frame's exact size) | `MovieFrame.Ensure` inside the 4.6 bracket, on a movie's first frame; filled per frame by `MovieFrame.Upload` from the staged host rows | a frame-size change (a new movie) deletes and recreates. Nothing else: no "movie ended" notice exists, so one texture outlives the movie that made it | `MovieFrame.ContextLost` drops the name and deletes nothing — the texture went with the context |
| The movie quad's program and buffers (`MovieService`: the `cx_movie_*` program, one VAO and the four-vertex VBO) | `MovieService.EnsureProgram` / `EnsureVao` inside the 4.6 bracket, on the first frame Alloy draws | nothing — one program and one quad for the session, in Alloy's own context | `MovieService.ContextLost` drops the three names and deletes nothing; the next movie frame builds them again |
| The movie frame's staging rows (`MovieFrame.Rows`, one frame's bytes) | `NativeMemory.Realloc` in `MovieFrame.Stage`, grown when a frame is bigger than the last; filled by the hook on `GLRender::DirectCreateSubTexture` | nothing, by design: one frame's rows, kept for the session rather than freed on an edge the engine never reports | none — host memory |

### Indexes

| Resource | Created by | Killed by | Context loss |
|---|---|---|---|
| The visible-set index (`DbGrid`: 64×64 wrapping cells of one NWN tile, a static list per cell, one mover list, one unbounded list) | `DbGrid.File` from `DbFeed.FileObject`, on `Dirty.Created`/`Xform` and retried until the sphere is real (a static filed with a zero sphere would be unbounded for the session) | `DbGrid.Remove(o)` from `ObjectDied`; `DbGrid.Reset` from `DbFeed.ContextLost`. A cell's box only ever grows, which costs a candidate and never a miss | via `DbFeed.ContextLost` |
| The hit index (`HitGrid`) and the per-object hit columns | `HitFeed.NoteObject` seeds the columns; `HitFeed.Fold` files the grid once per frame, from `DbAnim` after the world matrices are written and before `TakeDirty` | `HitGrid.Remove(o)` inside `HitFeed.Fold`, off the `Dirty.Destroyed` mark the table already carries — "nothing on destroy", deliberately | none — CPU only |
| Per-`MeshDef` triangle BVHs (`MeshBvh`) | built on first cast for a mesh | `MeshBvh.Evict`, subscribed to `ModelCache.Evicting` from `HitFeed.HookEviction` — lazily on first real use rather than at `Initialize`, because `ModelCache.SelfCheck` assigns `Evicting = null` partway through its own run and would drop an earlier subscription | none |
| The tile table and its cell map (`TileBuild._byCell`, `_statics`, `SceneDb.Tile*`) | `TileBuild.SpawnTile` / `PlaceStatic`, after `SetTileset` | `TileBuild.Drop(t)` from `DeleteTile`, from `Revoke` (the registry world walk) and from `Forget(ModelDef)` (a model eviction pulls every placement standing on it). `Drop` frees the tile's statics, its lights, its object and the record | none |
| Tile merge plans (`TileBuild._plans`, keyed on the `ModelDef`) | `TileBuild.Plan(ModelDef)` | `TileBuild.Forget(ModelDef)` only — kept across an area change on purpose, because the next area re-uses every tile model it shares with this one | none |
| The light table (`SceneDb.Lights`, `TileLightIds` stride 8) | `TileBuild.SetTileLights` / gob lights through the same path | `TileBuild.FreeLights` from `Drop`/`FreeObject`. `TileLightIds` is explicitly **not** a contiguous run: `DbTable`'s free list is LIFO, so after the first area change two lights allocated together land out of order | none |
| `DbInputs._lightPart` / `_emitPart` (the renderer's light and emitter lists, 4096 each) | `DbInputs.PartCreated` from the part fold's `Dirty.Created`, and `RegisterTileLight` — idempotent in both directions, because a tile whose light both streams register would otherwise be two lights at one place | `DbInputs.PartDestroyed` from the object fold's `ObjectDied`, the part fold's `Dirty.Destroyed` and `TileBuild`; swap-with-last, so a walk of the table is a walk of what is live. `DbInputs.Reset` from `DbFeed.ContextLost` | via `DbFeed.ContextLost` |

## Deliberately no death edge

These are session-lifetime on purpose. Listed so they are not mistaken for leaks, and so the next person who
adds one knows the shape:

- **Every `SceneDb` column array**, the `DbTable` free lists, `DbGrid`'s six index arrays, `DbGui`'s four,
  `DbFeed`'s view/visible/retry/seated arrays, `SceneFeed`'s per-scene stamps. Fixed size, type-init, cleared
  per record by `DbFeed.InitObject` / `DbScene.Create` / `DbCamera.Create` / `PartTree.Clear`.
- **The two `RenderSnapshot.Frame`s.** Grown once, reused forever; `DbSceneSelfCheck` asserts that a warm
  `Publish` allocates **zero** bytes, which is the NativeAOT rule for a hook (a collection on the game thread
  at the publish is a stutter).
- **`MeshArena`'s CPU mirrors and its GL buffers/VAOs**, keyed on content: an area change invalidates nothing
  in them, which is what makes the next area re-use every mesh it shares with the last.
- **Every `TexRegistry` slot**, for the same reason — the `texreg` pass is registered with no `Revoke` and the
  comment says why. A texture lives for the session, not for the area.
- **`TileBuild._plans`**, keyed on the `ModelDef` instance, so the next area re-uses every tile model it shares
  with this one. Its edge is `Forget` on a model eviction, which is wired.
- **The interned native name table.** One copy per distinct node/model name, never freed; tens of kilobytes for
  the session against a lifetime problem with no other cheap answer.
- **`TextMarkup.Stack`**, a faithful port of the engine's own process-wide colour-stack leak.
- **`SceneDb.CamBehavior`**, a `CAurBehavior*` the game allocated: ADR 0028 section 2 says CX never frees one.
- **The three workers** (`cx-model`, `cx-tex`, `cx-mdl`) and `SeamShell`'s vtable blocks. Background threads
  and boot-time native blocks; `Facade.DropTables` and `SeamShell.DropAll` exist and are self-check-only,
  because `IExtenderService` has no shutdown hook.

## What writing this down exposed

Each of these is a producer the contract names and the code does not have. Re-verified against the tree on
**2026-10-02**; the behaviour consequences, and the deliberate differences that are not gaps at all, are in
CX-184.

**Closed since the first pass** (2026-10-01), and not repeated below — each was a numbered gap here and each
now has its producer:

- The part table has one allocator: `PartTree.Build` goes through `SceneDb.Parts.AllocRange` (which marks the
  run live and `Dirty.Created`) and seeds the feed columns through `DbFeed.InitPart`, which `PartTree.Clear`
  also calls, so a recycled chunk cannot inherit the dead tenant's recipe or renderer slot.
- `DrawRecipe.Attach` is called (`AlloyRenderer.cs`), so a recipe retires with its model and the texture
  budget can evict.
- `DbScene.ObjectDied` is called from `ObjectSlots.ReleaseRecord`, `TileBuild.FreeObject`, `DbCamera.Destroy`
  and `DbFeed`'s destroy fold — and `DbScene.Destroy` now **detaches** its members as `Scene::~Scene` does
  rather than destroying them.
- A scene destroy reaches the tiles: `TileBuild.NewObject` joins the scene (`DbScene.JoinNew`), and
  `DbScene.Destroy` runs `AlloyRenderer.AreaEdge` for a world scene before the member sweep, so
  `TileBuild.Revoke` does the tile teardown with its census and its model holds.
- `HitFeed.NoteObject` is called by `ObjectFns` and `TileBuild`, so `MeshBvh` has a death edge and the hit
  test answers; and the per-frame fold runs once per **frame** rather than once per scene.
- `DbAnim`'s lifetime API has its callers: `ObjectCreated`/`ObjectRetired`/`TreeChanged` from the seam's
  create, release and tree edges, with `ObjectDying` raising the engine's `"Dying"` event first and then
  taking the attach children. The `DynTake` recycled-buffer overrun is gone with `DanglyForget`.
- The quarantine clock ticks (`SeamService.Frame` on `FramePhase.PostPresent`), the window is 1800 frames
  above the measured ~900-frame re-attach gap, and `Facade.Retire` swaps the shell's vptrs to the **poison
  twins** — so the ~20 slots that answer from the shell's own words are counted refusals too.
- `Gob+0x90` (the root `Part*`) is written, so the unhooked `AurEnableShadowing`/`AurDisableShadowing` reach a
  part.
- `SceneDb.ObjPltRow` and the material-parameter overrides go with the record (`TexSeam.ObjectDied` from
  `ObjectSlots.ReleaseRecord`): a recycled index no longer wears the dead tenant's skin, hair and armour.
- `TextAtlas.ContextLost` has a caller (`GuiPass`).
- `MeshArena.Pump` runs in the frame fold, so a GUI-only frame collects finished work.
- A compaction refreshes exactly the records that moved: `MeshArena` reports the moved ranges and
  `AlloyRenderer.DbRangesMoved` rewrites those slots' draw arguments. Nothing re-stages.
- `DbFeed.Retry` keeps its own retry list instead of re-marking `Dirty.Material` from inside the fold that
  clears it.

### Still open

1. **`HitTest.PartName` returns 0**, so `CHitInfo+0x68` is never filled, although `NativeNames.OfPart` now
   exists to fill it. A managed string's address is not a pointer the game may hold; the interning is the
   missing half.
2. **`HitGrid` is keyed on the raw object index with no generation.** `HitTest.Pass` guards with
   `Objects.LiveAt(o)`, which catches a destroyed slot and not a recycled one. The grid has no wholesale
   reset: it lives as long as the database, and the hit fold removes a slot on its `Dirty.Destroyed`.
3. **A camera's `CAurObject` sub-record is never freed.** `FacadeParts.NewCameraObject` allocates an objects
   row; `DbCamera.Destroy` frees only the camera row and says the sub-object is seam-obj's to free, and
   `CameraSlots.Die` does not. Bounded by `MaxCameras`.
4. **A text-object destroy leaves one loose end.** `StringSlots.Destruct` → `TextSlots.Destruct` frees the
   shell's lists and the record, and leaves `ObjKindOf[i]` reading `Text` over a freed slot.
5. **Font atlases have no replace or re-bake edge.** `TextFonts.Register` replaces a font by name without
   deleting the replaced atlas's GL texture, and `TextAtlas`'s per-bitmap-font registry references are never
   released, so those slots can never go idle or be evicted. `TextFontBake.GuiScale` is the only publisher of
   the pixel grid and its only caller is the TTF bake itself, so a GUI-scale change after a font is baked
   neither re-bakes the atlas nor moves `GridGen`.
6. **`AurClearPalette` orphans a live GL texture.** `PaletteFns.AurClearPalette` implements "drop the palette
   texture" by calling `PltPalettes.ContextLost`, which zeroes `_tex` **without** deleting it — correct for a
   dead context, a leak for a live one, because the next `Create` then has nothing to delete. The deferral
   `AurCreatePaletteTexture` used to record is gone with its pending flag: `PltPalettes.Create` is called
   unconditionally from both bracket sites and early-returns on `!_dirty && _tex != 0`.
7. **`DrawRecipe.Forget` does not reset `SceneDb.PartRecipe[p]`.** A part still naming a retired recipe gets
   `Range(r) == -1`, counts `_dbNoRange` and goes back on the retry list every frame, for ever. Latent while a
   part's range outlives its recipe only across a model eviction.
8. **`BindLightCubes` had no DB-mode caller** for a while, so no point light was shadowed: it ran only
   inside `SceneInputs.FrameStart`, the engine-walk path, and `SceneInputs.DbShadowSlot` did the focus and the
   bake and not the bind — `FrameInputs.AreaLightSlot` was never written in a DB frame and `LightsWithCube`
   read 0 against 22 on the same light set. `DbShadowSlot` binds now, and CX-188 deleted `FrameStart`
   outright. **A slotless light was harmless to brightness** — verified in the shader
   rather than taken on trust: `cx_lightfrag.glsl:462` adds a slotless light's full unshadowed diffuse and
   specular — so it is not the interior-darkness defect, which is a draw-class question
   (`DrawRecipe.ClassOf`'s conservative CUTOUT guess) and lives in
   CX-184. The **sun** cascades have the mirror problem one level up:
   the bake runs (813 staged draws, 4/4 cascades) and `CasterPass.CascadesFresh` gates the uniform upload on
   a `_frameSeq` the DB pass bumps at a different point in the frame, so nothing casts.
9. **Nothing charts the arena's ranges, so no baked lightmap page can exist in DB mode.** A rect is minted
   from a charted draw (`BuildBatches` → `MeshChart.Range` → `LightmapBake.Enrol`) and a DB draw comes out of
   the mesh arena, which carries no lightmap chart. `LightmapBake.Produces` says so, the loading-screen hold
   stands down the moment the set closes with no rect source, and every DB light therefore stays **dynamic** —
   self-retiring, because the first chart offered ends it.
10. **The camera's keyhole byte is never set, so the DB keyhole is inert.** `DbKeyhole` is the column's writer
    and fills the shader inputs, but `Camera+0xe50` reads 0 on the CX camera shell every pass (measured:
    33911 passes, "0 armed / 33911 with the aperture shut"). No `Camera::` function in the decompile writes
    it, so the writer is inlined somewhere in the game's camera update — that is the missing piece. The
    counter line says so rather than letting it look like it works.
11. **Two housekeeping facts about the handle scheme itself.** `DbTable.Free` masks the generation with
    `0xFFF`, which also clears the `1u << 31` live bit — correct, and worth knowing because it means `LiveAt`
    and `Valid` are cleared by one write. And the generation is 12 bits and wraps, so a stale `DbId` aliases
    again after 4096 frees of the same slot; nothing counts or guards the wrap. `DbTable.Mark` takes a raw
    index with no bounds test, so an out-of-range mark throws rather than corrupting.
12. **`Tiles` and `Lights` dirty sets are never `TakeDirty`'d in production**, so their bits saturate for the
    session. Bounded, not an overflow — a slot enters the list at most once per window — but a reader
    expecting `DirtyAt` to mean "changed this frame" will be wrong on those two.

---

# Part 2 — What the contract deletes

Everything below existed only because the engine produced the records and CX inferred their lifetimes. Under
the scene database nothing produces them, so nothing has to be inferred. **Deleted 2026-10-07** with the
engine-scene path; the table stays as the record of what went and why.

| What goes | Where it lives | Why the contract does not need it |
|---|---|---|
| The `Part::~Part` death notice and its cascade | `DrawFunnelService.OnPartDtor` (`managed/Alloy/DrawFunnelService.cs`), installed by `hooks.InstallPartDtor` in `DrawFunnelService.Initialize`; it drives `SkyBox.Revoke`, `PartTable.MarkFreed`/`Die`, `NonMeshParts.Died` | A part record is a `DbTable` slot. `DbTable.Free` bumps the generation, so every outstanding `DbId` is dead at the free. There is no borrowed pointer to be told about. |
| The #221 tombstone | `PartEntry.Freed` + `PartTable.MarkFreed`, `PartTable.EntryLive` (`managed/Alloy/Scene/PartTable.cs`); the same shape in `AlloyCensus.Find`, `GobLod.Insert`'s stamp | A tombstone exists to stop a **recycled address** reviving a record. A `DbId` carries a generation, so the recycled-identity class of bug cannot be expressed (`DbId`'s own summary says so). |
| The VDO fingerprint and the recycled-address re-harvest | `VdoSig` (`managed/Alloy/Core/EngineRefs.cs`), `_vSig` + `SlotHolds` (`managed/Alloy/AlloyRenderer.Vao.cs`), the re-fingerprint in `VdoSlot` (`managed/Alloy/Scene/AlloyRenderer.Enroll.cs`), and `Beams`' copy of all three | There is no VDO. Geometry is a `MeshArena` range keyed on `MeshArena.ContentKey(MeshDef)` — content, not an address — and a range is handed back by `MeshArena.Release`, told. |
| The VAO drip and the per-VDO VAOs | `AlloyRenderer.Vao.cs` in its entirety bar `BindBatchState`/`VaoName`: `Harvest`, `HarvestFailed`, `DripVao`/`DripArm`/`DripForget`/`DripRearm`, `EnsureVao`, `_vVao46`/`_vOwned`/`_vStale`, `ForgetLodVao`/`ForgetLodSlot`; `Beams._vVao` | Every DB draw comes out of the one arena: `AlloyRenderer.DbWriteColumns` writes `_oVao[i] = 0` and `_oLodBit[i] = DbArenaVaoBit`, so there is no per-record VAO to mint, retire or wait for. |
| The enrolment walk | `EnrollBegin`/`EnrollStep`/`EnrollOne`/`EnrollFinish`/`TryEnrollDelta`/`RebuildOrder`/`VdoSlot`/`MeshCensus` and the walk-budget state (`managed/Alloy/Scene/AlloyRenderer.Enroll.cs`), driven from `OnRenderSinglePass` | `AlloyRenderer.DbSeat`/`DbRelease` write a slot's stable columns from a `(DbId part, int recipe)` pair on a dirty bit. The order is rebuilt only when `DbSetGen` moves (`AlloyRenderer.DbOrder`), which only a seat or a release bumps. |
| `SceneWalk`'s sweep and its audit | `SceneWalk.Sweep`, `SceneWalk.Tick`, `SceneWalk.Enrol`, `SceneWalk.TryBind`, `SceneWalk.Rebind`, the `alloy.walkaudit` cadence and the `[enum] audit misses=` line (`managed/Alloy/Scene/SceneWalk.cs`) | A sweep answers "is this part still reachable?". The game says when a part dies; `DbFeed`'s `ObjectDied` releases the whole part range on that one edge. |
| The `Gob::SetScene` leave notice | `SceneWalk.OnGobSetScene` → `SceneWalk.ReleaseGobTrees`, and its two consumers `GobXform.Forget` and `GobLod.Forget` | `DbScene.Leave`/`DbScene.ObjectDied` is the same news, told by the setter that caused it, with the handle in hand. |
| The census refusals | `AlloyCensus.TryOwnCandidate` and the refused-gob set (`managed/Alloy/Scene/AlloyCensus.cs`), the `NOT DRAWN` clause and the `skinUnownable`/`gobParts`/`RefusedGobs` tallies on the `[alloy]` period line | Those refusals described engine states Alloy could not express (a lit part it could not own, a skin without a bind pose, a gob with one refused part). A DB part has no engine state to refuse: the remaining refusals are CX's own ceilings (`DbFeed.NoRecipe`, `MeshArena.Refuse`, `DrawRecipe.Spilled`), which are faults, not vanilla fallbacks. |
| `OwnedTextures`' borrowed-pointer verdicts | `OwnedTextures.NoteUnbuilt`/`DripRebuilds`/`_rebuildTried` and its `AreaEdge` drop (`managed/Alloy/OwnedTextures.cs`), fed by `AlloyRenderer.Batches.cs`'s idless-diffuse branch; `SkinPose.NoteUnprepared`/`DripPrepares` on the same anchor | Both exist because the engine owned the texture and the bind pose and might never have built them. CX decodes its own textures (`TexRegistry`) and builds its own skin data from `ModelDef`, so there is nothing to retry and no ledger keyed on a `CAurTexture*`. |
| The area edge's address-keyed revoke list | `AlloyRenderer.AreaEdge` (`managed/Alloy/AlloyRenderer.AreaEdge.cs`): `RevokeAll` + `PartTable.WipeTables` + `OwnedTextures.AreaEdge` + `GobXform.AreaEdge` + `GobLod.Reset` + `InvalidateRecords`, consumed by `TakeAreaEdge` from `OnRenderSinglePass` | Its whole subject is "identities the engine recycles across an area". Every DB table is keyed on a handle or on content, so an area change invalidates nothing in them — which is also what makes the next area re-use every mesh and texture it shares with the last. |
