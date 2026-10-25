# The Alloy refusal set

#263 (ADR 0018 strand 1 step 8). **The renderer is chosen once per session, not once per frame.** The config
half is `alloy.enabled=0` in `cx.cfg`, read at `AlloyGate.Arm` (`managed/Alloy/Core/AlloyGate.cs`) before anything
installs; the capability half is asked from the present's `Start` phase and frozen the moment it answers.
After that the only term that still moves is whether there is a world frame at all. Every render site reads
`AlloyGate.Alloy`; nothing decides for itself.

`RenderMode` is gone — it carried a live toggle, and there is no longer such a thing.

## Why session-atomic and not frame-atomic

#216 latched the verdict once per frame, on the argument that a frame must be wholly one renderer's. That was
right and is not what changed. What changed is what a *mode* costs.

Through M0–M3 Alloy drew inside the engine's frame from the engine's resources, so both renderers were warm at
all times and a flip was free. Strand 1 of M5 (#259–#262) made Alloy's residency **exclusive**: Alloy draws its
geometry from its own mesh arena, never the engine's pools, textures are uploaded into Alloy-minted handles at the
decode window (#259), PLT compositing is forced to the CPU so those textures reach the funnel at all (#277),
and skinned parts are posed by Alloy rather than by re-entering the engine's draw (#262). None of that can be
un-done at a frame edge — the content was loaded once, in one world's shape.

So the two resource worlds are never warm at once, and the mode is a **boot decision**. `AlloyRenderer` and
`DrawFunnelService` both return early on `alloy.enabled=0`, before any Attach and any Install, which is what makes
vanilla mode actually vanilla rather than "the engine drawing from resources Alloy took away".

## Four verdicts, not a bool

ADR 0010's fourth bullet is still paid: **the detected capability tier and the requested mode are separate
pieces of state.** `AlloyGate.OptedOut` carries the request, `AlloyGate.Capable` / `CapabilityReason` the
device side. Bare `/alloy` reports both — so "I set it and nothing changed" is distinguishable from "this machine
cannot run it".

| Verdict | Means | Player sees |
| --- | --- | --- |
| `Alloy` | Alloy draws | — |
| `Choice` | the config asked for the engine | the `/alloy` report |
| `Incapable` | device, driver or engine setting rules it out | an on-screen notice, once |
| `Idle` | no world frame to draw (main menu) | — |

## The set

Asked in this order; the first that answers is the verdict's reason. `Choice` and `Alloy` **freeze** the
decision. `Incapable` does not — before the first Alloy frame it means "not yet" as often as "never" (the 4.6
context is created from a present, programs build on first use), and the cost of asking again is one branch on
a frame that is rendering vanilla anyway. It freezes the moment it stops being true.

| # | Refusal | Verdict |
| --- | --- | --- |
| 1 | `alloy.enabled=0` in `cx.cfg` | Choice |
| 2 | `alloy.drawhooks` not `batch` in `cx.cfg` | Choice |
| 3 | `alloy.hdr=0` in `cx.cfg` — the Alloy stages have no display-space variant | Choice |
| 4 | `alloy.gl46=0` in `cx.cfg` — an opt-out, kept ahead of the capability block so it is not reported as a device verdict | Choice |
| 4a | an `EngineLayout` offset row Alloy reads has no resolution for this platform (#456, `docs/engine-layout.md`) — the only capability term answerable cold, because it is a fact about the binary; asked ahead of everything that needs a warm client | Incapable |
| 5 | `AlloyRenderer` never armed (hooks did not install) — **after** every opt-out above, because each of them returns from `Initialize` before `_armed` is set, so asking this first reports the mechanism instead of the request | Idle |
| 5a | the `RenderSinglePass` shim never armed (an engine symbol or draw funnel did not resolve) — same placement, one layer down; since #458 there is no Alloy-inside-the-engine's-frame mode to fall back to | Idle |
| 5b | a self-check that gates arming failed — since CX-89 that includes `DoorFade.SelfCheck`: the shim stops calling `Scene::FadeDoors`, so a wrong fade would freeze `Gob+0x10c` and `Gob::HitCheck` would refuse the click. Standing the path down hands the fade back to the engine | Idle |
| 5c | the scene seam did not install (`SeamService.Db` false: the facade self-check or an object/scene factory failed). Alloy draws only the CX scene database, so there is nothing to draw without it | Incapable |
| 6 | no module loaded — **narrowed by CX-95**: it now means only "there is no world frame". The GUI scenes, the menus included, are Alloy's on their own verdict (`GuiPass.Capturing`: Alloy requested at boot, the 4.6 context up, the GUI program built, `alloy.gui` on), and so are the screen clears and the present (`ScreenOwner.Owned`) | Idle |
| 7 | no GL 4.6 share-listed context — ADR 0010's macOS/old-driver case; the context creation *is* the check, and it also refuses a context missing the GL 4.5 DSA entry points Alloy creates and edits through (CX-44) | Incapable |
| 7a | `alloy.backend vk` in `cx.cfg` and the Vulkan backend cannot run here: no Vulkan loader, a loader or device below Vulkan 1.3, no device whose UUID is the GL context's, no dynamic rendering or synchronization2, no graphics+compute queue, or a missing interop extension on either side (`GL_EXT_memory_object_fd`/`GL_EXT_semaphore_fd` and `VK_KHR_external_memory_fd`/`VK_KHR_external_semaphore_fd`, the `_win32` ones on Windows). Probed once, with the engine's context current (`VkBackend.Refusal`); the reason names the missing piece. Not a fallback to the GL seam: `alloy.backend gl` is the player's choice to make | Incapable |
| 8 | a program did not build, `glMultiDrawElementsBaseVertex`/`glBindBufferBase` missing, or the stream ring's entry points missing or its persistent map refused (CX-45) | Incapable |
| 8a | GL compute or `glMultiDrawElementsIndirectCount` missing (`CullPass.Capable`) — since #264 the compute cull is the frame's only visibility test, so without it every enumerated part of the area would draw every frame | Incapable |
| 9 | the HDR target latched a stand-down (`PostChain.Failed`) | Incapable |

**Entries 8 and 9 answer here only in principle.** Programs build on first use, which is inside the first
frame the decision already froze on, so in practice those two flags latch *after* the freeze and reach the
player as a `Fail` (below) rather than as a verdict.

**Stock Enhanced Lighting is not a refusal.** Its setting writes both `g_bFullDynamicLighting` and the engine's
gamma parameter (`GetGlobalShaderParami(0)`), and both only steer engine shaders and `Scene` code the scene
database replaces: the Alloy world stages compile with `GAMMA_CORRECTION 1` and `FRAGMENT_LIGHTING 1` whatever the
engine's values are, and only the GUI pass follows the engine's gamma. Refusing on it would leave no world at all.

**Entry 6 sits where it does for two reasons, and the second is the load-bearing one.** GUI/menu scenes do not
need the world verdict (CX-95 gives them their own, above) — but more importantly, every capability term below it is only answerable on a
*warm* client. The 4.6 context is created from a present inside a world frame, so asking entry 7 at the main
menu answers "no 4.6 context on this device" on every single boot. The first cut of #263 moved this term to
the end and put a spurious dropout notice on screen at startup; that is what the "an Idle or Incapable verdict
does not freeze" self-check now pins.

## After the decision: hard failure, not fallback

Two checks cannot be asked before the frame is taken — an offset viewport and an incomplete HDR framebuffer
(both `PostChain`; the depth attachment is Alloy's own format since CX-326 and matches nothing) — plus a
`MakeCurrent` that refuses mid-frame (`glXMakeCurrent`, or `wglMakeCurrent` on Windows), and since
CX-109 **no measured world camera**: `AlloyCamera` derives every view's projection and view matrix itself, so a
frame that reaches the anchor with no measured world view (`Camera::RenderScene` never hooked, or
`g_nScreenWidth`/`g_nScreenHeight` unresolved) has no camera to draw from and sits out rather than drawing the
world through an identity view; with `view.cameraloop 0` the same slot refuses on an unresolved
`m_aurMtxStack` instead (all `AlloyRenderer.NoFrameBehindUs`).

All of them call `AlloyGate.Fail` — and so does every capability latch that can only be taken *after* the freeze:
`AlloyRenderer` when the multidraw or stream-ring entry points are missing, `StreamRing` when the driver refuses
its persistent map, its program builder when a program does not link, `PostChain.StandDown`, and
`PresentTarget` where framebuffer 0 is not the buffer the platform presents (Windows).

A ring that exists but cannot take a push (a region fence past `GpuRing.TimeoutNs`, a frame past the bounds the
region is sized from, a slice rebound after the ring left it) is not a `Fail`: that pass sits the frame out, the
first failure of each kind is logged in full (`[stream] UPLOAD FAILED`), the `[alloy]` line counts the rest, and
a red band marks it on screen -- top edge the draw slots (the colour frame did not draw), left the cull source
(drawn unculled), right the previous bones (no mover motion vectors). There is no fallback upload. Those flags are read by entries 8 and 9 above, but programs build on first use,
inside the very frame the decision froze on, so nothing re-reads them — without the `Fail` call a broken
program is a permanently bare world with no line in the log.

**A `Fail` does not move the verdict.** There is nothing behind an Alloy frame
(#236: under the shim an unowned part is *nothing*) and no way to warm the engine's resource world
mid-session, so the honest report is a failure and not a fallback: one log line, one on-screen notice naming
the reason and the restart, and the mode stays where it booted. First reason wins.

## Who reads the verdict

`AlloyGate.Alloy`, and nothing else, gates: the shim's take-the-frame branch (`AlloyRenderer.Armed4Shim`),
enrollment, the #132 frame anchor, `CasterPass` and `SceneInputs`. (`ShadowService`, which used to put the
engine's stencil shadows back, went in CX-188: the engine renderer module whose draws it suppressed does not
run at all when CX owns the scene graph.)

## Exercising the forced-failure paths

Neither needs fault-injection machinery, and none is shipped:

- **No 4.6 context.** `alloy.gl46=0` in `cx.cfg` takes the same branch the device check takes (verdict `Choice`,
  reason named) and proves the whole fallback lands in vanilla. The `Incapable` wording differs; the path does
  not.
- **Program link failure.** `/alloy shaderdump`, then break one stage in the dump directory (an undeclared
  identifier is enough) and relaunch.

Off-device, `AlloyGate.SelfCheck` covers the decision semantics: the config opt-out is a `Choice` and says
nothing about the device, the decision does not un-freeze, and a `Fail` taken afterwards moves neither this
verdict nor the next.

## Per-class stand-downs are not in this set

`GrassField`, `CasterPass`, `EnvProbe`, `SceneDepth`, `FrameSnapshot` and `PostChain` each own a `StandDown`;
`PostStack` (#375) stands down per STAGE, dropping the one that did not build and running the rest.
Those degrade to "that class does not render", not to "the frame is not drawn" — punch-list territory (#217),
not a frame verdict.

`LightmapBake` (#674) is the same shape one level finer, and every refusal lands on the real-time loop, which
is what an unbaked surface takes anyway: a missing entry point, too few texture units or a failed link latch
the pass off for the context (`StandDown.Fail`, every record stays unbaked); a page that will not allocate
refuses the pages past it (`Refuse`, the rest of the area stays unbaked and is counted as `past the cap`);
a rect whose chart range moved before its bake is dropped and its readers re-enrol; a light whose scratch
cube cannot be rendered this frame (no caster gather) leaves the step for the next frame. The period line
names each: `lightmap pages N/cap, bakes landed/pending/refused (… past the cap)`.

## The per-part census gates are not in this set either, and one is left standing

`AlloyCensus`'s gate chain names why a recorded part is not an Alloy candidate. #263 deleted the one that had
become a boot decision (`inactive` — `alloy.drawhooks=off` now stands the whole service down before the census can
run). The rest are **classification, not hand-back**: `OWNED`, `state`, `bucket` and `scene` say a part is
already taken, dead, in a class Alloy does not draw, or in a different `Scene`, and none of them is expressible
as a device capability.

What is left that genuinely *is* a per-part capability refusal — a part Alloy could draw and does not, which
under #236 is a hole rather than a vanilla draw — was: `stale-identity`, `volumetric`, `vdo`,
`scene-tint`, `item` and `item-range`. CX-91 closed or accounted for all of them except `volumetric`
(the other half of bucket 5, accepted in writing below); see **The census holes, closed (CX-91)**.


## Every decline is a hole now, and each one's disposition (#285)

Before #264 an "Alloy declines this part" branch fell through to the engine's bucket walk and the part drew
vanilla. There is no walk. A decline is geometry that nothing renders, so each branch is either **owned** or
**counted under a name on a period line**. Nothing is left implicit.

### Owned since #285

- **The placeable highlight** (`gob+0x18`). `AlloyCensus.GobUnownable` refused it, and #128's reasoning —
  "SKIPPED, not revoked; the engine's own walk draws it highlighted, which is vanilla" — stopped being true
  the moment the walk went. Hovering a placeable made it vanish for the length of the hover while
  `Scene::DoHighlightGob` painted a glow over nothing. Alloy draws the part normally now and the engine's
  overlay composited the glow on top. CX-82 took that over too: the glow is Alloy's own pass now
  (`Highlight`'s outline, or `HighlightGlow`'s glow quads for a root it cannot outline), and no engine draw
  follows the Alloy bracket. What is given up is only
  `PartTriMesh::Mat` @0x450960's highlight branch — `Disable(5)` plus `SetColor4f(1,1,1,alpha)`, the part
  going flat-white-unlit under the cursor.
- **The per-gob env override** (`gob+0x480`) was already owned by #121 and is *not* a flag:
  `RenderEnvironmentMapped` @0x451040 reads it as a `CAurTexture*` that REPLACES the material's env map
  (falling back to `mat+0x40`, then to `currentscene+0x1e8`), then calls `RenderFlat` anyway. Alloy's IBL
  probe supplies the texture. A gob's own override sets the record's env bit, exactly as a material env map
  does (`AlloyRenderer.DbEnvMapped`).
  *Known approximation, unowned*: that function also passes `part+0xa0 * gob+0x108` to
  `SetPixelShaderAmbientColor` — the reflection's strength. Alloy stages ambient.w as 1.

### Counted, not owned

- **The scene tint** (`gob+0x19`). `Mat`'s tint branch is `SetColor4f(currentscene+0x14c..0x154)` under
  `Disable(5)`: the surface replaced by one unlit colour, which is not arithmetic on any term the CxDraw
  record carries. Rare, and expressible only by a program variant nothing else would use.
  **Counted** as `scene-tint=` on the census line (enrollment) and `instance-state` on the `[alloy]` line
  (draw loop).
- **The enrollment reject tail.** Every gate already had a named total and every post-classification refusal
  a per-class total, and neither said *which gate ate which class* — "87 cutout / 87 water refused after
  classifying" could not be attributed without a Ghidra session. The census line now carries the cross:
  `[by gate: vdo/Water=87 empty-item/Cutout=87 …]`, printed only where a cell is nonzero, over the five gates
  that refuse a classified part (`vdo`, `scene-tint`, `empty-item`, `table-full`, `no-vao-slot`).
- **Unbuilt diffuse textures** (`null-tex … drawn white/frame` on the `[alloy]` line). Not a refusal at all
  any more: `AurTextureGetTID` @0x48e080 returns 0 for the engine's shared `m_pNullTexture`, `CAurTexture::Bind`'s
  IsNull branch binds 0 and the draw is still issued, so vanilla renders such a part blank white. Alloy now
  draws it on a 1x1 white owned texture (`OwnedTextures.WhiteTid`) instead of deleting it from the frame, and
  the record stays stale so a late GL id is picked up the frame it arrives. `no-gl-id` survives for the one
  case that is still a hole -- the stand-in itself could not be minted.

## The census holes, closed (CX-91)

Since #264 every census refusal is geometry nothing renders, so each reason is now either an **owned path** or
a **skip with an engine fact behind it**. Of the seven names the ticket listed, two hid a real hole and got an
owned path; the other four are correct skips (`item-range` is folded into `empty-item`). Addresses below are
nm addresses (the read-only dump in `$NWN_DECOMP/` prints them + 0x100000).

### Owned paths built

- **`stale-identity` — re-enrolled, not refused.** The record's `MdlNode`/`Material` moved under it, or the
  cached VDO no longer holds the item (`VertexPrimitiveFlat::SetupArrays` @0x458920 calls
  `VertexDataObject::RemoveItem` before it re-adds, and a freed VDO block re-minted at the same address carries
  somebody else's item table — see the `vdo-address-recycling` note). #256 refused and queued the re-read,
  which was right while an unowned part still drew vanilla; since #264 the frame between the notice and the
  next `Rebind()` tick is the part *missing*. The census now calls `SceneWalk.RebindNow` in the same call,
  re-reads the draw arguments and carries on with the refreshed columns. `stale-identity=` counts only records
  the rebind could not make usable — no geometry yet, no node, no material — which are the `unbound` /
  `rec-dead` populations under another name, not a refusal of a drawable part. The residency rule is
  `PartTable.ItemResident`, asserted off-device by `PartTable.ItemResidentSelfCheck`.
- **`vdo`'s "no diffuse" third — owned, drawn untextured.** The gate rolled three conditions into one name and
  the live one was "the material has no texture slot 0". **The engine draws such a part.**
  `Material::BindAllStandardTextures` @0x4c6e30 answers a null slot with `SetActiveTextureAndBind(unit, 4, 0)`,
  which is `GLRender::SetTextureStageEnabled(unit, 0)` @0x4c93d0 — the stage leaves
  `m_bEnabledTextureStages`, so it contributes no multiply — and `PartTriMesh::Draw` @0x4513d0 calls
  `RenderFlat` regardless. That is a white surface, which is exactly the stand-in `OwnedTextures.WhiteTid`
  already mints for a live diffuse with no GL id. Both ends take it now: `no-diffuse=N owned on the white
  stand-in` on the census line, `no-diffuse N drawn untextured/frame` on the `[alloy]` line (it used to be
  `tex (no-diffuse …)` under **NOT DRAWN**). Unlike the tid-0 case the record is *not* left stale: an absent
  slot is not a pending build, so nothing will ever arrive to re-resolve.
  The other two conditions (`v == 0`, `Mode != 1`) stay as a guard and are unreachable for a bound record —
  `SceneWalk.TryBind` releases a non-mode-1 VDO outright, and the drift gate above re-reads a vacated one — so
  a nonzero `vdo=` is a table bug, not content.

### Correct skips, with the engine fact

- **`scene-tint` (`gob+0x19`).** `PartTriMesh::Mat` @0x450960's branch paints the part in
  `SetColor4f(scene+0x14c..0x154, 1.0)` under `Disable(5)` — the scene's **fog colour**
  (`Scene::SetFogColor` @0x45e220 is that field's setter), unlit, the gob alpha discarded. The flag has
  exactly two writers: `Gob::Gob` (0) and `Scene::SetSkyFade` @0x45e2b0 (1). The only object ever passed to
  the latter is the `skyfade1` model `CNWCArea::LoadSkyBox` creates, and that gob is never `Gob::SetScene`'d —
  `Scene::SetSkyFade` just stores it at `Scene+0x38`, so it never reaches `Scene::ChildAdd` @0x461d50 and is
  not in `Scene+0x130`, the list `SceneWalk` enumerates. The engine draws it from `Scene::RenderSkyBoxes`
  @0x466910 alone, outside every draw bucket, which is the sky's own path (#125/#136). The world census
  cannot reach it, and owning it there would be a second sky at world depth. Expect `scene-tint=0`.
  *Correction on the way past:* `gob+0x18`, which the #285 note above calls "the placeable highlight", is
  the **skybox** flag — its only writer is `Scene::SetSkyBox` @0x45e260, setting it on all four skybox gobs,
  and `Mat` reads it to keep a skybox part out of the lit branch. The placeable highlight is `Scene`'s own
  highlight list (`Scene::AddHighlightGob` / `DoHighlightGob`), not a gob byte.
- **`empty-item`** (was `item` + `item-range`; the out-of-range half moved to the drift gate above). What is
  left is an item the VDO holds with **zero indices**. `RenderFlat` @0x450eb0 passes
  `VertexDataObject::GetItemElementSize` straight to `RenderVertexData` as the index count, so vanilla issues
  a draw of nothing for it too — and the `item < 0` case is `SetupArrays` @0x458920's own zero-totals exit
  (a part with no vertices or no faces gets no item), the same verdict `SceneWalk.RecordHasNoGeometry` reads.
- **`unrenderable`.** `PartTriMesh::IsPartRenderable` @0x450e60 is the gate every `Part*::Draw` override runs
  first, and that both bucket-add functions run before every colour-bucket add —
  `Scene::AddPartsToDrawBuckets` @0x4751d0 over the texture-ordered mesh lists
  (`if (part && (IsPartRenderable(part) || g_nRenderPartBBox | g_nRenderPartPivots))`) and
  `Scene::AddPartsTreeToDrawBuckets` @0x460880 over a gob tree. A part failing it is drawn from **no**
  engine path. `part+0x96` has two writers — the `PartTriMesh` ctor sets it, `CombineParts`
  @0x473d30 clears it on every part it merges into a combined mesh — so these are absorbed originals still
  hanging in the scene tree whose geometry Alloy already draws, as the combined mesh that replaced it (#338).
- **`dup-sibling`.** Two coincident tri-mesh siblings under one parent with byte-equal transform, equal
  element/vertex counts and byte-equal vertex positions (`AlloyRenderer.SamePositions`). Vanilla draws both
  and the depth tie decides which is seen; Alloy's submission order is not the engine's, so drawing both
  flickered (#397, the stock `ashlw_072.mdl` shield). Refusing the earlier reproduces the tie's outcome from
  one draw: it is a skip of a **duplicate**, not of a surface — whichever twin vanilla shows, the pixels are
  the same geometry. The ceiling stands (`tools/mdlcensus/dupsib.py` is the offline twin): a deliberate
  coincident overlay with equal counts *and* equal positions loses its base. #317 / PR #648 narrowed it from
  "equal counts" to "equal positions" after it ate a door leaf's near face.

- **`hidden` (CX-92).** A part under a subtree whose `part+0x34` draw gate is clear. `Scene::AddPartsTreeToDrawBuckets`
  @0x460880 and `Gob::PartDraw` @0x451d50 both return on that byte before recursing, so vanilla draws nothing
  there either. The entry is HELD (`PartTable.StHidden`) rather than released, so a re-show is noticed by
  re-reading the byte (`docs/missing-parts-triage.md`). It is exempt from #128's all-owned-or-none, and it is
  counted as `H held hidden on part+0x34` in the census `instances:` clause rather than under `rejects:`,
  because it is a hold, not a refusal.

### What to read live

`rejects: stale-identity=… vdo=… scene-tint=… empty-item=… unrenderable=… dup-sibling=… | no-diffuse=…` on the
census line is the whole ledger, and `[by gate: …]` attributes the post-classification half by class.
`scene-tint` and `vdo` should read **0**; `stale-identity` should read 0 in a settled area and a small rate
under churn; `empty-item`, `unrenderable` and `dup-sibling` are the correct skips above and may read nonzero,
as may the `held hidden` count on the `instances:` clause.

### Accepted in writing: `volumetric` materials (the other half of draw bucket 5)

`sample_framebuffer` has two values and they are different classes, not degrees of one:
`Scene::AddPartsTreeToDrawBuckets` @0x460880 routes **2** (water) to bucket 6 and **1** to bucket 5. #129 gave
water an Alloy program that samples the one framebuffer copy Alloy already takes. **CX-90 owns bucket 5**, and
what is left in it is the *other* keyword the same function files there: `volumetric`.

**What `sample_framebuffer 1` actually buys, measured.** The engine's own changelog
(`lang/en/docs/patchnotes/87.8193.35.md:263`) states the whole of it — "renders near the end of the frame.
Allows rendering refractions and similar" — and the code agrees: bucket 5 is drawn right after
`Camera::CopyRenderSubframe` @0x4a6b90, which binds the copy's colour and depth on texture units 11 and 12
(`Camera::BindRenderSubframeTextures` @0x4a6ab0). The *refraction* is then the material's own fragment
shader's business. Three facts settle that there is nothing else to port:

1. **No stock `.mtr` declares the keyword.** The base game ships seven `.mtr` files in total and none of them
   use it; a binary scan of every `data/*.bif` finds the string only in the engine binaries and the changelog.
2. **No stock world shader samples the copy.** `texFBColor`/`texFBDepth` are declared unconditionally in
   `inc_common.shd`, and the only geometry shaders that read them are `fswater.shd` (refraction + SSR) and
   `fsparticle.shd` (a depth tap for soft particles). Everything else that reads them is post-process.
3. **The content that does use it names a stock shader.** The three `sample_framebuffer 1` materials on this
   machine (`assets/content/item/quarterstaff/081-085_fatherofstaves/stff_mtl_{red,slvr,gold}.mtr`) are
   `transparency 1` + `customshaderfs fslit_nm` — stock, and `fslit_nm` never touches `texFBColor`. They are
   using the keyword as a **late-draw hint**, which is all it is.

So Alloy draws bucket 5 by classifying each part off its material exactly as it classifies every other part
(`AlloyClasses.Of`). For the content above that is the blended class — sorted back to front, which is strictly
better than vanilla's unsorted late slot. Nothing takes a second framebuffer copy, because nothing would read
it. *If* a content `.mtr` ever pairs `sample_framebuffer 1` with a custom FS that samples `texFBColor`, the
place that lands is the content pair's own program (`ContentPrograms`, CX-303) plus two texture units the
lit bracket does not already spend — `TextureSlots.SnapshotColor`/`SnapshotDepth` are shared with the SSAO
and OIT pairs, so they are not free inside the lit pass. That is a ticket, and it has no content yet.

**`volumetric` is refused, and this is the writing.** The changelog describes it as two draws: "renders
back-facing vertices to depth and renders with updated depth buffer sample texture before rendering
front-facing". `AddPartsTreeToDrawBuckets` matches — a volumetric part is added to bucket 3 **and** to bucket 5
— so one part is drawn twice per pass, in two states, and Alloy's one-owner invariant cannot express that
without a second pass built for it. `AlloyCensus.RefusedByLateKeywords` is the whole rule, asserted in both
directions by `AlloyCensus.SelfCheck`, and `volumetric=` on the census line counts what it eats.

**The instrument.** `sample-class=<hole>/<class>` on the `[scenewalk]` line: the right number is how many parts
the engine's own classification put in bucket 5 this period, the left one is how many of those Alloy does not
own — i.e. the size of the hole, read off the engine's answer rather than ours. `SceneWalk.CrossCheck` computes
both against `Scene+0x208`, which holds the finished classification. The left number must be 0 wherever the
only late keyword in play is `sample_framebuffer 1`.

**Open box (needs a live run, cannot be ticked here):** stand in front content that uses the keyword — a
Father of Staves is the known case — and read the pair. `0/n` with the staff visible is the fix working;
anything else names what is left.

## The GUI and the screen (CX-95, CX-101)

A second, smaller verdict sits beside the world's, because the GUI has no world frame to wait for. Only the
first row of it still has an engine GUI behind it: once Alloy is rendering, the scene database IS the GUI and
there is no engine traversal underneath, so every later row costs the panels rather than handing them back.

| Term | Where | If it fails |
| --- | --- | --- |
| Alloy requested at boot (`alloy.enabled`, `alloy.drawhooks=batch`) and the `RenderSinglePass` shim armed | `AlloyRenderer.Initialize` → `GuiPass.Install` | nothing installs; the engine draws its GUI |
| the screen size resolved | `GuiPass.Install` | `_log.Always`; the pass never arms, so `DbGui` refuses every scene loudly and the panels are NOT DRAWN |
| the 4.6 context exists and the GUI program built | `GuiPass.Capturing` | the panels are NOT DRAWN while it is false (one or two frames at boot); a build failure is a loud `Fail` and they stay undrawn for the session |
| `alloy.gui` on | `GuiPass.Capturing` | the player's choice, live -- and it costs the game's panels, which nothing else draws |
| the world is Alloy's or absent | `ScreenOwner.Owned` | an engine-drawn world (an `Incapable` verdict) keeps the engine's clears and present; the GUI stays Alloy's over it |
| the present composite image built | `PresentTarget.Ensure` | the framebuffer object is dropped with the image, so `PresentTarget.Fbo46` stops claiming one and every binder writes framebuffer 0. Under GLX that IS the buffer the engine presents, so the frame still reaches the screen and a loud `Always` says so. Under WGL framebuffer 0 in the 4.6 context is a buffer nobody presents, so there is no fallback to take: the same line says the world renders vanilla and Alloy's GUI draws reach nothing, and `AlloyGate.Fail` makes it the session's stated refusal. `/alloy` carries it either way (`present: framebuffer 0 -- ... REFUSED`) |

## The movie quad (CX-335)

A movie is its own small verdict, asked per frame and answered either way without the player losing the
movie: the engine keeps the decode, the pacing and the present, so what Alloy can refuse is only who draws
the quad. `MovieService` runs last of the `PrePresent` participants (priority 90, behind `SeamDraw`'s frame
boundary), opens a 4.6 bracket of its own, uploads the decoded rows into its texture, draws the letterboxed
quad into `PresentTarget`'s image over whatever the world composited there, and lets the engine context's
hand-back blit put it on the screen. Nothing of it reaches the engine's own GL.

| Term | Where | If it fails |
| --- | --- | --- |
| `alloy.enabled` on | `MovieService.Initialize` | nothing installs and the engine plays its own movies, as in a vanilla client |
| `CExoMoviePlayerInternal::ShowFrame` hooked | `MovieService.Initialize` | said once at boot; the engine keeps the whole movie draw |
| `alloy.movie` on | `MovieService.DrawPending` | the player's choice, live: the engine's quad draws the frame under `ScreenOwner.EngineFrame`, which is what lets its opening `Clear` through |
| the decoder's frame fits the texture it names (`frameW/frameH` above zero, neither past `texW/texH`) | `MovieService.Draw` | a loud `Always`, latched for the session: a shape we cannot trust would draw stretched or sample past the decoded rows, so the engine's quad takes every frame after it |
| the GL entry points the quad is made of resolved (`Gl.Init`, then `ClearBufferfv`, `ColorMask`, `BindFramebuffer`, `Viewport`) | `MovieService.Draw` | the same latched refusal |
| `g_nScreenWidth/Height` resolved, and reading above zero | `MovieService.Draw` | an unresolved symbol is latched. A zero READ is said once and **not** latched: the engine sizes its window after a movie frame can first arrive, and `GuiPass.Screen` treats the same pair the same way, so the engine's quad draws until it reads |
| the decoded rows reached an Alloy texture | `MovieFrame.Upload` | a loud `Always`, latched for the session, and the engine's quad draws every frame after it. The staging hook on `GLRender::DirectCreateSubTexture` leaves the engine's own texture filled, so that quad still has its picture |
| the 4.6 context is this thread's to enter | `MovieService.Draw` | said once, **not** latched: the context may not be up yet, and a render thread that holds it makes every movie frame the engine's for as long as it does |
| the program, the quad's VAO and a present image exist | `MovieService.InBracket` | a loud `Always`, latched for the session; the engine's quad draws from then on. On Windows a refused present image is the one case with nowhere to draw, and `PresentTarget` has already failed Alloy for it |
| the draw raised no GL error | `MovieService.InBracket` | the same latched refusal, read before the bracket closes: a draw that errored leaves the present image at the clear alone, which is the one way this can fail with nothing to show for it |
