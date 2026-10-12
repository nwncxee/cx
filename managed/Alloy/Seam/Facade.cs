using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>Which engine class a facade's shell mirrors.</summary>
public enum FacadeKind : byte { Object, String, Scene, Camera }

/// <summary>ADR 0028 section 2: the native block the game layer holds for a <see cref="SceneDb"/> record -- a
/// LAYOUT-COMPATIBLE SHELL, not a bare vtable. The shell keeps current the literal offsets the game reaches
/// (the SafePointer back-ref list at +0x08, <c>Gob</c> +0x24/+0x88/+0x90/+0xa8/+0xb4/+0x450/+0x490,
/// <c>CAurString</c> +0x4c0, a camera's <c>CAurObject</c> +8, <c>Scene</c> +0x130/+0x138/+0x168/+0x1b8).</summary>
public static unsafe partial class Facade
{
    internal static ExtenderLog? Log;

    /// <summary>Retires that found the shell already retired -- the base half of a destructor chain. Counted
    /// so a genuine double destroy is still visible on the period line, but not logged as a fault.</summary>
    public static long Redundant { get; private set; }

    /// <summary>Slot calls that arrived on a retired shell, and on a pointer that was never a facade. Both are
    /// counted and both are named once per slot in cx.log: a use-after-free in game code is a line, not a
    /// crash, for as long as the quarantine holds the block.</summary>
    public static long PoisonCalls { get; private set; }

    /// <summary>Destructor slots that found the shell already retired by the derived destructor.</summary>
    public static long BaseDestructors { get; private set; }

    /// <summary>Count a base-destructor call on a retired shell.</summary>
    internal static void NoteBaseDestructor() => BaseDestructors++;

    /// <summary>Set while the boot self-check is deliberately firing the refusal paths, so its lines cannot
    /// be read as findings in a live log. Off everywhere else.</summary>
    internal static bool InSelfCheck;
    public static long AlienCalls { get; private set; }

    /// <summary>The shell behind any interface pointer the game holds -- a primary, a camera's
    /// <c>CAurObject</c> at +8, a text shell's <c>CAurString</c> at +0x4c0 -- or 0 for one carrying none of our
    /// vtables; the adjustment is the Itanium offset-to-top word ahead of every table. The vptr test is EXACT
    /// against the recorded points (<see cref="PointBit"/>): a range would adjust a foreign pointer to a shell.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Shell(nint self)
    {
        if (self == 0) return 0;
        nint vptr = *(nint*)self;
        nint off = vptr - _tableBase;
        if ((nuint)off >= (nuint)_tableBytes || (off & 7) != 0) return 0;
        int w = (int)(off >> 3);
        if ((_pointBits[w >> 6] & (1UL << (w & 63))) == 0) return 0;
        return self + ((nint*)vptr)[-2];
    }

    /// <summary>Resolve <paramref name="self"/> to its record for logical slot <paramref name="logical"/>, or
    /// a null <see cref="DbId"/> on a retired or alien pointer -- the POISON path, which names the slot once
    /// and counts the call. A slot that gets a null handle returns zero and touches nothing.</summary>
    public static DbId Resolve(nint self, int logical, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        // A NULL CAurObject*/CAurScene* is a legal argument everywhere in this API -- "no object" -- and the
        // engine's own slots test for it rather than refusing, so it is not counted as a foreign pointer.
        if (self == 0) return default;
        nint shell = Shell(self);
        if (shell == 0) { Alien(self, logical, member, file); return default; }
        if (!SeamShell.Known(shell, out byte state, out DbId id)) { Alien(self, logical, member, file); return default; }
        if (state != SeamShell.StateLive) { Poison(shell, logical, member, file); return default; }
        return id;
    }

    /// <summary>The record behind a <c>CAurObject*</c> (a <c>Gob</c>-shaped shell, or a camera's +8 sub-object),
    /// validated against the objects table.</summary>
    public static DbId Object(nint self, int logical, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        => GobOf(self, logical, out DbId id, out _, member, file) ? id : default;

    /// <summary>The record AND the <c>Gob</c>-shaped block behind a <c>CAurObject*</c>: a <c>Camera</c> is the
    /// one shell where neither is what <see cref="Shell"/> normalises to -- its <c>CAurObject</c> is the
    /// <c>+8</c> sub-object, its record <see cref="SceneDb.CamObj"/>, its <c>Gob</c> words
    /// <see cref="SeamLayout.CameraGobSub"/> on, while the one header handle is the CAMERA row's.</summary>
    public static bool GobOf(nint self, int logical, out DbId id, out nint gob, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        id = default;
        gob = 0;
        if (self == 0) return false;
        nint shell = Shell(self);
        if (shell == 0) { Alien(self, logical, member, file); return false; }
        if (!SeamShell.Known(shell, out byte state, out DbId raw)) { Alien(self, logical, member, file); return false; }
        if (state != SeamShell.StateLive) { Poison(shell, logical, member, file); return false; }
        if ((FacadeKind)SeamShell.Head(shell)->Kind == FacadeKind.Camera)
        {
            gob = shell + SeamLayout.CameraGobSub;
            id = SceneDb.Cameras.Valid(raw) ? SceneDb.CamObj[raw.Index] : default;
        }
        else
        {
            gob = shell;
            id = raw;
        }
        if (!SceneDb.Objects.Valid(id)) { Stale(self, logical, member, file); return false; }
        return true;
    }

    /// <summary>The <c>Gob</c>-shaped block behind a <c>CAurObject*</c>, for the slot bodies that only need
    /// the mirrored words. Identity for every kind but a camera, whose <c>Gob</c> half is its <c>+8</c>
    /// sub-object (<see cref="GobOf"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint GobBlock(nint self)
    {
        nint shell = Shell(self);
        if (shell == 0) return 0;
        return (FacadeKind)SeamShell.Head(shell)->Kind == FacadeKind.Camera
             ? shell + SeamLayout.CameraGobSub : shell;
    }

    /// <summary>The record behind a <c>CAurScene*</c>.</summary>
    public static DbId Scene(nint self, int logical, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        DbId id = Resolve(self, logical, member, file);
        if (id.IsNull) return default;
        if (!SceneDb.Scenes.Valid(id)) { Stale(self, logical, member, file); return default; }
        return id;
    }

    /// <summary>The record behind a <c>CAurCamera*</c>.</summary>
    public static DbId Camera(nint self, int logical, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        DbId id = Resolve(self, logical, member, file);
        if (id.IsNull) return default;
        if (!SceneDb.Cameras.Valid(id)) { Stale(self, logical, member, file); return default; }
        return id;
    }

    /// <summary>The record behind a <c>CAurString*</c> (a text shell's +0x4c0 sub-object). Text objects are
    /// object records of <see cref="ObjKind.Text"/>, so this is the objects table too.</summary>
    public static DbId StringRecord(nint self, int logical, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        => Object(self, logical, member, file);

    /// <inheritdoc cref="Object(nint, int)"/>
    public static DbId ObjectId(nint self, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        => Object(self, SeamSlot.Unnamed, member, file);
    /// <inheritdoc cref="Scene(nint, int)"/>
    public static DbId SceneId(nint self, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        => Scene(self, SeamSlot.Unnamed, member, file);
    /// <inheritdoc cref="Camera(nint, int)"/>
    public static DbId CameraId(nint self, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        => Camera(self, SeamSlot.Unnamed, member, file);
    /// <inheritdoc cref="StringRecord(nint, int)"/>
    public static DbId StringId(nint self, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        => StringRecord(self, SeamSlot.Unnamed, member, file);

    /// <summary>The shell the game holds for record <paramref name="id"/>, or 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint ObjectShell(DbId id) => SceneDb.Objects.Valid(id) ? SceneDb.ObjFacade[id.Index] : 0;

    /// <summary>Mint the shell for record <paramref name="id"/>; 0 when it cannot (logged, counted).</summary>
    public static nint Mint(FacadeKind kind, DbId id)
    {
        if (!_built) { Log?.Always("seam: Mint before the vtables were built -- refused"); return 0; }
        nint shell = SeamShell.Alloc(kind, id);
        if (shell == 0) { Log?.Always($"seam: out of memory minting a {kind} facade -- refused"); return 0; }
        if (kind == FacadeKind.Scene) _sceneProbe.MintedScenes.Add(shell);
        switch (kind)
        {
            case FacadeKind.Object:
            case FacadeKind.String:
                if (SceneDb.Objects.Valid(id)) SceneDb.ObjFacade[id.Index] = shell;
                break;
            case FacadeKind.Scene:
                if (SceneDb.Scenes.Valid(id)) SceneDb.SceneFacade[id.Index] = shell;
                break;
            case FacadeKind.Camera:
                if (SceneDb.Cameras.Valid(id)) SceneDb.CamFacade[id.Index] = shell;
                break;
        }
        return shell;
    }

    /// <summary>The game destroyed the object: null its SafePointers, mark the shell retired and quarantine
    /// it. The record itself is freed by the caller (the destructor slot or <c>RemoveAuroraObject</c>), because
    /// what dies with it differs per kind.</summary>
    public static void Retire(nint self, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        nint shell = Shell(self);
        if (shell == 0) { Alien(self, SeamSlot.Unnamed, member, file); return; }
        if (!SeamShell.Known(shell, out byte state, out DbId id)) { Alien(self, SeamSlot.Unnamed, member, file); return; }
        // A second retire is NOT a use-after-free. A C++ destructor chain runs the derived destructor and
        // then the base one, and CX models those as separate vtable slots on the same block -- a StringGob
        // reaches both StringSlots' and the Gob table's, a Camera both CameraSlots' and the Gob table's. So
        // the second one is expected and silent; only a SLOT call on a retired shell is the real thing.
        if (state != SeamShell.StateLive) { Redundant++; return; }
        FacadeKind kind = (FacadeKind)SeamShell.Head(shell)->Kind;
        NullSafePointers(shell, kind);
        // The vptrs go to the poison twins BEFORE the record is released. Every slot is then a counted
        // refusal, including the ones that answer from the shell's own words and never resolve a handle --
        // a use-after-free can no longer read stale data or, worse, WRITE into a block queued for reuse.
        *(nint*)shell = Poison(SeamLayout.PrimaryTable(kind));
        if (kind == FacadeKind.String) *(nint*)(shell + SeamLayout.StringGobString) = Poison(SeamTable.CAurString);
        else if (kind == FacadeKind.Camera) *(nint*)(shell + SeamLayout.CameraGobSub) = Poison(SeamTable.CameraGob);
        switch (kind)
        {
            case FacadeKind.Object:
            case FacadeKind.String:
                if (SceneDb.Objects.Valid(id)) SceneDb.ObjFacade[id.Index] = 0;
                break;
            case FacadeKind.Scene:
                if (SceneDb.Scenes.Valid(id)) SceneDb.SceneFacade[id.Index] = 0;
                break;
            case FacadeKind.Camera:
                if (SceneDb.Cameras.Valid(id))
                {
                    // ...and the +8 sub-object's forward pointer with it: it addresses the INTERIOR of this
                    // same block, so leaving it set is a dangling shell for every reader of ObjFacade.
                    DbId sub = SceneDb.CamObj[id.Index];
                    if (SceneDb.Objects.Valid(sub)) SceneDb.ObjFacade[sub.Index] = 0;
                    SceneDb.CamFacade[id.Index] = 0;
                }
                break;
        }
        SeamShell.Retire(shell);
    }

    /// <summary>What <c>SafePointerList&lt;CAurObject&gt;::~SafePointerList</c> @0x445470 does, and nothing
    /// more: for each back-reference in the list at the shell's +0x08, zero the SafePointer's target word
    /// (+0x00), set its index (+0x08) to -1, then release the array. Every entry points at THIS object
    /// (<c>SafePointer::operator=</c> @0x477d70 appends <c>this</c>); one that does not is a seam bug, named.</summary>
    private static void NullSafePointers(nint shell, FacadeKind kind)
    {
        int at = SeamLayout.SafeListAt(kind);
        nint arr = *(nint*)(shell + at);
        int count = *(int*)(shell + at + 8);
        if (arr != 0 && count > 0)
        {
            nint owner = kind == FacadeKind.Camera ? shell + SeamLayout.CameraGobSub : shell;
            for (int i = 0; i < count; i++)
            {
                nint sp = ((nint*)arr)[i];
                if (sp == 0) continue;
                nint target = *(nint*)sp;
                if (target != owner && target != 0)
                {
                    Log?.Always($"seam: SafePointer 0x{sp:x} in a facade's back-ref list targets 0x{target:x}, not 0x{owner:x} -- left alone");
                    continue;
                }
                *(nint*)sp = 0;
                *(int*)(sp + 8) = -1;
            }
        }
        *(nint*)(shell + at) = 0;
        *(int*)(shell + at + 8) = 0;
        *(int*)(shell + at + 12) = 0;
        if (arr == 0) return;
        // The array is the GAME's: the shell hands out capacity 0 so the first Add is the engine's own
        // realloc(NULL), and every later one comes from the same heap. nwmain.exe statically links its CRT and
        // exports no free, so on Windows the block is counted and left; on Linux both sides are glibc.
        if (OperatingSystem.IsWindows()) SeamShell.NoteForeignArray();
        else System.Runtime.InteropServices.NativeMemory.Free((void*)arr);
    }

    // ---- the mirrored words every setter keeps current ----

    /// <summary>Write the object's position and orientation into both the columns and the shell words the game
    /// reads without a vtable (<c>Gob</c> +0xa8 / +0xb4), and mark the record dirty -- the one place that does
    /// both, so a setter cannot keep half of it. <b>The orientation goes in the ENGINE's order, w first</b>
    /// (<see cref="EQuat"/>, and the <c>0x3f800000</c> <c>Gob::Gob</c> leaves at +0xb4), as do all its writers.</summary>
    public static void SetXform(DbId id, nint shell, Vector3 pos, Quaternion rot)
    {
        SceneDb.ObjPos[id.Index] = pos;
        SceneDb.ObjRot[id.Index] = rot;
        if (shell != 0)
        {
            float* p = (float*)(shell + SeamLayout.GobPos);
            p[0] = pos.X; p[1] = pos.Y; p[2] = pos.Z;
            float* q = (float*)(shell + SeamLayout.GobRot);
            q[0] = rot.W; q[1] = rot.X; q[2] = rot.Y; q[3] = rot.Z;
        }
        SceneDb.Touch(id, Dirty.Xform);
    }

    /// <summary>The position the shell carries -- what a <c>GetPosition</c> slot answers, and what the game's
    /// devirtualisation guards would read inline if they took the fast path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ShellPos(nint shell)
    {
        float* p = (float*)(shell + SeamLayout.GobPos);
        return new Vector3(p[0], p[1], p[2]);
    }

    /// <inheritdoc cref="SetXform"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion ShellRot(nint shell)
    {
        float* q = (float*)(shell + SeamLayout.GobRot);
        return new Quaternion(q[1], q[2], q[3], q[0]);
    }

    /// <summary>Copy <paramref name="name"/> into the shell's inline name array at +0x24, truncated to the
    /// room <c>Gob::~Gob</c>'s 0x100-bounded copy proves is there. The game reads it through
    /// <c>Gob::GetName</c>, which returns the ADDRESS of this array, so it has to be storage in the shell and
    /// not a managed string.</summary>
    public static void SetName(nint shell, ReadOnlySpan<char> name)
    {
        byte* dst = (byte*)(shell + SeamLayout.GobName);
        int n = 0;
        for (int i = 0; i < name.Length && n < SeamLayout.GobNameMax - 1; i++)
        {
            char c = name[i];
            dst[n++] = c < 0x80 ? (byte)c : (byte)'?';
        }
        dst[n] = 0;
    }

    /// <summary>Copy a NUL-terminated engine string into the shell's name array, byte for byte: no transcode,
    /// because the engine's text is a single-byte codepage and this storage is handed straight back to it.</summary>
    public static void SetName(nint shell, byte* name)
    {
        byte* dst = (byte*)(shell + SeamLayout.GobName);
        int n = 0;
        if (name != null)
            while (n < SeamLayout.GobNameMax - 1 && name[n] != 0) { dst[n] = name[n]; n++; }
        dst[n] = 0;
    }

    /// <summary>The shell's name as the game would read it.</summary>
    public static string NameOf(nint shell)
    {
        byte* s = (byte*)(shell + SeamLayout.GobName);
        int n = 0;
        while (n < SeamLayout.GobNameMax && s[n] != 0) n++;
        return System.Text.Encoding.ASCII.GetString(s, n);
    }

    /// <summary>The scene shell the game reads at <c>Gob</c> +0x88, kept current by every attach and detach.</summary>
    public static void SetSceneWord(nint shell, nint sceneShell) => *(nint*)(shell + SeamLayout.GobScene) = sceneShell;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint SceneWord(nint shell) => *(nint*)(shell + SeamLayout.GobScene);

    // ---- the refusal paths ----

    private static void Poison(nint shell, int logical, string member = "", string file = "")
    {
        PoisonCalls++;
        if (InSelfCheck)
        {
            if (SeamShell.FirstReport(shell, logical))
                Log?.Always($"seam: [self-check, on purpose] {SlotName(logical, member, file)} on a retired facade returned zero");
            return;
        }
        // ATTRIBUTION, not theory: who destroyed this shell, and who is touching it now. Both ends come off
        // the native stack; one line per distinct (deleter, toucher, slot) triple, however many calls follow.
        int by = SeamShell.Head(shell)->RetiredBy;
        int now = SeamCallers.Here();
        long key = ((long)by << 40) ^ ((long)now << 16) ^ (uint)logical;
        lock (_danglers)
        {
            if (!_danglers.Add(key)) return;
        }
        string deleter = SeamCallers.Name(by), toucher = SeamCallers.Name(now);
        Log?.Always($"seam: game-side dangling call (vanilla bug, caught) -- {deleter} destroyed \"{NameOf(shell)}\", "
                  + $"then {toucher} called {SlotName(logical, member, file)} on it; returned zero. The shell is held "
                  + $"poisoned for {SeamShell.QuarantineFrames} frames, so the call reads zero instead of another "
                  + "object's memory. Further calls of this pair are counted, not logged.");
    }

    private static readonly System.Collections.Generic.HashSet<long> _danglers = new();

    /// <summary>Distinct (deleter, toucher, slot) triples seen on the dangling path.</summary>
    public static int DanglingPairs { get { lock (_danglers) return _danglers.Count; } }

    /// <summary>A shell whose RECORD is gone -- a seam bug rather than a game-side one, so it is named -- but
    /// named like <see cref="Alien"/>: on the powers of two, with the running count, because one line per CALL
    /// is a log flood and an interpolated string in a slot body.</summary>
    private static void Stale(nint self, int logical, string member = "", string file = "")
    {
        PoisonCalls++;
        StaleCalls++;
        if ((StaleCalls & (StaleCalls - 1)) == 0)
            Log?.Always($"seam: {SlotName(logical, member, file)} on facade 0x{self:x} whose record is gone; "
                      + $"returned zero ({StaleCalls} so far)");
    }

    /// <summary>Slot calls that reached a live shell whose record had already been freed.</summary>
    public static long StaleCalls { get; private set; }

    private static void Alien(nint self, int logical, string member = "", string file = "")
    {
        AlienCalls++;
        if ((AlienCalls & (AlienCalls - 1)) == 0)
            Log?.Always(InSelfCheck
                ? "seam: [self-check, on purpose] a foreign pointer did not resolve to a facade"
                : $"seam: {SlotName(logical, member, file)} called on 0x{self:x}, which is not a CX facade; returned zero ({AlienCalls} so far)");
    }

    /// <summary>A slot's name, or for a resolution outside any slot the C# caller that asked.</summary>
    private static string SlotName(int logical, string member, string file)
        => logical != SeamSlot.Unnamed || member.Length == 0 ? SeamVtables.Name(logical)
         : $"{System.IO.Path.GetFileNameWithoutExtension(file)}.{member}";

    /// <summary>Every scene shell address ever minted, and the attaches that left an object in no scene.</summary>
    internal sealed class SceneProbeState
    {
        internal readonly System.Collections.Generic.HashSet<nint> MintedScenes = new();
        internal long OrphaningAttaches;
        internal int LinesLeft = OrphanLines;
        internal long WorldIntoGui;
        internal int WorldIntoGuiLeft = OrphanLines;
    }

    /// <summary>Lines <see cref="NoteOrphaningAttach"/> and <see cref="NoteJoin"/> each write per session; the
    /// counts keep going past it.</summary>
    private const int OrphanLines = 256;

    private static SceneProbeState _sceneProbe = new();

    /// <summary>The game attached object <paramref name="id"/> to a scene pointer that resolves to no live
    /// scene, so it now belongs to none and draws nowhere. Names the object, the scene it left and what the
    /// pointer is: a foreign block, a scene shell CX once minted, or a retired shell.</summary>
    internal static void NoteOrphaningAttach(DbId id, DbId was, nint scene)
    {
        SceneProbeState st = _sceneProbe;
        st.OrphaningAttaches++;
        if (InSelfCheck || st.LinesLeft <= 0) return;
        st.LinesLeft--;
        nint shell = Shell(scene);
        string what = shell == 0
            ? (st.MintedScenes.Contains(scene) ? "a CX scene shell once, now FOREIGN (block reused)" : "never a CX scene shell")
            : !SeamShell.Known(shell, out byte state, out _) ? "a CX vtable on an unknown block"
            : state != SeamShell.StateLive ? "a RETIRED CX scene" : "a live shell whose scene record is gone";
        int o = id.Index;
        Log?.Always($"seam: [orphan] o={id.Value} model='{SceneDb.ObjSeamModelName[o]}' kind={SceneDb.ObjKindOf[o]} "
                    + $"flags={SceneDb.ObjFlags[o]} attached to scene 0x{scene:x} ({what}) -- it left scene {was.Value} "
                    + $"and is now in NO scene, so it does not draw ({st.OrphaningAttaches} so far)");
    }

    /// <summary>Object <paramref name="obj"/> joined <paramref name="scene"/>: stamp its shell with the kind of
    /// scene, and name a world object moving into a GUI scene -- a GUI pointer that reached a world shell.</summary>
    internal static void NoteJoin(DbId scene, DbId obj)
    {
        if (!SeamShell.NoteScene(Shell(SceneDb.ObjFacade[obj.Index]), SceneDb.SceneIsWorld[scene.Index]) || InSelfCheck) return;
        SceneProbeState st = _sceneProbe;
        st.WorldIntoGui++;
        if (st.WorldIntoGuiLeft <= 0) return;
        st.WorldIntoGuiLeft--;
        int o = obj.Index;
        Log?.Always($"seam: [world->gui] o={obj.Value} model='{SceneDb.ObjSeamModelName[o]}' kind={SceneDb.ObjKindOf[o]} "
                    + $"flags={SceneDb.ObjFlags[o]} was in a world scene and joined GUI scene {scene.Value}, by "
                    + $"{SeamCallers.Chain(4)} ({st.WorldIntoGui} so far)");
    }

    /// <summary>The seam's period line: shells, quarantine, and every refusal counted.</summary>
    public static string Stats()
        => $"shells live={SeamShell.Live} quarantined={SeamShell.Quarantined} gui-held={SeamShell.Held} alloc={SeamShell.Allocated} reuse={SeamShell.Reused}"
         + $" poison={PoisonCalls} stale={StaleCalls} alien={AlienCalls} orphaning-attaches={_sceneProbe.OrphaningAttaches} world-into-gui={_sceneProbe.WorldIntoGui} redundant-retire={Redundant} base-dtor={BaseDestructors} dangling-pairs={DanglingPairs} foreign-arrays={SeamShell.ForeignArraysLeaked}";
}

/// <summary>Logical slot ids a hand-written caller needs by name. Everything else is
/// <c>SeamVtables.Logical(table, slot)</c> at registration time and a literal in the slot body.</summary>
public static class SeamSlot
{
    /// <summary>For a resolution that is not a vtable slot at all (a hooked free function, a retire).</summary>
    public const int Unnamed = -1;
}
