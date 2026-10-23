# ADR 0026: The modern material model, and the .mtr's Alloy view

Status: accepted (2026-09-23). Author-facing reference: [docs/spec/materials.md](../spec/materials.md).

## Context

Alloy's lit stage reproduced the engine's shading from #120 through #122, on purpose: GGX with the
engine's roughness convention (alpha = rough, not rough²), Schlick-Smith shadowing with k = alpha, a
scalar Fresnel tinted by a synthesised specular colour, `(1 - fSpecularity)` as the whole energy
split, metallicness that only tints the highlight (stock never applies it to the diffuse, verified in
`base_shaders.bif`), a flat ambient with the engine's 0.15 sun-facing wrap, and a probe term that
tints the light already there by a bare F0. That was right for the goal it served: every roughness
in the content was tuned against those formulas, and a textbook remap had already made SoW's stone
ten times glossier once (#121).

The goal has moved. Alloy is meant to **improve** on the vanilla look while keeping the artist's
intent, and to open up materials the engine cannot express. The hard constraint is that **one `.mtr`
serves both clients**: a hak goes to vanilla players and Alloy players alike, so nothing may change
what a stock client does with the file.

Two facts make that possible (Ghidra, `SharedMaterial::ParseField` @0x4c6760 and
`GLRender::GetCustomParameterToken` @0x4cc300):

- A `parameter` line with a name no shader declares is parsed, stored, and at load fails to link to a
  uniform: one `Failed to link material custom parameter` log line per program, then it is skipped
  on every bind by a null-token test. No GL call, no per-frame cost, no error. On a material with no
  custom shader it is never looked up at all.
- `texture11` and up are never parsed by the engine (#275); Alloy already owns `texture11` as the AO
  map.

## Decision

**A material has two views. The stock parameters are its vanilla view, and a `Cx`-prefixed parameter
vocabulary, glTF's metal/rough model with three of its extensions, is its Alloy view. The lit stage
shades through a modern BRDF that reads the stock view exactly as the engine means it and the Alloy
view where the content declares one.** One knob, `light.brdf`, switches the whole model; off is the
pre-ADR stage bit for bit.

### The shading model (every material, `light.brdf` on)

- **Roughness keeps the engine's meaning.** alpha = `Roughness`, as before. Every roughness inside the
  lit stage stays in engine units; glTF's perceptual roughness is squared into them once, at the
  material. The GGX distribution is unchanged, so a legacy highlight keeps its shape and size.
- **Shadowing: Karis' k = alpha / 2** in place of the engine's k = alpha. Still separable into a light
  half and a view half, so the per-light / once-per-fragment split survives; identical for glossy
  lobes, brighter and more correct for rough ones at grazing angles.
- **Multiple scattering** (Fdez-Agüera 2019) on every specular term, so rough metal stops reading dark.
- **Specular anti-aliasing** (Kaplanyan & Hoffman / Tokuyoshi) widens alpha by the screen-space
  variance of the geometric normal (`light.specaa`), taken ahead of the cutout discard.
- **The probe term weighs by the split-sum DFG fit** (Karis' analytic form, no LUT, achromatic bias)
  times Lagarde's specular occlusion, in place of the bare F0, clamped to 1. It keeps the engine's *tint* form: the probe still
  scales the light already arriving, which is what keeps cobblestone from reflecting the sky (#121).
- **Directional ambient** (`light.ambientdir`, default 0.5): the probe's own shape toward N against
  -N, `2a / (a + b)`, gives the area ambient a direction at its authored level. The engine's 0.15
  stays the floor. Off during the capture itself, and where no probe exists.

- **Legacy metal reflects.** A texel the engine itself derives as metal (`clamp(3s - 0.6)` off its spec
  map, or a stated `Metallicness`) reflects the probe weighted by that metalness, env map or not. The
  #121 gate still keeps everything else out: matte stone sits at s <= 0.2, metalness 0. This is what
  makes spec/rough content, the workflow NWN artists actually use, read as real metal with no asset
  work; the Alloy view below is an option beside it, not the path to it.

### The Alloy view (content opts in, per material)

| parameter | glTF source | meaning |
|---|---|---|
| `CxMetallic` | `metallicFactor` | physical metalness: the diffuse goes, F0 becomes the base colour |
| `CxRoughness` | `roughnessFactor` | perceptual roughness (squared into alpha) |
| `CxOrm` | `occlusionRoughnessMetallic` | `texture11` is packed R occlusion / G roughness / B metal |
| `CxClearcoat`, `CxClearcoatRoughness` | `KHR_materials_clearcoat` | a second, white, glossy lobe over the base |
| `CxSheenColor`, `CxSheenRoughness` | `KHR_materials_sheen` | Charlie-distribution rim for cloth |
| `CxEmissiveStrength` | `KHR_materials_emissive_strength` | scales the illumination map into HDR |

A material that declares `CxMetallic`, `CxRoughness` or a bound `CxOrm` map is **physical**: its F0 is
`mix(0.04, base, metal)`, its diffuse share is `(1 - metal)` times the dielectric's own `(1 - F0)`, and it reflects the probe whether or not
it names an env map (a metal that reflects nothing is black). The translation runs *before* a hosted
content body (#350), so a weather ground or any hosted surface edits it in the engine's units like
any other material. The Alloy view overrides the stock view in Alloy; the stock view is what vanilla
renders. Values are clamped at harvest.

## Consequences

- `CxDraw` grows by three vec4s after `prev` (240 → 288 bytes). No older field moved. A hak-shipped
  override (ADR 0024) with the old record fails `RecordSafe` and falls back to the embedded stage
  with a log line: re-dump after this update.
- Authors can ship a glTF-exported ORM texture as `texture11` with `parameter int CxOrm 1`. An AO-only
  map that is not ORM-packed keeps working unchanged: without the flag only R is read.
- A vanilla client that loads a material using the Alloy view prints one "failed to link" line per
  Cx parameter per custom-shader program at load. It is a log line and nothing else. Parameter names
  must stay under 64 characters (the engine's `%s` into a 100-byte record); every Cx name does.
- The sheen takes no ambient term, and the coat and sheen do not reduce the base's energy by their
  own albedo beyond the coat's Fresnel transmission. Both are `ponytail:` ceilings: a sheen DFG table
  and a directional-albedo scale are the upgrade path if cloth or lacquer reads wrong.
- Water, grass, particles and weather keep the engine lobe: they never upload `uMaterial` and read it
  as 0. The shared light loop takes the lit stage's material lobe only through `CX_LIT_MATERIAL`.
- Off-device coverage: `check-shaders.py` folds the glTF translation, the DFG fit, multiscatter and
  specular occlusion against an independent transcription; `MaterialExtSelfCheck` runs a forged
  parameter list through the harvest. What is not covered offline is the look. It needs a live A/B
  (`/light brdf 0`/`1`).

### Amendment (#97, 2026-10-23): classic materials keep the engine's grazing

"The shading model (every material)" no longer holds for the grazing terms. A material that is not
**physical** (no `CxMetallic`, `CxRoughness` or `CxOrm`) takes the engine's three grazing forms even with
`light.brdf` on: Schlick-Smith k = alpha, a highlight that keeps its specular colour (the lobe's scalar
`mix(spec, 1, F)` rise, no per-channel whitening), and a probe weight of
`specColor * mix(mix(spec, 1, F(N.V)), spec, sqrt(rough))` -- `inc_lighting.shd`'s `fEnvironmentSpecularity`,
in place of the split-sum fit. Physical materials keep Karis' k, the whitening and the DFG weight.

Why: a live A/B on a server's NWN-made content (cloth and skin with grey `_s` maps) read the modern
grazing as a glossy sheen on every edge, 1.6x the stock highlight at N.V 0.1 from k alone, and preferred
no rise at all over it. The content was tuned against the stock grazing; Karis' k and the DFG fit are
the right model for content authored against a PBR reference, which the Alloy view is how content says.

`light.grazing` (on by default) is the dial: off holds every classic material at its head-on
specularity. The deferred walk carries the choice as `CX_GB_STOCKLOBE`. The rest of this ADR -- roughness
units, multiscatter, specular AA, specular occlusion, directional ambient, legacy metal -- is unchanged.
