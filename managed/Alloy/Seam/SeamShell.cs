using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The engine field offsets a facade shell must reproduce, and the shell size per kind: the sizes are
/// the <c>operator new</c> argument of the engine's own factory (<c>NewCAurObject</c> @0x5412c0,
/// <c>NewCAurScene</c> @0x572df0, <c>NewCAurString</c> @0x594050, <c>NewCAurCamera</c> @0x5a62d0), the offsets
/// the ones game code reads without a vtable. A shell is full engine size: a stray write lands inside it.</summary>
public static class SeamLayout
{
    /// <summary>sizeof(Gob): <c>NewCAurObject</c> @0x5412c0 <c>this = operator_new(0x4c0)</c>.</summary>
    public static readonly int GobSize = EngineLayout.SeamGobSizeof;
    /// <summary>sizeof(Scene): <c>NewCAurScene</c> @0x572df0 <c>this = operator_new(0x518)</c>.</summary>
    public static readonly int SceneSize = EngineLayout.SeamSceneSizeof;
    /// <summary>sizeof(StringGob): <c>NewCAurString</c> @0x594050 <c>operator_new(0x558)</c>, and it returns
    /// <c>this + 0x4c0</c> -- the <c>CAurString</c> sub-object, i.e. sizeof(Gob) exactly.</summary>
    public static readonly int StringGobSize = EngineLayout.SeamStringGobSizeof;
    /// <summary>sizeof(Camera): <c>NewCAurCamera</c> @0x5a62d0 <c>this = operator_new(0xe68)</c> on Linux, and
    /// rva 0x59f8c's <c>mov $0xe70,%ecx</c> on Windows -- the ONE class in this set whose size differs per OS.
    /// The shell takes the larger on both: the Gob sub-object is at +8 either way, so eight bytes of slack
    /// past the end costs nothing and removes a per-OS constant from every caller.</summary>
    public static readonly int CameraSize = EngineLayout.SeamCameraSizeof;

    // ---- Gob words the game reaches without a vtable ----
    /// <summary>`SafePointerList&lt;CAurObject&gt;`: array pointer, then count and capacity as two ints.
    /// The GAME mutates these in place (`SafePointer::operator=` @0x477d70 inlined in
    /// `CGuiDragSource::HandleMouseMoveCaptured` and friends), including the `realloc`.</summary>
    public static readonly int GobSafeList = EngineLayout.GobSafePointers;
    public static readonly int GobSafeCount = EngineLayout.GobSafePointerCount;
    public static readonly int GobSafeCap = EngineLayout.SeamGobSafeCap;
    /// <summary>Inline name array; `Gob::GetName` @0x444660 is `return this + 0x24`, and `Gob::~Gob` copies it
    /// with a 0x100 bound, so 0x24..0x87 is the name's room.</summary>
    public static readonly int GobName = EngineLayout.SeamGobName;
    public const int GobNameMax = 0x40;   // not-an-offset: the length of the inline char array at Gob+0x24, not a place in it
    /// <summary>`Gob::GetScene` @0x4447b0 `return *(Scene**)(this+0x88)`; read raw by `GuiAurAttachToObject`.</summary>
    public static readonly int GobScene = EngineLayout.GobScene;
    /// <summary>Root `Part*` of LOD 0; `AurEnableShadowing` @0x461ad0 reads it raw. `NewCAurObject` fills
    /// +0x90/+0x98/+0xa0 with one instance tree per LOD model.</summary>
    public static readonly int GobRoot = EngineLayout.GobPartTree;
    public static readonly int GobRootLod1 = EngineLayout.GobLodRoot1;
    public static readonly int GobRootLod2 = EngineLayout.GobLodRoot2;
    /// <summary>Position `Vector` (3 floats) and orientation `Quaternion` (4 floats); the game hands their
    /// INTERIOR ADDRESSES to `Part::GetWorld` for the engine to fill, so they must be real storage.</summary>
    public static readonly int GobPos = EngineLayout.GobPosition;
    public static readonly int GobRot = EngineLayout.GobRot;
    /// <summary>`Gob::GetBehavior` @0x5371e0 / `Gob::SetScene` @0x43e020; compared against `this` by the
    /// game's own `CAurBehavior*::Control` bodies.</summary>
    public static readonly int GobBehavior = EngineLayout.GobAttach;
    /// <summary>`Gob::GetExternalData` @0x444700 -- the owning `CNWC*`/`CGui*`; `CGuiMan::GuiObjectHitCheck`
    /// inlines the read behind a devirtualisation guard.</summary>
    public static readonly int GobExternal = EngineLayout.GobExternalData;
    /// <summary>`Gob::GetExternalDataType` @0x444720: the int beside it.</summary>
    public static readonly int GobExternalType = EngineLayout.GobExternalType;
    /// <summary>LOD switch distances `NewCAurObject` parses out of the model's LOD resource and stores at
    /// +0x444/+0x448, with +0x440 the selected level.</summary>
    public static readonly int GobLodLevel = EngineLayout.GobLodLevel;
    public static readonly int GobLodDist1 = EngineLayout.GobLodNear;
    public static readonly int GobLodDist2 = EngineLayout.GobLodFar;
    /// <summary>`Gob::IsInViewVolume` @0x444780 is one byte at +0x1b; `EnableInViewVolume` @0x436f10 sets it
    /// AND fires `DoEvent("propagateviewvolume")`, `Disable` @0x436f30 only clears it. The ctor leaves it 1, so
    /// a new object claims to be in view until the first cull says otherwise.</summary>
    public static readonly int GobInView = EngineLayout.GobInView;
    /// <summary>`Gob::SetPropagateHighlightEffects` @0x444770, one byte; the ctor leaves it 1.</summary>
    public static readonly int GobPropagateHighlight = EngineLayout.GobPropagateHilite;
    /// <summary>The shadow flag `TurnOnShadows`/`TurnOffShadows` @0x444750/@0x444740 write as a 16-bit unit,
    /// so the byte at +0xf5 is cleared with it. On by default.</summary>
    public static readonly int GobShadows = EngineLayout.SeamGobShadows;
    /// <summary>`Gob::SetColorShifting` @0x439040: a 16-bit enable at +0xf6, the colour at +0xfc (x, y) and
    /// +0x104 (z), and the alpha at +0x108. The trimesh draw multiplies the colour into the material diffuse
    /// and the alpha into the vertex alpha, and the opacity classification and the shadow-alpha cutoff both
    /// read the same two words.</summary>
    public static readonly int GobColourShiftFlag = EngineLayout.GobColourMul;
    public static readonly int GobColourShift = EngineLayout.GobColour;
    public static readonly int GobColourAlpha = EngineLayout.GobAlpha;
    /// <summary>`Gob::SetIllumination` @0x4390a0: a 16-bit enable at +0xf8 and the colour at +0x110 (x, y) /
    /// +0x118 (z). The draw ADDS it into the ambient terms rather than multiplying.</summary>
    public static readonly int GobIllumFlag = EngineLayout.GobSelfIllum;
    public static readonly int GobIllum = EngineLayout.GobEmissive;
    /// <summary>The cached bounds the five bounding slots read: box min at +0x160, max at +0x16c, sphere
    /// radius at +0x178 and centre at +0x17c. Only the two void `Recompute` slots write them, which is why
    /// `NewCAurObject` calls both at construction.</summary>
    public static readonly int GobBoundsMin = EngineLayout.GobBoundsMin;
    public static readonly int GobBoundsMax = EngineLayout.GobBoundsMax;
    public static readonly int GobSphereRadius = EngineLayout.GobRadius;
    public static readonly int GobSphereCentre = EngineLayout.GobCentre;
    /// <summary>`Gob::SetRenderPersonalSpace` @0x4447c0 (+0x198) and `Gob::SetWind` @0x436f40 (+0x199 flag,
    /// +0x1a0 strength) -- two independent stores, nothing else.</summary>
    public static readonly int GobPersonalSpace = EngineLayout.SeamGobPersonalSpace;
    public static readonly int GobWindFlag = EngineLayout.GobWindSource;
    public static readonly int GobWindStrength = EngineLayout.GobWindStrength;
    /// <summary>`Gob::ProcessGob` @0x43e920 recomputes "the tree has geometry" into +0x1a4 when the dirty bit
    /// at +0x1a5 (`Gob::SetProcessFlag` @0x43e970) is set; `Gob::Render` gates on +0x1a4.</summary>
    public static readonly int GobHasGeometry = EngineLayout.GobVisible;
    public static readonly int GobProcessFlag = EngineLayout.GobGeomDirty;
    /// <summary>The `CExoArrayList&lt;CallBack*&gt;` behind `Gob::RegisterCallBack` @0x497db0: ptr at +0x460,
    /// count at +0x468, capacity at +0x46c. A `CallBack` is 0x30 bytes -- `char name[0x20]`, the function
    /// pointer at +0x20, the user data at +0x28.</summary>
    public static readonly int GobCallbackList = EngineLayout.GobCallbacks;
    public static readonly int GobCallbackCount = EngineLayout.GobCallbackCount;
    public static readonly int GobCallbackCap = EngineLayout.SeamGobCallbackCap;
    /// <summary>The embedded `CAurObjectVisualTransformData`: ten `LerpFloat`s of 0x40 bytes
    /// (`Gob::GetVisualTransformData` @0x436e70 is `return this + 0x1c0`).</summary>
    public static readonly int GobVisualXform = EngineLayout.GobScaleLerp;
    public const int GobVisualXformSize = 0x280;   // not-an-offset: sizeof(CAurObjectVisualTransformData), ten LerpFloats

    /// <summary>`EnableForceAnimations` @0x436e80 / `IsForceAnimated` @0x436ea0 -- one byte.</summary>
    public static readonly int GobForceAnim = EngineLayout.SeamGobForceAnim;
    /// <summary>`EnableAnimations` @0x436eb0 / `IsAnimating` @0x436ed0 -- the SAME byte, so "is animating" is
    /// the engine's name for "animations are enabled".</summary>
    public static readonly int GobAnimEnabled = EngineLayout.SeamGobAnimEnabled;
    /// <summary>`SetSynchronizeAnimations` @0x4447d0.</summary>
    public static readonly int GobSyncAnim = EngineLayout.GobSyncHier;

    /// <summary>The attachment `CExoArrayList&lt;Gob*&gt;`: `Gob::AddAttachment` @0x5424a0 and
    /// `Gob::GetAttachedGobs` @0x539350 read and REALLOC it in place without a vtable, so it has to be real
    /// storage at the engine's offsets. The engine stops adding past 16 entries.</summary>
    public static readonly int GobAttachList = EngineLayout.SeamGobAttachList;
    public static readonly int GobAttachCount = EngineLayout.SeamGobAttachCount;
    public static readonly int GobAttachCap = EngineLayout.SeamGobAttachCap;
    /// <summary>The two scale channels `Gob::ScaleObject` @0x53efd0 writes (`type` 0 or 1 only) and the final
    /// product `CalculateFinalScale` leaves at +0x49c, which `Gob::GetAdjustedScale` @0x537cf0 reads.</summary>
    public static readonly int GobScaleFinal = EngineLayout.GobScaleBase;
    public static readonly int GobScaleChannel = EngineLayout.SeamGobScaleChannel;
    /// <summary>Total tri-mesh area; `Gob::GetRandomPart` @0x53d4c0 treats -1 as "not computed yet".</summary>
    public static readonly int GobTriMeshArea = EngineLayout.SeamGobTriMeshArea;
    /// <summary>The `CAurString` sub-object's vptr inside a `StringGob`: `RemoveAuroraString` @0x58b2b0
    /// recovers the `Gob` by subtracting 0x4c0 from its argument.</summary>
    public static readonly int StringGobString = EngineLayout.SeamStringGobStringSub;

    // ---- Scene words (seam-scene's columns; the offsets are the machinery's) ----
    public static readonly int SceneSafeList = EngineLayout.SeamSceneSafeList;
    /// <summary>The SCENE's own measured row, not the <c>Gob</c>'s: both are 0x10 on this build, and a row
    /// that happens to be equal is still the wrong row to read.</summary>
    public static readonly int SceneSafeCount = EngineLayout.SeamSceneSafeCount;
    public static readonly int SceneSafeCap = EngineLayout.SeamSceneSafeCap;
    /// <summary>`GetAurSceneObjectCount` @0x58b380 is `return *(u32*)(scene + 0x138)` -- there is no vtable
    /// getter, so the count must be a live word.</summary>
    public static readonly int SceneObjList = EngineLayout.SceneGobs;
    public static readonly int SceneObjCount = EngineLayout.SceneGobCount;
    /// <summary>`CAurScene::SetAmbient` @0x461ab0 writes +0x168/+0x170 non-virtually.</summary>
    public static readonly int SceneAmbient = EngineLayout.SeamSceneAmbient;
    public static readonly int SceneAmbient2 = EngineLayout.SeamSceneAmbient2;
    /// <summary>`CAurObject::AnimateExternal` @0x43a560 increments it before and after the `Animate` vcall.</summary>
    public static readonly int SceneExternalAnimate = EngineLayout.SceneAnimFrame;

    // ---- Camera: the CAurCamera vptr at +0, the Gob sub-object at +8 ----
    /// <summary>`GuiAttachViewport` @0x58bda0 adjusts a `CAurCamera*` to its `CAurObject*` with a literal +8.</summary>
    public static readonly int CameraGobSub = EngineLayout.SeamCameraGobSub;

    /// <summary>The shell size for <paramref name="kind"/>.</summary>
    public static int Size(FacadeKind kind) => kind switch
    {
        FacadeKind.Object => GobSize,
        FacadeKind.String => StringGobSize,
        FacadeKind.Scene => SceneSize,
        FacadeKind.Camera => CameraSize,
        _ => 0,
    };

    /// <summary>The primary table whose vptr goes at the shell's +0.</summary>
    public static SeamTable PrimaryTable(FacadeKind kind) => kind switch
    {
        FacadeKind.Object => SeamTable.Gob,
        FacadeKind.String => SeamTable.StringGob,
        FacadeKind.Scene => SeamTable.Scene,
        _ => SeamTable.Camera,
    };

    /// <summary>Where the SafePointer back-reference list sits for <paramref name="kind"/> -- the `Gob`'s own
    /// for an object or a text object, the `Scene`'s, and the camera's `Gob` sub-object's (so +8 further on).</summary>
    public static int SafeListAt(FacadeKind kind) => kind switch
    {
        FacadeKind.Camera => CameraGobSub + GobSafeList,
        _ => GobSafeList,
    };
}

/// <summary>CX's own words, in the block IMMEDIATELY BEFORE the pointer the game holds. A `DbId` has to live
/// somewhere the game never reads, and the only place that is true by construction rather than by absence of
/// evidence is outside the engine's layout altogether: every offset from 0 to the shell's size belongs to the
/// engine's class, and the RE can only ever show that game code has not been SEEN to read one.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SeamHeader
{
    /// <summary>`CXFACADE` as 8 bytes: the test that a pointer handed to a slot is one of ours at all.</summary>
    public ulong Magic;
    public uint Id;
    public byte Kind;
    public byte State;
    /// <summary><see cref="SeamShell.InGui"/> / <see cref="SeamShell.InWorld"/>: the kinds of scene this
    /// shell's object has joined, sticky for the shell's life.</summary>
    public byte Scenes;
    public byte Pad;
    /// <summary>The frame <see cref="SeamShell.Retire"/> ran, for the quarantine.</summary>
    public uint RetiredAt;
    /// <summary>Bit per logical slot group already named in a use-after-free line, so the poison path logs
    /// once per slot and not once per call.</summary>
    public uint Reported;
    /// <summary>The SafePointer array pointer we last saw, to tell our allocation from a game `realloc`.</summary>
    public nint SafeArray;
    /// <summary>The interned <see cref="SeamCallers"/> id of the game function that destroyed this shell, so a
    /// later call on it can name BOTH ends of the dangle.</summary>
    public int RetiredBy;
    public int Pad2;
    public nint Reserved1;
}

/// <summary>Allocation, retire and quarantine of facade shells (ADR 0028 section 2). A shell is a native
/// block: CX's <see cref="SeamHeader"/> then the engine-sized shell, and the pointer the game holds is the
/// shell base. Retired blocks go to a per-kind quarantine ring, reused only after <see cref="QuarantineFrames"/>
/// published frames AND only once the ring is full, so a use-after-free lands on a block that still says RETIRED.</summary>
public static unsafe class SeamShell
{
    private const ulong Magic = 0x4544414341465843;   // not-an-offset: "CXFACADE" little-endian
    internal const byte StateLive = 1, StateRetired = 2;
    internal const byte InGui = 1, InWorld = 2;
    /// <summary>Published frames a retired block is kept poisoned before it may be reused. Measured: the game
    /// re-attaches a pointer to a destroyed object about 900 frames later (a panel closed, then reopened), so
    /// the floor sits above that -- recycle sooner and the call lands on a live unrelated record, i.e. silent
    /// corruption instead of a named line. <see cref="RingSize"/> is the hard cap on memory.</summary>
    public const int QuarantineFrames = 1800;
    /// <summary>Retired blocks kept per kind before the oldest is reused. 1024 shells of the largest kind is
    /// ~4 MB held at worst, which buys a detection window of a whole area's worth of destroys.</summary>
    internal const int RingSize = 1024;

    internal static readonly int HeaderSize = (sizeof(SeamHeader) + 15) & ~15;

    private static readonly nint[,] _ring = new nint[4, RingSize];
    private static readonly int[] _ringHead = new int[4], _ringTail = new int[4], _ringLive = new int[4];

    /// <summary>Shells that retired while the ring was full of blocks still inside their quarantine. Held,
    /// never dropped: the alternative is freeing a block the game may still call through.</summary>
    public static long Overflowed { get; private set; }
    private static System.Collections.Generic.List<nint>? _overflow;
    private static readonly System.Collections.Generic.List<nint> _held = new();

    /// <summary>Retired shells whose object was ever in a GUI scene. GUI code calls through pointers to
    /// objects it already destroyed, so such a block is never reused or freed: no later object, world or GUI,
    /// can sit at its address. ponytail: held for the session; a GUI-only pool needs the minter's origin.</summary>
    public static int Held => _held.Count;

    /// <summary>The seam's own frame counter, advanced once per published frame by <see cref="Frame"/>.</summary>
    public static uint FrameNo { get; private set; }

    /// <summary>Shells alive, retired-and-quarantined, and allocation/reuse counts, for the period line.</summary>
    public static int Live { get; private set; }
    public static int Quarantined { get; private set; }
    public static long Allocated { get; private set; }
    public static long Reused { get; private set; }
    /// <summary>Back-reference arrays the game `realloc`ed onto its own heap and we could not free
    /// (Windows: nwmain statically links its CRT and exports no `free`). Counted, never silent.
    /// ponytail: an `rva` row for the static CRT's `free` in the build file would close it.</summary>
    public static long ForeignArraysLeaked { get; private set; }

    /// <summary>Advance the quarantine clock: called once per published frame.</summary>
    public static void Frame()
    {
        FrameNo++;
        PartShell.Drain();
        if (_overflow == null || _overflow.Count == 0) return;
        for (int i = _overflow.Count - 1; i >= 0; i--)
        {
            nint b = _overflow[i];
            if (FrameNo - Head(b)->RetiredAt < QuarantineFrames) continue;
            _overflow.RemoveAt(i);
            Quarantined--;
            NativeMemory.AlignedFree((void*)(b - HeaderSize));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SeamHeader* Head(nint shell) => (SeamHeader*)(shell - HeaderSize);

    /// <summary>A zeroed shell of <paramref name="kind"/> carrying <paramref name="id"/>, with its primary
    /// vptr set; 0 when the block cannot be had (the caller logs and refuses). The SafePointer list is left
    /// NULL/0/0 deliberately: the game's own `realloc(NULL, 0x80)` then allocates it on the heap every later
    /// `realloc` of it will come from, which is the only arrangement that is correct on both OSes.</summary>
    public static nint Alloc(FacadeKind kind, DbId id)
    {
        int size = SeamLayout.Size(kind);
        if (size == 0) return 0;
        int k = (int)kind;
        nint shell = 0;
        if (_ringLive[k] == RingSize)
        {
            nint candidate = _ring[k, _ringTail[k]];
            SeamHeader* h = Head(candidate);
            if (FrameNo - h->RetiredAt >= QuarantineFrames)
            {
                _ring[k, _ringTail[k]] = 0;
                _ringTail[k] = (_ringTail[k] + 1) % RingSize;
                _ringLive[k]--;
                Quarantined--;
                shell = candidate;
                Reused++;
            }
        }
        if (shell == 0)
        {
            void* block = NativeMemory.AlignedAlloc((nuint)(HeaderSize + size), 16);
            if (block == null) return 0;
            shell = (nint)block + HeaderSize;
            Allocated++;
        }
        NativeMemory.Fill((void*)(shell - HeaderSize), (nuint)(HeaderSize + size), 0);
        SeamHeader* head = Head(shell);
        head->Magic = Magic;
        head->Id = id.Value;
        head->Kind = (byte)kind;
        head->State = StateLive;
        *(nint*)shell = Facade.Table(SeamLayout.PrimaryTable(kind));
        if (kind == FacadeKind.String)
            *(nint*)(shell + SeamLayout.StringGobString) = Facade.Table(SeamTable.CAurString);
        else if (kind == FacadeKind.Camera)
            *(nint*)(shell + SeamLayout.CameraGobSub) = Facade.Table(SeamTable.CameraGob);
        Live++;
        return shell;
    }

    /// <summary>Record that <paramref name="shell"/>'s object joined a world or a GUI scene. True when a shell
    /// that was in a world scene joins a GUI one for the first time; false for a block that is not ours.</summary>
    internal static bool NoteScene(nint shell, bool world)
    {
        if (shell == 0) return false;
        SeamHeader* h = Head(shell);
        if (h->Magic != Magic) return false;
        byte was = h->Scenes;
        h->Scenes |= world ? InWorld : InGui;
        return !world && (was & (InWorld | InGui)) == InWorld;
    }

    /// <summary>Mark <paramref name="shell"/> retired and queue it for reuse after the quarantine, or hold it for
    /// good when its object was ever in a GUI scene (<see cref="Held"/>). The caller has already nulled the
    /// SafePointer back-references (<see cref="Facade.Retire"/>); this only changes the block's state, so every
    /// later call through its vtable takes the poison path.</summary>
    public static void Retire(nint shell)
    {
        SeamHeader* h = Head(shell);
        if (h->Magic != Magic || h->State != StateLive) return;
        h->State = StateRetired;
        h->RetiredAt = FrameNo;
        h->Reported = 0;
        h->RetiredBy = SeamCallers.Here();
        Live--;
        if ((h->Scenes & InGui) != 0) { _held.Add(shell); return; }
        int k = h->Kind & 3;
        if (_ringLive[k] == RingSize)
        {
            // The ring is full, so the oldest has to go. Freeing it is only safe once the quarantine has
            // elapsed FOR IT; when it has not, the ring is full of blocks younger than two frames, which
            // means more than 1024 shells died in two frames. Then the ring GROWS rather than dropping one
            // on the floor -- the earlier version wrote the evicted block back at the head and the next line
            // overwrote that same index with the new shell, leaking it.
            nint evicted = _ring[k, _ringTail[k]];
            if (FrameNo - Head(evicted)->RetiredAt >= QuarantineFrames)
            {
                _ring[k, _ringTail[k]] = 0;
                _ringTail[k] = (_ringTail[k] + 1) % RingSize;
                _ringLive[k]--;
                Quarantined--;
                NativeMemory.AlignedFree((void*)(evicted - HeaderSize));
            }
            else
            {
                Overflowed++;
                if ((Overflowed & (Overflowed - 1)) == 0)
                    Facade.Log?.Always($"seam: more than {RingSize} {(FacadeKind)k} shells retired inside the "
                                     + $"quarantine window -- holding {Overflowed} extra rather than freeing one early");
                _overflow ??= new System.Collections.Generic.List<nint>();
                _overflow.Add(shell);
                Quarantined++;
                return;
            }
        }
        _ring[k, _ringHead[k]] = shell;
        _ringHead[k] = (_ringHead[k] + 1) % RingSize;
        _ringLive[k]++;
        Quarantined++;
    }

    /// <summary>Is <paramref name="shell"/> one of ours, and live? <paramref name="state"/> tells a retired
    /// block (the poison path) from a pointer that was never a facade at all.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Known(nint shell, out byte state, out DbId id)
    {
        state = 0;
        id = default;
        if (shell == 0) return false;
        SeamHeader* h = Head(shell);
        if (h->Magic != Magic) return false;
        state = h->State;
        id = new DbId(h->Id);
        return true;
    }

    /// <summary>True the first time <paramref name="logical"/>'s group is named on this shell, so a poison
    /// line prints once per slot and not once per call. 32 groups of logical slots share the word.</summary>
    internal static bool FirstReport(nint shell, int logical)
    {
        SeamHeader* h = Head(shell);
        uint bit = 1u << (logical & 31);
        if ((h->Reported & bit) != 0) return false;
        h->Reported |= bit;
        return true;
    }

    internal static void NoteForeignArray() => ForeignArraysLeaked++;

    /// <summary>Hand every block back at shutdown; the quarantine ring is the only owner left.</summary>
    internal static void DropAll()
    {
        for (int k = 0; k < 4; k++)
        {
            while (_ringLive[k] > 0)
            {
                nint b = _ring[k, _ringTail[k]];
                _ring[k, _ringTail[k]] = 0;
                _ringTail[k] = (_ringTail[k] + 1) % RingSize;
                _ringLive[k]--;
                if (b != 0) NativeMemory.AlignedFree((void*)(b - HeaderSize));
            }
            _ringHead[k] = 0;
        }
        foreach (nint b in _held) NativeMemory.AlignedFree((void*)(b - HeaderSize));
        _held.Clear();
        Quarantined = 0;
    }
}
