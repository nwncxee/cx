using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The composition half of <see cref="DbAnim"/>'s suite: how names resolve across appended pieces,
/// which piece an amputation takes, and what a tree change does to the objects riding the creature. Reached
/// from <c>DbAnim.SelfCheck.cs</c>'s <c>SelfCheck</c>, which is the suite's one entry point.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>A name that only an APPENDED piece carries resolves against the whole composition, and both
    /// overloads of the question give the same answer. <c>Gob::Append</c> asks with a managed string, and a
    /// creature's later body parts anchor to nodes the earlier ones brought -- so an overload that searched
    /// only the base model refused those appends and the pieces never existed.</summary>
    private static void Composition(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out _);
        DbId piece = Subject(Graft(), scene, out int pieceFirst);
        check.Expect(SceneDb.PieceLink(body, piece), "the piece would not link onto the body");

        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(body, "gmid") == pieceFirst + 1,
                     "a part only an appended piece carries resolves against the composition");
        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(body, "mid") != pieceFirst + 1,
                     "...and the base model's own name still wins");
        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(body, "no_such_node") < 0,
                     "...while a name nothing carries is still -1");

        SceneDb.PieceDropAll(body);
        Drop(piece);
        Drop(body);
        SceneDb.Scenes.Free(scene);
        CompositionDepth(check);
        Reanchored(check);
    }

    // Given an effect attached to a node that lives on a creature's appended piece
    // When the creature's tree changes (an Append or Amputate)
    // Then the effect is re-anchored to that node on the piece and keeps following it
    /// <summary>A tree change re-resolves a rider by name across the whole composition, as its first attach
    /// did. Oracle: the fixture's seated part index, and the piece leaf's place worked out by hand.</summary>
    private static void Reanchored(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out int first);
        DbId piece = Subject(Graft(), scene, out int pieceFirst);
        DbId fx = Subject(Chain(), scene, out _);
        check.Expect(SceneDb.PieceLink(body, piece), "the piece would not link onto the body");
        SceneDb.ObjAttachTo[piece.Index] = body;
        SceneDb.SetAttachPart(piece.Index, first + 2);
        SceneDb.ObjSeamFlags[piece.Index] |= (uint)SeamFlag.Composed;
        SceneDb.ObjAttachTo[fx.Index] = body;
        SceneDb.SetAttachPart(fx.Index, pieceFirst + 2);

        TreeChanged(body);
        check.Expect(SceneDb.ObjAttachPart[fx.Index] == pieceFirst + 2,
                     "a rider on an appended piece's node keeps that node when the creature's tree changes");
        check.Expect(SceneDb.ObjAttachPart[piece.Index] == first + 2,
                     "...and the piece keeps the anchor it was appended at");
        SceneDb.ObjAttachPart[fx.Index] = first + 1;
        TreeChanged(body);
        check.Expect(SceneDb.ObjAttachPart[fx.Index] == pieceFirst + 2,
                     "...and a rider whose index went stale is moved back onto the piece's node by its name");

        SceneDb.ObjPos[body.Index] = new Vector3(5f, -3f, 1f);
        Moved(body.Index);
        ComposeObject(body);
        Moved(piece.Index); ComposeObject(piece);
        Moved(fx.Index); ComposeObject(fx);
        // The body's leaf is (0,0,2) in object space and the graft's nodes sit on their parent: (5,-3,3).
        check.Expect(NearV(Translation(SceneDb.ObjWorld[fx.Index]), new Vector3(5f, -3f, 3f)),
                     "...and follows the piece's node as the creature moves");

        SceneDb.ObjAttachTo[fx.Index] = default;
        SceneDb.SetAttachPart(fx.Index, -1);
        SceneDb.ObjSeamFlags[piece.Index] &= ~(uint)SeamFlag.Composed;
        SceneDb.ObjAttachTo[piece.Index] = default;
        SceneDb.SetAttachPart(piece.Index, -1);
        SceneDb.PieceDropAll(body);
        Drop(fx);
        Drop(piece);
        Drop(body);
        SceneDb.Scenes.Free(scene);
        // The tree change and the composes leave marks and a cursor the next check's compose would read.
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        _dirtyUpTo = 0;
    }

    /// <summary>The name search stops at the 8 levels of composition every piece walk covers, whatever the
    /// data does: a chain grafted deeper answers for the levels inside it and -1 for the ones past it. It is
    /// asked on every update, so following a corrupt chain down is the game thread.</summary>
    private static void CompositionDepth(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId[] chain = new DbId[10];   // deeper than the 8 levels the search covers
        bool built = true;
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = Subject(Graft($"deep{i}_g"), scene, out _);
            if (i > 0) built &= SceneDb.PieceLink(chain[i - 1], chain[i]);
        }
        check.Expect(built, "the deep composition would not link");
        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(chain[0], "deep7_g") >= 0,
                     "the search stopped short of the 8 levels of composition it covers");
        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(chain[0], "deep8_g") < 0,
                     "the search went past the 8 levels of composition it covers");

        for (int i = chain.Length - 1; i >= 0; i--)
        {
            SceneDb.PieceDropAll(chain[i]);
            SceneDb.PieceForget(chain[i].Index);
            Drop(chain[i]);
        }
        SceneDb.Scenes.Free(scene);
        // Ten records created and freed leave that many marks and a reader's cursor past them; the next
        // check's compose reads both, so this fixture closes on a frame boundary.
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        _dirtyUpTo = 0;
    }

    /// <summary>An amputation takes the piece ROOTED at the name, not the first that merely holds a part of
    /// it. Two grafted pieces share every inner node name here, as a creature's body parts do -- matching on
    /// containment took whichever was appended last, so a re-equip removed the wrong piece and left the old
    /// one under the new one.</summary>
    private static void Amputation(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out _);
        DbId chest = Subject(Graft("chest_g"), scene, out _);
        DbId arm = Subject(Graft("arm_g"), scene, out _);
        // Appended head-first, as Gob::Append links them: arm, then chest.
        check.Expect(SceneDb.PieceLink(body, arm) && SceneDb.PieceLink(body, chest),
                     "both pieces would not link onto the body");

        DbId take = CX.Alloy.Seam.ObjectSlots.AmputateTarget(body, "arm_g", out bool byContainment);
        check.Expect(take.Value == arm.Value, "an amputation takes the piece ROOTED at the name");
        check.Expect(!byContainment, "...on its root, not by containment");

        // "gmid" is a node BOTH pieces carry, which is the ambiguous case the engine never reaches.
        take = CX.Alloy.Seam.ObjectSlots.AmputateTarget(body, "gmid", out byContainment);
        check.Expect(take.Value == chest.Value && byContainment,
                     "a name no piece is rooted at falls back to the first that holds it, and says so");
        check.Expect(CX.Alloy.Seam.ObjectSlots.AmputateTarget(body, "no_such_node", out _).IsNull,
                     "...and a name nothing carries takes nothing");

        SceneDb.PieceDropAll(body);
        Drop(arm);
        Drop(chest);
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary><see cref="Chain"/>'s shape under names nothing else uses, for the composition check.</summary>
    private static ModelDef Graft(string root = "groot")
    {
        var m = new ModelDef { Name = "cx_selfcheck_graft", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = root, Index = 0, Parent = -1, Children = new[] { 1 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "gmid", Index = 1, Parent = 0, Children = new[] { 2 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "gleaf", Index = 2, Parent = 1, Children = Array.Empty<int>(), Kind = NodeKind.Dummy },
        };
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName[m.Nodes[i].Name] = i;
        return m;
    }
}
