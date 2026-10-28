# CX-117: textures and PLT -- the API surface, and the engine machinery it replaces

Status: the registry, the PLT decode, the master palette and the shader resolve are in and are what a record
draws from. The engine-side texture machinery they replace is still in the tree; section 3 is the list of it.
This file is the contract: what the seam calls, and what is owed.

## 1. What CX owns now

| Concern | The engine's own machinery | CX's (CX-117) |
|---|---|---|
| which textures exist | `CAurTexture` / `CAurTextureBasic`, one per area, freed at the transition | `TexRegistry`, keyed on (resref, kind, cell) under a generation-checked `TexId`, refcounted, never freed by an area edge |
| a texture's identity | `AurTextureCalculateHashID` (anagram-blind) + a `strcasecmp` | 64-bit FNV-1a over the lower-cased resref + kind, cell in the low 16 bits |
| who holds a pointer | every material, every Alloy record (`_recAur`), borrowed (ADR 0015) | nobody: a slot is a resref, a GL name and a bindless handle |
| arrival | the staging walk re-resolves the pointer and the tid every frame | arrival marks the dependent part records `Dirty.Texture` off a dependents index |
| a PLT | composited per creature on CPU (`ImageDecode`) or by rendering (`fs_pltgen`), one texture per colour set | one RG8 upload per PLT file, resolved per instance in `cx_plt.glsl` |
| PLT colours | ten palette lookups per texel, re-run on every change | ten `ushort` rows on the object record; a change is one write plus `Dirty.Material` |

## 2. The game-facing functions the seam routes here

Every one of these is a **name export on Windows**, so each needs an `Nm` row and no RVA anchoring
(`msvc-mangling-is-the-oracle`). CX-117 provides the plain C# implementation; the CX-120 seam-obj /
seam-feed streams install the hooks and the vtable slots.

| Engine entry point | Linux mangled | Windows export | CX-117 implementation |
|---|---|---|---|
| `AurSetPalette(int, char const*)` @0x485190 | `_Z13AurSetPaletteiPKc` | `?AurSetPalette@@YAHHPEBD@Z` | `PltPalettes.SetPalette(slot, resref)` |
| `AurClearPalette()` @0x485090 | `_Z15AurClearPalettev` | `?AurClearPalette@@YAXXZ` | `PltPalettes.ContextLost()` (drops the texture; the buffer is rebuilt on the next `SetPalette`) |
| `AurCreatePaletteTexture()` @0x4874d0 | `_Z23AurCreatePaletteTexturev` | `?AurCreatePaletteTexture@@YAXXZ` | `PltPalettes.Create()` -- **must run in Alloy's bracket**, so the seam defers it to the frame rather than calling it inline |
| `CAurTextureBasic::SetScheme(int*, int)` @0x485f90 | `_ZN16CAurTextureBasic9SetSchemeEPii` | `?SetScheme@CAurTextureBasic@@QEAAXPEAHH@Z` | `PltColors.SetRows(obj, rows)` |
| `AurTextureSetLayered(CAurTexture*, char const*, int*, int)` @0x489bd0 | `_Z20AurTextureSetLayeredP11CAurTexturePKcPii` | `?AurTextureSetLayered@@YAXPEAVCAurTexture@@PEBDPEAHH@Z` | `PltColors.SetRows(obj, rows)` + `TexRegistry.Swap(old, resref)` |
| `Material::ReplaceTexturePLT(char const*, char const*, char const*, int*, int)` @0x4c7a10 | `_ZN8Material17ReplaceTexturePLTEPKcS1_S1_Pii` | `?ReplaceTexturePLT@Material@@QEAAXPEBD00PEAHH@Z` | `TexRegistry.Swap` + `PltColors.SetRows` |
| `Gob::ReplaceTexture(char const*, char const*)` @0x43e980 (vtbl slot 12) | `_ZN3Gob14ReplaceTextureEPKcS1_` | `?ReplaceTexture@Gob@@UEAAHPEBD0@Z` | `TexRegistry.Swap` on the object's parts |
| `Gob::ReplaceTexture(char const*, char const*, int, unsigned short*, int)` @0x43eb30 (vtbl slot 13; Win slot 11) | `_ZN3Gob14ReplaceTextureEPKcS1_iPti` | `?ReplaceTexture@Gob@@UEAAHPEBD0HPEAGH@Z` | `TexRegistry.Swap` + `PltColors.SetRows` (widen the `ushort*` as the engine does) |
| `Gob::ReplaceTextureSubtree(char const*, char const*, int, unsigned short*)` @0x43ed40 (slot 14; Win 13) | `_ZN3Gob21ReplaceTextureSubtreeEPKcS1_iPt` | `?ReplaceTextureSubtree@Gob@@UEAAHPEBD0HPEAG@Z` | the same, over the part subtree |
| `Gob::RestoreTexture()` @0x43b510 (slot 15; Win 14) | `_ZN3Gob14RestoreTextureEv` | `?RestoreTexture@Gob@@UEAAHXZ` | re-`Acquire` each part's recipe texture (`DrawRecipe.TexName`) and `Release` the override |
| `Gob::SetTextureAttributes(char const*, char const*)` @0x43f9d0 (slot 17; Win 16) | `_ZN3Gob20SetTextureAttributesEPKcS1_` | `?SetTextureAttributes@Gob@@UEAAHPEBD0@Z` | not implemented: the `.txi` attributes it sets are the registry's own sampler state. Deliberately DB-mode-only-later; log once and refuse. |
| `Scene::ReplaceTileTexture(Vector const&, char const*, char const*)` @0x45f090 (slot 10; Win 9) | `_ZN5Scene18ReplaceTileTextureERK6VectorPKcS4_` | `?ReplaceTileTexture@Scene@@UEAAXAEBVVector@@PEBD1@Z` | `TexRegistry.Swap` over the tile's parts (owner: the tiles stream, CX-116) |
| `AurTextureRefreshAll()` @0x48f050 | `_Z20AurTextureRefreshAllv` | `?AurTextureRefreshAll@@YAXXZ` | no-op: a registry slot is content-keyed and never stale |
| `AurTextureSuspendUpdates()` @0x486af0 / `AurTextureResumeUpdates()` @0x486b00 | `_Z24AurTextureSuspendUpdatesv` / `_Z23AurTextureResumeUpdatesv` | `?AurTextureSuspendUpdates@@YAXXZ` / `?AurTextureResumeUpdates@@YAXXZ` | no-op: the registry has no update list to suspend |
| `AurResetTexture(char const*)` @0x48fa90 | `_Z15AurResetTexturePKc` | `?AurResetTexture@@YAXPEBD@Z` | no-op (the engine's rebuild; a registry slot is immutable, a change is a new slot) |

### Registry API the renderer and the seam use

    TexId  TexRegistry.Acquire(resref, kind = Auto, cell = 0)   // +1 reference, loads on first ask
    void   TexRegistry.Release(TexId)                           // last one leaves it Idle (ADR 0023)
    TexId  TexRegistry.Swap(old, resref, kind = Auto, cell = 0)  // ReplaceTexture / a cycle step
    ulong  TexRegistry.Handle(TexId)                            // resident bindless handle, 0 = stand-in
    bool   TexRegistry.Resident(TexId) / TexState StateOf / TexKind KindOf / string ResrefOf
    void   TexRegistry.AddDependent(TexId, int partIndex)       // tell me when it lands
    bool   TexRegistry.TryResident(TexId, out name, out handle)  // for a consumer that binds by NAME too
    bool   TexRegistry.Clamps(TexId)                            // the sampler's wrap, for the PLT flag word
    TexCoverage TexRegistry.Coverage(TexId)                     // Unknown | Opaque | Cutout | Blended
    void   TexRegistry.Tick()                                   // GAME thread: file arrivals, offer, evict
    void   TexRegistry.Drain()                                  // BRACKET: upload, mint, delete
    void   PltColors.SetRow(obj, layer, row) / SetRows(obj, rows)
    void   PltColors.Stage(slot, obj) / Clear(slot) / Upload()

`TexKind.Auto` is the default because a model's `bitmap` line is only a resref: the locate step tries DDS,
then TGA, then PLT, which is the order the engine's own loader and `ReplaceTexturePLT`'s
`CExoResMan::Exists(resref, 6)` gate reach between them, and the slot takes the kind it found.

## 3. Engine texture machinery the registry replaces

Still in the tree, and owed a deletion. Each line is gated by nothing -- it goes rather than being branched
on, because a renderer that keeps both warm is what ADR 0018's restart-only rule exists to prevent.

**PLT, which the shader resolve replaces outright:**
- `OwnedTextures.KeepPltOnCpu()` and the `_pltOnGpu` / `_pltForced` state
  (`managed/Alloy/OwnedTextures.Plt.cs`), plus its two call sites in `OnConstructImage` / `OnGlImageBasic` and the
  per-frame re-assert in `AlloyRenderer.cs` (the `OwnedTextures.KeepPltOnCpu(); OwnedTextures.Drain();`
  bracket hook). `SymbolMap.PltOnGpu` goes with them.
- `FrameZones.Zone.DemPlt` and `Ev.PltDemands`: in DB mode no PLT is ever demanded through the engine, so
  `DemPlt` reads 0 on `[zones]` after a PLT's first load. (The counters stay until the engine demand path
  goes; they are the acceptance read.)
- `RasterReader`'s PLT half -- `OnDecode` / `Decode` / `DecodePlt` / `PltStrays` / `PltRowBytes` /
  `PltMaxLayers` and the `_pltOurs` / `_pltAgreed` / `_pltMismatched` / `_pltEngine` / `_pltUnchecked`
  counters. `PltImage` + `cx_plt.glsl` replace them. **Keep `DecodePlt` until then**: `PltVerify` and
  `TexRegistry.SelfCheck` compare against it as the engine's own arithmetic.
- `PltVerify` itself and the `alloy.pltverify` knob: it reads the engine's composite, so it has no subject in
  DB mode.

**Texture identity and the per-frame re-resolve, which the registry replaces:**
- `AlloyRenderer.Table.cs`'s `_recTid` / `_recTidS` / `_recSlotMask` columns and `AlloyRenderer.Batches.cs`'s
  texture block around `StagePart`. `DrawRecipe.TexHandle` + the dependents index are the replacement, and
  this block is what the ticket's second perf bullet names. (`_recAur` / `_recAurS` / `_recCtl` /
  `_recSlotQueued` / `_recArrive` are already gone.)
- `OwnedTextures`' borrowed-pointer verdicts: `_borrowed`, `NoteBorrowedName`, `IsOwned`, `TryDraw`/`Draw`,
  and the `AurTex` vtable guard on every material-derived texture read (ADR 0015). A `TexId` cannot dangle.
- `OwnedTextures`' warm path (`Plain`, `Capture`, `TryWarm`, `_meta`, `_index`, `OnDeleteTextures`,
  `OnBuildAndStoreTexture`) and ADR 0023's hand-back: the registry has no engine delete funnel to survive.
- `TexLoad`, `TexStream` and the `alloy.texsingle`
  knob -- the registry's own loader is the only reader path left. `TexLoadRead` (the location record and the
  worker's file handles) STAYS: the registry uses it.
- `CycleTextures` (the `CycleTIDTextureController` tick and its `~CAurTexture` death notice): **done.**
  `TexRegistry.Cycle` keeps the decoded grid and re-uploads one cell per slot on the registry's own clock, so
  the handle never moves; the table, the vtable probe and the death notice are gone. The `.txi` cell count
  comes from the parser.
- `TexScan` stays: the caster hole fraction and the fog mean are reads of a decoded image, and the registry
  has the decoded image. Its CAurTexture virtuals are what change.

## 4. Deliberate differences from vanilla

1. **PLT mips are built on the pairs, not the composite.** Vanilla mips the finished RGBA; Alloy mips
   (intensity, layer) with a majority-layer / mean-intensity reduction and resolves at the sampled level. A
   layer boundary therefore stays crisp at distance instead of blending two palettes' colours together, and
   the blend across a boundary happens in the shader's four taps at full resolution instead. A 2-2 tie goes
   by the destination texel's `(x ^ y) & 1` parity, not by the layer index: every boundary 2x2 is a tie, so
   taking the lower index won half of each boundary and lost none and the low-numbered layer grew one texel
   per level -- `pmh0_chest004`'s metal walked from 63% of the texels to 79% by level 5, a trim becoming a
   surface as the creature walks away. Alternating leaves each layer its share.
2. **The master palette's unused rows are black, not uninitialised.** `AurSetPalette` copies a 176-row
   palette into a 256-row slot with a flat `memcpy`, so rows 176..255 of every slot are whatever
   `operator new[]` left; a colour index past 175 samples that. Alloy zeroes them, and `PltColors` clamps a
   row to the master palette and counts it.
3. **Ten layers, not fifteen.** `CNWCAnimBaseParts::ReplaceTexture` @0x9d8c80 takes the PLT branch only at a
   count of exactly 10, so ten is what the game layer can ask for; `tools/pltcheck` confirms no shipped PLT
   pixel names a layer past 9 (six files declare 11 layers, none uses the eleventh). A layer past 9 resolves
   against layer 9's row and is counted, where the engine reads stale stack.
4. **The palette is fetched exactly, not filtered.** Alloy reproduces the **CPU** composite
   (`ImageDecode` @0x4b6580), which is an exact 32-bit copy of one palette texel -- and the CPU composite is
   what the engine path produces too, since `OwnedTextures` holds the GPU-PLT flag at zero. The engine's two *other*
   producers of a palette row normalise it differently and neither is reproduced:
   `glImageBasic`'s GPU branch uploads `(scheme[i] + 0.5f) / (float)paletteheight` and samples
   `fs_pltgen`'s `texUnitPalette` with LINEAR filtering (disassembled at 0x48c480-0x48c4bd: `movups` 1.0f
   from .rodata 0x12ee9e4, `divss` by `(float)paletteheight`, then per entry `cvtsi2ss`, `addss` 0.5f from
   0x12eedac, `mulss`), while `Material::ReplaceTexturePLT` @0x4c7a10 stores `scheme[i] / 1792.0f` with **no**
   half-texel offset and a hard-coded divisor (0x4c7b32 `movss`, 0x4c7b4c `cvtsi2ss`, 0x4c7b52 `divss`,
   constant 1792.0f at 0x12f3d74). The two disagree with each other; the CPU path is the one the default
   renders and the one Alloy matches texel for texel.
5. **A PLT's wrap mode is stated, not assumed.** `cx_plt.glsl` does its own addressing, so it cannot see a
   sampler's wrap mode; the registry puts it in the per-record flag word (`PltColors.FlagClamp`). Every slot
   is REPEAT today because a `.txi clamp` is a model-side attribute the CX-112 parser will carry -- honouring
   it is one line in `TexRegistry.Upload` and nothing in the shader.
6. **A PLT caster mask is opaque.** The caster pass reads a diffuse's alpha for its cutout; an RG8 pair
   texture has no alpha to read, so a PLT part casts its full silhouette. The engine-mode path marked PLT
   composites opaque by hand for the same reason.
7. **A texture lives for the session, not the area.** ADR 0023's decision without the engine's delete funnel:
   a slot with no references goes Idle and is evicted oldest-first off an LRU, only past `texcache_mb`.
8. **A second mip level is never blended past LOD 4** (`CX_PLT_LOD_CUT`): four taps instead of eight where the
   levels are a few texels wide and their colours are within a rounding of each other. No dither, no temporal
   term, so it holds with TAA off.

## 5. Where the registry's GL work runs, and why in two places

`TexRegistry.Drain`, `PltColors.Upload` and `PltPalettes.Create` are called from
**both** Alloy bracket sites, and that is not redundancy: `GlContextService.OnFrame` runs its health bracket
(`InBracket`) only on a frame in which nothing else entered the context (`!_worked`), so an in-play frame --
every frame the colour pass draws -- never reaches it. Hung on the health bracket alone, the registry uploaded
nothing for a whole live session: the PLT GPU check waited for a texture that never arrived, and CX-119's
bitmap font atlases would have waited for ever too. The health bracket still matters for the opposite case
(the main menu, the disarm latch), where the colour bracket returns before `Enter` for the rest of the session.

`TexRegistry.Tick` is the game thread's half and runs from `FramePhase.Start`: it files what the bracket
uploaded (which writes `SceneDb`'s dirty lists), offers what was waiting (which reads the engine's resource
manager) and evicts past the budget. Nothing in the bracket reads engine memory or writes `SceneDb`.

## 6. Alpha coverage is a decode, and the registry is where it is decided

`TexRegistry.Coverage(TexId)` answers `Unknown | Opaque | Cutout | Blended` for CX-120's draw
classification, derived from the DECODED pixels on the `cx-tex` worker and from nothing else. It has to be a
decode: `texture-coverage-is-a-decode-question` records that the engine's `Material::GetIsOpaque` @0x4c6190
and `PartTriMesh::GetIsOpaque` @0x43cee0 memoise verdicts nobody checked a texel for, and the GL internal
format is no better — `tcm02_leaf01` is DXT1 with `TEXTURE_ALPHA_SIZE=0` and 48% punchthrough blocks of which
5.2% of texels are actually transparent, because encoders emit that mode for opaque black too.

- **TGA**: the alpha channel's histogram; 1- and 3-byte rasters have no alpha to read and are `Opaque`.
- **BC2 / BC3 / BC7**: level 0 is decoded through `BcnDecode.Unpack` and the alpha histogrammed.
- **BC1**: `Opaque`, always. Alloy uploads it as `COMPRESSED_RGB_S3TC_DXT1` and `BcnDecode.PixelBytes` gives
  it three bytes per pixel for the same reason: there is no alpha in the sampler's answer, and measured on
  this driver no DXT1-RGB texture returns alpha below 1.0. Scanning its punchthrough blocks would produce a
  verdict about pixels nothing ever samples.
- **BC4 / BC5 / BC6**: no alpha channel, so `Opaque`.
- **PLT**: `Opaque`. A PLT has no alpha of its own — the composite's alpha is the palette texel's, and that
  channel is the engine's REFLECTIVITY mask (#121: `fEnvMapLevel = 1 - alpha`), not coverage: along
  `pal_armor01`'s row 44 it ramps DOWN from 219 to 40 as the intensity rises, so read as opacity every
  highlight becomes a hole. Measured on the stock male body with its own scheme, 33% of `pmh0_chest004`'s
  texels and 62% of `pmh0_legr015`'s resolve at or under the 0.2 discard — which is what `Cutout` punched
  out of DB-mode armour, leaving the dark green-tinted low-intensity texels behind. The engine does not test
  it either: `Material::GetIsOpaque` @0x4c6190 answers with the material's `HasEnvMap` flag and never looks
  at the texture, and a creature part is env-mapped (the appearance's ENVMAP column, `CreateBodyParts`
  @0x734440), so vanilla draws it in bucket 0 with the discard off.

The tolerance is 2 and 253 rather than exact 0 and 255, because a BCn alpha block's endpoint interpolation
lands a nominal 255 on 254. Erring that way alpha-tests a near-opaque texel as fully opaque, which no eye
resolves; erring the other way calls a plain cutout blended and sorts the whole creature.

`Unknown` means the decode has not run (or could not answer): the caller keeps its own conservative guess and
is told again at arrival, because the landing marks every dependent part `Dirty.Texture` through the same
edge that replaces the white stand-in — no new mechanism. A resident slot still `Unknown` is counted on the
period line (`coverage UNDECIDED`), because a draw classified against one keeps the blended guess for ever.
The verdict is kept across a context loss: it is a property of the content, and the reload derives the same
answer.
