using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;

namespace CX.Alloy.Seam;

/// <summary>The <c>CAurPart*</c> the seam hands the game, and the one Part vtable behind it. The game never
/// walks a part tree: the only <c>Part*</c> it holds is what <c>Gob::GetPart(name)</c> returned, and all it
/// does with it is a VIRTUAL call through <c>Part</c> vtable slot 4 -- <c>Part::GetWorld</c>, dumped from
/// <c>_ZTV4Part</c> @0x186b2b8 -- so a shell is real memory: a CX header plus a vptr, one per (object, part).</summary>
public static unsafe class PartShell
{
    private const ulong Magic = 0x5452_4150_5F58_43;   // not-an-offset: "CX_PART"
    /// <summary>Which slot <c>Part::GetWorld</c> is, from the measured <c>vtbl.GetWorld</c> row: Linux
    /// <c>+0x20</c> (slot 4), Windows <c>+0x18</c> (slot 3, the collapsed destructor pair). Read off the row
    /// rather than derived as "Linux minus one", so the number is this build's measurement on both OSes.</summary>
    private static int GetWorldSlot => CX.Framework.EngineLayout.PartGetWorldSlot / sizeof(nint);
    /// <summary>Slots built. The game reaches only slot 4; the rest are the shared refusal, and the engine's
    /// own Part code never runs. 16 covers every slot any observed caller indexes.</summary>
    private const int Slots = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct Head
    {
        public ulong Magic;
        public uint ObjId;
        public int Part;
        public nint Next;
        /// <summary>The frame this shell was retired, for the quarantine.</summary>
        public uint RetiredAt;
        public uint Pad;
    }

    private static readonly int HeadSize = (sizeof(Head) + 15) & ~15;
    private static nint _table;
    private static long _alive, _made;

    /// <summary>Part shells alive, for the period line.</summary>
    public static long Alive => _alive;

    /// <summary>Build the Part vtable. Called by the seam once the facade tables exist.</summary>
    internal static void Build()
    {
        if (_table != 0) return;
        void* block = NativeMemory.AlignedAlloc((nuint)((2 + Slots) * sizeof(nint)), 64);
        if (block == null) { Facade.Log?.Always("seam: no memory for the Part vtable -- GetPart will refuse"); return; }
        NativeMemory.Fill(block, (nuint)((2 + Slots) * sizeof(nint)), 0);
        nint point = (nint)block + 2 * sizeof(nint);
        nint refuse = (nint)(delegate* unmanaged<nint, nint>)&Refuse;
        for (int i = 0; i < Slots; i++) ((nint*)point)[i] = refuse;
        int world = GetWorldSlot;
        // 0 is what an unresolved row reads as, and GetWorld is never slot 0 (the destructor is, on both
        // OSes), so 0 and anything past the table are the same refusal.
        if (world <= 0 || world >= Slots)
        {
            Facade.Log?.Always($"seam: Part::GetWorld resolves to slot {world} (vtbl.GetWorld = "
                             + $"{CX.Framework.EngineLayout.PartGetWorldSlot}), which is not a slot of the "
                             + $"{Slots}-slot Part table -- GetPart refuses rather than build a wrong table");
            NativeMemory.AlignedFree(block);
            return;
        }
        ((nint*)point)[world] = (nint)(delegate* unmanaged<nint, nint, nint, void>)&GetWorld;
        _table = point;
    }

    /// <summary>The <c>CAurPart*</c> for part <paramref name="part"/> of <paramref name="obj"/>, minted once
    /// and kept for the object's life so the game's stored copies stay valid; 0 when it cannot be had.</summary>
    public static nint For(DbId obj, int part)
    {
        if (_table == 0 || !SceneDb.Objects.Valid(obj)) return 0;
        for (nint p = SceneDb.ObjSeamPartShells[obj.Index]; p != 0; p = ((Head*)(p - HeadSize))->Next)
            if (((Head*)(p - HeadSize))->Part == part) return p;
        void* block = NativeMemory.AlignedAlloc((nuint)(HeadSize + sizeof(nint)), 16);
        if (block == null) { Facade.Log?.Always("seam: no memory for a Part shell -- GetPart refused"); return 0; }
        nint shell = (nint)block + HeadSize;
        Head* h = (Head*)block;
        h->Magic = Magic;
        h->ObjId = obj.Value;
        h->Part = part;
        h->Next = SceneDb.ObjSeamPartShells[obj.Index];
        h->RetiredAt = 0;
        *(nint*)shell = _table;
        SceneDb.ObjSeamPartShells[obj.Index] = shell;
        _alive++; _made++;
        return shell;
    }

    /// <summary>Which part of which object a <c>CAurPart*</c> the game handed back stands for.</summary>
    public static bool Resolve(nint self, out DbId obj, out int part)
    {
        obj = default; part = -1;
        if (self == 0) return false;
        Head* h = (Head*)(self - HeadSize);
        if (h->Magic != Magic) return false;
        obj = new DbId(h->ObjId);
        part = h->Part;
        return SceneDb.Objects.Valid(obj);
    }

    /// <summary>Retire every Part shell of <paramref name="obj"/>: QUARANTINED when the object was only ever in
    /// world scenes, else <see cref="Held"/>; never freed at once. The game keeps a <c>CAurPart*</c> for a behaviour's life (<c>CAurBehaviorSubwindow</c>
    /// +0x30/+0x38, <c>CAurBehaviorAttach</c> +0x30) and calls <c>Part::GetWorld</c> through it every frame, so a
    /// freed shell is a wild vptr; a retired one keeps vptr and header and answers the identity transform.</summary>
    public static void FreeAll(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        nint p = SceneDb.ObjSeamPartShells[obj.Index];
        SceneDb.ObjSeamPartShells[obj.Index] = 0;
        bool hold = SceneDb.ObjSceneKinds[obj.Index] != SeamShell.InWorld;
        while (p != 0)
        {
            Head* h = (Head*)(p - HeadSize);
            nint next = h->Next;
            h->ObjId = 0;                      // retired: Resolve fails, GetWorld takes the poison path
            h->RetiredAt = SeamShell.FrameNo;
            _alive--;
            // Held blocks stay readable for GetWorld forever, so the link must not name a block that gets freed.
            if (hold) { h->Next = 0; SeamShell.NoteHeld("part shells", ++_held.Count); p = next; continue; }
            h->Next = _quarantine;
            _quarantine = p;
            _quarantined++;
            p = next;
        }
        Drain();
    }

    /// <summary>Free the quarantined part shells whose window has passed. Called at every retire and from
    /// the frame tick, so the list cannot grow without bound while shells keep dying.</summary>
    internal static void Drain()
    {
        nint keep = 0;
        nint p = _quarantine;
        while (p != 0)
        {
            Head* h = (Head*)(p - HeadSize);
            nint next = h->Next;
            if (SeamShell.FrameNo - h->RetiredAt >= SeamShell.QuarantineFrames)
            {
                h->Magic = 0;
                NativeMemory.AlignedFree(h);
                _quarantined--;
            }
            else { h->Next = keep; keep = p; }
            p = next;
        }
        _quarantine = keep;
    }

    private static nint _quarantine;
    private static long _quarantined;

    /// <summary>Part shells held past their object's death, waiting out the quarantine.</summary>
    public static long Quarantined => _quarantined;

    /// <summary>The GUI part shells <see cref="Held"/> counts.</summary>
    internal sealed class HeldState { internal long Count; }
    private static readonly HeldState _held = new();

    /// <summary>Retired part shells whose object was ever in a GUI scene or never in any: a GUI layout takes its
    /// <c>Part*</c> before the join and keeps it past the death, so no later part may sit at its address.
    /// ponytail: held for the session; a GUI-only part pool would let them be reused safely.</summary>
    public static long Held => _held.Count;

    /// <summary><c>Part::GetWorld(Vector&amp;, Quaternion&amp;)</c>: both out-params are references, so they
    /// are pointers on both ABIs and the slot needs no per-OS form. The engine initialises them to
    /// (0,0,0) and the identity quaternion first and only then composes, so a refusal here is the engine's own
    /// "no transform" answer rather than untouched caller memory.</summary>
    [UnmanagedCallersOnly]
    private static void GetWorld(nint self, nint outPos, nint outRot)
    {
        if (outPos != 0) { float* p = (float*)outPos; p[0] = 0f; p[1] = 0f; p[2] = 0f; }
        if (outRot != 0) { float* q = (float*)outRot; q[0] = 1f; q[1] = 0f; q[2] = 0f; q[3] = 0f; }
        if (!Resolve(self, out DbId obj, out int part))
        {
            // A retired part shell, or one whose object died: the out-params keep the identity the engine
            // seeds, and the call is named once so a behaviour still driving a dead attachment is visible.
            Head* h = (Head*)(self - HeadSize);
            if (h->Magic == Magic && h->ObjId == 0)
            {
                _afterDeath++;
                if ((_afterDeath & (_afterDeath - 1)) == 0)
                    Facade.Log?.Always($"seam: Part::GetWorld on a RETIRED part shell 0x{self:x} -- the game is still "
                                     + $"driving an attachment whose object died; answered the identity ({_afterDeath} so far)");
            }
            return;
        }
        DbAnim.ComposeFor(obj);
        _served++;
        Matrix4x4 m = SceneDb.PartWorld[part];
        if (outPos != 0) { float* p = (float*)outPos; p[0] = m.M41; p[1] = m.M42; p[2] = m.M43; }
        if (outRot == 0) return;
        // UNIT, out of the UNSCALED basis: a composed matrix's rows are `s` times a rotation's, and a GUI
        // panel scales its controls -- so decomposing it as it stands hands the caller a quaternion of norm
        // sqrt(s), which the attach behaviours then write into the owner's placement and scale a second time.
        Quaternion r = DbAnim.RotOf(m);
        // The engine's Quaternion is (x, y, z, w) in memory: Gob::GetOrientation @0x4446a0 reads +0xb4..+0xc0
        // as two movq, and Part::GetWorld seeds (1,0,0,0) -- w first is the IDENTITY's one non-zero, which is
        // x in that order only if the layout were (w,x,y,z). It is: the seed writes 0x3f800000 at [0].
        float* o = (float*)outRot;
        o[0] = r.W; o[1] = r.X; o[2] = r.Y; o[3] = r.Z;
    }

    private static long _afterDeath, _served;

    /// <summary><c>Part::GetWorld</c> calls that arrived on a retired shell.</summary>
    public static long AfterDeath => _afterDeath;

    /// <summary><c>Part::GetWorld</c> calls ANSWERED from the database -- the number that says the one Part
    /// member the game calls is actually reaching this vtable. Zero while anything is attached means the
    /// shells are not being handed out, or a hook is swallowing the call before it dispatches.</summary>
    public static long Served => _served;

    [UnmanagedCallersOnly]
    private static nint Refuse(nint self)
    {
        Facade.Log?.Always($"seam: a Part slot the game was not expected to reach was called on 0x{self:x}; returned zero");
        return 0;
    }
}
