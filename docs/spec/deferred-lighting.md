# Deferred point lights (CX-188, ADR 0029)

The contract between the colour pass that WRITES the G-buffer and the compute pass that LIGHTS from it. The
lane format is `managed/Alloy/Shaders/cx_gbuf.glsl` and nothing else; this page says what each lane means and
what each side owes the other.

## What moves and what stays

Only the **point lights** move: the work `CxClusteredLightLm` (cx_lightfrag.glsl) and `CxFilmStatics`
(cx_material.glsl) do for an opaque or cutout fragment of the main view. Everything else the lit stage does
stays in the lit stage and still lands in the HDR target during the colour pass: sun and cascades, ambient,
the lightmap term at full strength, the probe, SSR records, weather, emissive, keyhole, fog, overlays.

Forward, unchanged by this contract:

- the probe capture (`uCapture == 1`) and the planar mirror (`uCapture == 2`);
- the blended class and the OIT-lists twin, water, grass, blades, particles, weather (step 5 slims their loop);
- `/alloy viz` modes (any `uViz != 0` draws with the forward program, so every debug view stays truthful);
- `light.deferred 0`, the A/B.

### The light record: blended fragments lying on a deferred surface

The deferred walk also stores its own answer per pixel -- `point` and `pointSpec` before the transfer weights,
the record's view depth (negative on a baked receiver) and the share of its baked term the movers took back --
in `GBuffer.TexLight` (`CxLightRecPack`, cx_gbuf.glsl). `cx_deferred_cs` writes every pixel of the rect (zero
where no walk ran) and `cx_pairdebt` adds its debt. During the blended pass only, a forward lit fragment whose
own view depth matches the record's (`CX_GB_RECORD`) and whose baked state is the record's takes that answer
instead of walking the lights: a floor decal (`tts02_splotch01` cost 1.2 ms of a 1.6 ms blended pass walking
the same lights the floor beneath it had just taken). Anything off a surface -- glass, a veil, smoke -- fails
the depth test and walks as before.

The deviation: the decal's specular lobe is the surface's (rough, specularity and the specular colour scale
the record was walked with), and the decal takes no film highlights of its own -- the surface's show through
it at `1 - alpha`.

### Ground decals fold into the surface (`DecalLayer`, CX-273)

A ground decal is not lit at all. `AlloyRenderer.DbDecal` marks a lit, plainly alpha-blended (`SrcAlpha`,
`OneMinusSrcAlpha`), up-facing sheet on a static object with no self-illumination, env map or content vertex half (an
alpha-tested cover, even one drawing blended: a cover hides its ground's relief and material as well as its colour,
and a fold keeps both -- see washes below); the blended run puts those first, and before the opaque pass `DecalLayer` draws them with the lit stage built as
`CX_DECAL_WRITE`, which stops at the albedo. A fragment lying on the prepassed surface (`CxDecalOn`: at most
0.1 m above it, plus 0.1% of view depth, measured as height off the surface so a grazing view does not stretch it;
one-sided, so a surface standing over the decal -- a foot on it -- is never taken for its ground) over-blends `albedo * coverage` into an RGBA16F layer whose alpha is the share
of the surface left showing, and writes an RG32F pair under `MIN`: the prepass depth it was judged on, and the
nearest decal's view depth. Every other fragment is discarded there. In the opaque pass, the cutout pass and the
wash interiors, a fragment the nearest decal lies on (`CxDecalOn` again, this surface against that decal depth)
folds the layer into its albedo (`albedo * a + rgb`) before anything is lit, so the decal takes the surface's
normal, material, sun and local shadows, lightmap, SSR, wetness and point lights by being that surface. A cutout
or wash interior drawn over the prepassed ground (a tileset's grass rim along a road) therefore shows the decal
rather than covering it. The blended pass draws the decal batches again and drops exactly the fragments the layer
took (`uDecal == 2`, the same `CxDecalOn` on the same prepass depth), so a sheet that only partly lies on the
ground keeps the rest as before.

The deviations: the decal has no specular, roughness or normal of its own (the surface's stand); no 0.2 alpha
discard on a blended-class decal (the layer takes the whole graded edge); a surface lying under the nearest decal
at a pixel takes every decal folded there, even one that in fact lies below it; a decal over an unlit surface is
lost at those pixels.
`/alloy viz 53` shows what each surface folded. The layer is not drawn into the probe capture or the mirror.

### Sheets that are not decals draw as washes

A see-through sheet lying flat (every triangle within 45 degrees of horizontal, judged off the triangles) that
is not a ground decal -- alpha-tested, refused by `DbDecal`, or a cover meant to hide what it lies on -- keeps
its own surface and lighting, and is drawn as a **wash** (`AlloyRenderer.DbWash`): under a polygon offset
(-1, -2), so it does not z-fight the surface it lies on (content lays these at +1 cm, a quarter of the stock
ones at exactly 0). An alpha-tested wash whose diffuse measures graded alpha draws in the blended class, as
vanilla draws its alpha-tested bucket (blend and the 0.2 test together): drawn hard, its edge showed the colour
the texture keeps under alpha 0, white on many decal sheets. A blending wash that is not a decal is
**split** (`AlloyRenderer.WashSplit`): its fully covered texels (coverage times part alpha at least 0.99,
`uSolid`) draw solid with the cutouts through the deferred twin (`Issue.WashInteriors`, the `washes` GPU
term); the rest blends, drawn under `GL_LESS` so every interior fragment meets its own depth and is dropped
unshaded. A forward-lit blended fragment costs several opaque ones, and these sheets cover much of a town's ground. Only static objects' sheets are washes, and
never a coverage or env-mapped material or a posed mesh. `[decals] flat sheet ... draws as a wash` in `cx.log` names the first 64 a
session with the reason each is not a decal.

### The forward consumers' slim walk (`CX_LIGHT_LITE`)

A transparent or per-vertex receiver compiled the whole of `CxClusteredLightLm`, which exists for a baked
opaque surface. Step 5 brackets that function: a stage head that sets `CX_LIGHT_LITE` before including
`cx_lightfrag` gets a slim body with the same signature and the same per-light arithmetic, and every other
program -- the forward opaque lit stage, its deferred twin, the compute pass, the bake -- gets the full one,
unchanged. The nine heads that set it are the forward consumers of the walk, vertex and fragment both: grass
cards, grass blades, water, particles, weather drops and splashes. `ShaderContract` asserts all nine set it
and that the lit head does not.

What the slim body leaves out, and why it is not a behaviour change:

- **the baked-receiver half** -- the hot-light branch, its 16-sphere mover ray test, `CxMoverOwe` and the
  mirror walk of baked statics. Every call arrives through `CxClusteredLight` or `CxClusteredDiffuse`, whose
  `baked` is 0, so `inPages` is false and none of it was reachable; `lmSub` is 0 by construction.
- **the glossy mirror candidate** (`light.hidden`, the one extra tap after the loop). None of these programs
  uploads `uLightOcc`, so it reads `(0, 0)` and `CxGlossy` is already false at all of them -- water included,
  whose roughness would otherwise qualify.
- **the two-candidate shadow cap and its filter.** The slim body gives *every* shadowing light one hardware
  tap (`CxLocalShadowQ(..., soft: false)`, which folds the blocker search and both Vogel discs out of the
  inlined body) -- the full walk's own `shadow.cap 0` path, measured cheaper than two PCSS candidates.
  `/shadow cubedist`, `/shadow floor`, the per-slot fade, `light.relevance` and the program's own
  `uLocalCubes` (`/particles shadows 0`, the grass shadow knob) all still apply, term for term.

The look deviation is the shadow edge: a point-light shadow on grass, water, a particle or a raindrop is a
hardware tap rather than a contact-hardened disc, so it has no penumbra. Against that, the cap's evicted
lights were drawn *unshadowed* and are now shadowed, which is the larger difference and the correct one.

There is no `light.lite` knob. A `#define` cannot be toggled live, and the uniform branch that could would
keep the full walk's registers allocated in the very programs the step exists to free -- the same reason
`cx_ownlit_synth` does not branch into the walk for its viz modes.

## Frame order

`DepthPrepass -> AmbientOcclusion -> OpaqueOwned -> SkyOwned -> CutoutOwned -> DeferredLights -> CoverageOwned -> GrassOwned -> Reflections -> ...`

`DeferredLights` is one pass and two dispatches: the shadow record (`DeferredShadows`, `cx_defshadow_cs`) and then
the light walk (`DeferredLights`, `cx_deferred_cs`), each with its own GPU term.

- `GBuffer.BeginFrame()` clears G0 to zero before `OpaqueOwned`. G1..G3 are never cleared: G0's flag byte
  gates every read.
- `OpaqueOwned` and `CutoutOwned` attach G0..G3 at COLOR4..7 (draw buffers `{0, NONE, 2, 3, 4, 5, 6, 7}`
  while Ssr's records are attached, `{0, NONE, NONE, NONE, 4, 5, 6, 7}` otherwise) and detach after
  `CutoutOwned`. A prepassed batch draws at `GL_EQUAL`, a non-prepassed one at `LEQUAL` with depth writes, so
  the nearest writer wins either way. The sky draws between them and writes no G-buffer: its pixels keep G0 = 0.
- A content pair (CX-303) draws through its own deferred twin: the pair's program text under the same two defines.
  Its fragment half's surface body only edits the surface inputs, so it writes the G-buffer like any other; a pair
  that edits the lit colour or lights itself has no twin and draws forward.
- A forward draw in those passes (a content pair with no twin, a viz batch) writes no G-buffer
  and can land on a pixel a twin already wrote. The record carries the twin's view depth; the reader drops it when the frame's depth no longer
  agrees within `CX_GB_RECORD` (Ssr's record test). The depth the deferred pass reads is the depth buffer AFTER
  the cutout pass -- the prepass plus any colour-pass writes -- and the tile depth bounds come from it too.
- `DeferredLights` runs after the last writer and before grass, whose root blend copies the lit frame under
  each blade. SSR traces after it, so reflections see lit surfaces.

## The twin

The lit fragment stage compiled with `#define CX_DEFERRED` (and `CX_GBUF_OUT` for the outputs) is the
**deferred twin**. Issue.cs draws an opaque or cutout batch with it when `DeferredLights.Active`. It:

1. skips `CxClusteredLightLm` and `CxFilmStatics` (`point`, `pointSpec`, `lmSub`, film all zero) -- the loop
   code is compiled out, not branched over: its registers are the reason this exists;
2. keeps the lightmap term `lm` at full strength (no hot-light debt: the compute pass takes that back);
3. writes the G-buffer through `CxGbufWrite`, setting `CX_GB_DEFERRED` on every pixel it covers.

A content override of `cx_ownlit_fs` that does not call `CxGbufWrite` cannot be a twin: the extender checks
the compiled text, and `light.deferred` stands down for the session with one loud log line naming the
override. It is never silently half-deferred.

## Lane semantics

The compute pass must reproduce, at every pixel the twin covered, exactly the change the forward loop's
results made to the finished pixel. The forward lit stage is LINEAR in those results up to the fog (see
Known deviations), so the twin writes the derivatives:

| lane | meaning |
|---|---|
| `N` | the shading normal handed to `CxClusteredLightLm` |
| `rough`, `spec`, `fresnel` | the loop's `rough`, `specularity`, and `cxFresnel` at the call |
| `coat`, `coatRough`, `sheen`, `sheenRough`, `f0`, `specScale` | `cxCoat`, `cxCoatRough`, `cxSheen`, `cxSheenRough`, `cxF0`, `cxSpecScale` -- the state `CxLoopSpec` reads |
| `flags` | `CX_GB_DEFERRED`; `CX_GB_BAKED` (the loop's `baked`); `CX_GB_ONMOVER` (`onMover`); `CX_GB_FILM` (the `CxFilmStatics` gate: `wxFilm > 0 && baked && cxCoat > 0`); `CX_GB_NOSHADOW` (`cxRecvShadow == 0`) |
| `diffT` (Wd) | d(final pixel) / d(`point`): albedo x matDiffuse x kd x coatT x wxTransmit x dirOcc, plus the couplings through `diffuse` (the legacy env pattern's `(ambient + diffuse) * pat`, the coat probe's `(ambient + diffuse) * cenv`), x keyhole x fog transmittance (`1 - CxFogRevealFactor`, the ramp the pixel took) |
| `specT` (P) | d(final pixel) / d(`pointSpec`) = d/d(film): dirOcc x the ghost divide (`1/partA`, the part's own alpha `rot.w`, never the texture's coverage) x keyhole x fog transmittance (`1 - CxFogRevealFactor`, the ramp the pixel took) |
| `lmLum` | luminance of the undimmed `lm` (the `lmSub.x` denominator); 0 on an unbaked pixel |
| `lmShare` (L) | the full contribution of `lm` to the final pixel (its diffuse through Wd and its lobe through P), so the debt is `final -= L * min(lmSub.x / lmLum + lmSub.y, 1)` |
| `lmTexel`, `lmMover`, `lmPick` | `CxLmTexelWorld(chart.rec)`, `CxLightmapMover(chart.rec, vChart)`, `chart.pick` (byte lanes: a set index past 254 reads back as no channel -- ponytail: widen the lane if an area ever closes that many sets) |
| `viewDepth` | the twin's linear view depth, `1 / gl_FragCoord.w` -- the record's ownership test |

The compute pass then adds, per pixel:

    hdr.rgb += Wd * acc + P * (spec + film) - L * min(lmSub.x / max(lmLum, 1e-5) + lmSub.y, 1)

where `acc`, `spec`, `lmSub` are what `CxClusteredLightLm` returns for (worldPos, N, V, viewDepth, onMover,
rough, spec, baked, lmTexel, lmMover, lmPick), and `film` is `CxFilmStatics(...)` when `CX_GB_FILM` is set.
The sum is taken in two dispatches of `cx_deferred_cs`: the walk adds every term but `P * film`, and the same
stage compiled with `CX_FILM_PASS` adds `P * film` on the `CX_GB_FILM` pixels alone, only while
`weather.filmlights` can lift a film. The split is exact (the term is linear) and is there for cost: with the
film's light loop in the walk's program the pass ran 2.1 ms slower on the test area (`/alloy bench`, 2026-10-08).
worldPos is reconstructed from the prepass depth with the inverse of the JITTERED view-projection the prepass
rasterised with; viewDepth is the linear view depth (the forward stage's `vDepth = gl_Position.w`). At the
cluster-list phase (step 2a) this is bit-for-bit the forward loop's arithmetic on the same inputs.

## Where `lmSub` comes from (step 4)

The deferred stage defines **`CX_NO_HOT`**, so `CxClusteredLightLm`'s HOT branch is not merely unbranched but
absent: no 16-sphere `uDynMovers` ray test, no dyn-page taps for a static light on a baked receiver, no hot tap
cap, and `lmSub` comes back `(0, 0, 0)`. A hot static light behaves there exactly as a cold one. The tile
cull's own `CxTileHot` goes with it, so a tile no longer runs a 16-sphere loop per static light either. The
registers that branch cost every pixel of the dispatch are the reason it is a macro and not a uniform -- the
same argument `CX_LIGHT_LITE` makes for the forward consumers.

The debt is taken instead by **`cx_pairdebt`**, a second 16x16 compute dispatch immediately after
`DeferredLights`, over the same G-buffer:

- `CasterPass` keeps a per-**creature** budget of the `lightmap.moverlights` (default 2) strongest static
  lights whose reach holds it, ranked by the engine's curve times luma -- `cx_lightbuild`'s own `Importance`.
  Only those (light, creature) **pairs** cast a creature shadow from a static light onto baked ground.
  A creature is ONE mover: its parts (a player character is ~19 rigid meshes) fold into the box round them, so
  a body keeps one set of lamps and one tile per lamp, which the bake fills with every dynamic part inside it.
  A kept pair casts at `PairShadows.BudgetWeight` strength, 0 where its score meets the first light turned
  away and 1 at half again over it, so a step that swaps the kept lights fades the shadows instead of
  popping them. Movers past a light's `MaxPerLight`, and a mover too close to its lamp to frame
  (`ShadowMath.ConeMaxSin`), go without that light's shadow, counted; the light's other pairs stand.
- Each pair gets one small perspective depth tile in a 2D-array atlas (`lightmap.pairsize`,
  `lightmap.paircount`), framed on the creature's sphere and fitted to the light's reach, baked through the
  existing layered caster path over the DYNAMIC set alone. A light whose pairs all land **publishes no dynamic
  cube page**: `uLocalDynPage[slot]` carries `-(first pair + 1)`, which every existing `< 0.5` / `> 0.5` reader
  already treats as "no page" -- and that is the truth.
- The debt dispatch culls those pairs per tile against the tile's own view-space box (the truncated shadow cone
  vs the box's bounding sphere, `CxPairReach`), sorts the survivors so one light's pairs are adjacent, and for
  each covering light takes `vis` as the **min** over its pairs' tile taps. A tile with no pair over it exits
  at the first barrier having read no G-buffer texel.
- The two quantities are `CxMoverOwe`'s, with the pair tile standing in for the dyn page:

      ch = CxPickChan(pair.set, lmPick)
      ch < 4:  lmSub.y += lmMover[ch] * dfade * uLocalFade[slot] * (1 - vis)
      else:    lmSub.x += lum * dfade * (1 - mix(1, 1 - CxLocalBakedVis(...) * (1 - vis), uLocalFade[slot]))
      lmSub.x *= 1 - uLocalFloor

  and the subtraction is this page's own line: `hdr.rgb -= lmShare * min(lmSub.x / max(lmLum, 1e-5) + lmSub.y, 1)`.
  `lightmap.moverlights 0` is the feature's A/B; `light.deferred 0` is the forward path, which still computes
  the debt per fragment per light.

## Known deviations

- **The hot-light debt is a per-pixel min, not the forward loop's per-fragment cap.** The forward walk ranks
  hot lights per fragment and gives the strongest `shadow.cap` of them the soft dyn filter and the rest one
  hard tap; the debt pass gives every covering pair the same four-tap tile disc. Two creatures of one light
  over a pixel fold into one `min()` rather than two `CxMoverOwe` calls.
- **`light.relevance` is not applied to the debt.** The loop drops a light whose share of the fragment's
  brightest falls under the cut, and reproducing that per pixel needs the cluster walk the debt pass exists to
  avoid. A relevance-culled hot light therefore still owes its creature's shadow: slightly too dark, never
  missing.
- **A baked receiver drawn FORWARD in a deferred frame owes nothing** -- a blended baked sheet, the probe
  capture, a hosted opaque batch. Its light is paired, so it has no page and the forward hot gate reads it as
  cold. `light.deferred 0` restores it; `/alloy viz` frames are never deferred, so no debug view is affected.
- **An unbaked receiver** (a creature shadowed by another creature under a lamp) gets its movers back from the
  pair tiles through `CxLocalShadowQ` and `CxLocalShadowFast`, but only in the stages that define
  **`CX_PAIR_RECV`** -- `cx_defshadow_cs` (step 3's record, which is where the deferred path's verdict for such
  a pixel actually comes from), `cx_deferred_cs` (its mirror tap) and `cx_pairdebt`. On the forward consumers
  (grass, water, particles, weather, the transparents) a paired light's creature shadow on an unbaked surface
  is lost for the frame. Keeping that tap out of the
  forward loop is the lesson of the reverted pair-shadow attempt (`504b6e0`), whose always-compiled branches
  grew the feature's own OFF baseline by 7.8 ms.
- **A light past the atlas, too crowded (more than `CasterPass.DynSpheres` creatures) or unframeable (a
  creature engulfing its own light) keeps its page** -- and the deferred frame then owes it nothing, because
  the walk's hot branch is gone. Counted on `CasterPass`'s period line, never silent.
- **Fog** mixes in display space (cx_tail's own ponytail on `CxReflOut`), so the transmittance in Wd and P is
  the linear approximation `1 - CxFogRevealFactor` (the fog-of-war reveal ramp where it applies). Same approximation Ssr already takes.
- **Display overlays** (noir, racial vision, targeting shape) apply after the fog in the forward stage. They
  are not linear in the lit colour; under an active overlay the deferred contribution misses it. Rare states;
  the A/B knob is the check.
- **worldPos** is reconstructed from the depth buffer, not interpolated: it carries the window-depth
  quantisation into V, the attenuation distance and the cube-shadow geometry. "Bit-for-bit on the same inputs"
  holds for every input but this one.
- **Precision**: N is snorm16 octahedral, the small alphas and the sheen colour sqrt-encoded unorm8, the
  weights half floats. A highlight off a near-mirror film is where it would show first. `rough` within 1/255 of the
  `light.hidden` ceiling can fall on the other side of `CxGlossy`/`CxGlossW` than it did forward.

### The shadow record (step 3)

Before the walk, `cx_defshadow_cs` ranks each deferred pixel's shadow-casting lights by their CONTRIBUTION there
-- `CxLightRank`, the luminance of the light's own unshadowed diffuse, the term the forward cap and the hot-light
cap rank by too (per PIXEL, never per tile: #336) -- and evaluates the strongest `shadow.perpixel` of them (default
4, the record's `CX_SH_MAX`) once into a record (`cx_shrec.glsl`: RGBA8 visibilities, RG32UI slots
plus a depth/normal key; key 0 = no record), at full or half resolution (`shadow.deferredres`, default half, with a
depth- and normal-aware upsample). The walk reads a light's visibility by slot match; **a shadowing light past the
budget lights the pixel unshadowed** -- that is the budget, and it is what the forward cap's evicted candidates
already did.

Membership is a step, so the record **dissolves its own edge**. The pre-budget path hid that step behind a
hardware tap for the (K+1)th light; nothing hides it now, and a full shadow ending at a hard contour would crawl
as the camera moves (no temporal trick is allowed to fix it -- see the TAA rule). So `cx_defshadow_cs` also tracks
`rDrop`, the best score that did **not** fit, and fades each lane's visibility toward 1 over
`[rDrop, CX_SH_FADE * rDrop]` (`cx_shrec.glsl`, an octave) before writing it. At the crossover the Kth and
(K+1)th scores are equal, the Kth lane's weight is exactly 0, and the light leaving the record has already
dissolved; a lane well clear of the edge -- every hero light -- is untouched. The weight is folded into the
visibility itself, so the reader needs no lane and the walk's own `dfade` still multiplies on top; a lane the
dissolve has taken to nothing is not filtered at all. `rDrop` is 0 when nothing was dropped and when the budget is
off, and then the record carries exactly the visibilities the filter returned.

Membership is therefore the **record's alone** -- the walk never re-ranks, which is what keeps the half-resolution
record's choice and the full-resolution walk's from disagreeing -- and the score itself (`CxLightRank`) is a
function of position only, with no surface term, so a half-resolution record's choice is right for the whole 2x2
block. `shadow.cap 0` stands the record and the budget down together (every light one hardware tap). A record
that fails to link stands `light.deferred` down for the session. Deviations from the forward look, all live-unverified:

- only the strongest light gets the blocker search; lights 2-4 sit at `shadow.soft`'s ceiling;
- the dyn page takes its centre tap alone, so a mover's shadow with no static cube under it has a one-texel edge;
- the static disc is 4 taps, rotated by a fixed 2x2 interleave (the half-res upsample mixes all four);
- half res shifts the shadow field by half a pixel;
- a light past `shadow.perpixel` is unshadowed, as the forward cap's evicted candidates are -- and the lane at
  the budget's edge is faded toward unshadowed with it, so a dense pixel keeps fewer than `shadow.perpixel`
  shadows at full strength. That is the price of the edge being invisible;
- one-pixel slivers at half res can find no usable record; those pixels take one hardware tap for each light
  the nearest record texel holds, so a missed record is a HARDER shadow and never a missing one (counted). A
  sliver with no record texel near it at all keeps its lights unshadowed;
- `shadow.cap 1` and `2` mean the same on the deferred path.

The SUN is not in the record at all. Its cascades are sampled in the lit fragment stage, and the deferred
twin IS that stage, so the sun's own blocker search and disc (`shadow.sunangle`, `cx_sunshadow.glsl`) are the
same code on both paths and need no deviation list of their own.

## The lighting budget

Alloy used to light every pixel as though every light were the hero light: each light reaching a pixel got the
whole model -- the engine curve, the near-field core, the sphere wrap, the subsurface leak, a source-sized
specular lobe -- *and* a cube-shadow lookup, out to its full authored radius. On the CX-188 test area that read
as ~1.1M hard cube taps a frame spent on a pixel's fifth-and-later shadowed light, with `light.bincap` at 0
(unlimited) above it. Every limit that exists after the budget work, what happens past it, and the knob that
sets it:

| limit | default | past it | knob |
|---|---|---|---|
| **lights a froxel can hold** (`CX_CLUSTER_CAP`) | 128, a build constant | the froxel drops the rest and counts the overflow on the `[alloy]` line | -- (`AlloyLights.ClusterCap`) |
| **lights a froxel KEEPS** | all of them | -- | `light.bincap` (0 = keep all; a cap keeps the N strongest per froxel and draws the cluster grid where more overlap) |
| **a light's binned + walked radius, as a share of its authored one** | 0.8 | not binned, and the walk's window fades it to 0 over the last 30% in f2 | `light.binreach` (a global share: a look change, not a perceptual cut) |
| **a light's binned + walked radius, as a BRIGHTNESS** | 0.0003 display-linear | same window, per light | `light.floor` |
| **a light's share of the pixel's brightest** | 0.01 | faded out over `[cut, full)` and skipped, per pixel, so no grid | `light.relevance` |
| **lights with the full model at a pixel** | every light (off) | the plain engine curve: no near-field core, lobe spread 0, no subsurface leak | `light.fullmodel` (a share of the brightest, faded in over the octave below it) |
| **lights a tile's own culled list can hold** | 128 (`CX_TILE_CAP` = the froxel cap) | dropped, counted as `TILE LIGHT LIST OVERFLOW` | -- (and `light.deferredtiles` is off by default, so the froxel lists are what the walk reads) |
| **lights a PIXEL may shadow** | 4 | lit **unshadowed**, counted as `lights unshadowed past the budget`; the lane at the budget's edge is faded toward unshadowed with it, so the boundary is continuous | `shadow.perpixel` (0 = no budget, and then every knob's off position is bit-identical to the pre-budget frame) |
| **lights a pixel's shadow record can hold** | 4 (`CX_SH_MAX`) | the format's ceiling, so `shadow.perpixel` cannot exceed it | -- (`cx_shrec.glsl`) |
| **lights a pixel BLOCKER-searches** (contact hardening) | 2 forward (`CX_CAP_MAX`); 1 deferred -- the record's strongest, the other lanes sitting at `shadow.soft`'s ceiling | the single hardware tap forward; the record's cheap 4-tap disc deferred | `shadow.cap` (0 = every light one hard tap, and it stands the record and the budget down with it) |
| **lights a pixel hardware-taps** | 0, unless it found no record | -- | implied: a pixel with no usable record keeps the pre-budget path, one tap per shadowing light, counted |
| **shadowed lights on a slim-walk receiver** (grass, blades, water, particles, weather) | every shadowing light, one hardware tap each | -- | none, deliberately: see the slim walk's own section |
| **view depth past which a pixel reads no cube** | 30 m | no shadow at all, faded out over the last quarter | `shadow.cubedist` |
| **lights that have a shadow cube at once** | 32 | no cube, so unshadowed everywhere | `shadow.cubes` |
| **movers with a dynamic page** | every cube rank | the combined bake | `shadow.dynranks`, `shadow.dyncount` |
| **mover spheres a hot light's ray test holds** | `CX_DYN_SPHERES` (`CasterPass.CubeDynSphere`) | the light keeps its page and the deferred frame owes it nothing; counted on `CasterPass`'s line | -- |
| **static lights that cast a creature's shadow onto baked ground** | 2 per creature | no pair tile, so that (light, creature) pair owes no debt | `lightmap.moverlights` (0 = the feature off) |
| **pair tiles in the atlas** | `lightmap.paircount` x `lightmap.pairsize` | the light keeps its page instead; counted | `lightmap.paircount` |
| **mirror (`light.hidden`) taps a pixel pays** | 1, for the strongest glossy lobe over `CX_MIRROR_FLOOR` | unshadowed reflection of the light | `light.hidden` |
| **film reflection picks a froxel ranks** | `CX_REFL_MAX` = 4, and `weather.filmlights` | not reflected in the film | `weather.filmlights` |

Three of those are per-pixel budgets on the SAME ranking -- `CxLightRank`: the light's **irradiance** at the
pixel, the engine curve times the linear colour's luminance. `shadow.perpixel` (what casts a shadow),
`shadow.cap` (what gets the soft filter) and the hot-light cap (what owes a mover's debt) all order their
candidates with it. A budget ranked one way and filled another does not hold what it says, which is why the
record stopped ranking by `f2`.

The score is a function of **position alone**, and deliberately so: N.L, the subsurface leak, the relevance
weight and the lighting LOD are all out of it, because a per-pixel *surface* term would hang the membership
contour off the normal map and make it speckle. It therefore has the spatial scale of a light's own falloff.
With the budget off (`shadow.perpixel 0`) the score is `-f2`; negation is exact, so `-f2 > -best` and
`f2 < best` are the same comparison, ties included, and the forward cap and the record order their candidates
exactly as they did before this work. The hot-light cap cannot take `-f2` (its accumulator starts at 0 and its
quantity is a luminance), so it orders by the shared score when the budget is on and by its own pre-budget
luminance when it is off -- bit-identical either way. That cap carries the debt luminance separately from the
score now: one number was doing both jobs, so a change of ranking silently changed the debt.

The hot, pair and mirror taps are separate budgets and keep their own semantics; they do NOT count against
`shadow.perpixel`. A pixel's worst case is therefore `shadow.perpixel` record lanes, plus one mirror tap, plus
the pair-debt dispatch's own `min()` over the pairs covering it -- three budgets, each with a counter.

### What the period line says

`DeferredLights`' `[alloy]` clause carries the two numbers the budget is about, sampled over the harvested
frames: **`N lights/px`** -- the average lights that actually lit a deferred pixel, which no longer grows with
the area's lamp count -- and **`N hard taps/px`**, the cube taps the walk still paid, which the per-pixel shadow
budget holds near zero. Beside them: the lights the budget left unshadowed (its spend), the pixels that found no
usable record (the record's reach), and the tile-list overflow. The walked count is accumulated in the shader's
own sixteens (`CX_WALK_UNIT`) so a 32-bit counter cannot wrap between harvests, which makes it read about a
percent low: an average, not an audit. **`N entries/px (M rejected)`** beside them is the list length the walk
indexed and how much of it lit nothing (reach, relevance, facing): the price of a long list is paid per entry,
lit or not.

`/alloy bench` also prices the passes from the inside: it rebuilds `cx_deferred_cs`, `cx_defshadow_cs` and
`cx_pairdebt` with `CX_ABLATE n`, each rung stopping after one more stage, and ends with one
`[bench] ablation:` line per pass giving each stage's own cost. The variants exist only while the bench runs.

## Streams (branch `cx-188-deferred-lighting`)

| step | owner files |
|---|---|
| 0 ADR | `docs/adr/0029-*.md`, `GLOSSARY.md` |
| 1 G-buffer writer | `Pass/GBuffer.cs`, `cx_ownlit_*.glsl` (CX_DEFERRED blocks), `Programs.cs` (the twin), `Issue.cs` (selection), Ssr draw-buffer interplay |
| 2 deferred compute | `Pass/DeferredLights.cs`, `cx_deferred_cs.glsl`, `cx_lightfrag.glsl` (list-source generalisation), `ShaderStages.All`, bench step, period line |
| 3 shadows in compute | `Pass/DeferredShadows.cs`, `cx_defshadow_cs.glsl`, `cx_shrec.glsl` (the record's format), `cx_lightfrag.glsl` (the `CX_SHADOW_SOURCE` hook), `cx_localshadow.glsl` (`CxLocalShadowFast`), `cx_deferred_cs.glsl` (the reader) |
| 4 hot-light debt | `Pass/PairShadows.cs`, `Pass/PairDebt.cs`, `cx_pairbuf/cx_pairshad/cx_pairdebt.glsl`, `ShadowMath.PairViewProj`, `CasterPass` (the hoisted cull verdict, the plan, the pair bakes), `cx_lightfrag.glsl` (`CX_NO_HOT` in the full body), `cx_localshadow.glsl` (`CX_PAIR_RECV` in `CxLocalShadowQ` and `CxLocalShadowFast`), `cx_deferred_cs.glsl` + `cx_defshadow_cs.glsl` (the define) |
| 5 forward slim loop | `cx_lightfrag.glsl` (the `CX_LIGHT_LITE` bracket), the nine consumer `*head` stages, `ShaderContract.cs` |

`cx_gbuf.glsl` and this page are the seam: a change to either goes through the integration branch, never a
stream.
