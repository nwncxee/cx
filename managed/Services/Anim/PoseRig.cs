using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Alloy.Seam;
using CX.Engine;

namespace CX.Services.Anim;

/// <summary>The pose layer's rig access, one vocabulary over both sessions it can run in. With Alloy on, a
/// body is the facade shell the game holds and a part a <see cref="SceneDb"/> part index carried as
/// <c>index + 1</c> -- a handle, never dereferenced; in a vanilla session (<c>alloy.enabled=0</c>) a part is
/// an engine <c>Part*</c> and a body its <c>Gob*</c>. The modules' maths does not know which.</summary>
internal static unsafe class PoseRig
{
    /// <summary>Is Alloy drawing (so the rig is the scene database's)? Latched when the layer arms.</summary>
    internal static bool Db;
    private static bool _wrote;

    internal static IntPtr Handle(int part) => (IntPtr)(part + 1);
    internal static int Index(IntPtr part) => (int)(nint)part - 1;

    /// <summary>Did the layer write a DB pose column since the last ask? Clears.</summary>
    internal static bool TakeWrote() { bool w = _wrote; _wrote = false; return w; }

    /// <summary>The object behind a DB body handle.</summary>
    internal static DbId Obj(IntPtr gob) => Facade.ObjectId(gob);

    /// <summary>World position and rotation, the scale in the position as <c>Part::GetWorld</c> answers it.</summary>
    internal static void World(IntPtr part, out Vector3 pos, out Quaternion rot)
    {
        if (Db) { DbAnim.PoseWorld(Index(part), out pos, out rot); return; }
        Vector3 p;
        float* q = stackalloc float[4];
        PoseMath.GetWorld(part, &p, q);
        pos = p;
        rot = new Quaternion(q[1], q[2], q[3], q[0]);
    }

    internal static Quaternion Local(IntPtr part) => Db ? SceneDb.PartRot[Index(part)] : new Part(part).Rot;

    internal static void SetLocal(IntPtr part, Quaternion q)
    {
        if (!Db) { new Part(part).Rot = q; return; }
        int p = Index(part);
        SceneDb.PartRot[p] = q;
        SceneDb.TouchPart(p, Dirty.Anim);
        _wrote = true;
    }

    internal static Vector3 Pos(IntPtr part) => Db ? SceneDb.PartPos[Index(part)] : new Part(part).Pos;

    internal static void SetPos(IntPtr part, Vector3 v)
    {
        if (!Db) { new Part(part).Pos = v; return; }
        int p = Index(part);
        SceneDb.PartPos[p] = v;
        SceneDb.TouchPart(p, Dirty.Anim);
        _wrote = true;
    }

    internal static IntPtr Parent(IntPtr part)
    {
        if (!Db) return new Part(part).Parent.Ptr;
        int up = SceneDb.PartParent[Index(part)];
        return up < 0 ? IntPtr.Zero : Handle(up);
    }

    /// <summary>The body's root part: the first of its pre-order range in DB mode.</summary>
    internal static IntPtr Root(IntPtr gob)
    {
        if (!Db) return new Gob(gob).PartTree.Ptr;
        DbId o = Obj(gob);
        return o.IsNull || SceneDb.ObjPartCount[o.Index] <= 0 ? IntPtr.Zero : Handle(SceneDb.ObjPartFirst[o.Index]);
    }

    /// <summary><c>Gob::GetAdjustedScale</c>: the seam's own answer in DB mode.</summary>
    internal static float Scale(IntPtr gob, GobGetAdjustedScaleFn engine)
    {
        if (!Db) return engine(gob);
        DbId o = Obj(gob);
        return o.IsNull ? 0f : ObjectBridge.ObjectAdjustedScale?.Invoke(o) ?? SceneDb.ObjScale[o.Index];
    }

    internal static Vector3 Position(IntPtr gob)
    {
        if (!Db) return new Gob(gob).Position;
        DbId o = Obj(gob);
        return o.IsNull ? default : SceneDb.ObjPos[o.Index];
    }

    /// <summary>In the view volume. ponytail: DB mode has no per-object cull flag the layer can read, so every
    /// body inside <c>anim.distance</c> counts; the upgrade is the feed's visible set.</summary>
    internal static bool InView(IntPtr gob) => Db ? !Obj(gob).IsNull : new Gob(gob).InView != 0;

    internal static bool InScene(IntPtr gob, IntPtr scene)
    {
        if (!Db) return new Gob(gob).Scene == scene;
        DbId o = Obj(gob), s = Facade.SceneId(scene);
        return !o.IsNull && !s.IsNull && SceneDb.ObjScene[o.Index].Value == s.Value;
    }

    /// <summary>The engine's live look-at on the body, not yet finished.</summary>
    internal static bool LookingAt(IntPtr gob)
    {
        if (Db) return DbAnim.LookingAt(Obj(gob));
        LookAtAnimation look = new Gob(gob).LookAt;
        return !look.IsNull && look.Finished == 0;
    }

    /// <summary>Every bone the body's part range names by <paramref name="rig"/>'s names, first match wins. DB mode
    /// scans the range by node name.</summary>
    internal static void FindBones(IntPtr gob, IntPtr root, byte[][] rig, ref PoseService.Bones bones)
    {
        if (!Db) { PoseService.FindBones(root, rig, ref bones, 0); return; }
        FindBones(Obj(gob), rig, ref bones);
    }

    /// <inheritdoc cref="FindBones(IntPtr, IntPtr, byte[][], ref PoseService.Bones)"/>
    internal static void FindBones(DbId o, byte[][] rig, ref PoseService.Bones bones)
    {
        if (o.IsNull) return;
        int first = SceneDb.ObjPartFirst[o.Index], n = SceneDb.ObjPartCount[o.Index];
        byte* name = stackalloc byte[MaxName + 1];
        for (int p = first; p < first + n; p++)
        {
            ModelDef? m = SceneDb.PartModel[p];
            int node = SceneDb.PartNode[p];
            if (m == null || (uint)node >= (uint)m.Nodes.Length) continue;
            string s = m.Nodes[node].Name;
            if (s.Length > MaxName) continue;
            for (int k = 0; k < s.Length; k++) name[k] = (byte)s[k];
            name[s.Length] = 0;
            int bone = PoseService.BoneIndex(name, rig);
            if (bone >= 0 && bones[bone] == IntPtr.Zero) bones[bone] = Handle(p);
        }
    }

    /// <summary>Longest node name tested as a bone: an MDL node name is a <c>char[32]</c>.</summary>
    private const int MaxName = 31;

    /// <summary>The body's model name as a <see cref="RigMap"/> cache key; zero for no model.</summary>
    internal static (ulong, ulong) ModelKey(IntPtr gob)
    {
        if (!Db) { Model m = new Gob(gob).Node; return m.IsNull ? default : RigMap.Key(m.Chars); }
        DbId o = Obj(gob);
        return o.IsNull || SceneDb.ObjModel[o.Index] is not { } d ? default : RigMap.Key(d.Name);
    }

    /// <summary>The body's model and its supermodels' names, nearest first. Cold: it allocates.</summary>
    internal static List<string> ModelChain(IntPtr gob)
    {
        var chain = new List<string>();
        if (!Db)
        {
            for (Model m = new Gob(gob).Node; !m.IsNull && chain.Count < RigMap.MaxChain; m = m.Super)
                chain.Add(Marshal.PtrToStringAnsi((IntPtr)m.Chars) ?? "");
            return chain;
        }
        DbId o = Obj(gob);
        for (ModelDef? d = o.IsNull ? null : SceneDb.ObjModel[o.Index]; d != null && chain.Count < RigMap.MaxChain; d = d.Super)
            chain.Add(d.Name);
        return chain;
    }

    /// <summary>Runs on the body's queue, oldest first and newest last as the engine lists them.</summary>
    internal static int RunCount(IntPtr gob) => Db ? DbAnim.RunCount(Obj(gob)) : new Gob(gob).RunCount;

    /// <summary>Run <paramref name="i"/> in the engine's order (DB mode stores the newest first).</summary>
    internal static RunRef Run(IntPtr gob, int i)
    {
        if (!Db) return new RunRef(new Gob(gob).Run(i));
        DbId o = Obj(gob);
        int n = DbAnim.RunCount(o);
        return (uint)i < (uint)n ? new RunRef(DbAnim.RunSlot(o, n - 1 - i)) : default;
    }
}

/// <summary>One animation run, an engine <c>AnimRun</c> or a DB pool slot (carried as <c>slot + 1</c>).</summary>
internal readonly unsafe struct RunRef
{
    private readonly IntPtr _e;
    private readonly int _d;

    internal RunRef(AnimRun e) { _e = e.Ptr; _d = 0; }
    internal RunRef(int slot) { _e = IntPtr.Zero; _d = slot + 1; }

    public bool IsNull => _d == 0 ? _e == IntPtr.Zero : DbAnim.RunAnim(_d - 1) == null;

    public float Rate
    {
        get => _d != 0 ? DbAnim.RunRate(_d - 1) : new AnimRun(_e).Rate;
        set { if (_d != 0) DbAnim.RunRate(_d - 1) = value; else { AnimRun r = new(_e); r.Rate = value; } }
    }

    public float Time
    {
        get => _d != 0 ? DbAnim.RunTime(_d - 1) : new AnimRun(_e).Time;
        set { if (_d != 0) DbAnim.RunTime(_d - 1) = value; else { AnimRun r = new(_e); r.Time = value; } }
    }

    public float Blend
    {
        get => _d != 0 ? DbAnim.RunBlend(_d - 1) : new AnimRun(_e).Blend;
        set { if (_d != 0) DbAnim.RunBlend(_d - 1) = value; else { AnimRun r = new(_e); r.Blend = value; } }
    }

    public float BlendTime
    {
        get => _d != 0 ? DbAnim.RunFade(_d - 1) : new AnimRun(_e).BlendTime;
        set { if (_d != 0) DbAnim.RunFade(_d - 1) = value; else { AnimRun r = new(_e); r.BlendTime = value; } }
    }

    /// <summary>The <c>AnimRun+0xc</c> bits; DB mode keeps the engine's own.</summary>
    public uint Flags => _d != 0 ? (uint)DbAnim.RunFlagsOf(_d - 1) : new AnimRun(_e).Flags;

    /// <summary>The animation's length in seconds, 0 when there is none.</summary>
    public float Length
    {
        get
        {
            if (_d != 0) return DbAnim.RunAnim(_d - 1)?.Length ?? 0f;
            Animation a = new AnimRun(_e).Animation;
            return a.IsNull ? 0f : a.Length;
        }
    }

    /// <summary>The run's identity across frames: the <c>AnimRun*</c>, or in DB mode the animation it plays
    /// (pool slots shift as runs come and go, so a slot is no identity).</summary>
    public long Id => _d != 0 ? DbKey : (long)_e;

    /// <summary>The animation the run plays, as a key: its <c>Animation*</c>, or the DB animation's identity.</summary>
    public long AnimKey
    {
        get
        {
            if (_d != 0) return DbKey;
            Animation a = new AnimRun(_e).Animation;
            return (long)a.Ptr;
        }
    }

    private long DbKey
    {
        get
        {
            AnimDef? a = DbAnim.RunAnim(_d - 1);
            return a == null ? 0L : (1L << 40) | (uint)RuntimeHelpers.GetHashCode(a);
        }
    }

    /// <summary>Do the two runs play an animation of the same name (two models load their own animations)?</summary>
    public bool SameName(in RunRef other)
    {
        if (IsNull || other.IsNull) return false;
        if (_d != 0 || other._d != 0)
        {
            AnimDef? a = _d != 0 ? DbAnim.RunAnim(_d - 1) : null, b = other._d != 0 ? DbAnim.RunAnim(other._d - 1) : null;
            return a != null && b != null && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
        Animation x = new AnimRun(_e).Animation, y = new AnimRun(other._e).Animation;
        if (x.IsNull || y.IsNull) return false;
        byte* p = x.Chars, q = y.Chars;
        for (int i = 0; i < MaxName; i++)
        {
            if (p[i] != q[i]) return false;
            if (p[i] == 0) return true;
        }
        return true;
    }

    /// <summary>Is the animation one the pose layer leaves alone (<see cref="PoseService.IsDenied"/>)?</summary>
    public bool Denied
    {
        get
        {
            if (_d == 0)
            {
                Animation a = new AnimRun(_e).Animation;
                return !a.IsNull && PoseService.IsDenied(a.Chars);
            }
            AnimDef? d = DbAnim.RunAnim(_d - 1);
            if (d == null || d.Name.Length > MaxName) return false;
            byte* s = stackalloc byte[MaxName + 1];
            for (int i = 0; i < d.Name.Length; i++) s[i] = (byte)d.Name[i];
            s[d.Name.Length] = 0;
            return PoseService.IsDenied(s);
        }
    }

    /// <summary>Longest animation name compared.</summary>
    private const int MaxName = 64;
}
