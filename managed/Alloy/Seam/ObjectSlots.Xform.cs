using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;

namespace CX.Alloy.Seam;

public sealed unsafe partial class ObjectSlots
{
    private static void RegisterXform(SeamRegistry r)
    {
        // The seven by-value returns of this table. SysV classifies Vector and Quaternion into xmm0:xmm1 with
        // no hidden buffer; Win64 passes a buffer in rdx with `this` in rcx and pushes every later argument one
        // register right. One shared declaration is wrong on Linux twice over, so each has two bodies and the
        // registration picks the OS's.
        bool win = OperatingSystem.IsWindows();
        r.Slot(SeamTable.Gob, 23, win ? (void*)(delegate* unmanaged<nint, nint, EVec, nint>)&SetPositionWin
                                      : (void*)(delegate* unmanaged<nint, EVec, EVec>)&SetPositionLin);
        r.Slot(SeamTable.Gob, 24, win ? (void*)(delegate* unmanaged<nint, nint, EQuat, nint>)&SetOrientationWin
                                      : (void*)(delegate* unmanaged<nint, EQuat, EQuat>)&SetOrientationLin);
        r.Slot(SeamTable.Gob, 25, win ? (void*)(delegate* unmanaged<nint, nint, nint>)&GetPositionWin
                                      : (void*)(delegate* unmanaged<nint, EVec>)&GetPositionLin);
        r.Slot(SeamTable.Gob, 26, win ? (void*)(delegate* unmanaged<nint, nint, nint>)&GetOrientationWin
                                      : (void*)(delegate* unmanaged<nint, EQuat>)&GetOrientationLin);
        r.Slot(SeamTable.Gob, 27, win ? (void*)(delegate* unmanaged<nint, nint, nint>)&GetPreviousPositionWin
                                      : (void*)(delegate* unmanaged<nint, EVec>)&GetPreviousPositionLin);
        r.Slot(SeamTable.Gob, 92, win ? (void*)(delegate* unmanaged<nint, nint, nint>)&GetAdjustedPositionWin
                                      : (void*)(delegate* unmanaged<nint, EVec>)&GetAdjustedPositionLin);
        r.Slot(SeamTable.Gob, 93, win ? (void*)(delegate* unmanaged<nint, nint, nint>)&GetAdjustedOrientationWin
                                      : (void*)(delegate* unmanaged<nint, EQuat>)&GetAdjustedOrientationLin);

        r.Slot(SeamTable.Gob, 28, (void*)(delegate* unmanaged<nint, nint, void>)&SetVisualTransformData);
        r.Slot(SeamTable.Gob, 29, (void*)(delegate* unmanaged<nint, nint>)&GetVisualTransformData);
        r.Slot(SeamTable.Gob, 91, (void*)(delegate* unmanaged<nint, float>)&GetAdjustedScale);
        r.Slot(SeamTable.Gob, 55, (void*)(delegate* unmanaged<nint, float, int, byte>)&ScaleObject);

        r.Slot(SeamTable.Gob, 39, (void*)(delegate* unmanaged<nint, nint, EVec, EQuat, int>)&SetPartPosition);
        r.Slot(SeamTable.Gob, 40, (void*)(delegate* unmanaged<nint, nint, EVec*, EQuat*, int>)&GetPartPosition);
        r.Slot(SeamTable.Gob, 41, (void*)(delegate* unmanaged<nint, nint, EVec, EQuat, int>)&SetPartLocalPosition);
        r.Slot(SeamTable.Gob, 42, (void*)(delegate* unmanaged<nint, nint, EVec*, EQuat*, int>)&GetPartLocalPosition);

        r.Slot(SeamTable.Gob, 63, (void*)(delegate* unmanaged<nint, EVec*, EVec*, void>)&GetBoundingBox);
        r.Slot(SeamTable.Gob, 64, (void*)(delegate* unmanaged<nint, EVec*, EVec*, void>)&GetMinimumBoundingBoxOut);
        r.Slot(SeamTable.Gob, 65, (void*)(delegate* unmanaged<nint, EVec*, float*, void>)&GetMinimumBoundingSphere);
        r.Slot(SeamTable.Gob, 89, (void*)(delegate* unmanaged<nint, void>)&RecomputeBoundingBox);
        r.Slot(SeamTable.Gob, 90, (void*)(delegate* unmanaged<nint, void>)&RecomputeSphere);

        r.Slot(SeamTable.Gob, 71, (void*)(delegate* unmanaged<nint, nint, nint>)&GetPart);
        r.Slot(SeamTable.Gob, 72, (void*)(delegate* unmanaged<nint, nint, nint, void>)&EnumParts);
        r.Slot(SeamTable.Gob, 76, (void*)(delegate* unmanaged<nint, nint>)&GetRandomPart);
        r.Slot(SeamTable.Gob, 79, (void*)(delegate* unmanaged<nint, byte>)&ContainsSkinnedMeshes);
    }

    // ---- position and orientation ----

    /// <summary><c>Gob::SetPosition(Vector)</c> @0x437190: the engine writes +0xa8/+0xb0 and returns nothing
    /// through the buffer its signature declares, so the seam answers the new position -- defined rather than
    /// whatever the caller's buffer happened to hold.</summary>
    private static EVec SetPos(nint self, EVec v)
    {
        if (!Live(self, 23, out DbId id, out nint shell)) return default;
        if (Renderer.HitCheck.Boot && DbAnim.Behavior(id) != 0 && _behMovesSaid < BehMovesMax)
        {
            _behMovesSaid++;
            Facade.Log?.Info($"[dbbeh] SetPosition o={id.Index} \"{SceneDb.ObjModel[id.Index]?.Name ?? "-"}\" "
                             + $"{SceneDb.ObjPos[id.Index]} -> {v.V}");
        }
        Facade.SetXform(id, shell, v.V, SceneDb.ObjRot[id.Index]);
        return v;
    }

    /// <summary><c>alloy.hitcheck=1</c>: a behaviour-placed object moved by a direct setter, budgeted.</summary>
    private const int BehMovesMax = 40;
    private static int _behMovesSaid;

    [UnmanagedCallersOnly] private static EVec SetPositionLin(nint self, EVec v) => SetPos(self, v);

    [UnmanagedCallersOnly]
    private static nint SetPositionWin(nint self, nint sret, EVec v)
    {
        EVec r = SetPos(self, v);
        if (sret != 0) *(EVec*)sret = r;
        return sret;
    }

    private static EQuat SetRot(nint self, EQuat q)
    {
        if (!Live(self, 24, out DbId id, out nint shell)) return default;
        Facade.SetXform(id, shell, SceneDb.ObjPos[id.Index], q.Q);
        return q;
    }

    [UnmanagedCallersOnly] private static EQuat SetOrientationLin(nint self, EQuat q) => SetRot(self, q);

    [UnmanagedCallersOnly]
    private static nint SetOrientationWin(nint self, nint sret, EQuat q)
    {
        EQuat r = SetRot(self, q);
        if (sret != 0) *(EQuat*)sret = r;
        return sret;
    }

    /// <summary><c>Gob::GetPosition</c> @0x444670 reads the shell word, so the answer cannot drift from what
    /// the game's devirtualisation guards read inline.</summary>
    private static EVec Pos(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 25); return default; }
        return EVec.From(Facade.ShellPos(shell));
    }

    [UnmanagedCallersOnly] private static EVec GetPositionLin(nint self) => Pos(self);

    [UnmanagedCallersOnly]
    private static nint GetPositionWin(nint self, nint sret)
    {
        if (sret != 0) *(EVec*)sret = Pos(self);
        return sret;
    }

    private static EQuat Rot(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 26); return EQuat.Identity; }
        return EQuat.From(Facade.ShellRot(shell));
    }

    [UnmanagedCallersOnly] private static EQuat GetOrientationLin(nint self) => Rot(self);

    [UnmanagedCallersOnly]
    private static nint GetOrientationWin(nint self, nint sret)
    {
        if (sret != 0) *(EQuat*)sret = Rot(self);
        return sret;
    }

    /// <summary><c>Gob::GetPreviousPosition</c> @0x4446d0 reads <c>Gob+0xc4</c>, which the engine's animate
    /// step stamps; the seam keeps it as a column the transform step writes.</summary>
    private static EVec PrevPos(nint self)
    {
        if (!Live(self, 27, out DbId id, out _)) return default;
        return EVec.From(SceneDb.ObjSeamPrevPos[id.Index]);
    }

    [UnmanagedCallersOnly] private static EVec GetPreviousPositionLin(nint self) => PrevPos(self);

    [UnmanagedCallersOnly]
    private static nint GetPreviousPositionWin(nint self, nint sret)
    {
        if (sret != 0) *(EVec*)sret = PrevPos(self);
        return sret;
    }

    // ---- the visual transform fold ----

    /// <summary><c>Gob::GetAdjustedScale() const</c> @0x437cf0 is <c>Gob+0x49c * Lerped(Gob+0x1c0)</c>: the
    /// product of the two <c>ScaleObject</c> channels times the visual transform's animated scale.</summary>
    [UnmanagedCallersOnly]
    private static float GetAdjustedScale(nint self)
    {
        if (!Live(self, 91, out DbId id, out nint shell)) return 1f;
        return *(float*)(shell + SeamLayout.GobScaleFinal) * VisualXform.Scale(shell);
    }

    /// <summary><c>Gob::GetAdjustedPosition() const</c> @0x439900: the object's position plus the visual
    /// transform's animated translation (the three <c>LerpFloat</c>s at +0x340/+0x380/+0x3c0).</summary>
    private static EVec AdjPos(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 92); return default; }
        Vector3 p = Facade.ShellPos(shell);
        Vector3 t = VisualXform.Translate(shell);
        if (t != Vector3.Zero) p += Vector3.Transform(t, Facade.ShellRot(shell));
        return EVec.From(p);
    }

    [UnmanagedCallersOnly] private static EVec GetAdjustedPositionLin(nint self) => AdjPos(self);

    [UnmanagedCallersOnly]
    private static nint GetAdjustedPositionWin(nint self, nint sret)
    {
        if (sret != 0) *(EVec*)sret = AdjPos(self);
        return sret;
    }

    /// <summary><c>Gob::GetAdjustedOrientation() const</c> @0x437d20: the raw orientation when the three
    /// animated angles (<c>Lerped</c> at +0x280 yaw, +0x2c0 pitch, +0x300 roll) are all zero -- which is the
    /// common case and the engine's own early-out -- and otherwise the orientation times
    /// <c>YawPitchRoll(yaw, pitch, roll)</c>, in that multiplication order.</summary>
    private static EQuat AdjRot(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 93); return EQuat.Identity; }
        Quaternion q = Facade.ShellRot(shell);
        if (VisualXform.Angles(shell, out float rx, out float ry, out float rz))
            q *= VisualXform.Rotation(rx, ry, rz);
        return EQuat.From(q);
    }

    [UnmanagedCallersOnly] private static EQuat GetAdjustedOrientationLin(nint self) => AdjRot(self);

    [UnmanagedCallersOnly]
    private static nint GetAdjustedOrientationWin(nint self, nint sret)
    {
        if (sret != 0) *(EQuat*)sret = AdjRot(self);
        return sret;
    }

    /// <summary><c>Gob::SetVisualTransformData(CAurObjectVisualTransformData const&amp;)</c> @0x439bd0. The
    /// struct is the game's and the engine stores it field by field into its <c>LerpFloat</c> slots. The seam
    /// keeps the caller's BYTES verbatim in a side pool, so <see cref="GetVisualTransformData"/> hands back a
    /// reference to the same layout the caller gave, and reads the scale/translate/rotate floats for the fold.</summary>
    [UnmanagedCallersOnly]
    private static void SetVisualTransformData(nint self, nint data)
    {
        if (!Live(self, 28, out DbId id, out nint shell) || data == 0) return;
        VisualXform.Store(shell, data);
        DbAnim.SetVisualTransformed(id);
        SceneDb.Touch(id, Dirty.Xform);
    }

    /// <summary><c>Gob::GetVisualTransformData</c> @0x436e70 returns a <c>const&amp;</c>, i.e. a pointer into
    /// storage that must outlive the call. The pool entry is that storage; an object that was never given one
    /// answers the zeroed shared entry rather than null, because the engine's reference is never null.</summary>
    [UnmanagedCallersOnly]
    private static nint GetVisualTransformData(nint self)
    {
        nint shell = Facade.GobBlock(self);
        return shell == 0 ? VisualXform.Zero : VisualXform.Of(shell);
    }

    /// <summary><c>Gob::ScaleObject(float, AUR_OBJECTSCALE_TYPE)</c> @0x43efd0:
    /// <c>if (model != 0 &amp;&amp; type &lt; 2) { channel[type] = scale; CalculateFinalScale(); return 1; }
    /// return 0;</c> -- so there are exactly TWO scale channels and the final scale is their product. An
    /// object with no model refuses, which is why a text object cannot be scaled this way.</summary>
    [UnmanagedCallersOnly]
    private static byte ScaleObject(nint self, float scale, int type)
    {
        if (!Live(self, 55, out DbId id, out nint shell)) return 0;
        if ((uint)type >= 2 || SceneDb.ObjModel[id.Index] == null) return 0;
        float* ch = (float*)(shell + SeamLayout.GobScaleChannel);
        ch[type] = scale;
        float final = ch[0] * ch[1];
        *(float*)(shell + SeamLayout.GobScaleFinal) = final;
        SceneDb.ObjScale[id.Index] = final;
        SceneDb.ObjSeamScaleMode[id.Index] = (byte)type;
        SceneDb.Touch(id, Dirty.Xform);
        return 1;
    }

    // ---- parts ----

    /// <summary>The part index in <see cref="SceneDb.Parts"/> whose node is named <paramref name="name"/>, or
    /// -1. <c>Gob::GetPart</c> @0x43c630 matches <c>"root"</c> case-insensitively to the root and otherwise
    /// runs <c>FindPart</c> over LOD 0's tree only, so the seam searches the object's one part range. Matches
    /// the engine's NUL-terminated bytes directly: it is called every update, so it may not allocate.</summary>
    internal static int PartByName(DbId obj, byte* name)
    {
        if (!SceneDb.Objects.Valid(obj)) return -1;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        if (count <= 0) return -1;
        if (name == null || *name == 0 || AsciiEq(name, "root")) return first;
        for (int p = first; p < first + count; p++)
        {
            ModelDef? m = SceneDb.PartModel[p];
            if (m == null) continue;
            int n = SceneDb.PartNode[p];
            if ((uint)n < (uint)m.Nodes.Length && AsciiEq(name, m.Nodes[n].Name)) return p;
        }
        // Not in the object's own range: a composed body part (Gob::Append) keeps its own range, and the
        // game addresses the whole composition by name.
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            int p = PartByName(c, name);
            if (p >= 0) return p;
        }
        return -1;
    }

    /// <summary>Case-insensitive compare of NUL-terminated ASCII against a managed name, no allocation.
    /// Node names are ASCII in every model format the engine reads.</summary>
    private static bool AsciiEq(byte* a, string b)
    {
        int i = 0;
        for (; i < b.Length; i++)
        {
            byte x = a[i];
            if (x == 0) return false;
            if (x >= 'A' && x <= 'Z') x += 32;
            char y = b[i];
            if (y >= 'A' && y <= 'Z') y = (char)(y + 32);
            if (x != y) return false;
        }
        return a[i] == 0;
    }

    /// <summary>The managed-string twin of the <c>byte*</c> overload, searching the COMPOSITION the same way:
    /// <c>Gob::Append</c> is its caller, a creature's body parts are appended one after another, and an anchor
    /// node in a piece already appended -- a forearm under a bicep -- must resolve or the append refuses and
    /// that piece of the creature never exists. Two overloads of one question must not give two answers.</summary>
    internal static int PartByName(DbId obj, string? name)
    {
        if (!SceneDb.Objects.Valid(obj)) return -1;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        if (count <= 0) return -1;
        if (string.IsNullOrEmpty(name) || string.Equals(name, "root", StringComparison.OrdinalIgnoreCase)) return first;
        for (int p = first; p < first + count; p++)
        {
            ModelDef? m = SceneDb.PartModel[p];
            if (m == null) continue;
            int n = SceneDb.PartNode[p];
            if ((uint)n < (uint)m.Nodes.Length
                && string.Equals(m.Nodes[n].Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            int p = PartByName(c, name);
            if (p >= 0) return p;
        }
        return -1;
    }

    /// <summary><c>Gob::GetPart(char const*)</c> @0x43c630. The <c>CAurPart*</c> handed back is a seam Part
    /// shell: the game only calls <c>Part::GetWorld</c> through its vptr and otherwise stores it opaquely, and
    /// because that call is VIRTUAL the value has to be a real pointer with a real vtable
    /// (<see cref="PartShell"/>).</summary>
    [UnmanagedCallersOnly]
    private static nint GetPart(nint self, nint name)
    {
        if (!Live(self, 71, out DbId id, out _)) return 0;
        int p = PartByName(id, (byte*)name);
        return p < 0 ? 0 : PartShell.For(id, p);
    }

    /// <summary><c>Gob::EnumParts(int (*)(CAurPart*), List&lt;CAurPart*&gt;*)</c> @0x43fff0: every part the
    /// predicate accepts, appended to the caller's list through the engine's own
    /// <c>CExoArrayList&lt;CAurPart*&gt;::Add</c> so the array stays on the engine's heap. A null predicate
    /// takes every part, which is how the engine's own callers use it.</summary>
    [UnmanagedCallersOnly]
    private static void EnumParts(nint self, nint predicate, nint list)
    {
        if (!Live(self, 72, out DbId id, out _) || list == 0) return;
        if (EngineFn.ListAddCAurPart == null)
        {
            Facade.Log?.Always("seam: CExoArrayList<CAurPart*>::Add did not resolve -- EnumParts answers an empty list");
            return;
        }
        var test = (delegate* unmanaged<nint, int>)predicate;
        int first = SceneDb.ObjPartFirst[id.Index], count = SceneDb.ObjPartCount[id.Index];
        for (int p = first; p < first + count; p++)
        {
            nint shell = PartShell.For(id, p);
            if (shell == 0) continue;
            if (predicate != 0 && test(shell) == 0) continue;
            EngineFn.ListAddCAurPart(list, shell);
        }
    }

    /// <summary><c>Gob::GetRandomPart</c> @0x43d4c0 picks a part with probability proportional to its tri-mesh
    /// AREA (the engine caches the object's total at +0x4b8, -1 until <c>UpdatePartTriMeshArea</c> runs) and
    /// returns its NAME. Area-weighted, not uniform: a blood spatter lands on the big surfaces. SIMPLIFIED:
    /// the seam weights by bounding-box surface area -- the same shape, with no pass over the arena.</summary>
    [UnmanagedCallersOnly]
    private static nint GetRandomPart(nint self)
    {
        if (!Live(self, 76, out DbId id, out _)) return 0;
        int first = SceneDb.ObjPartFirst[id.Index], count = SceneDb.ObjPartCount[id.Index];
        if (count <= 0) return 0;
        float total = 0f;
        for (int p = first; p < first + count; p++) total += PartArea(p);
        if (total <= 0f) return 0;
        float pick = (float)(_rand.NextDouble() * total);
        for (int p = first; p < first + count; p++)
        {
            pick -= PartArea(p);
            if (pick > 0f) continue;
            return NativeNames.OfPart(p);
        }
        return 0;
    }

    private static readonly Random _rand = new();

    /// <summary>A part's weight in the area-weighted draw. The engine sums triangle areas; the seam uses the
    /// part's bounding-box surface area, which has the same shape.</summary>
    private static float PartArea(int part)
    {
        if (SceneDb.PartMesh[part] < 0 || !SceneDb.PartEnabled[part]) return 0f;
        return SeamBridge.PartArea(part);
    }

    /// <summary><c>Gob::ContainsSkinnedMeshes</c> @0x43a250 walks the tree for a skinned mesh; the seam reads
    /// the flag the part-tree build left, because the answer cannot change without a tree change.</summary>
    [UnmanagedCallersOnly]
    private static byte ContainsSkinnedMeshes(nint self)
    {
        if (!Live(self, 79, out DbId id, out _)) return 0;
        return Has(id, SeamFlag.Skinned) ? (byte)1 : (byte)0;
    }

    /// <summary><c>Gob::SetPartPosition(char const*, Vector, Quaternion)</c> @0x438ee0: place a part in WORLD
    /// space by writing the parent-relative pose that composes to it under the parent's current world. A
    /// controller the part's animation keys overwrites it on the next pose, as in the engine.</summary>
    [UnmanagedCallersOnly]
    private static int SetPartPosition(nint self, nint name, EVec pos, EQuat rot)
    {
        if (!Live(self, 39, out DbId id, out _)) return 0;
        int p = PartByName(id, (byte*)name);
        if (p < 0) return 0;
        DbId owner = SceneDb.PartObj[p];
        DbAnim.ComposeFor(owner);
        int parent = SceneDb.PartParent[p];
        Matrix4x4 parentWorld = parent >= 0 ? SceneDb.PartWorld[parent] : SceneDb.ObjWorld[owner.Index];
        if (!Matrix4x4.Invert(parentWorld, out Matrix4x4 inv)) return 0;
        SceneDb.PartPos[p] = Vector3.Transform(pos.V, inv);
        SceneDb.PartRot[p] = DbAnim.LocalRot(rot.Q, parentWorld);
        SceneDb.Touch(owner, Dirty.Anim | Dirty.Xform);
        DbAnim.PoseWritten(owner);
        return 1;
    }

    /// <summary><c>Gob::GetPartPosition(char const*, Vector*, Quaternion*)</c> @0x4372b0: the part's WORLD
    /// pose through <c>GetPart</c> then <c>Part::GetWorld</c>, with either out-pointer optional. Non-zero when
    /// the part exists -- the engine returns 0 and writes nothing when it does not.</summary>
    [UnmanagedCallersOnly]
    private static int GetPartPosition(nint self, nint name, EVec* pos, EQuat* rot)
    {
        if (!Live(self, 40, out DbId id, out _)) return 0;
        int p = PartByName(id, (byte*)name);
        if (p < 0) return 0;
        DbAnim.ComposeFor(id);
        Matrix4x4 m = SceneDb.PartWorld[p];
        if (pos != null) *pos = new EVec { X = m.M41, Y = m.M42, Z = m.M43 };
        if (rot != null) *rot = EQuat.From(Quaternion.CreateFromRotationMatrix(m));
        return 1;
    }

    /// <summary><c>Gob::SetPartLocalPosition</c> @0x437350: the part's parent-relative pose, written straight
    /// to the pose columns the animation runtime shares.</summary>
    [UnmanagedCallersOnly]
    private static int SetPartLocalPosition(nint self, nint name, EVec pos, EQuat rot)
    {
        if (!Live(self, 41, out DbId id, out _)) return 0;
        int p = PartByName(id, (byte*)name);
        if (p < 0) return 0;
        SceneDb.PartPos[p] = pos.V;
        SceneDb.PartRot[p] = rot.Q;
        SceneDb.Touch(id, Dirty.Anim | Dirty.Xform);
        DbAnim.PoseWritten(id);
        return 1;
    }

    /// <summary><c>Gob::GetPartLocalPosition</c> @0x4373b0.</summary>
    [UnmanagedCallersOnly]
    private static int GetPartLocalPosition(nint self, nint name, EVec* pos, EQuat* rot)
    {
        if (!Live(self, 42, out DbId id, out _)) return 0;
        int p = PartByName(id, (byte*)name);
        if (p < 0) return 0;
        if (pos != null) *pos = EVec.From(SceneDb.PartPos[p]);
        if (rot != null) *rot = EQuat.From(SceneDb.PartRot[p]);
        return 1;
    }

    // ---- bounds ----

    /// <summary><c>Gob::GetBoundingBox(Vector&amp;, Vector&amp;)</c> @0x436ee0 reads the cached box at
    /// <c>Gob+0x160..+0x177</c> and nothing else -- it never recomputes, which is why the two void
    /// <c>Recompute*</c> slots exist and why <c>NewCAurObject</c> calls them at construction.</summary>
    [UnmanagedCallersOnly]
    private static void GetBoundingBox(nint self, EVec* min, EVec* max)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 63); return; }
        ReadBox(shell, out Vector3 lo, out Vector3 hi);
        if (min != null) *min = EVec.From(lo);
        if (max != null) *max = EVec.From(hi);
    }

    /// <summary><c>Gob::GetMinimumBoundingBox(Vector&amp;, Vector&amp;)</c> @0x436ff0: the tight box, i.e. the
    /// one the engine computes from the part tree's own bounds rather than the model's declared extent.</summary>
    [UnmanagedCallersOnly]
    private static void GetMinimumBoundingBoxOut(nint self, EVec* min, EVec* max)
    {
        if (!Live(self, 64, out DbId id, out nint shell)) return;
        RecomputeBounds(id, shell);
        ReadBox(shell, out Vector3 lo, out Vector3 hi);
        if (min != null) *min = EVec.From(lo);
        if (max != null) *max = EVec.From(hi);
    }

    /// <summary><c>Gob::GetMinimumBoundingSphere(Vector&amp;, float&amp;)</c> @0x437050 recomputes first (it
    /// calls its own <c>GetMinimumSphere</c> slot) and then reads the cached centre and radius.</summary>
    [UnmanagedCallersOnly]
    private static void GetMinimumBoundingSphere(nint self, EVec* centre, float* radius)
    {
        if (!Live(self, 65, out DbId id, out nint shell)) return;
        RecomputeBounds(id, shell);
        ReadSphere(shell, out Vector3 c, out float rr);
        if (centre != null) *centre = EVec.From(c);
        if (radius != null) *radius = rr;
    }

    /// <summary><c>Gob::GetMinimumBoundingBox()</c> @0x436f60 -- the VOID one: it seeds the cache with
    /// &#177;10000 and asks the root part to grow it. The seam recomputes from the part range; the seed is kept
    /// because an object with no geometry must answer the engine's inverted box, not zeros.</summary>
    [UnmanagedCallersOnly]
    private static void RecomputeBoundingBox(nint self)
    {
        if (Live(self, 89, out DbId id, out nint shell)) RecomputeBounds(id, shell);
    }

    /// <summary><c>Gob::GetMinimumSphere()</c> @0x4370a0 -- the VOID one.</summary>
    [UnmanagedCallersOnly]
    private static void RecomputeSphere(nint self)
    {
        if (Live(self, 90, out DbId id, out nint shell)) RecomputeBounds(id, shell);
    }
}
