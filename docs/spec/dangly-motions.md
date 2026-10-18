# Dangly motions

What moves a danglymesh node, and the contract between the pieces that do it: the rig built at load
(`DanglyRig`), the CPU solvers (`VanillaDangly`, `DanglyJoints`) and the scene-DB wiring (`DbAnim.Dangly`)
that draws a rigged part through the GPU skinning path and a vanilla one through its own vertex range. The design rests on a content census (9,689 danglymesh nodes over the base game and a
large persistent world's haks) that found four populations, each with its own authored motion. One
force law for all of them is what made the previous solve fail: a force in world units moves a 2 cm hair
lock and a 12 m ship rope by amounts 40x apart.

## 1. Principles

1. **The authored rest pose is the equilibrium.** No constant force may move it. Gravity acts only through
   a change of the part's orientation from the authored upright pose (zero for every tile and unrotated
   placeable); wind leans a part within its own authored range.
2. **Every number is in the asset's own units.** A node's natural frequency, damping, wind gain and
   excursion limit are derived from its own `constraints`, `displacement`, `tightness` and `period`, so the
   authored character of every asset is kept and only what vanilla gets wrong changes: vertices moving
   independently (jelly, stretch), wind that cannot gust, frame-rate dependence, normals that never follow,
   and no air drag from the owner's own motion.
3. **The motion is chosen per node from its data, then per object.** No resref or node names.

## 2. Vanilla, exactly (`VanillaDangly`)

The stock solve (`PartDanglyMesh::Animate`, nm 0x49bbb0), in prose. Per part per call:

- `dt` is clamped to `[0.0125, 0.035]`.
- The part's world position and rotation are read. A move of more than 1 unit since the previous call
  copies the rest positions over the dynamic ones (velocity is NOT cleared), stores the pose, and ends.
- A previous rotation whose x, y and z are all zero is seeded with the current pose.
- `wind20 = conj(rot) * GetWindVector(worldPos) * 20` (part-local).
- `d = conj(rot) * prevRot` (quaternion product) and `tLocal = conj(prevRot) * (prevPos - pos)`.
- Per vertex `i` with `c_i > 0` (a vertex with `c_i <= 0` is never written):
  `p = d * (dyn_i + tLocal)`, `v = d * vel_i`,
  `v += (rest_i - p) * c_i * 0.5 * tightness * dt - v * 1.5 * period * dt` (stored),
  `p += v * dt`, then `p += wind20 * displacement * (1 - c_i/255)` -- inside the stored state, so wind acts
  as a drift the spring and damping answer, not as a display offset.
  If any axis of `p - rest_i` is beyond `±1e10` the vertex is reset to rest; otherwise each axis is clamped
  to `rest_i ± displacement * (1 - c_i/255)` (velocity untouched).
- The pose is stored as the previous pose.

CX runs that step at a fixed `H = 1/60` s -- what a 60 fps machine shows -- through an accumulator (at most 4
steps a frame) and draws the state interpolated between the last two steps, so the look does not change
with frame rate. This is the fallback for every part the other motions decline, and the reference they are
graded against.

## 3. Per-node data (`DanglyRig`, built once per `NodeDef` at load, immutable, shared)

- **Mobility** `m_i = (c_i <= 0 || c_i >= 255) ? 0 : 1 - c_i/255`. Content uses constraint 0 as the anchor
  (two thirds of every vegetation card's vertices), so 0 and 255 both pin.
- **Moving set** = vertices with `m_i > 0`; `c̄` = their mean constraint; `cap_i = displacement * m_i`.
- **Frequency and damping** of vanilla's spring: `ω = sqrt(0.5 * c̄ * tightness)` rad/s,
  `ζ = 1.5 * period / (2ω)`. `c̄ * tightness` is floored at 0.5 (`ω >= 0.5` rad/s), so a node authored with
  no spring at all still returns to rest; `ω` is capped at 120 rad/s and `ζ` so that `2ζωh <= 0.5` at the
  joint solver's 1/120 s substep -- the stability ceilings of an explicit step.
- **Wind gain** `G = 3 * period * 20 / (H * c̄ * tightness)`, over the same floored product: vanilla's steady drift offset is
  `G * |w| * cap`, so `G` carries vanilla's wind scale into the new motions as a lean `tanh(G * |w|)` of the
  node's own range.
- **Class**: `corr` = Pearson correlation of `m_i` with node-local `z` over all vertices (0 when undefined).
  `corr > 0.5` -> **Sway** (free at the top: vegetation); else `ζ > 0.15` -> **Jiggle** (body parts,
  creature appendages); else **Swing** (cloth, rigging). No moving vertex -> **Static**. A Swing whose moving
  set sits on its own anchor plane has no length to chain down and is rigged as Jiggle. On the census this
  matches the name families 92% (vegetation), 97% (body parts), 82% (creatures), 83% (cloth), 93% (rigging).
- **Joints and skin weights** for Jiggle and Swing (section 5), and per-joint drag tensors.

## 4. Choosing the motion at run time

`motion = class`. A kind whose knob is off (`dangly.sway`, `dangly.swing`, `dangly.jiggle`),
`dangly.motion = vanilla` (labelled *stock*), a node with no rig, or a part the skinning path cannot take
(`renderer.ownskin` off at start-up, no skinned arena range, no bone range -- the last two counted and said
once) runs `VanillaDangly`. Static parts are drawn at rest and cost nothing, and with `dangly.enabled` off
every dangly part is drawn at rest.

## 4a. Drawing a rigged part

Every rigged motion is drawn as a skinned part: the rig's `SkinDef` gives the node's mesh the skinned
lanes (one shared range per node, since the weights depend only on the rest mesh), the part's recipe is
flagged skinned as well as dangly, and each frame the solver's bones are written, node space and
`Store()`-form, into a bone range of their own. The shader's bone base is an index into one flat palette,
so small rigs share a 64-bone slot. Normals, tangents, motion vectors (the previous palette) and every
depth, shadow and highlight stage then follow from the existing skinning path. A rigged part counts as a
moving shadow caster only on frames its bones moved (`NoteDanglyMoved`), not on every frame the way a
creature's skin does, and a settled part publishes nothing.

## 5. Joints (`DanglyJoints`: Jiggle and Swing share one solver)

**Rig.** Anchors = vertices with `m_i = 0`; when there are none, the lowest-mobility tenth. `P` = anchor
centroid, `C` = mobility-weighted centroid of the moving set, hang axis `u = normalize(C - P)`.

- **Jiggle**: one chain of one segment from `P` to `C`, no length constraint (the tip may stretch).
  Vertex weight on the segment bone `w_i = min(1, m_i / m90)` (`m90` = 90th-percentile mobility), the rest
  on the root (the part itself).
- **Swing**: the moving set's extent along `u` is the length `L`; across `u` (its principal axis) the width
  `W`. `K = clamp(round(2W / L), 1, 4)` columns, `J = clamp(round(L / 0.5 m), 2, 4)` segments each; joint
  `(k, j)` at the column's centre line, `j * L / J` down the axis from the anchor plane; joint 0 of every
  column is kinematic. A vertex's chain coordinate `s` (in segments) gives hat weights
  `max(0, 1 - |s - (j + 0.5)|)` over its two nearest segment bones, normalised over them (a vertex at a chain
  end belongs wholly to the end segment), blended across its two nearest columns by lateral distance, then
  mixed with the root by `smoothstep(0, m90, m_i)`; at most 4 influences, normalised.
- **Drag tensor** per joint: `N_j = Σ A_f n_f n_fᵀ / Σ A_f` over the faces whose centroid it owns
  (eigenvalues in [0, 1]: 1 = broadside, 0 = edge-on).

**Step.** World space, semi-implicit Euler, substeps of at most 1/120 s, the targets travelling from last
frame's pose to this one's across them (held still and jumped once a frame, a joint on a steadily moving
part would chase a staircase and never settle). Per moving joint `x` (velocity `v`, rest-pose world target
`T`, the target's velocity `vT`, cap `cap_j = displacement * mean m over the vertices it weighs most`):

    a = ω² (T - x) - 2ζω (v - vT)                               vanilla's spring and damping
      + ω² cap_j tanh(G |N_j w|) normalize(N_j w) * windgain     wind lean in the node's own range
      + g * tilt * (down_world - R_part down_model)              gravity relative to the authored pose
      - drag * N_j v                                             air drag from the owner's own motion

then two iterations of length constraints (Swing: parent-child at rest length, neighbouring columns at rest
spacing at half strength), then the cage `|x - T| <= cage * cap_j`. A part that moved more than 1 unit in a
frame resets to its rest pose. Bone matrices (node space, System.Numerics row-vector, mapping a rest vertex
to its current place): segment bone `(k, j)` = the shortest-arc rotation from the rest segment to the
current one, about the rest joint, translated onto the current joint; Jiggle adds the residual that lands
its tip exactly; Sway's is the pure translation of its joint.

**Settled.** When no moving joint is further than 1e-4 (node units) from where its bones were last
published, and none moves faster than 1e-3 relative to the part, the part publishes nothing (no palette
upload, the caster cache may keep it). Measured against the last publish, not last frame, so a slow creep
is still published once it adds up; measured in the part's own frame, so a cloak carried by a steadily
walking creature settles, and a part leaning still in a steady wind is settled. A publishing frame reports
its largest node-space joint move to the shadow-caster verdict, which `dangly.still` thresholds.

## 6. Sway

A one-joint rig like Jiggle, with the tip at the moving set's mobility-weighted centroid and a pure
translation for a bone: the moving vertices shift together by their weight, the shear vanilla's own
vegetation motion amounts to. The same step drives it, so a card leans with the wind in its own range,
overshoots and rings at its authored 1.2 Hz when a creature walks past, and costs one joint a frame.

## 7. Knobs (`dangly` group)

| key | kind | default | meaning |
|---|---|---|---|
| `dangly.enabled` | master | on | off: every dangly part is drawn at rest |
| `dangly.motion` | `auto` / `vanilla` (labelled automatic / stock) | `auto` | stock moves every part the stock way |
| `dangly.sway`, `dangly.swing`, `dangly.jiggle` | toggle, ChatOnly | on | off runs that kind the stock way |
| `dangly.windgain` | 0–3 · 1 | ×vanilla's wind response |
| `dangly.tilt` | 0–1 · 1 | how fully a tipped part falls toward the ground |
| `dangly.drag` | 0–3 · 1 | air drag from the owner's motion, 1/s |
| `dangly.breeze` | 0–2 · 0 | wind when the area sets none: a horizontal vector turning at 0.05 rad/s |
| `dangly.cage` | 0.5–4 · 1.5 | ×the authored excursion limit (new motions only) |
| `dangly.still` | as before | |

Every force knob of the per-vertex integrator this replaces is retired -- the gravity, wind, turbulence,
variation, body-proxy and solver-rate keys, and the worker-thread toggle that never dispatched anything.
`tools/gates/retired-terms.txt` names them, and refuses them anywhere under `managed/` or `docs/`.
