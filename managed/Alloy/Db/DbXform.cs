using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary><b>The transform step.</b> <c>ObjWorld</c> from the placement columns plus the attach chain and
/// <c>PartWorld</c> down each part tree, visiting only what moved (ADR 0028 section 4). A leaf is
/// <c>S_leaf T_leaf ... S_obj T_obj</c>, row-vector, translation in the fourth row; every scale is uniform, so
/// a part's matrix is its local times its parent's world -- a flat pre-order walk. GAME thread only.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>The compose generation: a per-object stamp says "already composed under this generation"
    /// without clearing an array. Nothing in a live frame bumps it -- staleness is the authority
    /// (<see cref="Stale"/> for the object's placement, <see cref="_partClock"/> for a part pose). It stays as
    /// the one way to force a whole pass, and a recycled slot's zero stamp still reads as never composed.</summary>
    private static int _poseGen = 1;
    /// <summary>Per object: <see cref="DbTable.MarkClock"/> of the parts table as its last compose left it. A
    /// part whose <see cref="DbTable.MarkAt"/> is past this was marked AFTER that compose -- the one change
    /// <see cref="Stale"/> cannot see, since a part pose is not in the object's placement. Nothing invalidates
    /// it: the clock is monotonic, and <see cref="OnCreate"/> resets an object's stamp to -1.</summary>
    private static readonly long[] _partClock = Unstamped();

    /// <inheritdoc cref="_partClock"/>
    private static long[] Unstamped()
    {
        var a = new long[SceneDb.MaxObjects];
        Array.Fill(a, -1L);
        return a;
    }
    /// <summary>How far into <see cref="DbTable.DirtyCount"/> the last compose got. The dirty list is
    /// seam-feed's to consume at the present boundary, so this only READS it -- and an on-demand compose after
    /// a setter walks the tail rather than the whole list.</summary>
    private static int _dirtyUpTo, _partDirtyUpTo, _dirtyTakes, _partDirtyTakes;
    private static int _composed, _composedParts;
    /// <summary>Per object: how many times it has been composed. <see cref="HitFeed"/> refreshes a hit box only
    /// when this moved since its last fold.</summary>
    internal static readonly int[] ComposeCount = new int[SceneDb.MaxObjects];

    /// <summary>Compose everything that has changed since the last pass. Idempotent and cheap to repeat: a
    /// still frame walks an empty tail and composes nothing.</summary>
    private static void ComposePending()
    {
        DbTable t = SceneDb.Objects;
        int n = t.DirtyCount;
        // seam-feed took the list at the last present, so the tail restarted -- by the take count, not the length:
        // a list refilled past the old cursor before this compose (a panel rebuilt) skipped its first entries.
        if (t.DirtyTakes != _dirtyTakes) { _dirtyTakes = t.DirtyTakes; _dirtyUpTo = 0; }
        for (int k = _dirtyUpTo; k < n; k++)
        {
            int i = t.DirtyIndex(k);
            if (!t.LiveAt(i)) continue;
            if ((t.DirtyAt(i) & (Dirty.Xform | Dirty.Tree | Dirty.Anim | Dirty.Created)) == 0) continue;
            ComposeIndex(i);
        }
        _dirtyUpTo = n;

        // ...and the PARTS list, because a part-only setter (a pose written by something other than this
        // runtime, a subtree enable, a merged-away bit) marks the part and not its object -- so the object's
        // own dirty entry may never appear and its part matrices would stay as they were.
        DbTable pt = SceneDb.Parts;
        int pn = pt.DirtyCount;
        if (pt.DirtyTakes != _partDirtyTakes) { _partDirtyTakes = pt.DirtyTakes; _partDirtyUpTo = 0; }
        for (int k = _partDirtyUpTo; k < pn; k++)
        {
            int p = pt.DirtyIndex(k);
            if (!pt.LiveAt(p)) continue;
            if ((pt.DirtyAt(p) & (Dirty.Xform | Dirty.Anim | Dirty.Tree)) == 0) continue;
            DbId o = SceneDb.PartObj[p];
            if (!SceneDb.Objects.Valid(o)) continue;
            // CX-188: the mark has to be NEWER than the object's last compose. The walk restarts from 0 at the
            // boundary (a part behind the cursor can be re-marked), so without this test every object holding
            // any dirty part recomposed once a frame -- and most of those marks were folded in long before, or
            // were written BY the compose itself (the static restage mark below).
            if (pt.MarkAt(p) <= _partClock[o.Index]) continue;
            // The mark is the signal ComposeIndex's own staleness test cannot read, so say it in the one
            // language that test speaks. Exactly one compose per object: it re-stamps _partClock, and this
            // object's other newly marked parts are then older than the stamp.
            _flags[o.Index] |= ObjAnim.Moved;
            ComposeIndex(o.Index);
        }
        _partDirtyUpTo = pn;
    }

    /// <summary>A getter's compose: everything pending, then THIS object even when the dirty tail no longer
    /// holds it. An object moved twice in one pass is on the dirty list once, behind the tail cursor after the
    /// first compose, so <see cref="Compose"/> alone would answer its old matrix.</summary>
    public static void ComposeFor(DbId obj)
    {
        ComposePending();
        ComposeObject(obj);
    }

    /// <summary>Compose one object now (the end of its animate step, and what a getter asks for).</summary>
    private static void ComposeObject(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        ComposeIndex(obj.Index);
    }

    /// <summary>The placement the last compose was taken from, so "is this matrix still current" is three
    /// compares and needs no cooperation from whoever wrote the columns. The seam's own setters mark
    /// <c>Dirty.Xform</c> and nothing else -- they have no reason to know about this file -- so a bit of our
    /// own would miss them.</summary>
    private static readonly Vector3[] _fromPos = new Vector3[SceneDb.MaxObjects];
    private static readonly Quaternion[] _fromRot = new Quaternion[SceneDb.MaxObjects];
    private static readonly float[] _fromScale = new float[SceneDb.MaxObjects];

    /// <summary>The attach-chain scale the last compose folded in, so an ANCESTOR's visual scale changing is
    /// staleness too -- <c>Ramp</c> marks the ancestor, and nothing marks its descendants.</summary>
    private static readonly float[] _fromAttach = new float[SceneDb.MaxObjects];

    /// <summary>Is <paramref name="i"/>'s composed matrix still the one its columns imply?</summary>
    private static bool Stale(int i)
        => (_flags[i] & ObjAnim.Moved) != 0
        || SceneDb.ObjPos[i] != _fromPos[i]
        || SceneDb.ObjRot[i] != _fromRot[i]
        || SceneDb.ObjScale[i] != _fromScale[i]
        || AttachScale(i) != _fromAttach[i];

    private static void ComposeIndex(int i)
    {
        // CX-120 (gui): an attached object with no behaviour is placed by its parent's part, and that write has
        // to land BEFORE the staleness test or a moving parent would never recompose the child. DbXform.Attach.cs.
        SnapAttached(i);
        // The generation dedups within one pass; staleness is the authority, so a setter that ran after this
        // object was composed -- anybody's setter -- is still honoured.
        if (_composeAt[i] == _poseGen && !Stale(i)) return;
        _composeAt[i] = _poseGen;
        // The attach link, noted wherever it is read: the death edge sweeps only the parents something is
        // actually attached to, and this is the one place every attached object passes through.
        DbId attachedTo = SceneDb.ObjAttachTo[i];
        if (!attachedTo.IsNull) NoteAttached(attachedTo);
        float attach = AttachScale(i);
        _fromPos[i] = SceneDb.ObjPos[i];
        _fromRot[i] = SceneDb.ObjRot[i];
        _fromScale[i] = SceneDb.ObjScale[i];
        _fromAttach[i] = attach;
        _flags[i] &= ~ObjAnim.Moved;
        _composed++;
        ComposeCount[i]++;

        Vector3 pos = SceneDb.ObjPos[i];
        Quaternion rot = SceneDb.ObjRot[i];
        float scale = SceneDb.ObjScale[i];
        bool lerping = false;
        if (_vt[i]) ObjectBridge.ObjectVisualAdjust?.Invoke(SceneDb.Objects.IdAt(i), ref pos, ref rot, ref scale, out lerping);
        _vtLerp[i] = lerping;
        SceneDb.ObjWorld[i] = Build(scale * attach, rot, pos);
        if (!(attach > 1e-3f) && Grafted(i)) VanishedGraft(i, attach);
        Sphere(i);
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        if (n > 0)
        {
            Matrix4x4 root = SceneDb.ObjWorld[i];
            // a static object's fold does not fan an anim mark out to every part: only a part this moved is restaged
            bool still = (SceneDb.ObjFlags[i] & ObjFlag.Static) != 0;
            for (int k = 0; k < n; k++)
            {
                int p = first + k;
                int parent = SceneDb.PartParent[p];
                Matrix4x4 up = parent >= 0 ? SceneDb.PartWorld[parent] : root;
                Matrix4x4 world = Build(SceneDb.PartScale[p], SceneDb.PartRot[p], SceneDb.PartPos[p]) * up;
                if (still && world != SceneDb.PartWorld[p]) SceneDb.Parts.Mark(p, Dirty.Anim);
                SceneDb.PartWorld[p] = world;
            }
            _composedParts += n;
        }
        // AFTER the part walk, so the restage marks it just made (and any other mark taken during this
        // compose) count as folded in rather than as a reason to compose again next pass.
        _partClock[i] = SceneDb.Parts.MarkClock;
        // LAST, with this object's parts current: the attached children ride them, and the compose visits only
        // what changed -- a child is not marked by its parent's move. DbXform.Attach.cs.
        PlaceChildren(i);
    }

    /// <summary>The object's world cull sphere (<see cref="SceneDb.ObjSphere"/>), which the feed's visible-set
    /// index files objects by -- an object without one is never drawn. Centre is the world origin, radius the
    /// model's stated bound about that origin times the composed scale: the exporter writes those bounds from
    /// the ANIMATED geometry, so the sphere over-reports and can never cull something that should draw.</summary>
    private static void Sphere(int i)
    {
        Matrix4x4 w = SceneDb.ObjWorld[i];
        var centre = new Vector3(w.M41, w.M42, w.M43);
        ModelDef? m = SceneDb.ObjModel[i];
        float r = m == null ? 0f : Bound(m);
        SceneDb.ObjSphere[i] = new Vector4(centre, r * ScaleOf(w));
    }

    /// <summary>The model's bounding radius ABOUT ITS ORIGIN: the furthest of the eight bound corners, because
    /// a part tree is placed at the object's origin and not at the bound's centre. <see cref="ModelDef.Radius"/>
    /// is used when the bounds are empty.</summary>
    private static float Bound(ModelDef m)
    {
        Vector3 a = m.BMin, b = m.BMax;
        float x = MathF.Max(MathF.Abs(a.X), MathF.Abs(b.X));
        float y = MathF.Max(MathF.Abs(a.Y), MathF.Abs(b.Y));
        float z = MathF.Max(MathF.Abs(a.Z), MathF.Abs(b.Z));
        float r = MathF.Sqrt(x * x + y * y + z * z);
        return r > 0f ? r : m.Radius;
    }

    /// <summary>The one placement term <c>Part::GetWorld</c> does not carry: the engine's gob loop in
    /// <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 climbs the attach chain and multiplies each ancestor's
    /// visual-transform SCALE -- not its adjusted scale -- into the pushed scale. 1.0 without an attachment; a
    /// grafted piece (<see cref="Grafted"/>) is in its creature's ONE tree, so it takes the anchor's world scale.</summary>
    private static float AttachScale(int i)
    {
        if (Grafted(i))
        {
            int a = SceneDb.ObjAttachPart[i];
            return SceneDb.Parts.LiveAt(a) ? ScaleOf(SceneDb.PartWorld[a]) : 1f;
        }
        float s = 1f;
        DbId up = SceneDb.ObjAttachTo[i];
        for (int hop = 0; hop < MaxAttach && SceneDb.Objects.Valid(up); hop++)
        {
            s *= _visScale[up.Index] * (ObjectBridge.ObjectVisualScale?.Invoke(up) ?? 1f);
            up = SceneDb.ObjAttachTo[up.Index];
        }
        return s;
    }

    /// <summary>A grafted body piece whose anchor's world scale is zero or not a number draws nothing -- the shape
    /// of a creature missing a forearm and hand while its bicep stands. Said once an object, budgeted, with the
    /// anchor's own animated scale and rotation, so the next report names its cause.</summary>
    private static void VanishedGraft(int i, float attach)
    {
        if (_vanishedSaid >= 16 || _vanishedSeen[i] == SceneDb.Objects.IdAt(i).Value) return;
        _vanishedSeen[i] = SceneDb.Objects.IdAt(i).Value;
        _vanishedSaid++;
        int a = SceneDb.ObjAttachPart[i];
        DbId up = SceneDb.ObjAttachTo[i];
        _log?.Always($"DbAnim: grafted piece \"{SceneDb.ObjModel[i]?.Name}\" on \"{SceneDb.NodeNameOf(a)}\" of "
            + $"\"{(SceneDb.Objects.Valid(up) ? SceneDb.ObjModel[up.Index]?.Name : "-")}\" composes at scale {attach} -- it draws "
            + $"NOTHING (anchor part scale {SceneDb.PartScale[a]}, rot {SceneDb.PartRot[a]}, creature scale {SceneDb.ObjScale[up.Index]})");
    }
    private static int _vanishedSaid;
    private static readonly uint[] _vanishedSeen = new uint[SceneDb.MaxObjects];

    /// <summary>Is <paramref name="o"/> a <c>Gob::Append</c>ed piece -- part of its parent's one tree in the engine,
    /// a child object here? A reference node's model is composed the same way but is its own gob in the engine
    /// (<c>PartReference</c>), so it is not one.</summary>
    internal static bool Grafted(int o)
    {
        if ((SceneDb.ObjSeamFlags[o] & (uint)SeamFlag.Composed) == 0) return false;
        int a = SceneDb.ObjAttachPart[o];
        return (uint)a >= (uint)SceneDb.MaxParts || SceneDb.PartRefObj[a].Value != SceneDb.Objects.IdAt(o).Value;
    }

    /// <summary>The object a grafted piece belongs to in the engine's terms: its parent chain climbed while
    /// <see cref="Grafted"/>. Itself for everything else.</summary>
    internal static int GraftRoot(int o)
    {
        for (int hop = 0; hop < MaxAttach && Grafted(o); hop++)
        {
            DbId up = SceneDb.ObjAttachTo[o];
            if (!SceneDb.Objects.Valid(up)) break;
            o = up.Index;
        }
        return o;
    }

    /// <summary>A similarity transform as a matrix, in the engine's own convention: row-vector, translation in
    /// the fourth row (<c>MakeMatrix4x4</c> @0x6b8ce0, which matches Numerics term for term).</summary>
    internal static Matrix4x4 Build(float s, Quaternion q, Vector3 p)
        => Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);

    /// <summary>The uniform scale a composed matrix carries: its first row's length. What a local radius is
    /// multiplied by to bound the part the matrix actually draws.</summary>
    public static float ScaleOf(in Matrix4x4 m) => new Vector3(m.M11, m.M12, m.M13).Length();

    // ---- the on-demand getters the seam's by-value slots answer from ----

    /// <summary>The object's composed world matrix, composing first if a setter has run since the last pass.
    /// This is what makes <c>GetPosition</c> after <c>SetPosition</c> answer the new value without waiting for
    /// a frame.</summary>
    public static Matrix4x4 ObjectWorld(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return Matrix4x4.Identity;
        int i = obj.Index;
        ComposeIndex(i);
        return SceneDb.ObjWorld[i];
    }

    /// <summary>Part <paramref name="part"/>'s composed world matrix (an absolute index into
    /// <see cref="SceneDb.Parts"/>), composing its object first if anything moved.</summary>
    public static Matrix4x4 PartWorld(int part)
    {
        if ((uint)part >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(part)) return Matrix4x4.Identity;
        DbId obj = SceneDb.PartObj[part];
        if (SceneDb.Objects.Valid(obj)) ComposeIndex(obj.Index);
        return SceneDb.PartWorld[part];
    }

    /// <summary><c>Part::GetWorld(Vector&amp;, Quaternion&amp;)</c> (Part vtbl +0x20), the one Part member the
    /// game layer calls -- from <c>GuiAurAttachToObject</c>, <c>GuiAttachViewport</c> and the two cross-scene
    /// attach solvers. Decomposed from the composed matrix, so it answers exactly what the renderer draws.</summary>
    public static void PartGetWorld(int part, out Vector3 pos, out Quaternion rot)
    {
        Matrix4x4 m = PartWorld(part);
        pos = new Vector3(m.M41, m.M42, m.M43);
        rot = RotOf(m);
    }

    /// <summary>The rotation a composed matrix carries, as a UNIT quaternion. The matrix is a similarity
    /// transform, so its basis rows are <c>s</c> times a rotation's and
    /// <see cref="Quaternion.CreateFromRotationMatrix"/> would answer norm <c>sqrt(s)</c>; the attach solver
    /// writes this into <c>ObjRot</c>, where a non-unit one scales the attached piece a second time.</summary>
    internal static Quaternion RotOf(in Matrix4x4 m)
    {
        float s = ScaleOf(m);
        if (!(s > 1e-12f)) return Quaternion.Identity;
        float k = 1f / s;
        var b = new Matrix4x4(m.M11 * k, m.M12 * k, m.M13 * k, 0f,
                              m.M21 * k, m.M22 * k, m.M23 * k, 0f,
                              m.M31 * k, m.M32 * k, m.M33 * k, 0f,
                              0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(b));
    }

    /// <summary>The parent-relative rotation that composes to <paramref name="world"/> under
    /// <paramref name="parentWorld"/>: a part's world is its local times its parent's, which is
    /// <c>parent * local</c> in quaternion terms, so the local is <c>parent⁻¹ * world</c>.</summary>
    internal static Quaternion LocalRot(Quaternion world, in Matrix4x4 parentWorld)
        => Quaternion.Normalize(Quaternion.Inverse(RotOf(parentWorld)) * world);

    /// <summary>The part of <paramref name="obj"/> whose node is named <paramref name="name"/>, as an absolute
    /// part index, or -1 (<c>Gob::GetPart(char const*)</c>, Gob vtbl +0x238). Case-insensitive, and scoped to
    /// the parts instanced from the object's own model so a reference sub-tree cannot shadow a name.</summary>
    public static int PartByName(DbId obj, string? name)
    {
        if (!SceneDb.Objects.Valid(obj) || string.IsNullOrEmpty(name)) return -1;
        int i = obj.Index;
        ModelDef? model = SceneDb.ObjModel[i];
        if (model == null) return -1;
        int node = model.Find(name);
        if (node < 0) return -1;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            if (SceneDb.PartNode[p] == node && ReferenceEquals(SceneDb.PartModel[p], model)) return p;
        }
        return -1;
    }

    /// <summary>The transform step's period numbers: how many objects and parts a frame actually recomposed.
    /// In a still scene both read 0, which makes the change-driven claim checkable.</summary>
    internal static (int Objects, int Parts) TakeComposeCounts()
    {
        var r = (_composed, _composedParts);
        _composed = 0; _composedParts = 0;
        return r;
    }
}
