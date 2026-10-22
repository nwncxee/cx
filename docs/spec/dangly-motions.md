# Dangly motions

What moves a danglymesh node, and the contract between the pieces that do it: the rig built at load
(`DanglyRig`), the CPU solvers (`VanillaDangly`, `DanglyJoints`) and the scene-DB wiring (`DbAnim.Dangly`)
that draws a rigged part through the GPU skinning path and a vanilla one through its own vertex range. The design rests on a content census (9,689 danglymesh nodes over the base game and a
large persistent world's haks) that found four populations, each with its own authored motion. One
force law for all of them is what made the previous solve fail: a force in world units moves a 2 cm hair
lock and a 12 m ship rope by amounts 40x apart.

## 1. Principles

1. **The authored rest pose is the equilibrium in still air.** Gravity acts on a plant or a body part only
   through a change of the part's orientation from its authored upright pose; a chain hangs under it along
   the line it was authored on, so it swings as a pendulum about that line and still rests on it.
2. **The authored numbers say what kind of thing a node is, not how far it may move.** Vanilla's
   `displacement` is a clamp sized for a solver whose vertices move independently: a banner authored at
   0.15 m on a 1.9 m drop could swing 7 degrees. A rigged mesh moves as one, so cloth and tall plants are
   bounded by angles instead, and only what would push through a body (hair, body parts, short plants)
   keeps its authored range. The spring frequency and damping are still the node's own.
3. **The air is real.** Wind is a velocity in metres per second with gust fronts the wind carries across
   the area, and it pushes a part as moving air does: by the square of the speed across its faces, the
   faces turning with the part. The same law drags a moving character's cloth and hair back.
4. **The motion is chosen per node from its data, then per object.** No resref or node names.

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
- **Class**: `corr` = Pearson correlation of `m_i` with node-local `z` over all vertices (0 when undefined).
  `corr > 0.5` -> **Sway** (free at the top: vegetation); else `ζ > 0.15` -> **Jiggle** (body parts,
  creature appendages); else **Swing** (cloth, rigging). No moving vertex -> **Static**. A Swing whose moving
  set sits on its own anchor plane has no length to chain down and is rigged as Jiggle. On the census this
  matches the name families 92% (vegetation), 97% (body parts), 82% (creatures), 83% (cloth), 93% (rigging).
- **Joints and skin weights** for Jiggle and Swing (section 5), and per-joint drag tensors.
- **Bone shape and limits.** Jiggle, and a Sway whose `|C - P|` is under 0.25, translate and keep the cage
  `cage * cap_j`. A taller Sway bends about `P` within 40 degrees; a Swing segment turns within 80. A part a
  creature wears (its object, or one up its attach chain, is a creature) keeps the cage on top of its cone: clothing
  authors a few centimetres of give, and a free pendulum on a walking body throws it half a metre.
- **Air response** `K` per metre: cloth 1 (times `min(1, W / 0.5)`: a narrower strip is cord, heavy for its
  area), foliage 0.4, a body part 0.1.

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

## 5. Joints (`DanglyJoints`: one solver for Sway, Jiggle and Swing)

**Rig.** Anchors = vertices with `m_i = 0`; when there are none, the lowest-mobility tenth. `P` = anchor
centroid, `C` = mobility-weighted centroid of the moving set, hang axis `u = normalize(C - P)`.

- **Jiggle**: one segment from `P` to `C`, no length constraint, its bone a pure translation as Sway's is:
  vanilla only ever translates a vertex, and turning the moving set about `P` amplifies a tip offset by
  `|C - P|`, which on a head (whose hair centroid sits a few cm from its anchors') reads as snapping.
  Vertex weight on the segment bone `w_i = min(1, m_i / m90)` (`m90` = 90th-percentile mobility), the rest
  on the root (the part itself).
- **Swing**: the moving set's extent along `u` is the length `L`; across `u` (its principal axis) the width
  `W`. `K = clamp(round(2W / L), 1, 4)` columns, `J = clamp(round(L / 0.35 m), 2, 6)` segments each; joint
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
`T`, the target's velocity `vT`, air velocity `w` at `T`):

    a = ω² (T - x)                       Sway and Jiggle only: the node's own spring
      - 2ζω (v - vT)                     the node's own damping
      - 2 (v - v_parent)                 Swing only: the chain's own friction, per second
      + g * tilt * (down_world - R_part down_model)      gravity relative to the authored pose
      + g * R_part u                     Swing only: the chain hangs along its authored line
      + K * windgain * |N'_j (w - v)| N'_j (w - v)        moving air on the faces

`N'_j` is the joint's drag tensor turned with its segment (the shortest arc from the rest segment to the
current one), so a banner that lifts edge-on to the wind catches less of it and settles where
`K U² cos² θ = g sin θ`. The chain's friction is what the top-down sweep needs: moving only the child, it
pumps energy into the chain's sideways modes, and an edge-on strip with no air across it flails; damping
each joint against its parent drains those modes and hardly touches the whole chain's swing. After each substep: Swing's constraints (every segment turned back inside its cone
and set to its rest length, one top-down sweep; neighbouring columns to their rest spacing at half
strength; the walls of section 5b; two iterations), a bending Sway's cone, or the cage -- and the cage after
either limit on a worn part; then each joint's velocity is taken from
where they left it, so a joint on a limit stops instead of storing the motion it was refused. A part that
moved more than 1 unit in a frame resets to its rest pose. Bone matrices (node space, System.Numerics
row-vector, mapping a rest vertex to its current place): a turning bone is the shortest-arc rotation from
the rest segment to the current one about the rest root, translated onto the current root; a translating
one is the pure translation of its joint.

## 5a. The air (`DanglyAir`)

The mean wind is the area's base wind at 3.5 m/s per unit (an area set to weak wind blows 1, strong 2,
along the engine's fixed diagonal), raised to `dangly.breeze` units outdoors; a breeze with no area
direction turns slowly (0.05 rad/s). Gust fronts are three sines of the distance down the wind *less
the distance the wind has carried them*, 30, 13 and 6 m apart, swinging the speed by ±60%, so a gust
crosses a field as a wave at the wind's own speed. Turbulence is three plane waves per axis from 0.4 to
2.2 Hz, 35% of the gusting speed, so neighbouring cards do not move as one. The engine's point sources
(a moving creature, a detonation) add 6 m/s per unit, sampled at the part's origin as the stock solve
samples. The engine's per-sample yaw and pitch deviation -- radians fed to `sincos` unhalved, which turns
the stock wind several times a second -- is the stock solve's alone.

**Settled.** When no moving joint is further than 1e-4 (node units) from where its bones were last
published, and none moves faster than 1e-3 relative to the part, the part publishes nothing (no palette
upload, the caster cache may keep it). Measured against the last publish, not last frame, so a slow creep
is still published once it adds up; measured in the part's own frame, so a cloak carried by a steadily
walking creature settles, and a part leaning still in a steady wind is settled. A publishing frame reports
its largest node-space joint move to the shadow-caster verdict, which `dangly.still` thresholds.

## 5b. Walls (`DanglyColliders`, `DbAnim.DanglyWalls`)

A Swing part keeps clear of the static geometry it hangs beside, read from what the scene draws: every visible
model's drawn trimesh in the part's scene (tiles as merged and drawn, placeables static or not) at its placed
pose. Never a creature or anything it wears or holds, anything riding another object, a door, an effect, or
another dangly mesh: those move, and a snapshot of them would be a ghost.

- **When.** A part gathers its faces once it has held its pose for 0.5 s, and again every 10 s so a placeable
  that arrives or goes later is seen; no gather starts once 0.5 ms of gathering has been spent in the current
  1/60 s of wall clock. The frame its pose changes it drops them and waits again, so a cloak on a creature
  never collides and a banner on a tile or a placeable always does. A gather never builds a mesh tree: it
  reads a mesh through the tree the hit test already built for it, or walks its faces.
- **Gather.** A moving joint's reach is a sphere about its column's pinned root, as wide as the chain to it.
  A face (front counter-clockwise) is kept for the joint when it is in that reach and the joint rests in front
  of it -- so a face the cloth hangs behind, the back of the wall it hangs on included, is never a wall for it.
  The joint keeps its rest distance from the face less the room its nearest vertex had beyond a 1 cm skin: that
  vertex stops 1 cm from the face, or where it was authored if nearer. A vertex resting more than 2 cm behind
  the face was authored inside it (a strand whose far edge is sunk in its wall) and does not count. At most 64
  faces and 256 joint-face pairs a part, the nearest kept.
- **Solve.** After each constraint iteration of every substep, a joint the substep carried from in front of a
  face's clearance to behind it, crossing inside the face (2 cm slack at its edges, so a seam is not a gap), is
  put back on the clearance along the face normal, and its slide along the face is cut by half the push
  (friction). A joint already behind at the substep's start came round an edge and is left there. Its velocity
  is then taken from where it was left, so the contact is inelastic and a banner the wind presses to its wall
  settles there.

## 6. Sway

A one-joint rig with the tip at the moving set's mobility-weighted centroid. A card tall enough to bend
turns about its anchors, so it keeps its length at any lean, and a gust front bends a field of them in
turn; it overshoots and rings at its authored frequency when a creature walks through it, and costs one
joint a frame.

## 7. Knobs (`dangly` group)

| key | kind | default | meaning |
|---|---|---|---|
| `dangly.enabled` | master | on | off: every dangly part is drawn at rest |
| `dangly.motion` | `auto` / `vanilla` (labelled automatic / stock) | `auto` | stock moves every part the stock way |
| `dangly.sway`, `dangly.swing`, `dangly.jiggle` | toggle, ChatOnly | on | off runs that kind the stock way |
| `dangly.collide` | toggle, ChatOnly | on | off: hanging cloth swings through the walls and floors beside it |
| `dangly.windgain` | 0–3 · 1 | ×`K`: how hard moving air pushes a part, the wind and the owner's own motion alike |
| `dangly.tilt` | 0–1 · 1 | how fully a tipped part falls toward the ground |
| `dangly.breeze` | 0–2 · 0.5 | the least wind an outdoor area blows, in area units (automatic motion only) |
| `dangly.cage` | 0.5–4 · 1.5 | ×the authored excursion limit of a translating rig, or of any rig a creature wears (automatic motion only) |
| `dangly.still` | as before | |

Every force knob of the per-vertex integrator this replaces is retired -- the gravity, wind, turbulence,
variation, body-proxy and solver-rate keys, and the worker-thread toggle that never dispatched anything.
`tools/gates/retired-terms.txt` names them, and refuses them anywhere under `managed/` or `docs/`.
