using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Mdl;
using CX.Services.Anim;

namespace CX.Alloy.Db;

/// <summary>A controller the part-pose columns do not carry (a light's colour or radius, an emitter's rate):
/// the sampled value, its track type and the run's weight, for the stream that owns that record's columns.
/// <paramref name="part"/> is an index into <see cref="SceneDb.Parts"/>.</summary>
public delegate void DbChannelWrite(int part, int type, Vector4 value, float weight);

/// <summary>The pose pass: one run applied to one object's part range. This is <c>AnimateHierarchy</c> @0x499990
/// plus the seven <c>InternalControl</c> bodies, with the tree recursion and the per-node sibling probe deleted --
/// the part range is pre-order and contiguous (<see cref="SceneDb.ObjPartFirst"/>) and the cached binding already
/// says which animation node drives which model node, so the pass is one linear walk over three columns.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>The controller types the part-pose columns carry. A file id is the ORIGINAL 32-bit <c>Part</c>
    /// field offset, which the engine's loader @0x5c01b0 remaps to this build's layout (base nodes +8, mesh nodes
    /// +0x18/+0x20). CX keeps the FILE id: the same number on both OSes, since the remap is where the engine's
    /// per-OS <c>Part</c> layout enters and the DB has no such layout.</summary>
    private const int CtlPosition = Ctl.Position, CtlOrientation = Ctl.Orientation, CtlScale = Ctl.Scale;
    /// <inheritdoc cref="CtlPosition"/>
    private const int CtlSelfIllum = Ctl.SelfIllumColor, CtlAlpha = Ctl.Alpha;

    /// <summary><see cref="SceneDb.PartLitAnim"/>'s three column bits, and <see cref="LitKeyed"/>: a light track
    /// with more than one key has driven the part, so its value never settles -- the bake's
    /// <see cref="CX.Alloy.LightmapBake.AnimationDrives"/> test.</summary>
    internal const byte LitColour = 1, LitRadius = 2, LitMul = 4, LitKeyed = 8;

    private static int _litWrites;

    /// <summary>Light tracks with no DB column (shadow radius, vertical displacement), sampled by nobody.
    /// Named in the period line so "that light does not flicker right" has a line to check.</summary>
    private static int _litUncarried;

    /// <summary>The light terms the feed reads for part <paramref name="p"/>: the animated column once an
    /// animation has written it, the node's rest key until then. These are the three entries
    /// <c>DbInputs.Light</c> calls in place of its own <c>Rest3</c>/<c>Rest1</c> -- see
    /// <c>cx-wt/notes/seam-anim-light-columns.md</c>.</summary>
    public static Vector3 LitColour3(int p, Vector3 rest)
        => (uint)p < (uint)SceneDb.MaxParts && (SceneDb.PartLitAnim[p] & LitColour) != 0
            ? SceneDb.PartLitColour[p] : rest;

    /// <inheritdoc cref="LitColour3"/>
    public static float LitRadius1(int p, float rest)
        => (uint)p < (uint)SceneDb.MaxParts && (SceneDb.PartLitAnim[p] & LitRadius) != 0
            ? SceneDb.PartLitRadius[p] : rest;

    /// <inheritdoc cref="LitColour3"/>
    public static float LitMul1(int p, float rest)
        => (uint)p < (uint)SceneDb.MaxParts && (SceneDb.PartLitAnim[p] & LitMul) != 0
            ? SceneDb.PartLitMul[p] : rest;

    /// <summary>Does light part <paramref name="p"/> keep moving: a multi-key colour, radius or multiplier track
    /// on its model node, or on an animation that has posed it. A single-key track settles and is not motion. The
    /// model-node half is <see cref="NodeDef.LitKeyed"/>, memoised on the node, because this is asked per light per
    /// <c>DbLights.Walk</c> and again per <c>Halos.EnrollDb</c> -- O(lights in the area), every frame.</summary>
    public static bool LitMoving(int p)
    {
        if ((uint)p >= (uint)SceneDb.MaxParts) return false;
        if ((SceneDb.PartLitAnim[p] & LitKeyed) != 0) return true;
        ModelDef? m = SceneDb.PartModel[p];
        int node = SceneDb.PartNode[p];
        if (m == null || (uint)node >= (uint)m.Nodes.Length) return false;
        return m.Nodes[node].LitKeyed;
    }

    /// <summary>Install a colour on a light part the way <c>Gob::ControlPartLightColor</c> @0x4436d0 does, which
    /// is NOT a poke of the colour field: the engine parses <c>"color %f %f %f"</c> into a temporary
    /// <c>MdlNode</c> and applies that node's controller, so the value behaves as a single-key CONTROLLER and
    /// overrides both the node's rest key and a tile light's area-palette colour from then on.</summary>
    public static void ControlLightColour(int p, Vector3 colour)
    {
        if ((uint)p >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(p)) return;
        SceneDb.PartLitAnim[p] |= LitColour;
        SceneDb.PartLitColour[p] = colour;
        SceneDb.Parts.Mark(p, Dirty.Material);
        _litWrites++;
    }

    /// <summary>The node's rest key for a controller: its value at key 0, which is what the light terms were
    /// before anything animated them and what the first blend has to start from.</summary>
    private static Vector3 Rest3(int p, int type, Vector3 def)
    {
        ControllerTrack? c = SceneDb.PartModel[p]?.Nodes[SceneDb.PartNode[p]].Controller(type);
        return c != null && c.Values.Length >= 3 ? new Vector3(c.Values[0], c.Values[1], c.Values[2]) : def;
    }

    /// <inheritdoc cref="Rest3"/>
    private static float Rest1(int p, int type, float def)
    {
        ControllerTrack? c = SceneDb.PartModel[p]?.Nodes[SceneDb.PartNode[p]].Controller(type);
        return c != null && c.Values.Length > 0 ? c.Values[0] : def;
    }

    /// <summary>Where a controller the pose columns do not carry goes. seam-feed installs this for light and
    /// emitter parts; null means such a track is sampled by nobody and is counted, not silently applied.</summary>
    public static DbChannelWrite? ChannelSink;

    private static int _offChannel;

    /// <summary>The inverse of the model/animation binding: model node index -> animation node index, so the pose
    /// walk can go over PARTS (a linear column walk) instead of over animation nodes. Keyed on the binding object,
    /// so it is collected with it when the model cache evicts the model.</summary>
    private static readonly ConditionalWeakTable<ModelCache.AnimBinding, int[]> _inverse = new();

    private static int[] Inverse(ModelCache.AnimBinding b)
    {
        if (_inverse.TryGetValue(b, out int[]? inv)) return inv;
        inv = new int[b.Model.Nodes.Length];
        for (int i = 0; i < inv.Length; i++) inv[i] = -1;
        int[] map = b.Node;
        for (int a = 0; a < map.Length; a++)
        {
            int n = map[a];
            // First writer wins, as the engine's parent-scoped match does: two animation nodes cannot bind the
            // same model node, and if a parser ever emits that, the earlier node is the one the match chose.
            if ((uint)n < (uint)inv.Length && inv[n] < 0) inv[n] = a;
        }
        _inverse.Add(b, inv);
        return inv;
    }

    /// <summary>Apply run <paramref name="s"/> of <paramref name="anim"/> to <paramref name="obj"/>'s parts at
    /// weight <paramref name="w"/>.</summary>
    private static void Pose(DbId obj, AnimDef anim, int s, float w)
    {
        int i = obj.Index;
        ModelDef? model = SceneDb.ObjModel[i];
        if (model == null) return;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        if (n <= 0) return;
        // CX-113's binding is per (model, animation) and cached for the model's lifetime; it maps animation
        // nodes to MODEL node indices, which is why the walk below pairs on PartNode and not on a part index.
        ModelCache.AnimBinding bind = ModelCache.Bind(model, anim);
        if (bind.Bound == 0) { _unbound++; return; }
        int[] inv = Inverse(bind);
        float t = _rTime[s], scale = _rPosScale[s];
        bool wrote = PoseTree(i, model, inv, anim, first, n, t, scale, w);
        // The SHOWN .lod alternate animates too, as the engine's does: SetLODModel swaps it in at Gob+0x90/+0x68,
        // the very pair AnimateHierarchy reads. Its own binding, by name, as a grafted piece gets.
        ModelDef? alt = SceneDb.ObjLodShown[i] switch
        {
            1 => SceneDb.ObjSeamLodModel1[i], 2 => SceneDb.ObjSeamLodModel2[i], _ => null,
        };
        if (alt != null && !ReferenceEquals(alt, model) && ModelCache.Bind(alt, anim) is { Bound: > 0 } ab)
            wrote |= PoseTree(i, alt, Inverse(ab), anim, first, n, t, scale, w);
        if (wrote) Moved(i);
        PoseComposition(model, inv, anim, obj, t, scale, w);
    }

    /// <summary>One model's parts of object <paramref name="i"/>'s range, posed through that model's binding.
    /// True when any part was written.</summary>
    private static bool PoseTree(int i, ModelDef model, int[] inv, AnimDef anim, int first, int n, float t, float scale, float w)
    {
        AnimNode[] nodes = anim.Nodes;
        bool wrote = false;
        // a STATIC object's records are restaged by what changed (the compose's world compare, the alpha and
        // self-illum compares), not by having been posed: a tile replaying a still pose restaged every part
        bool still = (SceneDb.ObjFlags[i] & ObjFlag.Static) != 0;

        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            // NOT gated on PartEnabled: the engine's enable byte is read by the draw, the shadow volume, the
            // hit check and the highlight, never by AnimateHierarchy -- and the parts it gates off are the
            // VFX anchors, the cloak rig and the attach dummies, whose world matrix other layers still need.
            if (SceneDb.PartMerged[p]) continue;
            // A part instanced from another ModelDef is a reference node's sub-tree; this animation's nodes do
            // not name it (SkinPose's gather makes the same refusal for the same reason).
            if (!ReferenceEquals(SceneDb.PartModel[p], model)) continue;
            int node = SceneDb.PartNode[p];
            if ((uint)node >= (uint)inv.Length) continue;
            int a = inv[node];
            if (a < 0) continue;
            AnimNode an = nodes[a];
            ControllerTrack[] tracks = an.Controllers;
            // The engine's animmesh rule, kept: a non-positive weight contributes nothing to a morph AND its
            // controller loop is skipped too.
            if (an.AnimMesh != null)
            {
                if (!(w > 0f)) continue;
                Morph(p, an.AnimMesh, t, w);
            }
            for (int c = 0; c < tracks.Length; c++) Control(p, tracks[c], t, w, scale);
            _controllers += tracks.Length;
            _posed++;
            wrote = true;
            if (!still || an.AnimMesh != null) SceneDb.Parts.Mark(p, Dirty.Anim);
        }
        return wrote;
    }

    /// <summary>
    /// The same animation over the pieces <c>Gob::Append</c> grafted onto this object -- a creature's torso, arms
    /// and cloak are each a child object, and without this they keep their REST pose. Each piece binds against its
    /// OWN model, as the engine's supermodel synchronise does by name, and is scoped to the ANCHOR as
    /// <c>AnimateHierarchy</c> @0x599990 is: from the animation root instead, a custom leg takes the hip track.
    /// </summary>
    private static void PoseComposition(ModelDef model, int[] baseInv, AnimDef anim, DbId obj, float t, float scale, float w)
    {
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            int ci = c.Index;
            ModelDef? cm = SceneDb.ObjModel[ci];
            int first = SceneDb.ObjPartFirst[ci], n = SceneDb.ObjPartCount[ci];
            if (cm == null || n <= 0) continue;
            int under = AnchorChild(model, baseInv, anim, SceneDb.ObjAttachPart[ci], cm);
            if (under < 0) continue;
            ModelCache.AnimBinding bind = ModelCache.BindUnder(cm, anim, under);
            if (bind.Bound == 0) continue;
            int[] inv = Inverse(bind);
            AnimNode[] nodes = anim.Nodes;
            bool wrote = false;
            for (int k = 0; k < n; k++)
            {
                int p = first + k;
                if (SceneDb.PartMerged[p]) continue;   // not PartEnabled: the pose ignores the engine's gate
                if (!ReferenceEquals(SceneDb.PartModel[p], cm)) continue;
                int node = SceneDb.PartNode[p];
                if ((uint)node >= (uint)inv.Length) continue;
                int a = inv[node];
                if (a < 0) continue;
                AnimNode an = nodes[a];
                ControllerTrack[] tracks = an.Controllers;
                if (an.AnimMesh != null)
                {
                    if (!(w > 0f)) continue;
                    Morph(p, an.AnimMesh, t, w);
                }
                for (int q = 0; q < tracks.Length; q++) Control(p, tracks[q], t, w, scale);
                _controllers += tracks.Length;
                _posed++;
                wrote = true;
                SceneDb.Parts.Mark(p, Dirty.Anim);
            }
            if (!wrote) continue;
            _graftPosed++;
            Moved(ci);
            // The piece is a scene member of its own, so the walk reaches it -- but only the pose above gives
            // it anything to do, and only this marks it as having moved.
            SceneDb.Objects.Mark(ci, Dirty.Anim);
        }
    }

    /// <summary>The animation node a grafted piece's root pairs with: the child of the anchor part's animation
    /// node that carries the root's name, or -1. A piece grafted onto the base model's ROOT keeps the old
    /// root-to-root pairing (0), which is what the root's own walk does.</summary>
    private static int AnchorChild(ModelDef model, int[] inv, AnimDef anim, int anchor, ModelDef piece)
    {
        if ((uint)anchor >= (uint)SceneDb.MaxParts || !ReferenceEquals(SceneDb.PartModel[anchor], model)) return -1;
        int node = SceneDb.PartNode[anchor];
        if (node == 0) return 0;
        int a = (uint)node < (uint)inv.Length ? inv[node] : -1;
        if (a < 0 || piece.Nodes.Length == 0) return -1;
        string root = piece.Nodes[0].Name;
        AnimNode[] an = anim.Nodes;
        for (int j = 0; j < an.Length; j++)
            if (an[j].Parent == a && string.Equals(an[j].Name, root, StringComparison.OrdinalIgnoreCase)) return j;
        return -1;
    }

    /// <summary>Pieces whose pose came from the object they are grafted onto, for the period line.</summary>
    private static int _graftPosed;

    /// <inheritdoc cref="_graftPosed"/>
    internal static int TakeComposedPieces() { int v = _graftPosed; _graftPosed = 0; return v; }

    /// <summary><c>NewController::Control</c> @0x498f20: sample the track and blend it into the column the
    /// controller's id names, by <paramref name="w"/>. A weight of 1 writes the key outright; below 1 it blends
    /// over whatever the previous run of this frame left there.</summary>
    private static void Control(int p, ControllerTrack c, float t, float w, float posScale)
    {
        // The three geometry controllers are the base node's own and mean the same thing on every kind.
        switch (c.Type)
        {
            case CtlPosition:
            {
                Vector3 v = SampleVector(c, t) * posScale;
                SceneDb.PartPos[p] = v * w + SceneDb.PartPos[p] * (1f - w);
                return;
            }
            case CtlOrientation:
            {
                Quaternion q = SampleQuat(c, t);
                SceneDb.PartRot[p] = Slerp(SceneDb.PartRot[p], q, w);
                return;
            }
            case CtlScale:
            {
                float v = SampleFloat(c, t);
                SceneDb.PartScale[p] = w * v + (1f - w) * SceneDb.PartScale[p];
                return;
            }
        }
        // Every other id is read against the node's KIND, because that is what the engine does: its loader
        // renumbers each keyword to a byte offset inside the node's own Part subclass, so one number names a
        // different field per kind. The file ids collide the same way -- 100 is a mesh's selfIllumColor AND a
        // light's verticalDisplacement (both engine +0x7c, MdlNodeTriMesh/MdlNodeLight ASCII parsers), 128 a
        // mesh's alpha and an emitter's fps, 88 a light's radius and an emitter's birth rate. Dispatching on
        // the id alone wrote every animated emitter rate into the light columns, and a torch that only burns
        // because its "equip" animation ramps the rate off a rest key of 0 never caught fire (CX-120).
        switch (SceneDb.PartKindOf[p])
        {
            case PartKind.Emitter:
                Channel(p, c, t, w);
                return;

            case PartKind.Light:
                switch (c.Type)
                {
                    case Ctl.LightColor:
                    {
                        if (c.Times.Length > 1) SceneDb.PartLitAnim[p] |= LitKeyed;
                        Vector3 v = SampleVector(c, t);
                        if ((SceneDb.PartLitAnim[p] & LitColour) == 0)
                        {
                            SceneDb.PartLitAnim[p] |= LitColour;
                            SceneDb.PartLitColour[p] = Rest3(p, Ctl.LightColor, Vector3.One);
                        }
                        SceneDb.PartLitColour[p] = v * w + SceneDb.PartLitColour[p] * (1f - w);
                        SceneDb.Parts.Mark(p, Dirty.Material);
                        _litWrites++;
                        return;
                    }
                    case Ctl.LightRadius:
                    {
                        if (c.Times.Length > 1) SceneDb.PartLitAnim[p] |= LitKeyed;
                        float v = SampleFloat(c, t);
                        if ((SceneDb.PartLitAnim[p] & LitRadius) == 0)
                        {
                            SceneDb.PartLitAnim[p] |= LitRadius;
                            SceneDb.PartLitRadius[p] = Rest1(p, Ctl.LightRadius, 1f);
                        }
                        SceneDb.PartLitRadius[p] = w * v + (1f - w) * SceneDb.PartLitRadius[p];
                        SceneDb.Parts.Mark(p, Dirty.Material);
                        _litWrites++;
                        return;
                    }
                    case Ctl.LightMultiplier:
                    {
                        if (c.Times.Length > 1) SceneDb.PartLitAnim[p] |= LitKeyed;
                        float v = SampleFloat(c, t);
                        if ((SceneDb.PartLitAnim[p] & LitMul) == 0)
                        {
                            SceneDb.PartLitAnim[p] |= LitMul;
                            SceneDb.PartLitMul[p] = Rest1(p, Ctl.LightMultiplier, 1f);
                        }
                        SceneDb.PartLitMul[p] = w * v + (1f - w) * SceneDb.PartLitMul[p];
                        SceneDb.Parts.Mark(p, Dirty.Material);
                        _litWrites++;
                        return;
                    }
                    // A light's other two tracks (shadowRadius 96, verticalDisplacement 100) have no DB
                    // consumer. Counted rather than handed to the emitter sink: a block there is keyed by
                    // PART, and spending the sink's 256 on lights nothing reads would refuse a real emitter.
                    default:
                        _litUncarried++;
                        return;
                }

            case PartKind.Mesh:
            case PartKind.Skin:
            case PartKind.Dangly:
            case PartKind.AnimMesh:
            case PartKind.Aabb:
            case PartKind.Merged:
                switch (c.Type)
                {
                    case CtlAlpha:
                    {
                        float v = SampleFloat(c, t), was = SceneDb.PartAlpha[p];
                        SceneDb.PartAlpha[p] = w * v + (1f - w) * was;
                        if (SceneDb.PartAlpha[p] != was) SceneDb.Parts.Mark(p, Dirty.Material);
                        return;
                    }
                    case CtlSelfIllum:
                    {
                        Vector3 v = SampleVector(c, t), was = SceneDb.PartSelfIllum[p];
                        SceneDb.PartSelfIllum[p] = v * w + was * (1f - w);
                        if (SceneDb.PartSelfIllum[p] != was) SceneDb.Parts.Mark(p, Dirty.Material);
                        return;
                    }
                    default:
                        Channel(p, c, t, w);
                        return;
                }

            default:
                Channel(p, c, t, w);
                return;
        }
    }

    /// <summary>A track no column of its own carries: sampled and handed to the sink
    /// (<see cref="DbEmitterChannels"/>), or counted when nobody installed one.</summary>
    private static void Channel(int p, ControllerTrack c, float t, float w)
    {
        DbChannelWrite? sink = ChannelSink;
        if (sink == null) { _offChannel++; return; }
        Vector4 v;
        switch (c.Columns)
        {
            case 1: v = new Vector4(SampleFloat(c, t), 0f, 0f, 0f); break;
            case 3: { Vector3 u = SampleVector(c, t); v = new Vector4(u, 0f); break; }
            case 4: { Quaternion q = SampleQuat(c, t); v = new Vector4(q.X, q.Y, q.Z, q.W); break; }
            default: return;
        }
        sink(p, c.Type, v, w);
    }

    // ================= the key math =================

    /// <summary>The engine's key search as <see cref="CX.Alloy.AnimKeys"/> transcribes it: a binary
    /// search where the engine scans, which its own self-check proves identical.</summary>
    private static void Key(ControllerTrack c, float t, out int idx, out float alpha)
    {
        fixed (float* times = c.Times) AnimKeys.Key(times, c.Times.Length, t, out idx, out alpha);
    }

    /// <summary>A scalar track at <paramref name="t"/>: the engine's three branches
    /// (<c>GetFloatValue</c> @0x498480), or the cubic Bezier for a Bezier-authored track.</summary>
    private static float SampleFloat(ControllerTrack c, float t)
    {
        int n = c.Times.Length;
        if (n == 0 || c.Values.Length == 0) return 0f;
        int st = Rows(c) * c.Columns;
        Key(c, t, out int idx, out float a);
        if (idx == 0) return c.Values[0];
        if (idx >= n) return c.Values[(n - 1) * st];
        int k = (idx - 1) * st;
        if (c.Bezier) return Bezier(c.Values[k], c.Values[k + 2 * c.Columns],
                                    c.Values[k + st], c.Values[k + st + c.Columns], a);
        return (1f - a) * c.Values[k] + a * c.Values[k + st];
    }

    /// <summary>A vector track at <paramref name="t"/> (<c>GetVectorValue</c> @0x498620). The position-scale
    /// multiply is the caller's, because only the POSITION controller takes it.</summary>
    private static Vector3 SampleVector(ControllerTrack c, float t)
    {
        int n = c.Times.Length;
        if (n == 0 || c.Values.Length < 3) return default;
        int st = Rows(c) * c.Columns;
        if (!c.Bezier && n >= 3 && AnimKnobs.Curves.V)
            if (st == 3 && KeyCurveService.Position(c.Times, c.Values, n, t, out Vector3 curve)) return curve;
        Key(c, t, out int idx, out float a);
        if (idx == 0) return V3(c.Values, 0);
        if (idx >= n) return V3(c.Values, (n - 1) * st);
        int k = (idx - 1) * st;
        if (c.Bezier)
        {
            // Rows are (value, in-tangent, out-tangent), each `Columns` wide: the near key's OUT row is two
            // columns on, and the far key's IN row one column past its value.
            int o = 2 * c.Columns, j = k + st, m = c.Columns;
            return new Vector3(Bezier(c.Values[k], c.Values[k + o], c.Values[j], c.Values[j + m], a),
                               Bezier(c.Values[k + 1], c.Values[k + o + 1], c.Values[j + 1], c.Values[j + m + 1], a),
                               Bezier(c.Values[k + 2], c.Values[k + o + 2], c.Values[j + 2], c.Values[j + m + 2], a));
        }
        return V3(c.Values, k) * (1f - a) + V3(c.Values, k + st) * a;
    }

    /// <summary>A rotation track at <paramref name="t"/> (<c>GetQuaternionValue</c> @0x498640). Keys are stored
    /// <c>(x, y, z, w)</c> -- the file's order, and <see cref="Quaternion"/>'s own field order. There is no
    /// Bezier form for rotations.</summary>
    private static Quaternion SampleQuat(ControllerTrack c, float t)
    {
        int n = c.Times.Length;
        if (n == 0 || c.Values.Length < 4) return Quaternion.Identity;
        int st = Rows(c) * c.Columns;
        if (n >= 3 && AnimKnobs.Curves.V)
            if (st == 4 && KeyCurveService.Turn(c.Times, c.Values, n, t, out Quaternion curve)) return curve;
        Key(c, t, out int idx, out float a);
        if (idx == 0) return Q4(c.Values, 0);
        if (idx >= n) return Q4(c.Values, (n - 1) * st);
        int k = (idx - 1) * st;
        return Slerp(Q4(c.Values, k), Q4(c.Values, k + st), a);
    }

    /// <summary>Rows per key: three for a Bezier track (value, in-tangent, out-tangent), one otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Rows(ControllerTrack c) => c.Bezier ? 3 : 1;

    private static Vector3 V3(float[] v, int k) => new(v[k], v[k + 1], v[k + 2]);
    private static Quaternion Q4(float[] v, int k) => new(v[k], v[k + 1], v[k + 2], v[k + 3]);

    /// <summary>
    /// A cubic Bezier segment between two keys, on <c>InterpolateBezier</c> @0x5b9140's polynomial
    /// (<c>a*u^3 + 3*c*u^2*t + 3*d*u*t^2 + b*t^3</c>, so the arguments are P0, P3, P1, P2). A deliberate
    /// divergence: the engine's inner control points come from <c>value[k] + value[k+1]</c> and its past-the-end
    /// branch reads a key past the array, so this uses the near key's out-tangent and the far key's in-tangent.
    /// </summary>
    private static float Bezier(float p0, float outT, float p3, float inT, float t)
    {
        _bezier++;
        float u = 1f - t, p1 = p0 + outT, p2 = p3 + inT;
        return p0 * u * u * u + 3f * p1 * u * u * t + 3f * p2 * u * t * t + p3 * t * t * t;
    }

    /// <summary><c>slerp</c> @0x5b6500, transcribed so the blend is bit-identical: the
    /// shortest arc by negating the FIRST argument, the engine's two exact escapes, and no renormalisation.
    /// </summary>
    private static Quaternion Slerp(Quaternion a, Quaternion b, float w)
    {
        CX.Engine.Quat r = AnimKeys.Slerp(
            new CX.Engine.Quat { W = a.W, X = a.X, Y = a.Y, Z = a.Z },
            new CX.Engine.Quat { W = b.W, X = b.X, Y = b.Y, Z = b.Z }, w);
        return new Quaternion(r.X, r.Y, r.Z, r.W);
    }
}
