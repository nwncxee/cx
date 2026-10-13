using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>A generation-checked handle into one <see cref="SceneDb"/> table (ADR 0028). Low 20 bits index, high
/// 12 bits generation; 0 is null. A handle to a dead record resolves to nothing, which is why no table keyed on
/// a handle needs a death notice to stay correct -- the recycled-address class of bug cannot be expressed.</summary>
public readonly record struct DbId(uint Value)
{
    public const int IndexBits = 20;
    public const uint IndexMask = (1u << IndexBits) - 1;
    public int Index => (int)(Value & IndexMask);
    public uint Gen => Value >> IndexBits;
    public bool IsNull => Value == 0;
    public static DbId Make(int index, uint gen) => new(((gen & 0xFFF) << IndexBits) | (uint)index);
}

/// <summary>What changed on a record since the renderer last looked (ADR 0028: change-driven build). A seam
/// setter ORs its bit in and appends the record to its table's dirty list once.</summary>
[Flags]
public enum Dirty : uint
{
    None = 0,
    Xform = 1 << 0,
    Material = 1 << 1,
    Visible = 1 << 2,
    Anim = 1 << 3,
    Tree = 1 << 4,
    Texture = 1 << 5,
    Text = 1 << 6,
    Created = 1 << 7,
    Destroyed = 1 << 8,
}

/// <summary>What an object record is, which decides which interface its facade carries.</summary>
public enum ObjKind : byte { Free, Model, Text, Light, Empty }

/// <summary>The boolean state a <c>Gob</c> kept as bits, as one word per object
/// (<see cref="SceneDb.ObjSeamFlags"/>). Owner: the seam-obj stream.</summary>
[Flags]
public enum SeamFlag : uint
{
    None = 0,
    /// <summary>`TurnOnShadows` / `TurnOffShadows`.</summary>
    Shadows = 1 << 0,
    /// <summary>`EnableFog` / `DisableFog`.</summary>
    Fog = 1 << 1,
    /// <summary>`SetWind(true, strength)`.</summary>
    Wind = 1 << 2,
    /// <summary>`SetAsCharacter` -- the engine's "this is a creature" marker.</summary>
    Character = 1 << 3,
    /// <summary>`SetRenderPersonalSpace`.</summary>
    PersonalSpace = 1 << 4,
    /// <summary>`SetPropagateHighlightEffects`.</summary>
    PropagateHighlight = 1 << 5,
    /// <summary>`EnableInViewVolume` -- the object asked to be view-volume tested.</summary>
    InViewTest = 1 << 6,
    /// <summary>The last test's answer, which `IsInViewVolume` reports.</summary>
    InView = 1 << 7,
    /// <summary>`SetOverrideTextureFilter`.</summary>
    TexFilterOverride = 1 << 8,
    /// <summary>The model has a skinned mesh (`ContainsSkinnedMeshes`).</summary>
    Skinned = 1 << 9,
    /// <summary>A colour shift is active (`SetColorShifting` with a non-zero amount).</summary>
    ColourShift = 1 << 10,
    /// <summary>Self-illumination was set (`SetIllumination`).</summary>
    Illuminated = 1 << 11,
    /// <summary>Animations enabled (`EnableAnimations` / `DisableAnimations`), set at creation.</summary>
    Animate = 1 << 12,
    /// <summary>Animations forced even off screen (`EnableForceAnimations`).</summary>
    ForceAnimate = 1 << 13,
    /// <summary>`SetSynchronizeAnimations`.</summary>
    SyncAnimate = 1 << 14,
    /// <summary>The object draws last in its scene (`GobRenderLast` / `Scene::RenderGobLast`).</summary>
    RenderLast = 1 << 15,
    /// <summary>The object has been attached to a valid scene at least once. Not state the game can see --
    /// it exists so a sceneless `GetScene` can say whether the object was never attached (a missing attach
    /// path) or was attached and detached (an ordering difference), which are different bugs.</summary>
    EverAttached = 1 << 16,
    /// <summary>This object is a PIECE of its attach parent's one part tree -- what `Gob::Append` grafted on,
    /// or a reference node's sub-model -- rather than a separate gob riding a node. The engine had one part
    /// range and so needed no such mark; CX makes each piece its own record, and the walks that have to see
    /// the composition as one tree read this.</summary>
    Composed = 1 << 17,
}

/// <summary>The generation/free-list core every <see cref="SceneDb"/> table shares. Index 0 is reserved so a
/// zero handle is null; a freed slot's generation is bumped at free, never at reuse, so a handle taken before
/// the free is dead the moment it happens.</summary>
public sealed class DbTable
{
    private uint[] _gen;
    private int[] _freeNext;
    private int _freeHead = -1, _high = 1;
    private Dirty[] _dirty;
    private int[] _dirtyList;
    private int _dirtyN;
    private long[] _markAt;
    private long _markClock;

    public DbTable(int capacity)
    {
        _gen = new uint[capacity];
        _freeNext = new int[capacity];
        _dirty = new Dirty[capacity];
        _dirtyList = new int[capacity];
        _markAt = new long[capacity];
    }

    public int Capacity => _gen.Length;
    /// <summary>One past the highest index ever handed out: a dense walk's bound.</summary>
    public int High => _high;
    public int Live { get; private set; }

    /// <summary>Allocate a slot, or null when the table is full (the caller logs and refuses; never a quiet drop).</summary>
    public DbId Alloc()
    {
        int i;
        if (_freeHead >= 0) { i = _freeHead; _freeHead = _freeNext[i]; }
        else if (_high < _gen.Length) i = _high++;
        else return default;
        _gen[i] |= 1u << 31;
        Live++;
        Mark(i, Dirty.Created);
        return DbId.Make(i, _gen[i] & 0xFFF);
    }

    /// <summary>Free a live slot: the generation moves on now, so every outstanding handle to it is dead.</summary>
    public void Free(DbId id)
    {
        if (!Valid(id)) return;
        int i = id.Index;
        _gen[i] = (_gen[i] + 1) & 0xFFF;
        if ((_gen[i] & 0xFFF) == 0) _gen[i] = 1;
        _freeNext[i] = _freeHead;
        _freeHead = i;
        Live--;
        Mark(i, Dirty.Destroyed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Valid(DbId id)
    {
        int i = id.Index;
        uint g = (uint)i < (uint)_gen.Length ? _gen[i] : 0;
        return i != 0 && (g & (1u << 31)) != 0 && (g & 0xFFF) == id.Gen;
    }

    // ---- contiguous ranges ----
    // A table whose records are allocated as a RANGE (the part table: SceneDb.PartParent is an absolute
    // index by contract) allocates them HERE, from the same _high cursor Alloc() uses. That is the whole
    // point: when the range allocator kept its own cursor, Alloc() handed out _high++ indices inside chunks
    // the range allocator would later hand out again, and the second claim failed -- "the part table already
    // holds 8884..8885" at the first GUI build. One table, one cursor, no overlap by construction.

    /// <summary>Size classes for a chunk: 1, 2, 4 ... 65536 slots. ponytail: a model with more nodes than
    /// that does not exist; a run past it is a refusal the caller logs, not a clamp.</summary>
    private const int RangeClasses = 17;
    private List<int>[]? _rangeFree;
    /// <summary>Chunk capacity by first index; 0 for an index that is not a chunk head.</summary>
    private int[]? _rangeCap;
    private int _rangeHeld;

    /// <summary>Allocate <paramref name="count"/> CONTIGUOUS live slots and hand back the first index. The
    /// chunk is rounded up to a power of two (at most 2x waste) and comes from a size-class free list or from
    /// this table's own cursor, so it can never overlap a slot <see cref="Alloc"/> handed out.
    /// <para>False = the table is full or the run is past the biggest class; the caller logs and refuses.</para></summary>
    public bool AllocRange(int count, out int first)
    {
        first = 0;
        if (count <= 0) return false;
        _rangeFree ??= NewRangeBins();
        _rangeCap ??= new int[_gen.Length];
        int c = ClassOf(count);
        if (c >= RangeClasses) return false;
        int cap = 1 << c;
        List<int> bin = _rangeFree[c];
        if (bin.Count > 0)
        {
            first = bin[^1];
            bin.RemoveAt(bin.Count - 1);
        }
        else
        {
            if (_high + cap > _gen.Length) return false;
            first = _high;
            _high += cap;
            _rangeCap[first] = cap;
        }
        for (int i = first; i < first + count; i++)
        {
            _gen[i] |= 1u << 31;
            if ((_gen[i] & 0xFFF) == 0) _gen[i] |= 1;
            Live++;
            Mark(i, Dirty.Created);
        }
        _rangeHeld += cap;
        return true;
    }

    /// <summary>Release a run from <see cref="AllocRange"/>: every slot's generation moves on NOW, so a
    /// handle taken before this is dead, each is marked destroyed for the consumers' dirty walk, and the
    /// chunk goes back to its size class.</summary>
    public void FreeRange(int first, int count)
    {
        if (_rangeCap == null || first <= 0 || first >= _rangeCap.Length) return;
        int cap = _rangeCap[first];
        if (cap <= 0) return;
        for (int i = first; i < first + count && i < _gen.Length; i++)
        {
            if ((_gen[i] & (1u << 31)) == 0) continue;
            Mark(i, Dirty.Destroyed);
            _gen[i] = (_gen[i] + 1) & 0xFFF;
            if ((_gen[i] & 0xFFF) == 0) _gen[i] = 1;
            Live--;
        }
        _rangeHeld -= cap;
        // a chunk at the very top goes straight back to the cursor, so a build/free cycle at one size never
        // walks it up
        if (first + cap == _high) { _high = first; _rangeCap[first] = 0; return; }
        _rangeFree![ClassOf(cap)].Add(first);
    }

    /// <summary>Give every free chunk that touches the top back to the cursor, repeatedly. The area edge;
    /// it moves nothing.</summary>
    public void TrimRanges()
    {
        if (_rangeFree == null || _rangeCap == null) return;
        bool again = true;
        while (again)
        {
            again = false;
            for (int c = 0; c < RangeClasses; c++)
            {
                List<int> bin = _rangeFree[c];
                for (int k = bin.Count - 1; k >= 0; k--)
                    if (bin[k] + (1 << c) == _high)
                    {
                        _high = bin[k];
                        _rangeCap[bin[k]] = 0;
                        bin.RemoveAt(k);
                        again = true;
                    }
            }
        }
    }

    /// <summary>Slots held by live chunks, including each chunk's rounding waste.</summary>
    public int RangeHeld => _rangeHeld;

    private static List<int>[] NewRangeBins()
    {
        var a = new List<int>[RangeClasses];
        for (int i = 0; i < RangeClasses; i++) a[i] = new List<int>();
        return a;
    }

    /// <summary>Smallest power of two at or above <paramref name="n"/>, as its class index.</summary>
    private static int ClassOf(int n)
    {
        int c = 0;
        while (c < RangeClasses && (1 << c) < n) c++;
        return c;
    }

    /// <summary>True for an allocated slot (a dense walk's test).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LiveAt(int i) => (_gen[i] & (1u << 31)) != 0;

    public DbId IdAt(int i) => DbId.Make(i, _gen[i] & 0xFFF);

    /// <summary>OR <paramref name="d"/> into slot <paramref name="i"/>'s dirty set; the first bit since the last
    /// <see cref="TakeDirty"/> appends it to the dirty list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Mark(int i, Dirty d)
    {
        if (_dirty[i] == Dirty.None) _dirtyList[_dirtyN++] = i;
        _dirty[i] |= d;
        _markAt[i] = ++_markClock;
    }

    /// <summary>A monotonic stamp per MARK, so "was this slot marked since I last looked at it" is one compare:
    /// the dirty BITS do not change when an already-set bit is re-OR'd and the list does not grow, so neither
    /// answers it. The clock is the TABLE's, so one reader stamp is valid against every slot. GAME THREAD ONLY
    /// (ADR 0028 section 5): the increment is a plain <c>++</c>, and every reader runs on that thread too.</summary>
    public long MarkClock => _markClock;
    /// <inheritdoc cref="MarkClock"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long MarkAt(int i) => _markAt[i];

    public Dirty DirtyAt(int i) => _dirty[i];
    public int DirtyCount => _dirtyN;
    public int DirtyIndex(int k) => _dirtyList[k];
    /// <summary>How many times <see cref="TakeDirty"/> has run: a reader's cursor into the list is only good while
    /// this is unchanged, because the list can refill past its old length between two reads.</summary>
    public int DirtyTakes => _takes;
    /// <summary>The list's position as one value (takes, length): equal means nothing was marked since.</summary>
    public long DirtyMark => ((long)_takes << 32) | (uint)_dirtyN;
    private int _takes;

    /// <summary>Close the frame's dirty set: the consumer has visited <see cref="DirtyIndex"/>(0..<see cref="DirtyCount"/>).</summary>
    public void TakeDirty()
    {
        for (int k = 0; k < _dirtyN; k++) _dirty[_dirtyList[k]] = Dirty.None;
        _dirtyN = 0;
        _takes++;
    }
}

/// <summary>ADR 0028: <b>CX's scene database</b> -- struct-of-arrays tables indexed by <see cref="DbId"/>,
/// driven by the game layer through the Aurora seam and read by the renderer, in place of the engine's
/// <c>Scene</c>/<c>Gob</c>/<c>Part</c> objects. The GAME thread is the only writer and the renderer reads a
/// per-frame <c>RenderSnapshot</c> of the dirty lists, so neither waits. A stream adds columns here, appended.</summary>
public static class SceneDb
{
    /// <summary>Records held at once. Objects: every creature, placeable, item, door, VFX, GUI control and text
    /// across every live scene (the heaviest measured area ~3k gobs, GUI ~2k). Parts: the instance trees.</summary>
    public const int MaxScenes = 256, MaxObjects = 1 << 16, MaxParts = 1 << 19, MaxCameras = 512;

    public static readonly DbTable Scenes = new(MaxScenes);
    public static readonly DbTable Objects = new(MaxObjects);
    public static readonly DbTable Parts = new(MaxParts);
    public static readonly DbTable Cameras = new(MaxCameras);

    // ---- objects (the CAurObject records) ----
    public static readonly ObjKind[] ObjKindOf = new ObjKind[MaxObjects];
    /// <summary>The model the object was created from (null for an empty/text object); its LOD siblings in <see cref="ObjLod"/>.</summary>
    public static readonly ModelDef?[] ObjModel = new ModelDef?[MaxObjects];
    public static readonly DbId[] ObjScene = new DbId[MaxObjects];
    /// <summary>The last scene the object joined: what <c>CAurBehaviorCrossSceneAttach::SetScene(old,new)</c>
    /// @0x68c250 keeps at +0x70, so a control the game moved into a viewport scene goes back there.</summary>
    public static readonly DbId[] ObjCrossScene = new DbId[MaxObjects];
    /// <summary>The kinds of scene the object has joined in its life (<c>SeamShell.InGui</c> | <c>InWorld</c>); its
    /// part shells are reused at death only when this is world alone.</summary>
    public static readonly byte[] ObjSceneKinds = new byte[MaxObjects];
    /// <summary>The game call chain that first put the object in a world scene (null: never, or a tile).</summary>
    public static readonly string?[] ObjWorldJoinBy = new string?[MaxObjects];
    /// <summary>The object's part range in <see cref="Parts"/>: first part index and count (pre-order, [0] the root).</summary>
    public static readonly int[] ObjPartFirst = new int[MaxObjects], ObjPartCount = new int[MaxObjects];
    /// <summary>Attachment: the object this one rides on and the part (index into <see cref="Parts"/>) it hangs from.</summary>
    public static readonly DbId[] ObjAttachTo = new DbId[MaxObjects];
    public static readonly int[] ObjAttachPart = new int[MaxObjects];
    /// <summary>The NODE NAME of the part <see cref="ObjAttachPart"/> names, so a ride survives its parent's
    /// part range being rebuilt. The index is absolute into one shared table: once the parent's range is
    /// freed and allocated again, that index is either another object's part -- the ride then stops, and the
    /// rider keeps the world matrix it last had -- or the WRONG node of the same creature.</summary>
    public static readonly string?[] ObjAttachNode = new string?[MaxObjects];

    /// <summary>Every rider of an object, as slot links (index + 1, 0 = none): a seam attach
    /// (<see cref="ObjAttachTo"/>) or an attach BEHAVIOUR the game built itself. The shell's own attach list
    /// (+0x4a8) is NOT that set -- <c>Gob::AddAttachment</c> @0x5424a0 keeps 17 and overwrites past that. A
    /// reader keeps a rider only while <see cref="RiderParent"/> is the parent's live handle.</summary>
    private static readonly int[] _kidHead = new int[MaxObjects], _kidTail = new int[MaxObjects],
                                  _kidNext = new int[MaxObjects], _kidPrev = new int[MaxObjects],
                                  _kidOf = new int[MaxObjects];
    private static readonly DbId[] _kidParent = new DbId[MaxObjects];

    /// <summary>The one writer of <see cref="ObjAttachTo"/>; files the rider link with it.</summary>
    public static void SetAttachTo(int child, DbId parent)
    {
        ObjAttachTo[child] = parent;
        LinkRider(child, parent);
    }

    /// <summary>Re-file <paramref name="child"/> under <paramref name="parent"/>'s rider list (appended, so
    /// attach order is kept); null unlinks.</summary>
    public static void LinkRider(int child, DbId parent)
    {
        _kidParent[child] = parent;
        int was = _kidOf[child] - 1, now = parent.IsNull ? -1 : parent.Index;
        if (was == now) return;
        if (was >= 0)
        {
            int p = _kidPrev[child], n = _kidNext[child];
            if (p != 0) _kidNext[p - 1] = n; else _kidHead[was] = n;
            if (n != 0) _kidPrev[n - 1] = p; else _kidTail[was] = p;
        }
        _kidPrev[child] = _kidNext[child] = 0;
        _kidOf[child] = now + 1;
        if (now < 0) return;
        _kidPrev[child] = _kidTail[now];
        if (_kidTail[now] != 0) _kidNext[_kidTail[now] - 1] = child + 1; else _kidHead[now] = child + 1;
        _kidTail[now] = child + 1;
    }

    /// <summary>The parent a rider is filed under (null = none).</summary>
    public static DbId RiderParent(int child) => _kidParent[child];

    /// <summary>The first rider link of <paramref name="parent"/> (slot + 1, 0 = none); walk with
    /// <see cref="NextRider"/> and keep a slot only while <see cref="RiderParent"/> names the parent.</summary>
    public static int FirstRider(int parent) => _kidHead[parent];

    /// <inheritdoc cref="FirstRider"/>
    public static int NextRider(int child) => _kidNext[child];

    /// <summary>Attach <paramref name="child"/> to parent part <paramref name="part"/>, remembering the
    /// node's name with it. The one writer of <see cref="ObjAttachPart"/> that is not a detach.</summary>
    public static void SetAttachPart(int child, int part)
    {
        ObjAttachPart[child] = part;
        ObjAttachNode[child] = NodeNameOf(part);
    }

    /// <summary>The name of the node <paramref name="part"/> instances, or null.</summary>
    public static string? NodeNameOf(int part)
    {
        if ((uint)part >= MaxParts || !Parts.LiveAt(part)) return null;
        CX.Alloy.Mdl.ModelDef? m = PartModel[part];
        int node = PartNode[part];
        return m != null && (uint)node < (uint)m.Nodes.Length ? m.Nodes[node].Name : null;
    }
    /// <summary>Which <c>CAurBehaviorAttach*</c> subclass the game's attach asked for -- the <c>mode</c>
    /// argument of <c>Gob::AttachToObject</c> @0x43e0e0 / <c>Gob::AttachToPart</c> @0x43e2a0, whose switch is
    /// 0 plain, 1 translate, 2 rotate, 3 no-orientation, 4 GUI-scale. CX constructs no behaviour object, so
    /// the variant lives here; <c>StringGob::SetPositionOffset</c> is the first reader.</summary>
    public static readonly byte[] ObjAttachMode = new byte[MaxObjects];
    /// <summary><see cref="ObjAttachMode"/>'s translate variant, the one a GUI label is attached with.</summary>
    public const byte AttachTranslate = 1;
    /// <summary><see cref="ObjAttachMode"/> for an attach the engine installed no behaviour for (its switch's
    /// default arm).</summary>
    public const byte AttachNone = 255;
    public static readonly Vector3[] ObjPos = new Vector3[MaxObjects];
    public static readonly Quaternion[] ObjRot = new Quaternion[MaxObjects];
    public static readonly float[] ObjScale = new float[MaxObjects];
    /// <summary>Composed object-to-world, written by the transform step from the columns above.</summary>
    public static readonly Matrix4x4[] ObjWorld = new Matrix4x4[MaxObjects];
    /// <summary>Gob colour folds: diffuse multiplier (rgb) + alpha (w), emissive add.</summary>
    public static readonly Vector4[] ObjTint = new Vector4[MaxObjects];
    public static readonly Vector3[] ObjEmissive = new Vector3[MaxObjects];
    public static readonly bool[] ObjVisible = new bool[MaxObjects];
    /// <summary>The native block the game layer holds for this object (its <c>CAurObject*</c>), 0 until the seam mints one.</summary>
    public static readonly nint[] ObjFacade = new nint[MaxObjects];

    // ---- parts (instance nodes) ----
    public static readonly DbId[] PartObj = new DbId[MaxParts];
    public static readonly ModelDef?[] PartModel = new ModelDef?[MaxParts];
    /// <summary>The node this part instances, as an index into <see cref="PartModel"/>'s nodes.</summary>
    public static readonly int[] PartNode = new int[MaxParts];
    /// <summary>Parent part index (absolute, into <see cref="Parts"/>), -1 for an object root.</summary>
    public static readonly int[] PartParent = new int[MaxParts];
    /// <summary>Animated local pose (parent-relative); the rest pose until an animation writes it.</summary>
    public static readonly Vector3[] PartPos = new Vector3[MaxParts];
    public static readonly Quaternion[] PartRot = new Quaternion[MaxParts];
    public static readonly float[] PartScale = new float[MaxParts];
    /// <summary>Part-to-world, written by the transform step.</summary>
    public static readonly Matrix4x4[] PartWorld = new Matrix4x4[MaxParts];
    /// <summary>The subtree draw gate (<c>SetSubtreeEnabled</c>) and the <c>CombineParts</c> merged-away marker.</summary>
    public static readonly bool[] PartEnabled = new bool[MaxParts], PartMerged = new bool[MaxParts];
    /// <summary>Animated per-part alpha and self-illumination (controllers), 1 / 0 at rest.</summary>
    public static readonly float[] PartAlpha = new float[MaxParts];
    public static readonly Vector3[] PartSelfIllum = new Vector3[MaxParts];
    /// <summary>The mesh-arena range this part draws, -1 for a part with no geometry.</summary>
    public static readonly int[] PartMesh = new int[MaxParts];
    /// <summary>A reference part's instanced object -- the engine's <c>PartReference+0x68</c>, which
    /// <c>CreateReferenceObjects</c> @0x4421e0 fills and <c>Gob::ReattachReference</c> @0x43fc50 re-points.
    /// Null for every other part kind. It is the p2p edge: <c>SetEmitterTarget</c> @0x4aa7d0 takes the first
    /// reference CHILD of an emitter part and makes its object that emitter's target.</summary>
    public static readonly DbId[] PartRefObj = new DbId[MaxParts];

    // ---- scenes ----
    /// <summary>A scene with a tile grid is a world scene; one without is a GUI panel's (the engine's <c>Scene+0x188</c> test).</summary>
    public static readonly bool[] SceneIsWorld = new bool[MaxScenes];
    public static readonly nint[] SceneFacade = new nint[MaxScenes];
    /// <summary>The scene's name as <c>NewCAurScene</c> was given it
    /// (<c>sceneGUI_&lt;panel&gt;</c>, <c>sceneArea</c>, ...). The GUI depth convention and the engine's two
    /// lit-GUI-scene tests are name rules, so the name has to survive the seam; seam-obj writes it.</summary>
    public static readonly string[] SceneName = new string[MaxScenes];

    /// <summary>Scene <paramref name="scene"/>'s name as the single-byte bytes the depth-range table compares,
    /// into <paramref name="dst"/>; returns the length written (never past <paramref name="dst"/>'s last byte,
    /// so the caller can always terminate). Allocation-free.</summary>
    public static int SceneNameBytes(DbId scene, Span<byte> dst)
    {
        string? n = (uint)scene.Index < MaxScenes ? SceneName[scene.Index] : null;
        if (n == null) return 0;
        int k = 0;
        for (; k < n.Length && k < dst.Length - 1; k++) dst[k] = (byte)n[k];
        return k;
    }

    // ---- cameras ----
    public static readonly DbId[] CamScene = new DbId[MaxCameras];
    public static readonly nint[] CamFacade = new nint[MaxCameras];
    /// <summary>The camera's name after <see cref="DbScene.UniqueName"/>, the one its shell's Gob half carries.</summary>
    public static readonly string?[] CamName = new string?[MaxCameras];


    // ---- CX-120 seam-obj: the columns the CAurObject slots drive. Append-only; names prefixed ObjSeam* so
    // they cannot collide with another stream's block. ----

    /// <summary>The object's boolean state the engine kept as bits on the Gob. One word so a slot's setter is
    /// one OR and the renderer's read is one load.</summary>
    public static readonly uint[] ObjSeamFlags = new uint[MaxObjects];
    /// <summary><c>Gob::SetWind(bool, float)</c>'s strength; the flag says whether wind applies at all.</summary>
    public static readonly float[] ObjSeamWind = new float[MaxObjects];
    /// <summary><c>Gob::SetLightPriority(int)</c>.</summary>
    public static readonly int[] ObjSeamLightPri = new int[MaxObjects];
    /// <summary><c>Gob::SetColorShifting(Vector, float, int)</c>: the colour in xyz, the amount in w.</summary>
    public static readonly Vector4[] ObjSeamColourShift = new Vector4[MaxObjects];
    /// <summary>The <c>int</c> third argument of <c>SetColorShifting</c> and the second of
    /// <c>SetIllumination</c> -- which part set the shift applies to.</summary>
    public static readonly int[] ObjSeamColourShiftMode = new int[MaxObjects], ObjSeamIllumMode = new int[MaxObjects];
    /// <summary><c>Gob::ScaleObject</c>'s <c>AUR_OBJECTSCALE_TYPE</c>.</summary>
    public static readonly byte[] ObjSeamScaleMode = new byte[MaxObjects];
    /// <summary>The LOD block <c>NewCAurObject</c> fills: the level in use and the two switch distances
    /// (99999 when the object has no <c>.lod</c> resource), with the models the other two levels use.</summary>
    public static readonly int[] ObjSeamLod = new int[MaxObjects];
    public static readonly float[] ObjSeamLodDist1 = new float[MaxObjects], ObjSeamLodDist2 = new float[MaxObjects];
    public static readonly ModelDef?[] ObjSeamLodModel1 = new ModelDef?[MaxObjects], ObjSeamLodModel2 = new ModelDef?[MaxObjects];
    /// <summary>Last frame's position, for <c>Gob::GetPreviousPosition</c> and the motion vectors;
    /// <see cref="NoPrevPos"/> until the animate step has stamped one.</summary>
    public static readonly Vector3[] ObjSeamPrevPos = new Vector3[MaxObjects];
    /// <summary>The "never stepped" value <c>Gob::Gob</c> seeds <c>Gob+0xc4</c> with and
    /// <c>Gob::Animate</c>'s gust test compares against.</summary>
    public const float NoPrevPos = -1e7f;
    /// <summary>Head of the object's intrusive list of Part shells (the <c>CAurPart*</c> values handed to the
    /// game) and of its registered animation callbacks; both freed when the object retires.</summary>
    public static readonly nint[] ObjSeamPartShells = new nint[MaxObjects], ObjSeamCallbacks = new nint[MaxObjects];
    /// <summary>The objects this one OWNS, as an intrusive list: the head is here and each member's next is
    /// in <see cref="ObjSeamAppendNext"/>. Two kinds, one lifetime rule -- they die with their parent: what
    /// <c>Gob::Append</c> composed onto it (CX composes child records where the engine grew one part tree),
    /// and what a reference node instanced (<c>CreateReferenceObjects</c> @0x5421e0).</summary>
    public static readonly DbId[] ObjSeamAppendHead = new DbId[MaxObjects], ObjSeamAppendNext = new DbId[MaxObjects];

    /// <summary>Index into the visual-transform pool, -1 for an object that has none.</summary>
    public static readonly int[] ObjSeamVisualXform = new int[MaxObjects];
    /// <summary>The engine's model name is the MODEL's, not the object's (<c>Gob::GetModelName</c> is
    /// <c>model + 0x10</c>), so a text or empty object has none; this is the resref the object was asked for,
    /// which is what every caller actually wants.</summary>
    public static readonly string?[] ObjSeamModelName = new string?[MaxObjects];

    // ---- CX-119 (text): the text position offset ----

    /// <summary>A text object's position offset, in the attached frame. <c>StringGob::SetPositionOffset</c>
    /// @0x492420 writes it onto the installed behaviour, and only a <c>CAurBehaviorAttachTranslate</c> (type
    /// <c>0x3ea</c>), whose <c>Control</c> @0x4a2960 applies it after the base attach has snapped the object
    /// onto its parent part. Hence a column of its own: <see cref="ObjPos"/> is overwritten every compose.</summary>
    public static readonly Vector3[] TextPosOffset = new Vector3[MaxObjects];

    /// <summary>The behaviour <see cref="TextPosOffset"/> was set for, 0 for none. In the engine the offset
    /// lives ON the behaviour, so replacing the behaviour drops it; keeping the pointer reproduces that
    /// lifetime without a vtable call per compose.</summary>
    public static readonly nint[] TextOffsetBehavior = new nint[MaxObjects];

    /// <summary>Mark object <paramref name="o"/> dirty; the seam's setters call this and nothing else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Touch(DbId o, Dirty d) { if (Objects.Valid(o)) Objects.Mark(o.Index, d); }

    // ---- CX-120 seam-scene: scene and camera columns (append-only; owner: DbScene / DbCamera) ----

    /// <summary>Planes a camera's view volume carries: <c>Camera::RenderScene</c> fills six (near, far and the
    /// four side planes) into the list at <c>Camera+0x4e8</c>, and <c>GetViewVolume(i)</c> indexes it.</summary>
    public const int ViewPlanes = 6;

    /// <summary>Scene ambient (<c>CAurScene::SetAmbient</c>, engine <c>Scene+0x168</c>), mirrored into the
    /// facade shell at that offset because the game reads it there without a getter.</summary>
    public static readonly Vector3[] SceneAmbient = new Vector3[MaxScenes];
    /// <summary>Fog as the four fog slots hold it: range, colour, and whether it is on.</summary>
    public static readonly float[] SceneFogNear = new float[MaxScenes], SceneFogFar = new float[MaxScenes];
    public static readonly Vector3[] SceneFogColor = new Vector3[MaxScenes];
    public static readonly bool[] SceneFogOn = new bool[MaxScenes];
    /// <summary>The point the engine's static-lighting and tile-fade steps measure from
    /// (<c>Scene::SetSceneFocus</c>): the player's gob position, written once per frame by the game.</summary>
    public static readonly Vector3[] SceneFocus = new Vector3[MaxScenes];
    /// <summary><c>Scene::SetGeomFadeProperties</c>'s integer. GLOBAL, not per scene:
    /// <c>Scene::SetGeomFadeProperties</c> @0x55e1d0 writes the <c>geomFadeEnabled</c> global and
    /// <c>Scene::IsGeomFadeEnabled</c> @0x5778b0 is a STATIC that reads it back -- neither takes a
    /// <c>this</c> that matters. One int for the session, therefore.</summary>
    public static int GeomFade;
    /// <summary><c>Scene::EnableAnimations</c>/<c>DisableAnimations</c>: the <c>Animate</c> slot's gate.</summary>
    public static readonly bool[] SceneAnimOn = new bool[MaxScenes];
    /// <summary><c>Scene::IsAnimating</c>'s answer. The engine caches it in a byte at <c>Scene+0xb9</c> and
    /// the getter is one load; so is this. Written by the animation step, which is the only thing that knows.</summary>
    public static readonly bool[] SceneAnimating = new bool[MaxScenes];
    /// <summary><c>Scene::SetGlobalWind(dir, magnitude, gustiness)</c> and <c>SetWindUpdateTimeout</c>.</summary>
    public static readonly Vector3[] SceneWindDir = new Vector3[MaxScenes];
    public static readonly float[] SceneWindMag = new float[MaxScenes], SceneWindGust = new float[MaxScenes],
                                  SceneWindTimeout = new float[MaxScenes];
    /// <summary>The fade object (<c>Scene::SetSkyFade</c>, engine <c>Scene+0x38</c>) -- separate from the four
    /// skybox objects, which live in <see cref="SceneSky0"/>..<see cref="SceneSky3"/>.</summary>
    public static readonly DbId[] SceneSkyFade = new DbId[MaxScenes];
    /// <summary><c>Scene::ClearSkyBox</c>'s off byte (engine <c>Scene+0x4c</c>). Note it is OFF, not on:
    /// <c>SetSkyBox</c> clears it and <c>ClearSkyBox</c> sets it, and neither forgets the objects.</summary>
    public static readonly bool[] SceneSkyOff = new bool[MaxScenes];
    /// <summary>Skybox objects a scene holds.</summary>
    public const int SkyBoxSlots = 4;
    /// <summary><c>Scene::SetBlindLights</c>: the one object whose lights alone light the scene while a
    /// blindness effect is up (the engine's own name for it). Null = normal lighting.</summary>
    public static readonly DbId[] SceneBlindLights = new DbId[MaxScenes];
    /// <summary><c>Scene::SetShadowGob</c>/<c>GetShadowGob</c>: the one object the engine's single-caster
    /// shadow path casts from.</summary>
    public static readonly DbId[] SceneShadowGob = new DbId[MaxScenes];
    /// <summary><c>Scene::SetHighlightGobTexture</c>: the scrolling texture the highlight pass modulates by.</summary>
    public static readonly string[] SceneHighlightTex = new string[MaxScenes];
    /// <summary><c>Scene::SetRenderAABB</c>: the engine's AABB-tree debug draw.</summary>
    public static readonly bool[] SceneRenderAabb = new bool[MaxScenes];
    /// <summary>The scene's member list, in DRAW ORDER: <c>AddObject</c>/<c>ChildAdd</c> append, the four
    /// <c>RenderGob*</c> slots move, and the facade shell's <c>+0x130</c>/<c>+0x138</c> mirror it so
    /// <c>GetAurSceneObjectCount</c>'s direct read answers. Null until the scene has a member.</summary>
    public static readonly DbList?[] SceneObjects = new DbList?[MaxScenes];
    /// <summary>The scene's highlighted objects and their colours (<c>AddHighlightGob</c>), parallel lists.</summary>
    public static readonly DbList?[] SceneHighlights = new DbList?[MaxScenes];
    /// <summary>The scene's debug line set (<c>AddLinesToRender</c>): points, packed RGBA colours and the
    /// per-strip point counts, exactly the three lists the slot is handed.</summary>
    public static readonly DbLines?[] SceneLines = new DbLines?[MaxScenes];
    // ---- cameras ----
    public static readonly Vector3[] CamPos = new Vector3[MaxCameras], CamPrevPos = new Vector3[MaxCameras];
    public static readonly Quaternion[] CamRot = new Quaternion[MaxCameras];
    /// <summary>The camera's own viewport rect (<c>Camera+0x4d4..+0x4e0</c>). Both extents zero means "the
    /// whole GL viewport", which is how the WORLD camera is recognised -- <c>Camera::SetViewPort</c> clamps a
    /// non-positive extent to 1, so a camera that ever set one cannot read as the world camera.</summary>
    public static readonly float[] CamViewX = new float[MaxCameras], CamViewY = new float[MaxCameras],
                                   CamViewW = new float[MaxCameras], CamViewH = new float[MaxCameras];
    /// <summary>Near/far clip (<c>Camera+0x4cc/+0x4d0</c>) and the vertical view angle in DEGREES
    /// (<c>Camera+0x4c4</c>); an angle of 0 or less is the ORTHO branch, which is every GUI camera.</summary>
    public static readonly float[] CamNear = new float[MaxCameras], CamFar = new float[MaxCameras],
                                   CamViewAngle = new float[MaxCameras];
    /// <summary>The object the camera rides (<c>SetFollow</c>/<c>AttachToObject</c>/<c>AttachToPart</c>) and
    /// the part index it hangs from, -1 for the object's root.</summary>
    public static readonly DbId[] CamFollow = new DbId[MaxCameras], CamAttachTo = new DbId[MaxCameras];
    public static readonly int[] CamAttachPart = new int[MaxCameras];
    /// <summary>The game's own <c>CAurBehavior*</c> for this camera (<c>SetBehavior</c>). An engine-layout
    /// object the game constructs and subclasses, so CX holds the pointer and calls its vtable -- never a
    /// record of ours (ADR 0028 section 2).</summary>
    public static readonly nint[] CamBehavior = new nint[MaxCameras];
    /// <summary>The camera's world-space view volume, <see cref="ViewPlanes"/> per camera (camera-major), and
    /// whether this frame's view filled it (<c>Camera+0x4e4</c>).</summary>
    public static readonly Plane[] CamVolume = new Plane[MaxCameras * ViewPlanes];
    public static readonly bool[] CamVolumeSet = new bool[MaxCameras];
    /// <summary>The two geometry-fade planes <c>Camera::SetFadePlanes</c> hands the renderer.</summary>
    public static readonly Plane[] CamFadeA = new Plane[MaxCameras], CamFadeB = new Plane[MaxCameras];
    /// <summary>The <c>CAurObject</c> sub-object a <c>Camera</c> IS: the game reaches it as
    /// <c>(CAurObject*)(camera + 8)</c> by a literal offset (<c>GuiAttachViewport</c>), and every
    /// <c>CAurObject</c> slot reached through it works on THIS record. Minted with the camera.</summary>
    public static readonly DbId[] CamObj = new DbId[MaxCameras];

    /// <summary>Mark scene <paramref name="s"/> dirty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TouchScene(DbId s, Dirty d) { if (Scenes.Valid(s)) Scenes.Mark(s.Index, d); }

    /// <summary>Mark camera <paramref name="c"/> dirty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TouchCam(DbId c, Dirty d) { if (Cameras.Valid(c)) Cameras.Mark(c.Index, d); }

    // ---- CX-114 part columns (append-only; owner: the part-tree builder) ----

    /// <summary>What the part instances, from its node's kind. The draw path's first discriminator: a mesh
    /// kind has an arena range, a light is the light registry's, an emitter the particle pass's, and a
    /// <see cref="PartKind.Dummy"/> exists only to carry a transform and a name.</summary>
    public static readonly PartKind[] PartKindOf = new PartKind[MaxParts];
    /// <summary>The geometry this part draws -- the node's own <see cref="MeshDef"/>, or the NEW one
    /// <c>CombineParts</c> produced when the engine merged several tile meshes into this one. Null for a part
    /// with no geometry. <see cref="PartMesh"/> is this mesh's <see cref="MeshArena"/> range.</summary>
    public static readonly MeshDef?[] PartMeshDef = new MeshDef?[MaxParts];
    /// <summary>The part's material terms, as <c>PartInitMaterial</c> and the <c>.mtr</c> leave them.</summary>
    public static readonly PartMaterial[] PartMtl = new PartMaterial[MaxParts];
    /// <summary>The resolved <c>.mtr</c> for this part, shared by every part whose mesh names the same
    /// material; null when the mesh names none or it could not be read. Its texture names win over the
    /// mesh's.</summary>
    public static readonly MtrDef?[] PartMtr = new MtrDef?[MaxParts];
    /// <summary>Four texture ids per part (stride 4, slot 0 the diffuse one), for the texture registry
    /// to fill; 0 is "not resolved yet". The NAMES are not copied per part -- they live in the shared
    /// <see cref="PartMtr"/> or <see cref="PartMeshDef"/>, and <see cref="PartTexName"/> reads them.</summary>
    public static readonly int[] PartTexId = new int[MaxParts * 4];
    /// <summary>The part's bounds in ITS OWN space, from <c>ComputePartBBox</c> over the part's geometry, and
    /// the sphere the cull and the cast pass read.</summary>
    public static readonly Vector3[] PartBMin = new Vector3[MaxParts], PartBMax = new Vector3[MaxParts];
    public static readonly Vector3[] PartCenter = new Vector3[MaxParts];
    public static readonly float[] PartRadius = new float[MaxParts];

    /// <summary>Texture slot <paramref name="slot"/> of part <paramref name="part"/> as the draw resolves it:
    /// the part's OVERRIDE when it has one, else its recipe's own. The ONE selection the stage, the sampler
    /// path and the <c>.txi</c> path share -- an environment map is not one of these slots, it is
    /// <see cref="ObjEnvMap"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TexId PartTex(int part, int recipe, int slot)
    {
        if ((uint)slot < 4)
        {
            int ov = PartTexId[part * 4 + slot];
            if (ov != 0) return new TexId((uint)ov);
        }
        return recipe >= 0 && slot < DrawRecipe.TexSlots ? DrawRecipe.Tex(recipe, slot) : default;
    }

    /// <inheritdoc cref="PartTex(int,int,int)"/>
    internal static TexId PartTex(int part, int slot) => PartTex(part, PartRecipe[part], slot);

    /// <summary>Did the game name an environment map for the object this part belongs to
    /// (<c>Gob::SetEnvironmentMap</c> @0x43f530, which kept ONE texture per gob at <c>gob+0x480</c>)? The
    /// instance half of the draw's env-map bit; the other half is the diffuse's own <c>.txi</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool PartEnvMapped(int part)
    {
        int o = PartObj[part].Index;
        return (uint)o < MaxObjects && ObjEnvMap[o] != null;
    }

    /// <summary>Texture slot <paramref name="slot"/> of part <paramref name="part"/>: the <c>.mtr</c>'s name
    /// when it states one, else the mesh's own. Empty for none.</summary>
    public static string PartTexName(int part, int slot)
    {
        if ((uint)slot >= 4) return "";
        MtrDef? m = PartMtr[part];
        if (m != null && m.Textures[slot].Length > 0) return m.Textures[slot];
        MeshDef? mesh = PartMeshDef[part];
        return mesh != null ? mesh.Textures[slot] : "";
    }

    // ---- CX-118 (hit testing): the columns a cast reads. Append-only, owned by the CX-118 stream. ----

    /// <summary>The object's world AABB, the union of its enabled parts' world boxes. Written by
    /// <see cref="HitBounds.Refresh"/> on the object's <see cref="Dirty.Xform"/> / <see cref="Dirty.Tree"/>
    /// mark; read by <see cref="AreaGrid"/> and the cast's broad phase. A degenerate box (min &gt; max) means
    /// "no geometry": never filed, never hit.</summary>
    public static readonly Vector3[] HitObjBMin = new Vector3[MaxObjects], HitObjBMax = new Vector3[MaxObjects];
    /// <summary>The object's world bounding sphere -- vanilla's own first reject (<c>Gob::HitCheck</c>'s
    /// <c>SphereIntersect</c> against <c>Gob+0x17c</c> / <c>+0x178</c> scaled by <c>GetAdjustedScale</c>).</summary>
    public static readonly Vector3[] HitObjSphereC = new Vector3[MaxObjects];
    /// <inheritdoc cref="HitObjSphereC"/>
    public static readonly float[] HitObjSphereR = new float[MaxObjects];
    /// <summary>The object's own alpha (<c>Gob+0x108</c>): <c>Scene::HitCheckGob</c> skips an object at or
    /// below zero, so a fully transparent object is not pickable.</summary>
    public static readonly float[] HitObjAlpha = new float[MaxObjects];
    /// <summary>The door fade's output (<c>Gob+0x10c</c>) -- the DB column that replaces the engine field
    /// <c>DoorFade</c> writes when Alloy is off. A door at or below <c>mindooralpha</c> is not hit-tested.</summary>
    public static readonly float[] HitObjFadeAlpha = new float[MaxObjects];
    /// <summary>The model's classification byte (<c>Model+0x8e</c>), copied per object so the cast's gates
    /// cost no model deref: 1 effect, 2 tile, 4 character, 8 door, 0 anything else.</summary>
    public static readonly byte[] HitObjClass = new byte[MaxObjects];
    /// <summary>The object belongs to the area's static set (a tile's parts, a static placeable), which a cast
    /// tests only when it asks for statics -- the engine's <c>hitcheck_static_trimesh_parts</c> gate.</summary>
    public static readonly bool[] HitObjStatic = new bool[MaxObjects];
    /// <summary>Part-space bounds of the part's own geometry, from its <c>MeshDef</c> (degenerate for a part
    /// with none): the cast's per-part reject, and the whole test in box-only mode.</summary>
    public static readonly Vector3[] HitPartBMin = new Vector3[MaxParts], HitPartBMax = new Vector3[MaxParts];
    /// <summary>The trigger payload <c>PartTrigger::HitCheckGeom</c> copies into <c>CHitInfo+0x70</c>
    /// (from <c>PartTrigger+0xd0</c>) -- an area transition's identity, for the parts that are triggers.</summary>
    public static readonly long[] HitPartTrigger = new long[MaxParts];


    // ---- CX-116 tiles: one record per tile PLACEMENT, written by TileBuild ----

    /// <summary>Tile placements held at once. The engine's largest grid is 32x32 and
    /// <c>AreaBorder.MaxRing</c> is 40 tiles of ring on each side, so the worst case is
    /// <c>(32 + 80)^2 = 12544</c>; 16384 covers it with the occupancy on the period line.</summary>
    public const int MaxTiles = 1 << 14;
    public static readonly DbTable Tiles = new(MaxTiles);

    public static readonly DbId[] TileScene = new DbId[MaxTiles];
    /// <summary>The object record the tile's geometry hangs on (one object per placement).</summary>
    public static readonly DbId[] TileObj = new DbId[MaxTiles];
    /// <summary>The tile model, shared by every placement of this tile kind.</summary>
    public static readonly ModelDef?[] TileModel = new ModelDef?[MaxTiles];
    /// <summary>Grid cell and height step as the area message states them (<c>CNWTile::SetPosition</c>
    /// @0x732560 stores x at +0x1c, y at +0x20, the height index at +0x18).</summary>
    public static readonly int[] TileGridX = new int[MaxTiles], TileGridY = new int[MaxTiles], TileHeight = new int[MaxTiles];
    /// <summary>Quarter turns about +Z (<c>CNWCTile::SetOrientation</c> @0x90c9b0: <c>orient * 90</c> degrees).</summary>
    public static readonly int[] TileOrient = new int[MaxTiles];
    /// <summary>The tileset row the area message named, or -1 for a ring tile.</summary>
    public static readonly int[] TileSetId = new int[MaxTiles];
    /// <summary>World placement: <c>(gx*10+5, gy*10+5, heightTransition*height)</c>.</summary>
    public static readonly Vector3[] TilePos = new Vector3[MaxTiles];
    /// <summary>World bounds of the placement, the model-space merged bounds placed by the transform --
    /// the engine's <c>TileData+0x58</c>/<c>+0x64</c> union over its trimesh parts' world boxes.</summary>
    public static readonly Vector3[] TileBmin = new Vector3[MaxTiles], TileBmax = new Vector3[MaxTiles];
    /// <summary>The <c>lightcolor.2da</c> palette rows the area message names for the two main lights and the
    /// two source lights (0 = none; the source-light wire byte is stored +1, as the loop reads it).</summary>
    public static readonly byte[] TileMain1 = new byte[MaxTiles], TileMain2 = new byte[MaxTiles];
    public static readonly byte[] TileSrc1 = new byte[MaxTiles], TileSrc2 = new byte[MaxTiles];
    /// <summary>The three <c>AnimLoop01..03</c> enables and the texture-replacement palette row.</summary>
    public static readonly byte[] TileAnim1 = new byte[MaxTiles], TileAnim2 = new byte[MaxTiles], TileAnim3 = new byte[MaxTiles];
    public static readonly byte[] TileReplaceRow = new byte[MaxTiles];
    /// <summary>The texture the swap put in place of <c>replace_tex</c>, or null.</summary>
    public static readonly string?[] TileReplaceTex = new string?[MaxTiles];
    /// <summary>A ring tile (the engine's <c>border</c> argument to <c>SpawnTile</c> was non-zero).</summary>
    public static readonly bool[] TileIsBorder = new bool[MaxTiles];
    /// <summary>Fog of war: set while the area calls the tile unexplored (<c>AuroraSetTileFoggedByWar</c>).</summary>
    public static readonly bool[] TileFogged = new bool[MaxTiles];
    /// <summary>The two per-tile ramps <c>TileFadeRamp</c> drives, and the camera-fade hysteresis accumulator.</summary>
    public static readonly float[] TileFade = new float[MaxTiles], TileFadeAcc = new float[MaxTiles], TileReveal = new float[MaxTiles];
    /// <summary>The tile's lights in <see cref="Lights"/>: <see cref="TileLightStride"/> slots per tile,
    /// each a raw index or -1. NOT a contiguous run -- <see cref="DbTable"/>'s free list is LIFO, so after the
    /// first area change two lights allocated together land out of order.</summary>
    public const int TileLightStride = 8;
    public static readonly int[] TileLightIds = NewTileLightIds();
    public static readonly int[] TileLightCount = new int[MaxTiles];
    private static int[] NewTileLightIds() { var a = new int[MaxTiles * TileLightStride]; Array.Fill(a, -1); return a; }
    /// <summary>World bounds of the tile's <c>tilefade 1</c> geometry together: the UNION, used to ask
    /// whether any of it is in view. NOT the occlusion test -- see <see cref="TileFadeBoxLo"/>.</summary>
    public static readonly Vector3[] TileFadeBmin = new Vector3[MaxTiles], TileFadeBmax = new Vector3[MaxTiles];
    /// <summary>World bounds of each <c>tilefade 1</c> PART of the tile, up to
    /// <see cref="TileFadeBoxStride"/>. <c>CheckFadePlane</c> @0x47bf30 tests the parts one at a time and
    /// fades the tile when ANY is not entirely beyond the plane -- not the union's question, whose empty
    /// corners straddle planes every real part is clear of (the stock inn: 3 roof tiles faded, not 1).</summary>
    public const int TileFadeBoxStride = 4;
    public static readonly Vector3[] TileFadeBoxLo = new Vector3[MaxTiles * TileFadeBoxStride];
    /// <inheritdoc cref="TileFadeBoxLo"/>
    public static readonly Vector3[] TileFadeBoxHi = new Vector3[MaxTiles * TileFadeBoxStride];
    /// <inheritdoc cref="TileFadeBoxLo"/>
    public static readonly byte[] TileFadeBoxN = new byte[MaxTiles];
    /// <summary>The two <c>fx_flame01</c> "sourcelight" objects the source-light rows asked for.</summary>
    public static readonly DbId[] TileFlame1 = new DbId[MaxTiles], TileFlame2 = new DbId[MaxTiles];

    // ---- CX-116 lights: the DB light set, opened for tile lights; gob lights join it the same way ----

    /// <summary>Light records held at once. Sized from the deleted <c>LightRegistry</c>'s capacity, which was itself sized
    /// from a 32x32 area at the 2,4 lights per tile a lamp-lit city tileset measures.</summary>
    public const int MaxLights = 1 << 14;
    public static readonly DbTable Lights = new(MaxLights);

    /// <summary>The object the light belongs to (a tile light's object is its placement's) and the part that
    /// instances its node, -1 while the part tree is a stub.</summary>
    public static readonly DbId[] LitObj = new DbId[MaxLights];
    public static readonly int[] LitPart = new int[MaxLights];
    /// <summary>The tile placement a tile light was registered with, null for a gob light.</summary>
    public static readonly DbId[] LitTile = new DbId[MaxLights];
    /// <summary>World position, written with the record and again whenever its part's world matrix moves.</summary>
    public static readonly Vector3[] LitPos = new Vector3[MaxLights];
    public static readonly Vector3[] LitColour = new Vector3[MaxLights];
    /// <summary>The engine's two radii (<c>PartLight</c> <c>range</c> +0x70 and <c>rangeAlt</c> +0x78).</summary>
    public static readonly float[] LitRange = new float[MaxLights], LitRangeAlt = new float[MaxLights];
    public static readonly float[] LitMultiplier = new float[MaxLights];
    /// <summary><c>PartLight</c> <c>priority</c> (+0x8c): 4 for a tileset main light.</summary>
    public static readonly int[] LitPriority = new int[MaxLights];
    public static readonly bool[] LitShadow = new bool[MaxLights], LitAmbientOnly = new bool[MaxLights], LitDynamic = new bool[MaxLights];
    /// <summary>A tileset MAIN light: the one the area's palette rows re-colour through
    /// <c>Scene::SetTileLights</c>. Slot 0 is main light 1, slot 1 main light 2.</summary>
    public static readonly int[] LitMainSlot = new int[MaxLights];

    // ---- CX-117: PLT colours (owner: TexRegistry / PltColors) ----

    /// <summary>PLT layers a pixel may name, one palette each: the engine's layered-texture count.</summary>
    public const int PltLayers = 10;
    /// <summary>Per object, one master-palette ROW per PLT layer (<see cref="PltLayers"/> entries,
    /// layer-major). The value is the engine's own scheme integer -- a global row into the 256x1792 master
    /// palette, <c>paletteSlot*256 + colour</c>. Alloy resolves <c>colour = master[row[layer]][intensity]</c>
    /// in the shader, so a colour change is one entry here plus <see cref="Dirty.Material"/>.</summary>
    public static readonly ushort[] ObjPltRow = new ushort[MaxObjects * PltLayers];
    /// <summary>Per object, the environment map <c>Gob::SetEnvironmentMap</c> last named (null = none). The
    /// engine kept it on the gob (<c>gob+0x480</c>), so a body piece appended AFTER the call still drew
    /// env-mapped: <c>CNWCCreatureAppearance::CreateBodyParts</c> @0x834440 sets appearance.2da's ENVMAP before
    /// it grafts a single part. A graft takes it from here (<c>ObjectSlots.Append</c>).</summary>
    public static readonly string?[] ObjEnvMap = new string?[MaxObjects];
    /// <summary>Per part, the PLT rows a <c>ReplaceTexture</c> that swapped THIS part's texture carried (same
    /// layout as <see cref="ObjPltRow"/>). The engine's colour lives on the layered texture, so one creature
    /// wears a different scheme per body part; a part with <see cref="PartPltOwn"/> clear reads its object's
    /// rows instead. Written only through <c>PltColors</c>.</summary>
    public static readonly ushort[] PartPltRow = new ushort[MaxParts * PltLayers];
    /// <summary>Does <see cref="PartPltRow"/> hold this part's rows? Cleared when the part record is reused.</summary>
    public static readonly bool[] PartPltOwn = new bool[MaxParts];

    // ---- seam-feed (CX-120) ----
    // The columns the renderer's record feed reads in place of the per-frame engine dereferences
    // the engine-scene renderer made. Everything here is per-INSTANCE: the stable half of a draw
    // (class, blend, textures, material terms, local bounds, arena range) depends only on the (ModelDef, node)
    // pair and is interned once in <see cref="DrawRecipe"/>, which is why a part needs one int for all of it.

    /// <summary>What an object is, for the questions the draw used to answer by chasing <c>Gob</c> pointers.</summary>
    public static readonly ObjFlag[] ObjFlags = new ObjFlag[MaxObjects];
    /// <summary>Quarter turns of TEXTURE rotation this placement's parts carry, NWN's tile anti-repetition
    /// scheme: <c>SpawnPartsForTile</c> @0x563450 ends with <c>SetPartTextureRotate(parts, orient)</c>
    /// @0x561670, <c>orient * 90</c> degrees on every eligible mesh (<c>PartTriMesh+0xb0</c>). Per OBJECT, not
    /// per recipe: one <see cref="DrawRecipe"/> serves every placement; a static placeable gets orient 0.</summary>
    public static readonly byte[] ObjTexRotate = new byte[MaxObjects];
    /// <summary>The object a blended run composites WITH and a fade applies to: the root of the attachment
    /// chain, or the explicit fade owner. Resolved when attachment or fade ownership changes, never per frame
    /// (the engine path climbed <c>Gob+0x68</c> up to eight hops per blended part, per pass).</summary>
    public static readonly DbId[] ObjRunRoot = new DbId[MaxObjects];
    /// <summary>Which part-tree alternate this object shows (the engine's <c>GobPartTree</c> pick). A part of
    /// another alternate is skipped; <see cref="PartLodTree"/> is the part's side of the same test.</summary>
    public static readonly byte[] ObjLodShown = new byte[MaxObjects];
    /// <summary>World cull sphere, centre + radius, written by the transform step beside <see cref="ObjWorld"/>.</summary>
    public static readonly Vector4[] ObjSphere = new Vector4[MaxObjects];

    /// <summary>The interned stable half of this part's draw (<see cref="DrawRecipe"/>), -1 for a part that
    /// draws nothing. Replaces the per-frame reads of <c>PartRef.Mdl</c>, <c>MdlRef.Ambient/Diffuse</c>,
    /// <c>MaterialRef.Texture/MapTexture</c>, the VDO index range and the <c>IsDangly</c> vtable probe.</summary>
    public static readonly int[] PartRecipe = Fill(new int[MaxParts], -1);
    /// <summary>The part-tree alternate this part belongs to: 0 = in every alternate, else LOD level + 1 (the
    /// level-0 tree of an object that HAS alternates is 1, so it can hide). Read through <see cref="LodHidden"/>.</summary>
    public static readonly byte[] PartLodTree = new byte[MaxParts];
    /// <summary>A part of tree <paramref name="tree"/> is skipped while its object shows LOD level <paramref name="shown"/>.</summary>
    public static bool LodHidden(byte tree, byte shown) => tree != 0 && tree != shown + 1;
    /// <summary>The fade multiplier the tile this part stands on, or the door it belongs to, currently applies
    /// (1 = no fade). Written by the tiles stream's tile-fade ramp and by the hit stream's door fade; the feed
    /// only multiplies it in, so neither costs the draw a tile dereference.</summary>
    public static readonly float[] PartFade = Fill(new float[MaxParts], 1f);
    /// <summary>The fog-of-war REVEAL this part's tile is at (1 = fully revealed). The engine's fog of war is a
    /// FOG term, not an alpha: below 1 the draw enables fog, the colour being the scene's fog colour times
    /// <c>reveal^2</c>, the start <c>reveal * Scene+0x144</c> and the end <c>reveal * Scene+0x148 + 0.1</c>.
    /// Folding it into <see cref="PartFade"/> would make an unexplored tile transparent instead of fogged.</summary>
    public static readonly float[] PartReveal = Fill(new float[MaxParts], 1f);

    /// <summary>The keyhole dissolve this part is under (1 = solid), written by the dissolve step.</summary>
    public static readonly float[] PartDissolve = Fill(new float[MaxParts], 1f);
    /// <summary>The skin palette's base bone slot for this part this frame, -1 for a rigid part. Written by
    /// seam-anim's pose step; the feed uploads the palette it names.</summary>
    public static readonly int[] PartBoneBase = Fill(new int[MaxParts], -1);
    /// <summary>The renderer's ownership slot holding this part's record, -1 for a part with none. The join the
    /// change-driven build needs, so a dirty part is rewritten without a table scan.</summary>
    public static readonly int[] PartSlot = Fill(new int[MaxParts], -1);

    // ---- seam-anim (CX-120): the light terms an animation drives ----

    /// <summary>A light part's ANIMATED colour, radius and multiplier, written by the pose step from the node's
    /// <c>LightColor</c>/<c>LightRadius</c>/<c>LightMultiplier</c> controllers (file ids 76 / 88 / 140) and
    /// meaningless until <see cref="PartLitAnim"/> says so. A blend target, not a sample -- a controller writes
    /// <c>lerp(column, key, weight)</c> -- seeded from the node's rest keys on its first write.</summary>
    public static readonly Vector3[] PartLitColour = new Vector3[MaxParts];
    /// <inheritdoc cref="PartLitColour"/>
    public static readonly float[] PartLitRadius = new float[MaxParts];
    /// <inheritdoc cref="PartLitColour"/>
    public static readonly float[] PartLitMul = new float[MaxParts];
    /// <summary>Which of <see cref="PartLitColour"/> / <see cref="PartLitRadius"/> / <see cref="PartLitMul"/>
    /// an animation has written: bit 0 colour, bit 1 radius, bit 2 multiplier. Zero means "read the node's
    /// rest keys", which is every light in a model that animates none of them.</summary>
    public static readonly byte[] PartLitAnim = new byte[MaxParts];

    /// <summary>A column whose empty value is not zero, seeded at type init: -1 means "no slot", "no recipe",
    /// "no bone palette", and part 0 / recipe 0 / bone slot 0 are all real.</summary>
    private static int[] Fill(int[] a, int v) { Array.Fill(a, v); return a; }
    private static float[] Fill(float[] a, float v) { Array.Fill(a, v); return a; }

    /// <summary>The scene's three SKY objects, in the engine's own draw order (current,
    /// the one fading in over it, the cross-fade). They hang off the SCENE rather than off any part tree --
    /// the engine kept them at <c>Scene+0x18</c>/<c>+0x38</c> and only <c>RenderSkyBoxes</c> ever walked them.
    /// seam-scene's <c>SetSkyBox</c> slot writes them.</summary>
    public static readonly DbId[] SceneSky0 = new DbId[MaxScenes],
                                  SceneSky1 = new DbId[MaxScenes],
                                  SceneSky2 = new DbId[MaxScenes],
                                  SceneSky3 = new DbId[MaxScenes];

    /// <summary>The sky cross-fade, as the engine's own three fields hold it (<c>Scene+0x40/+0x44/+0x48</c>,
    /// read off <c>Scene::SetSkyAlpha</c> @0x55f100): <see cref="SceneSkyT"/> is the blend 0..1,
    /// <see cref="SceneSkyCur"/> the mode being shown and <see cref="SceneSkyDesired"/> the one being faded
    /// to. Written by seam-scene's sky slots, read by the sky pass.</summary>
    public static readonly float[] SceneSkyT = new float[MaxScenes];
    /// <inheritdoc cref="SceneSkyT"/>
    public static readonly int[] SceneSkyCur = new int[MaxScenes];
    /// <inheritdoc cref="SceneSkyT"/>
    public static readonly int[] SceneSkyDesired = new int[MaxScenes];

    /// <summary>Skybox object <paramref name="slot"/> of <paramref name="scene"/> (0..3).</summary>
    public static DbId SceneSky(DbId scene, int slot)
    {
        int i = scene.Index;
        if ((uint)i >= MaxScenes) return default;
        return slot switch { 0 => SceneSky0[i], 1 => SceneSky1[i], 2 => SceneSky2[i], 3 => SceneSky3[i], _ => default };
    }

    /// <summary>Write skybox object <paramref name="slot"/> of <paramref name="scene"/>.</summary>
    public static void SetSceneSky(DbId scene, int slot, DbId o)
    {
        int i = scene.Index;
        if ((uint)i >= MaxScenes) return;
        switch (slot)
        {
            case 0: SceneSky0[i] = o; break;
            case 1: SceneSky1[i] = o; break;
            case 2: SceneSky2[i] = o; break;
            case 3: SceneSky3[i] = o; break;
        }
    }

    /// <summary>The scene's highlight set as a per-object bit, which is what the glow and
    /// outline passes read. A set rather than a walk: the engine asked each gob a flag, and the pass wants the
    /// handful that answered yes.</summary>
    public static readonly DbId[] SceneHighlight = new DbId[MaxScenes * MaxHighlight];
    public static readonly int[] SceneHighlightN = new int[MaxScenes];
    /// <summary>Objects one scene may highlight at once. The engine highlights a selection and a hover, so
    /// this is generous; past it the add is refused and counted.</summary>
    public const int MaxHighlight = 64;

    /// <summary>Add <paramref name="o"/> to <paramref name="scene"/>'s highlight set; false when it is full
    /// (refused loudly by the caller, never dropped quietly) or already in it.</summary>
    public static bool Highlight(DbId scene, DbId o)
    {
        int s = scene.Index;
        if ((uint)s >= MaxScenes) return false;
        int n = SceneHighlightN[s];
        for (int k = 0; k < n; k++) if (SceneHighlight[s * MaxHighlight + k].Value == o.Value) return false;
        if (n >= MaxHighlight) return false;
        SceneHighlight[s * MaxHighlight + n] = o;
        SceneHighlightN[s] = n + 1;
        return true;
    }

    /// <summary>Drop <paramref name="o"/> from <paramref name="scene"/>'s highlight set.</summary>
    public static void Unhighlight(DbId scene, DbId o)
    {
        int s = scene.Index;
        if ((uint)s >= MaxScenes) return;
        int n = SceneHighlightN[s];
        for (int k = 0; k < n; k++)
            if (SceneHighlight[s * MaxHighlight + k].Value == o.Value)
            { SceneHighlight[s * MaxHighlight + k] = SceneHighlight[s * MaxHighlight + n - 1]; SceneHighlightN[s] = n - 1; return; }
    }

    /// <summary>Mark part <paramref name="p"/> (an absolute index into <see cref="Parts"/>) dirty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TouchPart(int p, Dirty d) { if ((uint)p < MaxParts && Parts.LiveAt(p)) Parts.Mark(p, d); }
}
/// <summary>A scene-sided ordered list of handles -- the member list, the highlight list. Grown on the game
/// thread at a cold edge (an object joins or leaves a scene, the game reorders a draw), never per frame, so a
/// plain array with a count is the whole structure. Not a <see cref="DbTable"/>: there is no handle TO a list.</summary>
public sealed class DbList
{
    private DbId[] _a = new DbId[16];
    private Vector4[]? _aux;
    public int Count { get; private set; }

    public DbId this[int i] => _a[i];
    /// <summary>The per-entry extra a highlight carries (its colour); zero-length when the list has none.</summary>
    public Vector4 Aux(int i) => _aux != null ? _aux[i] : default;

    public int IndexOf(DbId id)
    {
        for (int i = 0; i < Count; i++) if (_a[i] == id) return i;
        return -1;
    }

    public void Add(DbId id, Vector4 aux = default, bool withAux = false)
    {
        if (Count == _a.Length)
        {
            Array.Resize(ref _a, _a.Length * 2);
            if (_aux != null) Array.Resize(ref _aux, _a.Length);
        }
        if (withAux) { _aux ??= new Vector4[_a.Length]; _aux[Count] = aux; }
        _a[Count++] = id;
    }

    public bool Remove(DbId id)
    {
        int at = IndexOf(id);
        if (at < 0) return false;
        RemoveAt(at);
        return true;
    }

    public void RemoveAt(int at)
    {
        for (int i = at; i < Count - 1; i++)
        {
            _a[i] = _a[i + 1];
            if (_aux != null) _aux[i] = _aux[i + 1];
        }
        Count--;
    }

    /// <summary>Move <paramref name="id"/> to position <paramref name="to"/> (clamped), keeping every other
    /// entry's relative order: what the four <c>RenderGob*</c> slots do to the draw order.
    /// <paramref name="to"/> is an index in the list AS IT IS NOW -- taking the element out shifts everything
    /// after it down one, so a target past its current position is corrected here, once.</summary>
    public void MoveTo(DbId id, int to)
    {
        int at = IndexOf(id);
        if (at < 0) return;
        if (to > at) to--;
        Vector4 aux = _aux != null ? _aux[at] : default;
        RemoveAt(at);
        if (to > Count) to = Count;
        if (to < 0) to = 0;
        if (Count == _a.Length)
        {
            Array.Resize(ref _a, _a.Length * 2);
            if (_aux != null) Array.Resize(ref _aux, _a.Length);
        }
        for (int i = Count; i > to; i--)
        {
            _a[i] = _a[i - 1];
            if (_aux != null) _aux[i] = _aux[i - 1];
        }
        _a[to] = id;
        if (_aux != null) _aux[to] = aux;
        Count++;
    }

    public void Clear() => Count = 0;
}

/// <summary>One scene's debug line set, exactly as <c>Scene::AddLinesToRender(List&lt;Vector&gt;,
/// List&lt;unsigned int&gt;, List&lt;int&gt;)</c> hands it over: the points, one packed colour per point, and
/// the point count of each strip. Cleared by <c>ClearLinesToRender</c>.</summary>
public sealed unsafe class DbLines
{
    public Vector3[] Points = new Vector3[0];
    public uint[] Colors = new uint[0];
    public int[] Strips = new int[0];
    public int PointCount, StripCount;

    public void Clear() { PointCount = 0; StripCount = 0; }

    public void Set(Vector3* pts, uint* cols, int* strips, int nPts, int nStrips)
    {
        if (Points.Length < nPts) { Points = new Vector3[nPts]; Colors = new uint[nPts]; }
        if (Strips.Length < nStrips) Strips = new int[nStrips];
        for (int i = 0; i < nPts; i++) { Points[i] = pts[i]; Colors[i] = cols != null ? cols[i] : 0xFFFFFFFFu; }
        for (int i = 0; i < nStrips; i++) Strips[i] = strips[i];
        PointCount = nPts; StripCount = nStrips;
    }
}

/// <summary>What a part instances. One per <see cref="NodeKind"/> the renderer treats differently, plus
/// <see cref="Merged"/> for a tile mesh <c>CombineParts</c> folded into another part's geometry.</summary>
public enum PartKind : byte
{
    Dummy, Mesh, Skin, Dangly, AnimMesh, Emitter, Light, Reference, Aabb, Camera, Trigger, Merged,
}

/// <summary>A part's material terms: what <c>PartInitMaterial</c> hands the draw, folded with the
/// <c>.mtr</c>. A value record in a column, not an object per part.</summary>
public struct PartMaterial
{
    public Vector3 Diffuse, Ambient, Specular;
    public float Shininess;
    /// <summary>The file's <c>transparencyhint</c> and <c>tilefade</c> integers, kept verbatim.</summary>
    public int TransparencyHint, TileFade;
    /// <summary>The EE <c>renderhint</c> the engine's <c>SharedMaterial::ExtractRenderHint</c> maps to; 0 is
    /// none.</summary>
    public int RenderHint;
    public bool Render, Shadow, Beaming, Lightmapped, RotateTexture;
}

/// <summary>The object facts the draw answers without dereferencing a <c>Gob</c>.</summary>
[Flags]
public enum ObjFlag : ushort
{
    None = 0,
    /// <summary>A creature, or a part of one (a head, a held weapon, a hat). The receiver class the record's
    /// <c>emi.w</c> carries, and what the engine path answered by climbing the attachment chain per part.</summary>
    Creature = 1 << 0,
    /// <summary>Tile geometry: receiver class 1, and the population the automatic LOD and the normal weld skip.</summary>
    Tile = 1 << 1,
    /// <summary>Never moves once placed, so its records are filed in the visible-set index rather than routed
    /// every frame.</summary>
    Static = 1 << 2,
    /// <summary><c>GobRenderLast</c>: draws after the rest of its class.
    /// <para>ponytail: DECLARED, not honoured. Alloy sorts each class by (texture, feature, VAO) and a
    /// per-object "last" would be a fifth radix digit; the vanilla uses are a handful of VFX. The upgrade is
    /// that digit, above the texture one, not a second draw list.</para></summary>
    RenderLast = 1 << 3,
    /// <summary><c>AurDisableShadowing</c>: casts nothing. Read by the caster admission.</summary>
    NoShadow = 1 << 4,
    /// <summary>In the scene's highlight set (the glow/outline pass's object set).</summary>
    Highlight = 1 << 5,
    /// <summary>A GUI control's model object: in a GUI panel's scene rather than the world.</summary>
    Gui = 1 << 6,
    /// <summary>One of the scene's four skybox objects. The engine marks the gob itself (<c>obj+0x18 = 1</c>,
    /// set by <c>Scene::SetSkyBox</c> @0x55e260), because the sky is drawn by the sky pass and must not also
    /// be drawn as ordinary geometry. seam-scene's <c>SetSkyBox</c> slot sets it.</summary>
    Sky = 1 << 7,
    /// <summary>Painted in the scene's FOG COLOUR at full alpha rather than in its own texture's colour --
    /// the engine's <c>obj+0x19</c>, which <c>Scene::SetSkyFade</c> @0x55e2b0 sets on the object it is handed
    /// and <c>PartTriMesh::Mat</c> @0x450960 reads. It is what makes vanilla's horizon band darken at
    /// night.</summary>
    FogTint = 1 << 8,
}
