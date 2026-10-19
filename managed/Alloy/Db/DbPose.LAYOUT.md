# Pose buffer layouts — seam-anim produces, seam-feed uploads (CX-120)

Two things a pose cannot express as a matrix, and the exact shape in which seam-anim hands them over.

## 1. Bone palettes (skinned parts)

- **Where:** the renderer's own bone stage, `AlloyRenderer.DbBoneStage(slot)`. It has to be that block, because
  that is what `Issue.UploadBones` sends and what `feat.y` indexes. `feat.y` is an ABSOLUTE bone index into one
  flat palette (`cx_bone_v.glsl` reads it as exactly that), so a part need not own a whole slot.
- **Who claims a slot:** seam-anim, through `AlloyRenderer.DbAllocBone()` / `DbFreeBone(slot)`
  (`Scene/AlloyRenderer.DbBones.cs`). A DB slot carries owner `-2`, which both engine-path reclaimers skip by
  construction, so it is **sticky** for as long as the part holds it — which is what #308 needs, since the
  shadow bake pairs the previous build's `feat.y` with this frame's block.
- **Shape:** `64 * 16` floats per slot. Per bone, a **column-major affine**: rotation columns in `m[0..2]`,
  `m[4..6]`, `m[8..10]` and the translation in `m[12..14]`, `m[15] = 1`. An unused slot is the identity, never
  the previous part's matrices. (`SkinPose.Compose`'s two-transpose derivation is where this layout comes from;
  `DbAnim.SelfCheck`'s skin case pins it — a transposed store passes every diagonal test and fails that one.)
- **The handshake:** `SceneDb.PartBoneBase[p]` is the part's ABSOLUTE first bone, or -1 for a rigid part — a
  whole skin's is `slot * 64`, a rigged dangly part's is its cell's own index. seam-feed reads it and names it
  in the record (`AlloyRenderer.Db.cs` already does). seam-anim writes it at the end of the object's animate
  step, after the compose.
- **The previous-frame palette IS written.** `DbBonePublish` copies each live slot's stage into the frame's
  upload buffer and last frame's into the history one, so a DB part's motion vectors are its posed ones. A
  slot's first published frame is its own history (`_dbFresh`); a single CELL's first frame is seeded with
  `DbBoneSeedPrev`, because freshness per slot cannot speak for one of its tenants.

### Rigged dangly parts (bone CELLS)

A dangly node whose rig the builder accepted draws as a skinned part (`docs/spec/dangly-motions.md` section
4a), and its rig is a handful of bones rather than a creature's sixty-four:

- **The cell.** `DbAnim.DanglyBoneAlloc` cuts each claimed slot into power-of-two cells of 2 to 64 bones and
  hands one out; the part's base is `slot * 64 + offset`. Freed cells come back before a new slot is claimed,
  and a slot, once claimed, stays claimed — a DB bone slot is sticky by design.
- **What is published.** The joint solver's node-space bones, in `Store()` form, bone 0 the identity (the part
  itself). A settled part publishes nothing at all, so its history catches up to its own pose and its motion
  vector goes to zero.
- **The motion split.** One part is on exactly one path: a rigged part holds a bone cell and no dynamic vertex
  range, a stock one the reverse, and switching between them (a `dangly.motion` flip) hands the resources over
  in the same frame. A `Static` node holds neither and draws at rest.
- **Lifetime.** `DanglyBonesForget` releases a part range's cells on the same three edges the dynamic buffers
  go on (object retirement, a tree change, an LOD swap); a lost context asks for all of them back through
  `DanglyBonesContextLost`, which the game thread acts on at the end of its next dangly walk.

## 2. Dynamic vertex positions (dangly and animmesh)

**The feed needs no code for this.** A part whose vertices move gets its OWN arena range and its OWN interned
recipe, and `SceneDb.PartRecipe[part]` points at it — so the record feed draws the solved vertices through the
path it already has. The plumbing is `MeshArena.AcquireDynamic` / `WriteDynamic` / `DynUpload`
(`Db/MeshArena.Dyn.cs`, a new file in the parts stream's pattern; the only edits to `MeshArena.cs` itself are
the `partial` keyword and one `DynUpload()` call inside `Pump`, and to `DrawRecipe.cs` the `partial` keyword).
The patch reaches the GPU from `Pump`, on the render thread, inside the same bracket and under the same lock as
every other arena upload. The range's bound moves with the vertices, so a flapping cloak is not culled against
a pose it no longer holds.

The host-side buffer below is the producer's own copy, which the publish above uploads from.

- **Where:** seam-anim's own per-part arena: `3 * count` **model-space** positions per part with dynamic
  state, in the `MeshDef`'s vertex order.
- **Change:** a part whose vertices moved is marked `Dirty.Anim` on that frame, and only then.
- **Semantics:** the buffer **replaces** the mesh's rest positions for that part; everything else (normals,
  indices, tangents) stays the arena's. Vertex order and count are the `MeshDef`'s.
- **Lifetime:** the buffer belongs to the part record and is freed when the object retires or its tree is
  replaced. A consumer keeps the PART index, never the pointer.
- **Ceiling:** 2048 parts may hold one at once; past that a part draws its rest geometry, said once and counted
  on `[dbanim]`.
- **If nobody reads it:** seam-anim says so once, loudly ("dynamic vertex buffers are being produced but
  seam-feed has never read one") — a cloak that does not flap is a visible difference, not an error this side
  can fix.
- **UVs morph too.** An animmesh with a `animtverts` pool gets a second buffer (`2 * Count` floats) hanging
  off the position one — same vertex count, same arena range — and the publish patches the range's diffuse UV
  lane beside its positions. A part whose node has UV frames and NO position frames still gets both buffers:
  the positions are then the rest ones and only the UVs move, which is the load screen's smoke. Counted as
  `uv morph(s)` on `[dbanim]`.
