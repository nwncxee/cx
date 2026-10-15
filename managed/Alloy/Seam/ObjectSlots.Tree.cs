using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Alloy.Mdl;

namespace CX.Alloy.Seam;

public sealed unsafe partial class ObjectSlots
{
    private static long _appendRefusals, _amputateRefusals;

    /// <summary><c>Gob::SetSubtreeEnabled(char const*, int)</c> @0x43c670 writes ONE byte, <c>Part+0x34</c>, on
    /// the named part -- not recursively. The name is about what that byte means to the draw: the engine's
    /// part draw returns immediately when it is zero, so the whole subtree is skipped by one test. Returns
    /// false iff the name did not resolve.</summary>
    [UnmanagedCallersOnly]
    private static byte SetSubtreeEnabledSlot(nint self, nint name, int on)
    {
        if (!Live(self, 47, out DbId id, out _)) { GateRefused(null, on); return 0; }
        string? nm = Str(name);
        int p = PartByName(id, nm);
        if (p < 0) { GateRefused(nm, on); return 0; }
        if (on == 0) _gatesOff++; else _gatesOn++;
        SceneDb.PartEnabled[p] = on != 0;
        SceneDb.Touch(id, Dirty.Visible | Dirty.Tree);
        return 1;
    }

    /// <summary>A gate the game set and CX did not. The engine disables about two dozen subtrees per
    /// humanoid -- the cloak rig, the tail and belt fans, the VFX anchors, the head and hand attach points --
    /// and a parity dump of the same inn found 192 parts the engine disables and NOT ONE here. A refusal in
    /// this gate is how that happens, so it is named and counted rather than returning 0 in silence.</summary>
    private static void GateRefused(string? name, int on)
    {
        _gatesRefused++;
        if (name != null && _saidGateRefusal.Count < 16 && _saidGateRefusal.Add(name))
            Facade.Log?.Always($"seam: Gob::SetSubtreeEnabled(\"{name}\", {on}) matched no part -- the gate the "
                             + "game set is LOST. Counted on /alloy as a refused subtree gate.");
    }

    private static readonly System.Collections.Generic.HashSet<string> _saidGateRefusal = new();
    private static long _gatesOn, _gatesOff, _gatesRefused;

    /// <summary><c>Gob::Append(char const* model, char const* parentPart)</c> @0x4422a0: load a second model
    /// and graft its tree under the named part -- the composite BODY-PART loader
    /// (<c>CNWCAnimBaseParts::LoadModel</c> @0xadb550, per head/torso/arms/legs/robe). Returns 1, or 0 when the
    /// name or model misses. CX grafts a CHILD OBJECT, so <see cref="PartByName"/> searches the composition.</summary>
    [UnmanagedCallersOnly]
    private static int Append(nint self, nint model, nint parentPart)
    {
        if (!Live(self, 45, out DbId id, out nint shell)) return 0;
        string? resref = Str(model);
        string? anchor = Str(parentPart);
        if (string.IsNullOrEmpty(resref)) { Refuse(resref, anchor, "the model name is empty"); return 0; }
        int at = PartByName(id, anchor);
        if (at < 0) { Refuse(resref, anchor, "the parent part name does not resolve"); return 0; }
        // The engine replaces a body part by amputating the old graft and appending the new one at the same
        // node. A second graft on a node that already carries one means the amputation did not land, and what
        // the player sees is the OLD piece still there under the new armour -- so say so rather than letting
        // the composition quietly grow a second torso.
        Doubled(id, at, resref!, anchor);
        DbId child = ObjectFns.Compose(resref!, id, at);
        if (child.IsNull) { Refuse(resref, anchor, "the model would not load"); return 0; }
        SceneDb.ObjSeamAppendNext[child.Index] = SceneDb.ObjSeamAppendHead[id.Index];
        SceneDb.ObjSeamAppendHead[id.Index] = child;
        PltColors.Inherit(child, id);
        ObjectTex.InheritEnvMap(child, id);
        InheritShift(child, id);
        // The engine force-enables the whole path to the graft, so a disabled ancestor cannot hide it.
        for (int p = at; p >= 0; p = SceneDb.PartParent[p]) SceneDb.PartEnabled[p] = true;
        // ...and sets the process flag, so the next Gob::ProcessGob re-gates the whole composition: the path
        // stays on because geometry now hangs below it, every other dummy subtree collapses again.
        PartGate.Reflow(id);
        Bounds(id, shell);
        DbAnim.TreeChanged(id);
        HitFeed.NoteObject(id);
        SceneDb.Touch(id, Dirty.Tree);
        _appends++;
        Note(id, resref!, anchor, at);
        return 1;
    }

    /// <summary>The composition as the game states it, for the first <see cref="MaxNoted"/> grafts of a
    /// session: the piece, the node it was asked for, and the part that name resolved to -- the record of what
    /// a creature is actually built out of, which is the first thing a missing or wrong body part needs.</summary>
    private static void Note(DbId id, string model, string? anchor, int at)
    {
        if (_noted >= MaxNoted) return;
        _noted++;
        DbId owner = SceneDb.PartObj[at];
        Facade.Log?.Info($"seam: graft {_noted}/{MaxNoted}: \"{model}\" onto \"{anchor}\" of "
                         + $"\"{SceneDb.ObjModel[id.Index]?.Name ?? "-"}\" (o={id.Index}) -- part {at}, owned by "
                         + (owner.Value == id.Value ? "the base model" : $"o={owner.Index} "
                           + $"(\"{(SceneDb.Objects.Valid(owner) ? SceneDb.ObjModel[owner.Index]?.Name : null) ?? "-"}\")"));
    }

    private static int _noted;
    private const int MaxNoted = 120;

    /// <summary>Is a piece already grafted at <paramref name="at"/>? Counted always, said the first few
    /// times with both models named, because which pair it is is the whole question.</summary>
    private static void Doubled(DbId id, int at, string model, string? anchor)
    {
        for (DbId c = SceneDb.ObjSeamAppendHead[id.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            if (SceneDb.ObjAttachPart[c.Index] != at) continue;
            _doubledGrafts++;
            if (_doubledGrafts <= 8)
                Facade.Log?.Always($"seam: \"{model}\" is the SECOND piece grafted onto \"{anchor}\" of "
                                 + $"\"{SceneDb.ObjModel[id.Index]?.Name ?? "-"}\" -- "
                                 + $"\"{SceneDb.ObjModel[c.Index]?.Name ?? "-"}\" is still there, so the "
                                 + "amputation that should have replaced it did not land. Counted on /alloy.");
            return;
        }
    }

    /// <summary>Appends onto a node that already carried a graft: a body part the game meant to REPLACE and
    /// did not.</summary>
    private static long _doubledGrafts;

    /// <summary>An append the engine would have made and CX did not: the composition is short a piece, which
    /// on a creature is a missing body part. Said once per reason, counted always.</summary>
    private static void Refuse(string? model, string? anchor, string why)
    {
        _appendRefusals++;
        if (_saidAppendRefusal.Add(why))
            Facade.Log?.Always($"seam: Gob::Append(\"{model}\", \"{anchor}\") REFUSED -- {why}. The "
                             + "composition is short that piece. Counted on /alloy as an append refusal.");
    }

    private static readonly System.Collections.Generic.HashSet<string> _saidAppendRefusal = new();

    /// <summary><c>Gob::Amputate(char const*)</c> @0x443fa0: delete the named subtree and release the model
    /// the matching <c>Append</c> took. Returns 1, or 0 when the name does not resolve. The subtree IS a child
    /// object, so this destroys exactly what the append created; a name inside the BASE model's own tree has
    /// no appended record to drop, so that subtree is disabled instead and the refusal is counted.</summary>
    [UnmanagedCallersOnly]
    private static int Amputate(nint self, nint part)
    {
        if (!Live(self, 46, out DbId id, out nint shell)) return 0;
        string? name = Str(part);
        if (string.IsNullOrEmpty(name)) return 0;

        // The engine resolves the name to ONE part and drops the grafts rooted in ITS subtree
        // (Gob::Amputate @0x543fa0: FindPart, then the subtree walk against the graft list). Every body-part
        // model of a creature carries the SAME rig node names, so "does this piece hold a part of that name"
        // matches nearly every piece: a re-equip amputated whichever one came first in the list and left the
        // one it meant, which is a chest that stays naked under new armour. Match the piece whose ROOT is
        // that name first, and only fall back to containment when no root answers.
        DbId take = AmputateTarget(id, name!, out bool byContainment);
        if (!take.IsNull)
        {
            if (_noted < MaxNoted)
            {
                _noted++;
                Facade.Log?.Info($"seam: cut {_noted}/{MaxNoted}: \"{name}\" off "
                                 + $"\"{SceneDb.ObjModel[id.Index]?.Name ?? "-"}\" (o={id.Index}) -- takes o={take.Index} "
                                 + $"(\"{SceneDb.ObjModel[take.Index]?.Name ?? "-"}\")"
                                 + (byContainment ? ", matched by CONTAINMENT" : ""));
            }
            DbId prev = default;
            for (DbId c = SceneDb.ObjSeamAppendHead[id.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
            {
                if (c.Value == take.Value) break;
                prev = c;
            }
            DbId next = SceneDb.ObjSeamAppendNext[take.Index];
            if (prev.IsNull) SceneDb.ObjSeamAppendHead[id.Index] = next;
            else SceneDb.ObjSeamAppendNext[prev.Index] = next;
            DestroyRecord(take);
            // The piece's geometry is gone, so the path that carried it gates off again (Gob::Amputate sets
            // the process flag the same way Gob::Append does).
            PartGate.Reflow(id);
            Bounds(id, shell);
            DbAnim.TreeChanged(id);
            HitFeed.NoteObject(id);
            SceneDb.Touch(id, Dirty.Tree);
            _amputations++;
            if (byContainment) _containedAmputations++;
            return 1;
        }

        // Not an appended piece: the engine would have cut it out of the base tree.
        int p = PartByName(id, name);
        if (p < 0)
        {
            // The engine's FindPart would have answered, so a piece the game means to replace is STILL THERE
            // -- new armour over the old chest. Naming it is the only way to tell which name went unmatched.
            _amputateRefusals++;
            if (_saidAmputateRefusal.Add(name!))
                Facade.Log?.Info($"seam: Gob::Amputate(\"{name}\") matched NOTHING -- neither a grafted "
                                 + "piece's root, nor a part of one, nor a part of the object's own model. The "
                                 + "piece it names is still in the composition. Counted on /alloy.");
            return 0;
        }
        SceneDb.PartEnabled[p] = false;
        SceneDb.Touch(id, Dirty.Tree | Dirty.Visible);
        _baseAmputations++;
        if ((_baseAmputations & (_baseAmputations - 1)) == 0)
            Facade.Log?.Info($"seam: Gob::Amputate(\"{name}\") names a part of the object's OWN model, not an "
                             + $"appended one -- the subtree is disabled rather than deleted ({_baseAmputations} so far)");
        return 1;
    }

    /// <summary>Which appended piece an amputation takes for <paramref name="name"/>: the one ROOTED at that
    /// name, and only if none is, the first that merely holds a part of it. Every body-part model of a creature
    /// carries the SAME rig node names, so "holds a part of that name" matches nearly every piece, and rooted-
    /// first is what keeps a re-equip from dropping some other piece and leaving the one it meant.</summary>
    internal static DbId AmputateTarget(DbId id, string name, out bool byContainment)
    {
        byContainment = false;
        if (!SceneDb.Objects.Valid(id) || string.IsNullOrEmpty(name)) return default;
        for (int pass = 0; pass < 2; pass++)
            for (DbId c = SceneDb.ObjSeamAppendHead[id.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
            {
                if (!SceneDb.Objects.Valid(c)) break;
                if (pass == 0 ? RootNamed(c, name) : PartNamed(c, name))
                {
                    byContainment = pass != 0;
                    return c;
                }
            }
        return default;
    }

    /// <summary>Is <paramref name="name"/> the root node of appended child <paramref name="c"/>? That is what
    /// the engine's amputation matched on, because the graft's root is the node it linked in.</summary>
    private static bool RootNamed(DbId c, string name)
    {
        ModelDef? m = SceneDb.ObjModel[c.Index];
        return m != null && m.Nodes.Length > 0
               && string.Equals(m.Nodes[0].Name, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does appended child <paramref name="c"/> hold a part named <paramref name="name"/>?</summary>
    private static bool PartNamed(DbId c, string name) => PartByName(c, name) >= 0;

    private static long _appends, _amputations, _baseAmputations, _containedAmputations;

    /// <summary>Appends, amputations, amputations that named the base model's own part, and the ones that
    /// matched a piece by CONTAINMENT because no piece was rooted at the name -- the ambiguous arm.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _saidAmputateRefusal = new();

    public static string TreeStats()
        => $"tree: {_appends} appends, {_amputations} amputations, {_baseAmputations} base-part amputations"
         + (_containedAmputations == 0 ? "" : $", {_containedAmputations} matched by containment")
         + (_appendRefusals == 0 ? "" : $", {_appendRefusals} APPENDS REFUSED")
         + (_amputateRefusals == 0 ? "" : $", {_amputateRefusals} AMPUTATIONS MATCHED NOTHING")
         + (_doubledGrafts == 0 ? "" : $", {_doubledGrafts} DOUBLED GRAFTS (a piece appended over one still there)")
         + $" | subtree gates: {_gatesOn} on, {_gatesOff} off"
         + (_gatesRefused == 0 ? "" : $", {_gatesRefused} REFUSED (matched no part)");

    /// <summary>Recompute the object's bounds after a composition change.</summary>
    private static void Bounds(DbId id, nint shell) => RecomputeBounds(id, shell);

    /// <summary>Recompute the object's cached bounds, as the two void bounding slots and <c>NewCAurObject</c>
    /// do. The cache lives IN THE SHELL at the engine's own offsets (+0x160 min, +0x16c max, +0x178 radius,
    /// +0x17c centre), so a getter and raw engine reads see one value. The engine seeds an INVERTED
    /// &#177;1e4 box; an object with no geometry keeps that, because a zero box tests as a hit.</summary>
    internal static void RecomputeBounds(DbId id, nint shell)
    {
        if (shell == 0) return;
        var g = new Gob(shell);
        // A zero or non-finite scale would collapse the box, and the box is a DIVISOR for the game:
        // CMainMenuBackgroundPanel::Reposition @0x9205b0 computes screenWidth / |max.x - min.x|. Scale 0 there
        // is an infinite object scale and a NaN position, which is how the main menu's backdrop vanished.
        float scale = g.ScaleBase * VisualXform.Scale(shell);
        if (!float.IsFinite(scale) || scale == 0f) scale = 1f;
        if (SeamBridge.ObjectBounds(id, true, out Vector3 min, out Vector3 max) && Usable(id, shell, ref min, ref max))
        {
            min *= scale; max *= scale;
            g.BoundsMin = min;
            g.BoundsMax = max;
            g.Centre = (min + max) * 0.5f;
            g.Radius = (max - min).Length() * 0.5f;
            return;
        }
        g.BoundsMin = new Vector3(BoundsSeed);
        g.BoundsMax = new Vector3(-BoundsSeed);
        g.Centre = Vector3.Zero;
        g.Radius = 0f;
    }

    /// <summary>The object's bounds from its part range (the per-part boxes), in OBJECT space: a part's
    /// box is already its own, so the union is the object's. Parts with no geometry and disabled subtrees are
    /// left out, which is what makes it the MINIMUM box the engine's own version computed.</summary>
    internal static bool PartRangeBounds(DbId obj, bool minimumOnly, out Vector3 min, out Vector3 max)
    {
        min = default; max = default;
        if (!SceneDb.Objects.Valid(obj)) return false;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        bool any = false;
        for (int p = first; p < first + count; p++)
        {
            if (SceneDb.PartMesh[p] < 0 || !SceneDb.PartEnabled[p]) continue;
            Vector3 lo = SceneDb.PartBMin[p], hi = SceneDb.PartBMax[p];
            if (!any) { min = lo; max = hi; any = true; continue; }
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
        }
        return any;
    }

    /// <summary>Is the measured box one the GAME can divide by? It divides the screen by an object's extent --
    /// <c>CMainMenuBackgroundPanel::Reposition</c> @0x9205b0 does <c>screenW / |max.x - min.x|</c> -- so a zero
    /// or non-finite extent is an infinite scale and a NaN position. A degenerate measurement answers the
    /// inverted &#177;10000 seed <c>Gob::GetMinimumBoundingBox()</c> @0x436f60 writes, and says so once.</summary>
    private static bool Usable(DbId id, nint shell, ref Vector3 min, ref Vector3 max)
    {
        Vector3 d = max - min;
        bool finite = float.IsFinite(d.X) && float.IsFinite(d.Y) && float.IsFinite(d.Z);
        if (finite && d.X > 0f && d.Y > 0f) return true;
        DegenerateBounds++;
        if (SeamShell.FirstReport(shell, 64))
            Facade.Log?.Info($"seam: \"{Facade.NameOf(shell)}\" measured a degenerate bounding box "
                             + $"({min} .. {max}) over {SceneDb.ObjPartCount[id.Index]} part(s) -- answering the "
                             + "engine's unmeasured box instead, because the game DIVIDES by this extent");
        return false;
    }

    /// <summary>Objects whose part range measured a box the game could not divide by.</summary>
    public static long DegenerateBounds { get; private set; }

    /// <summary>Read the cached box out of the shell.</summary>
    internal static void ReadBox(nint shell, out Vector3 min, out Vector3 max)
    {
        var g = new Gob(shell);
        min = g.BoundsMin;
        max = g.BoundsMax;
    }

    /// <summary>Read the cached sphere out of the shell.</summary>
    internal static void ReadSphere(nint shell, out Vector3 centre, out float radius)
    {
        var g = new Gob(shell);
        centre = g.Centre;
        radius = g.Radius;
    }
}
