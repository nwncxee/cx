# 0011 — The M3 frame graph

Status: accepted (#108 grilling, 2026-08-20). Constrains ADR 0009's M3; sits under ADR 0010.
Settles the frame-graph slots ADR 0009 sketched and #75 left open for the opaque path only.

## Context

#75 settled the M1 frame graph for the **opaque** path. M3 adds the passes it left out —
transparents, water, post — and inherits one defect nobody decided on: the shadow maps the owned
colour pass samples are baked at the *present*, after the colour pass that reads them
(`CasterPass.EndFrame()` from `UiHostService.OnSwap`), so the lit stage runs on the previous
frame's cascades and cubes.

Two research tickets moved the ground under the question. #103 established that **nothing sorts
inside a draw bucket** — statics walk in texture order, dynamics sort per-GOB only, `glDepthMask`
stays on for everything but additive, and the whole model survives on a 0.2 alpha cutout. #107 then
made transparent/additive/fade/water **all-owned-or-none** and pulled PBR into M3. So M3 is not
reproducing an ordering; it is choosing one.

Two binary facts settled the mechanics (Ghidra, this session, addresses are nm):

- **Bucket 0's walk is unconditional.** In `Scene::RenderSinglePass` (0x46e5f0) only buckets 5 and
  6 are gated on `DrawBucketManager::HasItemsOfType`; 0, 1, 7, 8 and 4 are straight-line calls.
  Draw order is 0 → [Sky] → 1 → 7 → 5 → 8 → 6 → 4. Buckets 2/3 are reached from
  `RenderDrawBucket` only inside the wireframe-debug branch.
- **The engine does not use the GL matrix stack.** Projection and view live in a software stack,
  `m_aurMtxStack` (0x19e2f60; projection at +0x18, view at +0x70), written by `Camera::RenderScene`
  (0x4a7920) *before* the vcall into `Scene::Render`. `Camera` has no matrix getter — it exposes
  parameters only. The drift-proof handle is `GLRender::GetModelMatrix()` (0x4c9340) minus 0x58
  (view) and minus 0xB0 (projection).

## Decision

### The graph

```
[pass slot — Scene::AddPartsToDrawBuckets, after-original]      GL bracket 1
  light master-list walk
  sun cascades + local light cubes          world casters, same-frame

[frame anchor — AlloyRenderer.FrameAnchor, called from the shim (#132, CX-89)]  GL bracket 2
  1. depth prepass          opaque owned, depth-only, published
  2. opaque + cutout        PBR, IBL, alpha-test / alpha-to-coverage
  3. skybox                 depth-tested against the prepass
  4. blended                sorted back-to-front, depth-write off
  5. framebuffer copy       one copy, colour + depth → units 11/12
  6. FB-sampling, then water
  7. additive + beams + emitters
  8. resolve                linear → display space   (lands at the M3 exit step)

[present — GLRender::SwapBuffers]
  creature pose capture                     read by the NEXT frame's bake: one frame of pose lag, named below
```

### The shadow slot is split

World casters — sun cascades and local light cubes — bake at the pass slot, ahead of the colour
pass that samples them. They are view-independent and motion-classified, and `ShadowFeedService`'s
BSP re-walk has already run by then, so this is a move, not a new mechanism. It costs no extra GL
bracket.

Creature casters stay at the present, because their poses are captured *during* the colour pass
(`CasterPass.SkinBegin`/`SkinEnd` bracket `PartTriMesh::Draw`) and do not exist before it.

**Amended by #118.** What stays at the present is the pose *capture*, not a second bake. A literal
present-time creature bake would be a third GL bracket, which the budget below forbids, and the
upgrade path named two sections down — "add a pose-capture walk ahead of the shadow slot … what is
missing is a reader, not the data" — only reads as an upgrade if creatures already bake *at* the
slot off stale poses. So there is one bake site, at the pass slot; creature records latched there
are the previous colour pass's. The lag magnitude is exactly what this ADR named, and the question
stays separable because the fix is still a capture walk and touches nothing else. The graph diagram
above should read "creature pose capture" where it says "creature shadow bake".

The split was not chosen for speed — both arrangements cost the same two brackets. It was chosen
because moving everything early would have forced the creature-pose question, and this way the
question stays separable.

### Named approximation: creature shadows lag one frame

Creature shadows sample a pose captured on the previous frame. At 60fps that is ~16ms of skeletal
delay on a soft, low-contrast, usually-small feature. It is accepted for M3 **as a decision, not as
a leftover**, and it is the one place in the graph where "same frame" is not true.

**Upgrade path**, if it ever reads badly — and it is the most likely of M3's approximations to,
because pose divergence grows with animation speed and with the number of creatures on screen:
add a pose-capture walk ahead of the shadow slot. The skeletons already exist at the pass slot
(`AddPartsToDrawBuckets` has run); what is missing is a reader, not the data. The cost is a second
walk over every visible creature, every frame, for the one caster class that never caches — which
is why it is not being paid up front.

### Transparency: classify, don't sort everything

Parts are classified at capture into two sets:

- **Cutout** — the 0.2-alpha-discard majority. Drawn in the opaque pass, depth-write on, no sort,
  alpha-test or alpha-to-coverage. This is most of the content.
- **Blended** — genuine alpha. Sorted back-to-front per part, depth-write off.

Only the blended remainder pays a sort, and it is small enough that a per-part distance sort is
cheap. This is also what makes #107 §5's grass punch-through structurally impossible rather than
fixed: nothing alpha-blended writes depth.

OIT (weighted-blended or otherwise) is **M4**, not M3.

### Depth prepass: in, opaque-only, and published

Opaque Alloy geometry, depth-only. It is *published as a resource*, not kept private to the colour
pass — water's screen-space depth read and M4's SSAO/GTAO both want exactly this buffer, and
building it without a consumer contract means building it twice.

Cutouts in the prepass are a **measurement** question, not a design one: they cost a texture fetch
per fragment to save shading later, and alpha-to-coverage grass changes that arithmetic. Measure
overdraw before adding them.

**Amended by #119.** Shipped as ADR order step 1, colour-masked, over the same staged batches as the
shading pass. Two things the design text did not name and the build settled:

- The published contract is `DepthResource` (attachment name, `IsTexture`, size, pass generation), not a
  rebuilt `InDepthPass` bool. `IsTexture` is published rather than assumed because until the exit step the
  buffer is the engine's own attachment, which may be a renderbuffer and therefore unsampleable.
- Re-testing prepass depth in the colour pass requires every Alloy vertex stage to compute one clip
  expression, character for character, under `invariant gl_Position` — the lit stage's association differed
  and was unified. The colour pass tests LEQUAL, not EQUAL, so a stood-down prepass stays correct.

`ShadowInputs.InDepthPass` has **zero writers** — a vestige of the interim `RenderShadows` pipeline
retired in `0981181`, leaving seven permanently-false guards across `BatchService`,
`OwnedDrawService`, `ShadowCaptureService` and `DrawStatsService`. It is deleted and rebuilt with
real writers. A flag that is always false is worse than no flag, because the next reader trusts it.

### Skybox draws after opaque, before blended

Vanilla draws it between buckets 0 and 1, depth mask off, replace-blend — which is exactly why it
paints over bucket 0, the ordering hazard #107 §3 wanted removed. With a depth prepass in hand,
drawing it after the opaque set shades only the pixels the world did not cover, and placing it
before the blended pass means blended geometry composites against a finished sky.

### Water keeps vanilla's placement; its reflection source improves

The **copy placement** is vanilla's: one snapshot taken before the FB-sampling class, water drawn
after it. Everything at step 7 — additive, beams, emitters — is therefore invisible to water's
reflection and refraction, exactly as in vanilla.

That is not inertia. Additive does not write depth (`Material::SetBlendingMode`, #103) and water
does, so drawing FX before water means water paints over any FX in front of it: you would gain
reflections of torch glow and lose the torch glow. And a second copy taken after the FX is
circular — the copy must contain them, so they must draw before water, which is the same move
again with the FX drawn twice.

**What does improve is the source.** SSR reflects only what is on screen, so a sea reflects the
shoreline in front of you and nothing else — the sky it should be reflecting is off-screen above
the camera. M3 feeds the **per-area prefiltered IBL cubemap probe** (#107 §4, being built anyway
for material shading, not per frame) as the fallback wherever the SSR ray march misses: off-screen,
no hit, past the frame edge. SSR is **not** deleted — it stays for near-field contact detail, where
it is the right technique. True planar reflection is M4.

This matters more than a graph slot for SoW specifically, which is a sea and seaside setting.

### One framebuffer copy, not two

The engine takes two `Camera::CopyRenderSubframe` snapshots per frame — subframe 1 before bucket 5
("Sample Framebuffer") and subframe 6 before bucket 6 ("Water"). Each is a full colour+depth
`CopyFramebuffer` plus a bind to texture units 11/12.

M3 takes **one**, before the FB-sampling class, and moves fade geometry ahead of it so water loses
nothing from its reflection. Fade has no ordering constraint against the FB-sampling classes — under
the classification above it is a blended-class member — so the move is free. The engine takes two
because its bucket order is fixed, not because two are needed.

### Colour space: one resolve stage, flipped at the M3 exit

The Alloy frame becomes **linear end to end**, with a single fullscreen resolve encoding to display
space as the last step inside bracket 2, writing straight into the engine's framebuffer as its
output — no third bracket, no blit. The Alloy frame is *finished* when we hand the context back,
which makes the seam with the engine's 2D UI a single inspectable point.

This is cheaper than the per-shader encode it replaces — one `pow()` per screen pixel instead of one
per fragment, so overdraw stops paying for it — and it is what makes the blended pass correct, since
blending in gamma space is wrong and gets worse the more classes we own.

It is **not** ADR 0009's M4 post stack. Tonemapping, exposure and bloom stay M4; the resolve stage
is what gives them somewhere to plug in.

It also retires `OwnedLights.cs`'s unconditional sRGB revert, which today ignores the
`GetGlobalShaderParami(0)` gate the engine honours. With the encode in one place, that parameter is
read once instead of assumed by every shader.

### The frame anchor: bucket-0-enter now, RenderSinglePass entry at the exit

Two independences get conflated under "a separate rendering pipeline", and only one is real:

- **Trigger independence** is neither achievable nor desirable. Something must say a frame is
  happening, and the engine's frame is the only clock in the process. The question is only *which*
  callback, and whether it can dry up.
- **State independence** — not reading the engine's FBO, viewport, or matrices — is what actually
  makes the pipeline separate, and the depth prepass and the resolve stage already commit us to it.

Through M3's build-out the anchor stays at `RenderDrawBucket` bucket-0-enter, inheriting engine
frame state. That walk is unconditional, so the anchor cannot dry up as ownership grows.

**The M3 exit step** — one flip, not a drift:

1. bind our own HDR render target,
2. read view and projection from `m_aurMtxStack` instead of inheriting engine frame state,
3. move the anchor to `Scene::RenderSinglePass` entry,
4. add the resolve stage,
5. delete the per-shader sRGB encode.

Steps 1–3 and 4–5 are the same event wearing two hats: the frame where we stop inheriting engine
frame state. Doing it as one step is what keeps it from becoming two half-transitions, and it is a
**named M3 exit criterion**, not a footnote — it is the kind of thing that gets forgotten and then
rediscovered as a bug.

> **Amended by #132 (2026-08-23), step 3 only.** "RenderSinglePass entry" turned out not to be
> reachable, and the correction is worth recording because the reasoning that put it here missed a
> fact. RSP's first act is `Camera::InitializeFrameBufferResources` (0x4a6fd0), and its **steady
> state** path — the size compare at +0x240 falling through to +0xed — is
> `SetRenderSubframeTarget(0)` then `Clear(7)`, every frame, not only on resize. So at literal entry
> **Amended by CX-89 (2026-09-30).** The anchor is the same STEP but no longer a hook: `RspShim.Run` calls
> `AlloyRenderer.FrameAnchor` directly, and `Scene::FadeDoors` is neither called nor hooked on an Alloy frame
> (`CX.Alloy.DoorFade` does its work). The clear the paragraph below turns on has not run since #215 replaced
> `RenderSinglePass` wholesale; what keeps the step is the bookkeeping order, not the clear.
>
> the world FBO is neither bound nor cleared: step 1 frees the owned *draw* from the engine's
> binding, but the *resolve* still has to land in the engine's attachment after that clear, and
> deferring it past the clear costs a second colour bracket this ADR does not have.
>
> The anchor is therefore an after-original hook on `Scene::FadeDoors` (0x467990, Scene vtable slot
> 0x260) — the last engine call inside RSP before `RenderDrawBucket(0)`. At its return the world FBO
> is bound and cleared, view and projection are valid (they were valid from `Camera::RenderScene`,
> per #108), and nothing has been drawn. It also sits after `Scene::DoFog` (slot 0x280), which runs
> between the clear and bucket 0 and writes state the Alloy pass reads; anchoring ahead of the two
> vcalls would buy a one-frame fog/fade lag for nothing. Steps 1, 2, 4 and 5 landed as written.
>
> Two consequences the flip carries, both deliberate: the Alloy target is now **linear**, which
> moves scene fog across the encode and changes how a foggy frame looks (this ADR's own §"Colour
> space" ordering, un-welded); and a `PostChain` refusal — default framebuffer, multisampled depth,
> offset viewport — now stands the **whole Alloy pass** down to vanilla for that frame, because a
> linear Alloy frame with no resolve is wrong by a gamma and no display-space variant of the owned
> stages survives.

Two facts that shape it, both verified rather than assumed:

- The matrices **are** valid at `RenderSinglePass` entry (software stack, written by
  `Camera::RenderScene` before the vcall). The comment at `OwnedDrawService.cs:28-34` claiming
  otherwise is half wrong and gets corrected — the FBO half is right.
- The world **FBO is not** bound at entry; `Camera::InitializeFrameBufferResources` binds it inside
  `RenderSinglePass`, gated on the scene being `"mainscene"`. This stops mattering at step 1.
- The view matrix has viewport scale baked in (`GLRender::SetViewTransform` applies
  `m_fViewportXScale/YScale` before rotation and translation), so it is not a pure look-at.

"`RenderSinglePass` suppressed" (#107 §1) therefore means **submits nothing**, not never called.
ADR 0010 already requires the engine counterpart to stay callable.

### Suppression stays at the iterator

Engine draw suppression happens at the bucket **iterator** (`BucketGetNextItem` →
`OwnedDrawService.IterSight`), never at bucket population (`Scene::AddPartsToDrawBuckets`).

Membership is decided at population time: `HasItemsOfType` reads three int counts written only by
`DrawBucket::Add` and zeroed only by `Reset`; the iterator moves a cursor and never touches a count.
So per-item skipping leaves every bucket populated and every content gate passing.

This is *not* what keeps the anchor alive — bucket 0's walk is unconditional either way. It is what
keeps `AuditPass`'s one-owner check meaningful (a part must be sighted and skipped, not absent) and
what leaves buckets 5/6's gates intact for anything still staged to the engine.

### Two GL brackets per frame, as a budget

> **Amended by ADR 0032 (2026-10-24):** a world view now opens ONE bracket covering both. The two
> reasons below lapsed with ADR 0028 — `RenderSinglePass` does not run, so there is no engine-chosen
> re-entry to couple to, and a GUI 3D panel never reaches the owned pass chain. The budget stands as
> a ceiling: a pass that needs a bracket of its own is still a pass in the wrong slot.


One shadow bracket at the pass slot, one colour bracket at the anchor. That is 4 `glXMakeCurrent`
calls per frame, which is what the frame costs today (`DrawOwned`, `CasterPass.EndFrame`).

**M3 may not exceed it.** Each bracket is a fence plus `glFlush` plus two context switches plus an
error drain — a GPU pipeline bubble. The Alloy pass is fast because it hands the GPU big
uninterrupted blocks, and every pass M3 adds goes *inside* an existing bracket. A pass that "needs"
its own bracket is a pass in the wrong slot.

Per-pass bracketing was rejected (6–8 brackets), and so was collapsing to a single bracket: that
couples the shadow bake to the colour pass's re-entry, and GUI 3D panels run `RenderSinglePass` too.
Coupling extender passes to engine-chosen cadence is the ADR 0007 bug.

## Consequences

- The one-frame shadow lag stops being an accident and becomes one named, bounded approximation
  scoped to creatures, with a written upgrade path.
- The M3 exit step is a checklist item with five entries, so "the frame is linear now" cannot half
  happen.
- Blended transparents run in gamma space until that step. No worse than vanilla, correct the moment
  the flip lands.
- The bracket budget is measurable and already logged (`[gl46] N brackets`), so it is a gate
  criterion rather than a hope.
- M4 inherits four items from this session, filed rather than remembered: OIT, planar water
  reflection, the post stack the resolve stage plugs into, and same-frame creature pose capture.
- ADR 0009's M3 text ("remaining buckets (water, particles, alpha)") is narrower than what M3 became
  across #107 and this ticket. This ADR and #107's decision are the current scope; 0009's milestone
  list is the older sketch.
