using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>The detonate event's approved examples: an animation's events played at a seated object, its
    /// Explosion emitters stepped after. Oracle: <c>ExplosionEmitter::Update</c> births (int)birthrate once per
    /// request, read in Ghidra on both builds -- <see cref="BlastRate"/> particles a burst.</summary>
    private static void DetonateExamples(ExtenderLog log)
    {
        var check = new Checker(log, "EmitterSim.DetonateApprovedExamples.SelfCheck");
        Reset();
        DetonateWithoutKey(check);
        Reset();
        KeyAndEventsInOneFrame(check);
        Reset();
        DetonateReach(check);
        Reset();
        log.Info(check.Ok ? "EmitterSim.DetonateApprovedExamples.SelfCheck: passed" : "EmitterSim.DetonateApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>The blast's birthrate, which is one burst's particle count.</summary>
    private const float BlastRate = 6f;

    // Given an object whose model has an Explosion emitter and no controller-228 key (vff_explfire)
    // When its animation fires a "detonate" event
    // Then that emitter bursts once, as it would from the controller key
    private static void DetonateWithoutKey(Checker check)
    {
        ModelDef m = BlastModel("cx_detfire", false);
        AnimDef anim = BlastAnim("detonate", "detonate");
        DbId obj = SeatBlast(m, out int first);
        int blast = first + 1, s = BlastSlot(blast);
        int burst = BlastFrame(obj, blast, s, anim, 0f, 1f);
        check.Expect(burst == (int)BlastRate,
            $"D1: a \"detonate\" event bursts an Explosion emitter with no detonate key ({burst} particles, want {(int)BlastRate})");
        int quiet = BlastFrame(obj, blast, s, anim, 1f, 1.5f);
        quiet = BlastFrame(obj, blast, s, anim, 1.5f, 1.9f);
        check.Expect(quiet == (int)BlastRate,
            $"...and once: the frames after it birth nothing more ({quiet} particles, want {(int)BlastRate})");
        int again = BlastFrame(obj, blast, s, anim, 0.25f, 0.75f);
        check.Expect(again == 2 * (int)BlastRate,
            $"...and a later detonate, past a quiet frame, bursts again ({again} particles, want {2 * (int)BlastRate})");
        UnseatBlast(obj, first, m.Nodes.Length);
    }

    // Given an Explosion emitter with both a controller-228 key and a "detonate" event at the same time
    // When the animation plays past them
    // Then the emitter bursts as many times as the stock client bursts it, read in Ghidra
    private static void KeyAndEventsInOneFrame(Checker check)
    {
        ModelDef m = BlastModel("cx_detkey", true);
        AnimDef anim = BlastAnim("Detonate", "DETONATE");
        DbId obj = SeatBlast(m, out int first);
        int blast = first + 1, s = BlastSlot(blast);
        int burst = BlastFrame(obj, blast, s, anim, 0f, 1f);
        check.Expect(burst == (int)BlastRate,
            $"D2: a detonate key and two detonate events in one frame burst once ({burst} particles, want {(int)BlastRate})");
        UnseatBlast(obj, first, m.Nodes.Length);
    }

    // Given an object with an Explosion and a Fountain emitter, a reference node with an Explosion child of its
    // own and expanded into its parts, an appended piece and a reference instance, each holding an Explosion emitter
    // When its animation fires an unrelated event, then "detonate"
    // Then only its own Explosion emitter and the appended piece's are asked to burst, and only by "detonate"
    private static void DetonateReach(Checker check)
    {
        ModelDef m = BlastModel("cx_detreach", false), sub = BlastModel("cx_detsub", false);
        ModelDef piece = BlastModel("cx_detpiece", false);
        AnimDef anim = BlastAnim("snd_boom", "detonate");
        DbId obj = SeatReach(m, sub, out int first);
        DbId app = SeatBlast(piece, out int appFirst), inst = SeatBlast(piece, out int instFirst);
        SceneDb.PieceLink(obj, app);
        SceneDb.SetAttachTo(app.Index, obj);
        SceneDb.SetAttachPart(app.Index, first);
        SceneDb.PieceLink(obj, inst);
        SceneDb.SetAttachTo(inst.Index, obj);
        SceneDb.SetAttachPart(inst.Index, first + RefNode);
        SceneDb.PartRefObj[first + RefNode] = inst;

        DbAnim.FireEventsForCheck(obj, anim, 0f, 0.4f);
        check.Expect(!Asked(first + 1), "D3: an event that is not \"detonate\" asks no emitter to burst");
        DbAnim.FireEventsForCheck(obj, anim, 0.4f, 1f);
        check.Expect(Asked(first + 1), "...and \"detonate\" asks the object's own Explosion emitter");
        check.Expect(Asked(appFirst + 1), "...and the appended piece's, whose emitters register on the same Gob");
        check.Expect(!Asked(first + FountNode), "...and not a Fountain emitter, which registers no callback");
        check.Expect(Asked(first + RefKid), "...and a reference node's own child, which its model instances on the Gob");
        check.Expect(!Asked(first + SubBlast),
            "...and not a reference node's expansion, which is instanced with no Gob to register on");
        check.Expect(!Asked(instFirst + 1), "...and not a reference instance, which is an object of its own");

        SceneDb.PartRefObj[first + RefNode] = default;
        SceneDb.PieceUnlink(obj, inst);
        SceneDb.PieceUnlink(obj, app);
        UnseatBlast(inst, instFirst, piece.Nodes.Length);
        UnseatBlast(app, appFirst, piece.Nodes.Length);
        UnseatBlast(obj, first, ReachParts);
    }

    /// <summary>Does the snapshot of <paramref name="part"/> ask its emitter to burst?</summary>
    private static bool Asked(int part)
    {
        Emitter e = default;
        return FillDb(part, ref e) && e.Detonate;
    }

    /// <summary>Open slot 0 for the emitter on <paramref name="part"/>, as the walk would.</summary>
    private static int BlastSlot(int part)
    {
        Emitter e = default;
        FillDb(part, ref e);
        return Slot0(in e);
    }

    /// <summary>One frame: the events in (<paramref name="from"/>, <paramref name="to"/>], then the explosion's
    /// birth half, in <c>Gob::Animate</c>'s order. Returns the slot's live particles.</summary>
    private static int BlastFrame(DbId obj, int part, int s, AnimDef anim, float from, float to)
    {
        DbAnim.FireEventsForCheck(obj, anim, from, to);
        Emitter e = default;
        if (!FillDb(part, ref e)) return -1;
        e.M = _tab[s].Mut;
        ExplosionBirth(ref _tab[s], ref e, IntPtr.Zero, 1f / 60f);
        _tab[s].Mut = e.M;
        return _tab[s].Live;
    }

    /// <summary>An animation 2 s long with <paramref name="first"/> at 0.25 s and <paramref name="second"/> at
    /// 0.5 s.</summary>
    private static AnimDef BlastAnim(string first, string second) => new()
    {
        Name = "cx_blast", Length = 2f,
        Events = new[] { new AnimEvent { Time = 0.25f, Name = first }, new AnimEvent { Time = 0.5f, Name = second } },
        Nodes = new[] { new AnimNode { Name = "root", Parent = -1 } },
    };

    /// <summary>A root and one Explosion emitter node, with a controller-228 rest key of 1 when
    /// <paramref name="key"/> is set.</summary>
    private static ModelDef BlastModel(string name, bool key)
    {
        var m = new ModelDef { Name = name };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy, Children = new[] { 1 } };
        m.Nodes = new[] { root, EmitterNode("blast", 1, "Explosion", key) };
        m.NodeByName["root"] = 0; m.NodeByName["blast"] = 1;
        return m;
    }

    /// <summary>An emitter node of <paramref name="update"/> under the root, 6 a burst and 2 s of life.</summary>
    private static NodeDef EmitterNode(string name, int index, string update, bool key)
    {
        var c = key
            ? new[] { Ctl1(Ctl.EmBirthRate, BlastRate), Ctl1(Ctl.EmLifeExp, 2f), Ctl1(Ctl.EmDetonate, 1f) }
            : new[] { Ctl1(Ctl.EmBirthRate, BlastRate), Ctl1(Ctl.EmLifeExp, 2f) };
        return new NodeDef
        {
            Name = name, Index = index, Parent = 0, Kind = NodeKind.Emitter, Controllers = c,
            Emitter = new EmitterDef { Update = update, Render = "Normal", Blend = "Normal", Texture = "fxpa_flame" },
        };
    }

    /// <summary>Where <see cref="SeatReach"/> puts the Fountain node, the reference node, its own Explosion
    /// child and the expansion's Explosion emitter, as offsets into the object's parts.</summary>
    private const int FountNode = 2, RefNode = 3, RefKid = 4, SubBlast = 6, ReachParts = 7;

    /// <summary>Seat <paramref name="m"/> with a Fountain node and a reference node added, the reference
    /// expanded into <paramref name="sub"/>'s two parts, as <c>ExpandReferenceParts</c> lays it out.</summary>
    private static DbId SeatReach(ModelDef m, ModelDef sub, out int first)
    {
        NodeDef root = m.Nodes[0];
        root.Children = new[] { 1, FountNode, RefNode };
        var refNode = new NodeDef
        {
            Name = "ref", Index = RefNode, Parent = 0, Kind = NodeKind.Reference, Children = new[] { RefKid },
            Reference = new ReferenceDef { Model = sub.Name },
        };
        NodeDef kid = EmitterNode("refkid", RefKid, "Explosion", false);
        kid.Parent = RefNode;
        m.Nodes = new[] { root, m.Nodes[1], EmitterNode("fount", FountNode, "Fountain", false), refNode, kid };
        m.NodeByName["fount"] = FountNode; m.NodeByName["ref"] = RefNode; m.NodeByName["refkid"] = RefKid;
        DbId obj = Seat(m, ReachParts, out first);
        if (first < 0) return obj;
        for (int k = 0; k < 2; k++)
        {
            int p = first + RefKid + 1 + k;
            SceneDb.PartModel[p] = sub;
            SceneDb.PartNode[p] = k;
            SceneDb.PartParent[p] = k == 0 ? first + RefNode : p - 1;
        }
        return obj;
    }

    /// <summary>Seat <paramref name="m"/> as one object, a part per node.</summary>
    private static DbId SeatBlast(ModelDef m, out int first) => Seat(m, m.Nodes.Length, out first);

    /// <summary>One object of <paramref name="count"/> parts, the first <c>m.Nodes.Length</c> of them
    /// <paramref name="m"/>'s nodes in order, at the origin.</summary>
    private static DbId Seat(ModelDef m, int count, out int first)
    {
        DbId obj = SceneDb.Objects.Alloc();
        if (!SceneDb.Parts.AllocRange(count, out first)) { SceneDb.Objects.Free(obj); first = -1; return default; }
        int o = obj.Index;
        DbFeed.InitObject(o);
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjRot[o] = Quaternion.Identity;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjWorld[o] = Matrix4x4.Identity;
        SceneDb.ObjPartFirst[o] = first;
        SceneDb.ObjPartCount[o] = count;
        for (int k = 0; k < count; k++)
        {
            int p = first + k;
            DbFeed.InitPart(p);
            SceneDb.PartObj[p] = obj;
            SceneDb.PartModel[p] = k < m.Nodes.Length ? m : null;
            SceneDb.PartNode[p] = k;
            SceneDb.PartParent[p] = k == 0 ? -1 : first + (k < m.Nodes.Length ? m.Nodes[k].Parent : 0);
            SceneDb.PartScale[p] = 1f;
            SceneDb.PartWorld[p] = Matrix4x4.Identity;
        }
        return obj;
    }

    /// <summary>Free what <see cref="Seat"/> seated, each part reset first.</summary>
    private static void UnseatBlast(DbId obj, int first, int count)
    {
        if (first < 0) return;
        for (int p = first; p < first + count; p++)
        {
            DbFeed.InitPart(p);
            SceneDb.PartModel[p] = null;
        }
        SceneDb.ObjPartCount[obj.Index] = 0;
        SceneDb.Parts.FreeRange(first, count);
        SceneDb.Objects.Free(obj);
    }
}
