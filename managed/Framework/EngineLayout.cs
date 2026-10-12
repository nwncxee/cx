using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CX.Framework;

/// <summary>
/// Every engine-memory constant the extender reads -- struct field offsets and vtable slots -- in one table,
/// keyed by (os, build); <see cref="SymbolMap"/> answers where a FUNCTION is. Code declares, data measures: a
/// row names a class, a field and a probe, and the running build's file gives its value per OS
/// (<see cref="ClientBuilds"/>, <c>Framework/Builds/*.tsv</c>); provenance is <c>docs/engine-layout.md</c>.
/// </summary>
public static unsafe class EngineLayout
{
    // "Measured identical", "differs" and "Windows never looked" (a `-`) are facts about a build, so they
    // live with it; the last is the migration's completion counter. Every row resolves once at static init
    // and reads afterwards as a plain field, because Alloy reads these in the frame loop. They are declared
    // HERE rather than beside their readers so that the count is honest: a per-file static initializer runs
    // on first touch, and a count that depends on what the player has done is not a count.
    // Declared first on purpose: static field initializers run in textual order, and the table below
    // registers into this list as it resolves.
    private static readonly List<Row> _rows = new();
    // Also ahead of the table: a row asks ClientBuilds for its value, which reads the build key off this.
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();

    // ================= the table =================
    // Each row names the engine class it belongs to: that is the key #431's Ghidra extractor pairs on,
    // and the unit #430 measures (a class diverges in 2-3 contiguous segments, anchored on a function pair).

    /// <summary>CNWCObject + 0x0c: object type byte -- 5 creature, 6 item, 9 placeable, 0xa door.</summary>
    public static readonly int ObjType = Field<byte>("CNWCObject", "type");
    /// <summary>CNWCObject -&gt; its object id (uint). Every sender passes <c>obj+8</c> as the OID on both builds
    /// (<c>PerformDefaultActionOnCreature</c>'s Attack / Dialog / UseObject sends, <c>HandleLMouseDown</c>'s
    /// stack-slot <c>+8</c> read).</summary>
    public static readonly int ObjId = Field<uint>("CNWCObject", "id");
    /// <summary>CNWCCreature: "the feedback plate is currently shown for me", set at the top of
    /// ShowFeedbackBubbleText (Linux disasm @0x71a81e, Windows @0x80b240).</summary>
    public static readonly int CreaturePlated = Field<int>("CNWCCreature", "plated", Expect.Byte01);
    /// <summary>CNWCCreature -&gt; CNWCCreatureStats* (Windows via <c>ShowFeedbackBubbleText</c>
    /// @0x80b240, docs/windows-symbols.md).</summary>
    public static readonly int CreatureStats = Field<CX.Engine.CNWCCreatureStats>("CNWCCreature", "stats");
    /// <summary>CNWCCreatureStats first / last name <c>char*</c> + length pairs: <c>GetFullName</c> (Linux nm 0x74db80, Windows
    /// rva 0x7f3f00) reads them the same on both builds; the +0x40/+0x48 override (SetName, a disguise) is not these.</summary>
    public static readonly int StatsFirstName = Field<IntPtr>("CNWCCreatureStats", "firstName");
    public static readonly int StatsFirstNameLen = Field<uint>("CNWCCreatureStats", "firstNameLen");
    public static readonly int StatsLastName = Field<IntPtr>("CNWCCreatureStats", "lastName");
    public static readonly int StatsLastNameLen = Field<uint>("CNWCCreatureStats", "lastNameLen");
    /// <summary>CNWCPlaceable, same flag (Linux disasm @0x7ea8b8, Windows @0x871b60).</summary>
    public static readonly int PlaceablePlated = Field<int>("CNWCPlaceable", "plated", Expect.Byte01);
    /// <summary>Virtual slot of <c>GetPortrait(unsigned char)</c> on CNWCObject -- MSVC orders the table
    /// differently, so this is a vtable index, not a field.</summary>
    public static readonly int GetPortraitSlot = Field("CNWCObject", "vtbl.GetPortrait", Expect.VtblSlot, sig: "sret(byte size)");

    // ---- vtable slots Alloy calls through (#433) ----
    // Every one is -8 on Windows and for one reason: Itanium gives a virtual destructor TWO slots
    // (complete + deleting), MSVC gives it one, so everything declared after it shifts by a pointer.
    // Measured, not assumed -- tools/abi/vtable-slots.py prints both tables side by side.

    /// <summary>Part::AsPartTriMesh() -- the type filter SkyBox/BucketGate/GrassField run a part through.</summary>
    public static readonly int PartAsTriMeshSlot = Field("Part", "vtbl.AsPartTriMesh", Expect.VtblSlot, sig: "PartTriMesh()");
    /// <summary><c>Part::AsPartSkin()</c> -- <c>WritePartHighlightVertices</c> @0x45fe40 glows a mesh
    /// part only when this is null, so a skinned body takes no glow quads.</summary>
    public static readonly int PartAsSkinSlot = Field("Part", "vtbl.AsPartSkin", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary>Part::AsPartBeam() -- same filter for the beam overlays.</summary>
    public static readonly int PartAsPartBeamSlot = Field("Part", "vtbl.AsPartBeam", Expect.VtblSlot, sig: "PartBeam()");
    /// <summary>Part::PostProcess(Part*).</summary>
    public static readonly int PartPostProcessSlot = Field("Part", "vtbl.PostProcess", Expect.VtblSlot, sig: "void(Part part)");
    /// <summary>CAurTexture::AsCAurTextureBasic().</summary>
    public static readonly int TexAsBasicSlot = Field("CAurTexture", "vtbl.AsCAurTextureBasic", Expect.VtblSlot, sig: "CAurTextureBasic()");
    /// <summary>CAurTexture::GetReferencedTexture() -- the indirection every tid read goes through first.</summary>
    public static readonly int TexReferencedSlot = Field("CAurTexture", "vtbl.GetReferencedTexture", Expect.VtblSlot, sig: "CAurTexture()");
    /// <summary>VertexPrimitive::SetupArrays() -- virtual on purpose: Skinned/Anim/Dangly each override it.</summary>
    public static readonly int VpSetupArraysSlot = Field("VertexPrimitive", "vtbl.SetupArrays", Expect.VtblSlot, sig: "void()");
    /// <summary>CAurBehavior::AsBehaviorCameraFollow() -- the camera-follow type filter.</summary>
    public static readonly int BehaviorAsFollowSlot = Field("CAurBehavior", "vtbl.AsBehaviorCameraFollow", Expect.VtblSlot, sig: "CAurBehaviorCameraFollow()");

    /// <summary>CycleTIDTextureController::Control() -- one animated-texture tick.</summary>
    public static readonly int CycleControlSlot = Field("CycleTIDTextureController", "vtbl.Control", Expect.VtblSlot, sig: "void()");

    // ================= Alloy: the Scene walk (#436, batch 1 of #429 section 2) =================
    // Measured 2026-09-11 with tools/ghidra/FieldDump.java over both programs in $GHIDRA_PROJECT/nwn-linux.rep,
    // diffed by tools/ghidra/field-diff.py (.scratch/436). A row is Same/Diverges only where a paired
    // function, a constructor block or a class-wide segment measured at both ends says so; where the class
    // had no validated pair, the row is Unresolved, which is the honest answer and not a failure.
    // Every value here is decompiler evidence -- a CANDIDATE until a runtime probe says otherwise.

    // ---- Scene (identical 0x0..0x1e0: 32 offsets paired over 38 functions, no conflict) ----
    /// <summary>Scene -&gt; <c>CExoArrayList&lt;Gob*&gt;</c>, EVERY gob in the scene: items at +0x130, count at
    /// +0x138. <c>Scene::ChildAdd</c> @0x461d50 appends here and <c>ManageSceneBSP</c> @0x47fa80 iterates
    /// <c>0..*(Scene+0x138)</c> over it three separate times. This is the authoritative membership list --
    /// <c>gobbucket</c> @0x19abbf0 is only the VISIBLE slice and is rebuilt every frame.</summary>
    public static readonly int SceneGobs = Field<IntPtr>("Scene", "gobs");
    /// <inheritdoc cref="SceneGobs"/>
    public static readonly int SceneGobCount = Field<int>("Scene", "gobCount");
    /// <summary>Scene -&gt; BSPNode* root (the static-geometry BSP). Linux-verified in ManageSceneBSP.</summary>
    public static readonly int SceneBspRoot = Field<IntPtr>("Scene", "bspRoot");

    /// <summary>The fog block <c>Scene::DoFog</c> @0x45f1b0 pushes into <c>GLRender::m_fog</c>: enable,
    /// start, end, and the colour <c>Scene::SetFogColor(Vector&amp;)</c> @0x45e220 writes -- also the colour
    /// <c>PartTriMesh::Mat</c> @0x450960 paints a scene-tinted gob in.</summary>
    public static readonly int SceneFogEnable = Field<int>("Scene", "fogEnable");
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogStart = Field<float>("Scene", "fogStart");
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogEnd = Field<float>("Scene", "fogEnd");
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogColor = Field<System.Numerics.Vector3>("Scene", "fogColor");

    /// <summary>Scene -&gt; LightManager* (<c>Scene::RenderSinglePass</c> @0x46e5f0 calls
    /// <c>GetShadowLights(*(this + 0x158))</c>).</summary>
    public static readonly int SceneLightManager = Field<CX.Engine.LightManager>("Scene", "lightManager");
    /// <summary>Scene -&gt; its <c>SafePointer&lt;WindManager&gt;</c>, the slot beside
    /// <see cref="SceneLightManager"/>. <c>Scene::GetWindVector</c> @0x460c60 is one line over it, and that
    /// one line is where both builds were measured.</summary>
    public static readonly int SceneWindManager = Field<IntPtr>("Scene", "windManager");

    /// <summary>Scene -&gt; the embedded <c>DrawBucketManager</c>, constructed in place by
    /// <c>Scene::Scene</c> (@0x4724f0 / rva 0xc0ca0) at the same offset on both builds. Its own base IS its
    /// first <c>DrawBucket</c> -- <c>GetDrawBucketByItemType</c> indexes from there by
    /// <see cref="BucketStride"/> -- so the row hands out that record and the rest are its indexer's.</summary>
    public static readonly int SceneDrawBuckets = Field<CX.Engine.DrawBucket>("Scene", "drawBuckets", embedded: true);

    /// <summary>Scene vtable slot 81 -- <c>Scene::IsAnimating</c> @0x45e000. Vanilla gates the whole dangly
    /// solve on it, which is what stops cloaks swaying behind a paused menu. Both tables are printed by
    /// <c>tools/abi/vtable-slots.py Scene</c>; the -8 on Windows is the virtual-destructor rule.</summary>
    public static readonly int SceneIsAnimatingSlot = Field("Scene", "vtbl.IsAnimating", Expect.VtblSlot, sig: "byte()");

    // ---- Gob (identical 0x0..0x4b8: 28 offsets paired over 50 functions, no conflict) ----
    /// <summary><c>gob+0x18</c> is the SKYBOX flag, not a highlight: its only writers are
    /// <c>Gob::Gob</c> (0) and <c>Scene::SetSkyBox</c> @0x45e260, which sets it on all four skybox gobs, and
    /// <c>PartTriMesh::Mat</c> @0x450960 reads it to keep a skybox part out of the lit branch. The row keeps
    /// its build-file key; the placeable highlight is Scene's own list.</summary>
    public static readonly int GobHighlight = Field<byte>("Gob", "highlight");
    /// <summary>The gob byte that says a part is painted in the scene's fog colour.
    /// <c>PartTriMesh::Mat</c> @0x450960 takes a branch nothing in the Alloy pass had: with this set and a
    /// live scene it calls <c>SetColor4f(fog.r, fog.g, fog.b, 1.0)</c> and draws the part in the FOG COLOUR
    /// at full alpha. The skyfade gob is that part.</summary>
    public static readonly int GobSceneTint = Field<byte>("Gob", "sceneTint");
    /// <summary>Gob -&gt; node pointer, and the same field the fade owner is read through. MUST be non-null
    /// before appending to the gobbucket: the sort comparator <c>FUN_0055e070</c> blindly derefs
    /// <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort does not).</summary>
    public static readonly int GobNode = Field<CX.Engine.Model>("Gob", "node");
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it;
    /// guard non-null so a geometry-less gob is skipped, not dereferenced.</summary>
    public static readonly int GobPartTree = Field<CX.Engine.Part>("Gob", "partTree");
    /// <summary>The two LOD-alternate part-tree roots beside <see cref="GobPartTree"/>.
    /// <c>Gob::SetLODModel</c> @0x43e6a0 ROTATES the three against each other every frame, per visible gob,
    /// with no construction, no destruction and no epoch bump.</summary>
    public static readonly int GobLodRoot1 = Field<CX.Engine.Part>("Gob", "lodRoot1");
    /// <inheritdoc cref="GobLodRoot1"/>
    public static readonly int GobLodRoot2 = Field<CX.Engine.Part>("Gob", "lodRoot2");
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0).</summary>
    public static readonly int GobPosition = Field<System.Numerics.Vector3>("Gob", "position");
    /// <summary>Gob -&gt; world orientation, the engine's <c>(w, x, y, z)</c>. <c>Gob::GetOrientation</c>
    /// (Linux @0x4446a0, Windows rva 0x48520) is a plain 16-byte read of exactly this on both builds, and its
    /// <c>Gob::GetPosition</c> neighbour reads the paired <see cref="GobPosition"/>, which brackets it.</summary>
    public static readonly int GobRot = Field<CX.Engine.Quat>("Gob", "rot");
    /// <summary>Gob fields that change how a part shades, all read by <c>PartTriMesh::Mat</c> @0x450960
    /// except the env override, which <c>PartTriMesh::Draw</c> @0x4513d0 reads to reroute the whole draw to
    /// <c>RenderEnvironmentMapped</c>. The two flags ARM a modulation; the values beside them are the
    /// modulation, and <c>gob+0xfc</c> is the colour whose 12 bytes end exactly where the alpha begins.</summary>
    public static readonly int GobColourMul = Field<short>("Gob", "colourMul");
    /// <inheritdoc cref="GobColourMul"/>
    public static readonly int GobSelfIllum = Field<short>("Gob", "selfIllum");
    /// <inheritdoc cref="GobColourMul"/>
    public static readonly int GobColour = Field<System.Numerics.Vector3>("Gob", "colour");
    /// <inheritdoc cref="GobColourMul"/>
    public static readonly int GobAlpha = Field<float>("Gob", "alpha");
    /// <inheritdoc cref="GobColourMul"/>
    public static readonly int GobFadeAlpha = Field<float>("Gob", "fadeAlpha");
    /// <inheritdoc cref="GobColourMul"/>
    public static readonly int GobEmissive = Field<System.Numerics.Vector3>("Gob", "emissive");
    /// <summary>Gob -&gt; <c>CExoArrayList&lt;Part*&gt;</c> of the roots this gob APPENDED at runtime
    /// (items +0x130, count +0x138), parallel to the <c>Model*</c> array at +0x120. <c>Gob::Append</c>
    /// @0x4422a0 pushes here; <c>Gob::Amputate</c> @0x443fa0 removes.</summary>
    public static readonly int GobAppendRoots = Field<IntPtr>("Gob", "appendRoots");
    /// <inheritdoc cref="GobAppendRoots"/>
    public static readonly int GobAppendCount = Field<int>("Gob", "appendCount");
    /// <summary>Gob -&gt; the <c>TileData*</c> of the tile it IS, or null for every other gob.
    /// <c>ProcessStaticObject</c> @0x5619e0 stores the tile here (and the gob back at <c>tile+0x40</c>);
    /// <c>Gob::Gob</c> @0x540a00 zeroes it and only tile code ever writes it, so a non-null field is a
    /// positive "this gob is tileset geometry, not an object".</summary>
    public static readonly int GobTileData = Field<CX.Engine.TileData>("Gob", "tileData");
    /// <summary>The per-gob environment-map override flag <c>PartTriMesh::Draw</c> @0x4513d0 reads.</summary>
    public static readonly int GobEnvOverride = Field<IntPtr>("Gob", "envOverride");

    // ---- MdlNode and its subclasses ----
    /// <summary>The <c>Model</c> byte whose bit 3 arms a second alpha multiply by
    /// <see cref="GobFadeAlpha"/>. <c>Model::Model</c> (@0x43a850 / rva 0x87bc0) writes it just below the
    /// reference count, and <c>PartTriMesh::Mat</c> (@0x450960 / rva 0xa2f50) reads it off the gob's fade
    /// owner as the same mask test on both builds.</summary>
    public static readonly int NodeOwnerFlags = Field<byte>("Model", "ownerFlags");
    /// <summary>MdlNode -&gt; its parent MdlNode. <c>GatherBones</c> @0x482b10 admits a child part only
    /// when this matches the walked part's own node, which is what keeps the traversal INDEX equal to the
    /// engine's.</summary>
    public static readonly int MdlNodeParent = Field<CX.Engine.MdlNode>("MdlNode", "parent");
    /// <summary>MdlNode -&gt; the node name (<c>char[]</c>) <c>FindPart</c> @0x43c5b0 strcasecmp-s through
    /// <see cref="PartNodeLink"/>. Forensics only.</summary>
    public static readonly int MdlNodeName = Field<byte>("MdlNode", "name");
    /// <summary>Model -&gt; its root <c>MdlNode*</c>, which the live parse check walks to compare CX's
    /// own tree with the engine's. <c>MaxTree::InternalDestructor</c> @0x43a7c0 is the whole function
    /// (<c>mov rax,[this+0x50]; test; jmp [rax]</c>); Windows measured the same way on
    /// <c>?InternalDestructor@MaxTree@@QEAAXXZ</c> rva 0x91be0, which reads <c>[rcx+0x50]</c>.</summary>
    public static readonly int MdlParseModelRoot = Field<CX.Engine.MdlNode>("Model", "root");

    /// <summary>The node's class-kind bit set, a dword: every <c>MdlNode::AsMdlNode*</c> cast is one
    /// <c>(kind &amp; bits) == bits</c> test on it, and the bits run past the low byte (a trigger is 0x421).
    /// Same offset on both builds, same masks.</summary>
    public static readonly int MdlNodeKind = Field<uint>("MdlNode", "kind");
    /// <summary><see cref="MdlNodeKind"/>'s mask AND its expected value -- both bits must be set.</summary>
    public const int MdlNodeKindTriMesh = 0x21;
    // ---- CX-113 (parser review A1): the two fields the engine MUTATES on a shared MdlNodeTriMesh at
    //      RUNTIME, long after the parse. MdlCache serialises a Model through the engine's own compiler, so a
    //      payload written after instantiation carries them -- and a later session loads that state back in.
    //      Both are restored to their pristine values around the compile (MdlCache.Pristine). ----

    /// <summary>MdlNodeTriMesh -&gt; the node's bounding radius. <c>CombineParts</c> (Linux @0x473d30,
    /// Windows rva 0xc5f30) GROWS it on the host node of a merge, in the same three instructions on both
    /// builds, and the node is the MODEL's, shared by every instance of it.</summary>
    public static readonly int MdlRadius = Field<float>("MdlNodeTriMesh", "radius");
    /// <summary>MdlNodeTriMesh -&gt; the sticky "this node's texture is rotated" byte.
    /// <c>SetPartTextureRotate</c> (Linux @0x461670, Windows rva 0xd34c0) writes 1 here when the part's
    /// material <c>HasBumpyShinyMap</c>, and nothing ever writes 0 -- so the pristine value is the
    /// constructor's zero. Identical offset on both builds (<c>mov BYTE PTR [rdi+0x2b9],0x1</c>).</summary>
    public static readonly int MdlTextureRotate = Field<byte>("MdlNodeTriMesh", "textureRotate");

    /// <summary>The mesh node's local bounding box, min then max -- what
    /// <c>WritePartHighlightVertices</c> builds its three glow quads from (Linux @0x45fe40, Windows rva
    /// 0xd4f30: the same six loads, +0xc0..+0xd4).</summary>
    public static readonly int MdlBboxMin = Field<System.Numerics.Vector3>("MdlNodeTriMesh", "bboxMin");
    /// <inheritdoc cref="MdlBboxMin"/>
    public static readonly int MdlBboxMax = Field<System.Numerics.Vector3>("MdlNodeTriMesh", "bboxMax");
    /// <summary>MdlNode -&gt; its <c>NewController</c> array (data +0x78, count +0x80, stride
    /// <see cref="NewControllerStride"/>). <c>MdlNode::InternalControl</c> @0x4990c0 is the loop, byte-for-byte
    /// the same on both builds.</summary>
    public static readonly int MdlNodeControllers = Field<IntPtr>("MdlNode", "controllers");
    /// <inheritdoc cref="MdlNodeControllers"/>
    public static readonly int MdlNodeControllerCount = Field<int>("MdlNode", "controllerCount");
    /// <summary>A NewController record is 0xc bytes: int target-field id (the raw Part byte offset the
    /// controller writes, e.g. <see cref="LightRange"/>) at +0, short key count at +4
    /// (<c>AddGenericControllerSingle</c> @0x4486f0 hard-codes 1 for a plain <c>radius</c>/<c>color</c> line).</summary>
    public const int NewControllerStride = 0xc;
    /// <inheritdoc cref="NewControllerStride"/>
    public static readonly int NewControllerKeyCount = Field<short>("NewController", "keyCount");
    /// <summary>Where a controller's keys sit in its node's float pool (<see cref="MdlNodeControllerData"/>):
    /// the key times from float index +6, the values from +8, and at +0xa the value's width (1 float, 3 vector,
    /// 4 quaternion as x, y, z, w) with 0x10 set on Bezier keys. <c>GetKeyIndexAndAlpha</c>,
    /// <c>GetVectorValue</c> and <c>GetQuaternionValue</c> read all four the same way on both builds.</summary>
    public static readonly int NewControllerTimeKey = Field<ushort>("NewController", "timeKey");
    /// <inheritdoc cref="NewControllerTimeKey"/>
    public static readonly int NewControllerValueKey = Field<ushort>("NewController", "valueKey");
    /// <inheritdoc cref="NewControllerTimeKey"/>
    public static readonly int NewControllerFormat = Field<byte>("NewController", "format");
    /// <summary>MdlNode -&gt; the float pool its controllers' key times and values index into
    /// (<see cref="NewControllerTimeKey"/>); the same three getters read it at +0x88 on both builds.</summary>
    public static readonly int MdlNodeControllerData = Field<IntPtr>("MdlNode", "controllerData");
    /// <summary>MdlNode -&gt; its child nodes (items +0x68, count +0x70). <c>CreateInstanceTreeR</c>
    /// (Linux @0x43c6b0, Windows rva 0x8dab0) sizes the Part's child array from the count and instances each
    /// entry, and <c>AnimateHierarchy</c> (Linux @0x499990, Windows rva 0x4fe90) pairs over the same two
    /// fields -- identical offsets on both builds.</summary>
    public static readonly int MdlNodeChildren = Field<IntPtr>("MdlNode", "children");
    /// <inheritdoc cref="MdlNodeChildren"/>
    public static readonly int MdlNodeChildCount = Field<int>("MdlNode", "childCount");
    /// <summary>The MDL node NUMBER, and the one key an animation is paired to a model by.
    /// <c>CreateInstanceTreeR</c> ends with <c>part+0x30 = node+0x34</c> (<see cref="PartNodeId"/>) and
    /// <c>AnimateHierarchy</c> walks the animation's node tree beside the part tree matching exactly that
    /// pair, scanning the animation's siblings from <c>i % n</c> and wrapping once.</summary>
    public static readonly int MdlNodeId = Field<int>("MdlNode", "nodeId");
    /// <summary>VertexPrimitive -&gt; model matrix (row-major float[16]). <c>Scene::RenderDrawBucket</c>
    /// (@0x46d060 / rva 0xcd210) passes it to <c>SetModelMatrix</c> under the same gate on both builds. The
    /// owning class is the vertex primitive, not the MdlNode: the disassembly derefs <c>part+0x88</c>
    /// (<see cref="PartNode"/>) and takes the node at <c>part+0x8</c> separately.</summary>
    public static readonly int NodeMatrix = Field<System.Numerics.Matrix4x4>("VertexPrimitive", "matrix");
    /// <summary>VertexPrimitive -&gt; matrix flags; only a matrix-class part (<c>vp+0x80 &amp; 0x1e</c>) gets
    /// its matrix pushed. <inheritdoc cref="NodeMatrix"/></summary>
    public static readonly int NodeFlags = Field<byte>("VertexPrimitive", "flags");
    /// <summary>The primitive-type index, argument 3 of the engine's own <c>RenderVertexData</c> call.
    /// Read out of <c>RenderFlat</c> (@0x450eb0 / rva 0xa4710) as <c>node+0x270</c> on both builds -- Ghidra
    /// even types the Windows read <c>AuroraPrimitiveTypes*</c>.</summary>
    public static readonly int MdlPrimType = Field<int>("MdlNode", "primType");

    /// <summary>The render gate <c>PartTriMesh::IsPartRenderable</c> @0x450e60 reads off the part's
    /// node -- zero here refuses the draw outright.</summary>
    public static readonly int MdlRenderGate = Field<int>("MdlNodeTriMesh", "renderGate");
    /// <summary>With <see cref="MdlRenderKind"/> zero, a null here refuses the draw.</summary>
    public static readonly int MdlRenderGeom = Field<IntPtr>("MdlNodeTriMesh", "renderGeom");
    /// <summary>The short that picks which of the two gate branches runs.</summary>
    public static readonly int MdlRenderKind = Field<short>("MdlNodeTriMesh", "renderKind");
    /// <summary>The pool token holding a dangly mesh's REST positions.</summary>
    public static readonly int MdlRestPool = Field<uint>("MdlNodeTriMesh", "restPool");
    /// <summary>Vertex count beside <see cref="MdlRestPool"/>.</summary>
    public static readonly int MdlVertexCount = Field<ushort>("MdlNodeTriMesh", "vertexCount");
    /// <summary>The per-group index counts (<c>int[]</c>) and how many groups there are. With
    /// <see cref="MdlIndexCount"/> and <see cref="MdlVertexCount"/> these are the ONLY inputs to
    /// <c>VertexPrimitiveFlat::SetupArrays</c>' no-geometry exit, which is why they are read at all
    /// (<see cref="MdlIndexCount"/> carries the anchor).</summary>
    public static readonly int MdlIndexGroupCounts = Field<IntPtr>("MdlNodeTriMesh", "indexGroupCounts");
    /// <inheritdoc cref="MdlIndexGroupCounts"/>
    public static readonly int MdlIndexGroups = Field<int>("MdlNodeTriMesh", "indexGroups");
    /// <summary>The ungrouped index count, the last term of the index total. Anchored on the function pair
    /// <c>VertexPrimitiveFlat::SetupArrays</c> (Linux @0x458920, Windows
    /// <c>?SetupArrays@VertexPrimitiveFlat@@UEAAXXZ</c> rva 0xabca0): its summing prefix reads
    /// <c>0x248</c>, <c>0x260</c>, <c>0x26c</c> and <c>0x284</c> at the same offsets on both builds.</summary>
    public static readonly int MdlIndexCount = Field<uint>("MdlNodeTriMesh", "indexCount");

    /// <summary>The MdlNode's <c>MdlNodeTriMesh_PostProcess*</c> -- the TEMPORARY load-time data
    /// <c>PartSkin::PostProcess</c> @0x483120 reads to build the bind arrays. Null with a zero bone count
    /// means the temporaries are GONE and no number of calls can ever prepare the node.</summary>
    public static readonly int SkinTempArrays = Field<IntPtr>("MdlNodeSkin", "tempArrays");
    /// <summary>Bone count on a <c>MdlNodeSkin</c>, a SHORT (zero = not prepared yet).</summary>
    public static readonly int SkinBoneCount = Field<short>("MdlNodeSkin", "boneCount");
    /// <summary>Data pointer of the inverse bind-pose <c>CExoArrayList&lt;Quaternion&gt;</c>, indexed by
    /// GATHERED index. <c>PartSkin::PostProcess</c> (@0x483120 / rva 0xdf860) adds to this list and to the
    /// <c>&lt;Vector&gt;</c> one beside it in the same gather loop, at the same offsets on both builds.</summary>
    public static readonly int SkinBindRot = Field<IntPtr>("MdlNodeSkin", "bindRot");
    /// <summary>The <c>&lt;Vector&gt;</c> half of <see cref="SkinBindRot"/>.</summary>
    public static readonly int SkinBindPos = Field<IntPtr>("MdlNodeSkin", "bindPos");
    /// <summary>The 64-entry <c>short</c> map from bone slot to gathered index. No MdlNodeSkin method
    /// touches it: the only reader is the free function <c>ExecuteSkinGPU</c> (@0x482800 / rva 0xdeb90),
    /// whose 64-iteration stride-2 walk starts here on both builds.</summary>
    public static readonly int SkinBoneMap = Field<short>("MdlNodeSkin", "boneMap");

    /// <summary>The dangly solve's own parameters, from <c>MdlNodeDanglyMesh::InternalParseField</c>
    /// (@0x44da00 / rva 0x99e20): the per-vertex constraint array and its count, then displacement,
    /// tightness and period. Each is pinned by its own <c>.mdl</c> field-name string, and
    /// <c>PartDanglyMesh::Animate</c> reads all four back at the same offsets on both builds.</summary>
    public static readonly int MdlConstraints = Field<IntPtr>("MdlNodeDanglyMesh", "constraints");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlConstraintCount = Field<int>("MdlNodeDanglyMesh", "constraintCount");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlDisplacement = Field<float>("MdlNodeDanglyMesh", "displacement");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlTightness = Field<float>("MdlNodeDanglyMesh", "tightness");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlPeriod = Field<float>("MdlNodeDanglyMesh", "period");

    // ---- Part (identical 0x0..0x58) ----
    /// <summary>The part's own LOCAL transform, which the engine's animation step writes and every
    /// bone solve composes: <c>Vector</c> position at +0x10, <c>Quaternion</c> orientation at +0x1c stored
    /// (w,x,y,z). <c>Part::GetWorld</c> and <c>Part::GetLocal</c> touch the same eight offsets on both
    /// builds -- the compilers only order them differently, which is why the strict pairing dropped them.</summary>
    public static readonly int PartPos = Field<System.Numerics.Vector3>("Part", "pos");
    /// <inheritdoc cref="PartPos"/>
    public static readonly int PartRot = Field<CX.Engine.Quat>("Part", "rot");
    /// <summary>Part -&gt; child array (items +0x38, count +0x40). Written by
    /// <c>CreateInstanceTreeR</c> @0x43c6b0 and freed by <c>Part::~Part</c> @0x437720.</summary>
    public static readonly int PartChildren = Field<IntPtr>("Part", "children");
    /// <inheritdoc cref="PartChildren"/>
    public static readonly int PartChildCount = Field<int>("Part", "childCount");
    /// <summary>The parent back-pointer <c>SkinPose</c> walks UP as well as down.</summary>
    public static readonly int PartParent = Field<CX.Engine.Part>("Part", "parent");
    /// <inheritdoc cref="MdlNodeId"/>
    public static readonly int PartNodeId = Field<int>("Part", "nodeId");
    /// <summary>Part -&gt; Gob* (the instance the part belongs to; null for world geometry).</summary>
    public static readonly int PartGob = Field<CX.Engine.Gob>("Part", "gob");
    /// <summary>Part -&gt; Material*. Ghidra RenderDrawBucket @0x46d060 / PartTriMesh::Draw @0x4513d0.</summary>
    public static readonly int PartMaterial = Field<CX.Engine.Material>("Part", "material");

    // ---- PartTriMesh (segmented: its own block starts at 0x64 and runs +0x4, then +0x8 from 0x88) ----
    // The constructor is the anchor: Linux zeroes 0x64,0x6c,0x74,0x7c,0x84,0x8c as six 8-byte stores
    // (0x64..0x93), Windows the same six at 0x68..0x97, so the block opens +0x4; above it every paired
    // read is +0x8 (0x88, 0x96, 0xa0, 0xb0, and the three bytes 0x94/0x95/0x96 -> 0x9c/0x9d/0x9e).

    /// <summary>The part's WORLD bbox, min then max -- what <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70
    /// hands <c>BoxAbovePlane</c>, and the same pair the cull passes test. WORLD space and TILE PARTS ONLY:
    /// <c>ComputePartBBox</c> @0x561370 is fed only from <c>ProcessStaticParts</c> and the constructor zeroes
    /// the six floats, so a GOB part carries zeros here forever.</summary>
    public static readonly int PartBboxMin = Field<System.Numerics.Vector3>("PartTriMesh", "bboxMin");
    /// <inheritdoc cref="PartBboxMin"/>
    public static readonly int PartBboxMax = Field<System.Numerics.Vector3>("PartTriMesh", "bboxMax");
    /// <summary>Part -&gt; its <c>VertexPrimitive</c>, and NOT a node despite the name (the vtables
    /// prove it, and its +0x10 is the back-pointer to the part itself). Paired twice on PartTriMesh and once
    /// on PartSkin, all +0x8.</summary>
    public static readonly int PartNode = Field<CX.Engine.VertexPrimitive>("PartTriMesh", "vertexPrimitive");
    /// <summary>Part -&gt; TileData* (Ghidra CheckFadePlane @0x47bf30). The constructor's single 8-byte
    /// write, Linux 0x98 against Windows 0xa0.</summary>
    public static readonly int PartTile = Field<CX.Engine.TileData>("PartTriMesh", "tile");
    /// <summary><c>part[0x96]</c>, the merged-away marker every <c>Part::Draw</c> override and
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 check before drawing anything.</summary>
    public static readonly int PartRenderFlag = Field<byte>("PartTriMesh", "renderFlag");
    /// <summary>Part -&gt; its own alpha multiplier, the first term of <c>PartTriMesh::GetIsOpaque</c>
    /// @0x43cee0. Read live: it is animated state.</summary>
    public static readonly int PartAlpha = Field<float>("PartTriMesh", "alpha");
    /// <summary>Part -&gt; texture-rotation angle (degrees, float; Ghidra PartTriMesh::RotateTexture
    /// @0x44fff0) -- NWN's tile-texture 0/90/180/270 anti-repetition scheme.</summary>
    public static readonly int PartTexRotate = Field<int>("PartTriMesh", "texRotate");

    /// <summary>PartDanglyMesh's own mutable state: last frame's world pose (the quaternion is w FIRST).
    /// <c>PartDanglyMesh::Animate</c> (@0x49bbb0 / rva 0x4ef50) reads the three position floats, tests the
    /// quaternion against zero and stores both back, identically shaped on each build. The class's own fields
    /// shift +0xc on Windows, not the +0x8 of its PartTriMesh base.</summary>
    public static readonly int PartPrevPos = Field<System.Numerics.Vector3>("PartDanglyMesh", "prevPos");
    /// <inheritdoc cref="PartPrevPos"/>
    public static readonly int PartPrevRot = Field<CX.Engine.Quat>("PartDanglyMesh", "prevRot");

    // ---- PartLight (own block from 0x64 runs +0x4; 0x64/0x68/0x6c paired, the rest is that segment) ----
    /// <summary>PartLight RGB. The direction is <see cref="LightDir"/> and NOT this -- reading the other
    /// pair extrudes the static projections the wrong way.</summary>
    public static readonly int LightColour = Field<System.Numerics.Vector3>("PartLight", "colour");
    /// <summary>The MDL light node's <c>radius</c> controller (Ghidra <c>MdlNodeLight::InternalParseField</c>
    /// @0x44baf0). Directionals (sun/moon) carry ~100000 and points ~10, so it is also the discriminator.</summary>
    public static readonly int LightRange = Field<float>("PartLight", "range");
    /// <summary>Engine shadow-caster flag.</summary>
    public static readonly int LightCastShadows = Field<int>("PartLight", "castShadows");
    /// <summary>The <c>shadowradius</c> controller beside <see cref="LightRange"/>
    /// (<c>PartLight::PartLight</c> @0x43d1d0 defaults it to -1). Shadow-only, diagnostics here.</summary>
    public static readonly int LightRangeAlt = Field<float>("PartLight", "rangeAlt");
    /// <summary>Light kind: 0 = point/directional, and a discriminant that must be zero for the walk.</summary>
    public static readonly int LightKind = Field<int>("PartLight", "kind");
    /// <summary>Enable flags -- either set means active.</summary>
    public static readonly int LightFlagA = Field<int>("PartLight", "flagA");
    /// <inheritdoc cref="LightFlagA"/>
    public static readonly int LightFlagB = Field<int>("PartLight", "flagB");
    /// <summary>The light's fade weight, 0..1: how much of this light the frame is lighting with.
    /// <c>LightManager::ControlLightsFade</c> @0x4c2050 integrates it toward 1 for a light the frame wants
    /// and 0 for one it does not; Alloy's area terms and its point-light snapshot scale by it too. NOT the
    /// shadow fade, which <c>LightManager::GetShadowLights</c> @0x4c2910 integrates on its own.</summary>
    public static readonly int LightFade = Field<float>("PartLight", "fade");
    /// <summary>Intensity; the lit radius is <c>range * intensity</c> (GetLightAdjustedRadius).</summary>
    public static readonly int LightIntensity = Field<float>("PartLight", "intensity");
    /// <summary>World position, and the direction <c>BSPCreateStaticProjections</c> bakes against.</summary>
    public static readonly int LightDir = Field<System.Numerics.Vector3>("PartLight", "pos");

    // ---- LightManager (identical 0x0..0xc0) ----
    /// <summary>The MASTER <c>CExoArrayList&lt;PartLight*&gt;</c> -- every registered light in the loaded
    /// area, creation order (stable). Ghidra AddLight / GetShadowLights /
    /// SetupLightsForThisPositionAndRadius.</summary>
    public static readonly int LmMasterData = Field<IntPtr>("LightManager", "masterData");
    /// <inheritdoc cref="LmMasterData"/>
    public static readonly int LmMasterCount = Field<int>("LightManager", "masterCount");
    /// <summary>The PER-FRAME light list -- every master light close enough to the scene's focus to matter,
    /// in master order, refilled once per frame by <c>ManageSceneBSP</c> @0x47fa80.
    /// <c>LightManager::EnableLights</c> @0x4c2d10 hands it to the fade integrator as the "wanted" set: below
    /// <see cref="SymbolMap.LightMgrGetMaxLights"/> fades in, past it fades out, absent snaps to zero.</summary>
    public static readonly int LmFrameData = Field<IntPtr>("LightManager", "frameData");
    /// <inheritdoc cref="LmFrameData"/>
    public static readonly int LmFrameCount = Field<int>("LightManager", "frameCount");
    /// <summary><c>LightManager::ControlLightsFade</c> @0x4c2050: +0xa0 is the area AMBIENT PartLight*,
    /// +0x98 the area DIFFUSE (the sun/moon) -- the two it pushes with index -1.</summary>
    public static readonly int LmAreaDiffuse = Field<CX.Engine.PartLight>("LightManager", "areaDiffuse");
    /// <inheritdoc cref="LmAreaDiffuse"/>
    public static readonly int LmAreaAmbient = Field<CX.Engine.PartLight>("LightManager", "areaAmbient");

    // ---- Camera (Gob base region delta 0; own block 0x4c4..0x4e4 +0x4, then +0x8) ----
    /// <summary>Camera position and orientation, measured on Camera's own accessors: Linux carries two
    /// overloads of each and Windows folds each pair into one function that reads the same offsets. NOT Gob's
    /// position and orientation, which sit 8 bytes lower.</summary>
    public static readonly int CamPos = Field<System.Numerics.Vector3>("Camera", "pos");
    /// <inheritdoc cref="CamPos"/>
    public static readonly int CamRot = Field<CX.Engine.Quat>("Camera", "rot");
    /// <summary>CAurCamera -&gt; current behavior host, a <c>CAurBehavior*</c>; its
    /// <see cref="BehaviorAsFollowSlot"/> is <c>AsBehaviorCameraFollow()</c>. Paired three times at delta 0,
    /// and the highest field in the base region that is.</summary>
    public static readonly int CameraBehaviorHost = Field<CX.Engine.CAurBehavior>("Camera", "behaviorHost");
    /// <summary>Camera::GetViewAngle @0x434b10.</summary>
    public static readonly int CamViewAngle = Field<float>("Camera", "viewAngle");
    /// <summary>Camera::GetClipDist @0x4aa700 -- near and far.</summary>
    public static readonly int CamNear = Field<float>("Camera", "near");
    /// <inheritdoc cref="CamNear"/>
    public static readonly int CamFar = Field<float>("Camera", "far");
    /// <summary>Camera::SetViewPort @0x4a38a0 -- width and height, inside Camera's +0x4 segment.</summary>
    public static readonly int CamViewW = Field<float>("Camera", "viewW");
    /// <inheritdoc cref="CamViewW"/>
    public static readonly int CamViewH = Field<float>("Camera", "viewH");
    /// <summary>Every camera field <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 reads -- the enable
    /// byte, the aperture plane at +0x4..+0x10 and the height cut at +0x14 -- as one 24-byte span.
    /// <c>Camera::RenderScene</c> @0x4a7920 is their only writer, every frame, ahead of the frame anchor,
    /// so a snapshot compare at the anchor is a camera-dirty bit with no hook.</summary>
    public static readonly int CamKeyholeState = Field<byte>("Camera", "keyholeState");

    // ---- Material / SharedMaterial ----
    /// <summary>The material's blend pair -- source factor at +0x90, destination at +0x94, both
    /// <c>Aurora::AuroraBlendModes</c> (Ghidra <c>Material::SetBlendingMode</c> @0x4c5c50). <c>dst == 2</c>
    /// is <c>GL_ONE</c>, the engine's own additive test.</summary>
    public static readonly int MatSrcBlend = Field<int>("Material", "srcBlend");
    /// <inheritdoc cref="MatSrcBlend"/>
    public static readonly int MatDstBlend = Field<int>("Material", "dstBlend");
    /// <summary>The blend-disable flag <c>RenderFlat</c> reads (Ghidra GetTexture/GetEnvMapTID
    /// @0x4c5810/0x4c5c40).</summary>
    public static readonly int MatBlendFlag = Field<byte>("Material", "blendFlag");
    /// <summary>The material's own env map (<c>Material::HasEnvMap</c> @0x4c6180 tests exactly this field).
    /// NOT one of the numbered texture slots, so <c>GetTexture</c> never reaches it. Compared only, never
    /// dereferenced: a <c>CAurTexture*</c> is borrowed.</summary>
    public static readonly int MatEnvMap = Field<CX.Engine.CAurTexture>("Material", "envMap");
    /// <summary>Material -&gt; SharedMaterial* override.</summary>
    public static readonly int MatShared = Field<CX.Engine.SharedMaterial>("Material", "shared");

    // SharedMaterial is the one class here where the evidence points AT divergence without resolving it:
    // Windows SharedMaterial functions touch 0x190/0x1a1/0x1b2 -- the 0x11 resref stride, +0x8 from Linux's
    // 0x188/0x199/0x1aa -- while 0x1c0/0x1c8/0x1e8 pair at delta 0 and Linux's 0x1f1 has no Windows
    // counterpart at all. A +0x8 name block under delta-0 neighbours is not a segment anyone has explained,
    // so nothing here is resolved on a guess.

    /// <summary>The <c>customShaderVS</c>/<c>FS</c> resrefs the .mtr named, as inline 17-byte NUL-terminated
    /// buffers that <c>SharedMaterial::ParseField</c> @0x4c6760 fills and <c>Init</c> @0x4c6ff0 hands to
    /// <c>GetCustomShaderProgram</c>. This is what a material is CLASSIFIED by, not the compiled programs it
    /// caches beside them: an empty VS name is the real "pure texture redirect" test.</summary>
    public static readonly int SharedVsName = Field<byte>("SharedMaterial", "vsName");
    /// <inheritdoc cref="SharedVsName"/>
    public static readonly int SharedFsName = Field<byte>("SharedMaterial", "fsName");
    /// <summary>The three <c>.mtr</c> keywords that steer bucket membership
    /// (<c>SharedMaterial::ParseField</c> @0x4c6760): <c>transparency</c>, <c>sample_framebuffer</c>
    /// (1 = bucket 5, 2 = water) and <c>volumetric</c>.</summary>
    public static readonly int SharedTransparency = Field<byte>("SharedMaterial", "transparency");
    /// <inheritdoc cref="SharedTransparency"/>
    public static readonly int SharedSampleFb = Field<int>("SharedMaterial", "sampleFb");
    /// <inheritdoc cref="SharedTransparency"/>
    public static readonly int SharedVolumetric = Field<byte>("SharedMaterial", "volumetric");
    /// <summary>The <c>twosided</c> keyword, the bool byte after <c>transparency</c>, and what decides face
    /// culling on the world path -- not the emitter's own no-cull flag. The engine spends it at BUCKET-FILL
    /// time: <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 files each part in one of three sub-lists,
    /// which <c>RenderDrawBucket</c> draws with cull-face off, front-face or back-face.</summary>
    public static readonly int SharedTwoSided = Field<byte>("SharedMaterial", "twoSided");

    /// <summary>CAurTextureBasic's 32-byte name field, written by its constructor @0x487250. All thirteen
    /// of the class's functions were read by hand: every offset 0x28..0xec agrees, and Windows' extra stores
    /// are MSVC splitting Linux's 16-byte SSE ones, not layout.</summary>
    public static readonly int TexBasicName = Field<byte>("CAurTextureBasic", "name");

    // ---- VertexDataObject (0x8 delta 0; 0x14..0x44 +0x4; 0x48 and up back to delta 0) ----
    /// <summary>The byte <c>VertexDataObjectBase</c>'s ctor zeroes and <c>VertexDataObject::Build</c>
    /// @0x45bac0 sets to 1 for modes 1 and 2. <c>GetElementArrayBuffer</c> @0x45b320 returns 0 while it is
    /// clear, which sends <c>GLRender::BindBuffer</c> @0x4d04a0 down its unbind branch mid-build.</summary>
    public static readonly int VdoIndexed = Field<byte>("VertexDataObject", "indexed");
    /// <summary>The VAO id. <c>GLRender::SetVertexDataObject</c> (@0x4cff30 / rva 0xba770) generates it on
    /// first use and binds it, at the same offset on both builds. It sits below the divergence boundary:
    /// everything above is +4 on Windows, because MSVC does not reuse <c>VertexDataObjectBase</c>'s tail
    /// padding.</summary>
    public static readonly int VdoVao = Field<uint>("VertexDataObject", "vao");
    /// <summary>VDO mode (1 = indexed static).</summary>
    public static readonly int VdoMode = Field<int>("VertexDataObject", "mode");
    /// <summary>The three pool tokens whose GL names the lazy VAO build captures, and the attribute
    /// masks beside two of them -- the loop @0x4d0028..0x4d0427 runs twice, over (+0x24, mask +0x28) and
    /// then (+0x30, mask +0x34). An absent token reads <see cref="VdoNoPool"/> and the engine skips it.</summary>
    public static readonly int VdoElemToken = Field<uint>("VertexDataObject", "elemToken");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoVertToken = Field<uint>("VertexDataObject", "vertToken");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoVertMask = Field<uint>("VertexDataObject", "vertMask");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoDynToken = Field<uint>("VertexDataObject", "dynToken");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoDynMask = Field<uint>("VertexDataObject", "dynMask");
    /// <summary>The vertex item table {offset,size} and its count, read by <c>AddItem</c>,
    /// <c>RenderVertexData</c> and <c>SetVertexDataObject</c>. Windows spends four bytes low in the object
    /// and gets them back here, so the pointer blocks land at the same offsets on both.</summary>
    public static readonly int VdoVertItems = Field<IntPtr>("VertexDataObject", "vertItems");
    /// <inheritdoc cref="VdoVertItems"/>
    public static readonly int VdoVertCount = Field<int>("VertexDataObject", "vertCount");
    /// <summary>The element item table and its count.</summary>
    public static readonly int VdoElemItems = Field<IntPtr>("VertexDataObject", "elemItems");
    /// <inheritdoc cref="VdoElemItems"/>
    public static readonly int VdoElemCount = Field<int>("VertexDataObject", "elemCount");

    // ---- classes with no validated function pair on either side ----

    /// <summary>The draw arguments as FIELDS: <c>RenderFlat</c> (@0x450eb0 / rva 0xa4710) hands them to
    /// <c>RenderVertexData</c> on both builds, and every draw path reaches it. A part whose geometry has never
    /// been uploaded reads a null VDO and an item of -1 -- not an error, "not resident yet".</summary>
    public static readonly int VpVdo = Field<CX.Engine.VertexDataObject>("VertexPrimitive", "vdo");
    /// <inheritdoc cref="VpVdo"/>
    public static readonly int VpItem = Field<int>("VertexPrimitive", "item");
    /// <summary>VertexPrimitive -&gt; the <c>MdlNodeTriMesh</c> it draws, and the <c>Part*</c> list of the
    /// siblings merged into it (<c>CombineParts</c>) with its count. <c>SetupArrays</c> sums its geometry
    /// totals over this node first and then over <c>*(list[i] + </c><see cref="PartNodeLink"/><c>)</c> for
    /// <c>i &lt; count</c>; a NEGATIVE count skips the walk outright and the totals stay zero.</summary>
    public static readonly int VpNode = Field<CX.Engine.MdlNode>("VertexPrimitive", "node");
    /// <inheritdoc cref="VpNode"/>
    public static readonly int VpCombineList = Field<IntPtr>("VertexPrimitive", "combineList");
    /// <inheritdoc cref="VpNode"/>
    public static readonly int VpCombineCount = Field<int>("VertexPrimitive", "combineCount");
    /// <summary>VertexPrimitive vtable slot 5 -- returns the <c>VertexPrimitiveDangly</c> view whose
    /// <see cref="VpdVelocity"/> is the velocity array, NOT the VertexPrimitive itself (verified at
    /// 0x49bd85: the engine calls this and indexes +0xa0 off the RESULT). Both tables printed by
    /// <c>tools/abi/vtable-slots.py VertexPrimitive</c>: -8, like the other eight.</summary>
    public static readonly int VpAsDanglySlot = Field("VertexPrimitive", "vtbl.AsDangly", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary>The engine's own per-vertex velocity array, which the dangly solve integrates in place.
    /// <c>PartDanglyMesh::Animate</c> (@0x49bbb0 / rva 0x4ef50) null-checks <c>vpd+0xa0</c>, calls
    /// <c>GenerateVelocityArray</c> when it is null and then indexes it by vertex -- at 0xa0 on both.</summary>
    public static readonly int VpdVelocity = Field<IntPtr>("VertexPrimitiveDangly", "velocity");

    /// <summary>The BSP node's inventory pointer, and inside it the
    /// <c>CExoArrayList&lt;PartTriMesh*&gt;</c> of tile geometry. <c>SceneBSPAddPart</c> (@0x47ca10 / rva
    /// 0xdcfd0) is a function pair on its whole body: all seven of its bucket writes go through the
    /// inventory record at the same sub-offsets on both builds.</summary>
    public static readonly int BspNodeData = Field<IntPtr>("BSPNode", "data");
    /// <inheritdoc cref="BspNodeData"/>
    public static readonly int BspDataTriMesh = Field("BSPNode", "triMesh");
    /// <inheritdoc cref="BspNodeData"/>
    public static readonly int BspDataTriMeshCount = Field("BSPNode", "triMeshCount");

    /// <summary>The live tile-fade factor the <c>HandleFadingIn/OutBucket</c> step writes and the top of
    /// <c>ManageSceneBSP</c> (@0x47fa80 / rva 0xdb320) commits, and the pending factor beside it.
    /// That commit is one statement inside a loop over the scene's tile list, identical on both builds, so
    /// TileData needs no method of its own to be measured.</summary>
    public static readonly int TileFade = Field<float>("TileData", "fade");
    /// <inheritdoc cref="TileFade"/>
    public static readonly int TilePendingFade = Field<float>("TileData", "pendingFade");

    /// <summary>The 48 <c>DrawBucket</c> records at <see cref="SceneDrawBuckets"/> are 0x10 apart --
    /// <c>GetDrawBucketByItemType</c> (@0x47bd60 / rva 0xd94d0)'s <c>(type*3 + sub)*0x10</c> stride. A
    /// record SIZE, which MSVC may disagree about exactly like a field offset, so it was read on the Windows
    /// caller too: that accessor and <c>AddPartToBucket</c> are one-liners with the same stride on each.</summary>
    public static readonly int BucketStride = Field("DrawBucket", "stride");

    /// <summary>CNWCModule -&gt; the player's current CNWCArea*, off the engine's own
    /// HandleServerToPlayerArea chain. <c>CNWCCreature::UpdatePopUpBubble</c> (Linux @0x72e600, Windows rva
    /// 0x8107a0) dereferences it and compares area ids -- a pointer deref, not a position guess, at the same
    /// offset on both.</summary>
    public static readonly int ModuleCurrentArea = Field<CX.Engine.CNWCArea>("CNWCModule", "currentArea");
    /// <summary>CNWCArea -&gt; the ARE Flags DWORD, bit 0 = interior. The role is pinned by the WIRE ORDER,
    /// which no compiler may reorder: <c>CNWCArea::LoadArea</c> (Linux @0x70e010, Windows rva 0x7dba70)
    /// reads three ints and then this DWORD, identically on both builds.</summary>
    public static readonly int AreaFlags = Field<uint>("CNWCArea", "flags");

    // ---- CX-57: exosound (docs/audio.md). MSVC shifts the middle of CExoSoundSourceInternal by 4 (read off
    //      PlaySourceOn3DVoice @rva 0x1e0050 and SetPriorityGroup @rva 0x1e1610); the tail from +0x50 lines up. ----
    /// <summary>CExoSoundSourceInternal: the stored world position (<c>SetPosition</c> writes it, the play path
    /// hands it to AL as is).</summary>
    public static readonly int SoundSrcPosition = Field<CX.Engine.Vec3>("CExoSoundSourceInternal", "position");
    /// <summary>CExoSoundSourceInternal: <c>PlaybackVariance</c>, clamped to [0, 1] by <c>SetPriorityGroup</c>.</summary>
    public static readonly int SoundSrcVariance = Field<float>("CExoSoundSourceInternal", "variance");
    /// <summary>CExoSoundSourceInternal: the prioritygroups.2da row (<c>SetPriorityGroup</c>'s byte).</summary>
    public static readonly int SoundSrcGroup = Field<byte>("CExoSoundSourceInternal", "group");
    /// <summary>CExoSoundSourceInternal: the 3D voice while one is bound (<c>PlaySourceOn3DVoice</c> sets it).</summary>
    public static readonly int SoundSrcVoice3D = Field<CX.Engine.C3DVoice>("CExoSoundSourceInternal", "voice3d");
    /// <summary>CExoSoundSourceInternal: <c>MaxVolumeDist</c>, the authored full-volume radius that never reached AL.</summary>
    public static readonly int SoundSrcNear = Field<float>("CExoSoundSourceInternal", "nearDist");
    /// <summary>CExoSoundSourceInternal: <c>MinVolumeDist</c>, the far radius the engine sends as AL_MAX_DISTANCE.</summary>
    public static readonly int SoundSrcFar = Field<float>("CExoSoundSourceInternal", "farDist");
    /// <summary>CExoSoundSourceInternal: the sample offset the next play resumes from (AL_SAMPLE_OFFSET).</summary>
    public static readonly int SoundSrcOffset = Field<int>("CExoSoundSourceInternal", "sampleOffset");
    /// <summary>CExoSoundSourceInternal: the 2D voice while one is bound (<c>PlaySourceOn2DVoice</c> sets it).</summary>
    public static readonly int SoundSrcVoice2D = Field<CX.Engine.C2DVoice>("CExoSoundSourceInternal", "voice2d");
    /// <summary>C3DVoice: <c>{ owner; ALuint source; ALuint buffer; }</c> -- the AL source name.</summary>
    public static readonly int Voice3DSource = Field<uint>("C3DVoice", "source");
    /// <summary>C2DVoice: the same 16-byte slot as <see cref="Voice3DSource"/>'s.</summary>
    public static readonly int Voice2DSource = Field<uint>("C2DVoice", "source");
    /// <summary>CNWCArea -&gt; its <c>CNWCAmbientSound</c> (heap; <c>CNWCArea::Update</c> loads it to call
    /// <c>CNWCAmbientSound::Update</c>: Linux @0x707ad2, Windows rva 0x7e9038, the same <c>mov rcx,[rdi+0x2d8]</c>).</summary>
    public static readonly int AreaAmbientSound = Field<CX.Engine.CNWCAmbientSound>("CNWCArea", "ambientSound");

    // CX-120: the area's own DAY/NIGHT light palette, cached as floats by CNWCArea::LoadArea (Linux @0x7dba70,
    // Windows rva 0x7dba70) out of the ARE's byte colours: each `mov 0x98(%rdi),%ecx` then three
    // movzbl/divss-by-255 stores. CNWCArea::SetAsDay @0x8064a0 hands +0x1c8 to the ambient setter (vtbl+0x58)
    // and +0x1d4 to the diffuse one (vtbl+0x50); SetAsNight hands +0x164 and +0x170 to the same two. The
    // Windows block is instruction-for-instruction the same at the same displacements (objdump'd at
    // 0x1407de26b..0x1407de41d), and the branch that picks the pair is `cmpl $0x0,0xdc(%rdi)` at 0x1407de45c.
    /// <summary>CNWCArea -&gt; the NIGHT palette's ambient colour (Vector of floats, 0..1).</summary>
    public static readonly int AreaNightAmbient = Field<float>("CNWCArea", "nightAmbient", count: 3);
    /// <summary>CNWCArea -&gt; the NIGHT palette's diffuse (sun/moon) colour.</summary>
    public static readonly int AreaNightDiffuse = Field<float>("CNWCArea", "nightDiffuse", count: 3);
    /// <summary>CNWCArea -&gt; the DAY palette's ambient colour.</summary>
    public static readonly int AreaDayAmbient = Field<float>("CNWCArea", "dayAmbient", count: 3);
    /// <summary>CNWCArea -&gt; the DAY palette's diffuse (sun) colour.</summary>
    public static readonly int AreaDayDiffuse = Field<float>("CNWCArea", "dayDiffuse", count: 3);
    /// <summary>CNWCArea -&gt; the NIGHT palette's FOG colour, and the fog AMOUNT byte beside it.
    /// <c>SetAsNight</c> @0x806640 hands the pair to the area's own <c>SetFog</c>, and <c>SetAsDay</c> the day
    /// pair -- so the amount is what "is there fog here" MEANS at the game layer. Both are stored by
    /// <c>LoadArea</c>, at the same displacements on Windows.</summary>
    public static readonly int AreaNightFog = Field<float>("CNWCArea", "nightFog", count: 3);
    /// <inheritdoc cref="AreaNightFog"/>
    public static readonly int AreaNightFogAmount = Field<byte>("CNWCArea", "nightFogAmount");
    /// <summary>CNWCArea -&gt; the DAY palette's fog colour. <see cref="AreaNightFog"/>.</summary>
    public static readonly int AreaDayFog = Field<float>("CNWCArea", "dayFog", count: 3);
    /// <inheritdoc cref="AreaDayFog"/>
    public static readonly int AreaDayFogAmount = Field<byte>("CNWCArea", "dayFogAmount");
    /// <summary>CNWCArea -&gt; non-zero when the area is on its NIGHT palette. <c>SetAsDay</c> clears it,
    /// <c>SetAsNight</c> sets it, and <c>LoadArea</c> branches on it to pick which of the two pairs to
    /// apply.</summary>
    public static readonly int AreaIsNight = Field<int>("CNWCArea", "isNight");
    /// <summary>CNWCArea -&gt; the area's own <c>Scene</c>: the object the game writes every area-wide render
    /// term into, which is what makes those terms readable without a name lookup. <c>CNWCArea::SetFog</c>
    /// @0x802380 refuses outright when it is null, and <c>LoadArea</c> (rva 0x7dba70) hands it to the "sun"
    /// model at the same offset on Windows. In DB mode it is CX's own scene SHELL.</summary>
    public static readonly int AreaScene = Field<CX.Engine.Scene>("CNWCArea", "scene");
    /// <summary>CNWCAmbientSound -&gt; the music stream handle (heap; <c>PlayMusic</c> @0x801b10 / rva 0x876990).</summary>
    public static readonly int AmbientMusic = Field<CX.Engine.CExoStreamingSoundSource>("CNWCAmbientSound", "music");
    /// <summary>CNWCAmbientSound -&gt; the ambient-bed stream handle (<c>PlayAmbientSound</c> @0x801f30 / rva 0x8761a0).</summary>
    public static readonly int AmbientBed = Field<CX.Engine.CExoStreamingSoundSource>("CNWCAmbientSound", "ambient");
    /// <summary>CNWCAmbientSound -&gt; nonzero in the low-sound "truncated" mode, where one stream carries both.</summary>
    public static readonly int AmbientTruncated = Field<int>("CNWCAmbientSound", "truncated");
    /// <summary>CExoStreamingSoundSource: the handle's only member, its Internal (the ctor @0x574240 zeroes it and
    /// <c>Reset</c> fills it). Every stream list the engine keeps is keyed by the Internal, never the handle.</summary>
    public static readonly int StreamInternal = Field<IntPtr>("CExoStreamingSoundSource", "internal");
    /// <summary>CAurBehaviorCameraFollow -&gt; <c>SafePointer&lt;CAurObject&gt;</c> to the followed object
    /// (the PC); raw pointer at +0. Its <see cref="GobPosition"/> is what GetTargetWorldPosition returns --
    /// and that accessor (@0x4a03a0 / rva 0x56540) is the whole measurement: one line on each build reading
    /// <c>*(this+0x20) + 0xa8</c> (the Windows half returns the Vector through the sret pointer).</summary>
    public static readonly int BehaviorTargetObj = Field<IntPtr>("CAurBehaviorCameraFollow", "target");

    // ---- the camera solve (#536): CAurBehaviorCameraFollow::Control decompiled on both builds (Linux
    //      @0x4a2290, Windows @0x140055100, name-exported) reads every field below at the same offset ----

    /// <summary>CAurBehavior -&gt; owner <c>CAurObject*</c> (= <c>Camera+8</c>). Both ctors store it at +0x18.</summary>
    public static readonly int BehaviorOwner = Field<IntPtr>("CAurBehavior", "owner");
    /// <summary>CAurBehaviorCameraFollow -&gt; smoothed focus Vector (+0x30..+0x38), written back at the end of Control.</summary>
    public static readonly int FollowFocus = Field<System.Numerics.Vector3>("CAurBehaviorCameraFollow", "focus");
    /// <summary>CAurBehaviorCameraFollow -&gt; orbit distance (float). The engine's pull-in
    /// (<c>UpdateNormalCameraCollision</c> @0x7daf80) writes it DIRECTLY at its tail (<c>+0x48 = dist</c>,
    /// <c>+0x7c = 1</c>), before Control runs -- it is not a zoom velocity.</summary>
    public static readonly int FollowDist = Field<float>("CAurBehaviorCameraFollow", "dist");
    /// <summary>CAurBehaviorCameraFollow -&gt; zoom velocity: Control reads it as <c>dist *= 2^(-v*dt*0.5)</c>
    /// and zeroes it; +0x50 next to it is the focus spring (750). Same body on both builds (§9.4).</summary>
    public static readonly int FollowZoomVel = Field<float>("CAurBehaviorCameraFollow", "zoomVel");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw, degrees, 0 = looking along +Y.</summary>
    public static readonly int FollowYaw = Field<float>("CAurBehaviorCameraFollow", "yaw");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw velocity, deg/s: Control adds <c>v*dt</c> and zeroes it. The
    /// engine's chase drivers (Linux @0x7dddd0 / @0x7de660, Windows rva 0x7d0d00 / 0x7d1c90) and
    /// <c>TurnCamera(deg, 0)</c> (Linux @0x7d8960, Windows 0x7cfe60) write it at +0x58 on both builds.</summary>
    public static readonly int FollowYawVel = Field<float>("CAurBehaviorCameraFollow", "yawVel");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw impulse, degrees added once: <c>TurnCamera(deg, 1)</c> (the drag
    /// paths) accumulates it at +0x88 on both builds; Control consumes and zeroes it.</summary>
    public static readonly int FollowYawImpulse = Field<float>("CAurBehaviorCameraFollow", "yawImpulse");
    /// <summary>CAurBehaviorCameraFollow -&gt; pitch, degrees from vertical (1 = top-down, 89 = horizontal).</summary>
    public static readonly int FollowPitch = Field<float>("CAurBehaviorCameraFollow", "pitch");
    /// <summary>CAurBehaviorCameraFollow -&gt; pitch velocity, deg/s, and pitch impulse, degrees added once: the
    /// yaw pair's twins; Control integrates velocity and impulse into the pitch and zeroes both, at the same
    /// offsets on both builds (Linux @0x4a2290, Windows @0x140055100).</summary>
    public static readonly int FollowPitchVel = Field<float>("CAurBehaviorCameraFollow", "pitchVel");
    /// <inheritdoc cref="FollowPitchVel"/>
    public static readonly int FollowPitchImpulse = Field<float>("CAurBehaviorCameraFollow", "pitchImpulse");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw / pitch / dist clamp-enabled flags (floats, 0 or 1). Set by
    /// the server's LockCamera* and the lockcamera console var; the same words the photo mode locks with.</summary>
    public static readonly int FollowYawClamp = Field<float>("CAurBehaviorCameraFollow", "yawClamp");
    /// <inheritdoc cref="FollowYawClamp"/>
    public static readonly int FollowPitchClamp = Field<float>("CAurBehaviorCameraFollow", "pitchClamp");
    /// <inheritdoc cref="FollowYawClamp"/>
    public static readonly int FollowDistClamp = Field<float>("CAurBehaviorCameraFollow", "distClamp");
    /// <summary>The clamp ranges: yaw +0x68/+0x6c, pitch +0x74/+0x78, dist +0x80/+0x84 (min, max). The mode
    /// reset pushes the client's [camera.limits] into them with the clamp ENABLED, so "enabled" is normal play;
    /// a server lock writes min = max = the current value (the Windows handler's lock arms, rva 0x77a7f0).</summary>
    public static readonly int FollowYawMin = Field<float>("CAurBehaviorCameraFollow", "yawMin", count: 2);
    /// <inheritdoc cref="FollowYawMin"/>
    public static readonly int FollowPitchMin = Field<float>("CAurBehaviorCameraFollow", "pitchMin", count: 2);
    /// <inheritdoc cref="FollowYawMin"/>
    public static readonly int FollowDistMin = Field<float>("CAurBehaviorCameraFollow", "distMin", count: 2);

    /// <summary>CNWCModule -&gt; the module <c>Camera*</c>. <c>GetModuleCamera</c> (Linux @0x63a930) and the
    /// Windows camera message handler (rva 0x77a7f0) both read <c>GetModule()+0xe8</c>.</summary>
    public static readonly int ModuleCamera = Field<CX.Engine.Camera>("CNWCModule", "camera");
    /// <summary>CNWCModule -&gt; attached object id (<c>SetCameraFollow</c>: Linux @0x7d8540, Windows rva
    /// 0x7cf6f0, reached from the camera message's AttachCamera arm; both write +0x124 and +0x140).</summary>
    public static readonly int ModuleCameraAttached = Field<uint>("CNWCModule", "cameraAttached");
    /// <summary>CNWCModule -&gt; find-clear-view pending (int 0/1). See <see cref="ModuleCameraAttached"/>.</summary>
    public static readonly int ModuleFindClearView = Field<int>("CNWCModule", "findClearView", Expect.Byte01);
    /// <summary>CNWCModule -&gt; dialog camera state byte: 0 none, 1 framing, 2 returning (<c>SetDialogCamera</c>:
    /// Linux @0x7da240, Windows rva 0x7cfa90, reached from the dialog message handler).</summary>
    public static readonly int ModuleDialogCamera = Field<byte>("CNWCModule", "dialogCamera");
    /// <summary>CNWCModule -&gt; cutscene transition rate (float; 0 = no cutscene interpolation running).
    /// <c>SetCutsceneCamera</c>: Linux @0x7dd370, Windows rva 0x7cfa00, the camera message's SetCameraFacing tail.</summary>
    public static readonly int ModuleCutsceneRate = Field<float>("CNWCModule", "cutsceneRate");
    /// <summary>CClientExoAppInternal -&gt; <c>CClientOptions*</c> (first word). <c>CClientExoApp::GetClientOptions</c>
    /// is <c>*(*(this+8))</c> on both builds (Linux @0x637ea0, Windows rva 0x6ab9b0).</summary>
    public static readonly int InternalClientOptions = Field<CX.Engine.CClientOptions>("CClientExoAppInternal", "clientOptions");
    /// <summary>CClientOptions -&gt; camera mode byte: 0 chase, 1 top-down, 2 driving (nwscript STIFF_CHASE_CAMERA). Read at +0x1d by
    /// <c>SetCameraFollow</c>/<c>UpdateCameraFollow</c> on Linux and by the Windows camera message handler's
    /// lock arms (<c>*(byte*)(opts+0x1d) &lt; 3</c>).</summary>
    public static readonly int OptionsCameraMode = Field<byte>("CClientOptions", "cameraMode");

    // ---- #571: the stock settings.tml reader (StockConfig) ----
    /// <summary>CExoBase -&gt; <c>CExoConfig*</c> (first word). Both ctors store the freshly constructed config
    /// there (Linux <c>CExoBase::CExoBase</c> @0x4dd1c0 region, Windows @0x140114b00 <c>*(this) = new CExoConfig</c>),
    /// and every engine <c>Get</c> caller loads <c>**g_pExoBase</c> for <c>this</c> (Linux
    /// <c>CGuiInGame::InitializeUserOptions</c> @0x66e24a, Windows <c>CAurora::ComputeDesiredGUIScale</c> @0x140074f70).</summary>
    public static readonly int ExoBaseConfig = Field<CX.Engine.CExoConfig>("CExoBase", "config");
    /// <summary>CExoBase -&gt; the user directory CExoString's <c>char*</c>: <c>CExoBase::CExoBase</c> stores its ctor
    /// argument there via <c>InitFromCharArray(this+0x38)</c>, identical on both builds.</summary>
    public static readonly int ExoBaseUserDir = Field<IntPtr>("CExoBase", "userDir");
    /// <summary>CExoConfig -&gt; the "initializing" byte. <c>Initialize</c> (Linux @0x4e2800) sets it for its own
    /// duration and clears it at the end; while set, <c>Get</c> reads the committed table regardless of its
    /// bool. Same test at <c>*this</c> in both builds' <c>Get&lt;bool&gt;</c> (@0x4efce0 / @0x140119650).</summary>
    public static readonly int ExoConfigInitializing = Field<byte>("CExoConfig", "initializing");
    /// <summary>CExoConfig -&gt; the WORKING cpptoml table (<c>shared_ptr</c>, pointer word first): what the engine's
    /// own <c>Get(key, false)</c> callers read, and what the options panel edits before <c>Commit</c> copies it
    /// into the committed table at +0x38. Null between the ctor and <c>Initialize</c>, which is the reader's
    /// "not yet" test; same in both <c>Get&lt;bool&gt;</c> bodies.</summary>
    public static readonly int ExoConfigWorkingTable = Field<IntPtr>("CExoConfig", "workingTable");

    /// <summary>CExoMoviePlayerInternal + 0x78 -- the GL texture name the decoder uploads each movie
    /// frame into, and the one thing <c>ShowFrame</c> needs that is not already an argument.
    /// <c>CExoMoviePlayer::GetTexture</c> is this load behind one indirection on both builds (Windows export
    /// <c>?GetTexture@CExoMoviePlayer@@QEBAIXZ</c> rva 0x2321a0: <c>mov rax,[rcx]; mov eax,[rax+0x78]</c>).</summary>
    public static readonly int MovieTexture = Field<uint>("CExoMoviePlayerInternal", "texture");

    /// <summary>CClientExoApp -&gt; CClientExoAppInternal*. <c>CClientExoApp::GetInGameGui</c> (Linux
    /// @0x737c70, Windows rva 0x6abbc0) is the same one line on both builds.</summary>
    public static readonly int ExoAppInternal = Field<CX.Engine.CClientExoAppInternal>("CClientExoApp", "internal");
    /// <summary>CClientExoAppInternal -&gt; CGuiInGame*. Same one-line getter as <see cref="ExoAppInternal"/>.</summary>
    public static readonly int InternalInGameGui = Field<CX.Engine.CGuiInGame>("CClientExoAppInternal", "inGameGui");
    /// <summary>CClientExoAppInternal -&gt; the live <c>CMainMenuPanel*</c>. <c>DisplayMainMenu</c> (Linux nm
    /// 0x63c320, Windows rva 0x6d74f0) returns when it is non-null, else news the panel and stores it here;
    /// <c>DestroyMainMenuPanel</c> (Linux nm 0x63a650) deactivates, deletes and nulls it. Non-null = the main menu is
    /// up (it stays up under the pregame Options modal, which only hides the panel).</summary>
    public static readonly int InternalMainMenuPanel = Field<IntPtr>("CClientExoAppInternal", "mainMenuPanel");
    /// <summary>CClientExoAppInternal -&gt; "reached the main menu" latch (int 0/1): the same <c>DisplayMainMenu</c>
    /// sets it to 1 on the first display on both builds, <c>CClientExoApp::GetReachedMainMenu</c> (Linux nm 0x638cf0)
    /// reads it, nothing clears it -- past-the-intro, not menu-is-up.</summary>
    public static readonly int InternalReachedMainMenu = Field<int>("CClientExoAppInternal", "reachedMainMenu", Expect.Byte01);
    /// <summary>CClientExoAppInternal -&gt; "the in-game Options panel is open" int.
    /// <c>CClientExoApp::SetOptionsPanelOpen(int)</c> (Linux nm 0x638d30, Windows rva 0x6acbf0) is its one
    /// store: 1 after the panel activates, 0 from its close callback. Stays 1 under the Game/Video/Controls
    /// sub-panels, which only hide the Options panel.</summary>
    public static readonly int InternalOptionsPanelOpen = Field<int>("CClientExoAppInternal", "optionsPanelOpen", Expect.Byte01);
    /// <summary>CClientExoAppInternal -&gt; cutscene-mode int: <c>CClientExoAppInternal::SetCutsceneMode</c> (Linux @0x745580,
    /// Windows rva 0x6df430, unexported) compares and stores its first argument here on both builds, and
    /// <c>CClientExoApp::InCutscene</c> (Linux nm 0x638dc0; not exported on Windows) returns it.</summary>
    public static readonly int InternalCutsceneMode = Field<int>("CClientExoAppInternal", "cutsceneMode", Expect.Byte01);
    /// <summary>CNWCPlayer -&gt; character (creature) name CExoString. <c>CClientExoAppInternal::GetPlayerByCreatureName</c>
    /// (Linux @0x642350, Windows rva 0x6d8910) strcmp()s <c>*(player+0x40)</c> after comparing the length at +0x48
    /// on both builds; the player/account name is the +0x10/+0x18 pair (<c>GetPlayerByPlayerName</c>, rva 0x6d8a20).</summary>
    public static readonly int PlayerCreatureName = Field<IntPtr>("CNWCPlayer", "creatureName");
    /// <summary>CNWCPlayer -&gt; character name length (the CExoString's second word). See <see cref="PlayerCreatureName"/>.</summary>
    public static readonly int PlayerCreatureNameLen = Field<uint>("CNWCPlayer", "creatureNameLen");
    /// <summary>CNWCPlayer -&gt; the connection-scoped player id (what AppendToMsgBuffer passes and the id-based
    /// tell send takes). <c>CClientExoAppInternal::GetPlayerByPlayerID</c> (Linux nm 0x63f1b0, Windows rva
    /// 0x6d89f0) is a list walk comparing <c>*(player+0x8)</c> on both builds.</summary>
    public static readonly int PlayerId = Field<uint>("CNWCPlayer", "playerId");
    /// <summary>CNWCPlayer -&gt; that player's creature object id. <c>CClientExoAppInternal::GetPlayerByGameObjectID</c>
    /// (Linux nm 0x63f280, Windows rva 0x6d89c0) compares <c>*(player+0x34)</c> on both builds.</summary>
    public static readonly int PlayerCreatureId = Field<uint>("CNWCPlayer", "creatureId");
    /// <summary>CClientExoAppInternal -&gt; CNWCPlayer** roster base; count at <see cref="InternalPlayerCount"/>. Every
    /// GetPlayerBy* walker above reads <c>*(internal+0x38)</c> .. <c>+ *(int*)(internal+0x40)</c> on both builds.</summary>
    public static readonly int InternalPlayers = Field<IntPtr>("CClientExoAppInternal", "players");
    /// <summary>CClientExoAppInternal -&gt; roster entry count (int). See <see cref="InternalPlayers"/>.</summary>
    public static readonly int InternalPlayerCount = Field<int>("CClientExoAppInternal", "playerCount");
    /// <summary>CClientExoAppInternal -&gt; the local player's creature object id. <c>CClientExoApp::GetPlayerCreatureId</c>
    /// (Linux nm 0x6377a0, Windows rva 0x6ac1c0) is the one-liner <c>*(*(this+8)+0x48)</c> on both builds.</summary>
    public static readonly int InternalPlayerCreatureId = Field<uint>("CClientExoAppInternal", "playerCreatureId");
    // ---- CX-100: the one loading screen. The engine's loadscreen panel is the progress and the phase. ----
    /// <summary>CClientExoAppInternal -&gt; the live <c>CGuiAreaLoadscreen*</c>, null when no load bar is up.
    /// <c>CClientExoAppInternal::StartLoadBar</c> news it here and <c>EndLoadBar</c> deletes and nulls it
    /// (Linux @0x6433e0 / @0x642fd0, Windows 0x1406e37f0 / 0x1406d7650 -- both read <c>+0x448</c>, and the
    /// state int <c>GetLoadBarState</c> returns sits at <c>+0x450</c> on both).</summary>
    public static readonly int InternalLoadscreen = Field<CX.Engine.CGuiAreaLoadscreen>("CClientExoAppInternal", "loadscreen");
    /// <summary>CGuiAreaLoadscreen -&gt; its embedded <c>CGuiStatusBar</c> ("LoadBar"). The ctor constructs it in place
    /// (Linux @0x66bcd0 <c>this+0x130</c>, Windows @0x140836f60 <c>lea rcx,[r14+0x120]</c> into the exported
    /// <c>??0CGuiStatusBar@@QEAA@XZ</c>), and every load-progress call passes exactly this address to
    /// <c>CGuiStatusBar::SetStatus</c>.</summary>
    public static readonly int LoadscreenStatusBar = Field<CX.Engine.CGuiStatusBar>("CGuiAreaLoadscreen", "statusBar", embedded: true);
    /// <summary>CGuiAreaLoadscreen -&gt; the "the load is over, waiting for the player" int.
    /// <c>StartLoadBar</c> zeroes it and the two load tails set it to 1 beside the fade-out. While it is 0 the
    /// engine is still loading; at 1 the panel fades out over live world frames, which is where CX hands the
    /// screen back. The class is 8 bytes smaller under MSVC and its neighbours shift the same way.</summary>
    public static readonly int LoadscreenEndPhase = Field<int>("CGuiAreaLoadscreen", "endPhase", Expect.Byte01);
    /// <summary>CGuiStatusBar -&gt; the stored fill, 0..100. <c>CGuiStatusBar::SetStatus</c> clamps to that range and
    /// stores it (Linux @0x594be0 <c>this+0x7c</c>, Windows @0x1401ec270 <c>movss %xmm0,0x80(%rcx)</c>).</summary>
    public static readonly int StatusBarStatus = Field<float>("CGuiStatusBar", "status");
    /// <summary>CClientExoAppInternal -&gt; CNetLayer*. <c>CClientExoApp::GetNetLayer</c> (Linux nm 0x6361a0, Windows
    /// rva 0x6abfb0, unexported: anchored in docs/windows-symbols.md) is the one-liner <c>*(*(this+8)+0x18)</c> on both builds.</summary>
    public static readonly int InternalNetLayer = Field<CX.Engine.CNetLayer>("CClientExoAppInternal", "netLayer");
    /// <summary>CNWCModule -&gt; server-synced game hour (u32). <c>CNWCMessage::HandleServerToPlayerModuleUpdate_Time</c>
    /// (Linux nm 0x76c630, Windows rva 0x7a58e0) stores the flag-2 byte at <c>module+0x16c</c> on both builds.</summary>
    public static readonly int ModuleHour = Field<int>("CNWCModule", "hour");
    /// <summary>CNWCModule -&gt; real minutes per game hour (byte). <c>CNWCModule::SetTimeOfDayState</c> (Linux nm
    /// 0x7d96e0, Windows rva 0x7cfbb0) computes <c>+0x178 = byte[+0x15c] * 60.0</c> on both builds.</summary>
    public static readonly int ModuleMinutesPerHour = Field<byte>("CNWCModule", "minutesPerHour");
    /// <summary>CGuiInGameChatPrompt -&gt; shown flag (int). <c>CGuiInGameChatPrompt::Show</c> tests-then-sets it:
    /// Linux nm 0x6779b0 at +0x230, Windows rva 0x87cc00 at +0x200 (the ctor zeroes the same slot).</summary>
    public static readonly int ChatPromptShown = Field<int>("CGuiInGameChatPrompt", "shown");
    /// <summary>CGuiInGameChatPrompt -&gt; the embedded CGuiEdit. <c>HandleEnter</c> (Linux nm 0x679c50, Windows
    /// rva 0x87b0d0) hands <c>this+off</c> to <c>CGuiEdit::GetText</c>, <c>SetCaretPos</c> and <c>LoseFocus</c> on both.
    /// Embedded, not a pointer: the row hands out the sub-object's address.</summary>
    public static readonly int ChatPromptEdit = Field<CX.Engine.CGuiEdit>("CGuiInGameChatPrompt", "edit", embedded: true);
    /// <summary>CGuiEdit -&gt; <c>CAurString* m_pString</c>, the text object <c>CGuiEdit::GetText</c> (exported on both,
    /// Linux nm 0x592680 / Windows 0x1e93b0) reads its lines from; HandleEnter clears it through the same pointer.</summary>
    public static readonly int GuiEditString = Field<CX.Engine.CAurString>("CGuiEdit", "string");
    /// <summary>CGuiEdit -&gt; <c>bool m_bReadOnly</c>: <c>SetReadOnly</c> (Linux nm 0x5928d0, Windows rva 0x1ec1b0) stores
    /// its argument to +0x110 / +0xe8 and <c>HandleLMouseDown</c> (nm 0x593270, rva 0x1e9fe0) tests the same byte.</summary>
    public static readonly int GuiEditReadOnly = Field<byte>("CGuiEdit", "readOnly");
    /// <summary>Virtual slot of <c>CAurString::SetText(const char*)</c> -- the clear HandleEnter runs on the edit's string
    /// (Linux <c>*vtbl+0xd8</c> = the <c>StringGob</c> thunk <c>_ZThn1216_N9StringGob7SetTextEPKc</c>; Windows <c>+0xd0</c>,
    /// MSVC's single-slot virtual destructor). Both read off the HandleEnter bodies.</summary>
    public static readonly int AurStringSetTextSlot = Field("CAurString", "vtbl.SetText", Expect.VtblSlot, sig: "void(IntPtr text)");
    /// <summary>Virtual slots of <c>CAurString::GetTextLine(int)</c> / <c>GetTextLineCount()</c> (Linux 29/30, the StringGob
    /// thunks @0x492310/@0x495ad0; Windows 28/29, <c>tools/seam/aurora-seam-vtables.tsv</c>). The virtual call is the only
    /// read that works in DB mode, where the string is a CX facade the exported <c>StringGob</c> bodies cannot read.</summary>
    public static readonly int AurStringGetTextLineSlot = Field("CAurString", "vtbl.GetTextLine", Expect.VtblSlot, sig: "IntPtr(int line)");
    /// <inheritdoc cref="AurStringGetTextLineSlot"/>
    public static readonly int AurStringGetTextLineCountSlot = Field("CAurString", "vtbl.GetTextLineCount", Expect.VtblSlot, sig: "int()");
    /// <summary>CGuiInGame -&gt; CPanelQuickBar*. <c>CGuiInGame::Initialize</c> (Linux @0x772600, Windows rva
    /// 0x6f9190 -- the one caller of the CPanelQuickBar ctor) stores the freshly built panel here on
    /// both.</summary>
    public static readonly int InGameGuiQuickbar = Field<IntPtr>("CGuiInGame", "quickbar");
    /// <summary>CGuiInGame -&gt; the stock <c>CGuiInGameChatDialog*</c>, null while no engine conversation panel is
    /// up. <c>ShowDialogEntry</c> news it into +0x70 when null, <c>ShowDialogReplies</c> (Linux @0x770c30,
    /// Windows rva 0x6fb8e0) is a no-op on null +0x70, and <c>CloseDialog</c> (Linux @0x770320, rva 0x6f6d80)
    /// virtual-deletes it, nulls +0x70 and runs <c>ResetGuiState(0x298)</c> -- all on both builds.</summary>
    public static readonly int InGameGuiDialog = Field<IntPtr>("CGuiInGame", "dialog");
    /// <summary>CGuiInGame -&gt; the primary <c>CGuiInGameChatWindow*</c>, null until a module loads (the in-module
    /// signal the frame gate reads). <c>AppendToMsgBuffer</c> (Linux @0x76d550, Windows rva 0x6f68d0) loads it as
    /// <c>this[1]</c> first thing on both builds; <c>PrimaryMsgWindowIsVisible</c> (Linux @0x76e380) forwards it.</summary>
    public static readonly int InGameGuiChatWindow = Field<IntPtr>("CGuiInGame", "chatWindow");
    /// <summary>CGuiInGame -&gt; the live in-game <c>COptionsPanel*</c> (the ESC menu), null while it is down.
    /// <c>HandleOptionsButton</c> (Linux @0x76de30, Windows rva 0x6f86c0 -- the caller of the ctor that owns
    /// <c>"OPTIONS_MAIN"</c>) toggles an open panel closed, else news one here and activates it;
    /// <c>HandleOptionsPanelClose</c> nulls it before <c>CGuiPanel::Deactivate</c> deletes it.</summary>
    public static readonly int InGameGuiOptionsPanel = Field<IntPtr>("CGuiInGame", "optionsPanel");
    /// <summary>CGuiPanel -&gt; state bits: 1 active, 2 hidden (<c>Hide</c>/<c>UnHide</c>), 4 force-hidden (<c>ForceVisible</c>
    /// clears both); the engine's own "shown" test is <c>== 1</c> (<c>Activate</c>'s event choice, <c>Hide</c>'s scene
    /// detach). Exported <c>Hide</c>/<c>UnHide</c>/<c>Activate</c> read it at Linux +0xb8 (@0x687160 / 0x687740 / 0x6878b0),
    /// Windows +0xb0 (rva 0x1fc800 / 0x1fd0d0 / 0x1fbe70); the scene view behind it is +0xc0 / +0xb8.</summary>
    public static readonly int GuiPanelState = Field<uint>("CGuiPanel", "state");
    /// <summary>CGuiMan viewport width / height (int32 pair). <c>CGuiMan::SetGuiSize</c> (exported, Linux
    /// @0x67efc0 / Windows rva 0x1f6640) stores its two arguments here on both.</summary>
    public static readonly int GuiManVpW = Field<int>("CGuiMan", "vpWidth");
    public static readonly int GuiManVpH = Field<int>("CGuiMan", "vpHeight");
    /// <summary>CGuiMan's tracked mouse position (int32 pair, engine GUI space: Y flipped), the point its own
    /// press/wheel handlers hit-test. <c>CGuiMan::HandleLMouseDown</c> reads the pair on both builds.</summary>
    public static readonly int GuiManMouseX = Field<int>("CGuiMan", "mouseX");
    public static readonly int GuiManMouseY = Field<int>("CGuiMan", "mouseY");
    /// <summary>CGuiMan's modal-panel stack depth (the <c>CExoArrayList&lt;CGuiPanel*&gt;</c> count at +0x88/+0x90):
    /// <c>SetModal</c> / <c>UnSetModal</c> (Linux @0x680390 / 0x680330, Windows rva 0x1f66f0 / 0x1f7080) read it on both builds;
    /// every <c>CGuiModalPanel::Activate</c> pushes.</summary>
    public static readonly int GuiManModalCount = Field<int>("CGuiMan", "modalCount");
    /// <summary>CGuiMan's tooltip countdown (int, microseconds; armed from +0x9c on a mouse move, fires at &lt;= 0),
    /// the object it last fired for, and the <c>CGuiTextBubble*</c> it shows. <c>HandleGuiTimer</c> /
    /// <c>TriggerToolTip</c> (Linux @0x57df70 / 0x57f9e0, Windows rva 0x1f5190 / 0x1f6fc0) read +0x98 and +0x68 on
    /// both; the bubble is Linux +0x178, Windows +0x170.</summary>
    public static readonly int GuiManTipTimer = Field<int>("CGuiMan", "tooltipTimer");
    public static readonly int GuiManTipObject = Field<IntPtr>("CGuiMan", "tooltipObject");
    public static readonly int GuiManTipBubble = Field<IntPtr>("CGuiMan", "tooltipBubble");
    /// <summary>CGuiObject's model (<c>CAurObject*</c>): <c>CGuiTextBubble::FixAlignment</c> (Linux @0x588a50,
    /// Windows rva 0x1fd480) reads it at +0x10 on both.</summary>
    public static readonly int GuiObjectModel = Field<IntPtr>("CGuiObject", "model");
    /// <summary>CGuiMan's keyboard-capture object: <c>CaptureKeyboard</c> (Linux nm 0x57d8f0, Windows rva 0x1f4570)
    /// stores it to +0x50 and <c>HandleKeyPress</c> (nm 0x57e060, rva 0x1f52d0) forwards every key to it, on both.</summary>
    public static readonly int GuiManKeyboard = Field<IntPtr>("CGuiMan", "keyboardFocus");
    /// <summary>CGuiMan's double-click flag (int): <c>HandleLMouseDown(int)</c> (nm 0x57e9f0, rva 0x1f5300) stores its
    /// argument to +0xb0 on both, and <c>CGuiTreeview</c>'s click handler reads it nonzero as the double click.</summary>
    public static readonly int GuiManDoubleClick = Field<int>("CGuiMan", "doubleClick");
    /// <summary>CGuiMan's last GUI hit point (3 floats, world space): <c>GuiObjectHitCheck</c> (nm 0x57da40, rva 0x1f4e30)
    /// writes x,y at +0x108 and z at +0x110, and <c>CGuiEdit::HandleLMouseDown</c> hands that Vector to
    /// <c>MoveCaretTo</c> on both builds.</summary>
    public static readonly int GuiManHitPoint = Field<float>("CGuiMan", "hitPoint", count: 3);
    /// <summary>CGuiMan's last hit GUI object (null when the point hit none): <c>GuiObjectHitCheck</c> stores its result
    /// to +0x78 at the end on both builds (nm 0x57da40 / rva 0x1f4e30) and returns it from there when not asked afresh.</summary>
    public static readonly int GuiManHitObject = Field<IntPtr>("CGuiMan", "hitObject");
    /// <summary>CGuiMan's mouse captor (<c>CaptureMouse</c>/<c>ReleaseMouse</c> write +0x58 on both); a live drag's source.</summary>
    public static readonly int GuiManCapture = Field<IntPtr>("CGuiMan", "mouseCapture");
    /// <summary>CGuiDragSource: the <c>HandleMouseMoveCaptured</c> slot, its drag flag past the slop, and the dragged <c>CGuiIcon*</c>.</summary>
    public static readonly int DragMoveSlot = Field("CGuiDragSource", "vtbl.HandleMouseMoveCaptured", Expect.VtblSlot);
    public static readonly int DragSourceLive = Field<int>("CGuiDragSource", "dragging");
    public static readonly int DragSourceIcon = Field<IntPtr>("CGuiDragSource", "icon");
    /// <summary><c>CGuiQuickButton::HandleTriggerTooltip</c>: builds the button's text and hands it to <c>ShowTooltipText</c> (<c>TriggerToolTip</c> calls the slot).</summary>
    public static readonly int QuickButtonTipSlot = Field("CGuiQuickButton", "vtbl.HandleTriggerTooltip", Expect.VtblSlot, sig: "void()");

    /// <summary>What to add to a vtable SYMBOL to reach slot 0. Itanium puts offset-to-top and the typeinfo
    /// pointer in front of the function pointers, so <c>_ZTV&lt;class&gt;</c> is two words short of the
    /// table; MSVC's <c>??_7&lt;class&gt;@@6B@</c> already points at slot 0. Measured by construction in
    /// <c>tools/abi/vtable-slots.py</c>, which prints both tables side by side.</summary>
    public static readonly int VtblSymbolHeader = Field("(abi)", "vtblSymbolHeader");

    // ================= Alloy: the passes (#441, batch 2 of #429 section 2) =================
    // Measured 2026-09-12, same tooling as #436 (tools/ghidra/FieldDump.java over both programs, .scratch/441)
    // but read per function pair rather than through field-diff.py's strict filter, which rejects a pair the
    // moment the two decompilers split one 8-byte store differently. Reading the pairs by hand is what
    // resolved PartEmitter, the class #430 named as the worst case.
    // Every value here is decompiler evidence -- a CANDIDATE until a runtime probe says otherwise.

    // ---- Part: the base fields the pass walkers read (identical 0x0..0x58, #436) ----

    /// <summary>Part -&gt; its MdlNode. <c>PartEmitter::Initialize</c> @0x4aaaa0 reads it as
    /// <c>MdlNodeEmitter*</c> and <c>Scene::RenderLensFlares</c> @0x471860 as the light's node -- one field,
    /// whichever subclass of node the part instantiates.</summary>
    public static readonly int PartNodeLink = Field<CX.Engine.MdlNode>("Part", "node");
    /// <summary>Part -&gt; uniform scale, the third term <c>Part::GetWorld</c> composes beside
    /// <see cref="PartPos"/> and <see cref="PartRot"/>. Paired directly on <c>Part::PostProcess</c>.</summary>
    public static readonly int PartScale = Field<float>("Part", "scale");
    /// <summary>Part -&gt; the draw gate <c>Gob::PartDraw</c> reads before descending. Inside Part's measured
    /// identical run, not paired on its own.</summary>
    public static readonly int PartVisible = Field<byte>("Part", "visible");

    // ---- PartBeam (its whole Draw is two reads, and they pair at delta 0) ----

    /// <summary>The only two fields <c>PartBeam::Draw</c> @0x450120 reads -- the body is
    /// <c>RenderVertexData(part+0xa0, part+0xa8, 4, 0xffffffff)</c>, so the beam capture needs no funnel arm.
    /// One validated pair maps both offsets at delta 0: PartBeam does NOT inherit PartTriMesh's +0x8,
    /// because a class verdict does not travel to a sibling.</summary>
    public static readonly int BeamVdo = Field<IntPtr>("PartBeam", "vdo");
    /// <inheritdoc cref="BeamVdo"/>
    public static readonly int BeamItem = Field<int>("PartBeam", "item");

    // ---- PartTrigger (CX-83: the area-transition highlight). Every row pairs at delta 0, read off
    //      PartTrigger::SetHighlighted (Linux @0x4da090 / Windows rva 0xf0940), SetHighlightColor
    //      (@0x4d9ff0 / rva 0xf0910) and Scene::DoHighlightTriggers (@0x462310 / rva 0xc8930).

    /// <summary>The trigger's own 2D outline: a <c>CExoArrayList&lt;Vector&gt;</c>, points at +0x90 and the
    /// count at +0x98. Built once in <c>PartTrigger::PartTrigger</c> @0x4d9dd0 from the MDL node;
    /// <c>SetHighlighted</c> loops the count over the data to extrude the highlight volume, which is the pair
    /// both offsets were read on.</summary>
    public static readonly int TriggerPoints = Field<IntPtr>("PartTrigger", "points");
    /// <inheritdoc cref="TriggerPoints"/>
    public static readonly int TriggerPointCount = Field<int>("PartTrigger", "pointCount");
    /// <summary>"This trigger is highlighted" and the HALF of the bucket it belongs to -- the two bytes
    /// <c>DoHighlightTriggers</c> filters on (<c>+0xd8 != 0 &amp;&amp; half == +0xe0</c>). Both are written by
    /// <c>SetHighlighted</c>, whose only caller chain passes <c>true, true</c> on every branch, DM
    /// included.</summary>
    public static readonly int TriggerHighlighted = Field<byte>("PartTrigger", "highlighted");
    /// <inheritdoc cref="TriggerHighlighted"/>
    public static readonly int TriggerHalf = Field<byte>("PartTrigger", "half");
    /// <summary>Half the height the outline is extruded by, in world units: <c>SetHighlighted</c> stores
    /// <c>abs(param)</c> here and builds the volume at <c>z ± this</c>.</summary>
    public static readonly int TriggerHeight = Field<float>("PartTrigger", "height");
    /// <summary>The highlight colour, RGB then ALPHA -- four contiguous floats
    /// <c>PartTrigger::SetHighlightColor</c> writes and <c>DoHighlightTriggers</c> hands to
    /// <c>SetColorv</c>. The alpha is the gate: <c>UpdateTriggerColor</c> writes 0.0 for a trigger that is
    /// not under the cursor, which is how the engine draws the rest of the bucket invisibly.</summary>
    public static readonly int TriggerColour = Field<float>("PartTrigger", "highlightColour", count: 4);
    /// <summary>The prism's triangles as <c>Vector</c> triples (<c>Decompose</c>'s output) and how many VERTICES
    /// there are: <c>PartTrigger::HitCheckGeom</c> (Linux nm 0x4d9cb0, Windows rva 0xf06d0) walks +0x100 in 0x24-byte
    /// steps while the index is below +0x108, on both builds.</summary>
    public static readonly int TriggerTris = Field<IntPtr>("PartTrigger", "tris");
    /// <inheritdoc cref="TriggerTris"/>
    public static readonly int TriggerTriVerts = Field<int>("PartTrigger", "triVerts");
    /// <summary>What a hit reports at <c>CHitInfo+0x70</c> -- <c>AurPartTriggerCreate</c>'s last argument, stored at
    /// +0xd0 and copied by <c>HitCheckGeom</c> on both builds (the same two functions as above).</summary>
    public static readonly int TriggerPayload = Field<IntPtr>("PartTrigger", "payload");

    // ---- PartLight: one more field in the +0x4 block ----

    /// <summary>The lens-flare occlusion fade -- NOT <see cref="LightFade"/>, the PartLight weight: two
    /// distinct floats, both stored by the <c>PartLight</c> constructor @0x43d1d0.
    /// <c>Scene::RenderLensFlares</c> @0x471860 integrates this one toward 1.0 while the light is unoccluded
    /// and multiplies the flare colour by it. Windows by the class's uniform +0x4.</summary>
    public static readonly int LightFlareFade = Field<float>("PartLight", "flareFade");

    // ---- PartEmitter: four segments and a sign change (#430's worst case, now measured) ----
    // Anchored on eight function pairs (RenderEmitter, RenderNothing, GetTileAlpha, chkRandom,
    // chkParticleLife, moveParticle, moveParticleP2PGrav, moveParticleP2PBezier):
    //   0x00..0x50  delta 0     0x64..0xd8  +0x4     0xf8..0x158  +0x8
    //   0x188..0x1a8 delta 0    0x1c0 and up  -0x8
    // The -0x8 is corroborated by the object's own end: moveParticle pairs 0x1f8 -> 0x1f0.

    /// <summary>Blend mode: 1 = punch-through, 0 = normal alpha, anything else = additive.</summary>
    public static readonly int EmBlend = Field<int>("PartEmitter", "blend");
    /// <summary>Alpha at the end and the start of life (in that order in memory).</summary>
    public static readonly int EmAlphaEnd = Field<float>("PartEmitter", "alphaEnd");
    /// <inheritdoc cref="EmAlphaEnd"/>
    public static readonly int EmAlphaStart = Field<float>("PartEmitter", "alphaStart");
    /// <summary>Colour at the end and the start of life, three floats each.</summary>
    public static readonly int EmColorEnd = Field<System.Numerics.Vector3>("PartEmitter", "colorEnd");
    /// <inheritdoc cref="EmColorEnd"/>
    public static readonly int EmColorStart = Field<System.Numerics.Vector3>("PartEmitter", "colorStart");
    /// <summary>UV-grid animation: frames per second, then the end and start frame indices. A zero fps or
    /// equal ends means the frame index never advances.</summary>
    public static readonly int EmFps = Field<float>("PartEmitter", "fps");
    /// <inheritdoc cref="EmFps"/>
    public static readonly int EmFrameEnd = Field<float>("PartEmitter", "frameEnd");
    /// <inheritdoc cref="EmFps"/>
    public static readonly int EmFrameStart = Field<float>("PartEmitter", "frameStart");
    /// <summary>Particle lifetime. Zero draws nothing at all -- the engine's own early-out.</summary>
    public static readonly int EmLife = Field<float>("PartEmitter", "life");
    /// <summary>Quad half-extents before the 0.5 and the Gob scale: X at start/end, then Y at start/end.
    /// All four move together in <c>RenderEmitter</c>'s own read run.</summary>
    public static readonly int EmSizeStartX = Field<float>("PartEmitter", "sizeStartX");
    /// <inheritdoc cref="EmSizeStartX"/>
    public static readonly int EmSizeEndX = Field<float>("PartEmitter", "sizeEndX");
    /// <inheritdoc cref="EmSizeStartX"/>
    public static readonly int EmSizeStartY = Field<float>("PartEmitter", "sizeStartY");
    /// <inheritdoc cref="EmSizeStartX"/>
    public static readonly int EmSizeEndY = Field<float>("PartEmitter", "sizeEndY");
    /// <summary>The second fade owner the draw early-out tests, beside the Gob's own
    /// <see cref="GobTileData"/>. The only witness of the +0x8 segment's low end, and it has three
    /// (GetTileAlpha, RenderNothing, RenderEmitter all pair 0xf8 -&gt; 0x100).</summary>
    public static readonly int EmTileOwner = Field<CX.Engine.TileData>("PartEmitter", "tileOwner");
    /// <summary><c>Particle**</c>, and the count. The array is dense: index i is drawn as quad i.</summary>
    public static readonly int EmParts = Field<IntPtr>("PartEmitter", "parts");
    /// <inheritdoc cref="EmParts"/>
    public static readonly int EmCount = Field<int>("PartEmitter", "count");
    /// <summary>Render type -- which billboard basis the quads get.</summary>
    public static readonly int EmType = Field<int>("PartEmitter", "type");
    /// <summary>The Material whose slot-0 texture every quad samples. First field of the -0x8 segment.</summary>
    public static readonly int EmMaterial = Field<CX.Engine.Material>("PartEmitter", "material");
    /// <summary>Interpolation domain (a SHORT, not an int): 0 interpolates over the particle's INDEX in the
    /// array, non-zero over its age. Reading it as an int folds in the two bytes above and turns every
    /// index-interpolated emitter into an age-interpolated one.</summary>
    public static readonly int EmLifeInterp = Field<short>("PartEmitter", "lifeInterp");
    /// <summary>The emitter's own orientation quaternion, stored (w, x, y, z).</summary>
    public static readonly int EmOrient = Field<CX.Engine.Quat>("PartEmitter", "orient");
    /// <summary>Emitter world position, the point the particle tint is sampled at.</summary>
    public static readonly int EmPos = Field<System.Numerics.Vector3>("PartEmitter", "pos");

    // ---- CX-86: the simulation's own fields. Read off the four Update overrides and the three movers
    //      (FountainEmitter::Update @0x4b1c70, SingleEmitter::Update @0x4b2740, ExplosionEmitter::Update
    //      @0x4b23a0, LightningEmitter::Update @0x4b29b0, PartEmitter::moveParticle @0x4acaa0,
    //      moveParticleP2PBezier @0x4b0230, moveParticleP2PGrav @0x4b0d40, the standard mover's body
    //      @0x4afa90, PartEmitter::Initialize @0x4aaaa0, AnimateParticles @0x4ac660, RecursiveFractal
    //      @0x4acda0, Particle::initialize @0x4abbf0), each paired against the same function on nwmain.exe
    //      with tools/ghidra/FieldDump.java. Three rows (randVel, spread, velocity) have no pair of their
    //      own -- only `Particle::initialize` reads them, and there `this` is the particle, so the emitter
    //      offset never enters a slice from parameter 0. They are BRACKETED instead: each sits between two
    //      measured +0x4 pairs on either side inside the class's one +0x4 run, which the run's own witnesses
    //      pin at 0x64 (blend) below and 0xf0 (zigZag) above.

    /// <summary>Particles born per second. Read as an int by every update mode, so 0.5 births none and the
    /// whole spawn block is skipped at zero.</summary>
    public static readonly int EmBirthRate = Field<float>("PartEmitter", "birthRate");
    /// <summary>Restitution. Zero stops a bouncing particle dead instead of reflecting it.</summary>
    public static readonly int EmBounce = Field<float>("PartEmitter", "bounce");
    /// <summary>p2p bezier only: the exponent that blends the ballistic position into the curve
    /// (<c>pow(t³, combineTime)</c>).</summary>
    public static readonly int EmCombineTime = Field<float>("PartEmitter", "combineTime");
    /// <summary>p2p gravity only: per-second velocity retention, applied as <c>pow(drag, dt)</c>, and the
    /// acceleration toward the target.</summary>
    public static readonly int EmP2PDrag = Field<float>("PartEmitter", "p2pDrag");
    /// <inheritdoc cref="EmP2PDrag"/>
    public static readonly int EmP2PGrav = Field<float>("PartEmitter", "p2pGrav");
    /// <summary>Mass, as the engine uses it: the only thing it multiplies is <c>-9.81</c>, so it is a gravity
    /// scale and a zero means weightless.</summary>
    public static readonly int EmGravity = Field<float>("PartEmitter", "gravity");
    /// <summary>p2p bezier control-point lengths, along the emitter's own Z axis and the target's.</summary>
    public static readonly int EmBezier2 = Field<float>("PartEmitter", "bezier2");
    /// <inheritdoc cref="EmBezier2"/>
    public static readonly int EmBezier3 = Field<float>("PartEmitter", "bezier3");
    /// <summary>Roll rate, radians per second, wrapped to ±2π every step.</summary>
    public static readonly int EmSpin = Field<float>("PartEmitter", "spin");
    /// <summary>Spawn velocity: the base speed along the particle's own Z axis, and the ± window the birth
    /// randomises it inside (a hundredth of a unit per <c>rand()</c> tick).</summary>
    public static readonly int EmVelocity = Field<float>("PartEmitter", "velocity");
    /// <inheritdoc cref="EmVelocity"/>
    public static readonly int EmRandVel = Field<float>("PartEmitter", "randVel");
    /// <summary>Emission cone, in radians. Zero fires every particle straight along the emitter's Z.</summary>
    public static readonly int EmSpread = Field<float>("PartEmitter", "spread");
    /// <summary>p2p only: the arrival radius. Inside it the particle is aged out to <see cref="EmLife"/>.
    /// Compared squared.</summary>
    public static readonly int EmDeadSpace = Field<float>("PartEmitter", "deadSpace");
    /// <summary>Spawn-area half-extents in the emitter's local X and Y. Both zero spawns at the origin.
    /// </summary>
    public static readonly int EmSpawnX = Field<float>("PartEmitter", "spawnX");
    /// <inheritdoc cref="EmSpawnX"/>
    public static readonly int EmSpawnY = Field<float>("PartEmitter", "spawnY");
    /// <summary>Render type 6's streak length, as the TIME the previous position lags the current one by:
    /// the lerp rate is <c>min(dt, blurLength) / blurLength</c>.</summary>
    public static readonly int EmBlurLength = Field<float>("PartEmitter", "blurLength");
    /// <summary>Lightning: seconds between re-fractals, the midpoint-displacement radius, and the
    /// per-segment zigzag amplitude.</summary>
    public static readonly int EmBoltDelay = Field<float>("PartEmitter", "boltDelay");
    /// <inheritdoc cref="EmBoltDelay"/>
    public static readonly int EmBoltRadius = Field<float>("PartEmitter", "boltRadius");
    /// <inheritdoc cref="EmBoltDelay"/>
    public static readonly int EmZigZag = Field<float>("PartEmitter", "zigZag");
    /// <summary>The detonation request, set by the <c>Detonate</c> gob callback
    /// <c>MdlNodeEmitter::InternalCreateInstance</c> @0x4af5c0 registers and cleared by the explosion step.
    /// Alloy owns the step, so Alloy has to clear it -- left set, an explosion re-fires every frame.</summary>
    public static readonly int EmDetonate = Field<byte>("PartEmitter", "detonate");
    /// <summary>The emitter transform as the PREVIOUS step left it -- the origin an <c>inherit_local</c> or
    /// <c>inherit_part</c> emitter measures its own motion against. Written at the end of every step.</summary>
    public static readonly int EmPrevWorldPos = Field<System.Numerics.Vector3>("PartEmitter", "prevWorldPos");
    /// <inheritdoc cref="EmPrevWorldPos"/>
    public static readonly int EmPrevWorldRot = Field<CX.Engine.Quat>("PartEmitter", "prevWorldRot");
    /// <summary>The emitter's orientation as a 3x3 basis, nine floats COLUMN-major: X axis, then Y, then Z.
    /// Rebuilt from <see cref="EmOrient"/> at the top of every step, and it is the frame spawn works in --
    /// the spawn rectangle lies in XY and the launch direction is Z.</summary>
    public static readonly int EmBasis = Field<float>("PartEmitter", "basis", count: 9);
    /// <summary>p2p fountain only: the fraction of a particle's spacing the last spawn did not consume, so a
    /// chain stays evenly spaced across frames of different length.</summary>
    public static readonly int EmP2PLeftover = Field<System.Numerics.Vector3>("PartEmitter", "p2pLeftover");
    /// <summary>The emitter transform the PARTICLES were last expressed against, for
    /// <c>inherit</c> (world-space particles rebased into the emitter's new frame). Distinct from
    /// <see cref="EmPrevWorldPos"/>, which tracks the OWNER's motion.</summary>
    public static readonly int EmPrevRot = Field<CX.Engine.Quat>("PartEmitter", "prevRot");
    /// <inheritdoc cref="EmPrevRot"/>
    public static readonly int EmPrevPos = Field<System.Numerics.Vector3>("PartEmitter", "prevPos");
    /// <summary>Fractional births carried between frames. Without it a 3/s emitter at 60fps never births.
    /// </summary>
    public static readonly int EmSpawnAcc = Field<float>("PartEmitter", "spawnAcc");
    /// <summary>The p2p target, set by <c>SetEmitterTarget</c> @0x4aa7d0 from the model's
    /// <c>_EmitterTarget</c> attachment. Null means the emitter has no target yet and p2p emits nothing.
    /// </summary>
    public static readonly int EmP2PTarget = Field<CX.Engine.Gob>("PartEmitter", "p2pTarget");
    /// <summary>One float, two meanings by mode: the explosion's already-fired latch (1.0 = fired, reset to
    /// 0 when the detonate byte clears) and the lightning's seconds-since-last-fractal clock.</summary>
    public static readonly int EmModeClock = Field<float>("PartEmitter", "modeClock");
    /// <summary>Has the emitter's inherit basis been seeded yet? Zero on the first step after
    /// <c>Initialize</c>, which is what makes a freshly spawned inheriting emitter measure no motion.
    /// </summary>
    public static readonly int EmSeeded = Field<byte>("PartEmitter", "seeded");
    /// <summary>Is the emitter currently birthing (birthRate &gt; 0)? The fountain writes it and the p2p
    /// spawn path reads it to decide whether to keep laying a chain.</summary>
    public static readonly int EmBirthing = Field<byte>("PartEmitter", "birthing");

    // ---- LightningEmitter: two vectors of its own, past PartEmitter's end ----
    // Pinned by aligning the two Update functions' access ORDER rather than by set membership: the last
    // eleven accesses of LightningEmitter::Update @0x4b29b0 line up one for one with nwmain.exe's, and the
    // pairing they give is -0x4, not PartEmitter's -0x8.

    /// <summary>The emitter-to-target delta and the emitter position the last fractal was built at. The
    /// in-between frames rigid-transform the existing bolt by the change instead of re-fracturing it.
    /// </summary>
    public static readonly int BoltLastDelta = Field<System.Numerics.Vector3>("LightningEmitter", "lastDelta");
    /// <inheritdoc cref="BoltLastDelta"/>
    public static readonly int BoltLastPos = Field<System.Numerics.Vector3>("LightningEmitter", "lastPos");

    // ---- Particle (identical 0x0..0x77) ----
    // Two anchors, and both are exact: Particle::Particle touches the same thirteen offsets on both builds,
    // and Particle::initialize the same twenty-two -- including every offset below.

    /// <summary>Position, and the position the previous simulation step left. The previous one is only read
    /// by the motion-blurred type, where it is the far edge of a stretched quad.</summary>
    public static readonly int ParticlePos = Field<System.Numerics.Vector3>("Particle", "pos");
    /// <inheritdoc cref="ParticlePos"/>
    public static readonly int ParticlePrevPos = Field<System.Numerics.Vector3>("Particle", "prevPos");
    /// <summary>Roll about the billboard normal, in radians (the engine feeds it to <c>sincosf</c> after
    /// halving, i.e. as a quaternion half-angle).</summary>
    public static readonly int ParticleRoll = Field<float>("Particle", "roll");
    /// <summary>The particle's own orientation quaternion, (w, x, y, z) -- render type 5's whole basis.</summary>
    public static readonly int ParticleQuat = Field<CX.Engine.Quat>("Particle", "quat");
    /// <summary>UV-grid frame index, and the age at which it last advanced. Both are WRITTEN by the vertex
    /// build: owning the build means owning that advance, or every animated particle freezes.</summary>
    public static readonly int ParticleFrame = Field<int>("Particle", "frame");
    /// <inheritdoc cref="ParticleFrame"/>
    public static readonly int ParticleFrameTime = Field<float>("Particle", "frameTime");
    /// <summary>The splat normal, used as the billboard's reference axis on a splat emitter.</summary>
    public static readonly int ParticleSplatNormal = Field<System.Numerics.Vector3>("Particle", "splatNormal");
    /// <summary>Splat flag (one byte) and age.</summary>
    public static readonly int ParticleSplat = Field<byte>("Particle", "splat");
    /// <inheritdoc cref="ParticleSplat"/>
    public static readonly int ParticleAge = Field<float>("Particle", "age");
    /// <summary>The three fields only the SIMULATION touches -- velocity, the inherited velocity a
    /// spawn snapshot of the emitter's own motion leaves, and the scalar that decays it toward zero at
    /// <c>(1 - dt)</c> per step. All three pair at delta 0 on <c>Particle::initialize</c> @0x4abbf0 and
    /// <c>Particle::inheritVelocity</c> @0x4ab950, inside the class's identical 0x0..0x77 run.</summary>
    public static readonly int ParticleVel = Field<System.Numerics.Vector3>("Particle", "vel");
    /// <inheritdoc cref="ParticleVel"/>
    public static readonly int ParticleInhVel = Field<System.Numerics.Vector3>("Particle", "inhVel");
    /// <inheritdoc cref="ParticleVel"/>
    public static readonly int ParticleInhDecay = Field<float>("Particle", "inhDecay");
    /// <summary><c>sizeof(Particle)</c>, which is what lets <see cref="CX.Alloy.EmitterSim"/>'s pool
    /// hand <c>Particles</c> the same <c>Particle*</c> array the engine used to. <c>operator new(0x78)</c> in
    /// every one of the four spawn sites on Linux, and on Windows <c>ChunkyParticle</c>'s own field lands at
    /// 0x78, which puts the base class's end there too.</summary>
    public static readonly int ParticleStride = Field("Particle", "stride");

    // ---- MdlNodeEmitter (a +0x4 segment, and a boundary nobody has pinned) ----

    /// <summary>UV grid: columns then rows. Zero means one, and a grid of 1x1 skips the animation entirely.
    /// <para>Windows from the constructor, which pairs 0xa4 -&gt; 0xa8 and 0xb0 -&gt; 0xb4 and so brackets
    /// both of these inside one +0x4 run; every one of the class's 22 Enable/Is accessors pairs its flags
    /// word 0x170 -&gt; 0x174, which is the same +0x4 higher up.</para></summary>
    public static readonly int EmitterGridX = Field<int>("MdlNodeEmitter", "gridX");
    /// <inheritdoc cref="EmitterGridX"/>
    public static readonly int EmitterGridY = Field<int>("MdlNodeEmitter", "gridY");
    /// <summary>Non-zero DISABLES back-face culling for the emitter. Two readers pin it past the
    /// constructor's ambiguity: <c>PartEmitter::RenderEmitter</c> (@0x4ad4c0 / rva 0x62900) and
    /// <c>RenderStandard</c> (@0x4af4c0 / rva 0x643b0) both branch on it, at +0x4 on Windows like its
    /// neighbours.</summary>
    public static readonly int EmitterNoCull = Field<int>("MdlNodeEmitter", "noCull");

    // ---- CX-86: the node's own simulation declarations. The class diverges by +0x4 from 0x9c up (the
    //      constructor @0x43b950 pairs 0x9c/0xa0 as one 8-byte store against nwmain.exe's 0xa0/0xa4 and
    //      0xa4 against 0xa8; every one of its 22 Is*/Enable* accessors pairs the flags word 0x170 -> 0x174),
    //      and Δ0 at 0x98 and below -- so the divergence STARTS at 0x9c, which is why coneCap is +0x4 and
    //      the field below it is not.

    /// <summary>The emission-cone cap <c>Particle::randomDirection</c> @0x4ab160 clamps against: while the
    /// launch direction's dot with the view direction is outside <c>cos(π/2 - coneCap)</c>, it is rotated
    /// back. Zero skips the clamp.</summary>
    public static readonly int EmitterConeCap = Field<float>("MdlNodeEmitter", "coneCap");
    /// <summary>Explosion only: the point-source wind a detonation pushes into <c>WindManager</c>, as its
    /// strength and its falloff. A zero strength means the detonation moves no air.</summary>
    public static readonly int EmitterWindStrength = Field<float>("MdlNodeEmitter", "windStrength");
    /// <inheritdoc cref="EmitterWindStrength"/>
    public static readonly int EmitterWindFalloff = Field<float>("MdlNodeEmitter", "windFalloff");
    /// <summary>The <c>update</c> declaration, as the model's own literal string --
    /// <c>MdlNodeEmitter::InternalCreateInstance</c> @0x4af5c0 string-compares it to pick which
    /// <c>PartEmitter</c> subclass to build. Alloy reads the same string rather than the instance's vtable,
    /// because the Windows image does not export vftables.</summary>
    public static readonly int EmitterUpdate = Field<byte>("MdlNodeEmitter", "update", count: 16);
    /// <summary>The <c>chunk</c> model resref. A non-empty one makes every particle a <c>ChunkyParticle</c>
    /// holding a real <c>CAurObject</c>, and makes the emitter's own draw <c>RenderNothing</c> @0x4ad200 --
    /// so it is the flag that says "these particles are scene objects, not Alloy's".</summary>
    public static readonly int EmitterChunkModel = Field<byte>("MdlNodeEmitter", "chunkModel", count: 16);
    /// <summary>Single only: non-zero keeps re-seeding the one particle when its life runs out instead of
    /// freezing it at age zero.</summary>
    public static readonly int EmitterLoop = Field<int>("MdlNodeEmitter", "loop");
    /// <summary>The whole emitter flag word. Every <c>MdlNodeEmitter::Is*</c> accessor is one bit of it:
    /// 0 p2p, 1 p2p_sel, 2 affectedByWind, 3 tinted, 4 bouncing, 5 random, 6 inherit, 7 inherit_vel,
    /// 8 inherit_local, 9 splat, 10 inherit_part. Read as a word rather than through eleven calls, because
    /// the simulation wants all of them per emitter per frame.</summary>
    public static readonly int EmitterFlags = Field<int>("MdlNodeEmitter", "flags");
    /// <summary>The emitter's declared render order -- a SHORT, and the tiebreak the engine's emitter sort
    /// uses once two emitters tie on camera distance AND share a Gob, so two emitters of one model at one
    /// point composite in the order the model declared. Two witnesses, needed because it is the class's only
    /// 2-byte member, and <see cref="EmitterChunkModel"/> and <see cref="EmitterFlags"/> bracket it.</summary>
    public static readonly int EmitterRenderOrder = Field<short>("MdlNodeEmitter", "renderOrder");

    // ---- MdlNodeLight: the lens-flare declaration (no validated pair on either build) ----

    /// <summary>The light node's flare parameters: <c>flareradius</c>, the flare count, the
    /// <c>flaresizes</c> float array and the <c>flarecolorshifts</c> vec3 array.
    /// <para><c>Scene::RenderLensFlares</c> (@0x471860 / rva 0xcea90) reads all four on both builds, each
    /// pinned by its arithmetic; the class diverges in two segments, the radius +0x4 and the rest +0x8.</para></summary>
    public static readonly int FlareRadius = Field<float>("MdlNodeLight", "flareRadius");
    /// <inheritdoc cref="FlareRadius"/>
    public static readonly int FlareCount = Field<int>("MdlNodeLight", "flareCount");
    /// <inheritdoc cref="FlareRadius"/>
    public static readonly int FlareSizes = Field<IntPtr>("MdlNodeLight", "flareSizes");
    /// <inheritdoc cref="FlareRadius"/>
    public static readonly int FlareShifts = Field<IntPtr>("MdlNodeLight", "flareShifts");
    /// <summary>The MDL light node's <c>fadingLight</c> field, parsed by
    /// <c>MdlNodeLight::InternalParseField</c> @0x44baf0 and defaulted to 1. Zero means the light reaches full
    /// weight the instant it becomes wanted instead of ramping -- the one branch in
    /// <c>ControlLightsFade</c>'s add path, where the Windows value (rva 0x7ddb0) was read.</summary>
    public static readonly int LightFadingLight = Field<int>("MdlNodeLight", "fadingLight");

    // ---- MdlNodeTriMesh: the faces the grass binner reads ----

    /// <summary>MdlNodeTriMesh -&gt; faces (items +0xb0, count +0xb8), the array <c>Scene::AddFacesToBins</c>
    /// @0x460ef0 and <c>CAurTriangleList::AddTriangle</c> @0x4b5650 bin the engine's own grass from.
    /// <c>InternalGenVertices</c> pairs both offsets at delta 0, and <c>BuildVertexIndices</c> agrees.</summary>
    public static readonly int MeshFaces = Field<IntPtr>("MdlNodeTriMesh", "faces");
    /// <inheritdoc cref="MeshFaces"/>
    public static readonly int MeshFaceCount = Field<int>("MdlNodeTriMesh", "faceCount");

    /// <summary>One <c>MaxFace</c> record: material index at +0x10, the three vertex indices as u16 at
    /// +0x1a/+0x1c/+0x1e, 0x20 bytes a record. A LAYOUT with no functions of its own, so it is read on the
    /// CALLER: <c>MdlNodeTriMesh::BuildVertexIndices</c> (@0x455af0 / rva 0xa5e20) walks the array in those
    /// steps on both builds, and the stride is a SIZE, never carried over.</summary>
    public static readonly int FaceStride = Field("MaxFace", "stride");
    /// <inheritdoc cref="FaceStride"/>
    public static readonly int FaceVertA = Field<ushort>("MaxFace", "vertA");
    /// <inheritdoc cref="FaceStride"/>
    public static readonly int FaceVertB = Field<ushort>("MaxFace", "vertB");
    /// <inheritdoc cref="FaceStride"/>
    public static readonly int FaceVertC = Field<ushort>("MaxFace", "vertC");
    /// <summary>Stride of the host-side shadow of a vertex pool: positions 12 bytes apart. Same kind of
    /// unowned record as <see cref="FaceStride"/>, read the same way -- on the caller. The tangent-basis
    /// worker (Linux @0x456190, Windows inlined into rva 0xa6130) indexes the pool by it and resizes to
    /// twelve bytes a vertex on both builds.</summary>
    public static readonly int PoolPosStride = Field("VertexPool", "stride");
    /// <inheritdoc cref="PoolPosStride"/>
    public static readonly int PoolPos = Field<System.Numerics.Vector3>("VertexPool", "pos");

    // ---- Scene: four more spans inside the identical 0x0..0x1e0 run (#436) ----

    /// <summary>The skybox state <c>Scene::RenderSkyBoxes</c> @0x466910, <c>SetSkyBox</c> @0x45e260 and
    /// <c>SetSkyAlpha</c> @0x45f100 share: four skybox gobs from +0x18, the cross-fade gob at +0x38, the fade
    /// parameter t at +0x40, the current and desired slot indices (-1 = none) at +0x44/+0x48, and the
    /// "skybox off" byte at +0x4c that <c>TurnOffSkyBox</c> sets.</summary>
    public static readonly int SceneSkyGobs = Field<CX.Engine.Gob>("Scene", "skyGobs", count: 4);
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyFadeGob = Field<CX.Engine.Gob>("Scene", "skyFadeGob");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyT = Field<float>("Scene", "skyT");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyCur = Field<int>("Scene", "skyCur");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyDesired = Field<int>("Scene", "skyDesired");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyOff = Field<byte>("Scene", "skyOff");
    /// <summary>Where <c>RenderSkyBoxes</c> parks every sky gob: <c>Gob::SetPosition(Scene+0x190)</c>, a
    /// <c>Vector</c>, called once per gob per frame right before <c>Gob::Render</c>. Paired twice.</summary>
    public static readonly int SceneSkyPos = Field<System.Numerics.Vector3>("Scene", "skyPos");
    /// <summary>Scene -&gt; the light a shadow view is currently being baked for.</summary>
    public static readonly int SceneCurrentLight = Field<CX.Engine.PartLight>("Scene", "currentLight");
    /// <summary>The scene's environment map, a <c>SafePointer&lt;CAurTexture&gt;</c> whose first word is the
    /// borrowed texture. <c>Scene::Render</c> (@0x4762d0 / rva 0xcc730) fills it once per scene from the
    /// TILESET's own resref and falls back to <c>AurTextureGetFallbackEnvironmentMap</c> when the tileset
    /// names none. Same offset on both builds, where <c>RenderFlat</c> also binds it.</summary>
    public static readonly int SceneEnvMap = Field<CX.Engine.CAurTexture>("Scene", "envMap");

    // ---- Gob: four more fields and one vtable slot ----

    /// <summary>Gob -&gt; its cached bounding box, min at +0x160 and max at +0x16c.
    /// <c>Gob::GetBoundingBox</c> @0x436ee0 is a plain read of exactly these two, no recompute, and it pairs
    /// at delta 0. Only the EXTENT is used (the pair's space is not established, and the extent is the same
    /// either way); it is centred on <see cref="GobPosition"/>, which is unambiguously world.</summary>
    public static readonly int GobBoundsMin = Field<System.Numerics.Vector3>("Gob", "boundsMin");
    /// <inheritdoc cref="GobBoundsMin"/>
    public static readonly int GobBoundsMax = Field<System.Numerics.Vector3>("Gob", "boundsMax");
    /// <summary>The "visible" byte <c>Gob::Render</c> gates on.</summary>
    public static readonly int GobVisible = Field<byte>("Gob", "visible");
    /// <summary>Gob -&gt; the attachment link whose chain multiplies a further lerped scale into the gob's
    /// own. Paired three times at delta 0, and the highest Gob field measured.</summary>
    public static readonly int GobAttach = Field<CX.Engine.CAurBehavior>("Gob", "attach");
    /// <summary>CAurBehavior -&gt; the SafePointer&lt;CAurObject&gt; naming the object the behaviour hangs its gob
    /// off: the parent for the five CAurBehaviorAttach* classes (body parts, held items), the follow target
    /// for the camera ones, zero for Navigate. The raw pointer is the SafePointer's first word, and both
    /// constructors write it at the same offset (Linux @0x5a2d50, Windows @0x140053b30).</summary>
    public static readonly int BehaviorTarget = Field<CX.Engine.Gob>("CAurBehavior", "target");
    /// <summary><c>Gob::GetAdjustedScale() const</c> -- the scale every emitter size term is multiplied by.
    /// Printed on both sides by <c>tools/abi/vtable-slots.py Gob</c>; -8, like every other slot.</summary>
    public static readonly int GobAdjustedScaleSlot =
        Field("Gob", "vtbl.GetAdjustedScale", Expect.VtblSlot, sig: "float()");
    /// <summary><c>Gob::GetOrientation()</c> / <c>GetPosition()</c>, the p2p target's transform, and
    /// <c>GetAdjustedPosition()</c> / <c>GetAdjustedOrientation()</c>, the highlight glow's root transform.
    /// No signature is declared: SysV returns both in registers while Win64 returns them through a caller
    /// buffer, so <see cref="CX.Alloy.AurXform"/> carries both argument lists.</summary>
    public static readonly int GobGetOrientationSlot = Field("Gob", "vtbl.GetOrientation", Expect.VtblSlot);
    /// <inheritdoc cref="GobGetOrientationSlot"/>
    public static readonly int GobGetPositionSlot =
        Field("Gob", "vtbl.GetPosition", Expect.VtblSlot);
    /// <inheritdoc cref="GobGetOrientationSlot"/>
    public static readonly int GobGetAdjustedPositionSlot = Field("Gob", "vtbl.GetAdjustedPosition", Expect.VtblSlot);
    /// <inheritdoc cref="GobGetOrientationSlot"/>
    public static readonly int GobGetAdjustedOrientationSlot = Field("Gob", "vtbl.GetAdjustedOrientation", Expect.VtblSlot);
    /// <summary><c>Gob::IsForceAnimated()</c> -- the escape hatch <c>Scene::DoEmitterBucket</c>
    /// @0x476f00 tests before it skips a non-animating scene's emitters, so a forced-animation gob keeps
    /// simulating while the rest of the scene holds. Printed on both sides: 0x2a8 / 0x2a0.</summary>
    public static readonly int GobIsForceAnimatedSlot =
        Field("Gob", "vtbl.IsForceAnimated", Expect.VtblSlot, sig: "byte()");

    // ---- TileData: three fields the constructor pairs exactly ----
    // TileData::TileData writes 0x0..0xcb on both builds and agrees store for store outside 0x58..0x7c,
    // where MSVC splits three of Linux's 8-byte stores. That murky window is exactly where #436 left
    // TileFade/TilePendingFade unresolved, and it is why these three are resolvable and those two are not.

    /// <summary>TileData -&gt; its surviving parts (<c>CExoArrayList&lt;Part*&gt;</c>, items +0x30, count
    /// +0x38): the array <c>Scene::SpawnTile</c> @0x474470 walks and <c>ProcessStaticParts</c> @0x470970
    /// fills.</summary>
    public static readonly int TileParts = Field<IntPtr>("TileData", "parts");
    /// <inheritdoc cref="TileParts"/>
    public static readonly int TilePartCount = Field<int>("TileData", "partCount");
    /// <summary>The fade factor <c>PartEmitter::RenderEmitter</c> @0x4ad4c0 declines on: below 1.0 the engine
    /// draws the emitter not at all. Reached through <see cref="GobTileData"/> or
    /// <see cref="EmTileOwner"/>. A THIRD float, distinct from <see cref="TileFade"/> and
    /// <see cref="TilePendingFade"/>, which <c>HandleFadingInBucket</c> @0x47f670 drives.</summary>
    public static readonly int TileGeomFade = Field<float>("TileData", "geomFade");
    /// <summary>The fog-of-war flag <c>AuroraSetTileFoggedByWar</c> @0x453320 writes (one byte, with
    /// <see cref="TileGeomFade"/> zeroed beside it); while set, the @0x481000 visitor holds the ramp at 0.
    /// Alloy reads the ramp, not the flag: the flag has no expression the ramp does not already carry.
    /// Same on Windows by the ctor pairing (identical stores over 0x7c..0xcb).</summary>
    public static readonly int TileFoggedByWar = Field<byte>("TileData", "foggedByWar");

    // ---- CX-116: TileData's four NAMED tile parts ----

    /// <summary>TileData -&gt; the <c>ml1</c> / <c>ml2</c> lights and the <c>sl1</c> / <c>sl2</c> parts,
    /// which <c>ProcessTilePart</c> @0x561720 fills by matching each tile part's node name against those four
    /// suffixes. <c>Scene::SetTileLights</c> re-colours the lights and <c>SetSingleTileFlame</c> reads the
    /// parts for where a flame stands; they are the object's first four words.</summary>
    public static readonly int TileMainLight1 = Field<CX.Engine.PartLight>("TileData", "mainLight1");
    /// <inheritdoc cref="TileMainLight1"/>
    public static readonly int TileMainLight2 = Field<CX.Engine.PartLight>("TileData", "mainLight2");
    /// <inheritdoc cref="TileMainLight1"/>
    public static readonly int TileSourcePart1 = Field<IntPtr>("TileData", "sourcePart1");
    /// <inheritdoc cref="TileMainLight1"/>
    public static readonly int TileSourcePart2 = Field<IntPtr>("TileData", "sourcePart2");

    // ---- CX-104 buckets: Alloy's own visibility sets, in place of the engine's per-frame buckets ----

    /// <summary><c>Part::AsPartEmitter()</c> and <c>Part::AsPartTrigger()</c> -- the two class filters
    /// <c>SceneBSPAddPart</c> @0x47ca10 discriminates a tile's parts with, and so the ones
    /// <see cref="CX.Alloy.SceneParts"/> classifies its registry by. Both take the class-wide -0x8 on Windows,
    /// printed by <c>tools/abi/vtable-slots.py Part</c>.</summary>
    public static readonly int PartAsEmitterSlot = Field("Part", "vtbl.AsPartEmitter", Expect.VtblSlot, sig: "PartEmitter()");
    /// <inheritdoc cref="PartAsEmitterSlot"/>
    public static readonly int PartAsTriggerSlot = Field("Part", "vtbl.AsPartTrigger", Expect.VtblSlot, sig: "PartTrigger()");

    /// <summary>The MDL <c>tilefade</c> property, parsed straight into this int: 1 = the mesh fades when it
    /// stands between the camera and the player, 2 = the cap that shows through the hole, 4 = no shadow.
    /// <c>CheckFadePlane</c> @0x47bf30 tests only <c>== 1</c> parts and <c>ProcessStaticParts</c> @0x470970
    /// files 2 and 4 into the tile's side lists. Read through the MODEL, where every row is delta 0.</summary>
    public static readonly int MdlTileFade = Field<int>("MdlNodeTriMesh", "tileFade");

    /// <summary>The radius of the sphere the engine files an emitter into the BSP with and culls it by:
    /// <c>SceneBSPAddPart</c> @0x47ca10 passes it to <c>BSPFitSphere</c> and <c>PartOutside</c> @0x47a290 to
    /// <c>SphereAbovePlane</c> per frustum plane. Alloy's emitter set applies the same test against its own
    /// frustum. Bracketed on Windows by <see cref="EmSeeded"/> and <see cref="EmBirthing"/>.</summary>
    public static readonly int EmViewRadius = Field<float>("PartEmitter", "viewRadius");

    /// <summary>The area's grass bins, where the SCENE owns them rather than where a frame left them:
    /// <c>Scene::InitBins</c> @0x472390 sizes it as a <c>CExoArrayList</c> of 0x10-byte list heads, one per
    /// bin column, each holding one row of the bin grid. <c>binbucket</c> is the per-FRAME slice of this; the
    /// grid is the area.</summary>
    public static readonly int SceneBinGrid = Field<IntPtr>("Scene", "binGrid");
    /// <inheritdoc cref="SceneBinGrid"/>
    public static readonly int SceneBinGridCount = Field<int>("Scene", "binGridCount");

    // ---- the grass classes ----

    /// <summary><c>CAurTriangleBin</c>: the triangle-list array at +0x38 and its count at +0x40, from
    /// <c>RenderGrassPolys</c> @0x4b5410. Nine of the class's functions pair, every one at delta 0.</summary>
    public static readonly int BinLists = Field<IntPtr>("CAurTriangleBin", "lists");
    /// <inheritdoc cref="BinLists"/>
    public static readonly int BinListCount = Field<int>("CAurTriangleBin", "listCount");
    /// <summary>The bin's bounding sphere -- centre at +0x24, radius at +0x30. Two witnesses on the
    /// same pair: <c>SceneBSPAddBin</c> @0x47c440 files the bin with
    /// <c>BSPFitSphere</c> and <c>BinOutside</c> @0x47a620 culls it with <c>SphereAbovePlane</c>. Windows by
    /// the class-wide delta 0 in <see cref="BinLists"/>' note.</summary>
    public static readonly int BinCentre = Field<System.Numerics.Vector3>("CAurTriangleBin", "centre");
    /// <inheritdoc cref="BinCentre"/>
    public static readonly int BinRadius = Field<float>("CAurTriangleBin", "radius");

    /// <summary><c>CAurTriangleList</c>: the flat vertex array (three vec3 per face, stride 0x24) with its
    /// FLOAT count at +8, the parallel per-face blade counts at +0x30 with their count at +0x38, and the
    /// grass material index at +0x40 -- <c>RenderGrassPolys</c> compares exactly that against the material it
    /// is drawing. Seven paired functions, all delta 0, bracketing 0x40 between 0x3c and 0x48.</summary>
    public static readonly int ListVerts = Field<IntPtr>("CAurTriangleList", "verts");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListVertFloats = Field<int>("CAurTriangleList", "vertFloats");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListBlades = Field<IntPtr>("CAurTriangleList", "blades");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListBladeCount = Field<int>("CAurTriangleList", "bladeCount");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListMat = Field<int>("CAurTriangleList", "mat");

    /// <summary><c>CAurGrassInfo</c>, 0x54 bytes: 32-char texture name, density, blade height (the quad is
    /// height wide AND height tall), diffuse and ambient colours, and the four 2x2-atlas selection
    /// probabilities. Read live off <c>g_GrassInfo</c>, which a server <c>AddGrassOverride</c> rewrites. The
    /// class's one function, its constructor, is the only anchor to be had.</summary>
    public static readonly int GrassInfoName = Field<byte>("CAurGrassInfo", "name");
    /// <summary>Blades per face in <c>AddTriangle</c> are <c>|cross| * 0.5 * density</c>, i.e. face area
    /// times this.</summary>
    public static readonly int GrassInfoDensity = Field<float>("CAurGrassInfo", "density");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoHeight = Field<float>("CAurGrassInfo", "height");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoDiffuse = Field<System.Numerics.Vector3>("CAurGrassInfo", "diffuse");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoAmbient = Field<System.Numerics.Vector3>("CAurGrassInfo", "ambient");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoProb = Field<float>("CAurGrassInfo", "prob", count: 4);
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoInUse = Field<byte>("CAurGrassInfo", "inUse");
    /// <summary>Record size of <see cref="GrassInfoName"/>'s class -- the stride <c>g_GrassInfo</c> is
    /// indexed by. A SIZE, and the one row resting on an ABI argument rather than a reading: the ctor puts
    /// the last member at the same place on both, the largest member is a float, and the class has no base --
    /// and base tail-padding reuse, the whole cause of divergence, needs a base to reuse.</summary>
    public static readonly int GrassInfoStride = Field("CAurGrassInfo", "stride");

    // ---- aurMatrixStack ----

    /// <summary>The engine's software matrix stack: the top matrix's 16 floats at +0x18, and 0x58 bytes to
    /// the next stack (PROJECTION=0, VIEW=1, MODEL=2 inside <c>m_aurMtxStack</c>).
    /// <para>All seven of the class's functions pair and touch nothing outside 0x18..0x57 on either build,
    /// which measures the top AND the stride: the stride is the class's own size.</para></summary>
    public static readonly int MtxStackTop = Field<System.Numerics.Matrix4x4>("aurMatrixStack", "top");
    /// <inheritdoc cref="MtxStackTop"/>
    public static readonly int MtxStackStride = Field("aurMatrixStack", "stride");

    // ---- classes the pass reads that no sweep can pair ----

    /// <summary>The two <c>List&lt;MaterialParameter&gt;</c> the .mtr's declared values reach. Shared first,
    /// instance second, so a per-part override still wins -- the instance list is only SIZED to the shared
    /// count and carries defaults until something overrides. Two function pairs measure it:
    /// <c>Material::InitSharedMaterial</c> and <c>CheckAndBindCustomShader</c>.</summary>
    public static readonly int MatParams = Field<IntPtr>("Material", "params");
    /// <inheritdoc cref="MatParams"/>
    public static readonly int MatParamCount = Field<int>("Material", "paramCount");
    /// <summary>The shared half of <see cref="MatParams"/>. <c>SharedMaterial::Init</c> reads
    /// <c>1d8:4 / 1d0:8</c> on both builds, and Windows <c>ParseField</c> reads the same pair -- a direct
    /// pairing, not neighbour corroboration.</summary>
    public static readonly int SharedParams = Field<IntPtr>("SharedMaterial", "params");
    /// <inheritdoc cref="SharedParams"/>
    public static readonly int SharedParamCount = Field<int>("SharedMaterial", "paramCount");

    /// <summary>One <c>MaterialParameter</c>: name at +0, value at +0x44, type at +0x5c (0 = float),
    /// component count at +0x60 as a ushort, 100 bytes a record. Nothing takes one as <c>this</c>, so it is
    /// read out of <c>SharedMaterial::BindShaderParameters</c> (@0x4c5480 / @0x82aa0), which walks the whole
    /// record byte for byte the same on both.</summary>
    public static readonly int ParamValue = Field<float>("MaterialParameter", "value");
    /// <inheritdoc cref="ParamValue"/>
    public static readonly int ParamType = Field<int>("MaterialParameter", "type");
    /// <inheritdoc cref="ParamValue"/>
    public static readonly int ParamComps = Field<ushort>("MaterialParameter", "comps");
    /// <inheritdoc cref="ParamValue"/>
    public static readonly int ParamStride = Field("MaterialParameter", "stride");

    /// <summary>CNWCArea -&gt; tile grid width (+0xc) and height (+0x10): <c>CNWCArea::LoadBorder</c>
    /// (Linux @0x70a5c0, Windows rva 0x7dea10) multiplies exactly these for the tile count and bounds its
    /// strip loops on them. Width is the row-index MULTIPLIER and height the loop bound on both:
    /// <c>(height - 1) * width</c> is the last row's first tile in either decompilation.</summary>
    public static readonly int AreaTileWidth = Field<int>("CNWCArea", "tileWidth");
    /// <inheritdoc cref="AreaTileWidth"/>
    public static readonly int AreaTileHeight = Field<int>("CNWCArea", "tileHeight");

    /// <summary>The GL program id inside a <c>GLRender::PROG_FIXED</c> record. Every function that touches
    /// one takes it as an ARGUMENT rather than <c>this</c>, so it is read on the caller:
    /// <c>GLRender::SetProgram</c> (@0x4cf940 / rva 0xb7a40) takes it as the id it hands
    /// <c>glUseProgram</c>.</summary>
    public static readonly int ProgFixedId = Field<uint>("GLRender::PROG_FIXED", "programId");

    /// <summary>A <c>List&lt;T&gt;</c> / <c>CExoArrayList&lt;T&gt;</c> in this engine is {element pointer,
    /// count}: the symbol's own address is the list, not the elements. Both builds, every paired list.</summary>
    public static readonly int ListItems = Field("(abi)", "list.items");
    /// <inheritdoc cref="ListItems"/>
    public static readonly int ListItemCount = Field("(abi)", "list.count");

    // ================= Alloy: the renderer, textures, buffers and shim (#443, batch 3 of #429 section 2) ==
    // The last of section 2. Measured 2026-09-12: one FieldDump.java cycle over CNWCAnimBase/CRes/
    // CAurTexture/Material (.scratch/443), the earlier #436/#441 dumps re-read for the classes they already
    // covered, tools/ghidra/DecompOne.java for the three functions no class sweep reaches
    // (SharedMaterial::BindShaderParameters, GLRender::SetUniforms, GLRender::UnlockPool), and
    // tools/abi/vtable-slots.py for every vtbl row. Decompiler evidence: a CANDIDATE until probed.

    // ---- CAurTextureBasic (identical 0x28..0xec; #436 read the class by hand, this batch pairs the rest) ----

    /// <summary>The texture a wrapper stands in for; null = the wrapper answers for itself. <c>GetReferencedTexture</c>
    /// is <c>this+0x18 ?: this</c> on both builds (Linux @0x484200, Windows rva 0xebb00).</summary>
    public static readonly int TexReferenced = Field<CX.Engine.CAurTexture>("CAurTexture", "referenced");

    /// <summary>The decoded image buffer. <c>ConstructImage</c> fills it, <c>FreeImage</c> releases it, and
    /// <c>BuildAndStoreTexture</c> refuses without it -- all three read +0x58 on both builds.</summary>
    public static readonly int TexBasicImage = Field<IntPtr>("CAurTextureBasic", "image");

    /// <summary>The GL name the engine bound for a single-cell texture. <c>Reset</c> clears it and
    /// <c>glImageBasic</c> writes it, both at +0x6c on both builds.</summary>
    public static readonly int TexBasicGlId = Field<int>("CAurTextureBasic", "glId");

    /// <summary>The compressed byte count, zero for a sheet that came down the UNCOMPRESSED path.
    /// <c>ConstructImage</c> writes it; <c>BuildAndStoreTexture</c> and <c>GetMemoryUsage</c> read it, at the
    /// same offset on both.</summary>
    public static readonly int TexBasicCompressedSize = Field<int>("CAurTextureBasic", "compressedSize");

    /// <summary>Sheet dimensions and bytes per texel. <c>CheckAttributes</c> reads and rewrites all three
    /// on both builds, which is as clean a pair as this class offers.</summary>
    public static readonly int TexBasicWidth = Field<int>("CAurTextureBasic", "width");
    /// <inheritdoc cref="TexBasicWidth"/>
    public static readonly int TexBasicHeight = Field<int>("CAurTextureBasic", "height");
    /// <inheritdoc cref="TexBasicWidth"/>
    public static readonly int TexBasicBpp = Field<int>("CAurTextureBasic", "bpp");

    /// <summary>The format/kind word, 4 for a PLT. <c>glImageBasic</c> takes the GPU-composite branch on
    /// <c>== 4</c> and nothing else, so this is the ONLY test separating a build Alloy must copy from one it
    /// has already uploaded. <c>FreeImage</c> and <c>glImageBasic</c> read +0x84 on both.</summary>
    public static readonly int TexBasicPltKind = Field<int>("CAurTextureBasic", "pltKind");

    /// <summary><c>uint*</c>, the <c>numX*numY</c> GL names <c>glImageBasic</c> generates, one per cell.
    /// <c>Reset</c> frees and nulls it at +0x50 on both builds.</summary>
    public static readonly int TexBasicCellNames = Field<IntPtr>("CAurTextureBasic", "cellNames");

    /// <summary>The PLT layer scheme (<c>int*</c>) and its <c>short</c> count -- the other half of the
    /// engine's own texture identity. <c>SetScheme</c> writes both and <c>CalculateHashID</c> reads both,
    /// at +0x48/+0xd4 on either build.</summary>
    public static readonly int TexBasicSchemes = Field<IntPtr>("CAurTextureBasic", "schemes");
    /// <inheritdoc cref="TexBasicSchemes"/>
    public static readonly int TexBasicSchemeCount = Field<short>("CAurTextureBasic", "schemeCount");

    /// <summary>The <c>short</c> atlas grid the engine de-atlases into one GL texture per cell.
    /// <c>Reset</c> and the destructor read both on both builds (MSVC emits them in the other order, which
    /// is scheduling, not layout).</summary>
    public static readonly int TexBasicNumX = Field<short>("CAurTextureBasic", "numX");
    /// <inheritdoc cref="TexBasicNumX"/>
    public static readonly int TexBasicNumY = Field<short>("CAurTextureBasic", "numY");

    /// <summary>The mean-alpha cache and the animation controller pointer. The constructor writes the whole
    /// 0x28..0x37 region on both builds (Linux as two 8-byte stores, Windows as four dwords),
    /// <c>ConstructImage</c> rewrites the mean and <c>ParseField</c> walks the controller, at both
    /// ends.</summary>
    public static readonly int TexBasicAlphaMean = Field<float>("CAurTextureBasic", "alphaMean");
    /// <inheritdoc cref="TexBasicAlphaMean"/>
    public static readonly int TexBasicController = Field<IntPtr>("CAurTextureBasic", "controller");

    /// <summary>The RESOLVED texture name -- <c>char[0x20]</c> that <c>LoadImage</c> fills and
    /// <c>CAurTexture::Restore</c> re-acquires by, stable across a delete/rebuild, which is what makes it an
    /// identity. NOT <see cref="TexBasicName"/>: there are two 32-byte name fields, the requested name and
    /// this one, and both constructors write both runs.</summary>
    public static readonly int TexBasicResolvedName = Field<byte>("CAurTextureBasic", "resolvedName");

    /// <summary>The four state bytes <c>UnbuiltDiag</c> reads: cube map, image constructed, "no image and no
    /// controller", and never-build. <c>Reset</c> writes 0xe6/0xe7 (Windows as ONE 2-byte store over the
    /// pair), <c>BuildAndStoreTexture</c> reads 0xe6/0xe8 and <c>glImageBasic</c> 0xe3 -- all on both
    /// builds.</summary>
    public static readonly int TexBasicFlagCube = Field<byte>("CAurTextureBasic", "flagCube");
    /// <inheritdoc cref="TexBasicFlagCube"/>
    public static readonly int TexBasicFlagBuilt = Field<byte>("CAurTextureBasic", "flagBuilt");
    /// <inheritdoc cref="TexBasicFlagCube"/>
    public static readonly int TexBasicFlagNoImage = Field<byte>("CAurTextureBasic", "flagNoImage");
    /// <inheritdoc cref="TexBasicFlagCube"/>
    public static readonly int TexBasicFlagNeverBuild = Field<byte>("CAurTextureBasic", "flagNeverBuild");
    /// <summary>The byte <c>AurTextureBuildAndStoreAll</c> @0x48e680 tests per queued entry -- non-zero is
    /// skipped (not built) and still dropped with the rest when the count goes to 0. Both drains read it
    /// (Windows rva 0xe6910); its writer was not chased.</summary>
    public static readonly int TexBasicFlagNoDrain = Field<byte>("CAurTextureBasic", "flagNoDrain");
    /// <summary>The TXI <c>filerange</c> count -- non-zero makes <c>LoadImage</c> @0x487980 stack
    /// <c>%s%d</c> sub-files instead of reading the resolved name once (Windows RVA 0xEC570, identical).</summary>
    public static readonly int TexBasicFileRange = Field<short>("CAurTextureBasic", "fileRange");
    /// <summary>The TXI bump-map flag -- <c>CheckAttributes</c> @0x485d70 discards a bump image of more
    /// than one byte per pixel through <c>FreeImage</c> (Windows: inlined in <c>ConstructImage</c>, identical).</summary>
    public static readonly int TexBasicFlagBump = Field<byte>("CAurTextureBasic", "flagBump");
    /// <summary>How <c>FreeImage</c> @0x485140 frees <see cref="TexBasicImage"/> -- non-zero through
    /// <c>AurReleaseRaster</c>, zero through the engine's <c>delete[]</c> (<c>AurTextureFreeImage</c> @0x4850f0).
    /// <c>LoadImage</c> sets it whenever the decoder slot answered (Windows RVA 0xEC570, identical).</summary>
    public static readonly int TexBasicFlagRasterRelease = Field<byte>("CAurTextureBasic", "flagRasterRelease");

    // ---- CAurTexture's vtable: the ten accessors TexScan reads a sheet through (#219) ----
    // All -8 for #433's virtual-destructor reason EXCEPT GetCompressedSize(), where MSVC's overload
    // REVERSAL cancels the shift exactly. That is the trap #433 named, caught here by measuring rather
    // than by applying the rule: tools/abi/vtable-slots.py CAurTexture prints both tables.

    /// <summary><c>GetImage()</c> -- the pixel pointer every scan starts from.</summary>
    public static readonly int TexGetImageSlot = Field("CAurTexture", "vtbl.GetImage", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary><c>GetWidth()</c> / <c>GetHeight()</c> / <c>GetBPP()</c> -- the sheet's shape.</summary>
    public static readonly int TexGetWidthSlot = Field("CAurTexture", "vtbl.GetWidth", Expect.VtblSlot, sig: "int()");
    /// <inheritdoc cref="TexGetWidthSlot"/>
    public static readonly int TexGetHeightSlot = Field("CAurTexture", "vtbl.GetHeight", Expect.VtblSlot, sig: "int()");
    /// <inheritdoc cref="TexGetWidthSlot"/>
    public static readonly int TexGetBppSlot = Field("CAurTexture", "vtbl.GetBPP", Expect.VtblSlot, sig: "int()");
    /// <summary><c>GetNumX()</c> / <c>GetNumY()</c> -- the atlas grid an uncompressed sheet is cut on.</summary>
    public static readonly int TexGetNumXSlot = Field("CAurTexture", "vtbl.GetNumX", Expect.VtblSlot, sig: "int()");
    /// <inheritdoc cref="TexGetNumXSlot"/>
    public static readonly int TexGetNumYSlot = Field("CAurTexture", "vtbl.GetNumY", Expect.VtblSlot, sig: "int()");
    /// <summary><c>GetCompressedSize()</c>, the no-argument overload -- and the one slot in this table that
    /// does NOT move. MSVC emits the overload set <c>(int,int)</c>-then-<c>()</c> where Itanium emits
    /// <c>()</c>-then-<c>(int,int)</c>, so the swap gives back exactly the pointer the virtual destructor
    /// took away. Identify it by signature; "the Nth GetCompressedSize" is meaningless.</summary>
    public static readonly int TexCompressedSizeSlot = Field("CAurTexture", "vtbl.GetCompressedSize", Expect.VtblSlot, sig: "int()");
    /// <summary><c>GetNumMipHeaderBytes()</c> -- the header run to skip before the first texel.</summary>
    public static readonly int TexHeaderSkipSlot = Field("CAurTexture", "vtbl.GetNumMipHeaderBytes", Expect.VtblSlot, sig: "int()");
    /// <summary><c>GetMipMap()</c>.</summary>
    public static readonly int TexMipMapSlot = Field("CAurTexture", "vtbl.GetMipMap", Expect.VtblSlot, sig: "int()");
    /// <summary><c>GetCompressionFormat()</c> -- the index into <c>textureFormatTranslationTable</c>
    /// @0x12f5080 where 7 = DXT1 and 8 = DXT5.</summary>
    public static readonly int TexFormatSlot = Field("CAurTexture", "vtbl.GetCompressionFormat", Expect.VtblSlot, sig: "int()");

    // ---- Scene's vtable: the slots RspShim calls through (#132/#232/#233/#235) ----
    // Displacements from the loaded vptr, exactly as Scene::RenderSinglePass uses them. All -8.

    /// <summary><c>Scene::DoBinBucket()</c> -- the per-bucket draw RSP runs for each of the sixteen.</summary>
    public static readonly int SceneDoBinBucketSlot = Field("Scene", "vtbl.DoBinBucket", Expect.VtblSlot, sig: "void()");
    /// <summary><c>Scene::RenderBeams()</c>.</summary>
    public static readonly int SceneRenderBeamsSlot = Field("Scene", "vtbl.RenderBeams", Expect.VtblSlot, sig: "void()");
    /// <summary><c>Scene::DoEmitterBucket()</c>.</summary>
    public static readonly int SceneDoEmitterBucketSlot = Field("Scene", "vtbl.DoEmitterBucket", Expect.VtblSlot, sig: "void()");
    /// <summary><c>Scene::RenderLensFlares()</c>.</summary>
    public static readonly int SceneRenderLensFlaresSlot = Field("Scene", "vtbl.RenderLensFlares", Expect.VtblSlot, sig: "void()");
    /// <summary><c>Scene::FadeDoors()</c> -- retired as a frame anchor: the shim does not vcall it any more
    /// (<see cref="CX.Alloy.DoorFade"/> does the fade). The row stays because the shim's own SelfCheck proves
    /// no OTHER slot it calls collides with this displacement.</summary>
    public static readonly int SceneFadeDoorsSlot = Field("Scene", "vtbl.FadeDoors", Expect.VtblSlot, sig: "void()");
    /// <summary><c>Scene::DoFog()</c>.</summary>
    public static readonly int SceneDoFogSlot = Field("Scene", "vtbl.DoFog", Expect.VtblSlot, sig: "void()");
    /// <summary><c>Scene::DoHighlightGob()</c> and <c>Scene::DoHighlightTriggers(bool)</c> -- the two
    /// overlays RSP calls unconditionally and which self-gate inside.</summary>
    public static readonly int SceneDoHighlightGobSlot = Field("Scene", "vtbl.DoHighlightGob", Expect.VtblSlot, sig: "void()");
    /// <inheritdoc cref="SceneDoHighlightGobSlot"/>
    public static readonly int SceneDoHighlightTriggersSlot = Field("Scene", "vtbl.DoHighlightTriggers", Expect.VtblSlot, sig: "void(byte kind)");

    /// <summary><c>LightManager::EnableLights()</c> -- RSP's very first call, and load-bearing: the owned
    /// pass draws dark without it.</summary>
    public static readonly int LightMgrEnableLightsSlot = Field("LightManager", "vtbl.EnableLights", Expect.VtblSlot, sig: "void()");

    /// <summary><c>Gob::GetAttachedGobs(List&lt;CAurObject*&gt;&amp;)</c> -- fills a malloc'd
    /// {Gob** data, int count} the caller frees. NOT the head/tail list <c>Gob::CollectLights</c> walks.</summary>
    public static readonly int GobAttachedGobsSlot = Field("Gob", "vtbl.GetAttachedGobs", Expect.VtblSlot, sig: "void(IntPtr list)");

    // ---- the Scene highlight list, and the gob flag the walk reads (#232) ----

    /// <summary><c>Scene</c>'s highlight state as <c>Scene::AddHighlightGob</c> lays it out: a
    /// <c>CExoArrayList&lt;Gob*&gt;</c> (items +0x80, count +0x88) and a parallel RGB <c>Vector</c> array at
    /// +0x90, stride 0xc. <c>Scene::DoHighlightGob</c> reads all three at both ends, and both constructors
    /// write all three -- two pairs.</summary>
    public static readonly int SceneHiliteGobs = Field<IntPtr>("Scene", "hiliteGobs");
    /// <inheritdoc cref="SceneHiliteGobs"/>
    public static readonly int SceneHiliteCount = Field<int>("Scene", "hiliteCount");
    /// <inheritdoc cref="SceneHiliteGobs"/>
    public static readonly int SceneHiliteColors = Field<IntPtr>("Scene", "hiliteColors");
    /// <summary>The highlight glow's <c>CAurTexture*</c>, set by <c>Scene::SetHighlightGobTexture</c>
    /// (Linux @0x461cf0, Windows rva 0xd3420, both <c>this+0xa0</c>); <c>CNWCArea::LoadArea</c> names
    /// <c>fxpa_dot02</c>. <c>Scene::DoHighlightGob</c> draws nothing while it is null.</summary>
    public static readonly int SceneHiliteTexture = Field<IntPtr>("Scene", "hiliteTexture");

    /// <summary>The gob byte <c>Scene::DoHighlightGob</c> tests before recursing into attached children.
    /// <c>Gob::SetPropagateHighlightEffects</c> is its only writer on either build, which is also what
    /// NAMES it -- it is not the visible byte, which is <see cref="GobVisible"/>.</summary>
    public static readonly int GobPropagateHilite = Field<byte>("Gob", "propagateHilite");

    // ---- CX-95: the GUI scene path ----

    /// <summary>The scene's NUL-terminated name, <c>"sceneGUI_&lt;panel&gt;"</c> for a panel scene.
    /// <c>Scene::RenderOnlyGobs</c> strcasecmp's it at this offset on both builds (Linux @0x46837c
    /// <c>lea 0xf0(%rbx)</c>, Windows rva 0xcfaf5 <c>lea rcx,[rbx+0xf0]</c>) to pick its two lit scenes.</summary>
    public static readonly int SceneName = Field<byte>("Scene", "name");
    /// <summary><c>CAurObject::Render(bool propagate)</c> -- the per-gob draw <c>Scene::RenderOnlyGobs</c>
    /// calls with <c>false</c> (Linux @0x468414 <c>call *0x28</c>, Windows rva 0xcfb50 <c>call *0x20</c>). <c>Gob::Render</c>
    /// for a model, <c>StringGob::Render</c> for text.</summary>
    public static readonly int GobRenderSlot = Field("Gob", "vtbl.Render", Expect.VtblSlot, sig: "void(byte propagate)");

    /// <summary>A stream draw's element-pool token. <c>GLRender::SetVertexStreamDataObject</c> reads it at
    /// Linux +0x14 / Windows +0x18, the vertex token at +0x1c / +0x20 and the attribute flags at +0x20 / +0x24
    /// (Windows rva 0xbacc0 vs Linux @0x4d07a0); <c>CommitVertexData</c> reads the stride at +0x24 / +0x28.
    /// The indexed byte is +0x8 on both.</summary>
    public static readonly int VsdoIndexed = Field<byte>("VertexStreamDataObject", "indexed");
    /// <inheritdoc cref="VsdoIndexed"/>
    public static readonly int VsdoElemToken = Field<uint>("VertexStreamDataObject", "elemToken");
    /// <inheritdoc cref="VsdoIndexed"/>
    public static readonly int VsdoVertToken = Field<uint>("VertexStreamDataObject", "vertToken");
    /// <inheritdoc cref="VsdoIndexed"/>
    public static readonly int VsdoFormat = Field<uint>("VertexStreamDataObject", "format");
    /// <inheritdoc cref="VsdoIndexed"/>
    public static readonly int VsdoStride = Field<int>("VertexStreamDataObject", "vertexStride");

    /// <summary><c>GLRender::m_material</c>'s MATERIAL struct, per <c>SetMaterialv</c> (Linux @0x4cc060,
    /// Windows rva 0xb75b0, same switch on both): property 1 ambient at +0, 2 diffuse at +0x10, 3 emissive at
    /// +0x30, each an RGBA float quad.</summary>
    public static readonly int GlMatAmbient = Field<System.Numerics.Vector4>("GlMaterial", "ambient");
    /// <inheritdoc cref="GlMatAmbient"/>
    public static readonly int GlMatDiffuse = Field<System.Numerics.Vector4>("GlMaterial", "diffuse");
    /// <inheritdoc cref="GlMatAmbient"/>
    public static readonly int GlMatEmissive = Field<System.Numerics.Vector4>("GlMaterial", "emissive");

    /// <summary>The three fields of nuklear's <c>nk_context</c> that its flush touches, identical on both
    /// builds: <c>delta_time_seconds</c>, which <c>nk_sdl_render</c> stores first, and the
    /// <c>cmd_count</c>/<c>cmd_offset</c> pair inside <c>ctx-&gt;draw_list</c> that
    /// <c>nk__draw_list_begin</c> @0xde0e60 reads.</summary>
    public static readonly int NkCtxDeltaTime = Field<float>("NkContext", "deltaTime");
    /// <inheritdoc cref="NkCtxDeltaTime"/>
    public static readonly int NkCtxDrawCmdCount = Field<int>("NkContext", "drawCmdCount");
    /// <inheritdoc cref="NkCtxDeltaTime"/>
    public static readonly int NkCtxDrawCmdOffset = Field<long>("NkContext", "drawCmdOffset");

    /// <summary>The rest of <c>nk_context</c> that NUI's per-window render controls read, identical on both builds:
    /// <c>memory.memory.ptr</c> (the base every command offset is added to), <c>memory.allocated</c> (the end of
    /// the queue: <c>nk__next</c> stops at an offset at or past it), the head of the window list and the frame
    /// <c>seq</c> that marks a window as drawn this frame.</summary>
    public static readonly int NkCtxMemPtr = Field<IntPtr>("NkContext", "memPtr");
    /// <inheritdoc cref="NkCtxMemPtr"/>
    public static readonly int NkCtxMemAllocated = Field<ulong>("NkContext", "memAllocated");
    /// <inheritdoc cref="NkCtxMemPtr"/>
    public static readonly int NkCtxWindowBegin = Field<IntPtr>("NkContext", "windowBegin");
    /// <inheritdoc cref="NkCtxMemPtr"/>
    public static readonly int NkCtxSeq = Field<uint>("NkContext", "seq");

    /// <summary><c>nk_window</c>: <c>seq</c> (equal to the context's while the window was begun this frame).</summary>
    public static readonly int NkWinSeq = Field<uint>("NkWindow", "seq");
    /// <summary>The window's name, 64 bytes NUL-terminated: for a NUI window, the id the server gave it.</summary>
    public static readonly int NkWinName = Field<byte>("NkWindow", "name", count: 64);
    /// <summary><c>nk_window.flags</c>; bit 0x4000 is HIDDEN, which <c>nk__begin</c> skips.</summary>
    public static readonly int NkWinFlags = Field<uint>("NkWindow", "flags");
    /// <summary><c>nk_window.bounds</c>: x, y, w, h in screen px.</summary>
    public static readonly int NkWinBounds = Field<float>("NkWindow", "bounds", count: 4);
    /// <summary>The window's command buffer: <c>begin</c> and <c>end</c> bound its commands in the context queue
    /// (offsets), <c>last</c> is its last command in the main chain.</summary>
    public static readonly int NkWinBufBegin = Field<ulong>("NkWindow", "bufBegin");
    /// <inheritdoc cref="NkWinBufBegin"/>
    public static readonly int NkWinBufEnd = Field<ulong>("NkWindow", "bufEnd");
    /// <inheritdoc cref="NkWinBufBegin"/>
    public static readonly int NkWinBufLast = Field<ulong>("NkWindow", "bufLast");
    /// <summary><c>nk_window.popup.buf.active</c>: the window has popup commands the link pass appends.</summary>
    public static readonly int NkWinPopupActive = Field<int>("NkWindow", "popupActive");
    /// <summary><c>nk_window.next</c>: the window list runs bottom to top.</summary>
    public static readonly int NkWinNext = Field<IntPtr>("NkWindow", "next");

    // ---- CNWCAnimBase: the highlight CHANNELS (#336) ----
    // No CNWC* class is name-exported on Windows, so the class sweep dumps it EMPTY. #446 took #420's RVA
    // path instead: CNWCAnimBase::RefreshHilite is Linux @0x9cb260 / Windows rva 0x966170, paired on its own
    // ExoLog line ("nwcanimbase.cpp", 0x123a, "Attempting to unhighlight a gob in a null scene"), and all
    // three offsets are read off that one body on each build. No probe: Hilite writes the global
    // HiliteEnabled into the active flag rather than a literal 1, and a fresh CNWCAnimBase has every channel
    // zeroed -- a Byte01 here would pass on any offset that happens to be zero, and a probe that cannot fail
    // is not a probe.

    /// <summary>A hilite is written into one of five channel slots -- active flag at +0x9c[ch], colour at
    /// +0x60[ch] (12 bytes), "also add a glow gob" at +0xb0[ch] -- and <c>RefreshHilite</c> takes the LOWEST
    /// active slot as the winner. Tab and mouseover arrive on channel 2, every VFX on 3, which is why a Tab
    /// over a glowing creature shows the Tab colour. The Linux constructor writes +0x9c and +0xc8.</summary>
    public static readonly int AnimHiliteActive = Field<int>("CNWCAnimBase", "hiliteActive", count: 5);
    /// <inheritdoc cref="AnimHiliteActive"/>
    public static readonly int AnimHiliteGlow = Field<int>("CNWCAnimBase", "hiliteGlow", count: 5);
    /// <summary>CNWCAnimBase -&gt; its <c>Gob*</c>: the one the winning hilite channel is published against
    /// (<c>RefreshHilite</c> guards the whole body on it), and the one <c>CNWCAnimBase::SetPosition</c> @0x9c9d60
    /// / Windows rva 0x9680a0 forwards to. With <see cref="ObjAnimBase"/> this is the id -&gt; Gob walk.</summary>
    public static readonly int AnimGob = Field<CX.Engine.Gob>("CNWCAnimBase", "gob");

    // ---- GLRender's pool descriptor table (#260) ----

    /// <summary>The 0x20-byte pool descriptor <c>GLRender::RequestPool</c> callocs: host shadow, byte size,
    /// GL name, target index, lock flags, usage word and the two latches. <c>GLRender::UnlockPool</c> indexes
    /// the array and tests every one of them on both builds, with the same stride. <c>GlPool</c> is the
    /// record's pseudo-class, so the stride row carries the indexer.</summary>
    public static readonly int PoolStride = Field("GlPool", "stride");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolHost = Field<IntPtr>("GlPool", "host");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolSize = Field<int>("GlPool", "size");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolName = Field<uint>("GlPool", "name");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolTarget = Field("GlPool", "target");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolLockFlags = Field<byte>("GlPool", "lockFlags");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolUsage = Field<uint>("GlPool", "usage");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolUploaded = Field<byte>("GlPool", "uploaded");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolNamed = Field<byte>("GlPool", "named");

    /// <summary>The {base, count} pair the descriptor table hangs off -- the same pair on both builds: Linux
    /// reads the two adjacent globals <c>DAT_01ae5480</c>/<c>DAT_01ae5488</c>, Windows reads the first two words
    /// of the struct its helper is handed.</summary>
    public static readonly int PoolTableBase = Field<CX.Engine.GlPool>("GlPoolTable", "base");
    /// <inheritdoc cref="PoolTableBase"/>
    public static readonly int PoolTableCount = Field<uint>("GlPoolTable", "count");

    /// <summary>Byte offset from <see cref="SymbolMap.GlPoolTableAnchor"/> to that {base, count} pair.
    /// <para>An ANCHOR, not a value: Linux has no symbol on the pair, so it anchors on a neighbouring
    /// global and steps; Windows names the pair itself, so the step is zero. Same two globals, two ways of
    /// naming them, which is what a diverging row is for.</para></summary>
    public static readonly int PoolTableAnchorOffset =
        Field("GLRender", "poolTableAnchorOffset");

    // ---- GLRender::m_shaderExposedRuntimeData (#123/#124/#347) ----
    // The struct the engine's own SetUniforms uploads its scalar shader inputs from. Nothing takes it as
    // `this`, so it is read here the way #441 read the fade fields: off GLRender::SetUniforms decompiled on
    // both builds (@0x4ccbf0 / @0xb8380). The symbol is exported at Linux 0x19e1ea0 and Windows rva
    // 0x128ea10, and every displacement below appears in SetUniforms on BOTH builds at the SAME offset --
    // the field-by-field pairing this batch could not get out of the PROG_FIXED slot map alone.

    // #352: the module calendar the content post chain reads (rays/flare derive the sun from it). Paired
    // by NAME through ConnectShaderData's slot map: slot +0xb08 is "moduleHour" and GLRender::SetUniforms feeds it the
    // struct's first int; +0xb04 "moduleDay" <- +0x04, +0xb00 "moduleMonth" <- +0x08, +0xb0c
    // "moduleTimeIntoTransition" <- +0x10 (float). Identical displacements in the Windows SetUniforms
    // (@0xb8380, _m_shaderExposedRuntimeData rva 0x128ea10).
    /// <summary><c>moduleHour</c>, the calendar hour the shaders see.</summary>
    public static readonly int RtModuleHour = Field<int>("ShaderExposedRuntimeData", "moduleHour");
    /// <summary><c>moduleDay</c>.</summary>
    public static readonly int RtModuleDay = Field<int>("ShaderExposedRuntimeData", "moduleDay");
    /// <summary><c>moduleMonth</c>.</summary>
    public static readonly int RtModuleMonth = Field<int>("ShaderExposedRuntimeData", "moduleMonth");
    /// <summary><c>moduleTimeIntoTransition</c> (float), seconds into the current hour transition.</summary>
    public static readonly int RtModuleTimeIntoTransition = Field<float>("ShaderExposedRuntimeData", "moduleTimeIntoTransition");

    // CX-88: four more of the same struct, for the sky stage Alloy now compiles itself. Paired the same way
    // -- by NAME through ConnectShaderData's slot map, then by the displacement SetUniforms feeds that slot
    // from: +0xafc is "moduleYear" <- +0x0c, +0xaf0 "moduleDawnHour" <- +0x1c, +0xaf4 "moduleDuskHour" <-
    // +0x20, +0xb1c "areaWeatherDensity" <- +0x38 (a float; the int at +0x34 beside it is areaWeatherType and
    // nothing here reads it). Every one of the four appears at the SAME displacement in the Windows
    // SetUniforms (@0xb8380, m_shaderExposedRuntimeData rva 0x128ea10) -- read off the four rip-relative
    // loads at 0x1400b8711/8727/8753 and the movss at 0x1400b8827 -- and the pre-existing areaFlags row at
    // +0x3c, uploaded from the same run, is the cross-check that the pairing did not slip a field.
    /// <summary><c>moduleYear</c>: the content sky's lunar phase and star seed run off it.</summary>
    public static readonly int RtModuleYear = Field<int>("ShaderExposedRuntimeData", "moduleYear");
    /// <summary><c>moduleDawnHour</c> / <c>moduleDuskHour</c>: the module's day boundaries, which is what a
    /// sky stage turns the calendar hour into a sun elevation with.</summary>
    public static readonly int RtModuleDawnHour = Field<int>("ShaderExposedRuntimeData", "moduleDawnHour");
    /// <inheritdoc cref="RtModuleDawnHour"/>
    public static readonly int RtModuleDuskHour = Field<int>("ShaderExposedRuntimeData", "moduleDuskHour");
    /// <summary><c>areaWeatherDensity</c> (float), the engine's own cover value.</summary>
    public static readonly int RtAreaWeatherDensity = Field<float>("ShaderExposedRuntimeData", "areaWeatherDensity");

    /// <summary>Time of day, 0..1 -- what the sky and the wind phase run off.</summary>
    public static readonly int RtTimeOfDay = Field<int>("ShaderExposedRuntimeData", "timeOfDay");
    /// <summary>The area wind vector.</summary>
    public static readonly int RtAreaWind = Field<System.Numerics.Vector3>("ShaderExposedRuntimeData", "areaWind");
    /// <summary>The area flag word the roof-fade and interior tests read.</summary>
    public static readonly int RtAreaFlags = Field<int>("ShaderExposedRuntimeData", "areaFlags");
    /// <summary>The controlled character's position.</summary>
    public static readonly int RtPlayerPos = Field<System.Numerics.Vector3>("ShaderExposedRuntimeData", "playerPos");
    /// <summary>The camera EYE position.</summary>
    public static readonly int RtCamPos = Field<System.Numerics.Vector3>("ShaderExposedRuntimeData", "camPos");
    /// <summary>Camera pitch and player-camera distance -- the keyhole aperture's inputs.</summary>
    public static readonly int RtCamPitch = Field<float>("ShaderExposedRuntimeData", "camPitch");
    /// <inheritdoc cref="RtCamPitch"/>
    public static readonly int RtCamDist = Field<float>("ShaderExposedRuntimeData", "camDist");
    /// <summary>The camera PIVOT, uploaded under the name <c>cameraFocus</c>. Not the eye
    /// (<see cref="RtCamPos"/>): in chase cam the pivot sits on the controlled character at roughly chest
    /// height, which is what content measures a sight range against.</summary>
    public static readonly int RtCamFocus = Field<System.Numerics.Vector3>("ShaderExposedRuntimeData", "camFocus");

    // ---- the rest ----

    /// <summary>The part's self-illumination colour, <c>PartTriMesh::MatStatic</c>'s prop-3 material term.
    /// Measured rather than interpolated across PartTriMesh's segment boundary: <c>PartTriMesh::Mat</c>
    /// (@0x450960 / rva 0xa2f50) loads it on both builds, putting it at +0x4 with the bbox, and the extra four
    /// bytes at the boundary are the pad before the 8-aligned <see cref="PartNode"/>.</summary>
    public static readonly int PartSelfIllum = Field<System.Numerics.Vector3>("PartTriMesh", "selfIllum");

    /// <summary>The node material colours <c>PartTriMesh::MatStatic</c> @0x450560 hands to
    /// <c>SetMaterialv</c>: the enum runs ambient/diffuse/emissive, and the DIFFUSE term is the one carrying
    /// the part's alpha in fixed-function material setup. Both <c>MdlNodeTriMesh</c> constructors write the
    /// same run, and the rows are filed under the subclass that declares them.</summary>
    public static readonly int MdlDiffuse = Field<System.Numerics.Vector3>("MdlNodeTriMesh", "diffuse");
    /// <inheritdoc cref="MdlDiffuse"/>
    public static readonly int MdlAmbient = Field<System.Numerics.Vector3>("MdlNodeTriMesh", "ambient");

    /// <summary>Stride between the texture slots of a <c>Material</c> -- a <c>SafePointer</c>, pointer plus
    /// a refcount int. The constructor writes slots at 0x00/0x10/../0x60 on both builds and Windows
    /// <c>ClearTextures</c> walks each one's +8 refcount, so 0x10 is measured rather than assumed.</summary>
    public static readonly int MatSlotStride = Field("Material", "slotStride");

    /// <summary>Where a <c>SharedMaterial</c>'s own eleven texture slots begin -- they win over the
    /// Material's three. <c>SharedMaterial::BindAllStandardTextures</c> reads +0xd8 on both builds; the
    /// stride between slots is <see cref="MatSlotStride"/>, measured on the Material array.</summary>
    public static readonly int SharedSlot0 = Field("SharedMaterial", "slot0");

    /// <summary>The PLT scheme rows a <c>Material</c> carries, and how many are live.
    /// <c>Material::ReplaceTexturePLT</c> (Linux @0x5c7a10, Windows rva 0x85610) writes each scheme integer
    /// DIVIDED by the master palette's height, so a reader multiplies back. The creature parity dump is its
    /// only reader; the renderer takes its rows from <c>SceneDb.ObjPltRow</c>.</summary>
    public static readonly int MatPltRows = Field<float>("Material", "pltRows", count: 10);
    /// <inheritdoc cref="MatPltRows"/>
    public static readonly int MatPltRowCount = Field<int>("Material", "pltRowCount");

    /// <summary><c>sizeof(CRes)</c> -- what <c>CExoResMan::Get</c> passes to operator new. Both
    /// constructors' highest write is <c>0x58:8</c>, so the object is 0x60 on both.</summary>
    public static readonly int SizeOfCRes = Field("CRes", "sizeof");
    /// <summary><c>CRes::GetData</c> and <c>CRes::GetSize</c> -- the raw file bytes of a serviced .shd and
    /// their length. One-line accessors reading +0x20 and +0x30 on both builds: the cleanest pair in the
    /// batch.</summary>
    public static readonly int CResData = Field<IntPtr>("CRes", "data");
    /// <inheritdoc cref="CResData"/>
    public static readonly int CResSize = Field<int>("CRes", "size");
    /// <summary><c>CRes</c> carries no restype of its own; it points at its key-table entry, whose
    /// restype is a ushort (<c>CExoKeyTable::FindKey</c>, entry stride 0x38). Same offsets on both builds.</summary>
    public static readonly int CResKeyEntry = Field<IntPtr>("CRes", "keyEntry");
    /// <summary>The first int of the 16-byte <c>RESID</c> a <c>CRes</c> carries -- the container the
    /// resource is read from. <c>CExoResMan::Demand</c> (Linux @0x53ae70 / Windows rva 0x18ef90) dispatches
    /// on it: 0 nowhere, 1 KEY/BIF, 2 ERF/hak/mod, 3 loose, 5 NWSync. NOT the table kind, which
    /// <c>BuildNewTable</c> @0x5345a0 writes with 2 and 3 the other way round.</summary>
    public static readonly int CResSourceKind = Field<int>("CRes", "sourceKind");
    /// <inheritdoc cref="CResKeyEntry"/>
    public static readonly int KeyEntryResType = Field<ushort>("CKeyTableEntry", "resType");
    /// <summary>The entry's 16-byte resref leads it (<c>CExoKeyTable::FindKey</c> compares the entry itself as a
    /// <c>CResRef</c>, Linux @0x631340 / Windows RVA 0x18AD70).</summary>
    public static readonly int KeyEntryResRef = Field<byte>("CKeyTableEntry", "resRef", count: 16);
    /// <summary><c>Hash::SHA1</c> is <c>{ bool empty; byte raw[20]; char hex[41]; }</c>. Its constructor
    /// @0x534cd0 sets the flag, hex-encodes the raw bytes and clears the flag only once 40 digits parsed, so
    /// <c>empty</c> is INVERTED (non-zero = no hash). Same offsets on Windows, where
    /// <c>CExoResMan::AddManifest</c> rva 0x18e7a0 tests and reads the same two.</summary>
    public static readonly int Sha1Empty = Field<byte>("Sha1", "empty");
    /// <inheritdoc cref="Sha1Empty"/>
    public static readonly int Sha1Hex = Field<byte>("Sha1", "hex", count: 41);

    /// <summary>The live <c>CRes*</c> of an entry, or null. <c>CExoKeyTable::GetRes</c> returns exactly
    /// <c>*(CRes**)(entry+0x18)</c> on Linux @0x531430 and Windows RVA 0x18B1C0. A non-null one means the bytes
    /// are already in memory, so a worker must not read the file for them.</summary>
    public static readonly int KeyEntryRes = Field<IntPtr>("CKeyTableEntry", "res");
    /// <summary>The 16-byte <c>RESID</c> -- <c>[0]</c> source kind (1 BIF, 2 ERF/HAK, 3 loose file,
    /// 5 NWSync), <c>[1]</c> file index in the table's file array, <c>[2]</c> the owning table's id,
    /// <c>[3]</c> the entry index inside that file. <c>CExoKeyTable::GetResID</c> copies both halves from
    /// <c>entry+0x20</c> on Linux @0x531460 and Windows RVA 0x18B1E0.</summary>
    public static readonly int KeyEntryResId = Field<uint>("CKeyTableEntry", "resId", count: 4);
    /// <summary>The table's container kind -- 1 KEY, 2 directory, 3 ERF, 5 manifest, which is what
    /// <c>CExoResMan::Exists</c> answers and what the texture-format race compares.
    /// <c>CExoKeyTable::BuildNewTable</c> writes <c>+0x20</c> in all four branches, Linux @0x5345a0 and
    /// Windows RVA 0x18A690.</summary>
    public static readonly int KeyTableKind = Field<int>("CExoKeyTable", "kind");
    /// <summary>The table's alias name (<c>"HAK:foo"</c>, <c>"OVERRIDE:"</c>, an NWSync manifest sha1) as a
    /// <c>CExoString</c>; <see cref="SymbolMap.AliasResolveFileName"/> turns it into a filesystem path.
    /// <c>BuildNewTable</c> initialises it at <c>+0x28</c> on both builds.</summary>
    public static readonly int KeyTableName = Field<IntPtr>("CExoKeyTable", "name");
    /// <summary>The table's array of <c>CExoResFile*</c> (KEY) or <c>CExoEncapsulatedFile*</c> (ERF).
    /// Every <c>ServiceFrom*</c> indexes <c>*(void**)(table+0x50)</c> on both builds.</summary>
    public static readonly int KeyTableFiles = Field<IntPtr>("CExoKeyTable", "files");
    /// <summary>A BIF's alias name (<c>"HD0LOCINSTALL:data/nwn_base"</c>, no extension) and its parsed
    /// variable-resource table. <c>CExoResFile::LoadHeader</c> hands <c>this+8</c> to <c>CExoFile</c> on both
    /// builds; the entry array is <c>+0x40</c> on Linux @0x52f460 and <c>+0x48</c> on Windows RVA 0x18BD80 --
    /// the one layout divergence in this batch, read straight off both <c>ReadResource</c> bodies.</summary>
    public static readonly int ResFileName = Field<IntPtr>("CExoResFile", "name");
    /// <inheritdoc cref="ResFileName"/>
    public static readonly int ResFileEntries = Field<IntPtr>("CExoResFile", "entries");
    /// <summary>A hak/mod/erf's alias name (copied verbatim from <see cref="KeyTableName"/> by
    /// <c>AddEncapsulatedContents</c>) and its resource list. Both <c>+0x08</c> and <c>+0x50</c> on Linux
    /// @0x5340c0 and Windows RVA 0x18CA10.</summary>
    public static readonly int EncapFileName = Field<IntPtr>("CExoEncapsulatedFile", "name");
    /// <inheritdoc cref="EncapFileName"/>
    public static readonly int EncapFileEntries = Field<IntPtr>("CExoEncapsulatedFile", "entries");
    /// <summary>One BIF variable-resource entry, as <c>CExoResFile::LoadHeader</c> expands it -- a
    /// <c>V1</c> file's 16 on-disk bytes plus a zero compressed flag and the size repeated, or a <c>E1</c>
    /// file's 0x18 bytes verbatim. <c>ReadResource</c> seeks to <c>+4</c>, reads <c>+8</c> bytes, and takes the
    /// compressed branch when <c>+0x10</c> is 1, decompressing to <c>+0x14</c>. Identical on both builds.</summary>
    public static readonly int BifEntryStride = Field("BiffVarResEntry", "stride");
    /// <inheritdoc cref="BifEntryStride"/>
    public static readonly int BifEntryOffset = Field<int>("BiffVarResEntry", "offset");
    /// <inheritdoc cref="BifEntryStride"/>
    public static readonly int BifEntryStored = Field<int>("BiffVarResEntry", "storedSize");
    /// <inheritdoc cref="BifEntryStride"/>
    public static readonly int BifEntryCompressed = Field<int>("BiffVarResEntry", "compressed");
    /// <inheritdoc cref="BifEntryStride"/>
    public static readonly int BifEntryPlain = Field<int>("BiffVarResEntry", "plainSize");
    /// <summary>One ERF/HAK resource-list entry. <c>CExoEncapsulatedFile::ReadResource</c> seeks to
    /// <c>+0</c>, reads <c>+4</c> bytes, takes the compressed branch on <c>+8 == 1</c> and decompresses to
    /// <c>+0xc</c> (what <c>GetResourceSize</c> returns). Identical on both builds.</summary>
    public static readonly int ErfEntryStride = Field("ErfResListEntry", "stride");
    /// <inheritdoc cref="ErfEntryStride"/>
    public static readonly int ErfEntryOffset = Field<int>("ErfResListEntry", "offset");
    /// <inheritdoc cref="ErfEntryStride"/>
    public static readonly int ErfEntryStored = Field<int>("ErfResListEntry", "storedSize");
    /// <inheritdoc cref="ErfEntryStride"/>
    public static readonly int ErfEntryCompressed = Field<int>("ErfResListEntry", "compressed");
    /// <inheritdoc cref="ErfEntryStride"/>
    public static readonly int ErfEntryPlain = Field<int>("ErfResListEntry", "plainSize");
    /// <summary><c>CExoBase</c> -&gt; the <c>CExoAliasList*</c> every path resolution goes through.
    /// <c>BuildNewTable</c> and <c>CExoAliasListInternal::ResolveFileName</c> both load
    /// <c>*(CExoAliasList**)(g_pExoBase+0x20)</c> on both builds.</summary>
    public static readonly int ExoBaseAliasList = Field<CX.Engine.CExoAliasList>("CExoBase", "aliasList");
    /// <summary>How many <c>CNWTileData</c> records a loaded tileset has -- the bound
    /// <c>CNWTileSet::GetTileData(int)</c> tests before indexing its array (Linux @0x5e10d0 <c>+0x4c</c>,
    /// Windows rva 0x2c67d0 <c>+0x54</c>, one EXOWARNING past it), so the model-prefetch walk stops here
    /// instead of probing for the end.</summary>
    public static readonly int TileSetTileCount = Field<int>("CNWTileSet", "tileCount");
    /// <summary>CNWTileSet -&gt; the area's HEIGHT STEP, the multiplier <c>CNWCArea::LoadArea</c> applies to
    /// a tile record's height index. A field, not a call: <c>CNWTileSet::GetHeightTransition</c> @0x5e10c0 is
    /// one load and the Windows export (rva 0x2c67c0) the same load 8 bytes higher -- the delta
    /// <see cref="TileSetTileCount"/> also has, so both were read and neither assumed.</summary>
    public static readonly int TileSetHeightTransition = Field<float>("CNWTileSet", "heightTransition");

    /// <summary>The 0x38-byte helper <c>GetCompressedRaster</c> leaks as the owner of a DDS it hands
    /// out (the pointer stored 8 bytes before the resource) holds its <c>CRes*</c> here --
    /// <c>LoadCompressedTexture</c> @0x1133ab0 / Windows RVA 0xF3100 both demand <c>*(CRes**)(this+0x18)</c>.</summary>
    public static readonly int DdsHelperRes = Field<IntPtr>("CAuroraCompressedTextureDDS", "res");
    /// <summary><c>CResTGA</c>'s parse state, set by <c>OnResourceServiced</c> (@0x1154a60 / Windows rva
    /// 0x1a4730, which read and write the same offsets): the header (the resource bytes), the colour map, the
    /// pixels (in place, or a fresh RLE buffer) and the parsed latch.</summary>
    public static readonly int TgaHeader = Field<IntPtr>("CResTGA", "header");
    /// <inheritdoc cref="TgaHeader"/>
    public static readonly int TgaColorMap = Field<IntPtr>("CResTGA", "colorMap");
    /// <inheritdoc cref="TgaHeader"/>
    public static readonly int TgaPixels = Field<IntPtr>("CResTGA", "pixels");
    /// <inheritdoc cref="TgaHeader"/>
    public static readonly int TgaParsed = Field<int>("CResTGA", "parsed");
    /// <summary>Ownership: the rest of <c>OnResourceServiced</c>'s writes, same offsets on both OSes.
    /// <c>owned</c> = the pixels are a <c>new[]</c> rather than the resource's own, <c>colorMapped</c> (TGA
    /// type 1/9), <c>compressed</c> (9/10), <c>size</c> the pixel bytes and <c>srcSize</c> the file span an
    /// RLE image was read from.</summary>
    public static readonly int TgaOwned = Field<int>("CResTGA", "owned");
    /// <inheritdoc cref="TgaOwned"/>
    public static readonly int TgaColorMapped = Field<int>("CResTGA", "colorMapped");
    /// <inheritdoc cref="TgaOwned"/>
    public static readonly int TgaCompressed = Field<int>("CResTGA", "compressed");
    /// <inheritdoc cref="TgaOwned"/>
    public static readonly int TgaSize = Field<int>("CResTGA", "size");
    /// <inheritdoc cref="TgaOwned"/>
    public static readonly int TgaSrcSize = Field<int>("CResTGA", "srcSize");

    // ---- the occlusion fade (#537): the camera flags, the hit record, the gob's object kind ----

    /// <summary>CNWCModule -&gt; the <c>CAMERA_FLAG_*</c> bitmask the server's <c>SetCameraFlags</c> lands in;
    /// bit <see cref="CameraFlagDisableCollision"/> is what <c>UpdateCameraCollision</c> tests. Paired on
    /// that function: Linux @0x7dce30 and Windows rva 0x7d07d0 both read <c>+0x140 == 0 &amp;&amp;
    /// (byte)+0x2bc &amp; 2</c> before dispatching on <c>CClientOptions+0x1d</c>.</summary>
    public static readonly int ModuleCameraFlags = Field<int>("CNWCModule", "cameraFlags");

    // ---- camera-relative drive (#540): UpdateDriveMode's own fields. Linux @0x65f780 and Windows rva 0x6f1e40
    //      read every one of these at the SAME offsets (one decompile per build, tools/ghidra/DecompOne.java);
    //      the creature rows below are the two the function gates on, shifted -0x20 on Windows like `plated`. ----

    /// <summary>CClientExoAppInternal -&gt; "drive input changed" latch (int 0/1): HandleInputEvent raises it on
    /// any drive key edge, UpdateDriveMode sends <c>DriveControl</c> while it is up and clears it. No Byte01
    /// probe: the ctor zeroes +0x1b4.. but never +0x1b0, so before the first UpdateDriveMode returns it holds heap
    /// garbage and a probe there fails at random (the "camera drive stopped working" of 2026-09-16).</summary>
    public static readonly int InternalDriveChanged = Field<int>("CClientExoAppInternal", "driveChanged");
    /// <summary>CClientExoAppInternal -&gt; the walk-forward key (int 0/1): keymap action 0x59 "Drive Mode Forward",
    /// held like the six below (press/release, release aborts). UpdateDriveMode reads it only while
    /// <see cref="InternalDriveKeys"/>[0] is clear and then flags walk (0x01), or run (0x03) when
    /// <c>CClientOptions::m_bAlwaysRun</c> (<c>[[app]+4]</c>) is set. Not an auto-run toggle.</summary>
    public static readonly int InternalDriveWalk = Field<int>("CClientExoAppInternal", "driveWalk", Expect.Byte01);
    /// <summary>CClientExoAppInternal -&gt; six int key booleans in a row: forward run (action 0x5a "Drive Mode Forward
    /// Run"; flags run 0x03, or walk 0x01 under the Shift walk modifier <c>creature+0x2b0</c>), backward, turn left,
    /// turn right, strafe left, strafe right (+0x1b8..+0x1cc). <c>ClearDriveMode</c> zeroes +0x1b4..+0x1cf in one go.</summary>
    public static readonly int InternalDriveKeys = Field<int>("CClientExoAppInternal", "driveKeys", Expect.Byte01, count: 6);
    /// <summary>CClientExoAppInternal -&gt; microsecond stamp of the last drive packet (the 500 ms keep-alive
    /// compares against it); rewritten on every send, so a change across the call means a packet went out.</summary>
    public static readonly int InternalDriveLastSend = Field<long>("CClientExoAppInternal", "driveLastSend");
    /// <summary>CNWCObject -&gt; orientation Vector (x, y, z); position is the Vector at +0x38. UpdateDriveMode reads
    /// +0x44/+0x48 for the wire angle and TurnOnSpot rotates +0x44..+0x4c on both builds.</summary>
    public static readonly int ObjOrientation = Field<System.Numerics.Vector3>("CNWCObject", "orientation");
    /// <summary>CNWCObject -&gt; position Vector, the 12 bytes before <see cref="ObjOrientation"/> (<c>SetPosition</c>
    /// Linux @0x7e9180 writes +0x38..+0x40; the Windows orientation row at the same +0x44 pins it).</summary>
    public static readonly int ObjPosition = Field<System.Numerics.Vector3>("CNWCObject", "position");
    // ---- action-camera aim assist / auto-target / contextual crosshair (CX-125) ----
    // Every row below was read off ONE function pair: CNWCArea::HiliteAreaObjects (Linux @0x7119f0, Windows rva
    // 0x7db2f0), CClientExoAppInternal::UpdateMouseCursorAppearance (Linux @0x6629b0, Windows rva 0x6f2830),
    // CActionTarget::HandleLMouseDown (Linux @0x965840, Windows rva 0x83be00) and
    // CCachedMouseCursorManager::SetMouseCursor (Linux @0x6b72a0, Windows rva 0x83b3e0).

    /// <summary>CNWCObject -&gt; the live fade state (ONE byte, 1 = faded in / on screen). The highlight
    /// walk's first filter: <c>HiliteAreaObjects</c> skips the object while it is 0, reading a byte on both
    /// builds. Its writers are <c>CNWCObject::FadeObject</c> @0x7e7720 and <c>SetDesiredFadeState</c>. Read as
    /// a byte: the three above it are the desired state and padding, so no <c>Byte01</c> probe.</summary>
    public static readonly int ObjVisible = Field<byte>("CNWCObject", "visible");
    /// <summary>CNWCObject -&gt; "may be highlighted right now" (int). Same walk: zero and the engine un-hilites
    /// instead of hiliting. Identical offset on both builds. <b>Placeables carry it elsewhere</b> -- the type-9
    /// branch reads it through <see cref="GameObjAsNWCPlaceableSlot"/>, not on the CNWCObject.</summary>
    public static readonly int ObjHighlightable = Field<int>("CNWCObject", "highlightable");
    /// <summary>CNWCArea -&gt; the area's object-id array (<c>uint*</c>) and its count. <c>HiliteAreaObjects</c>
    /// walks <c>*(uint*)(*(area+0x318) + i*4)</c> for <c>*(int*)(area+0x320)</c> entries on both builds.</summary>
    public static readonly int AreaObjectIds = Field<IntPtr>("CNWCArea", "objectIds");
    /// <inheritdoc cref="AreaObjectIds"/>
    public static readonly int AreaObjectCount = Field<int>("CNWCArea", "objectCount");
    /// <summary>CNWCArea -&gt; the placeable-light registry: an array of POINTERS to 16-byte records
    /// {uint object id, Gob*}, not the records themselves. <c>CNWCArea::AddPlaceableObjectLight</c> @0x705120
    /// and <c>GetPlaceableObjectLight</c> @0x7057e0 walk exactly this; each Gob is a one-node
    /// <c>fx_placeable01</c> that owns one PartLight and no mesh.</summary>
    public static readonly int AreaPlaceableLights = Field<IntPtr>("CNWCArea", "placeableLights");
    /// <inheritdoc cref="AreaPlaceableLights"/>
    public static readonly int AreaPlaceableLightCount = Field<int>("CNWCArea", "placeableLightCount");
    /// <summary>One registry record's <c>Gob*</c> (the object id is at +0). Same on both binaries:
    /// <c>AddPlaceableObjectLight</c> writes the pair at +0/+8 on the PE decompile too (rva 0x7d8ed0).</summary>
    public static readonly int PlaceableLightGob = Field<CX.Engine.Gob>("CNWCPlaceableObjectLight", "gob");
    /// <summary>CNWCArea -&gt; the Static-placeable list, a <c>CExoArrayList&lt;unsigned int&gt;</c> of
    /// object ids (data +0x338, count +0x340) filled by <c>AddPlaceableObjectStatic</c> @0x7091c0. No per-object
    /// Static flag exists client-side (+0x1b4 is PLOT); membership here IS the static set. docs/engine-layout.md.</summary>
    public static readonly int AreaStaticPlaceables = Field<IntPtr>("CNWCArea", "staticPlaceables");
    /// <inheritdoc cref="AreaStaticPlaceables"/>
    public static readonly int AreaStaticPlaceableCount = Field<int>("CNWCArea", "staticPlaceableCount");
    /// <summary>The client's message read cursor: the buffer being read, its length and the byte offset
    /// reached. <c>CNWMessage::ReadDWORD</c> and friends (@0x5e8450 / rva 0x2d9bc0) are plain little-endian
    /// loads at the cursor, so a fixed-shape record still ahead in the buffer can be read without disturbing
    /// the engine's own parse. Only sub-byte widths go through <c>ReadBits</c>.</summary>
    public static readonly int MsgBuffer = Field<IntPtr>("CNWMessage", "buffer");
    /// <inheritdoc cref="MsgBuffer"/>
    public static readonly int MsgLength = Field<uint>("CNWMessage", "length");
    /// <inheritdoc cref="MsgBuffer"/>
    public static readonly int MsgCursor = Field<uint>("CNWMessage", "cursor");
    /// <summary><c>CNWRules</c> -&gt; its 2DA table, and that table -&gt; placeables.2da. Both hops read
    /// straight off <c>g_pRules</c> in <c>CNWCArea::AddPlaceableObjectStatic</c> and
    /// <c>RespawnStaticPlaceablesForTile</c>, and the Windows side (rva 0x7d95a0) takes the same two.</summary>
    public static readonly int RulesTwoDimArrays = Field<IntPtr>("CNWRules", "twoDimArrays");
    /// <inheritdoc cref="RulesTwoDimArrays"/>
    public static readonly int TwoDimPlaceables = Field<IntPtr>("CTwoDimArrays", "placeables");
    /// <summary>The 2DA table -&gt; appearance.2da (+0x100), surfacemat.2da (+0x110) and footstepsounds.2da (+0x200):
    /// the three tables <c>CNWCCreature::PlayFootstep</c> reads (Linux @0x72ff90, Windows rva 0x8057c0), the same
    /// hops on both builds.</summary>
    public static readonly int TwoDimAppearance = Field<IntPtr>("CTwoDimArrays", "appearance");
    /// <inheritdoc cref="TwoDimAppearance"/>
    public static readonly int TwoDimSurfaceMat = Field<IntPtr>("CTwoDimArrays", "surfacemat");
    /// <inheritdoc cref="TwoDimAppearance"/>
    public static readonly int TwoDimFootstepSounds = Field<IntPtr>("CTwoDimArrays", "footstepSounds");
    /// <summary>CNWCObject -&gt; its <c>CNWCArea*</c>, the area <c>PlayFootstep</c> asks <c>GetSurfaceMaterial</c> (+0x30 on
    /// both builds: a CNWCObject field, no creature shift).</summary>
    public static readonly int ObjAreaPtr = Field<IntPtr>("CNWCObject", "areaPtr");
    /// <summary>CNWCObject -&gt; nonzero = <c>PlayFootstep</c> returns at once, no sound and no visual (+0x108 on both
    /// builds). Only that use is known.</summary>
    public static readonly int ObjFootstepSkip = Field<int>("CNWCObject", "footstepSkip");
    /// <summary>CNWCCreature -&gt; the footstepsounds.2da row override; -1 = the appearance's <c>FootstepType</c>.
    /// <c>PlayFootstep</c>'s first read (Linux +0x3dc, Windows +0x3bc: the class's usual -0x20).</summary>
    public static readonly int CreatureFootstepType = Field<int>("CNWCCreature", "footstepType");
    /// <summary>CNWCCreature -&gt; 1 forces footstepsounds row 3 ("soft") whatever the type, in <c>PlayFootstep</c>
    /// (Linux +0x258, Windows +0x238).</summary>
    public static readonly int CreatureSoftStep = Field<int>("CNWCCreature", "softStep");
    /// <summary>CNWCObject -&gt; its <c>CNWCAnimBase*</c> (<c>CNWCObject::SetPosition</c> @0x7e9180 / rva
    /// 0x766760 reads it before the anim-base vcall). Hop one of id -&gt; placeable -&gt; anim base -&gt;
    /// <see cref="AnimGob"/>; the gob's composed transform is then <see cref="NodeMatrix"/> off its parts.</summary>
    public static readonly int ObjAnimBase = Field<CX.Engine.CNWCAnimBase>("CNWCObject", "animBase");
    /// <summary>CNWCCreature -&gt; the hovered object id mirrored onto the PLAYER creature (0x7f000000 = nothing).
    /// <c>UpdateMouseCursorAppearance</c> is the only writer: it stores the newly picked id there and clears it to
    /// 0x7f000000 when the hover drops. Linux +0x448, Windows +0x428 -- the same -0x20 CNWCCreature delta
    /// <see cref="CreaturePlated"/> and the nameplate rows carry.</summary>
    public static readonly int CreatureHoveredOid = Field<uint>("CNWCCreature", "hoveredOid");
    /// <summary>CClientExoAppInternal -&gt; CCachedMouseCursorManager*. Every <c>SetMouseCursor</c> call site in
    /// <c>UpdateMouseCursorAppearance</c> passes <c>*(internal + 0xc8)</c> on both builds.</summary>
    public static readonly int InternalCursorManager = Field<CX.Engine.CCachedMouseCursorManager>("CClientExoAppInternal", "cursorManager");
    /// <summary>CCachedMouseCursorManager -&gt; the live cursor id (int), the one <c>SetMouseCursor</c> early-outs on
    /// and then stores. 1 default, 3 walk, 7 attack, 0x1d magic, 0x5b pickup... (the id table is in the research
    /// doc); 0x125 is the ceiling the setter enforces. Same offset on both builds.</summary>
    public static readonly int CursorCurrentId = Field<int>("CCachedMouseCursorManager", "currentCursor");
    /// <summary>CActionTarget -&gt; the allowed-target array: pairs of int {objectType, cursorId}, stride 8, count
    /// at <see cref="ActionTargetAllowedCount"/>. Type 2 is the synthetic "ground point" (AoE); the rest are the
    /// engine object types. Read off both <c>HandleLMouseDown</c> bodies; MSVC packs the CGuiCallback
    /// members in 16 bytes where Itanium needs 24, so everything past +0x80 shifts.</summary>
    public static readonly int ActionTargetAllowed = Field<IntPtr>("CActionTarget", "allowed");
    /// <inheritdoc cref="ActionTargetAllowed"/>
    public static readonly int ActionTargetAllowedCount = Field<int>("CActionTarget", "allowedCount");
    /// <summary>CClientExoAppInternal -&gt; the live <c>CActionTarget*</c>: <c>RegisterActionTargetGUI</c> (Linux
    /// @0x645530, Windows rva 0x6de8e0) is a one-line store to +0x108 on both, called with <c>this</c> from
    /// <c>ActivateTargetMode</c> and with null from <c>Cleanup</c> (both the fire and the cancel path), so it is
    /// the "is this mode still up" read a deferred cast checks before touching the pointer again.</summary>
    public static readonly int InternalActionTarget = Field<CX.Engine.CActionTarget>("CClientExoAppInternal", "actionTarget");
    /// <summary>CNWCCreature -&gt; the server-sent attitude byte: 1 friend, 0 neutral, anything else (2) hostile.
    /// <c>HiliteAreaObjects</c> picks the enemy/friend/neutral colour on it and <c>PerformDefaultActionOnCreature</c>
    /// talks below 2 and attacks at 2. Linux +0x3a8, Windows +0x388 (the Windows HiliteAreaObjects, rva 0x7db2f0,
    /// same three-way branch).</summary>
    public static readonly int CreatureAttitude = Field<byte>("CNWCCreature", "attitude");
    /// <summary>CNWCObject -&gt; the script highlight colour override (Vector; x &lt; 0 = none), the first thing every
    /// <c>HiliteAreaObjects</c> branch tests before falling back to the type's global. Same offset on both.</summary>
    public static readonly int ObjHiliteColour = Field<float>("CNWCObject", "hiliteColour", count: 3);
    /// <summary>Virtual slots of <c>CNWCObject::Hilite(Vector, uchar channel, float, int glow)</c> and
    /// <c>UnHilite(uchar channel)</c>, called through the vtable because CNWCCreature overrides both.
    /// <c>HiliteAreaObjects</c> calls them with <c>(colour, 2, 0.0f, 1)</c> and <c>(2)</c> on both builds; the
    /// Vector crosses by value in the <see cref="CX.Engine.VectorArg"/> shape.</summary>
    public static readonly int ObjHiliteSlot = Field("CNWCObject", "vtbl.Hilite", Expect.VtblSlot,
                                                    sig: "void(VectorArg colour, byte channel, float fade, int glow)");
    /// <inheritdoc cref="ObjHiliteSlot"/>
    public static readonly int ObjUnHiliteSlot = Field("CNWCObject", "vtbl.UnHilite", Expect.VtblSlot, sig: "int(byte channel)");
    /// <summary>Virtual slots of <c>CNWCObject::MouseOver(int)</c> and <c>MouseOff()</c>, which the creature
    /// and door arms of <c>HiliteAreaObjects</c> call on both builds. <c>MouseOver(1)</c> raises the feedback
    /// plate (<c>ShowFeedbackBubbleText</c> and the flag <see cref="CreaturePlated"/>) and <c>MouseOff</c>
    /// drops it -- the tab-target lock's plate.</summary>
    public static readonly int ObjMouseOverSlot = Field("CNWCObject", "vtbl.MouseOver", Expect.VtblSlot, sig: "void(int on)");
    /// <inheritdoc cref="ObjMouseOverSlot"/>
    public static readonly int ObjMouseOffSlot = Field("CNWCObject", "vtbl.MouseOff", Expect.VtblSlot, sig: "void()");
    /// <summary>Virtual slot of <c>CGameObject::AsNWCObject()</c> -- what the area walk calls on every id it
    /// resolves. Linux <c>vtbl+0x20</c>, Windows <c>vtbl+0x18</c>: the usual Itanium two-dtor-slot shift.</summary>
    public static readonly int GameObjAsNWCObjectSlot = Field("CGameObject", "vtbl.AsNWCObject", Expect.VtblSlot, sig: "CNWCObject()");
    /// <summary>Virtual slot of <c>CGameObject::AsNWCPlaceable()</c> -- the walk's placeable branch tests
    /// <see cref="ObjHighlightable"/> on what THIS answers rather than on the CNWCObject. The base answers
    /// null and <c>CNWCPlaceable</c>'s override is an identity <c>return this</c>, so it is a type test that
    /// hands back the same address; mirroring it keeps a non-placeable carrying type byte 9 out.</summary>
    public static readonly int GameObjAsNWCPlaceableSlot = Field("CGameObject", "vtbl.AsNWCPlaceable", Expect.VtblSlot, sig: "CNWCObject()");

    // ---- what the player is doing right now: the action camera's crosshair feedback (CX-123) ----
    // Measured 2026-09-17 by decompiling the same function on both programs, never by extrapolating a class delta.

    /// <summary>CGuiInGame -&gt; CPanelActionQueue*, the client's mirror of the player's action queue. Both
    /// <c>HandleServerToPlayerUpdate_PlayerInfo</c> bodies (Linux @0x8a9f90, Windows rva 0x7b4f70) null-check
    /// <c>GetInGameGui()+0xa8</c> and hand exactly that to <c>ClearAllActions</c> / <c>AddAction</c>.</summary>
    public static readonly int GuiInGameActionQueue = Field<CX.Engine.CPanelActionQueue>("CGuiInGame", "actionQueuePanel");

    /// <summary>CPanelActionQueue -&gt; the "Current" button slot (<c>CPanelActionQueueButton*</c>, null = idle).
    /// <c>AddAction</c> fills it first and only overflows into the queued list, so "an action is pending" is this
    /// plus <see cref="ActionQueueCount"/>. Linux @0x7761b0 stores to <c>+0x158</c>, Windows rva 0x873df0 to
    /// <c>+0x150</c>.</summary>
    public static readonly int ActionQueueCurrent = Field<IntPtr>("CPanelActionQueue", "current");

    /// <summary>CPanelActionQueue -&gt; the queued-button count (int), the list at <c>current-8</c>'s array.
    /// <c>ClearAllActions</c> drains <c>while (0 &lt; count)</c> and <c>DeleteAction</c> early-outs on
    /// <c>count &lt; 1</c> on both builds (Linux @0x776100 / @0x776020, Windows rva 0x874670 / 0x8748b0).</summary>
    public static readonly int ActionQueueCount = Field<int>("CPanelActionQueue", "queuedCount");

    /// <summary>CNWCCreature -&gt; the server-sent AI state byte; <b>2 = attacking</b> and nothing else in the
    /// enumeration is established. Two independent call sites on each build:
    /// <c>WalkPlayerToPoint</c> (Linux @0x763950, Windows rva 0x6f3650) gates its "already close to my target"
    /// test on <c>== 2</c>, and <c>CPanelQuickBar::UpdateAttackButtons</c> paints IR_DISENGAGE on it.</summary>
    public static readonly int CreatureAiState = Field<byte>("CNWCCreature", "aiState");

    /// <summary>CNWCCreature -&gt; whom that creature is attacking (object id), written by
    /// <c>CreatureUpdate_Update</c> only while <see cref="CreatureAiState"/> is 2. Both
    /// <c>WalkPlayerToPoint</c> bodies resolve <c>GetGameObject(*(uint*)(creature + this))</c> inside the
    /// <c>== 2</c> branch.</summary>
    public static readonly int CreatureAiTarget = Field<uint>("CNWCCreature", "aiTarget");

    /// <summary>CNWCObject -&gt; current animation (short): <c>GetCurrentAnimation</c> is <c>*(short*)(this+0x5c)</c> on
    /// both builds (Linux @0x7e3f70, Windows rva 0x7630d0, the chase driver's own callee). Locomotion = 2, 3, 4,
    /// 0x4e, 0x4f, 0x54..0x57 (the chase drivers' test).</summary>
    public static readonly int ObjAnimation = Field<short>("CNWCObject", "animation");
    /// <summary>CNWCCreature -&gt; the server's <c>m_nAIState</c> WORD: UpdateDriveMode and DrivePlayer both
    /// refuse unless bit 2 is set. <b>0 = inert</b> -- the update handler puts such a creature on the
    /// hit-check ignore list and the click default treats it as a corpse to loot, so the tab-target lock reads
    /// 0 as "dead".</summary>
    public static readonly int CreatureAiFlags = Field<short>("CNWCCreature", "aiFlags");
    /// <summary>CNWCCreature -&gt; int that must be non-zero for drive input to act at all (UpdateDriveMode's second
    /// gate). Linux +0x43c, Windows +0x41c.</summary>
    public static readonly int CreatureDriveEnabled = Field<int>("CNWCCreature", "driveEnabled");
    /// <summary>CNWCCreature -&gt; the local facing target Vector the model turns toward: DrivePlayer's ±30° turn arm
    /// writes it alone, and UpdateDriveMode writes it together with <see cref="CreatureFacingTargetB"/> right after
    /// every drive packet. Linux +0x2c8, Windows +0x2a8.</summary>
    public static readonly int CreatureFacingTarget = Field<System.Numerics.Vector3>("CNWCCreature", "facingTarget");
    /// <summary>CNWCCreature -&gt; the second Vector of that pair (+0x2d4 / +0x2b4), written with the same direction on
    /// the send path and by TurnOnSpot; its own reader is not identified.</summary>
    public static readonly int CreatureFacingTargetB = Field<System.Numerics.Vector3>("CNWCCreature", "facingTargetB");
    /// <summary>CNWCCreature -&gt; the desired position Vector: <c>WalkUpdateLocation</c> steps it along the waypoints each
    /// frame (WalkUpdateLocationDistance starts from it) and <c>MoveCreatureToPosition</c> lerps the displayed +0x38
    /// toward it; the update handler and EvaluateNewWayPoints overwrite it from the server. Every drive line
    /// test starts here. Read off the <c>WalkUpdateLocation</c> bodies on both builds.</summary>
    public static readonly int CreatureDesiredPos = Field<System.Numerics.Vector3>("CNWCCreature", "desiredPos");
    /// <summary>CNWCCreature -&gt; int 0/1 "I am driving": set by ClientSideWalkCommand in drive mode and by a server
    /// update that says so, cleared when the walk ends. Linux +0x318, Windows +0x2f8. No Byte01 probe: the ctor
    /// is not known to zero it.</summary>
    public static readonly int CreatureDriving = Field<int>("CNWCCreature", "driving");

    // ---- CX-67: the gait signals. Every row below is read off ONE function pair, CNWCCreature::WalkUpdateLocation
    // (Linux @0x725830, Windows rva 0x8114d0), which on Windows carries ComputeSpeedFactor inlined -- so the ramp
    // divisor, the path walk and the trapezoid step are all in the one body. The block shift is the CNWCCreature
    // -0x20 the rows above already carry. CX-148.

    /// <summary>CNWCCreature -&gt; the speed factor, 0.1..1.0, that <c>ComputeSpeedFactor</c> returns and
    /// <c>WalkUpdateLocation</c> stores: the fraction of the rate the body is travelling at, and the engine's own
    /// ramp and stop ease. Reset to 0 on arrival. Linux +0x2fc, Windows +0x2dc -- both bodies read it as the
    /// trapezoid's previous term and write the new one back at the same slot.</summary>
    public static readonly int CreatureSpeedFactor = Field<float>("CNWCCreature", "speedFactor");
    /// <summary>CNWCCreature -&gt; the special-walk rate multiplier, applied to the rate on the driving arm only
    /// (1.0 in practice). Linux +0x300, Windows +0x2e0: the <c>rate *= this</c> immediately under the driving
    /// test in both bodies.</summary>
    public static readonly int CreatureSpecialWalk = Field<float>("CNWCCreature", "specialWalk");
    /// <summary>CNWCCreature -&gt; the walk and run rates in mm/s, server-supplied. <c>WalkUpdateLocation</c> picks
    /// the walk on animation 2, 3, 0x54, 0x55 (and looks WALKDIST up) and the run on 4, 0x56, 0x57 (RUNDIST);
    /// the pure sidesteps 0x4e/0x4f use a hardcoded 1600 mm/s instead. Linux +0x310/+0x314, Windows
    /// +0x2f0/+0x2f4 -- the same two animation-id chains reach them on both builds.</summary>
    public static readonly int CreatureWalkRate = Field<float>("CNWCCreature", "walkRate");
    /// <inheritdoc cref="CreatureWalkRate"/>
    public static readonly int CreatureRunRate = Field<float>("CNWCCreature", "runRate");
    /// <summary>CNWCCreature -&gt; the path's provenance (int): 1 = the server sent it (<c>EvaluateNewWayPoints</c>)
    /// and the speed factor ramps over divisor 500, 0 = the client predicted it (<c>ClientSideWalkCommand</c>) and
    /// the divisor is 1000. <b>Not</b> the driving flag, which is <see cref="CreatureDriving"/>. Both builds
    /// show the same <c>== 1</c> picking 500.0 over 1000.0, at the head of ComputeSpeedFactor.</summary>
    public static readonly int CreaturePathFromServer = Field<int>("CNWCCreature", "pathFromServer");
    /// <summary>CNWCCreature -&gt; the waypoint array (<c>Vector*</c>, stride 12), the cursor into it, and the
    /// count (ushort). The remaining path is <c>|cursor[0] - desiredPos|</c> plus every leg from there to
    /// <c>array + count</c>, measured in 2-D (z is forced to 0 in every leg). Linux +0x2b8/+0x2c0/+0x2f0,
    /// Windows +0x298/+0x2a0/+0x2d0 -- the one path-length loop, identical on both builds.</summary>
    public static readonly int CreatureWaypoints = Field<IntPtr>("CNWCCreature", "waypoints");
    /// <inheritdoc cref="CreatureWaypoints"/>
    public static readonly int CreatureWaypointCursor = Field<IntPtr>("CNWCCreature", "waypointCursor");
    /// <inheritdoc cref="CreatureWaypoints"/>
    public static readonly int CreatureWaypointCount = Field<ushort>("CNWCCreature", "waypointCount");
    /// <summary>CNWCCreature -&gt; the server-correction countdown in milliseconds, 0 in the normal case:
    /// <c>MoveCreatureToPosition</c> (Linux @0x71cd70, Windows rva 0x804840) snaps the drawn position onto the
    /// desired one once <see cref="ObjStepMs"/> reaches it and otherwise closes the gap by
    /// <c>this/(this + step)</c>. <c>ResetDesiredPositionTimer</c> sets it to 512.</summary>
    public static readonly int CreatureCorrectionMs = Field<uint>("CNWCCreature", "correctionMs");
    /// <summary>CNWCObject -&gt; the world-time delta of this AI update, whole milliseconds. It is the clock
    /// the position steps on -- the pose layer's own float seconds are a different clock -- so a speed read
    /// off the engine divides by this, not by the frame time. A CNWCObject field, so the CNWCCreature shift
    /// does not apply: the same offset on both.</summary>
    public static readonly int ObjStepMs = Field<uint>("CNWCObject", "stepMs");
    /// <summary>CNWCCreature -&gt; pointer to the creature's pathfind block (the object whose +0xcc ushort indexes
    /// creaturespeed.2da). Linux +0x320, Windows +0x300.</summary>
    public static readonly int CreaturePathfind = Field<CX.Engine.CPathfindInformation>("CNWCCreature", "pathfind");
    /// <summary>Pathfind block -&gt; two consecutive floats (+0xe0, +0xe4) that ClientSideWalkCommand, WalkPlayerToPoint
    /// and EvaluateNewWayPoints hand <c>CNWCArea::TestDirectLine</c> as its 4th and 5th arguments (personal space and
    /// height, order as the engine passes them). Same on both builds (WalkPlayerToPoint rva 0x6f3650).</summary>
    public static readonly int PathfindSpaceArgs = Field<float>("CPathfindInformation", "spaceArgs", count: 2);
    /// <summary>Pathfind block -&gt; the ushort <c>PlayFootstep</c> hands appearance.2da as the row of its
    /// <c>FootstepType</c> and <c>SIZECATEGORY</c> reads (+0xcc on both builds; the creature's appearance).</summary>
    public static readonly int PathfindAppearance = Field<ushort>("CPathfindInformation", "appearance");
    /// <summary>Virtual slot of <c>CNWCCreature::SetPosition(Vector)</c>: MoveCreatureToPosition calls Linux
    /// <c>vtbl+0x130</c> (@0x71cd70), Windows <c>+0x128</c> (rva 0x804840), the class's usual -8. The Vector crosses by
    /// value (SysV xmm0/xmm1, Win64 a pointer to a copy), which is <see cref="CX.Engine.VectorArg"/>.</summary>
    public static readonly int CreatureSetPositionSlot = Field("CNWCCreature", "vtbl.SetPosition", Expect.VtblSlot,
                                                               sig: "void(VectorArg pos)");
    /// <summary>CNWCModule -&gt; "chase suppressed" (int 0/1): <c>UpdateCameraModeChaseCam/EQ</c> set it the frame the
    /// user moved the camera (+0x134) and skip the chase while it holds; cleared on the creature's next
    /// idle-to-locomotion edge, which <see cref="ModuleChaseWasIdle"/> records. Same on Windows (rva 0x7d0d00 /
    /// 0x7d1c90 read all four).</summary>
    public static readonly int ModuleChaseSuppressed = Field<int>("CNWCModule", "chaseSuppressed", Expect.Byte01);
    /// <summary>CNWCModule -&gt; "creature was idle" (int 0/1) the chase drivers set on a non-locomotion animation and
    /// consume (clearing <see cref="ModuleChaseSuppressed"/>) on the first locomotion frame after.</summary>
    public static readonly int ModuleChaseWasIdle = Field<int>("CNWCModule", "chaseWasIdle", Expect.Byte01);
    /// <summary>CNWCModule -&gt; the server's <c>SetCameraLimits</c> max distance (float; -1 = not set, use the client
    /// config). Linux @0x7def00 writes the four in order (min pitch, max pitch, min dist, max dist) and
    /// Windows rva 0x7cf9d0 the same; it is the camera's leash radius when set.</summary>
    public static readonly int ModuleCameraMaxDist = Field<float>("CNWCModule", "cameraMaxDist");
    /// <summary>CNWCCreature -&gt; combat state int, server-fed: <c>SetCombatState(int)</c> (Linux @0x722160, Windows
    /// rva 0x80a230, the callee right after the AI-flags cluster in the creature update handler) stores it
    /// first thing. The free camera stands down on it.</summary>
    public static readonly int CreatureCombatState = Field<int>("CNWCCreature", "combatState");
    /// <summary>CGuiInGame -&gt; "toggled off" int: <c>ToggleOff</c> returns early when set, <c>UnToggleOff</c> when
    /// clear, and <c>SetCutsceneMode</c> reads it to know whether the exit should un-toggle. Linux +0x3d4, Windows +0x374.</summary>
    public static readonly int GuiInGameToggledOff = Field<int>("CGuiInGame", "toggledOff", Expect.Byte01);

    // ---- head tracking: the LookAtAnimation the engine already owns ----
    // One function pair carries every row: LookAtAnimation::Control (Linux @0x4973c0, Windows rva 0x50650, the
    // name-exported ?Control@LookAtAnimation@@UEAAXXZ) reads all four, and the ctor (Linux @0x49d480, Windows
    // rva 0x4d0a0) writes them in the same order on both builds. The class is laid out identically.

    /// <summary>CNWCCreature -&gt; virtual slot of <c>GetModel(unsigned char, int)</c>, which answers the
    /// creature's Gob (<c>*(animBase + 0xc8)</c>, <see cref="AnimGob"/>). Measured on the one function
    /// that calls it on both builds, <c>CNWCCreature::LookAt</c> (Linux @0x726890 uses +0x148, Windows rva
    /// 0x803f20 uses +0x140): the -8 every virtual destructor costs MSVC.</summary>
    public static readonly int CreatureGetModelSlot =
        Field("CNWCCreature", "vtbl.GetModel", Expect.VtblSlot, sig: "IntPtr(byte part, int flag)");

    /// <summary>LookAtAnimation -&gt; the "finished" byte <c>Deactivate</c> sets and <c>Gob::Animate</c> deletes
    /// the object on. Control's own gate: set, the aim block is skipped for the release latch at +0x68.</summary>
    public static readonly int LookAtFinished = Field<byte>("LookAtAnimation", "finished");

    // ---- CX-106 anim: the owned animation runtime (CX-106) ----
    // Every row below was measured on BOTH builds by disassembling the same function pair on each:
    // Scene::Animate (0x469810 / rva 0xc5210), Gob::Animate (0x49d8c0 / rva 0x4dbd0),
    // AnimateHierarchy (0x499990 / rva 0x4fe90), AnimateEvents (0x497f30 / rva 0x4fce0) and
    // MdlNodeAnimMesh::InternalControl (0x499320 / rva 0x51e60). Only two of them move.

    /// <summary>Scene -&gt; its animation frame counter. <c>Gob::Animate</c> compares
    /// <see cref="GobAnimFrame"/> against it and returns outright when they match, which is what makes it
    /// safe for one gob to be reached twice in a frame.</summary>
    public static readonly int SceneAnimFrame = Field<int>("Scene", "animFrame");
    /// <inheritdoc cref="SceneAnimFrame"/>
    public static readonly int GobAnimFrame = Field<int>("Gob", "animFrame");
    /// <summary>Gob -&gt; the PREVIOUS frame's world position, latched by <c>Gob::Animate</c> right after the
    /// wind point source reads it. All three components are <c>-1e7</c> before the first latch.</summary>
    public static readonly int GobPrevPosition = Field<System.Numerics.Vector3>("Gob", "prevPosition");
    /// <summary>Gob -&gt; the PREVIOUS frame's world rotation, latched beside <see cref="GobPrevPosition"/>.</summary>
    public static readonly int GobPrevRot = Field<CX.Engine.Quat>("Gob", "prevRot");
    /// <summary>Gob -&gt; "I am a wind point source", with <see cref="GobWindStrength"/> its falloff. Only
    /// <c>Gob::SetWind</c> @0x436f40 writes them (a bool store and a float), reached through the Gob vtable
    /// (+0x1c0) from <c>CNWCObject::SetWind</c> @0x7e8df0 -- so moving creatures DO raise gusts. The Gob ctor
    /// zeroes the byte alone: its two neighbours are padding.</summary>
    public static readonly int GobWindSource = Field<byte>("Gob", "windSource", Expect.Byte01);
    /// <inheritdoc cref="GobWindSource"/>
    public static readonly int GobWindStrength = Field<float>("Gob", "windStrength");
    /// <summary>Gob: non-zero exempts the gob from <c>Gob::Animate</c>'s view-volume gate, so it keeps
    /// animating off screen. Nothing in 8193.37-17 ever writes it, so the live escape is
    /// <c>Model+0x8e &amp; 1</c> (<see cref="NodeOwnerFlags"/>) alone.</summary>
    public static readonly int GobAnimOffScreen = Field<int>("Gob", "animOffScreen");
    /// <summary>Gob: a <c>short</c> that counts 0..4 once per animate; the bounding sphere is refreshed on
    /// the frame it comes back to 0.</summary>
    public static readonly int GobSphereTick = Field<short>("Gob", "sphereTick");
    /// <summary>Gob -&gt; its <c>CExoArrayList&lt;SafePointer&lt;CAurObject&gt;*&gt;</c> (data +0x8, count
    /// +0x10, capacity +0x14). <c>Gob::~Gob</c> zeroes every registered <c>SafePointer</c>, which is how
    /// <c>Gob::Animate</c> and <c>Gob::DoEvent</c> notice that an event they fired destroyed the gob.</summary>
    public static readonly int GobSafePointers = Field<IntPtr>("Gob", "safePointers");
    /// <inheritdoc cref="GobSafePointers"/>
    public static readonly int GobSafePointerCount = Field<int>("Gob", "safePointerCount");
    /// <summary>A <c>SafePointer&lt;CAurObject&gt;</c> is 12 bytes: the object at +0, its index in the owner's
    /// list at +8. Not a field row -- a record size, like <see cref="NewControllerStride"/>.</summary>
    public const int SafePointerStride = 0xc, SafePointerIndex = 0x8;
    /// <summary>Gob -&gt; its <c>CallBack*</c> array and the number of entries in it. The registry is PER
    /// GOB -- there is no global one -- grown by <c>Gob::RegisterCallBack</c> @0x497db0 and walked in reverse
    /// by <c>Gob::DoEvent</c> (@0x49d760 / rva 0x50d50), which fires an entry whose name matches the event
    /// case-insensitively, or is empty, or begins with <c>*</c> (a wildcard that hears every event).</summary>
    public static readonly int GobCallbacks = Field<IntPtr>("Gob", "callbacks");
    /// <inheritdoc cref="GobCallbacks"/>
    public static readonly int GobCallbackCount = Field<int>("Gob", "callbackCount");
    /// <summary>A <c>CallBack</c> record's handler. The record opens with its inline name buffer -- which is
    /// why <c>Gob::DoEvent</c> tests <c>*name</c> for NUL and <c>'*'</c> straight off the record pointer, and
    /// why reading the name needs no offset of its own -- and the handler sits at +0x20. A constant rather
    /// than a field row, read as the same instruction in both disassemblies.</summary>
    public const int CallBackFn = 0x20;

    /// <summary>AnimRun -&gt; last frame's <see cref="AnimRunTime"/>. The animation-event window is
    /// <c>(prevTime, time]</c> forward, <c>[time, prevTime)</c> in reverse.</summary>
    public static readonly int AnimRunPrevTime = Field<float>("AnimRun", "prevTime");
    /// <summary>AnimRun -&gt; the value copied into the <c>position_scale_factor</c> global before the pose,
    /// which every position track is multiplied by. The AnimRun constructor sets it to 1.</summary>
    public static readonly int AnimRunPosScale = Field<float>("AnimRun", "posScale");
    /// <summary>AnimRun: set when a fade-out reaches weight 0; the NEXT visit fires <c>done&lt;anim&gt;</c>
    /// and destroys the run.</summary>
    public static readonly int AnimRunDone = Field<int>("AnimRun", "done", Expect.Byte01);
    /// <summary>Animation -&gt; its keyed event array (stride <see cref="AnimEventStride"/>: a float time at
    /// +0, an inline name at +4) and the count beside it.</summary>
    public static readonly int AnimationEvents = Field<IntPtr>("Animation", "events");
    /// <inheritdoc cref="AnimationEvents"/>
    public static readonly int AnimationEventCount = Field<int>("Animation", "eventCount");
    /// <inheritdoc cref="AnimationEvents"/>
    public const int AnimEventStride = 0x24, AnimEventName = 4;

    /// <summary>MdlNode -&gt; a RAW function pointer (not a vtable slot): one of the seven
    /// <c>MdlNode*Control</c> thunks its constructor installs, which is what
    /// <see cref="CX.Alloy.AnimHierarchy"/> identifies the node class by.
    /// <c>void (*)(MdlNode*, Part*, float tPrev, float tCur, float weight)</c>.</summary>
    public static readonly int MdlNodeControl = Field<IntPtr>("MdlNode", "control");
    /// <summary>PartLight: zero here makes <c>AnimateHierarchy</c> fire
    /// <c>DoEvent("AnimateStaticLight", light)</c> on the owning gob, once per controlled light part.
    /// One of the two rows in this block that MOVES on Windows.</summary>
    public static readonly int LightStaticLit = Field<int>("PartLight", "staticLit");
    /// <summary>LookAtAnimation -&gt; the synthetic <c>Animation</c> it built, and the <c>AnimRun</c> playing
    /// it. <c>Gob::Animate</c> drops every queue entry whose animation is that one, frees the run, then
    /// deletes the LookAtAnimation itself.</summary>
    public static readonly int LookAtAnimation_ = Field<CX.Engine.Animation>("LookAtAnimation", "animation");
    /// <inheritdoc cref="LookAtAnimation_"/>
    public static readonly int LookAtRun = Field<CX.Engine.AnimRun>("LookAtAnimation", "run");

    /// <summary>MdlNodeAnimMesh: the morph target count per vertex stream, the seconds per morph frame, and
    /// the two packed VERTEX-MAJOR pools -- element <c>(v, f)</c> of the position pool is at
    /// <c>morphVerts[(vertFrames*v + f)*3]</c>, of the UV pool at <c>morphUvs[(uvFrames*v + f)*2]</c>.</summary>
    public static readonly int MorphVertexCount = Field<ushort>("MdlNodeAnimMesh", "morphVertexCount");
    /// <inheritdoc cref="MorphVertexCount"/>
    public static readonly int MorphSamplePeriod = Field<float>("MdlNodeAnimMesh", "samplePeriod");
    /// <inheritdoc cref="MorphVertexCount"/>
    public static readonly int MorphVerts = Field<IntPtr>("MdlNodeAnimMesh", "morphVerts");
    /// <inheritdoc cref="MorphVertexCount"/>
    public static readonly int MorphUvs = Field<IntPtr>("MdlNodeAnimMesh", "morphUvs");
    /// <inheritdoc cref="MorphVertexCount"/>
    public static readonly int MorphVertFrames = Field<int>("MdlNodeAnimMesh", "vertFrames");
    /// <inheritdoc cref="MorphVertexCount"/>
    public static readonly int MorphUvFrames = Field<int>("MdlNodeAnimMesh", "uvFrames");
    /// <summary>The dynamic item a morph writes is interleaved <c>[x, y, z, u, v]</c> -- five floats a
    /// vertex, which is what <c>MdlNodeAnimMesh::InternalControl</c>'s own pointer arithmetic walks.</summary>
    public const int MorphStrideFloats = 5;

    /// <summary>Virtual slot of <c>Gob::Animate(float)</c> -- the slot <c>Scene::Animate</c> dispatches over
    /// its child list, and the test that says a child is a plain Gob rather than a Camera.</summary>
    public static readonly int GobAnimateSlot = Field("Gob", "vtbl.Animate", Expect.VtblSlot, sig: "void(float dt)");
    /// <summary>Virtual slot of <c>Gob::DoEvent(char const*, void*)</c> -- THE seam into game code.</summary>
    public static readonly int GobDoEventSlot =
        Field("Gob", "vtbl.DoEvent", Expect.VtblSlot, sig: "void(byte* name, IntPtr data)");
    /// <summary>Virtual slot of <c>Gob::IsAnimating()</c>.</summary>
    public static readonly int GobIsAnimatingSlot = Field("Gob", "vtbl.IsAnimating", Expect.VtblSlot, sig: "byte()");
    /// <summary>Virtual slot of <c>Gob::IsInViewVolume()</c>.</summary>
    public static readonly int GobInViewSlot = Field("Gob", "vtbl.IsInViewVolume", Expect.VtblSlot, sig: "byte()");
    /// <summary>Virtual slot of <c>Gob::GetMinimumSphere()</c> -- void on both builds (the MSVC name ends
    /// <c>XXZ</c>), called purely to refresh the cached sphere.</summary>
    public static readonly int GobMinSphereSlot = Field("Gob", "vtbl.GetMinimumSphere", Expect.VtblSlot, sig: "void()");
    /// <summary>Virtual slot of <c>Part::Animate(float)</c> -- the per-part tick, virtual so that
    /// <c>PartDanglyMesh::Animate</c> (and through it <see cref="CX.Alloy.DanglyPhysics"/>) is reached.</summary>
    public static readonly int PartAnimateSlot = Field("Part", "vtbl.Animate", Expect.VtblSlot, sig: "void(float dt)");
    /// <summary>Virtual slot of <c>Part::AsPartAnimMesh()</c>.</summary>
    public static readonly int PartAsAnimMeshSlot = Field("Part", "vtbl.AsPartAnimMesh", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary>Virtual slot of <c>Part::GetWorld(Vector&amp;, Quaternion&amp;)</c> -- the ONE <c>Part</c>
    /// slot the game layer calls through, and so the one slot <c>PartShell</c> has to implement. Measured with
    /// <c>tools/abi/vtable-slots.py Part</c>: slot 4 on Linux, slot 3 on Windows.</summary>
    public static readonly int PartGetWorldSlot = Field("Part", "vtbl.GetWorld", Expect.VtblSlot);
    /// <summary>Virtual slot of a <c>CAurBehavior</c>'s <c>Control(float dt)</c> -- dispatched once per gob
    /// from <c>Gob::Animate</c>, BEFORE the pose and before any event. The base implementation recurses into
    /// each dependency gob's <c>Gob::Animate</c>, so an attach parent is animated before its child.</summary>
    public static readonly int BehaviorControlSlot = Field("CAurBehavior", "vtbl.Control", Expect.VtblSlot, sig: "void(float dt)");
    /// <summary>Virtual slot of <c>LookAtAnimation::IsActive()</c>.</summary>
    public static readonly int LookAtIsActiveSlot = Field("LookAtAnimation", "vtbl.IsActive", Expect.VtblSlot, sig: "byte()");
    /// <summary>Virtual slot of <c>LookAtAnimation::Control()</c>.</summary>
    public static readonly int LookAtControlSlot = Field("LookAtAnimation", "vtbl.Control", Expect.VtblSlot, sig: "void()");
    /// <summary>Virtual slot of the LookAtAnimation DELETING destructor. Itanium gives it a slot of its own
    /// and one argument; MSVC merges it into slot 0 and passes <c>1</c> as a second argument, so this call
    /// has two shapes and no <c>sig:</c>.</summary>
    public static readonly int LookAtDeleteSlot = Field("LookAtAnimation", "vtbl.Delete", Expect.VtblSlot);
    /// <summary>LookAtAnimation -&gt; the looker's <c>Gob*</c>: the distance check reads its position at +0xa8 on
    /// both builds, and the ctor stores its third argument here.</summary>
    public static readonly int LookAtGob = Field<CX.Engine.Gob>("LookAtAnimation", "gob");
    /// <summary>LookAtAnimation -&gt; the looker's head <c>Part*</c> (HEAD_NAME's node). Control calls
    /// <c>Part::GetWorld</c> through it and copies its local rotation (<see cref="PartRot"/>) into the first
    /// keyframe.</summary>
    public static readonly int LookAtHeadPart = Field<CX.Engine.Part>("LookAtAnimation", "headPart");
    /// <summary>LookAtAnimation -&gt; <c>float*</c> into the synthetic animation's second keyframe: the four
    /// floats Control writes as <c>(x, y, z, w)</c>, which <c>AnimateHierarchy</c> then blends onto the head
    /// part. This is the seam the head aim is overwritten at.</summary>
    public static readonly int LookAtTargetKey = Field<IntPtr>("LookAtAnimation", "targetKey");

    /// <summary>Gob -&gt; the kind of game object behind <c>SetExternalData</c>: 5 creature
    /// (<c>CNWCCreature::LoadModel</c> @0x72a4b0), 9 placeable (@0x7ec500), 10 door (@0x755ec0), through
    /// vtable <c>SetExternalDataType</c>. The accessor pair is one line on both builds: Linux @0x444720,
    /// Windows rva 0x48450 read <c>+0x498</c>.</summary>
    public static readonly int GobExternalType = Field<int>("Gob", "externalType");
    /// <summary>The <see cref="GobExternalType"/> a creature's gob carries.</summary>
    public const int GobKindCreature = 5;
    /// <summary>The <see cref="GobExternalType"/> a placeable's gob carries.</summary>
    public const int GobKindPlaceable = 9;

    /// <summary>Gob -&gt; the Scene it animates in. <c>Gob::Animate</c> reads it for the wind manager at
    /// <c>scene+0x160</c> (Linux @0x49d8c0, Windows @0x14004dbd0, both <c>this+0x88</c>).</summary>
    public static readonly int GobScene = Field<IntPtr>("Gob", "scene");
    /// <summary>Gob -&gt; its <c>CExoArrayList&lt;AnimRun*&gt;</c> (items +0xe0, count +0xe8), newest last:
    /// <c>Gob::Animate</c> fires <c>"start%s"</c> on the LAST entry's animation name on both builds (Linux
    /// @0x49d8c0, Windows @0x14004dbd0), so the last run is the animation being blended TO.</summary>
    public static readonly int GobAnimRuns = Field<IntPtr>("Gob", "animRuns");
    /// <inheritdoc cref="GobAnimRuns"/>
    public static readonly int GobAnimRunCount = Field<int>("Gob", "animRunCount");
    /// <summary>AnimRun -&gt; its <c>Animation*</c> (first word), and Animation -&gt; the name
    /// (<c>char[]</c>, inline): <c>sprintf("start%s", *run + 0x10)</c> and the <c>"loop%s"</c>/<c>"done%s"</c>
    /// events read it there in <c>Gob::Animate</c> on both builds.</summary>
    public static readonly int AnimRunAnimation = Field<CX.Engine.Animation>("AnimRun", "animation");
    /// <inheritdoc cref="AnimRunAnimation"/>
    public static readonly int AnimationName = Field<byte>("Animation", "name");
    /// <summary>Animation -&gt; the root of ITS OWN <c>MdlNode</c> tree, the one carrying the animated
    /// tracks. <c>Gob::Animate</c> calls <c>AnimateHierarchy(gob+0x90, anim+0x50, prev, time, blend)</c> on
    /// both builds (Linux @0x49d8c0, Windows @0x14004dbd0), so this tree -- not the model's -- holds the
    /// controllers a running animation writes through.</summary>
    public static readonly int AnimationNode = Field<CX.Engine.MdlNode>("Animation", "node");
    /// <summary>Animation -&gt; its length in seconds. <c>Gob::Animate</c> wraps and clamps
    /// <see cref="AnimRunTime"/> against it and publishes it as <c>AnimRun::m_AnimLength</c> on both builds
    /// (Linux @0x49d8c0, Windows rva 0x4dbd0). The Windows <c>Animation</c> layout shifts +4 from here on, so
    /// pair any further field of this class on its own.</summary>
    public static readonly int AnimationLength = Field<float>("Animation", "length");
    /// <summary>AnimRun -&gt; its playback rate (float, signed): <c>Gob::Animate</c> advances <c>+0x14</c> by
    /// <c>dt * rate</c>; <c>Gob::PlayAnimation</c> writes it, and <c>CNWCCreature::WalkUpdateLocation</c> re-paces
    /// walk/run through it every AI update. Same offset on both builds.</summary>
    public static readonly int AnimRunRate = Field<float>("AnimRun", "rate");
    /// <summary>AnimRun -&gt; its blend weight, 0..1 while fading in, counting down while fading out
    /// (<c>Gob::Animate</c>, both builds; same research, Q1).</summary>
    public static readonly int AnimRunBlend = Field<float>("AnimRun", "blend");
    /// <summary>AnimRun -&gt; flags (bit 0 = fading out), the current time in seconds, and the blend
    /// duration in seconds. <c>Gob::Animate</c> and <c>Gob::PlayAnimation</c> read the same offsets on both
    /// builds.</summary>
    public static readonly int AnimRunFlags = Field<uint>("AnimRun", "flags");
    /// <inheritdoc cref="AnimRunFlags"/>
    public static readonly int AnimRunTime = Field<float>("AnimRun", "time");
    /// <inheritdoc cref="AnimRunFlags"/>
    public static readonly int AnimRunBlendTime = Field<float>("AnimRun", "blendTime");
    /// <summary>The two <see cref="AnimRunFlags"/> bits that say a run's <see cref="AnimRunBlend"/> is not
    /// the weight the parts carry: <c>Queued</c> holds the whole per-run body of <c>Gob::Animate</c> behind a
    /// mask test, and <c>FadingOut</c> counts the weight down by <c>2*dt/blendTime</c> until the run is
    /// dropped (a zero blend time puts it at 1.0 for the one frame before that).</summary>
    public const uint AnimRunQueued = 0x20, AnimRunFadingOut = 0x1;
    /// <summary>Gob -&gt; the <c>CNWCObject*</c> behind <c>SetExternalData</c>, the pointer just before
    /// <see cref="GobExternalType"/>: <c>Gob::GetExternalData</c> reads the same offset on both builds (Linux
    /// @0x444700, Windows rva 0x58d00).</summary>
    public static readonly int GobExternalData = Field<IntPtr>("Gob", "externalData");

    /// <summary>CNWCCreature -&gt; the object id it is locked onto in combat (<c>0x7f000000</c> = none), written by
    /// every creature update and read by <c>SetDesiredOrientationForLock</c> to face the victim.</summary>
    public static readonly int CreatureLockTarget = Field<uint>("CNWCCreature", "lockTarget");

    /// <summary>CNWCCombatAttackData -&gt; the attacker's object id (<c>0x7f000000</c> on a projectile's synthetic
    /// record), the damage (i16), and the result byte (1 hit, 3 critical on the projectile path). The class
    /// is identical on both builds; the rows rest on the wire order the creature update handler reads it
    /// in.</summary>
    public static readonly int AtkAttacker = Field<uint>("CNWCCombatAttackData", "attacker");
    /// <inheritdoc cref="AtkAttacker"/>
    public static readonly int AtkDamage = Field<short>("CNWCCombatAttackData", "damage");
    /// <inheritdoc cref="AtkAttacker"/>
    public static readonly int AtkResult = Field<byte>("CNWCCombatAttackData", "result");

    /// <summary>Non-zero stops <c>Scene::HitCheck</c> after the static pass. A DWORD, written by
    /// <c>CHitInfo::Reset</c> (@0x45fa90 / rva 0xd3090); the whole record is 0x80 bytes and identical on both
    /// builds, as <c>Reset</c>, <c>ClearOutput</c> (@0x45fa20 / rva 0xc5a90) and <c>Scene::HitCheckGob</c>
    /// (@0x45f860 / rva 0xc9f50) agree store for store.</summary>
    public static readonly int HitInfoMode = Field<int>("CHitInfo", "mode");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoIgnoreList = Field<IntPtr>("CHitInfo", "ignoreList");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoIgnoreCount = Field<int>("CHitInfo", "ignoreCount");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoKind = Field<int>("CHitInfo", "kind");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoPoint = Field<System.Numerics.Vector3>("CHitInfo", "point");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoGob = Field<IntPtr>("CHitInfo", "gob");
    /// <inheritdoc cref="HitInfoMode"/>
    public const int SizeOfHitInfo = 0x80;

    // The rest of CHitInfo. A cast must fill it byte-identically, because the game layer reads the record
    // the engine filled; the INPUT fields are the caller's and a cast leaves them untouched. Verified on both
    // builds by disassembling CHitInfo::ClearOutput (rva 0xc5a90) and Reset (rva 0xd3090): the same set of
    // stores at the same offsets as Linux.

    /// <summary>INPUT: a 64-bit surface mask, tested as <c>mask &gt;&gt; face.surface &amp; 1</c> by
    /// <c>HitCheckAABBnode</c> @0x43da50 and, for bit 30, by <c>PartTrigger::HitCheckGeom</c>.</summary>
    public static readonly int HitInfoSurfaceMask = Field<long>("CHitInfo", "surfaceMask");
    /// <summary>INPUT: non-zero makes <c>PartTriMesh::HitCheckGeom</c> test the expanded box and no
    /// triangles, which is what every mouse pick does (<c>GameCameraHitCheck</c> @0x63a270 sets it).</summary>
    public static readonly int HitInfoBoxOnly = Field<int>("CHitInfo", "boxOnly");
    /// <summary>OUTPUT: the hit point in the hit PART's own space -- written at the leaf and never
    /// transformed back up, unlike <see cref="HitInfoPoint"/>.</summary>
    public static readonly int HitInfoLocalPoint = Field<System.Numerics.Vector3>("CHitInfo", "localPoint");
    /// <summary>OUTPUT: the hit surface as <c>1 &lt;&lt; id</c>.</summary>
    public static readonly int HitInfoSurface = Field<long>("CHitInfo", "surface");
    /// <summary>OUTPUT: the SafePointer's back-index into the hit object, -1 when unlinked.</summary>
    public static readonly int HitInfoSafeIndex = Field<int>("CHitInfo", "safeIndex");
    /// <summary>OUTPUT: the face index the hit landed on.</summary>
    public static readonly int HitInfoFace = Field<int>("CHitInfo", "face");
    /// <summary>OUTPUT: the hit normal, which a <c>PartTriMesh</c> TRIANGLE hit deliberately leaves zero --
    /// the engine's <c>polyhit</c> has no normal out -- while the AABB walk and the box faces write one.</summary>
    public static readonly int HitInfoNormal = Field<System.Numerics.Vector3>("CHitInfo", "normal");
    /// <summary>OUTPUT: the hit part's name pointer.</summary>
    public static readonly int HitInfoPartName = Field<IntPtr>("CHitInfo", "partName");
    /// <summary>OUTPUT: the hit trigger's payload, copied off the <c>PartTrigger</c>.</summary>
    public static readonly int HitInfoTrigger = Field<long>("CHitInfo", "trigger");
    /// <summary>INPUT: non-zero refuses a segment that starts inside a part's box.</summary>
    public static readonly int HitInfoRejectInside = Field<int>("CHitInfo", "rejectInside");
    /// <summary>INPUT: adds classification 4 to the skip set.</summary>
    public static readonly int HitInfoSkipCharacters = Field<byte>("CHitInfo", "skipCharacters");
    /// <summary>INPUT: 1 after <c>Reset</c>, which turns on the door gate <c>mindooralpha &lt;
    /// Gob+0x10c</c>.</summary>
    public static readonly int HitInfoDoorAlpha = Field<byte>("CHitInfo", "doorAlpha");

    // Section 2 is closed: batch 1 took the Scene walk (#436), batch 2 the passes (#441), batch 3 the
    // renderer, textures, buffers and shim (#443), and build.sh now refuses a bare hex offset under
    // managed/Alloy/. A second tested client build gets its own pinned key plus its own values below;
    // today there is exactly one per OS.

    // ================= CX-689: CRadialMenu (CX-162 §1) =================
    // Linux from the ctor @0x583460 / SetEntryx @0x5837f0 / SetSubMenuCreatorx @0x583f50 / CreateSubMenu @0x584790 /
    // HandleMenuAction @0x585530; Windows from the same exported CRadialMenu bodies in nwmain.exe (ctor rva 0x1f7950,
    // SetEntryx 0x1fa250, SetSubMenuCreatorx 0x1fa9e0, SetSubMenu 0x1fa490, CreateSubMenu 0x1f8960, DisableEntry
    // 0x1f8b30, HandleMenuAction 0x1f9350, RecursiveAutoDelete 0x1f9da0, Activate 0x1f7e80, BuildIcon 0x1f8400),
    // read side by side -- docs/engine-layout.md "The radial rows". The per-slot entry and creator callbacks are
    // CGuiCallback member-fn pairs: {obj, fn, adj} = 0x18 on Itanium, {obj, fn} = 0x10 on MSVC, so every array
    // after them shifts. sizeof(CRadialMenu) is 0x878 / 0x7f8 (each build's deleting dtor), and every subclass
    // starts its own fields right there, so the subclass rows carry a uniform -0x80. CANDIDATES until a live
    // probe (logs=1) reads a root on each platform.

    /// <summary>CGuiCallback: the object word of one member-fn callback record. Unset = the record's own address
    /// (the self-pointer sentinel <c>SetEntryx</c> leaves and <c>HandleMenuAction</c> tests).</summary>
    public static readonly int CallbackObj = Field<IntPtr>("CGuiCallback", "obj");
    /// <summary>CGuiCallback: the member-fn word. Itanium: odd = a vtable byte offset + 1, and the this-adjust is
    /// the word right after it; MSVC: the code address, with no adjust word.</summary>
    public static readonly int CallbackFn = Field<IntPtr>("CGuiCallback", "fn");
    /// <summary>Bytes per callback record: Itanium <c>{obj, fn, adj}</c> vs MSVC <c>{obj, fn}</c>. The records of
    /// one menu lie end to end, so this is the view's indexer step.</summary>
    public static readonly int CallbackStride = Field("CGuiCallback", "stride");

    /// <summary>CRadialMenu: <c>CGuiTextBubble* m_pText[8]</c> -- the slot labels.</summary>
    public static readonly int RadialText = Field<CX.Engine.CGuiTextBubble>("CRadialMenu", "text", count: 8);
    /// <summary>CRadialMenu: entry callback[8] -- the address of the first record, indexed by
    /// <see cref="CallbackStride"/>.</summary>
    public static readonly int RadialEntry = Field<CX.Engine.CGuiCallback>("CRadialMenu", "entry", embedded: true);
    /// <summary>CRadialMenu: <c>long m_nParam[8]</c> -- shared by the entry and the creator of a slot.</summary>
    public static readonly int RadialParam = Field<long>("CRadialMenu", "param", count: 8);
    /// <summary>CRadialMenu: <c>uint flags[8]</c>; bit 0 = disabled (<c>DisableEntry</c>).</summary>
    public static readonly int RadialFlags = Field<uint>("CRadialMenu", "flags", count: 8);
    /// <summary>CRadialMenu: creator callback[8], same stride and sentinel as <see cref="RadialEntry"/>.</summary>
    public static readonly int RadialCreator = Field<CX.Engine.CGuiCallback>("CRadialMenu", "creator", embedded: true);
    /// <summary>CRadialMenu: <c>CRadialMenu* m_pSubMenu[8]</c> -- the materialized child, null until CreateSubMenu.</summary>
    public static readonly int RadialSubMenu = Field<CX.Engine.CRadialMenu>("CRadialMenu", "subMenu", count: 8);
    /// <summary>CRadialMenu: <c>CRadialMenu* m_pParent</c>, set by HandleMenuAction on descent.</summary>
    public static readonly int RadialParent = Field<IntPtr>("CRadialMenu", "parent");
    /// <summary>CRadialMenu: <c>CGuiIcon* m_pIcon[8]</c>; the resref is the CResRef at CGuiIcon+0x8.</summary>
    public static readonly int RadialIcon = Field<CX.Engine.CGuiIcon>("CRadialMenu", "icon", count: 8);
    /// <summary>CRadialMenu: menu X / Y (int), filled by <c>Activate</c> from <c>CGuiMan::GetMousePos</c>.</summary>
    public static readonly int RadialMenuX = Field<int>("CRadialMenu", "menuX");
    /// <inheritdoc cref="RadialMenuX"/>
    public static readonly int RadialMenuY = Field<int>("CRadialMenu", "menuY");
    /// <summary>CRadialMenu: <c>m_bAutoDelete</c> (byte); every root and creator sets it 1. No <c>Byte01</c> probe:
    /// that reads a dword, and the ctor @0x583460 never writes +0x872/+0x873 (heap garbage) -- RadialService
    /// probes the byte itself.</summary>
    public static readonly int RadialAutoDelete = Field<byte>("CRadialMenu", "autoDelete");
    /// <summary>CRadialMenu: refcount (int); CreateSubMenu ++ on the child, RecursiveAutoDelete --.</summary>
    public static readonly int RadialRefCount = Field<int>("CRadialMenu", "refCount");
    /// <summary>CGuiIcon: the icon's <c>CResRef</c> (16 bytes) that <c>Initialize</c> (Linux @0x58cc20, Windows rva
    /// 0x1e4430: the first store on both, then the three size ints at +0x1c/+0x20/+0x24) and
    /// <c>ReplacePrimaryIconTexture</c> (@0x58cd30 / rva 0x1e4690) read.</summary>
    public static readonly int GuiIconResRef = Field<byte>("CGuiIcon", "resref", count: 16);
    /// <summary>CGuiIcon: the uses-left count label, a <c>CAurString*</c> (null when none). <c>SetCountText</c> (Linux
    /// @0x58d130, Windows rva 0x1e4720) tests, feeds and stores it at +0x40 on both; <c>CreateCountLabel</c> @0x58ce80
    /// makes it with <c>NewCAurString(1)</c>, so it is the same StringGob sub-object the bubble label is.</summary>
    public static readonly int GuiIconCountLabel = Field<CX.Engine.CAurString>("CGuiIcon", "countLabel");
    /// <summary>CGuiIcon: its size in 32 px inventory cells, <c>Initialize</c>'s +0x20/+0x24 stores on both.</summary>
    public static readonly int GuiIconCols = Field<int>("CGuiIcon", "cols");
    public static readonly int GuiIconRows = Field<int>("CGuiIcon", "rows");
    /// <summary>CGuiCreatureMenu / CGuiPlaceableMenu / CGuiDoorMenu / CGuiItemMenu / CClientTrapMenu: the target
    /// object id (uint), the first field after the CRadialMenu base on both builds. Read off each subclass
    /// constructor pair as the id argument's store, or the INVALID sentinel where the ctor takes none (item,
    /// whose <c>ActivateRadialMenu</c> stores the id later). Same offset in every subclass, so one row.</summary>
    public static readonly int RadialTargetId = Field<uint>("CRadialMenu", "targetId");
    /// <summary>CClientSpellMenu: the target object id (uint) the cast leaf reads. The ctor pair (Linux @0x6f1650,
    /// Windows rva 0x9a10e0) stores its <c>uint</c> argument there, right after the target <c>Vector</c>
    /// (<c>+0x900</c> / <c>+0x880</c>); <c>RadialMenuCast</c> (Linux @0x6efb10, Windows rva 0x9a4540) passes it as
    /// <c>PerformCastSpell</c>'s target on both.</summary>
    public static readonly int SpellMenuTargetId = Field<uint>("CClientSpellMenu", "targetId");
    /// <summary><c>sizeof(CGuiCreatureMenu)</c>: the <c>operator new</c> before its ctor (Linux
    /// <c>ActivatePlayerCharacterRadialMenu</c> @0x73f3a0, Windows the twin of the associate root, rva 0x98e140's caller).</summary>
    public static readonly int CreatureMenuSize = Field("CGuiCreatureMenu", "sizeof");
    /// <summary><c>sizeof(CClientSpellMenu)</c>: the <c>operator new</c> in every slot-1 cast creator (Linux
    /// <c>CreateClientSpellMenu</c> @0x73b360), the deleting dtor's size on Windows (rva 0x9a19c0).</summary>
    public static readonly int SpellMenuSize = Field("CClientSpellMenu", "sizeof");
    /// <summary>CGuiTextBubble: the <c>CAurString*</c> the label lives in -- the ctor (@0x588690 Linux, @rva 0x1fd310
    /// Windows) stores <c>NewCAurString(-1)</c> there and <c>SetText</c> @0x588790 feeds it through the CAurString
    /// vtable.</summary>
    public static readonly int TextBubbleString = Field<CX.Engine.CAurString>("CGuiTextBubble", "aurString");

    // ================= CX-693: item icons (CX-162 §5) =================
    // CGuiIcon's three subclasses, each read off its ctor on BOTH builds (Linux @0x58dcb0 / @0x58d9d0 / @0x58d5c0,
    // Windows rva 0x1e3000 / 0x1e2e40 / 0x1e2ca0): every subclass field lands at the same offset on each, because
    // the base is 0x48 on both (a single vptr, no Itanium-only padding). The model builders confirm the reads:
    // CLayeredIcon::CreateCopyIconModel @0x58c8b0 hands +0x48/+0x50 to the PLT ReplaceTexture, CCompositeIcon's
    // @0x58d320 replaces gui_replace01/02/03 with +0x8/+0x48/+0x59, CArmorIcon::LoadArmorLayeredIconModel
    // @0x58d7c0 (rva 0x1e4470) walks +0x48..+0xae by 0x11 and indexes +0xc0 by the compacted layer.

    /// <summary>CLayeredIcon: <c>int</c> palette-scheme length (10 for an item: <c>CNWCItem::UpdateIcons</c> @0x7cbc50
    /// passes 10) and the <c>unsigned short*</c> scheme the PLT is tinted with.</summary>
    public static readonly int LayeredIconSchemeCount = Field<int>("CLayeredIcon", "schemeCount");
    /// <inheritdoc cref="LayeredIconSchemeCount"/>
    public static readonly int LayeredIconScheme = Field<IntPtr>("CLayeredIcon", "scheme");
    /// <summary>CCompositeIcon: the second and third <c>CResRef</c> (17 bytes each, the first is the base's +0x8),
    /// bound to <c>gui_replace02</c> / <c>gui_replace03</c> of the <c>GUI_ICON_%dX%dLC</c> model -- bottom to top.</summary>
    public static readonly int CompositeIconResRef2 = Field<byte>("CCompositeIcon", "resref2", count: 17);
    /// <inheritdoc cref="CompositeIconResRef2"/>
    public static readonly int CompositeIconResRef3 = Field<byte>("CCompositeIcon", "resref3", count: 17);
    /// <summary>CArmorIcon: six <c>CResRef</c> layers at stride <see cref="ArmorIconResRefStride"/> (+0x48..+0xae),
    /// an empty one skipped; the shared scheme (<c>int</c> count, <c>unsigned short*</c>) and the optional per-layer
    /// scheme table (<c>count</c> shorts per EXISTING layer, null = every layer shares the one scheme).</summary>
    public static readonly int ArmorIconResRefs = Field<byte>("CArmorIcon", "resrefs", count: 102);   // ArmorIconLayers * ArmorIconResRefStride
    /// <summary><c>sizeof(CResRef)</c> as the armour layer array packs it (17 bytes, unaligned) and the layer count
    /// (the ctor's zero loop stops at +0xae = 0x48 + 6 * 0x11 on both builds). Values, not places -- no row.</summary>
    public const int ArmorIconResRefStride = 0x11;
    /// <inheritdoc cref="ArmorIconResRefStride"/>
    public const int ArmorIconLayers = 6;
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconSchemeCount = Field<int>("CArmorIcon", "schemeCount");
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconScheme = Field<IntPtr>("CArmorIcon", "scheme");
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconLayerSchemes = Field<IntPtr>("CArmorIcon", "layerSchemes");
    /// <summary>CNWCItem -&gt; the item's own <c>CGuiIcon*</c> (plain, layered, composite or armour by vptr): built and
    /// REPLACED (old one deleted) by <c>CNWCItem::UpdateIcons</c> @0x7cbc50 on every item update, so it is re-read
    /// from the item every frame, never held. <c>CGuiItemMenu::ActivateRadialMenu</c> hands it to <c>SetCenter</c>:
    /// Linux @0x7d3720 reads <c>item+0x258</c>, Windows rva 0x9a59d0 <c>item+0x238</c>.</summary>
    public static readonly int ItemGuiIcon = Field<IntPtr>("CNWCItem", "guiIcon");
    /// <summary>CNWCItem's deleting destructor in its primary vtable: Itanium's D0 at +0x08 (<c>_ZN8CNWCItemD0Ev</c>, after
    /// the complete D1), MSVC's scalar deleting destructor, the table's only slot (vftable rva 0xe4ceb0 -&gt; 0x83c8a0:
    /// the dtor, then <c>operator delete(this, 0x2b8)</c> when <c>flags &amp; 1</c>). The two take different arguments,
    /// so NuiItemIcons calls it per OS.</summary>
    public static readonly int ItemDeleteSlot = Field("CNWCItem", "vtbl.Delete", Expect.VtblSlot);
    /// <summary>CGameObject: the object type byte <c>CClientExoAppInternal::GetItemByGameObjectID</c> tests for 6
    /// (item) before casting -- Linux @0x63f3a0, Windows rva 0x6d82a0, both <c>go+0xc</c>.</summary>
    public static readonly int GameObjType = Field<byte>("CGameObject", "type");
    /// <summary>Virtual slot of <c>CGameObject::AsNWCItem()</c>, the cast the same two bodies make after the type
    /// test: Linux <c>vtbl+0x70</c>, Windows <c>vtbl+0x68</c> (the usual one-destructor-slot shift).</summary>
    public static readonly int GameObjAsNWCItemSlot = Field("CGameObject", "vtbl.AsNWCItem", Expect.VtblSlot, sig: "IntPtr()");

    // ================= CX-40: the last private offset constants (nameplate, consent, pose) =================
    // Three files kept their own `private const int` / `Win ? a : b` offsets. Every one is a row here now,
    // re-measured 2026-09-24 by decompiling the SAME function on both programs of $GHIDRA_PROJECT/nwn-linux.rep
    // (tools/ghidra/DecompOne.java); the anchor and its address per build is on each row and in
    // docs/engine-layout.md. Linux addresses are nm (Ghidra shows nm + 0x100000), Windows are RVAs.

    /// <summary>CNWCObject -&gt; its <c>CGuiInGamePopUpBubble*</c> (nullable, one per object).
    /// <c>CNWCObject::SetPopUpBubbleText</c> (Linux nm @0x7e8050, Windows rva 0x766450) reads
    /// <c>this[0x17]</c> and <c>new</c>s the bubble into it on both builds.</summary>
    public static readonly int ObjBubble = Field<CX.Engine.CGuiInGamePopUpBubble>("CNWCObject", "bubble");

    /// <summary>CNWCObject: 1 =&gt; the server has retired this object -- no plate and no bubble. The first test
    /// in <c>SetPopUpBubbleText</c> on both builds. No probe: its writer set is known but its semantics are
    /// not, so 0/1 is not something to assert.</summary>
    public static readonly int ObjRetired = Field<int>("CNWCObject", "retired");

    // CNWCObject's fade machine. Read off CNWCObject::FadeObject (Linux nm @0x7e7720, Windows rva 0x762e40) and
    // SetDesiredFadeState (Linux nm @0x7e79e0, Windows rva 0x765920): identical offsets on both builds.
    /// <summary>CNWCObject -&gt; the fade state <see cref="ObjVisible"/> is walking toward (byte: 0 out, 1 in).</summary>
    public static readonly int ObjFadeTarget = Field<byte>("CNWCObject", "fadeTarget");
    /// <summary>CNWCObject -&gt; nonzero = expire on fade-out (int). When the fade lands at 0, <c>FadeObject</c> stamps the
    /// time and detaches the gob (<c>CNWCAnimBase::AttachToScene(null)</c>); once <see cref="ObjFadeHoldMs"/> has
    /// passed it returns true and <c>AIUpdate</c> deletes the object.</summary>
    public static readonly int ObjFadeExpire = Field<int>("CNWCObject", "fadeExpire");
    /// <summary>CNWCObject -&gt; milliseconds a faded-out object is kept before it expires (uint). Read only while
    /// <see cref="ObjFadeExpire"/> is set; <c>SetDesiredFadeState</c> writes the two together.</summary>
    public static readonly int ObjFadeHoldMs = Field<uint>("CNWCObject", "fadeHoldMs");
    /// <summary>CNWCObject -&gt; the alpha the fade has reached (float, 0..1). <c>FadeObject</c> steps it by
    /// <c>dt_ms / 2000</c> and lands the fade the step it crosses <c>GetFinalFadeAlpha</c>.</summary>
    public static readonly int ObjFadeAlpha = Field<float>("CNWCObject", "fadeAlpha");

    /// <summary><c>ObjectTextBubbleOverride</c> -- the module's own plate text: mode <c>int</c>, then a
    /// <c>CExoString</c> (<c>char*</c> + a dword length). 1 = replace, 2 = prepend, 3 = append, anything
    /// else = leave alone. All three are read at the same offsets by both
    /// <c>ShowFeedbackBubbleText</c> overrides, the item one through the <c>CNWCObject</c> subobject.</summary>
    public static readonly int ObjOverrideMode = Field<int>("CNWCObject", "overrideMode");
    /// <inheritdoc cref="ObjOverrideMode"/>
    public static readonly int ObjOverrideText = Field<IntPtr>("CNWCObject", "overrideText");
    /// <inheritdoc cref="ObjOverrideMode"/>
    public static readonly int ObjOverrideLen = Field<int>("CNWCObject", "overrideLen");

    /// <summary>CNWCItem's "the feedback plate is currently shown for me" flag, measured from the
    /// <b>CNWCObject base subobject</b> -- the only item pointer CX ever holds, which is why the row's class
    /// is CNWCObject and not the declaring CNWCItem. Linux's own offset needs the base adjustment and Windows'
    /// does not, because MSVC hands the override the subobject directly.</summary>
    public static readonly int ObjItemPlated = Field<int>("CNWCObject", "itemPlated");

    /// <summary>CNWCCreature -&gt; the area id the plate is gated on: hidden when it differs from the player's.
    /// <c>CNWCCreature::UpdatePopUpBubble</c> (Linux nm @0x72e600, Windows rva 0x8107a0) is two reads on each
    /// build -- <c>this+area == module[currentArea][id]</c> -- and carries the class's usual Windows -0x20.</summary>
    public static readonly int CreatureArea = Field<uint>("CNWCCreature", "area");

    /// <summary>CNWCArea -&gt; its area id, the right-hand side of that same comparison (<c>area+0x118</c> on
    /// both builds).</summary>
    public static readonly int AreaId = Field<uint>("CNWCArea", "id");

    /// <summary>CNWCCreatureStats: the override name (<c>char*</c> + dword length) a disguise sets, which wins
    /// outright over first/last. <c>CNWCCreatureStats::GetFullName</c> (Linux nm @0x74db80, Windows rva
    /// 0x7f3f00) tests the length first and returns this pointer alone when it is non-zero -- same offsets,
    /// same order, on both builds.</summary>
    public static readonly int StatsOverrideName = Field<IntPtr>("CNWCCreatureStats", "overrideName");
    /// <inheritdoc cref="StatsOverrideName"/>
    public static readonly int StatsOverrideNameLen = Field<uint>("CNWCCreatureStats", "overrideNameLen");

    /// <summary>CNWCCreatureStats: damage-level byte 0..5 (5 = dead) -- a server-sent enum indexing
    /// <c>damagelevels.2da</c>, not a client HP division. <c>ShowFeedbackBubbleText</c> reads
    /// <c>*(byte*)(stats + 0x8e)</c> to pick the injury line's colour on both builds.</summary>
    public static readonly int StatsDamage = Field<byte>("CNWCCreatureStats", "damage");

    /// <summary>CNWCCreatureStats: current HP (temporary excluded), temporary HP and max HP, shorts. One
    /// creature-update flag is their only writer (Linux nm @0x7a1550, Windows rva 0x781e80: four
    /// <c>ReadSHORT</c>s beside <c>CPanelPartyBar::SetMemberHealth</c>), and the server sends it for the
    /// player's own creature and party members only. Same offsets on both builds.</summary>
    public static readonly int StatsCurHp = Field<short>("CNWCCreatureStats", "curHp");
    /// <inheritdoc cref="StatsCurHp"/>
    public static readonly int StatsTempHp = Field<short>("CNWCCreatureStats", "tempHp");
    /// <inheritdoc cref="StatsCurHp"/>
    public static readonly int StatsMaxHp = Field<short>("CNWCCreatureStats", "maxHp");

    /// <summary>CGuiInGamePopUpBubble: the projected screen anchor, whole-pixel snapped.
    /// <c>CGuiInGamePopUpBubble::Show(Vector)</c> (Linux nm @0x6a05a0, Windows rva 0x6e9820) stores the vector
    /// there -- x and y as one 8-byte store, z after. NOT the space a <c>Window</c> draws in: see
    /// <c>NameplateNative.AnchorRaw</c>.</summary>
    public static readonly int BubbleAnchorX = Field<float>("CGuiInGamePopUpBubble", "anchorX");
    /// <inheritdoc cref="BubbleAnchorX"/>
    public static readonly int BubbleAnchorY = Field<float>("CGuiInGamePopUpBubble", "anchorY");

    /// <summary>CGuiInGamePopUpBubble: 1 while live (chat or feedback) text owns the bubble. Every
    /// <c>ShowFeedbackBubbleText</c> clears it through <c>obj-&gt;bubble</c> before building a plate, on both
    /// builds; it is the field just below the mode <c>SetChatBubbleMode</c> writes.</summary>
    public static readonly int BubbleLiveText = Field<int>("CGuiInGamePopUpBubble", "liveText");

    /// <summary>Camera: the origin of the viewport rect <c>{x, y, w, h}</c> the camera projects into --
    /// <see cref="CamViewW"/> / <see cref="CamViewH"/> are its other two floats. All four are the consecutive
    /// stores of <c>Camera::SetViewPort(float,float,float,float)</c> (Linux nm @0x4a38a0, Windows rva
    /// 0x5b810, each clamping w and h up to 1.0), which is Camera's measured <c>+0x4</c> block.</summary>
    public static readonly int CamViewX = Field<float>("Camera", "viewX");
    /// <inheritdoc cref="CamViewX"/>
    public static readonly int CamViewY = Field<float>("Camera", "viewY");
    // The offset is the LIST HEAD, read off Camera::GetViewVolume(int)'s one load on both builds (Linux nm
    // @0x4a3a60, Windows rva 0x59000); the extra +4 over Camera's usual window is the pad the ViewVolumeSet
    // int pushes the pointer past.
    /// <summary>Camera -&gt; the world-space frustum as a <c>List&lt;Plane&gt;</c> head.
    /// <c>Camera::RenderScene</c> builds it every frame, so it is an INPUT to the scene walk and stays current
    /// when the walk is skipped. Untyped on purpose: a list head is not a field, so the accessor is the
    /// <see cref="CX.Engine.Camera.ViewVolume"/> concept.</summary>
    public static readonly int CamViewVolume = Field("Camera", "viewVolume");

    /// <summary>CClientExoAppInternal + 0x180: bit 0 = the "show mouse-over feedback" option, bit 1 =
    /// highlight-all (Tab) currently held. The engine tests the WHOLE word, which is why Tab plates even with
    /// the option off. The option's setter is one store on both builds
    /// (<c>*(uint*)(*(g_pAppManager) + 8) + 0x180) = *arg</c>: Linux nm @0x8ca7f0, Windows rva 0x6bdb20).</summary>
    public static readonly int InternalFeedbackWord = Field<int>("CClientExoAppInternal", "feedbackWord");
    /// <summary>Gob: the engine's in-view-volume flag, refreshed by <c>ManageSceneBSP</c>. The whole body of
    /// <c>Gob::IsInViewVolume</c> is <c>return *(this + 0x1b)</c> on both builds (Linux nm @0x444780, Windows
    /// rva 0x49110, name-exported <c>?IsInViewVolume@Gob@@UEAA_NXZ</c>). No <c>Byte01</c>: the dword at this
    /// offset spans <see cref="GobPropagateHilite"/>, so the predicate would judge two fields.</summary>
    public static readonly int GobInView = Field<byte>("Gob", "inView");
    /// <summary>Gob -&gt; the <c>LookAtAnimation</c> the engine is currently aiming this gob's head with, or null.
    /// <c>Gob::EndLookAtAnimate</c> reads it and calls a virtual on it at the same offset on both builds (Linux
    /// nm @0x4979e0, Windows rva 0x50eb0, name-exported <c>?EndLookAtAnimate@Gob@@UEAAXXZ</c>).</summary>
    public static readonly int GobLookAt = Field<CX.Engine.LookAtAnimation>("Gob", "lookAt");
    /// <summary><c>sizeof(CPanelOkCancel)</c>: the <c>operator new</c> argument in the engine's own spawn path
    /// -- Linux <c>COptionsPanel::HandleQuitClick</c> nm @0x6b9800 allocates 0x3a0, the Windows ExitButton twins
    /// (rva 0x7103a0, 0x84f480) allocate 0x360. The wrong size and the ctor/dtor touch out-of-bounds
    /// bytes.</summary>
    public static readonly int OkCancelSize = Field("CPanelOkCancel", "sizeof");
    /// <summary>CGuiPanel: the embedded <c>CGuiCallbackv1&lt;void,int&gt;</c> the panel invokes on close --
    /// the record CX fills in to be told the player's answer. The constructor (Linux nm @0x586fb0, Windows rva
    /// 0x1fbb80) seeds it with the unset sentinel <see cref="CX.Engine.CGuiCallback.IsSet"/> tests, and
    /// <c>Deactivate(int)</c> invokes it through the adjust word on Itanium and flat under MSVC.</summary>
    public static readonly int GuiPanelCloseCallback = Field<CX.Engine.CGuiCallback>("CGuiPanel", "closeCallback", embedded: true);
    // ================= the CX inventory screen (CX-146 §10) =================
    // Every row below was PAIRED: the same function decompiled on both programs of $GHIDRA_PROJECT/nwn-linux.rep,
    // Linux addresses as nm (Ghidra = nm + 0x100000), Windows as RVAs off base 0x140000000. The uniform
    // CNWCItem / CNWCCreature Windows -0x20 held everywhere it was measured here, but no row rests on it:
    // the pairing function is named on each. The inventory data model is NOT a client-side repository -- it
    // is these per-item fields plus the creature's flat equipped-id array.

    /// <summary>CNWCCreature: the flat <c>uint[18]</c> of equipped item object ids, indexed by
    /// <c>log2(INVENTORY_SLOT_*)</c> -- 0 HEAD to 17 CARMOUR, with an INVALID sentinel for empty. Measured as
    /// the whole switch of <c>GetEquippedItemID</c> (Linux nm @0x7212e0, Windows rva 0x800dd0): 18 consecutive
    /// dwords on both, so the array is the read and no call is needed.</summary>
    public static readonly int CreatureEquippedItems = Field<uint>("CNWCCreature", "equippedItems", count: 18);
    /// <summary>CNWCCreature: gold pieces, what the inventory's <c>GPLabel</c> prints. Read by
    /// <c>CPanelInventory::SetCharacterData</c> (Linux nm @0x68d810 <c>creature+0x1d8</c>, Windows rva 0x856fa0
    /// <c>creature+0x1b8</c>) as the <c>%d</c> of TLK 0x1907.</summary>
    public static readonly int CreatureGold = Field<uint>("CNWCCreature", "gold");
    /// <summary>CNWCCreature: non-zero on an associate (henchman, familiar, summon, companion), whose master's id is
    /// <see cref="CreatureMasterId"/>. <c>CGuiInvbox::HandleDropAsSource</c> (Linux @0x690990 <c>+0x294</c> / <c>+0x290</c>,
    /// Windows rva 0x8813d0 <c>+0x274</c> / <c>+0x270</c>) lets a potion dropped on the player's own associate be used on it.</summary>
    public static readonly int CreatureIsAssociate = Field<int>("CNWCCreature", "isAssociate");
    /// <inheritdoc cref="CreatureIsAssociate"/>
    public static readonly int CreatureMasterId = Field<uint>("CNWCCreature", "masterId");
    /// <summary>CPanelQuickBar: its 36 <c>CGuiQuickButton</c>s (three sets of 12) laid inline from <see cref="QuickbarButtons"/>,
    /// <see cref="QuickbarButtonStride"/> apart. <c>SetButton_Item</c> (Linux @0x6b1ab0 <c>slot*0x328 + 0xf8</c>, Windows rva
    /// 0x86b3d0 <c>slot*0x2c8 + 0xf0</c>); <c>UpdateItemButtons</c> stops at <c>+0x7298</c> (36 × 0x328 + 0xf8), and both
    /// quick-button drop handlers read the flag just past the array (<c>+0x72a4</c> / <c>+0x651c</c>).</summary>
    public static readonly int QuickbarButtons = Field("CPanelQuickBar", "buttons");
    /// <inheritdoc cref="QuickbarButtons"/>
    public static readonly int QuickbarButtonStride = Field("CPanelQuickBar", "buttonStride");
    /// <inheritdoc cref="QuickbarButtons"/>
    public const int QuickbarButtonCount = 36;
    /// <summary>CGuiQuickButton: what it holds, the <c>HandleLeftButton</c> switch (0 empty, 1 item, 2 spell, ...), and an item
    /// button's item object id. <c>SetButtonItem</c> writes both: Linux nm @0x6b1270 <c>+0x160</c> / <c>+0x10c</c>, Windows rva 0x867760 <c>+0x140</c> / <c>+0xec</c>.</summary>
    public static readonly int QuickButtonType = Field<byte>("CGuiQuickButton", "type");
    /// <inheritdoc cref="QuickButtonType"/>
    public static readonly int QuickButtonItem = Field<uint>("CGuiQuickButton", "itemOid");
    /// <summary>The rest of a button's content, as <c>SendButtonUpdateToServer</c> (Linux nm 0x6aa2e0, Windows rva 0x8602a0) reads it
    /// for the server, Windows 0x20 lower: a pair's second item, the int argument, the packed spell, an associate's target.</summary>
    public static readonly int QuickButtonItem2 = Field<uint>("CGuiQuickButton", "itemOid2");
    public static readonly int QuickButtonParam = Field<int>("CGuiQuickButton", "param");
    public static readonly int QuickButtonSpell = Field<long>("CGuiQuickButton", "spell");
    public static readonly int QuickButtonTarget = Field<uint>("CGuiQuickButton", "target");
    /// <summary>CPanelQuickBar's send gate: <c>SendButtonUpdateToServer</c> returns at 0 (the server's own set zeroes it around its writes).</summary>
    public static readonly int QuickbarSendGate = Field<int>("CPanelQuickBar", "sendGate");
    /// <summary>CGuiObject flags; bit 0 = disabled, which <c>CGuiButton::HandleLMouseDown</c> refuses on (<c>+8</c>, both builds).</summary>
    public static readonly int GuiObjectFlags = Field<uint>("CGuiObject", "flags");
    /// <summary>CGuiButton -&gt; its normal-state animation name (<c>char*</c>); quick buttons outline with <c>"qbEquip"</c> /
    /// <c>"qbMode"</c>. <c>SetButtonNormalAnimation</c> stores it at Linux <c>+0xe0</c>, Windows rva 0x1e5a00 <c>+0xc0</c>.</summary>
    public static readonly int ButtonNormalAnim = Field<IntPtr>("CGuiButton", "normalAnim");
    /// <summary>CNWCCreature: carried weight in <b>tenths of a pound</b> -- the label is
    /// <c>floor(weight / 10 + 0.5)</c>. Same two bodies (<c>+0x2ac</c> / <c>+0x28c</c>), and the same field the
    /// <c>'U'</c> case of <c>HandleServerToPlayerUpdate_GuiInventory</c> writes (Linux nm @0x79dd90, Windows rva
    /// 0x7b3f30). The max comes from <c>encumbrance.2da</c> column "Normal" at row = STR, not from the creature.
    /// </summary>
    public static readonly int CreatureCarriedWeight = Field<int>("CNWCCreature", "carriedWeight");
    /// <summary>CNWCItem: the base item id -- the index into <see cref="SymbolMap.GetBaseItem"/>'s array,
    /// and the same field on BOTH builds, because it sits below the class's divergence. Read by
    /// <c>CNWCItem::GetName</c> (Linux nm @0x7ce810, Windows rva 0x83d8a0) among others. Gold is base item 76;
    /// NOT the icon's base item, which is a second field.</summary>
    public static readonly int ItemBaseItem = Field<int>("CNWCItem", "baseItem");
    /// <summary>CNWCItem: non-zero =&gt; identified. <c>GetName</c> reads it twice on both builds
    /// (<c>+0x1e0</c> / <c>+0x1c0</c>): it gates the custom name, and when clear it prefixes TLK 0x15ac.</summary>
    public static readonly int ItemIdentified = Field<int>("CNWCItem", "identified");
    /// <summary>CNWCItem: the ghost / in-flight flag -- non-zero while a move waits on the server, which blocks a
    /// drag (<c>CGuiInvbox::CanEquipItem</c> @0x6906c0 refuses on it) and draws the icon at 0.4 alpha.
    /// <c>HandleServerToPlayerUpdate_GuiRepository</c> clears it on every confirmation (Linux nm @0x79e320
    /// <c>item+0x260</c>, Windows rva 0x7b4620 <c>item+0x240</c>).</summary>
    public static readonly int ItemGhost = Field<int>("CNWCItem", "ghost");
    /// <summary>CNWCItem: the <c>INVENTORY_SLOT_*</c> bitmask the item is equipped in, <c>-1</c> when it is not.
    /// <c>HandleServerToPlayerUpdate_GuiInventory</c> reads and writes it on both builds (Linux nm @0x79dd90
    /// <c>item+0x26c</c>, Windows rva 0x7b3f30 <c>item+0x24c</c>), beside a second slot field one dword up for
    /// two-handed / dual references.</summary>
    public static readonly int ItemEquipSlot = Field<int>("CNWCItem", "equipSlot");
    /// <summary>CNWCItem: the CONFIRMED backpack cell, two bytes. <c>y</c> is <b>global</b>
    /// (<c>page * rows + localY</c>), which is why one item's y can exceed the grid's 6 rows. Written by the
    /// <c>'A'</c> and <c>'M'</c> cases of <c>Update_GuiRepository</c> and read back by its remove step, on both
    /// builds. The pending pair the ghost writes is the next two bytes and is not declared.</summary>
    public static readonly int ItemRepoX = Field<byte>("CNWCItem", "repoX");
    /// <inheritdoc cref="ItemRepoX"/>
    public static readonly int ItemRepoY = Field<byte>("CNWCItem", "repoY");
    /// <summary>CNWCItem: the possessing creature's object id, INVALID when the item is on the ground.
    /// <c>Update_GuiRepository</c> sets it to the panel's shown oid on <c>'A'</c> and to INVALID on <c>'D'</c>
    /// (Linux nm @0x79e320, Windows rva 0x7b4620). Measured from the CNWCItem base, not the CNWCObject
    /// subobject -- contrast <see cref="ObjItemPlated"/>.</summary>
    public static readonly int ItemPossessor = Field<uint>("CNWCItem", "possessor");
    /// <summary>CNWCItem: 1 =&gt; the item sits in a container. The <c>'A'</c> of <c>Update_GuiContainer</c> sets it (Linux nm
    /// @0x79ee90 <c>item+0x288</c>, Windows rva 0x7b3790 <c>item+0x268</c>) and the <c>'A'</c> of <c>Update_GuiRepository</c>
    /// clears it (@0x79e320 / rva 0x7b4620). Both, and the container's <c>'D'</c>, stamp <see cref="ItemPossessor"/> with the
    /// panel's shown creature -- the player -- so this is what tells looted items from carried ones.</summary>
    public static readonly int ItemInContainer = Field<int>("CNWCItem", "inContainer");
    /// <summary>CNWCItem: stack size. The first store of <c>CNWCItem::SetStackSize(int)</c> on both builds
    /// (Linux nm @0x7cdbd0 <c>item+0x2a4</c>, Windows rva 0x83f190 <c>item+0x284</c>), which then suppresses the
    /// count label when <see cref="BaseItemStacking"/> is 1 or the count is below 2.</summary>
    public static readonly int ItemStackSize = Field<int>("CNWCItem", "stackSize");
    /// <summary>CNWCItem: the custom (renamed) item name as a <c>char*</c> + dword length. <c>GetName</c> tests
    /// the length and returns this text outright when the item is identified (Linux nm @0x7ce810
    /// <c>item+0x2b0/+0x2b8</c>, Windows rva 0x83d8a0 <c>item+0x290/+0x298</c>). Single-byte codepage, not UTF-8
    /// (docs: engine-text-codepage).</summary>
    public static readonly int ItemCustomName = Field<IntPtr>("CNWCItem", "customName");
    /// <inheritdoc cref="ItemCustomName"/>
    public static readonly int ItemCustomNameLen = Field<uint>("CNWCItem", "customNameLen");
    /// <summary>CNWBaseItem (a 200-byte record of <c>baseitems.2da</c>, reached through
    /// <see cref="SymbolMap.GetBaseItem"/>): the <b>equippable-slot bitmask</b>. The whole client-side equip
    /// rule is <c>(base-&gt;equipSlots &amp; invbox-&gt;slotMask) != 0</c>. Identical on both builds, paired
    /// on the shared-engine <c>CNWSCreature::CanEquipItem</c> (Linux nm @0xa7adf0, Windows rva 0x3d12c0).</summary>
    public static readonly int BaseItemEquipSlots = Field<uint>("CNWBaseItem", "equipSlots");
    /// <summary>CNWBaseItem: <c>InvSlotWidth</c> / <c>InvSlotHeight</c> in grid cells, already parsed by the client
    /// -- no 2DA read. <c>CNWCItem::UpdateIcons</c> @0x7cbc50 copies them onto the built <c>CGuiIcon</c>, which is
    /// what the grid places and reserves cells with. Identical on both builds, paired on the exported
    /// <c>CItemRepository::CheckBaseItemFits</c> (Linux nm @0xb439c0, Windows rva 0x4659e0).</summary>
    public static readonly int BaseItemInvSlotWidth = Field<byte>("CNWBaseItem", "invSlotWidth");
    /// <inheritdoc cref="BaseItemInvSlotWidth"/>
    public static readonly int BaseItemInvSlotHeight = Field<byte>("CNWBaseItem", "invSlotHeight");
    /// <summary>CNWBaseItem: the stacking limit as a <c>short</c>; <c>== 1</c> means "never show a count label".
    /// Identical on both builds -- <c>CNWCItem::SetStackSize</c> tests <c>*(short*)(base + 0x24) != 1</c> on each
    /// (Linux nm @0x7cdbd0, Windows rva 0x83f190).</summary>
    public static readonly int BaseItemStacking = Field<short>("CNWBaseItem", "stacking");
    /// <summary>CNWBaseItem: non-zero =&gt; a double-click means <see cref="SymbolMap.SendInputUseObject"/>; zero
    /// means "activate an item property" (walk the item's properties for type 0xf and open an action target).
    /// Identical on both builds -- <c>CGuiMultiInvbox::HandleDoubleClick</c> branches on
    /// <c>*(int*)(base + 0x80)</c> on each (Linux nm @0x690300, Windows rva 0x8801b0).</summary>
    public static readonly int BaseItemUsable = Field<int>("CNWBaseItem", "usable");
    /// <summary>CNWRules -&gt; its <c>CNWBaseItemArray*</c>. Every client body that needs a base item does the same
    /// two-step, <c>GetBaseItem(*(g_pRules + 0x20), item-&gt;baseItem)</c>, at the same offset on both builds
    /// (<c>CNWCItem::GetName</c> Linux nm @0x7ce810 / Windows rva 0x83d8a0, and three others).</summary>
    public static readonly int RulesBaseItems = Field<IntPtr>("CNWRules", "baseItems");
    /// <summary>CPanelInventory: non-zero =&gt; this panel is showing a CONTAINER rather than the plain
    /// backpack. <c>SetContainerGUIVisible</c> (Linux nm @0x68e5d0) is its only writer and
    /// <c>SetInventoryVisible</c>'s hide branch its reader. On a container OPEN it is still clear when
    /// <c>SetInventoryVisible(1, ...)</c> runs -- the container message calls the two in that order.</summary>
    public static readonly int InvContainerMode = Field<int>("CPanelInventory", "containerMode");
    /// <summary>CPanelInventory: whose inventory is on show. <c>SetInventoryVisible</c> resolves an INVALID
    /// argument to <c>GetPlayerCreatureId()</c> and stores it here before calling
    /// <see cref="SymbolMap.InvSetCharacterData"/> (Linux nm @0x68e760 <c>+0x31a0</c>, Windows rva 0x857cd0
    /// <c>+0x2f68</c>), and the notify tail sends it.</summary>
    public static readonly int InvShownOid = Field<uint>("CPanelInventory", "shownOid");
    /// <summary>CPanelInventory: 1 =&gt; this is my own inventory. <c>SetCharacterData</c> sets it for the player
    /// (or a DM) and CLEARS it for anyone else, stripping the gold label and gold button from the panel for good
    /// (Linux nm @0x68d810 <c>+0x31a4</c>, Windows rva 0x856fa0 <c>+0x2f6c</c>).</summary>
    public static readonly int InvIsMine = Field<int>("CPanelInventory", "isMine");
    /// <summary>CPanelInventory -&gt; its embedded <c>CPanelInvEquip</c> (the 14 equip slots), the subobject
    /// <c>SetInventoryVisible</c> adds and removes under the tag "Equip" (Linux nm @0x68e760 <c>this+0x368</c>,
    /// Windows rva 0x857cd0 <c>this+0x308</c>).</summary>
    public static readonly int InvEquipPanel = Field<CX.Engine.CPanelInvEquip>("CPanelInventory", "equipPanel", embedded: true);
    /// <summary>CPanelInventory: the three labels <c>SetCharacterData</c> fills -- gold, current weight, max
    /// weight. Each measured as the <c>m_pString</c> the body writes through, minus
    /// <see cref="LabelString"/> (Linux nm @0x68d810, Windows rva 0x856fa0). The gold one is also
    /// <c>RemoveFromPanel</c>'s target, an independent read of the same offset.</summary>
    public static readonly int InvGoldLabel = Field<CX.Engine.CGuiLabel>("CPanelInventory", "goldLabel", embedded: true);
    /// <inheritdoc cref="InvGoldLabel"/>
    public static readonly int InvWeightLabel = Field<CX.Engine.CGuiLabel>("CPanelInventory", "weightLabel", embedded: true);
    /// <inheritdoc cref="InvGoldLabel"/>
    public static readonly int InvMaxWeightLabel = Field<CX.Engine.CGuiLabel>("CPanelInventory", "maxWeightLabel", embedded: true);
    /// <summary>CPanelInventory -&gt; its embedded container grid (<c>CGuiContainer</c>, 7x5), the widget every container
    /// update lands in: <c>Update_GuiContainer</c> adds to <c>panel+0x2628</c> (Linux nm @0x79ee90) / <c>panel+0x24b0</c>
    /// (Windows rva 0x7b3790), and <c>HandleContainerPageUp</c> empties the same one (@0x68c520 / rva 0x855d00).</summary>
    public static readonly int InvContainerGrid = Field<CX.Engine.CGuiContainer>("CPanelInventory", "containerGrid", embedded: true);
    /// <summary>CPanelInventory -&gt; its embedded backpack grid (<c>CGuiInvRepository</c>, a <c>CGuiMultiPageMultiInvbox</c>), the
    /// box every backpack <c>'A'</c> adds to: <c>Update_GuiRepository</c> (Linux nm @0x79e320 <c>panel+0x1c50</c>, Windows rva
    /// 0x7b4620 <c>panel+0x1b08</c>), and <c>Update_GuiContainer</c>'s <c>'A'</c> takes a looted item out of the same one
    /// (@0x79ee90 / rva 0x7b3790). Its <c>CGuiMultiInvbox</c> base is the one <see cref="InvContainerGrid"/>'s view reads.</summary>
    public static readonly int InvBackpackGrid = Field<CX.Engine.CGuiContainer>("CPanelInventory", "backpackGrid", embedded: true);
    /// <summary>CPanelInventory: the open container's object id, INVALID when none. The container message stores it
    /// (Linux nm @0x766f30 <c>+0x2810</c>) and <c>HandleContainerClose</c> <c>UseObject</c>s it (@0x68be50 <c>+0x2810</c>,
    /// Windows rva 0x855ba0 <c>+0x268c</c>). It is the container grid's own target field (<c>grid+0x1e8</c> / <c>+0x1dc</c>),
    /// which <c>CGuiContainer::ProcessItemDrop</c> sends a drop to.</summary>
    public static readonly int InvContainerOid = Field<uint>("CPanelInventory", "containerOid");
    /// <summary>CPanelInventory: non-zero =&gt; the open container is an item (a bag), not a placeable.
    /// <c>SetContainerGUIVisible</c>'s third argument (Linux nm @0x68e5d0 <c>+0x2814</c>, Windows rva 0x8575f0
    /// <c>+0x2690</c>); the grid's radial flags and a world pickup into it key off it.</summary>
    public static readonly int InvContainerIsItem = Field<int>("CPanelInventory", "containerIsItem");
    /// <summary>CPanelInventory: the container's page count (a byte), from the open message and the server's <c>'P'</c>
    /// (<c>SetContainerPagenumVisible</c> @0x68e100 writes <c>+0x3198</c>); <c>HandleContainerPageUp</c> wraps at it (Linux
    /// nm @0x68c520 <c>+0x3198</c>, Windows rva 0x855d00 <c>+0x2f60</c>). The constructor sets 1.</summary>
    public static readonly int InvContainerPages = Field<byte>("CPanelInventory", "containerPages");
    /// <summary>CPanelInventory: the container's title label (tag <c>Title</c>): <c>SetContainerGraphic</c> writes the
    /// placeable, corpse or item name through its <see cref="LabelString"/> (Linux nm @0x68e280 <c>+0x2940</c>, Windows rva
    /// 0x8576d0 <c>+0x27c0</c>), so the label sits at <c>+0x28b0</c> / <c>+0x2730</c>, one <c>CGuiLabel</c> after the page
    /// label that <c>HandleContainerPageUp</c> writes (<c>+0x28a8</c> / <c>+0x2728</c>).</summary>
    public static readonly int InvContainerTitle = Field<CX.Engine.CGuiLabel>("CPanelInventory", "containerTitle", embedded: true);
    /// <summary>CGuiContainer (a <c>CGuiMultiPageMultiInvbox</c>): columns and rows of one page, as <c>CGuiMultiInvbox::SetSize</c>
    /// stores them (Linux nm @0x692bf0 <c>+0x1a0/+0x1a4</c>, Windows rva 0x883220 <c>+0x190/+0x194</c>).</summary>
    public static readonly int ContainerCols = Field<int>("CGuiContainer", "cols");
    /// <inheritdoc cref="ContainerCols"/>
    public static readonly int ContainerRows = Field<int>("CGuiContainer", "rows");
    /// <summary>CGuiContainer: <c>uint[cols*rows]</c>, the item id at each footprint's origin cell (<c>cols*localY + x</c>),
    /// INVALID elsewhere. <c>SetSize</c> allocates it (Linux nm @0x692bf0 <c>+0x1b0</c>, Windows rva 0x883220 <c>+0x1a0</c>),
    /// <c>AddItem</c> writes the origin and <c>RemoveItem</c> (@0x692760) resets it, so a scan of it is the page's items.</summary>
    public static readonly int ContainerItemIds = Field<IntPtr>("CGuiContainer", "itemIds");
    /// <summary>CGuiContainer: the page the grid holds; <c>CGuiMultiPageMultiInvbox::AddItem</c> drops any item off it (Linux
    /// nm @0x6943f0 <c>+0x1e0</c>, Windows rva 0x87f970 <c>+0x1d0</c>). For the container grid it is the server's page:
    /// <c>HandleContainerPageUp</c> moves it with its <c>SelectNextPage</c> (panel <c>+0x2808</c> / <c>+0x2680</c>).</summary>
    public static readonly int ContainerPage = Field<int>("CGuiContainer", "page");
    /// <summary>CPanelInvEquip: the character-name label (<c>TGuiLabel</c>) and the armour-class label, the other
    /// two <c>SetCharacterData</c> fills. Same derivation from the same two bodies: the panel-relative store
    /// minus the embedded equip panel's own offset, minus <see cref="LabelString"/>.</summary>
    public static readonly int InvEquipNameLabel = Field<CX.Engine.CGuiLabel>("CPanelInvEquip", "nameLabel", embedded: true);
    /// <inheritdoc cref="InvEquipNameLabel"/>
    public static readonly int InvEquipAcLabel = Field<CX.Engine.CGuiLabel>("CPanelInvEquip", "acLabel", embedded: true);
    /// <summary>CGuiLabel -&gt; <c>CAurString* m_pString</c>, the text object the label draws, at the same
    /// offset on both builds. A pointer to a live object, never an inline <c>CExoString</c>: the constructor
    /// @0x591fd0 fills it with <c>NewCAurString</c>, so it is non-null before the panel is ever shown, and
    /// every setter goes through the CAurString vtable (<see cref="AurStringSetTextSlot"/>).</summary>
    public static readonly int LabelString = Field<CX.Engine.CAurString>("CGuiLabel", "string");
    /// <summary>Nui::Layout::Layoutable: nonzero = the parent's child loop does NOT <c>nk_layout_space_push</c> this child,
    /// so <c>panel-&gt;row.item</c> still holds the previous child's rect when it renders (Linux nm @0x84c9f8
    /// <c>cmpb $0,0x50(%r15)</c> -&gt; the push at 0x84dab5; Windows rva 0x951260 @0x1409515d6, the push at 0x1409515fa).</summary>
    public static readonly int NuiLayoutableNoPush = Field<byte>("Nui::Layout::Layoutable", "noPush");
    /// <summary>Virtual slot of <c>Render(nk_context*)</c> on every Nui::Layout class: the parent's child call (Linux
    /// @0x84ce7e <c>call *0x28(%rax)</c>, Windows @0x140951cfe <c>call *0x20(%rax)</c>), and the slot the
    /// <c>Widget</c>/<c>StatefulWidget</c> vtables fill with <see cref="SymbolMap.NuiWidgetRender"/>.</summary>
    public static readonly int NuiRenderSlot = Field("Nui::Layout::Layoutable", "vtbl.Render", Expect.VtblSlot, sig: "void(IntPtr ctx)");

    /// <summary>kiwi::impl::SolverImpl, the Cassowary solver each NUI LayoutGroup embeds at +0x5a8: its tableau, a vector of
    /// (Symbol key, Row*) pairs sorted by symbol id -- begin, then end. <c>substitute</c> @0x85b890 / rva 0x3458e0 reads both
    /// once and walks between them. Windows sits 8 further per Loki map before it: MSVC keeps each AssocVector's empty
    /// comparator as a member, so every SolverImpl field after m_cns moves (and Row's constant, +0x18 vs +0x20).</summary>
    public static readonly int KiwiRows = Field<IntPtr>("kiwi::impl::SolverImpl", "rows");
    /// <inheritdoc cref="KiwiRows"/>
    public static readonly int KiwiRowsEnd = Field<IntPtr>("kiwi::impl::SolverImpl", "rowsEnd");
    /// <summary>m_infeasible_rows, the Symbol vector <c>substitute</c> appends every negative restricted row to (begin, end):
    /// the push at 0x85b9f0 / rva 0x3459f0 reads its end and capacity.</summary>
    public static readonly int KiwiInfeasible = Field<IntPtr>("kiwi::impl::SolverImpl", "infeasible");
    /// <inheritdoc cref="KiwiInfeasible"/>
    public static readonly int KiwiInfeasibleEnd = Field<IntPtr>("kiwi::impl::SolverImpl", "infeasibleEnd");
    /// <summary>m_artificial, non-null only while <c>addWithArtificialVariable</c> @0x85daa0 / rva 0x340410 runs its feasibility
    /// optimize: that row sits in the tableau under the slack symbol <see cref="KiwiIdTick"/> - 1, inserted without a substitute.</summary>
    public static readonly int KiwiArtificial = Field<IntPtr>("kiwi::impl::SolverImpl", "artificial");
    /// <summary>m_id_tick, the next symbol id: 1 after the reset <c>LayoutGroup::SetChildren</c> @0x853db0 inlines, bumped
    /// once per new symbol (read and incremented at the head of <c>addWithArtificialVariable</c> on both builds).</summary>
    public static readonly int KiwiIdTick = Field<ulong>("kiwi::impl::SolverImpl", "idTick");
    /// <summary>kiwi::impl::Row's cell vector of (Symbol, double) pairs sorted by symbol id -- begin, then end.</summary>
    public static readonly int KiwiRowCells = Field<IntPtr>("kiwi::impl::Row", "cells");
    /// <inheritdoc cref="KiwiRowCells"/>
    public static readonly int KiwiRowCellsEnd = Field<IntPtr>("kiwi::impl::Row", "cellsEnd");
    /// <summary>The (Symbol, 8-byte value) pair both kiwi maps store -- a tableau entry's value is its Row*, a cell's its
    /// coefficient: the symbol id (the only sort key), its type (1 External, 2 Slack, 3 Error, 4 Dummy), the value, and
    /// the record size. <c>substitute</c>'s walks on both builds.</summary>
    public static readonly int KiwiPairId = Field<ulong>("kiwi::impl::SymbolPair", "id");
    /// <inheritdoc cref="KiwiPairId"/>
    public static readonly int KiwiPairType = Field<int>("kiwi::impl::SymbolPair", "type");
    /// <inheritdoc cref="KiwiPairId"/>
    public static readonly int KiwiPairValue = Field<IntPtr>("kiwi::impl::SymbolPair", "value");
    /// <inheritdoc cref="KiwiPairId"/>
    public static readonly int KiwiPairStride = Field("kiwi::impl::SymbolPair", "stride");
    /// <summary>m_cns, the constraints the solver accepted: a flat vector of (Constraint, Tag) records sorted by
    /// ConstraintData* -- begin, then end. <c>addConstraint</c> inserts here only after the row is in the
    /// tableau, so a constraint that threw is never in it.</summary>
    public static readonly int KiwiCns = Field<CX.Engine.kiwi__impl__CnPair>("kiwi::impl::SolverImpl", "cns");
    /// <inheritdoc cref="KiwiCns"/>
    public static readonly int KiwiCnsEnd = Field<IntPtr>("kiwi::impl::SolverImpl", "cnsEnd");
    /// <summary>kiwi::impl::Row's constant term: <c>addConstraint</c>'s near-zero test on a new row, and
    /// <c>addWithArtificialVariable</c>'s success test on the artificial objective.</summary>
    public static readonly int KiwiRowConstant = Field<double>("kiwi::impl::Row", "constant");
    /// <summary>One m_cns record: the ConstraintData*, the ids of its Tag's marker and other symbols (each Symbol is
    /// {id, type}, 16 bytes), and the record size. The marker is the slack, dummy or error symbol the constraint left in
    /// the tableau, which is how a tableau row's symbols lead back to the rules it was built from.</summary>
    public static readonly int KiwiCnData = Field<CX.Engine.kiwi__ConstraintData>("kiwi::impl::CnPair", "data");
    /// <inheritdoc cref="KiwiCnData"/>
    public static readonly int KiwiCnMarker = Field<ulong>("kiwi::impl::CnPair", "marker");
    /// <inheritdoc cref="KiwiCnData"/>
    public static readonly int KiwiCnOther = Field<ulong>("kiwi::impl::CnPair", "other");
    /// <inheritdoc cref="KiwiCnData"/>
    public static readonly int KiwiCnStride = Field("kiwi::impl::CnPair", "stride");
    /// <summary>kiwi::Constraint::ConstraintData, the same on both builds: the expression's terms (begin, end) and constant,
    /// the strength (required = 1 001 001 000) and the operator (0 &lt;=, 1 &gt;=, 2 ==). The rule is
    /// <c>sum(coefficient * variable) + constant OP 0</c>.</summary>
    public static readonly int KiwiCnTerms = Field<CX.Engine.kiwi__Term>("kiwi::ConstraintData", "terms");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnTermsEnd = Field<IntPtr>("kiwi::ConstraintData", "termsEnd");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnConstant = Field<double>("kiwi::ConstraintData", "constant");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnStrength = Field<double>("kiwi::ConstraintData", "strength");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnOp = Field<int>("kiwi::ConstraintData", "op");
    /// <summary>kiwi::Term: the variable (a VariableData*, which NUI never names) and its coefficient, and the record size.</summary>
    public static readonly int KiwiTermVariable = Field<IntPtr>("kiwi::Term", "variable");
    /// <inheritdoc cref="KiwiTermVariable"/>
    public static readonly int KiwiTermCoefficient = Field<double>("kiwi::Term", "coefficient");
    /// <inheritdoc cref="KiwiTermVariable"/>
    public static readonly int KiwiTermStride = Field("kiwi::Term", "stride");
    /// <summary>A layout element's children, a vector of shared_ptr (element, control block) -- begin, then end.</summary>
    public static readonly int NuiChildren = Field<IntPtr>("Nui::Layout::Layoutable", "children");
    /// <inheritdoc cref="NuiChildren"/>
    public static readonly int NuiChildrenEnd = Field<IntPtr>("Nui::Layout::Layoutable", "childrenEnd");
    /// <summary>An element's twelve kiwi variables (VariableData*): x, y, width, height, then padding left, top, right,
    /// bottom, then margin left, top, right, bottom -- read off the ctor's three LayoutExpressions (inner =
    /// outer minus padding, outer plus margin).</summary>
    public static readonly int NuiVars = Field<IntPtr>("Nui::Layout::Layoutable", "vars", count: 12);
    /// <summary>A Span's axis, read once by <c>Span::BuildConstraintDependants</c>: 1 = vertical.</summary>
    public static readonly int NuiSpanDirection = Field<int>("Nui::Layout::Span", "direction");
    /// <summary>The kiwi solver a LayoutGroup embeds; <c>SetChildren</c> resets it and adds the subtree's constraints.</summary>
    public static readonly int NuiGroupSolver = Field<CX.Engine.kiwi__impl__SolverImpl>("Nui::Layout::LayoutGroup", "solver", embedded: true);
    /// <summary>The group's own width and height, the two edit variables <c>SetChildren</c> adds at medium strength.</summary>
    public static readonly int NuiGroupEditWidth = Field<IntPtr>("Nui::Layout::LayoutGroup", "editWidth");
    /// <inheritdoc cref="NuiGroupEditWidth"/>
    public static readonly int NuiGroupEditHeight = Field<IntPtr>("Nui::Layout::LayoutGroup", "editHeight");
    /// <summary>The vector (begin) holding the one constraint <c>SetChildren</c> adds itself: the global UI-scale variable
    /// equal to the current scale. Its single term names that variable, which has no symbol of its own.</summary>
    public static readonly int NuiGroupScaleConstraints = Field<IntPtr>("Nui::Layout::LayoutGroup", "scaleConstraints");
    /// <summary>The window's element-id map (JSON id to weak_ptr&lt;Layoutable&gt;; only elements that declared an id).
    /// Linux: the libstdc++ hashtable's before-begin next, the first node of a null-terminated list. Windows: the MSVC
    /// list's head sentinel, a circular list.</summary>
    public static readonly int NuiIdsHead = Field<IntPtr>("Nui::JSON::JsonWindow", "idsHead");
    /// <summary>An id-map node: the key std::string (its address) and the weak_ptr's element pointer.</summary>
    public static readonly int NuiIdNodeKey = Field<byte>("Nui::JSON::IdNode", "key");
    /// <inheritdoc cref="NuiIdNodeKey"/>
    public static readonly int NuiIdNodeElement = Field<IntPtr>("Nui::JSON::IdNode", "element");

    // ---- CX-108 lights ----
    // The light priority terms. MEASURED on both binaries with objdump: LightManager is name-exported on
    // Windows, so `PrioritizeLights` (rva 0x7ff60) reads there exactly as it does here.

    /// <summary>The light's own priority OVERRIDE, and zero means "use the MDL node's"
    /// (<see cref="MdlLightPriority"/>). <c>LightManager::PrioritizeLights</c> @0x4c0940 is the reader;
    /// <c>Gob::SetLightPriority</c> @0x43f760 and the tileset main-light setter (which writes 4) are the
    /// writers, and <c>PartLight::PartLight</c> zeroes it.</summary>
    public static readonly int LightPriority = Field<int>("PartLight", "priority");

    /// <summary>The MDL light node's <c>lightpriority</c> field
    /// (<c>MdlNodeLight::InternalParseField</c> @0x44baf0 parses the name into it; the constructor defaults
    /// it to 5). Lower sorts first, so a builder's 1 outranks the default.</summary>
    public static readonly int MdlLightPriority = Field<int>("MdlNodeLight", "lightPriority");

    /// <summary>The BSP node inventory's <c>CExoArrayList&lt;PartLight*&gt;</c> -- the TILE lights.
    /// <c>SceneBSPAddPart</c> @0x47ca10 files a light here, and it is the only container that holds one:
    /// <c>CollapsePartTree</c> @0x462ff0 clears every tile part's child list, so no part-tree walk can reach
    /// a tile light. Sub-offsets of the record <see cref="BspDataTriMesh"/> names.</summary>
    public static readonly int BspDataLight = Field("BSPNode", "light");
    /// <inheritdoc cref="BspDataLight"/>
    public static readonly int BspDataLightCount = Field("BSPNode", "lightCount");

    // ================= CX-107 behaviours (CX-107 §1, §6.1) =================
    // The behaviour solvers Alloy ports write engine fields through the same virtuals the engine does, so
    // every slot below was measured on BOTH images: the Windows Gob and Part vtables were located by
    // searching .rdata for the 64-bit VA of a known exported method and walking the table (Gob's is the
    // second GetPosition hit at 0xd88ac0; the first is StringGob's). Every one is -8 for the usual reason
    // (#433: MSVC gives a virtual destructor one slot, Itanium two) -- and the DELETING destructor is
    // therefore Windows slot 0 against Linux slot 8, not -8.

    /// <summary>The weak-reference back-list every <c>CAurObject</c> keeps: data, count, capacity. A solver
    /// registers a stack <c>SafePointer</c> here for the length of its solve, so an owner destroyed inside a
    /// dependency's <c>Animate</c> nulls it instead of leaving a dangling pointer. It sits immediately before
    /// the paired <see cref="GobHighlight"/>, so it is the same place on both builds.</summary>
    public static readonly int AurObjBackRefs = Field<IntPtr>("CAurObject", "backRefs");
    /// <inheritdoc cref="AurObjBackRefs"/>
    public static readonly int AurObjBackRefCount = Field<int>("CAurObject", "backRefCount");

    /// <summary>A behaviour's dependency list, the same <c>CExoArrayList</c> shape: the objects whose
    /// <c>Animate</c> the base solve drags forward before reading their part transforms.</summary>
    public static readonly int BehaviorDeps = Field<IntPtr>("CAurBehavior", "deps");
    /// <inheritdoc cref="BehaviorDeps"/>
    public static readonly int BehaviorDepCount = Field<int>("CAurBehavior", "depCount");

    /// <summary>The attach family's parent <c>CAurPart*</c> (the part whose world transform the owner is
    /// snapped to) and its "delete me rather than detach when the parent dies" flag, set by
    /// <c>Message("diewithoutparent &lt;n&gt;")</c>.</summary>
    public static readonly int AttachPart = Field<IntPtr>("CAurBehaviorAttach", "part");
    /// <inheritdoc cref="AttachPart"/>
    public static readonly int AttachDieWithoutParent = Field<int>("CAurBehaviorAttach", "dieWithoutParent");
    /// <summary>The translate / rotate subclasses' own offset <c>Vector</c> at the same place (one subclass
    /// each, so one row): a world translation rotated by the owner's orientation, or Euler degrees
    /// <c>(pitch, roll, yaw)</c> fed to <c>YawPitchRoll(z, x, y)</c>.</summary>
    public static readonly int AttachOffset = Field<System.Numerics.Vector3>("CAurBehaviorAttach", "offset");
    /// <summary><c>CAurBehaviorCrossSceneAttachWithOffset</c>'s constant world offset, added to the owner's
    /// position after the snap.</summary>
    public static readonly int CrossAttachOffset = Field<System.Numerics.Vector3>("CAurBehaviorCrossSceneAttach", "offset");
    /// <summary>CAurBehaviorCrossSceneAttach's remembered scene (<c>SafePointer&lt;CAurScene&gt;</c>) and its
    /// StopSceneChanges latch (byte): <c>Message</c> (Linux @0x58bf30, Windows rva 0x203810) reads +0x70 and writes
    /// +0x80 on both.</summary>
    public static readonly int CrossAttachKeptScene = Field<IntPtr>("CAurBehaviorCrossSceneAttach", "keptScene");
    public static readonly int CrossAttachLatch = Field<byte>("CAurBehaviorCrossSceneAttach", "latch");

    /// <summary>The subwindow solver's two dummy parts (top-left and bottom-right of the GUI viewport) and
    /// the <c>setPosition</c> flag <c>GuiAttachViewport</c> passes.</summary>
    public static readonly int SubwindowPartA = Field<IntPtr>("CAurBehaviorSubwindow", "partA");
    /// <inheritdoc cref="SubwindowPartA"/>
    public static readonly int SubwindowPartB = Field<IntPtr>("CAurBehaviorSubwindow", "partB");
    /// <inheritdoc cref="SubwindowPartA"/>
    public static readonly int SubwindowSetPos = Field<byte>("CAurBehaviorSubwindow", "setPos");

    /// <summary>The camera-follow target offset added to the target's position before the focus lerp
    /// (ctor default <c>(0, 0, 1)</c>: chest height), and the focus lag rate that drives
    /// <c>a = 1 - 0.5^(dt*rate)</c> (default 1.0). The rest of the class is already declared above
    ///.</summary>
    public static readonly int FollowOffset = Field<System.Numerics.Vector3>("CAurBehaviorCameraFollow", "offset");
    /// <inheritdoc cref="FollowOffset"/>
    public static readonly int FollowLag = Field<float>("CAurBehaviorCameraFollow", "lag");

    /// <summary><c>CAurBehaviorNavigate</c>'s four per-frame inputs, each consumed and cleared by one solve:
    /// yaw rate, pitch rate, forward speed, strafe speed (deg/s and units/s).</summary>
    public static readonly int NavigateYawRate = Field<float>("CAurBehaviorNavigate", "yawRate");
    /// <inheritdoc cref="NavigateYawRate"/>
    public static readonly int NavigatePitchRate = Field<float>("CAurBehaviorNavigate", "pitchRate");
    /// <inheritdoc cref="NavigateYawRate"/>
    public static readonly int NavigateForward = Field<float>("CAurBehaviorNavigate", "forward");
    /// <inheritdoc cref="NavigateYawRate"/>
    public static readonly int NavigateStrafe = Field<float>("CAurBehaviorNavigate", "strafe");
    /// <summary>Cleared by every <c>Navigate::Control</c> and read by nothing; kept so the port leaves the
    /// object in the same state the engine would.</summary>
    public static readonly int NavigateSpare = Field<float>("CAurBehaviorNavigate", "spare");

    /// <summary><c>CAurBehaviorCameraOnAStick</c>'s spring: last solved position, stick length (2.5), gain
    /// (20.0), pitch (70.0), fixed height (4.0), the mode int and the direction <c>Vector</c>. UNREACHABLE
    /// in this build -- nothing constructs the class -- so these are decompiler evidence only.</summary>
    public static readonly int StickLast = Field<System.Numerics.Vector3>("CAurBehaviorCameraOnAStick", "last");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickLength = Field<float>("CAurBehaviorCameraOnAStick", "length");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickGain = Field<float>("CAurBehaviorCameraOnAStick", "gain");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickPitch = Field<float>("CAurBehaviorCameraOnAStick", "pitch");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickHeight = Field<float>("CAurBehaviorCameraOnAStick", "height");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickMode = Field<int>("CAurBehaviorCameraOnAStick", "mode");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickDir = Field<System.Numerics.Vector3>("CAurBehaviorCameraOnAStick", "dir");

    /// <summary><c>CAurObject::SetBehavior(CAurBehavior*)</c> -- passing null is how a solver whose parent
    /// died detaches; the setter DELETES the behaviour, so nothing may touch <c>this</c> afterwards.</summary>
    public static readonly int GobSetBehaviorSlot = Field("Gob", "vtbl.SetBehavior", Expect.VtblSlot, sig: "void(IntPtr behavior)");
    /// <summary><c>CAurObject::GetBehavior(int type)</c> -- with -1, "the behaviour you have"; the
    /// identity test every solve makes before and after touching the owner.</summary>
    public static readonly int GobGetBehaviorSlot = Field("Gob", "vtbl.GetBehavior", Expect.VtblSlot, sig: "IntPtr(int type)");
    /// <summary>The DELETING destructor: <c>Gob::~Gob</c> + <c>operator delete</c>. An attach behaviour with
    /// <c>diewithoutparent</c> set deletes its owner through it. Itanium's two destructor slots are
    /// complete-then-deleting; MSVC has one -- so this row is the one slot in the table that is NOT -8, and
    /// MSVC's takes a flags argument Itanium's does not, which is why no signature is declared.</summary>
    public static readonly int GobDeleteSlot = Field("Gob", "vtbl.Delete", Expect.VtblSlot);
    /// <summary><c>CAurObject::GetName()</c> -- <c>Gob+0x24</c>, the inline name buffer. The subwindow
    /// solver keys on four exact panel names.</summary>
    public static readonly int GobGetNameSlot = Field("Gob", "vtbl.GetName", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary><c>CAurObject::GetScene()</c> and <c>CAurObject::AsCamera()</c> -- the subwindow solver's
    /// two lookups: the panel's scene, and the owner's <c>Camera</c> base (which sits 8 bytes before the
    /// <c>CAurObject</c> a <c>CAurCamera</c> hands out).</summary>
    public static readonly int GobGetSceneSlot = Field("Gob", "vtbl.GetScene", Expect.VtblSlot, sig: "IntPtr()");
    /// <inheritdoc cref="GobGetSceneSlot"/>
    public static readonly int GobAsCameraSlot = Field("Gob", "vtbl.AsCamera", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary><c>CAurObject::SetPosition(Vector)</c> and <c>SetOrientation(Quaternion)</c> -- the only
    /// writers the solvers' offsets go through. No signature is declared: both RETURN their argument by
    /// value, which SysV answers in registers and Win64 through a caller buffer, so
    /// <c>CX.Alloy.BehaviorCall</c> carries both argument lists.</summary>
    public static readonly int GobSetPositionSlot = Field("Gob", "vtbl.SetPosition", Expect.VtblSlot);
    /// <inheritdoc cref="GobSetPositionSlot"/>
    public static readonly int GobSetOrientationSlot = Field("Gob", "vtbl.SetOrientation", Expect.VtblSlot);

    // ================= engine constants that are not offsets =================
    // They rode into this table with #436 because the file they lived in was the offset file. They are
    // magic VALUES the engine compares against, not places -- no row, nothing to resolve per platform.

    /// <summary>The engine's "no such pool" token, tested at every one of the three VDO token fields
    /// (<c>cmp $0xffffffff,%eax</c> @0x4d003a) -- an absent token is skipped, not an error.</summary>
    public const uint VdoNoPool = 0xFFFFFFFF;

    /// <summary>Range that tells a directional (sun/moon) light from a placed one: directionals carry
    /// ~100000, points ~10. Read off <see cref="LightRange"/> by everything that has to tell them apart.</summary>
    public const float DirectionalRange = 1000f;

    /// <summary><c>CAMERA_FLAG_DISABLE_COLLISION</c> (nwscript.nss), the <see cref="ModuleCameraFlags"/> bit
    /// <c>UpdateCameraCollision</c> refuses on.</summary>
    public const int CameraFlagDisableCollision = 0x02;

    // ================= build key =================

    /// <summary>The running client's build key, whatever it is -- logged even on mismatch, so the stand-down
    /// line can name what was actually loaded.</summary>
    public static string BuildKey => _key ??= OperatingSystem.IsWindows() ? WindowsKey() : LinuxKey();

    /// <summary>Where the client image is loaded: the module base on Windows; on Linux the exe's lowest mapping (its
    /// first PT_LOAD sits at vaddr 0, so nm addresses are offsets from it), which the build-key read takes from
    /// <c>/proc/self/maps</c>.</summary>
    public static IntPtr ImageBase { get { _ = BuildKey; return (IntPtr)(long)_imageLo; } }

    /// <summary>True when <paramref name="p"/> lies inside the client image's mappings. A build key is not proof of the
    /// image: run-demo's stub host carries the client's build-id around a few KB of code.</summary>
    public static bool InImage(IntPtr p) { _ = BuildKey; return (ulong)p >= _imageLo && (ulong)p < _imageHi; }

    /// <summary>Is the running client a build we have measured (<see cref="ClientBuilds"/>)? The extender's
    /// one rot policy: <see cref="ServiceHost.Run"/> asks it before discovery and stands the whole unit down,
    /// so no per-feature gate can be the first to notice.</summary>
    public static bool KeyOk => ClientBuilds.Current != null;

    /// <summary>Rows the running build's file gives no Windows value (<c>-</c>). Zero is the section-3 finish
    /// line. Counted on BOTH platforms -- the migration is worked on Linux, and a counter that only moves on
    /// Windows is not visible where the work happens.</summary>
    public static int UnresolvedCount { get; private set; }

    /// <summary>Rows with no value for the OS we are running on -- read as 0, so <see cref="Require"/> stands
    /// down every feature that reads one.</summary>
    public static int MissingCount { get; private set; }

    /// <summary>Unit gate, <see cref="SymbolMap.Require"/>'s pattern for layout rather than symbols: a
    /// feature that reads engine memory calls this at the top of <c>Initialize</c> and stands down whole if
    /// the answer is no, because an offset measured on a different build renders garbage.</summary>
    /// <param name="classes">The engine classes this feature reads; empty means "every row".</param>
    public static bool Require(ExtenderLog log, string feature, params string[] classes)
    {
        if (!KeyOk)
        {
            log.Always($"{feature}: standing down as a unit -- client build {BuildKey} is not a known build " +
                       $"({ClientBuilds.Known}) (rot: docs/update-day.md)");
            return false;
        }
        int missing = classes.Length == 0 ? MissingCount : MissingIn(classes);
        if (missing > 0)
        {
            log.Always($"{feature}: standing down as a unit -- {missing} engine offset(s) have no value for " +
                       $"this OS in build {ClientBuilds.Current?.Name} (docs/engine-layout.md)");
            return false;
        }
        return true;
    }

    /// <summary>Join <c>/knobs</c>. Once, from the host.</summary>
    public static void Register() => KnobSurface.Note(() =>
        $"engine layout: {_rows.Count} offset(s), {UnresolvedCount} without a Windows value, {MissingCount} missing here; " +
        $"build {BuildKey} = {ClientBuilds.Current?.Name ?? $"UNKNOWN -- ROT (known: {ClientBuilds.Known})"}");

    // ================= probes =================

    /// <summary>What a resolved offset can assert about live memory before anything renders off it.</summary>
    private enum Expect
    {
        /// <summary>Nothing this offset can cheaply claim.</summary>
        None,
        /// <summary>A boolean field: the dword is 0 or 1. A wrong offset lands in a pointer or a float.</summary>
        Byte01,
        /// <summary><see cref="Byte01"/> on a ONE-byte field, which is what <c>Field&lt;byte&gt;</c> turns it into:
        /// the dword read would take in padding bytes no constructor writes (Gob+0x199's two neighbours stood
        /// the renderer down on heap garbage).</summary>
        Bool8,
        /// <summary>A vtable slot: <c>*(*obj + off)</c> is a code address inside the client image.</summary>
        VtblSlot,
    }

    /// <summary>
    /// Validate this class's resolved offsets against a live instance, once, and stand the caller down
    /// loudly rather than let it render off a wrong offset. Call at the first sighting of a pointer of that
    /// class, behind the caller's own one-shot latch -- a name lookup does not belong in a frame loop.
    /// </summary>
    /// <returns>false if any probe failed -- the caller should treat that as rot.</returns>
    public static bool Probe(string cls, IntPtr obj, ExtenderLog? log)
    {
        if (obj == IntPtr.Zero) return true;
        _ = BuildKey;   // the key readers are also what fill the image range VtblSlot compares against
        bool ok = true;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            if (r.Class != cls || r.Expect == Expect.None) continue;
            if (Holds(r, obj)) continue;
            log?.Always($"layout: probe FAILED -- {r.Class}.{r.Field} at +0x{r.Value:x} " +
                        $"(build {BuildKey}); re-measure, docs/engine-layout.md");
            ok = false;
        }
        return ok;
    }

    private static bool Holds(Row r, IntPtr obj)
    {
        switch (r.Expect)
        {
            case Expect.Byte01:
                int v = *(int*)(obj + r.Value);
                return v == 0 || v == 1;
            case Expect.Bool8:
                byte b = *(byte*)(obj + r.Value);
                return b <= 1;
            case Expect.VtblSlot:
                IntPtr vtbl = *(IntPtr*)obj;
                if (vtbl == IntPtr.Zero) return true;   // no instance to judge, not a failed probe
                IntPtr fn = *(IntPtr*)(vtbl + r.Value);
                return (ulong)fn >= _imageLo && (ulong)fn < _imageHi;
            default:
                return true;
        }
    }

    // ---- CX-103/104/105 ----

    /// <summary>Gob -&gt; its <c>CAurObjectVisualTransformData</c>, whose FIRST member is the scale
    /// <c>LerpFloat</c> that <c>Gob::GetAdjustedScale</c> @0x437cf0 reads. The gob loop of
    /// <c>Scene::AddPartsToDrawBuckets</c> folds each attach-chain ancestor's <c>Lerped</c> into the pushed
    /// scale -- the one placement term <c>Part::GetWorld</c> does not carry.</summary>
    public static readonly int GobScaleLerp = Field<CX.Engine.LerpFloat>("Gob", "scaleLerp", embedded: true);
    /// <summary>The other factor of <c>Gob::GetAdjustedScale</c> -- <c>Lerped(gob+0x1c0) *
    /// *(float*)(gob+0x49c)</c>. Read only as a CHANGE-DETECTION input (a plain load, where the getter is six
    /// <c>LerpFloat::Lerped</c> calls); the composed scale still comes from the getter.</summary>
    public static readonly int GobScaleBase = Field<float>("Gob", "scaleBase");
    /// <summary><c>LerpFloat</c>: the interpolation MODE and the value a settled one holds.
    /// <c>LerpFloat::Lerped</c> (@0x5f8fc0 / rva 0x2e9a80) tests the mode and, when it is 0, returns
    /// <see cref="LerpValue"/> unchanged -- so a zero mode is the one cheap proof that this number cannot move
    /// on its own. Identical on both builds.</summary>
    public static readonly int LerpMode = Field<int>("LerpFloat", "mode");
    /// <inheritdoc cref="LerpMode"/>
    public static readonly int LerpValue = Field<float>("LerpFloat", "value");
    /// <summary>The gob's bounding sphere, in the form the engine's own BSP visitor @0x481000 uses --
    /// radius at +0x178, LOCAL centre at +0x17c. That visitor computes the LOD distance as
    /// <c>|camPos - (gob.position + gob.rot * centre)|^2</c>, and <c>Scene::AddPartsToDrawBuckets</c> feeds the
    /// same pair to <c>SphereAbovePlane</c>. Alloy's visible-gob list uses it the same way.</summary>
    public static readonly int GobRadius = Field<float>("Gob", "radius");
    /// <inheritdoc cref="GobRadius"/>
    public static readonly int GobCentre = Field<System.Numerics.Vector3>("Gob", "centre");
    /// <summary>The gob loop's <c>SynchronizeHierarchies(gob+0x90, parent+0x90)</c> gate. An attached
    /// gob with this set has its part tree's locals OVERWRITTEN from its parent's every frame, by
    /// <c>Scene::AddPartsToDrawBuckets</c>. The owned composition reads the locals AFTER that write, so the
    /// field is a "this gob's locals are the parent's" note rather than a term of the matrix.</summary>
    public static readonly int GobSyncHier = Field<byte>("Gob", "syncHier");
    /// <summary><c>Gob::ProcessGob</c>'s dirty byte. <c>Gob::SetLODModel</c> @0x43e6a0 sets it on every
    /// LOD swap and <c>ProcessGob</c> @0x43e920 clears it after refreshing <see cref="GobVisible"/> and the
    /// tree's geometry flags -- so it is the engine's own "this gob's active tree changed" note.</summary>
    public static readonly int GobGeomDirty = Field<byte>("Gob", "geomDirty");
    /// <summary><c>Gob::SetLODModel</c>'s four inputs and its output level: the squared camera distance
    /// (written by the BSP visitor @0x481000, also the gobbucket sort key), the current level 0/1/2, and the
    /// two switch distances, each scaled by <c>g_fLODScaleFactor</c> and compared SQUARED. A negative distance
    /// stands the whole function down.</summary>
    public static readonly int GobLodDist2 = Field<float>("Gob", "lodDist2");
    /// <inheritdoc cref="GobLodDist2"/>
    public static readonly int GobLodLevel = Field<int>("Gob", "lodLevel");
    /// <inheritdoc cref="GobLodDist2"/>
    public static readonly int GobLodNear = Field<float>("Gob", "lodNear");
    /// <inheritdoc cref="GobLodDist2"/>
    public static readonly int GobLodFar = Field<float>("Gob", "lodFar");

    // ================= plumbing =================

    private readonly record struct Row(string Class, string Field, int Value, Expect Expect, bool Missing);

    private static string? _key;

    /// <summary>Declare a row: its value for this OS is the running build's (<see cref="ClientBuilds"/>).
    /// No value reads as 0 and counts as missing, which <see cref="Require"/> refuses.</summary>
    /// <param name="sig">A <c>vtbl.*</c> row's signature, <c>"R(T name, ...)"</c> without <c>this</c>: for
    /// <c>tools/engine/views.py</c>, which emits the typed virtual call.</param>

    // ---- CX-120 seam-obj: the Aurora seam shell. These are the offsets at which the SEAM places the
    // fields the game layer reads without a vtable, and they are the engine's own, per build and per OS
    // -- a shell whose words sat anywhere else would be a facade the game writes past the end of
    // (CX-182, managed/Alloy/Seam/README.md). ----
    /// <summary>SafePointerList capacity.</summary>
    public static readonly int SeamGobSafeCap = Field<int>("Gob", "seamSafeCap");
    /// <summary>EnableForceAnimations / IsForceAnimated.</summary>
    public static readonly int SeamGobForceAnim = Field<byte>("Gob", "seamForceAnim");
    /// <summary>EnableAnimations / IsAnimating.</summary>
    public static readonly int SeamGobAnimEnabled = Field<byte>("Gob", "seamAnimEnabled");
    /// <summary>inline char[0x40]; Gob::GetName returns its address.</summary>
    public static readonly int SeamGobName = Field<byte>("Gob", "seamName");
    /// <summary>TurnOnShadows / TurnOffShadows, a 16-bit store.</summary>
    public static readonly int SeamGobShadows = Field<short>("Gob", "seamShadows");
    /// <summary>SetRenderPersonalSpace.</summary>
    public static readonly int SeamGobPersonalSpace = Field<byte>("Gob", "seamPersonalSpace");
    /// <summary>callback capacity.</summary>
    public static readonly int SeamGobCallbackCap = Field<int>("Gob", "seamCallbackCap");
    /// <summary>ScaleObject's two channels, 0x4a0 and 0x4a4.</summary>
    public static readonly int SeamGobScaleChannel = Field<float>("Gob", "seamScaleChannel");
    /// <summary>CExoArrayList<Gob*> of attachments.</summary>
    public static readonly int SeamGobAttachList = Field<IntPtr>("Gob", "seamAttachList");
    /// <summary>attachment count.</summary>
    public static readonly int SeamGobAttachCount = Field<int>("Gob", "seamAttachCount");
    /// <summary>attachment capacity.</summary>
    public static readonly int SeamGobAttachCap = Field<int>("Gob", "seamAttachCap");
    /// <summary>GetRandomPart's cached total, -1 = stale.</summary>
    public static readonly int SeamGobTriMeshArea = Field<float>("Gob", "seamTriMeshArea");
    /// <summary>NewCAurObject's operator new size.</summary>
    public static readonly int SeamGobSizeof = Field<int>("Gob", "seamSizeof");
    /// <summary>SafePointerList array pointer.</summary>
    public static readonly int SeamSceneSafeList = Field<IntPtr>("Scene", "seamSafeList");
    /// <summary>SafePointerList count.</summary>
    public static readonly int SeamSceneSafeCount = Field<int>("Scene", "seamSafeCount");
    /// <summary>SafePointerList capacity.</summary>
    public static readonly int SeamSceneSafeCap = Field<int>("Scene", "seamSafeCap");
    /// <summary>CAurScene::SetAmbient.</summary>
    public static readonly int SeamSceneAmbient = Field<System.Numerics.Vector3>("Scene", "seamAmbient");
    /// <summary>the second vector SetAmbient writes.</summary>
    public static readonly int SeamSceneAmbient2 = Field<System.Numerics.Vector3>("Scene", "seamAmbient2");
    /// <summary>NewCAurScene's operator new size.</summary>
    public static readonly int SeamSceneSizeof = Field<int>("Scene", "seamSizeof");
    /// <summary>The <c>CAurString</c> sub-object's vptr inside a <c>StringGob</c>, 0x4c0 on BOTH images
    /// (<c>RemoveAuroraString</c>'s <c>lea -0x4c0(%rcx)</c> on Windows, the argument minus 0x4c0 on Linux). It
    /// is NOT the exported <c>StringGob</c> readers' this-adjust, which is 0 on Windows: wiring the shell to that
    /// one put the CAurString vtable over the primary vptr at +0.</summary>
    public static readonly int SeamStringGobStringSub = Field<IntPtr>("StringGob", "seamStringSub");
    /// <summary>NewCAurString's operator new size.</summary>
    public static readonly int SeamStringGobSizeof = Field<int>("StringGob", "seamSizeof");
    /// <summary>the CAurObject (Gob) sub-object.</summary>
    public static readonly int SeamCameraGobSub = Field<byte>("Camera", "seamGobSub");
    /// <summary>Camera::Camera's operator new size -- the ONE class whose size differs per OS.</summary>
    public static readonly int SeamCameraSizeof = Field<int>("Camera", "seamSizeof");

    /// <summary>the callback function pointer.</summary>
    public static readonly int SeamCallBackFn = Field<IntPtr>("CallBack", "seamFn");
    /// <summary>the callback's registered user data.</summary>
    public static readonly int SeamCallBackData = Field<IntPtr>("CallBack", "seamData");
    /// <summary>Gob::RegisterCallBack's operator new size.</summary>
    public static readonly int SeamCallBackSizeof = Field<int>("CallBack", "seamSizeof");
    /// <summary>one animated float of a CAurObjectVisualTransformData.</summary>
    public static readonly int SeamLerpFloatSizeof = Field<int>("LerpFloat", "seamSizeof");
    /// <summary>Itanium slot 1; MSVC's single scalar-deleting destructor is slot 0.</summary>
    public static readonly int SeamCAurBehaviorVtblDtorDeleting = Field("CAurBehavior", "vtbl.dtorDeleting", Expect.VtblSlot);
    /// <summary>CAurBehavior::Message(char const*).</summary>
    public static readonly int SeamCAurBehaviorVtblMessage = Field("CAurBehavior", "vtbl.Message", Expect.VtblSlot);
    /// <summary>CAurBehavior::GetType() -> int.</summary>
    public static readonly int SeamCAurBehaviorVtblGetType = Field("CAurBehavior", "vtbl.GetType", Expect.VtblSlot);

    private static int Field(string cls, string field, Expect probe = Expect.None, string? sig = null)
    {
        (int? linux, int? win) = ClientBuilds.Offset(cls, field);
        int? mine = OperatingSystem.IsWindows() ? win : linux;
        if (win == null) UnresolvedCount++;
        if (mine == null) MissingCount++;
        _rows.Add(new Row(cls, field, mine ?? 0, probe, mine == null));
        return mine ?? 0;
    }

    /// <summary>A row whose field holds a <typeparamref name="T"/> -- an engine view (<c>CX.Engine</c>) for
    /// a pointer to a class, else the primitive. The type is for <c>tools/engine/views.py</c>.</summary>
    /// <param name="count">Non-zero: an embedded array of that many, so the view gets a <c>Span</c>.</param>
    /// <param name="embedded">The field IS a <typeparamref name="T"/> record, not a pointer to one.</param>
    private static int Field<T>(string cls, string field, Expect probe = Expect.None, int count = 0, bool embedded = false) where T : allows ref struct
        => Field(cls, field, probe == Expect.Byte01 && typeof(T) == typeof(byte) ? Expect.Bool8 : probe);

    /// <summary>Every declared (class, field), for <see cref="ClientBuilds"/>' completeness check.</summary>
    internal static IEnumerable<(string Class, string Field)> Declared
    {
        get
        {
            foreach (Row r in _rows) yield return (r.Class, r.Field);
        }
    }

    /// <summary>The resolved value of row <c>cls.field</c>, or null when no such row is declared. For
    /// <see cref="ClientBuilds"/>' check that an override names a real row and actually landed.</summary>
    internal static int? RowValue(string cls, string field)
    {
        foreach (Row r in _rows)
            if (r.Class == cls && r.Field == field) return r.Value;
        return null;
    }

    /// <summary>Missing rows belonging to these engine classes. The gate is per feature, so the counter it
    /// reads has to be too: a renderer class nobody has measured must not stand the nameplates down.</summary>
    private static int MissingIn(string[] classes)
    {
        int n = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Missing && Array.IndexOf(classes, _rows[i].Class) >= 0) n++;
        return n;
    }

    // ---- build key readers ----

    private static ulong _imageLo, _imageHi;

    /// <summary>PE CodeView GUID + age, read from the loaded image: e_lfanew -> DataDirectory[6] ->
    /// IMAGE_DEBUG_DIRECTORY entries -> the type-2 (CODEVIEW) one -> "RSDS" + GUID + age.</summary>
    private static string WindowsKey()
    {
        byte* b = (byte*)Base;
        int lfanew = *(int*)(b + 0x3c);
        byte* opt = b + lfanew + 24;
        _imageLo = (ulong)Base;
        _imageHi = _imageLo + *(uint*)(opt + 56);            // SizeOfImage (PE32+)
        uint dbgRva = *(uint*)(opt + 112 + 6 * 8), dbgSize = *(uint*)(opt + 112 + 6 * 8 + 4);
        for (uint i = 0; i + 28 <= dbgSize; i += 28)
        {
            byte* e = b + dbgRva + i;
            if (*(uint*)(e + 12) != 2) continue;             // IMAGE_DEBUG_TYPE_CODEVIEW
            byte* cv = b + *(uint*)(e + 20);                 // AddressOfRawData
            if (cv[0] != 'R' || cv[1] != 'S' || cv[2] != 'D' || cv[3] != 'S') continue;
            var sb = new StringBuilder(33);
            sb.Append((*(uint*)(cv + 4)).ToString("X8"))
              .Append((*(ushort*)(cv + 8)).ToString("X4"))
              .Append((*(ushort*)(cv + 10)).ToString("X4"));
            for (int k = 12; k < 20; k++) sb.Append(cv[k].ToString("X2"));
            sb.Append(*(uint*)(cv + 20));                    // age
            return sb.ToString();
        }
        return "no-codeview";
    }

    /// <summary>ELF NT_GNU_BUILD_ID of the running program. Read from <c>/proc/self/exe</c> -- under
    /// LD_PRELOAD that is the client, and <see cref="Base"/> is a dlopen handle rather than a load address,
    /// so the in-memory shortcut the Windows reader takes is not available here.</summary>
    private static string LinuxKey()
    {
        try
        {
            ImageRangeFromMaps();
            using var fs = File.OpenRead("/proc/self/exe");
            var hdr = new byte[64];
            if (fs.Read(hdr, 0, 64) != 64) return "no-elf-header";
            long phoff = BitConverter.ToInt64(hdr, 0x20);
            int phentsize = BitConverter.ToUInt16(hdr, 0x36), phnum = BitConverter.ToUInt16(hdr, 0x38);
            var ph = new byte[phentsize];
            for (int i = 0; i < phnum; i++)
            {
                fs.Position = phoff + (long)i * phentsize;
                if (fs.Read(ph, 0, phentsize) != phentsize) break;
                if (BitConverter.ToUInt32(ph, 0) != 4) continue;                 // PT_NOTE
                long off = BitConverter.ToInt64(ph, 0x08), size = BitConverter.ToInt64(ph, 0x20);
                var notes = new byte[size];
                fs.Position = off;
                if (fs.Read(notes, 0, notes.Length) != notes.Length) continue;
                for (int p = 0; p + 12 <= notes.Length; )
                {
                    int namesz = BitConverter.ToInt32(notes, p);
                    int descsz = BitConverter.ToInt32(notes, p + 4);
                    int type = BitConverter.ToInt32(notes, p + 8);
                    int name = p + 12, desc = name + ((namesz + 3) & ~3);
                    if (type == 3 && namesz == 4 && notes[name] == 'G' && notes[name + 1] == 'N')
                        return Convert.ToHexString(notes, desc, descsz).ToLowerInvariant();
                    p = desc + ((descsz + 3) & ~3);
                }
            }
            return "no-build-id";
        }
        catch (Exception e) { return "unreadable:" + e.GetType().Name; }
    }

    /// <summary>Image extent of the main program, for the vtable-slot probe. From <c>/proc/self/maps</c>:
    /// the lines whose path is the exe, plus the nameless mapping that directly follows them -- the RW segment's
    /// <c>.bss</c> tail past its file size (nuklear's <c>nk_sdl.ogl.cmds</c> lives there), which the kernel maps
    /// anonymously.</summary>
    private static void ImageRangeFromMaps()
    {
        string exe = File.ResolveLinkTarget("/proc/self/exe", true)?.FullName ?? "";
        if (exe.Length == 0) return;
        foreach (string line in File.ReadLines("/proc/self/maps"))
        {
            bool mine = line.EndsWith(exe, StringComparison.Ordinal);
            if (!mine && (_imageHi == 0 || !IsAnonymous(line))) continue;
            int dash = line.IndexOf('-'), sp = line.IndexOf(' ');
            if (dash < 0 || sp < dash) continue;
            ulong lo = Convert.ToUInt64(line[..dash], 16), hi = Convert.ToUInt64(line[(dash + 1)..sp], 16);
            if (!mine) { if (lo == _imageHi) _imageHi = hi; continue; }
            if (_imageLo == 0 || lo < _imageLo) _imageLo = lo;
            if (hi > _imageHi) _imageHi = hi;
        }
    }

    /// <summary>A <c>/proc/self/maps</c> line with no path: address, perms, offset, device, inode, nothing else.</summary>
    internal static bool IsAnonymous(string line)
        => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 5;

    // ---- CX-109/110 ----

    /// <summary>Camera -&gt; the <c>Scene*</c> the camera renders. <c>Camera::RenderScene</c> (@0x4a7920 /
    /// rva 0x5a5d0) returns at once when it is null and dispatches <c>Scene::Render</c> on it;
    /// <c>Camera::AttachToScene</c> is its writer. Same offset on both builds, below Camera's measured
    /// <c>+0x4</c> segment (<see cref="CamViewX"/>).</summary>
    public static readonly int CameraScene = Field<CX.Engine.Scene>("Camera", "scene");

    /// <summary>The whole object's size, so a CX scene shell can own one. <c>Scene::Scene</c> allocates it
    /// with <c>operator new(0x870)</c> on BOTH OSes -- read off the Linux decompile @0x4724f0 and the Windows
    /// <c>??0Scene@@QEAA@PEBD@Z</c> (<c>mov $0x870,%ecx</c> immediately before the ctor call), not assumed
    /// equal.</summary>
    public static readonly int WindManagerSize = Field<int>("WindManager", "size");
    /// <summary>WindManager -&gt; the base wind <c>Vector</c>. Zero in normal play: the default ctor zeroes
    /// it and its only writer is the <c>setglobalwind</c> console command. Read each tick anyway, so a console
    /// or script-set wind still blows (<see cref="CX.Alloy.AlloyWind"/>). Every WindManager row here is
    /// identical on Windows, read off <c>updateTimer</c>, <c>GetGlobalWind</c> and <c>pointSourceWindVector</c>.</summary>
    public static readonly int WindBase = Field<System.Numerics.Vector3>("WindManager", "baseWind");
    /// <summary>WindManager -&gt; the maximum yaw deviation, in the units
    /// <c>WindManager::GetGlobalWind</c> @0x490ab0 hands <c>sincosf</c> (so the quaternion turns by twice it).
    /// The default ctor writes 130.0; <c>WindManager::SetMaximumDeviation</c> @0x4904d0 is the other writer.
    /// <inheritdoc cref="WindBase"/></summary>
    public static readonly int WindMaxYaw = Field<float>("WindManager", "maxYaw");
    /// <summary>WindManager -&gt; the maximum PITCH deviation, the second of
    /// <c>SetMaximumDeviation</c>'s pair. Default 0, i.e. vanilla wind does not tilt.
    /// <inheritdoc cref="WindBase"/></summary>
    public static readonly int WindMaxPitch = Field<float>("WindManager", "maxPitch");
    /// <summary>WindManager -&gt; how long one noise field lasts, in seconds
    /// (<c>WindManager::SetUpdateTimeoutValue</c> @0x490450; default 0.5). The accumulator at <c>+0x854</c>
    /// and the blend at <c>+0x850</c> are the ENGINE's clock and Alloy keeps its own, so neither has a row.
    /// <inheritdoc cref="WindBase"/></summary>
    public static readonly int WindPeriod = Field<float>("WindManager", "period");

    // ---- CX-113: the engine's model/animation tables, read ONLY by the live verify (CX.Alloy.ModelVerify).
    //      Measured on both builds: Linux from the decompilation of FindAnimation @0x497c30 and FindAnimScale
    //      @0x499b10, Windows by disassembling their own exports (?FindAnimation@@YAPEAVAnimation@@PEAVModel@@PEBD@Z
    //      rva 0x50f80, ?FindAnimScale@@YAMPEAVModel@@PEAVAnimation@@@Z rva 0x50ed0) -- the same +0x98 / +0xa0 /
    //      +0xa8 / +0xcc loads appear in both, so every value below is identical on the two platforms. The name
    //      Model's own name has no row of its own: it is MaxTree's field, which Model and Animation both derive
    //      from (MaxTree::MaxTree @0x43a5b0 strncpy()s it to this+0x10 with a 0x3f cap, i.e. char[64]), so the
    //      existing AnimationName row reads both. ----

    /// <summary>Model -&gt; its own <c>Animation*</c> array, and how many entries it has. Grown by
    /// <c>newanim</c> @0x447fe0 and walked by <c>FindAnimation</c>; the supermodel's are NOT in it, which is
    /// why the lookup is a chain walk.</summary>
    public static readonly int MdlcModelAnims = Field<IntPtr>("Model", "anims");
    /// <inheritdoc cref="MdlcModelAnims"/>
    public static readonly int MdlcModelAnimCount = Field<int>("Model", "animCount");
    /// <summary>Model -&gt; its supermodel, the one reference <c>setsupermodel</c> @0x44cd20 takes and
    /// <c>Model::InternalDestructor</c> @0x443de0 drops.</summary>
    public static readonly int MdlcModelSuper = Field<CX.Engine.Model>("Model", "super");
    /// <summary>Model -&gt; <c>setanimationscale</c> @0x446210's value, 1.0 unless the file sets it.
    /// <c>FindAnimScale</c> multiplies it in for every model BELOW the one owning the animation.</summary>
    public static readonly int MdlcModelAnimScale = Field<float>("Model", "animScale");
    /// <summary>
    /// The one runnable check. Proves the three forms register and count, that the build key reads,
    /// and -- the part that matters -- that a deliberately wrong offset makes a probe FAIL rather than pass
    /// quietly. Off-device: the probe runs against a hand-built fake object, not the client.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        int before = _rows.Count, unresolvedBefore = UnresolvedCount, missingBefore = MissingCount;
        // A row no build file gives a value: it must read 0, count as missing, and gate its class.
        int gap = Field("SelfCheck", "gap");
        if (gap != 0 || _rows.Count != before + 1) { log.Always("layout selfcheck FAIL: a gap did not read 0"); return; }
        if (MissingCount != missingBefore + 1 || MissingIn(["SelfCheck"]) != 1 || MissingIn(["Nope"]) != 0)
        { log.Always("layout selfcheck FAIL: missing counter"); return; }

        // A probe that cannot fail is not a probe: +0x40 holds 2, which no boolean does.
        long* fake = stackalloc long[16];
        for (int i = 0; i < 16; i++) fake[i] = 0;
        *(int*)((byte*)fake + 0x38) = 1;
        *(int*)((byte*)fake + 0x40) = 2;
        _rows.Add(new Row("SelfCheckProbe", "good", 0x38, Expect.Byte01, false));
        if (!Probe("SelfCheckProbe", (IntPtr)fake, null)) { log.Always("layout selfcheck FAIL: good probe rejected"); return; }
        _rows.Add(new Row("SelfCheckProbe", "bad", 0x40, Expect.Byte01, false));
        if (Probe("SelfCheckProbe", (IntPtr)fake, null)) { log.Always("layout selfcheck FAIL: bad probe accepted"); return; }

        // The .bss tail is a nameless maps line; [heap] and the exe's own lines are not.
        if (!IsAnonymous("01a12000-01bbb000 rw-p 00000000 00:00 0 ")
            || IsAnonymous("5600a000-5600c000 rw-p 00000000 00:00 0                          [heap]")
            || IsAnonymous("00400000-01a37000 r-xp 00000000 08:02 1234 /games/nwmain-linux"))
        { log.Always("layout selfcheck FAIL: a maps line was misread -- nuklear's .bss buffers fall outside the image"); return; }

        _rows.RemoveRange(before, _rows.Count - before);
        UnresolvedCount = unresolvedBefore;
        MissingCount = missingBefore;
        log.Info($"layout selfcheck ok: {_rows.Count} offset(s), {UnresolvedCount} without a Windows value, " +
                 $"{MissingCount} missing here, build {BuildKey}");
    }
}
