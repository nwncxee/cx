# CX-112: the MDL parser

CX reads models itself. `managed/Alloy/Mdl/` is a complete, engine-free port of the engine's own model reader:
the binary container, the ASCII interpreter and the post-process that turns authored geometry into render
geometry. Nothing in that directory touches engine memory, which is what lets `tools/mdlcheck` run the whole
parse off the device -- over the game's own containers, and over a content corpus (`CONTENT_MODELS`).

## The files

| file | what it is |
|---|---|
| `MdlParser.cs` | the entry point: tells binary from ASCII on the first dword, as `IODispatcher::ReadSync` does, and runs the post-process. Also the `MaterialHint` seam (below). |
| `MdlTokens.cs` | the engine's lexer: `AurResGetNextLine`'s line split, `dofile`'s trim and `#` rule, `firstword`'s separator set, and the `sscanf` overloads with their leave-the-field-alone-on-failure behaviour. |
| `MdlBinary.cs` | the container, the model and animation headers, the node common header, the controller records, and the file-id/engine-id controller map. |
| `MdlBinaryPayload.cs` | every node type's own block: trimesh, skin, animmesh, dangly, aabb, light, emitter, reference. |
| `MdlAscii.cs` | the console-script driver (nine whitelisted commands), `ImportMaxTree`, `ParseNode`, every class's field table, the three array-body forms, and `ReorderChildren` as libstdc++ sorts it. |
| `MdlPost.cs` | `ProcessVerts`, the weld (`InternalGenVertices`, with the AABB variant), `BuildNeighborLists`, the index list, the tangent basis with its handedness split, `ComputeLocalSurfaceArea`, and `SynchronizeTree`. |
| `MdlSelfCheck.cs` | the embedded fixtures: one ASCII model, one hand-built binary, the lexer's contracts. |
| `../MdlParseCheck.cs` | the LIVE check (knob `mdl.check`): every model the engine reads is parsed again by CX on its own worker and compared with the engine's in-memory tree. The only engine-touching part. |

## Controller ids

A `.mdl` file numbers its controllers 8 (position), 20 (orientation), 36 (scale), 76 (light colour), 100
(mesh self-illumination), 128 (mesh alpha), and so on. The ENGINE renumbers them on load into byte offsets
inside its runtime `Part` subclass, because `NewController::Control` writes `*(float*)(part + id)`; the ASCII
parser writes those engine numbers straight out, which is why the loader has a remap ladder at all. CX speaks
**file ids** everywhere (`Ctl` in `ModelDef.cs`), because they do not move between client builds;
`MdlBinary.EngineControllerId(fileId, kind)` is the ladder, and the only thing that needs it is the live
comparison. The ladder is per node class -- emitter, then the mesh family (which a TRIGGER node also passes,
since `0x421 & 0x21 == 0x21`), then light, then the generic rule.

## Engine quirks that change the result, ported deliberately

- `Vector::Normalize` leaves a length already within 1e-5 of one ALONE, turns the zero vector into `(1,0,0)`,
  and scales by `sqrt(1/len2)` rather than `1/sqrt(len2)`.
- Smooth normals are an AREA-weighted sum of raw cross products, normalised once, matched by vertex INDEX
  equality; a smoothing group of 0 means one flat normal per corner.
- The weld key is exact float equality on position, normal, mirror flag, stream-presence mask and every
  present stream, in first-seen order over face order x corner order. An AABB node uses a much more
  restrictive weld: it merges only against ONE earlier face, and only when two of three corners match.
- The degenerate-face purge compacts the face list without compacting the scratch array the adjacency pass
  then indexes, so a mesh that HAS degenerate faces gets adjacency built from mismatched face records. That is
  an engine bug; the compiled files carry it, so CX reproduces it.
- A plain controller line with exactly four numbers, and a keyed line with exactly five, are read as
  axis-angle and converted to a quaternion -- by VALUE COUNT, not by keyword.
- `wirecolor` is parsed and thrown away. `ignorefog 0` does nothing. `dummy` is not a node-type keyword (any
  unrecognised type makes a plain node). Only the first `faces` block of a node is kept. `bitmap` is
  `texture0` but wipes the field first. A mesh with no `bmin` line always encloses the origin.
- Block terminators are case-SENSITIVE prefix compares, so `endfoo` ends a block.
- An emitter node raises the owning model's radius to 40.
- `ReorderChildren` runs `std::sort` with a comparator under which nearly every pair is equal, so the child
  order is libstdc++'s introsort permutation, not a stable sort's. The three libstdc++ pieces are transcribed
  from the decompile; the live check (below) compares the resulting order with the engine's tree.

## What CX does NOT take from the file

- **Per-face smoothing groups** survive only in the normals they produced: a compiled `MaxFace` record has no
  smoothing-group field, so the two readers cannot be compared on it.
- **The four reserved per-vertex streams** at file 0x24c/0x250/0x254/0x25c are loaded, sized and serialised by
  the engine and never written or sampled by it. CX drops them.
- `texindices1..3` are parsed by the engine and never read; CX keeps them on `MeshRaw` for fidelity.
- **UV sets 1..3** (`tverts1..3`) are parsed and welded on both sides and reach no shader on either. The
  engine pointers and enables them as attributes 11/12/13 (`vTcIn1/2/3`) whenever the pools exist, and no
  stock stage declares one; Alloy keeps them off the vertex because the weld splits on them, so the welded
  vertex count depends on sets nothing samples. There is no lightmap sampler in this client at all:
  `texture1` lands on texture unit 1, where the only shader that declares it reads a normal map at UV set 0.
- A **skin's bone map** is a PART index the engine resolves at instance time against the instantiated part
  tree (`PartSkin::PostProcess`), not a node index from the file. CX resolves bone names against the model's
  own node tree instead, so `SkinDef.BoneNodes` is a different space from a compiled file's.
- The **bind pose** (`qbone_ref_inv`/`tbone_ref_inv`) is likewise appended at instance time from the
  instantiated skeleton; CX carries what the file states and leaves the rest to the scene layer.
- `depthtexture`, `frameblending`, `opacity`, `targetsize`, `numcontrolpts`, `controlptradius`,
  `controlptdelay`, `controlptsmoothing`, `lookathandle`, `lightningSubDiv`, `lightningZigZag`, `p2p_type`,
  `setlooping`, `multimaterial`, `lightmapped`, `hologram_donotdraw`, `m_bIsBackgroundGeometry`,
  `subclassification`, `emitter2` -- **none of these strings exist in this client**. They are NWmax or
  toolset concepts; the client cannot parse them and neither does CX.

## The `.mtr` seam

A mesh with no `renderhint` of its own still gets a tangent frame when the material it names
(`materialname`, or `texture0` when that is empty) carries one -- the engine looks that up through
`SharedMaterial::GetSharedMaterial`. Reading a `.mtr` is a resource lookup, so an engine-free parser cannot do
it: `MdlParser.MaterialHint` is a delegate the host sets once at startup. `tools/mdlcheck` points it at the
corpus's `.mtr` files; a session can point it at the engine's own lookup.

## Measured

`tools/mdlcheck` (the four default modes; `dotnet run -c Release --project tools/mdlcheck`):

- **golden** -- one engine-compiled payload checked in at `tools/mdlcheck/fixtures/`, parsed and asserted
  against recorded values, so the binary offsets are a regression lock and not a round trip of one belief.
- **bifs** -- **32832 of 32832** models in the base game's containers parse; 422178 nodes, 19.9M vertices,
  1.68 GiB at ~400 MiB/s wall across the pool, 0 out-of-range indices.
- **ascii** -- **31848 of 31848** files in a persistent world's content corpus parse (9391 of them binary), 2.19 GiB.
  278 out-of-range face/UV indices and 3 models with a clamped count: malformed content, counted and named
  rather than quietly clamped.
- **fuzz** -- **14400 mutations** (truncations and bit flips) of 240 real models: 0 throws, 0 over 4 s,
  0 over 192 MiB, worst case 149 ms.

## The live check

`/mdl check 1` (chat-only, off by default) chains `IODispatcher::ReadSync`, snapshots the engine's tree on the
main thread, and parses the same resource again on `cx-mdlcheck`, comparing kinds, names, node numbers,
hierarchy, controllers (converted to the engine's numbering), vertices and faces. On every area edge it logs
one line:

    MdlParseCheck: PASS -- 69 of 69 models compared (6 from ASCII, 63 from the compiled binary), 0
    mismatches; cx PARSE 17.1 ms on cx-mdlcheck vs engine 174.3 ms on the main thread (outermost ReadSync
    only; compare 0.1 ms worker, snapshot 1.4 ms main, 0 unparsed, 0 with refusals, ...)

It compares node kinds, names, node numbers, hierarchy, controllers (converted to the engine's numbering)
**and their key times and values, read out of the node's own float pool**, plus vertex and face counts. Three
areas, 230 models, 0 mismatches.

The timing is measured to be comparable: only the OUTERMOST `ReadSync` counts towards the engine's number
(a supermodel loads from inside its child's parse, so every level would double-count), the CX figure is the
PARSE alone with the comparison timed separately, and the line states how many models each side read as ASCII
versus as the compiled binary. The snapshot is the check's own main-thread cost and is reported apart, so the
claim the line supports -- that the CX parse costs the main thread nothing -- stays honest.

## Hostile input

Models arrive from a server's haks and NWSync, so a malformed one must come back as a refusal: never a throw,
never a hang, never an allocation past a bound. A NUL ends every scan (the engine's buffers are C strings);
the binary node walk has depth, node and cycle caps; the ASCII pre-order flatten is iterative; the walkmesh
tree has a depth cap; a declared element count is clamped by the bytes the file can still supply; and every
`count * stride` bound is computed in `long`. Each refusal is counted on `ModelDef.Refusals` and named in
`RefusalNote`. `MdlSelfCheck` runs 409 hostile cases (the six reproduced crash/DoS inputs plus 400 random
truncations and bit flips of both fixtures) under a time and allocation bound, and `mdlcheck fuzz` does the
same over real models.
