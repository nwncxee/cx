using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
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

    private static readonly nint[,] _ring = new nint[4, RingSize];
    private static readonly int[] _ringHead = new int[4], _ringTail = new int[4], _ringLive = new int[4];

    /// <summary>Shells that retired while the ring was full of blocks still inside their quarantine. Held,
    /// never dropped: the alternative is freeing a block the game may still call through.</summary>
    public static long Overflowed { get; private set; }
    private static System.Collections.Generic.List<nint>? _overflow;
    /// <summary>The blocks <see cref="Held"/> counts, owned until <see cref="DropAll"/>.</summary>
    internal sealed class HeldState
    {
        internal readonly System.Collections.Generic.List<nint> Blocks = new();
        internal long Unmarked;
        internal readonly System.Collections.Generic.HashSet<int> Destroyers = new();
    }
    private static HeldState _held = new();

    /// <summary>Retired shells whose object was ever in a GUI scene or never in any, and scene shells never
    /// stamped a world scene. GUI code calls through pointers to things it already destroyed, so such a block is never reused
    /// or freed: nothing later, world or GUI, can sit at its address. ponytail: held for the session; a GUI-only pool needs the minter's origin.</summary>
    public static int Held => _held.Blocks.Count;

    /// <summary>Object, text and camera shells retired before their object joined any scene; held with the GUI
    /// ones, since nothing says a GUI pointer does not still reach them.</summary>
    public static long Unmarked => _held.Unmarked;

    /// <summary>Held shells at which a held set is named loudly, and again at every doubling: about five times
    /// what one short session reached. Only a log line; a held shell is never freed.</summary>
    private const long HeldWarnFloor = 4096;

    /// <summary>True when a held set that just reached <paramref name="count"/> should be named.</summary>
    internal static bool HeldRunaway(long count) => count >= HeldWarnFloor && (count & (count - 1)) == 0;

    /// <summary>Name a held set that keeps growing, once per doubling past <see cref="HeldWarnFloor"/>.</summary>
    internal static void NoteHeld(string what, long count)
    {
        if (HeldRunaway(count) && !Facade.InSelfCheck)
            Facade.Log?.Always($"seam: {count} {what} held for the session and never freed or reused -- if this "
                             + "keeps doubling, the held set is growing without levelling off");
    }

    /// <summary>Distinct destroying game functions <see cref="NoteUnmarked"/> names per session; the count keeps going.
    /// Linux only: on Windows the destroyer is unnamed, so one line is written per session.</summary>
    private const int UnmarkedLines = 64;

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
            NativeMemory.AlignedFree(Headroom<SeamHeader>.BlockOf(b));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SeamHeader* Head(nint shell) => Headroom<SeamHeader>.Header(shell);

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
            void* block = NativeMemory.AlignedAlloc((nuint)(Headroom<SeamHeader>.Size + size), 16);
            if (block == null) return 0;
            shell = Headroom<SeamHeader>.PayloadOf(block);
            Allocated++;
        }
        NativeMemory.Fill(Headroom<SeamHeader>.BlockOf(shell), (nuint)(Headroom<SeamHeader>.Size + size), 0);
        SeamHeader* head = Head(shell);
        head->Magic = Magic;
        head->Id = id.Value;
        head->Kind = (byte)kind;
        head->State = StateLive;
        *(nint*)shell = Facade.Table(SeamLayout.PrimaryTable(kind));
        if (kind == FacadeKind.String)
            new StringGob(shell).SeamStringSub = Facade.Table(SeamTable.CAurString);
        else if (kind == FacadeKind.Camera)
            *(nint*)new CX.Engine.Camera(shell).GobSub = Facade.Table(SeamTable.CameraGob);
        Live++;
        return shell;
    }

    /// <summary>Record that <paramref name="shell"/>'s object joined a world or a GUI scene, or that a scene
    /// shell became the world scene. A block that is not ours is left alone.</summary>
    internal static void NoteScene(nint shell, bool world)
    {
        if (shell == 0) return;
        SeamHeader* h = Head(shell);
        if (h->Magic != Magic) return;
        h->Scenes |= world ? InWorld : InGui;
    }

    /// <summary>Mark <paramref name="shell"/> retired and queue it for reuse after the quarantine, or hold it for
    /// good when its object was ever in a GUI scene or none, or it is a non-world scene (<see cref="Held"/>). The caller
    /// has already nulled the SafePointer back-references (<see cref="Facade.Retire"/>); this only changes the
    /// block's state, so every later call through its vtable takes the poison path.</summary>
    public static void Retire(nint shell)
    {
        SeamHeader* h = Head(shell);
        if (h->Magic != Magic || h->State != StateLive) return;
        h->State = StateRetired;
        h->RetiredAt = FrameNo;
        h->Reported = 0;
        h->RetiredBy = SeamCallers.Here();
        Live--;
        if (h->Kind != (byte)FacadeKind.Scene && h->Scenes == 0) NoteUnmarked(shell, h);
        if (h->Scenes != InWorld)
        {
            _held.Blocks.Add(shell);
            NoteHeld("facade shells", _held.Blocks.Count);
            return;
        }
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
                NativeMemory.AlignedFree(Headroom<SeamHeader>.BlockOf(evicted));
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

    /// <summary>Count a retire before any scene join and name the game function that destroyed it, once each.</summary>
    private static void NoteUnmarked(nint shell, SeamHeader* h)
    {
        HeldState st = _held;
        st.Unmarked++;
        if (Facade.InSelfCheck || st.Destroyers.Count >= UnmarkedLines || !st.Destroyers.Add(h->RetiredBy)) return;
        var kind = (FacadeKind)h->Kind;
        string name = kind == FacadeKind.Camera ? "" : $" \"{Facade.NameOf(shell)}\"";
        Facade.Log?.Always($"seam: [unmarked] {kind}{name} destroyed by {SeamCallers.Name(h->RetiredBy)} before it "
                         + $"joined any scene -- held like a GUI shell, never reused ({st.Unmarked} so far)");
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
                if (b != 0) NativeMemory.AlignedFree(Headroom<SeamHeader>.BlockOf(b));
            }
            _ringHead[k] = 0;
        }
        foreach (nint b in _held.Blocks) NativeMemory.AlignedFree(Headroom<SeamHeader>.BlockOf(b));
        _held = new HeldState();
        Quarantined = 0;
    }
}
