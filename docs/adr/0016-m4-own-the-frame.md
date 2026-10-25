# 0016 — M4: own the frame

Status: accepted (#203 grilling, 2026-08-26). Scopes ADR 0009's M4; sits under ADR 0010; the
successor to ADR 0011's role for M3.

## Context

M3 closed with Alloy owning the in-world colour and shadow pipeline but not the frame: anchored at
`Scene::FadeDoors`, inside the engine's own `Scene::RenderSinglePass`, resolving into the engine's
attachment, sharing the engine's depth buffer. #132 dropped M3's frame-time gate for exactly that
reason — while the anchor, the clear and the present belong to the engine, a vanilla comparison
measures the engine. the research on CX-164 reached the same place independently: four
of its six readiness gates (G1 no engine GL state reads, G2 own the resources, G3 draw every class,
G4 own the present) are restatements of frame ownership.

#203 scoped M4 the way #107 scoped M3. Its first act was to reverse-engineer the engine's frame
loop *above* `RenderSinglePass`, which nobody had looked at. That RE is recorded here rather than in
the closed ticket, because it invalidated two of the scoping ticket's own premises.

## Decision

**M4 is: Alloy draws the entire 3D world, from its own resources, from its own loop. The engine
keeps the 2D UI and the present.**

The purpose is **control** — a renderer that stably draws everything vanilla drew. Vulkan-readiness
is a byproduct bought by hygiene, not by a layer. **Perf is neither an M4 goal nor an M4 gate**;
modern, high-performance techniques are required, but no frame-time number gates the milestone.

### The steps, in order

1. **Hygiene.** The `PassState` pass-preamble struct, a binding-slot `const` table asserted in
   `ShaderContract.SelfCheck`, written resource lifetimes, and the rule **no `Gl.*` in
   `Alloy/Scene/`**. No behaviour change; independently shippable. (RHI research §6.2–§6.4.)
2. **Retire the GL-state mirroring.** Replace the 49 `glGetIntegerv` and 17 `glIsEnabled` sites with
   a `RenderInterface::ResetRenderStates()` (0x45d330) call on hand-back. Closes G1.
3. **Own textures.** Alloy-created GL textures, not borrowed `AurTextureGetTID` names.
4. **Own geometry buffers.** Alloy-created vertex/index buffers copied from engine VDOs, part-keyed,
   freed on `Part::~Part`/revoke. Log a VRAM high-water mark.
5. **Own the depth buffer and render target.** Stop sharing the engine's depth attachment. Still
   resolves into the engine's attachment. The last step under the current anchor.
6. **The cutover.** Replace `Scene::RenderSinglePass` with a **mutate-only shim**, behind a config
   key, default off.
7. **Recompute the refusal set.** Every refusal becomes frame-start-only; add an on-screen notice
   and a logged reason on Alloy dropout. (#216; the recomputed set is `docs/alloy-refusals.md`.)
8. **Punch list.** Play, note what is missing, own it.
9. **Flip the default on. M4 closes.**

Geometry ownership lands **before** the cutover deliberately. It is the step most likely to surface
lifetime bugs, and those want a working engine path behind them; after the cutover every bug is
ambiguous between "the shim broke it" and "owned geometry broke it".

### The exit bar

> On the Alloy path, in a world frame (`Scene+0x188 != 0`), the original `Scene::RenderSinglePass`
> is never called, and the replacement shim issues **zero GL draw calls** — asserted by a draw-call
> counter wrapped around the shim, not by inspection. Alloy's render target, depth buffer,
> vertex/index buffers and textures are all Alloy-created. Verified live across a representative set
> of areas — interior, exterior, water, heavy-creature — with the punch list of missing classes
> empty. Frame time is measured and reported; it is **not** a gate.

Two counters, one instrument: the **shim counter** is the binary acceptance mechanism; the **scene
draw-call count** is a diagnostic for FPS-dip triage and gates nothing.

#### Amendment (#236): zero *undirected* draws

The bar above says zero GL draw calls, full stop. Live, that made ownership latency a **correctness**
bug: a part Alloy has not enrolled yet is handed back to the engine by the bucket walk, the shim ate
the hand-back, and the part was simply not drawn. Creatures rendered in pieces and a camera spin left
a three-second hole. Pre-cutover the same latency cost a frame of vanilla shading and nothing else.

So the bar is **zero draws nobody directed**. The walk marks the draws it is deliberately handing
back (`RspShim.HandBack`, set for the whole of `BatchService.PartDraw` and cleared only where the part
handed back is one Alloy owns), and those reach the original. `PASSED` on the `[rspshim]` line is
still the acceptance counter and still must read 0 — it now counts only a draw that reached the engine
by no route the walk asked for. `handed back/frame` beside it is the ownership-latency measure: it
falls as enrollment catches up and is not required to be zero.

This is a widening of the bar, not a retreat from it. The shim still issues no draw of its own, and
every engine draw the *bucket walk* lets through is attributable to a named refusal in it.

One thing the let-through also repairs, found while scoping it: BatchService's funnel observer submits
the rest of a batched group by `glMultiDrawElementsBaseVertex`, which reaches neither funnel and so was
never subject to the swallow. Under the cutover a batched group therefore drew during the window *minus
its first member*, whose vanilla draw the shim ate — invisible to `PASSED` by construction. The first
member is handed back now and the group matches it.

The live counters also settled what ownership *means* per frame. `BuildBatches` already refuses to draw
an owned part the engine has stopped walking, so holding an off-screen part costs a compare and a
`continue` — but the enrollment scan used the same one-frame window and therefore *dropped* it. Measured:
`stale` was the only non-zero rejection, 5465 of 12533 active parts, 5025 of them walked inside the
cadence. Enrollment was discarding ~5000 parts per scan for being behind the camera, and a returning part
is already `StActive` so it never reaches `Complete` and never latched a re-enrollment. That was the whole
of the residual camera-spin pop-in. The enrollment window is now `OwnSightingWindow` (60 frames), separate
from the one-frame *draw* window, and the liveness the old window bounded is re-proven explicitly
(captured node/material re-checked against the live part before any deref).

Scope, stated rather than assumed: the mark covers `PartDraw` only. Three hand-backs sit outside it —
the sky window, the not-in-a-bucket catch-all, and the skin bail — and stay swallowed. Whether any of
them fires inside the window is unmeasured; the `handed back/frame` figure and a live session are what
would settle it, and marking them on a guess about who draws the sky is exactly what this bar exists
to prevent.

### Ownership scope

**Own everything.** Temporary non-rendering is acceptable — missing classes get spotted in play and
filed as per-class RE tickets. That method has one documented blind spot (see *door clickability*
below), so **non-visual coupling gets checked by RE, not by looking**.

**G2 is in M4, as resources only.** Alloy creates its own buffers and textures; the engine remains
the authoritative scene *database* — what exists, where it is, its animation and fade state. Owning
the scene data itself (parsing geometry ourselves) is a different project and out of scope. This
amends ADR 0009's reader doctrine; see that ADR's second amendment.

Residency is **upload on enroll, free on revoke, no budget**, with a logged VRAM high-water mark. A
budget before a measurement is #149's mistake in a new costume. Budget work lands with the perf pass.

## The frame loop above `RenderSinglePass`

```
main -> Update(void*) @0x430030                    <- THE frame function
  RenderInterface::Clear(3)                        <- frame clear (COLOR|DEPTH|STENCIL)
  CClientExoApp::MainLoop
    CGuiMan::UpdateAndRender @0x580450             <- all 3D and all GUI
      per view: Camera::RenderScene @0x4a7920      <- per-view Clear(2), view/proj setup
        Scene::Render @0x4762d0
          ManageSceneBSP        @0x4763e9          <- tile-fade commit, BEFORE the dispatch
          AddPartsToDrawBuckets @0x476533
          call *0x218           @0x47653e          <- Scene::RenderSinglePass, the ONLY call site
      overlay/GUI views LAST (Scene+0x188 == 0 -> Scene::RenderOnlyGobs, vt+0x278)
  AurRenderStrings
  RenderInterface::SwapBuffers  @0x430924
```

All addresses are nm addresses; the Ghidra project is rebased +0x100000.

- `Scene::Render` discriminates on **`Scene+0x188`** (tile BSP root). A world scene dispatches to
  `RenderSinglePass`; `== 0` takes `Scene::RenderOnlyGobs`. GUI 3D panels, **the main menu and
  loading screens** all take the latter and never enter `RenderSinglePass` — so the engine's UI
  renders untouched through the entire cutover, with nothing to build.
- **There are three present paths**: `Update()` @0x430924, the nested `UpdateScreen()` pump
  @0x42eb83 that drives every blocking area load, and movies @0x1169545. The shim fires under the
  nested pump too, because that pump calls the same `Scene::Render` — **loads need no special case**.
- `RenderInterface::BeginScene`/`EndScene` are **no-ops**; do not use them for bracketing.
- `GLRender` shadow-caches essentially all GL state, and **`RenderInterface::ResetRenderStates()`
  (0x45d330) is a callable full resync** already run after every `SwapBuffers`. That is what makes
  step 2 cheap: one call on hand-back replaces the mirroring.

## `RenderSinglePass` is not draw-only

#203's own slogan — "`RenderSinglePass` is not entered rather than hollowed out per item" — is not
achievable. Full classification of RSP's 57 calls found drawing braided with seven pieces of
per-frame bookkeeping, four of which have **no other caller in the binary**. Skipping RSP outright
freezes:

1. **Door fades — and door clickability.** `Scene::FadeDoors` (0x467990, `Scene` vt+0x260) contains
   **zero GL calls**; it is pure arithmetic integrating `Gob+0x10c` by `DeltaT` and clamping to
   `[mindooralpha, 1.0]`, and it is the sole writer. `Gob::HitCheck` (0x438510) and
   `Gob::HitCheckPart` (0x437e87) compare `mindooralpha` against that field to decide whether a door
   is hit-testable, so a frozen door becomes permanently un-clickable or permanently clickable.
   **This is non-visual breakage, invisible to eyeball verification** — the named blind spot in the
   ownership-scope method above. *(Amended by CX-89, 2026-09-30: the fade is ported —
   `CX.Alloy.DoorFade` integrates the same field by the same rule — so `FadeDoors` is no longer called.
   `HitCheck` is at 0x4381d0; 0x438510 is the comparison site inside it.)*
2. **All particle simulation.** `Scene::DoEmitterBucket` (0x476f00, vt+0x1e8) is the only call site
   of `PartEmitter::AnimateParticles(DeltaT)`. Skipping it freezes particle *advancement*, not just
   drawing, so Alloy's particle pass would draw frozen particles. **Superseded by CX-86**: Alloy owns
   the advancement now (ADR 0018's CX-86 amendment) and the vcall is dropped — the frozen-particle
   trap this item names is why it took a port rather than a deletion.
3. **Grass wind and bin LOD.** `Scene::DoBinBucket` (0x474c50, vt+0x1c0) calls
   `CAurTriangleBin::UpdateGrassForWind` and `SetDistanceSquared`.
4. **Lens-flare occlusion fade.** `Scene::RenderLensFlares` (0x471860, vt+0x230) integrates
   `PartLight+0xa0` by `DeltaT`. Self-contained. *(Superseded by CX-85: `Halos` integrates that field from
   its own depth probe and the shim no longer calls the virtual.)*
5. **`focuslighting` (0x19ab748).** RSP is its only writer (0x46ec3d, 0x46fe64); it is read by the
   exported `Scene::GetLightingValueAtFocus` (0x462aa0, vt+0x138). *(Superseded by CX-85: `LightFade.Tick`
   writes it, and the reader turned out to be `CNWCArea::GetIsDark` @0x705930 through that export.)*
6. **All framebuffer and subframe setup.** `Camera::InitializeFrameBufferResources` (0x4a6fd0) has
   **exactly one caller: RSP**, and is the resolution/MSAA/HDR/bloom-scale change detector.
   `Camera::CopyRenderSubframe` (0x4a6b90) and `Camera::DoFrameBufferEffects` (0x4a8eb0) likewise
   have no other callers; the latter also lazily compiles the post-effect shaders.
7. **The shadow-light lists** `Scene+0x60` / `+0x70`, rebuilt from `LightManager::GetShadowLights`.
   The only located consumer is inside RSP.

**So the cutover is a mutate-only shim**: it calls the bookkeeping and issues no draws. Every
mutator is a `Scene` vtable slot callable directly, and three of them are ones Alloy already hooks
to suppress drawing — the machinery inverts rather than being built.

**Not broken by the skip:** the draw buckets. `ClearBuckets`, `AddPartsToDrawBuckets` and
`ManageSceneBSP` all run in `Scene::Render` before the dispatch, and `RenderDrawBucket` only
iterates; it never resets.

**Tile fading also survives**, for the same reason. `TileData+0x70` — the factor
`RenderDrawBucket` reads for bucket 8 — is committed at the top of `ManageSceneBSP` (0x47fa80, store
at 0x47fb40), called directly from `Scene::Render` before the dispatch. The layout is a two-slot
commit: `+0x70` is the committed alpha, `+0x74` the pending alpha (integrated at 4.0/sec by
`HandleFadingInBucket` 0x47f670 and `HandleFadingOutBucket` 0x47f870), `+0x78` a hold timer, `+0xc0`
the state enum. **To control tile fade, write `+0x74`** — anything written to `+0x70` is overwritten
at the next frame's commit. Fades advance even on camera-static frames.

The M3 anchor choice was better than it looked: `FadeDoors` is pure mutate with zero GL, which is
why anchoring there worked at all. *(CX-89 kept the step and dropped the hook: the anchor is a direct
call from the shim, so nothing is anchored on an engine function any more.)*

## What is out of M4, and why

- **The present, 2D drawing, and the UI system.** The engine's UI draws flat quads on top of a
  finished Alloy frame; it shares none of Alloy's buffers, constrains none of its draw order, and
  limits no area size. Owning it makes nothing faster and lets nothing more be drawn. The 800×600
  layout problem lives in `CGuiPanel`'s **layout**, not in the rasterizer, so drawing the same
  widgets with Alloy's GL changes none of what makes it worth wanting.
- **The scene database.** See *Ownership scope*.
- **An RHI layer.** A Vulkan backend rewrites the submission half, which has to be rewritten anyway,
  and rewrites *none* of the RE knowledge in `Alloy/Scene/`. The boundary that preserves that
  knowledge is **extraction-must-not-touch-GL** — step 1 — not `IRhiDevice`. Re-ask at M5, when a
  second consumer is in sight. the research on CX-164 §6.5 still applies.
- **ADR 0011's filed M4 inheritance**, which moves to M5: planar water reflection, the post stack the
  resolve plugs into, and same-frame creature pose capture. (OIT was already ruled out by #110.) All
  three are technique work on an owned pass — the same category ADR 0009's 2026-08-26 amendment
  moved out of M4.

## Consequences

- **Owning the UI system is the hard prerequisite for any Vulkan backend.** GL cannot draw into a
  Vulkan swapchain; a Vulkan backend needs every pixel, UI included. The panel-by-panel `CX.Ui`
  work (ADR 0004, with the chatbox as the shipped precedent) is therefore not a modding nicety but
  the longest-lead item in the project, and it should run **in parallel** with M4 rather than after
  it. Nothing in M4 blocks it.
- **G5's permanence dissolves.** The RHI research assumed a Vulkan backend would be a third
  *runtime* mode alongside GL-Alloy and vanilla, and called that the trap an RHI is meant to
  prevent. It will instead be a separate **configuration** with no runtime swapping, which is not
  that trap. G5 failed on premises, not on facts. Nothing else in that research changes.
- **The suppression machinery needs no new shape.** `BucketGate` has four call sites outside its own
  file (`BatchService.cs:111`, `:123`, `:740`, `:764`), and a walk that is never entered fires no
  hooks. ADR 0010's vanilla mode keeps them all callable; that is the whole requirement.
- **Under the cutover the default framebuffer becomes the resolve target every frame**, so
  `PostChain.Precheck`'s "default framebuffer" refusal (`PostChain.cs:187`) becomes the *normal*
  case. The whole refusal set must be recomputed at step 7, and every refusal becomes frame-start
  only: there is no engine frame behind a mid-frame fallback once the shim is in.
- **Alloy must allocate its own depth buffer.** It currently shares the engine's depth attachment
  (`PostChain.cs:216-218`); after the cutover there is no engine world FBO to borrow one from.
- **The cutover removes six engine overlays, five stencil-shadow draws and bucket 5's
  `CopyRenderSubframe` wholesale**, because all of them live inside RSP. "Let the engine keep
  drawing class X" stops being an available option on the Alloy path.
- ADRs 0009 and 0010 are amended alongside this one; ADR 0015 needs review, because G2 changes what
  "borrowed" means for geometry and textures.

## Step 9 (#218): the flip, and what the flip does not settle

`ownframe` defaulted to **1** since #218 and was **deleted** by #458: submitting inside the engine's
frame was unusably slow, not a fallback. `vanilla=1` is the one escape hatch and takes the whole Alloy
path down; an unarmed shim is now an `AlloyGate` refusal, so the shim's gate is
`RspShim.Ready && AlloyGate.Alloy`.

The bar's resource half is now asserted rather than assumed. `RspShim.Bar` runs on the first frame
the shim takes and logs `[bar]`: the render target and depth buffer are Alloy-generated names
(`PostChain.Fbo`/`DepthTex`), the geometry and texture tables are armed, and neither has **refused**
a copy. The refusal counters are the discriminating term — both tables fall back to the engine's
name per resource when a copy fails, so "armed" is not "owned". The `[rspshim]` period line carries
the same two as a **per-period delta**, so a later area that regresses one is caught without a
restart — a lifetime total could not, because neither counter ever decrements and one transient
refusal would then read as a standing fault for the rest of the session.

`[bar]` is a verdict, not a stand-down. By the time it can be evaluated the engine's frame is gone;
a shim that refused to finish the frame over one borrowed texture would trade a wrong-looking world
for no world.

**Still owed, and #218 stays open until it is paid:** the live pass. Everything above is static. The
three criteria a running client has to answer are `PASSED 0` on `[rspshim]` in interior, exterior,
water and heavy-creature content; `[bar]` reading `resources OWNED` in each; and a frame time
recorded per area. `handed back/frame` beside `PASSED` is the ownership-latency measure and is
informational — the amendment above is what makes it so. On the frame-time number, ADR 0017 already
holds the measured post-cutover regression; it is reported here, not gated, exactly as this ADR says.

## Unconfirmed, and named rather than assumed

- Callers of `Scene::GetLightingValueAtFocus` (reached via `call *0x138`) were not resolved. *(CX-85 did:
  `CNWCArea::GetIsDark` @0x705930, on the player's own creature light, with hysteresis at 0.6062/0.866 —
  called from `CNWCObject::ActivateHardCodedVisualWithHighestPriority`.)*
- Whether anything outside RSP reads the `Scene+0x60` / `+0x70` shadow-light lists.
- Whether the present or GUI path reads `Camera+0x528` after RSP returns.
- The identity of `CGuiMan+0x48`, the second always-rendered view.

## Amendment (2026-10-25, CX-315 / CX-340): the RHI layer, re-asked and taken

*What is out of M4* declined "an RHI layer" with "Re-ask at M5, when a second consumer is in sight" (ADR 0014's
#206 amendment records the same decline). The second consumer is now in sight: CX-313 puts Alloy on Vulkan, as a
replacement for GL Alloy, not a third render mode. CX-315 re-asked, and its decided answers (2026-10-25, comment #2)
take the seam:

- Passes declare pipeline state and their resource reads/writes, and record into a `CommandList`; a backend
  executes it. The types live in `managed/Rhi/` (`CX.Rhi`, no GL, no Vortice); the GL backend in
  `managed/Graphics/Rhi/` behind an `IDevice` interface, not a function-pointer table.
- Recording happens on the render thread, meaning the thread that draws the published frame: the
  `RenderSnapshot` publish is the hand-off from the game thread, and `alloy.renderthread` is not required.
- It ships GL-only. CX-340 lands the types with no pass on them; passes move one per PR (CX-341, `SsaoPass`,
  first), under a hard 0.15 ms CPU budget with a revert, and `tools/gates/rhi-seam.sh` ratchets the direct
  `Gl.*` call sites under `managed/Alloy` down to zero.

What the rejection protected still holds: extraction-must-not-touch-GL stays step 1, and the RE knowledge in
`Alloy/Scene/` is untouched by the seam. The seam sits on the submission half, the half a Vulkan backend
rewrites anyway.
