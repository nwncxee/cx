# The shader override contract

NWNCX:EE draws most of the world with its own GLSL. Every stage of that
pipeline ships **embedded in the extender binary**, and every one of them can be
replaced at runtime by a `.shd` resource of the same name — a customization surface
for people who want the look to be theirs.

Alloy renders through Vulkan, and that changes nothing about what you write: a stage is still GLSL text,
still overridden by a `.shd` of GLSL text. The extender translates it for Vulkan and compiles it with
shaderc at load, so the source you edit is the source the GPU runs.

This document is the contract that surface comes with: what an override is, what the
twenty-nine stages are, and precisely what the extender refuses and why.

## What an override is, and what it is not

An override is a **customization**. You take the stage this build compiles, edit it,
and place it where your client can see it. The extender picks it up instead of its own
copy.

A hak is a supported place for one. A module that wants Alloy to look its way ships
`cx_*.shd` in its hak (or NWSync) and every extended player gets that look; a stock client
ignores the files. See [ADR 0024](adr/0024-hak-shipped-alloy-overrides.md), which
supersedes 0012's ban.

What shipping costs is **drift**, and it is not hypothetical: when the per-draw record grew
in #120, every shipped copy went on reading each draw out of its neighbour's slot and the
world rendered black and streaked. Three things keep that in hand, and a shipped override
depends on all three:

- **Start from `/alloy shaderdump`, re-dump after each extender update.** The dump is what this
  build compiles, so the diff against your copy is exactly the drift.
- **Keep the sizing tokens.** They are expanded on load, so a size change on the C# side
  never stales a file that kept them.
- **The guards below.** A shipped stage that disagrees with the record, cutout, skin or
  linear contract is thrown away for the embedded one with a log line, not rendered wrong.
  What they cannot catch is a feature the embedded stage gained and your copy did not.

## Getting a starting point

Type **`/alloy shaderdump`** in-game (chat commands need General → "Enable chat
commands" in `/settings`, i.e. `general.commands=1`). It writes every overridable stage as an editable `.shd`
into `cx_shader_dump/` inside the client's own user directory (next to `nwn.ini`).

What you get is the stage **this build compiles** — dumped from the same text the
renderer uses, so it is current by construction rather than by somebody remembering to
update it. Each file opens with a short header naming the constraints that stage carries
and pointing back here.

There is also a headless trigger for offline work: put `alloy.dumpshaders=1` in
`cx.cfg` in the same directory, and the dump runs at startup.

### The workflow

1. `/alloy shaderdump`.
2. Edit `cx_shader_dump/<resref>.shd`.
3. Place it where your client's resource chain will find it — an `override/` directory
   for local play, a hak for a module you control.
4. Restart the client and read `~/cx.log`. An override that loaded says so:
   `shader override: <resref>.shd from resman (N bytes)`.

### Sizing tokens

Dumped files contain `$CAP$`, `$SORT$`, `$DYNSPH$`, `$CUBES$`, `$GROUP$`, `$SRGB$`, `$AA$`, `$POSTMASK$`
and `$SQM$` unexpanded (the authoritative list is `ShaderResource.Tokens`). **Leave them
that way.** The extender expands them when it compiles, for an override exactly as for
its own embedded default, so a file that keeps the tokens stays correct when those sizes
change on the C# side. Freezing today's numbers into your copy is how it goes stale.

### SPIR-V stages: not a supported override

**Write GLSL.** A `.shd` that holds a SPIR-V module instead of text is still sniffed by its magic word, and
still named in the log — but nothing consumes it any more. The accepting path was the GL program builder's
`glShaderBinary`/`glSpecializeShader` pair, and it went with the GL backend; the Vulkan side only ever
compiles modules shaderc has just produced from text it can see. `alloy.spirv=1` therefore reaches no live
code, and a module `.shd` falls back to the embedded stage with a line saying so.

That is not a gap waiting to be filled. A module is unauditable, and everything the text path does would be
skipped for one: no token expansion (`$CAP$` and friends are C#-side sizes), no library splice, no vanilla
bridge or content pair — all of which are text surgery — and none of the guards below, which read source
there would not be.

If you want a stage compiled your own way, compile it to GLSL and let the extender translate it.

## The overridable stages

These are the stage resrefs. The libraries they include are listed by the dump; see
[Libraries](#libraries).

| resref | stage | what it does |
| --- | --- | --- |
| `cx_own_vs` | vertex | Unlit ("fullbright") Alloy geometry: projects the batch, reads the per-draw record. |
| `cx_own_fs` | fragment | Unlit shading — texture, per-draw tint, alpha cutout. |
| `cx_ownlit_vs` | vertex | Lit Alloy geometry, including the skinning branch for creature parts. |
| `cx_ownlit_fs` | fragment | The main lit shader: clustered local lights, sun cascades, PBR material synthesis, key light and fog. The big one. |
| `cx_water_vs` | vertex | Water surface. |
| `cx_water_fs` | fragment | Water shading: stock `fswater` on Alloy's lights, shadows, keyhole and fog. |
| `cx_grass_vs` | vertex | Instanced grass blades, wind animation. |
| `cx_grass_fs` | fragment | Grass shading. |
| `cx_cluster_cs` | compute | Builds the light cluster grid the lit and water stages read. |
| `cx_deferred_cs` | compute | CX-188 (ADR 0029): the deferred point-light pass. One invocation per render pixel: reads the G-buffer the lit stage's `CX_DEFERRED` twin wrote (`cx_gbuf`), reconstructs the pixel's world position from the frame depth, walks the lights through the SAME `cx_lightfrag` the forward loop is, and adds the result into the HDR target. 16x16 groups, and `light.deferredtiles` picks its light list: the froxel table every forward stage reads (the default), or this group's own depth-bounded cull. |
| `cx_defshadow_cs` | compute | CX-188 step 3 (ADR 0029): the shadow record. Per pixel of the G-buffer (half resolution under `shadow.deferredres`), the four strongest shadowed lights' visibility, filtered once, which `cx_deferred_cs` reads back instead of tapping a cube per light. A record that fails to link stands `light.deferred` down for the session. |
| `cx_pairdebt` | compute | CX-188 step 4 (ADR 0029): the hot-light debt. The deferred stage has the walk's hot branch compiled out, so this dispatch is where a creature's shadow comes back out of the baked term: each 16x16 tile culls this frame's (static light, creature) pair tiles against its own view-space box, and the pixels of a tile that holds one evaluate the tile and subtract `lmShare * min(lmSub.x / lmLum + lmSub.y, 1)`. `lightmap.moverlights 0` is its A/B. |
| `cx_vfog_cs` | compute | The volumetric fog's lighting pass (`vfog.enabled`): one invocation per froxel of the main view's grid (`cx_vfogmed`). Writes the light the medium scatters toward the eye per unit of extinction (rgb, linear) and the haze's own extinction (a), at a point jittered inside the froxel and blended with last frame's answer re-projected onto it. The sun comes through `cx_sunshadow`, the lamps through `cx_lightfrag`'s cluster table: a lamp's curve sharpened toward an inverse square whose strength grows with its reach (`vfog.reach`), an ambient-class light (a tile's own) its surface curve with no direction or shadow. 8x8x1 groups. |
| `cx_vfogsum_cs` | compute | The volumetric fog's integration: one invocation per froxel column, marching the slices from the eye, adding the area's fog wall to the haze and storing the in-scattered light and transmittance to each slice's far edge. The wall thins away across the fog bank's top (`vfog.top` above the eye), and the sky reads the last slice: the same air as the geometry in front of it. Every stage that fogs reads the result through `cx_vfog`. 8x8 groups. |
| `cx_cast_vs` | vertex | Shadow-map depth bake: projects casters into a cascade or cube face. |
| `cx_cast_fs` | fragment | The caster pass's fragment stage. Depth-only, and deliberately inert. |
| `cx_post_vs` | vertex | Fullscreen triangle shared by the whole post chain. |
| `cx_bloomdn_fs` | fragment | Bloom downsample. |
| `cx_bloomup_fs` | fragment | Bloom upsample. |
| `cx_resolve_fs` | fragment | The resolve: tonemap, dither, and the one stage that encodes to display space. |
| `cx_postgam_fs` | fragment | The post stack's gamma stage (#375): the client's gamma option as `pow(c, g/2.2)`, display space in and out. |
| `cx_smaaedge_fs` | fragment | The aa stage's first pass (#377): SMAA luma edges of the resolved frame. Carries the reference `SMAA.hlsl` under its GLSL port; `$AA$` is the preset the client's anti-aliasing option picked. |
| `cx_smaawgt_fs` | fragment | The aa stage's second pass: SMAA blend weights from the edges and the embedded area/search tables. |
| `cx_smaablend_fs` | fragment | The aa stage proper: SMAA neighbourhood blend of `uSrc` by the weights, display space in and out. |
| `cx_toon_fs` | fragment | The post stack's cel stage: the client's Toon video option and its three sliders -- a plane-fit depth and colour ink, and an opt-in luminance posterise. Display space in and out. |
| `cx_sharpen_fs` | fragment | CX-93: the post stack's sharpen stage -- the client's Sharpen video option. The stock effect's asymmetric edge darkening plus its global lift, without its own AA (the stack has SMAA/TAA ahead of it). Display space in and out. |
| `cx_vibrance_fs` | fragment | CX-93: the post stack's vibrance stage -- the client's Vibrance video option at the engine's own amount (`uVibrance`), saturating the greyest pixels hardest and restoring luminance. Display space in and out. |
| `cx_fbpost_fs` | fragment | #352: the hosted content post chain. Its `// $VANILLA-MAIN fsfbpostpr` region runs the resource chain's `fsfbpostpr.shd` whole, its `main()` and every include it reaches, so a hak or NWSync fork of that file or of `inc_postpr.shd` is what runs: a fork that draws over the frame from its own `main()` (a spell telegraph off the scriptable uniforms, a world position through `m_view_inv` and the depth) runs too. The stage stands down when nothing supplies it. Display space in and out. A native version of the chain (`cx_fsfbpostpr`, see "Native stages, images and passes") runs in this region in its place, built with `CX_NATIVE`. |
| `cx_taa_fs` | fragment | #389: the taa stage's copy-out of the history the accumulator resolved (`uHist`). The accumulator itself is embedded, not overridable. |
| `cx_ssao_fs` | fragment | Ambient occlusion (#379, ADR 0021): GTAO visibility from the prepass depth, R8, read by the lit stage's ambient term. Not a post stage: runs before the colour pass. The pass sets no sampler uniform: the depth is `layout(binding = 0) uniform sampler2D uDepth`, which `cx_ssaolib.glsl` declares. |
| `cx_ssaoblur_fs` | fragment | The depth-aware 4x4 blur that folds the AO pass's dither pattern away. Reads the raw visibility as `layout(binding = 1) uniform sampler2D uSrc`; a fork that drops the binding shares unit 0 with `uDepth`, and the device refuses it (logged; visibility ships unblurred). |
| `cx_halo_vs` | vertex | Light flares: one instanced billboard per authored flare element of every light vanilla would flare (`Halos.Flares`: lit, dynamic or affecting dynamics, near the scene focus, flare colour shifts and textures, outside fog of war). SSBO binding 8, three vec4 per element: position/occlusion softness, display colour + shift/flareradius, this element's flaresizes and flarepositions entries plus the occlusion-probe flag. The list is grouped by texture and drawn one range at a time, so the stage indexes from `uFirst + gl_InstanceID` — drop that and every group past the first draws the wrong element. Sized and placed as vanilla's flare quad (screen-space size, cos² off the view axis, reach to zero at `flareradius`, each channel clamped before linearising), and it hands the fragment stage `vOcc`, the NDC step from the element to the light. |
| `cx_halo_fs` | fragment | The flare look: the element's own texture on unit 0 (`uFlare`, `uTextured` 0 where content named none that is resident, which falls back to a procedural gaussian core, soft skirt, faint streaks and chromatic spread), times the per-pixel occlusion against `uSceneDepth` sampled at the light (`vOcc`, soft over one radius). Additive over the linear frame, so no encode. |
| `cx_weather_vs` | vertex | Rain and snow (#616): one instanced quad per drop or flake, its lattice point a hash of `gl_InstanceID` in a fall-aligned box that wraps around the camera as `uPhase` slides it; a streak along `uFall` or a camera-facing disc. Tests the instance against the rain occlusion map (`uCover`, a layer of the sun array, #617) and sends a sheltered one off-screen. No buffer, no attributes. |
| `cx_rain_fs` | fragment | The rain streak: a tapered core, faded where it meets geometry through `uSceneDepth` (`uSoft` = 1/m). Alpha over the linear frame, so no encode. `uViz` 1 paints the fade. |
| `cx_snow_fs` | fragment | The snowflake: a soft disc, the same contact fade. Alpha over the linear frame. |
| `cx_splash_vs` | vertex | CX-102: rain splashes -- one instanced quad per crown part, the crown's landing point read out of the cover map's own depth (`uCoverDepth`, the plain-depth view of the layer `cx_weather_vs` only compares against), so an impact lands on a roof, a ledge or the ground and never under shelter. A crown stands only on a plane: a landing point off the plane of its four neighbouring cover texels is an edge and draws nothing, nor does a face steeper than about 80 degrees, and the crown's ring and droplets are laid out in that plane, along its normal. A footstep crown takes the plane under the foot the same way, unless the cover map's hit there is a roof over it. A landing point the frame shows nothing at (`uSceneDepth` lies well past it -- a wall the keyhole dissolved) draws nothing either. `gl_InstanceID / CX_SPLASH_PARTS` is the crown, the remainder its part: index 0 the ring, the rest ballistic droplet streaks. The ring is strictly world-sized and fades with its screen size; every part fades out inside the first metre from the lens (`CX_SPLASH_NEAR`), so a camera inside a wall does not smear the crowns landing on it; a sub-pixel droplet widens to about a pixel and gives the area back as scatter. Same per-vertex light rig as the drops. No buffer, no attributes. |
| `cx_splash_fs` | fragment | One crown part: a thin expanding ring or a droplet streak, as premultiplied scatter over the linear frame. No contact fade -- a splash sits ON the surface, and the depth test is what hides it behind an occluder. |
| `cx_sky_fs` | fragment | CX-88: the sky, as the ENVIRONMENT a content sky stage runs in -- the names `inc_common`/`inc_uniforms`/`inc_standard` give a sky fragment stage, from Alloy's values and under the engine's own names. The `// $VANILLA-MAIN $SKYFS$` region is replaced at build with the material's own `customShaderFS` (e.g. a content `fst_sky`), main renamed; `$SKYFS$` is expanded by the extender, so leave it a token. The wrapper samples the diffuse, calls the stage, and takes its DISPLAY-space result to linear -- which is why there is no decode pass. The vertex half is `cx_own_vs`. A stage's **native version** (`cx_<stage>`, see "Native stages, images and passes") is hosted in this same region, built with `CX_NATIVE`: the clamp and the encode come off and the stage hands back linear HDR. |
| `cx_pass_cs` | compute | The wrapper a native stage's **pass** runs in: the whole-stage environment (`cx_stageenv`, the same one `cx_sky_fs` carries) plus the `// $VANILLA-MAIN $PASSCS$` region, replaced at build with the content compute stage a `#pragma cx_pass` line names, main renamed. `$PASSCS$` is expanded by the extender, so leave it a token. Overriding this replaces the environment a pass runs in, not the pass. |

One more file appears in the dump: `cx_ownlit_bl_ref.shd`. It is **reference
only** — the lit stage after the extender's own bindless rewrite. The extender never asks
for that resref, so shipping it changes nothing. Override `cx_ownlit_fs` and the rewrite is applied to whatever
you supply.

## Libraries

A stage is mostly `#include`s. Each library it includes (fog, the local and sun shadow
receivers, material synthesis, the keyhole, and so on) can be overridden **on its own**, as
`<resref>.shd`, and the replacement reaches every stage that includes it. The dump writes
every library next to the stages, unexpanded, under a short banner. Every embedded file is
named by a `cx_*` resref of at most 16 characters, because that is all a hak or NWSync
entry can carry. `ShaderSource.SelfCheck` refuses a longer name.

- A dumped stage brackets each library it spliced with `//@cx-lib <resref>` and
  `//@cx-end <resref>`. Keep the brackets and a library override still reaches your copy
  of the stage. Delete them and that library is frozen in your copy.
- A library override's own `#include` lines are expanded, and those libraries can be
  overridden in turn.
- A program that a library override breaks (compile or link) rebuilds from the embedded
  libraries with a log line, the same way a broken stage override does.
- The guards below read the text that actually ran, library overrides included. A stage
  they refuse is rebuilt from embedded stages **and** embedded libraries.
- **`cx_target` is the spell-targeting ground shape** (CX-93). It used to be the engine's own
  `inc_target.shd`, spliced in from the resource chain; it is Alloy's code now, so a content repo that
  wants a different shape — a different outline, a wider cone, a metamagic-aware range — ships
  `cx_target.shd` rather than forking a stock resref. Its uniform names are still the engine's
  (`targetShape`, `targetShapeSizeX`, `targetShapeSpellRange`, `targetShapeColour`, …), because the values
  are the engine's own global shader constants; `CxTargetStrength()` and `CxTargetApply()` are what the
  stages call.

## Content shaders

The stages above are Alloy's own. A **content pair** is yours: the `customShaderVS` and `customShaderFS` a
`.mtr` names, written the way you write them for the stock renderer. Alloy runs any pair, translated into
its own pipeline. Nothing is recognised by name.

**When it runs.** The `.mtr` names both halves, as the engine requires before it builds a content program at
all. A pair whose halves both add nothing to their stock counterparts (`#define`s, the engine's own includes,
and a `main()` that is the stock opener, the two standard calls and the hand-off) is the stock shading
already, so it draws Alloy's own lit path and builds nothing. Every other pair gets programs of its own, a
forward one and a deferred twin, plus a depth twin and two caster twins when its parts' depth is its own (see
below). They compile in the background from the first time a part on the pair draws, and are saved to the
shader cache. Until then that part draws the plain lit path.

**The vertex half** runs after Alloy's own vertex work. The stock outputs (`gl_Position`, `vPosView`,
`vPosWorld`, `vVertexNormal`, `vVertexTangent`, `fTextureHandedness`, `vVertexTexCoords`) start out holding
Alloy's own values, so a half that never touches one hands Alloy's back. Whatever it leaves is what Alloy draws
and lights: a swaying plant sways, a roof that drops itself under the floor drops. The attributes (`vPos`,
`vTcIn`, `vNormal`, `vTangent`, `fHandedness`, `vColor`, `vIndex`, `vWeight`), `m_bones` and `skinmesh`, the
matrices and the stock transform helpers (`TransformMainCoordinates`, `SkinmeshTransformPosition`, …) are
there under the engine's names. A varying the half declares of its own reaches the fragment half by name, as
the engine's link does. `cx_content_v.glsl` is the full map and `cx_constd_v.glsl` the `inc_standard` stand-in.

**The fragment half** runs as a **surface modifier**. Alloy has done the stock setup by the time your `main()`
starts: `FragmentColor` holds the texel in working space, `vFragmentNormal` the shading normal, and
`fSpecularity`, `fRoughness`, `fMetallicness`, `SpecularColor` the synthesised material. Whatever you write to
those, Alloy then lights, deferred or forward. Where the engine would light it, at your `ApplyStandardShader()`:

- the statements before the call are the surface body;
- the statements after it run on the **lit** colour, in display space, as the engine hands it back (a local
  they read is carried over);
- a stage that never calls `ApplyStandardShader()` lights itself, so all of it runs on the lit colour and its
  result is drawn as it leaves it.

A pair with statements after the call, or one that reads the engine's per-light arrays, draws forward only:
the deferred path has no lit colour to give it. The alpha you leave in `FragmentColor` (or `gl_FragColor`) is
the part's coverage. `#define KEYHOLING 0` keeps the part from dissolving, as it does in the engine.

**Its own depth.** A pair whose vertex half runs, or whose fragment half makes its own coverage (a discard, a
written alpha, `ENVIRONMENT_MAP 1` or `NO_TEXTURE 1`), places or cuts its own depth. Its parts go through the
pair's depth twin in the prepass and cast through its caster twins: the same vertex half and the same cut as
the colour draw, so a swaying hedge's shadow sways and a hair card's shadow keeps its gaps. The caster twins
need the bindless handle table; without it such a part casts off its diffuse alpha, unmoved.

**The environment.** The engine's prologue defines are there (`SHADER_QUALITY_MODE 2`, `NO_DISCARD 0`,
`BUILD_VERSION`/`BUILD_REVISION` of the running client, `varying`, `texture2D`, `gl_FragColor`), and so is
`CX_ALLOY`, so a stage can specialise for Alloy with `#ifdef CX_ALLOY`. The engine's vocabulary maps onto
Alloy's values:

- the varyings (`vTexCoords`, `vPosWorld`, `vPosView`, `vVertexNormal`, the tangent pair);
- `texUnit0`..`texUnit10` with their `textureNBound` flags (`texture7`..`texture10` as bindless handles);
- `texUnitNoise` (the engine's `solid_noise`), `texUnitEnvCube` (the area probe);
- the material terms, the fog, camera, player and wind terms;
- the module calendar (`moduleYear`..`moduleTransitionTime`), `sceneCurrentFrame`, `areaWeatherType`, the
  mouse, the wind's point sources (`windPointSourcesCount` and its five arrays) and the engine's projection
  `m_proj`, under the engine's names — these live in the `CxEngine` uniform block (`cx_envblk.glsl`), and the
  engine's names are macros over its members, so a stage that declares one of them itself (as `inc_uniforms`
  declares the whole set and `vs_beamvol` an inline `m_proj`) has its declaration dropped rather than
  colliding. An **override** of an environment must not re-declare one as a loose uniform: it would expand to
  a redeclaration of the block member and fail the compile;
- the per-light arrays (`numLights`, `lightPosition`, `lightColor`), filled per fragment from the lights binned
  there, for a stage that lights itself;
- `inc_scriptable`'s block, set by name from the engine's own constants;
- the stock helpers (`ApplyColorSpace`, `ColorToBrightness`, `AlphaDiscard`, `SetupTSB`, the noise and hash
  functions and so on).

"View space" is world space with the eye at the origin, so `normalize(vPosView)` is the view direction and
`m_view_inv * vPosView` is the world position, as they are in the engine. The full map is `cx_content_f.glsl`,
and like every library it is overridable as `cx_content_f.shd`. `cx_constd_f.glsl` is the stand-in for
`inc_standard`.

**Parameters.** A uniform either half declares reads its `.mtr` `parameter` line under the engine's own upload
rules:

- the name is case-sensitive, and a name both halves declare is one parameter;
- one value feeds a `float`, two to four values feed a `vec4`, and `parameter int` feeds an `int`;
- a `vec2` or `vec3` is never fed (the engine's `glUniform4fv` does not fit one), so it keeps its GLSL
  default;
- a uniform no line states keeps its GLSL initialiser;
- `SetMaterialShaderUniformInt/Vec4` on an object overrides a parameter its `.mtr` declares, for that
  object only, and an empty value resets them all;
- twelve parameters per pair.

**What differs from the engine.**

- UV sets 1 to 3 (`vTcIn1`..`vTcIn3`) read UV set 0: the mesh arena carries one set, and no content in any
  corpus reads another.
- A ground decal on a content pair draws in the decal layer off its texel, so its fragment half does not run
  there; one whose vertex half runs stays out of the layer.
- The point sources are Alloy's own wind's (`view.wind`), so with Alloy's wind off they read none.
- With `alloy.a2cmask` on, a part in the antialiased coverage layer still draws its pair's own program, which
  writes no per-sample mask: its edges cover every sample its cut keeps, rather than the mask's dithered ones.
- The stock helpers the stand-ins supply (`NoiseSimplex2D`/`3D`, `Random`, …) are Alloy's own, with the same
  contract but not the same values. A stage that includes the engine's own file gets the engine's. Without the
  bindless handle table, `texUnitNoise` and `texture7`..`texture10` sample the diffuse.
- A vertex half reads `texUnit0`..`texUnit10`. The probe and noise units reach the fragment half only.

**Checking it.**

- `grep "mtr shader:" cx.log` lists every pair a material named and what Alloy did with it.
- `grep "Alloy content pair" cx.log` says, per pair, which halves run, how many parameter lanes it took, which
  twins it built, and what the translation could only approximate. A pair that cannot run says why, as NOT RUN.
- Offline, `CX_CONTENT_STAGES=<dir> ./check-shaders.py` builds every material stage under `<dir>` the way the
  engine does (from the game's own shaders, `ovr.bif` over `base_shaders.bif` as the client resolves them) and as Alloy runs it, paired with its partner by name
  where the tree has one, twins included. It fails when Alloy cannot run a stage the engine can.

## Native stages, images and passes

Wherever Alloy hosts a content stage, a module may ship an **Alloy-native version** of it: a file named `cx_` +
the stage's resref, beside the stage. Where the resource chain answers for that name, Alloy runs it in the
stage's place. A vanilla client never asks for it, so the module keeps one version that works under both
renderers. The hosts:

| host | the stage | its native version |
| --- | --- | --- |
| the sky | the `customShaderFS` a sky material names | runs in `cx_sky_fs`, in linear HDR |
| a material pair | either half of the `customShaderVS`/`customShaderFS` pair a `.mtr` names | runs in the lit stages' content region, translated like any half (above); the other half is its own native version, the plain half, or stock |
| the post chain | `fsfbpostpr` | runs in the region of the EMBEDDED `cx_fbpost_fs` (an override of that wrapper names the chain's own stage); display space, as the chain is |

Nothing is recognised by name beyond the `cx_` prefix. A resref that is one of Alloy's own stages or libraries
(`cx_sky_fs`, for a content stage called `sky_fs`) is an override of Alloy, never a native stage. `alloy.native 0`
runs the plain stages everywhere, which is the A/B; the edit drops every spliced program, and they rebuild.

A native stage differs from the stage it replaces in three ways:

- **It is built with `CX_NATIVE`** defined, so a library both versions include can tell them apart. In the sky's
  host it also means **linear HDR out**: neither the clamp to one nor the display encode applies, so a sun can be
  40 and bloom. NaN and half-float overflow are still kept out of the frame and the probe cube. A material half
  keeps its host's output convention.
- **It may declare GPU images and compute passes of its own**, on `#pragma` lines in its own top-level text. An
  include is a library, not a place to declare. GLSL ignores an unknown pragma, so the lines cost the stage's
  own compile nothing.
- **It may name a fog colour and an overlay**, which the extender's own passes consume.

The `#pragma cx_` lines of a plain stage, one the engine also compiles, are not read.

A native stage may be a **shim**: it `#include`s the stage it stands in for and replaces one part of it. The stage
marks the part with a hook, its stock code under `#ifdef <macro>`'s `#else` and a prototype under the `#ifdef`; the
shim declares its images and passes, defines the macro, includes the stage and defines the replacement after the
include, where everything the stage declares is in scope. The stage's `main()` is the program's, so the shim declares
none: each host renames the `main()` of the text with its includes in place. A sky shim still hands back linear
light, so the stage converts its display colour under `#ifdef CX_NATIVE`, `pow(c, 2.2)` as the plain sky host does.

### The declarations

```
#pragma cx_image   <name> tex2d <W> <H> <format> [flag ...]
#pragma cx_image   <name> tex3d <W> <H> <D> <format> [flag ...]
#pragma cx_image   <name> view <N> <format> [flag ...]
#pragma cx_pass    <once|frame> <resref> <image>
#pragma cx_fog     <image> <x> <y>
#pragma cx_overlay <image>
```

Every token is a **GLSL identifier or a decimal integer**, and nothing else: a GLSL preprocessor tokenises the
line, and NVIDIA's refuses `3d:128x128x128` and `128x128` outright (`invalid char 'd' in integer constant
suffix`, `type suffix 'x' on constant literals`) — so a shape written that way would cost the stage its own
compile. Tokens are whitespace-separated and case-sensitive; a malformed line, or an unknown `cx_` verb, fails
the stage.

- `<name>` is the GLSL identifier a program reads the image by: at most 31 characters, unique among the stage's
  images, and not in the reserved `gl_` space.
- `tex2d` / `tex3d` take their extent in texels. `view <N>` is the **main view's render size** divided by `N`
  (rounded up, `N` in 1..8), so it follows the window and `alloy.scale`; a view image is always
  `CLAMP_TO_EDGE`.
- `<format>` is one of `rgba8`, `rgba16f`, `rgba32f`, `rg16f`, `rg8`, `r16f`, `r32f`, `r8`,
  `r11f_g11f_b10f`.
- Flags: `history` makes a second image, which a program reads as **last frame's** contents through the
  uniform `<name>_prev`; `mips` gives a full mip chain, regenerated after every pass that writes the image
  through an `image2D`/`image3D` uniform; `clamp` is `CLAMP_TO_EDGE` (`tex2d`/`tex3d` default to `REPEAT`).
  A `history` image needs a `frame` pass that writes it — only such an image's two halves swap, and a
  `history` flag on one nothing rewrites each frame is refused, since swapping it would alternate its name
  between the baked texture and a zeroed one, every frame, with nothing to see but the flicker.
- A pass's `<resref>` is a content compute stage in the chain, hosted in `cx_pass_cs`. It is dispatched
  over the named image's extent divided by the pass's **own** `local_size` (each axis rounded up), so the
  work-group size is declared once, in the GLSL, and read back off the program.
- `cx_fog` names the texel that holds **the colour the area's fog converges to**, linear: one texel of a
  `tex2d` image, inside it. See "The fog colour" below.
- `cx_overlay` names an image the resolve adds **over the whole frame**, sky and scene alike: a lens flare,
  frost on the glass, a vignette. A `tex2d` or `view` image that a `frame` pass writes as an image. See "The
  overlay" below.

Budgets, refused loudly past the edge: 32 native stages a module; per stage, 32 images, 32 passes, 2d sides up to
4096, 3d sides up to 256;
512 MiB of image memory across every native stage, taken at build over the fixed-size images and again with the
measured sizes whenever the view-sized ones are made. A module has **one** fog colour and **one** overlay: a stage
naming either while another live stage holds it is refused. Passes run in **declaration order**.

### Binding, by name

Every program built from a native stage — the stage's own programs and each pass program — gets the stage's
images where it declares a uniform of that name. The uniform's **type** decides how:

- `sampler2D` / `sampler3D` → the image's own texture unit. Every image, and its `_prev` half, takes a
  unit of its own from **33** up (`TextureSlots.ResourceBase`), above every unit Alloy claims, for the whole
  frame, and a sampler is bound to
  it once, when its program is built. So an image reads the same from every pass that draws the program: the
  colour pass, the depth prepass, a shadow caster, a probe face, a reflection. `LINEAR` magnification, `LINEAR`
  or `LINEAR_MIPMAP_LINEAR` minification, wrap per the flags.
- `image2D` / `image3D` → an image binding of the program's own, read back off the compiled module and filled
  from the frame's descriptor set each time the host binds the program (the declared format, read-write,
  layered for a `tex3d`): bindings 0..5 in a drawing program (the lit stage's decal images
  hold 6 and 7), 0..7 in a pass. The GLSL `layout(<format>)` qualifier has to be the format the `#pragma` line declared: a
  mismatch is undefined loads and stores, so `check-shaders.py` reconciles the two and fails on a disagreement.

A material pair's two halves link into one program, so an image either half declares reads in both; a name
both halves declare is refused. Declare an image whose shape and type disagree (`sampler3D` on a `tex2d`, say),
or `<name>_prev` for an image with no `history` flag, and the stage is refused by name. Every pointed uniform is
read back once it is set, and one the driver did not take refuses the stage too. A stage's samplers count
against the driver's per-stage limit (32 on every driver Alloy has met) only where it actively reads them; a
material half sits beside the lit stage's own samplers and has few to spare, and a link that runs out says so.

A drawing program may store into an image too: with `layout(early_fragment_tests) in;` it runs only where the
scene leaves its surface visible, so a store says "this pixel showed". The next frame's passes read it: before
them, the extender issues an image-access barrier.

### The frame

Every program Alloy hosts — native or not, sky, material or post chain — gets these set by name where it
declares them. No environment declares them, so a stage declares the ones it reads, and a stage the engine also
compiles may declare them too and reads 0 there.

| uniform | what it carries |
| --- | --- |
| `int cxFrame` | the frame counter, one per presented frame |
| `int cxMain` | 1 while the program draws into the main view, 0 into an env-probe cube face or a reflection |
| `int cxFresh` | 1 when this stage's history is not one frame old: its images were (re)made this frame, or its passes resumed after a frame they did not run |
| `mat4 cxInvVp` | the main view's UNJITTERED inverse view-projection: clip -> world |
| `mat4 cxPrevVp` | that view-projection one frame ago; equal to the current one after a frame Alloy did not draw |
| `vec3 cxEye` | the main view's eye, world space |
| `ivec2 cxView` | the main view's render size, in pixels |
| `int cxOverlay` | 1 on a frame a native stage's overlay is added, else 0 |

Every pass of a frame and every view a program is drawn into sees the **main view's** camera. A frame whose
stage did not run its passes on the frame before is a gap — indoors, out of view, `effects.sky 0`, a loading
screen — and reprojecting against a camera from minutes ago is worse than not reprojecting, which is what
`cxFresh` is for. Beside these, a pass gets every engine term the sky's fragment stage gets — the calendar and
weather scalars (`moduleYear`, `moduleTimeIntoTransition`, `areaWeatherDensity`, …) and `m_proj` out of the
same `CxEngine` block the content pairs read, plus the scriptable block, fog, time of day and the camera —
from the whole-stage environment `cx_stageenv`, one upload for both.

### When things run

- At the frame's **head**, after everything is staged and before the depth prepass, on the main view only:
  every live native stage's images are made if they are not, or remade when a view resize changed a view-sized
  one, and its `once` passes run over them. They re-run after a device loss, after a module change, and
  whenever a resize remade any image.
- Then each stage drawn in the previous frame, or staged in this one, swaps its history images and runs its
  `frame` passes. The sky and the material pairs are staged ahead of the head, so their passes run before the
  frame draws them. A stage nothing draws — a sky indoors, a material none of the frame's batches use — stops costing
  anything a frame after it leaves, and comes back with `cxFresh` 1.
- After every dispatch: an image-access and texture-fetch memory barrier, then a mip regeneration for each
  image the pass bound as an image and declared `mips`.
- `/alloy` prints the passes' GPU time as `native passes …`, and the `native stages:` clause of the `[alloy]`
  period line names each stage, how many passes ran this period, how many frames ran with no history (`cxFresh`
  1; a steady view has none) and how much image memory it holds.
- `/alloy snap` writes the frame's finished scene colour into `cx_snap/` as one `<set>_vkscene.pfm` (float
  RGB, rows bottom-up; `tools/snap/snap_stats.py` reads it), and every live stage's images, level 0, beside it as
  `<set>_native_<image>.cxi`: a `CXI <w> <h> <d>` line, then float RGBA, rows bottom-up. `cx.log` gets a
  `[snap]` line per pass and per program a host draws, with what its frame uniforms, samplers and images hold at
  that moment beside what they should. Each pass's plain-value uniforms go to `<set>_native_<pass>.uni` (the
  dome's to `<set>_native_<stage>_<program>.uni`), a `name f|i values…` line each, and
  `tools/sky/preview.py --uniforms <cx_snap>/<set> --size <view>` replays them, so the game's exact frame renders
  offline. A uniform a pass does not read is not in its file: pin one that a patched copy reads with `--scene`.

### The fog colour

A plain dome feeds the fog its texture's mean colour, and `fog.sky` pulls fully fogged geometry, the skyfade
band and the horizon band (`fog.skyband`) toward it, so the fog meets the sky without a seam. A hosted stage's
textures say nothing about its colour, so without a `cx_fog` texel the fog keeps the area's flat colour —
which, against a sky drawn by a shader, is a wall of the wrong colour along the horizon.

With one, the extender copies that texel back every frame its stage runs, without waiting on the GPU: the
colour arrives two frames late and is the colour total fog converges to from then on, in place of any dome's
mean. A frame pass should write it, since a sky changes over the day; any native stage may name it, a sky's being
the usual one. The area's own colour still owns the near and middle of the ramp.

### The volumetric fog

With `vfog.enabled` on, the fog a stage takes through `CxFog`, `CxFogFactor` and `CxFogReveal` (`cx_fog`), and the
sky's through `CxFogSky` (`cx_fogmix`), is the volume's: in-scattered light and transmittance between the main
view's eye and the fragment, `lin * T + S`, out of the 3D texture `uVFogVol` on unit 32. `CxVFogAt(world)` and
`CxVFogSkyAt(world)` (`cx_vfog`) are the lookups themselves, for a stage that fogs by its own expression. `uVFog.x`
is 1 while the pass reads the volume -- the main view only; a probe face or a reflection reads the ramp -- and a
stage that branches on it keeps both paths, since the switch is the player's (or the server's) and moves live.
The volume is built from the area's fog colour, start and end; a native stage's `cx_fog` texel still feeds the
sky colour it is pulled toward (`vfog.sky`). The resolve grades the share of a sky pixel's light the volume put there
(`S` over the pixel) the way it grades the fogged geometry beside it, so the two meet without a seam whatever the exposure;
with no dome at all, it covers the bare clear colour with the volume's whole ray (`CxVFogSkyUv`) itself.

### The overlay

The resolve samples the `cx_overlay` image across the frame (bilinear, so a `view 2` image is upsampled) and adds
it, as display-linear light, **after** the tone curve and before the encode — over a sky pixel, which the
resolve does not tone-map (`post.skyraw`), and over the scene, which it does, alike, so a lens ghost that crosses
a ridgeline stays one ghost. It is added only on frames its stage's `frame` passes ran, which is the main view
of a frame that stage is drawn in; in a probe face or after a gap there is none.

A content post chain with an effect of its own can stand aside for it: where the hosted `fsfbpostpr`
(`cx_fbpost_fs`) declares `uniform int cxOverlay;`, the extender sets it to 1 on a frame an overlay is added
and 0 otherwise. The chain declares it, under `#ifdef CX_ALLOY` so the stock client never sees it, and an
extender build that predates overlays leaves it 0.

### When it fails

A native stage that fails **anywhere** — a malformed declaration, a budget, an image that cannot be made, a
pass that is not in the chain or will not build as a compute pipeline, a stage that will not translate or
will not compile, a program whose images disagree with it, or a block a pass has no buffer for — is logged
once by name with the reason, and its host runs the
**plain stage** instead. That fallback is the module's own: a real picture, not a stub. The refusal is a latch.
A native sky that gives up mid-frame moves that one sky stage onto the plain program and stays there; a material
pair rebuilds from its plain halves the next frame that asks. A module change, a device loss or an
`alloy.native` edit is what clears it. `grep StageResources cx.log` is the whole story, and the `native stages:`
clause names each stage with what it runs, or `FELL BACK:` and the reason.

The native stages, their images and their passes have a **module** lifetime — a module change drops them, and
they die with the device.

Offline, `./check-shaders.py` always compiles and driver-links the fixture under `tools/native/fixture/` — a
native sky, its fallback stage and its three passes; a native material pair whose vertex half reads the image its
fragment half declares, with its pass; a shim of a material half; and a native post chain with its pass — and does the
same for every native stage it finds under `CX_CONTENT_SHD` and `CX_CONTENT_STAGES`, each the way its host
builds it, and fails a material half whose image uniform the translation turned into anything else. It reads a stage's
host and half off its text with its includes in place, so a shim checks as the material half it is. It mirrors
the pragma grammar — and pins that mirror to the extender's own parser by running the
accept/refuse lines out of `StageResources.SelfCheck.cs` through it — so a stage whose declarations the extender
would refuse fails the gate instead of a player's first frame. A pass stage missing from the chain, an image
uniform whose `layout` format or 2D/3D-ness disagrees with its declaration, and a `history` image or an overlay
no frame pass writes all fail there too. `tools/sky/preview.py` renders a native sky offline through that same
parser.

## The guards

An override that fails to compile or link costs you a log line and nothing else: the
extender rebuilds from its embedded default.

The dangerous overrides are the ones that build **perfectly** and are still wrong,
because they were written against an older version of a contract the C# side still
holds. Those failures are silent — a black world, missing shadows, a washed-out frame,
and an empty log. The guards exist to turn each of them into a log line.

Every guard works the same way: it inspects the text that **actually compiled**, and if
it disagrees, the program is thrown away and rebuilt from the embedded stage. The frame
is then correct and merely un-restyled, and you get a line naming what to fix.

Guards match **named terms**, not whole files. You can restyle, reformat and rename
freely around them; what you cannot do is drop the term the rest of the pipeline is
built on.

### Record — the per-draw SSBO layout

**Applies to:** `cx_own_vs`, `cx_ownlit_vs`, `cx_ownlit_fs`, both `cx_water_*`, `cx_deferred_cs`
(as `CxDraw`); `cx_cast_vs` (as `CxCaster` *and* `CxDraw`).

**Rejects:** a `struct CxDraw` declaring a different number of trailing `vec4` fields
than the C# stride stages. Counted, not compared, so renaming or reformatting the fields
is fine. A stage that declares no record at all cannot disagree and passes.

`cx_cast_vs` (#302) carries three terms of its own: `struct CxCaster` must be exactly
two `vec4`s and no `mat4` (the 2026-08-30 misrender was precisely a leftover `mat4` --
a 96-byte GPU stride over a 32-byte stage, every caster baking at a random part's
matrix); the stage must read Alloy's record through the ownership slot, spelled
`uDraw[int(uCaster[gl_BaseInstance].aux.x)]` verbatim; and it must keep the skinning
branch (`uBone`). Lose the branch and every creature casts its bind pose while it
animates -- it links fine and says nothing.

**Why:** the record is a contract with a C# constant. Declare too few fields and every
draw reads its neighbour's model matrix and material — a black, streaked world, silent.

**You see:** `OwnedDrawService: a content .shd for the <program> program disagrees with
the pass ... Ignoring it and building the embedded stages, which agree.` The caster pass
prints its own equivalent.

### Cutout — per-draw alpha

**Applies to:** the `cx_own_*` and `cx_ownlit_*` pairs.

**Rejects:** a fragment stage without `uAlphaCut`, or one that reads `vAlpha` without a
vertex stage that supplies it.

**Why:** a stage written before per-draw alpha links cleanly — `glUniform1f` on a
missing uniform's `-1` location is a legal no-op — and renders every cutout and blended
surface solid.

**You see:** the same `RebuildEmbedded` line as Record; it names both possibilities.

### Skin — creature skinning

**Applies to:** `cx_own_vs`, `cx_ownlit_vs`, and only while skinned parts are armed.

**Rejects:** a vertex stage with no `CX_FEAT_SKIN` branch.

**Why:** a vertex stage predating skinning links perfectly and the missing bone
attributes are simply disabled — so a robe renders in its **bind pose** at the node
matrix, silently.

### Linear — do not encode

**Applies to:** every Alloy fragment stage except the resolve — `cx_own_fs`,
`cx_ownlit_fs`, `cx_water_fs`, `cx_grass_fs`, `cx_bloomdn_fs`, `cx_bloomup_fs`.

**Rejects:** any use of `CxRevert` or `CxSoftClip`.

**Why:** the Alloy frame is linear end to end and the resolve encodes it once. A stage
that encodes as well hands display-space values to a target that gets encoded a second
time. The whole frame washes out, and nothing says so.

**You see:** the rebuild line from the owning pass — `PostChain: the <what> override is
refused ...` for the bloom stages, `GrassField: grass override still encodes to display
space ...` for grass.

### Resolve — the same guard from the other end

**Applies to:** `cx_resolve_fs`, the one stage that must encode.

**Rejects:** a stage that calls `CxLinear` (decoding a target that is already linear —
the frame comes out a gamma dark), or one with no `CxEncode` at all (the frame stays
linear and washes out).

Tonemap and dither around those terms as you like. The colour-space decision is not
yours to change; the rest of the pipeline is built on it.

### Post stack stages — neither end of the guard

**Applies to:** `cx_postgam_fs`, `cx_toon_fs`, `cx_smaablend_fs`, `cx_taa_fs`, and every stage the display-space post stack grows (ADR 0020). The aa stage's two own passes (`cx_smaaedge_fs`, `cx_smaawgt_fs`) are plain fullscreen programs: no `uSrc` contract, but they must keep the sampler names the C# side binds units to.

**Rejects:** a stage that calls `CxEncode` (a second encode, a gamma too bright) or `CxLinear`
(the frame goes linear, a gamma dark), or one that dropped the `uSrc` sampler the stack binds the
previous output to.

The contract a stage keeps: declare `vec4 CxStage(vec4 c)` over the display-space input and hand
display space on; the shared main dithers after it. `uDepth` carries the frame's depth for a stage
that wants it. A stage whose override AND embedded text both fail is dropped from the stack with
`PostStack: the <name> stage did not build -- DROPPED`, and the frame resolves without it.

### Caster inertness

**Applies to:** `cx_cast_fs`.

**Rejects:** `discard` or a written `gl_FragDepth`.

**Why:** the caster pass renders into a depth-only FBO, so those two are its entire
silent-failure surface — a `discard` means the caster casts no shadow at all, and a
written `gl_FragDepth` displaces the depth the map is compared against into acne or
peter-panning. Neither errors, neither logs.

`cx_cast_vs` additionally must keep `uBone`: without the skinning branch every creature
casts its bind pose.

### Depth — the prepass expression

**Applies to:** `cx_own_vs` and `cx_ownlit_vs`. Water is exempt — it tessellates,
so its evaluation stage is what projects, and it does not take the prepass.

**Requires, character for character:**

```glsl
invariant gl_Position;
...
gl_Position = uVP * d.model * vec4(aPos, 1.0);
```

**Why:** the depth prepass writes these depths and the colour pass re-tests them with
`LEQUAL`. `invariant gl_Position` only guarantees a bit-identical result for the *same
sequence of operations* — `uVP * (d.model * pos)` is a different sequence, and a ULP of
drift the wrong way punches speckled holes in lit surfaces.

**This one does not rebuild.** An override that rewrites the expression is accepted and
the **depth prepass is turned off** for the session, which is correct but slower. You
see:

```
OwnedDrawService: a content vertex .shd does not carry `gl_Position = uVP * d.model *
vec4(aPos, 1.0);` under `invariant gl_Position;` -- no depth prepass
```

If you want the prepass back, keep the expression.

### Pass block — the shared scene terms

**Applies to:** `cx_pass`, the library every Alloy lighting stage includes for the `CxPass`
uniform block.

**Rejects:** a copy whose member list is not the embedded one, member for member and in
order — and, from the C# side, a block whose std140 offsets no longer match the ones
`AlloyRenderer`'s `Pb*` constants stage. The block is a byte layout filled once per pass, so a
member added, removed or moved in an override reads every term after it out of its
neighbour's bytes: a silent frame, not a link error. `ShaderContract.PassCheck` computes the
layout from the GLSL itself, so reformatting and comment changes are free and a reorder is not.

The block grows at the end, never in the middle. CX-188's budget work added the last three
lanes, and a copy taken before it is missing all three:

| lane | what it carries |
|---|---|
| `uShBudget` | `shadow.perpixel` — lights a PIXEL may shadow at all; the rest light it unshadowed. 0 = no budget, which is also the pre-budget ranking (`-f2`) in every walk that reads it. |
| `uLightFloor` | `light.floor` — the absolute display-linear brightness a light's reach ends at, as against `light.binreach`'s share of the authored radius. 0 = the authored radius. |
| `uFullModel` | `light.fullmodel` — the share of the pixel's brightest light above which a light keeps the near-field core, the source-sized lobe and the subsurface leak. 0 = every light keeps all three. |

A caller that fills the block itself (the lightmap bake) leaves all three zero, which is why
zero has to mean "as before" in each of them.

### Not guarded

`cx_post_vs`, `cx_grass_vs`, `cx_cluster_cs`, `cx_defshadow_cs`, `cx_vfog_cs` and `cx_vfogsum_cs` carry no guard.
`cx_deferred_cs` and `cx_pairdebt` carry one: they read a G-buffer pixel's draw by its slot -- the lightmap record
(`CxChart`), and for `cx_deferred_cs` the sheen in `CxDraw` too -- so each is held to those records' counts, and a
stale one stands its pass down for the session.

The first two read no per-draw record and write no colour; an interface break in them
fails the **link**, which the build-failure fallback already refuses with a log line.
`cx_cluster_cs` does read a C#-strided record, but as a bare `vec4 uLights[]` with the
stride open-coded on both sides rather than as a struct — the counting guard cannot reach
it, and a text match on the indexing arithmetic would forbid restyling. It is stated here
rather than guessed at: an override of the cluster build that gets the stride wrong will
fail silently.

Past that record, `cx_deferred_cs` is the same case from the other end: it reads `uLights` and the cluster
table through the shared `cx_lightfrag` library rather than restating either, so what an
override of it can get wrong is the lane decode (`cx_gbuf`, a library of its own) or the
arithmetic that turns the walk's result into the pixel. Neither is a text match away. The
guard that does exist is the knob: `light.deferred 0` puts the whole frame back on the
forward loop, which is the A/B to reach for when an override of this stage looks wrong.

`cx_pairdebt` is the same case again, and the same two A/Bs bound it: `lightmap.moverlights 0`
takes the pass out of the frame entirely (and with it the pair tiles the caster pass bakes),
and `light.deferred 0` puts the hot-light debt back inside the forward light loop, where it
is computed per fragment per light instead. An override that gets the subtraction wrong
shows as creature shadows that are too dark, too light or in the wrong place on baked ground
-- never as a missing draw.

`cx_vfog_cs` and `cx_vfogsum_cs` write only the fog volume, so an override that gets them wrong shows as wrong fog and
nothing else, and `vfog.enabled 0` is the A/B: the fog group's ramp draws the fog again.

`cx_defshadow_cs` is bounded by the same knob: an override that writes a wrong record shows as
point-light shadows in the wrong place on deferred pixels only, and `light.deferred 0` takes the
forward loop's own taps instead. One that fails to link stands `light.deferred` down for the session.

## Where this is enforced

The guard predicates live in `managed/Alloy/Core/ShaderContract.cs`
(`RecordSafe`, `CutoutSafe`, `SkinSafe`, `LinearSafe`, `ResolveSafe`, `DisplaySafe`, `DepthSafe`) and
`managed/Alloy/Pass/CasterPass.cs` (`CastVsSafe`, `CastFsSafe`). Each is asserted
in both directions by the SelfCheck suites — the embedded stages pass, and deliberately
broken text is refused — and `run-demo.sh` fails the build if any suite fails. The one
exception is Skin, whose refusing direction cannot be asserted there: it short-circuits
on an arming flag that is set after SelfCheck runs.

`managed/Alloy/Shaders/<resref>.glsl` is each stage's composition: a list of `#include`
lines naming the libraries it is built from, spliced by `ShaderSource` when the stage is
compiled or dumped. `managed/Alloy/Core/ShaderStages.cs` keeps the rest of the contract --
which resrefs are overridable, and the header each dumped file carries. The offline checker
`check-shaders.py` compiles those same files rather than restating them.
