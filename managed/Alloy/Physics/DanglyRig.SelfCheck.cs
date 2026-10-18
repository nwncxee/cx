using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>What a rig must say about a node, checked against numbers worked out by hand from the spec's
/// formulas and each fixture's own coordinates.</summary>
internal static class DanglyRigCheck
{
    internal static void Cases(Checker check)
    {
        Card(check);
        Banner(check);
        Blob(check);
        Static(check);
        Census(check);
        Weights(check);
    }

    /// <summary>Oracle: hand arithmetic on the card's authored numbers -- <c>m = 1 - 112/255</c>,
    /// <c>w = sqrt(0.5 * 112 * 1)</c>, <c>z = 1.5 * 0.8 / (2w)</c>, <c>G = 3 * 0.8 * 20 / ((1/60) * 112)</c>,
    /// and a cap of <c>0.3 * m</c>; the joint points sit at the fixture's own anchor and tip centroids.</summary>
    private static void Card(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the vegetation card builds a rig"); return; }
        check.Expect(r.Class == DanglyClass.Sway, "a card whose mobility rises with height is Sway");
        check.Expect(DanglySelfCheck.Near(r.Mobility[4], 0.560784f, 1e-5f),
                     "constraint 112 is mobility 0.560784");
        check.Expect(r.Mobility[0] == 0f, "constraint 0 pins: mobility 0, not 1");
        check.Expect(DanglySelfCheck.Near(r.Omega, 7.483315f, 1e-4f), "the card's spring rate is 7.48331 rad/s");
        check.Expect(DanglySelfCheck.Near(r.Zeta, 0.0801784f, 1e-5f), "the card's damping ratio is 0.0801784");
        check.Expect(DanglySelfCheck.Near(r.WindGain, 25.714286f, 1e-3f), "the card's wind gain is 25.714286");
        check.Expect(r.Columns == 1 && r.Segments == 1, "Sway is one joint on one column");
        check.Expect(r.BoneCount == 2, "Sway has the part's bone and one more");
        check.Expect(r.Shear && !r.Stretch, "a Sway bone is a shear, not a stretch");
        check.Expect(r.BoneJoint[0] == -1, "bone 0 is the part itself");
        check.Expect(r.JointParent.Length == 2 && r.JointParent[0] == -1 && r.JointParent[1] == 0,
                     "the tip hangs off a kinematic root");
        check.Expect(DanglySelfCheck.Near(r.JointRest[0], Vector3.Zero, 1e-5f),
                     "the root sits at the anchor centroid (0, 0, 0)");
        check.Expect(DanglySelfCheck.Near(r.JointRest[1], new Vector3(0f, 0f, 1.5f), 1e-5f),
                     "the tip sits at the mobility-weighted centroid (0, 0, 1.5)");
        check.Expect(DanglySelfCheck.Near(r.JointCap[1], 0.1682353f, 1e-5f),
                     "the tip's excursion cap is 0.3 * 0.560784 node units");
        check.Expect(DanglySelfCheck.Near(r.DragDiag[1], new Vector3(0f, 1f, 0f), 1e-5f)
                  && DanglySelfCheck.Near(r.DragOff[1], Vector3.Zero, 1e-5f),
                     "every card triangle faces +-y, so its drag tensor is diag(0, 1, 0)");
        check.Expect(DanglySelfCheck.Near(r.RestDown, new Vector3(0f, 0f, -1f), 1e-5f),
                     "an unrotated node's authored down is model down");
    }

    /// <summary>Oracle: the spec's counts over the banner's own extents -- 1.2 wide, 1.5 deep, so
    /// <c>K = round(2 * 1.2 / 1.5) = 2</c> and <c>J = round(1.5 / 0.5) = 3</c>, six segment bones plus the
    /// part; the rope gives <c>K = 1</c>, <c>J = 4</c>.</summary>
    private static void Banner(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Banner());
        if (r == null) { check.Expect(false, "the banner builds a rig"); return; }
        check.Expect(r.Class == DanglyClass.Swing, "a banner pinned on its top row is Swing");
        check.Expect(r.Mobility[2] == 0f, "constraint 255 pins as hard as 0");
        check.Expect(r.Columns == 2, "1.2 wide over 1.5 deep is two columns");
        check.Expect(r.Segments == 3, "1.5 deep is three half-metre segments");
        check.Expect(r.BoneCount == 7, "two columns of three segments, plus the part");
        check.Expect(!r.Shear && !r.Stretch, "a Swing segment keeps its length");
        check.Expect(DanglySelfCheck.Near(r.Omega, 2.366432f, 1e-4f), "the banner's spring rate is 2.36643 rad/s");
        check.Expect(DanglySelfCheck.Near(r.Zeta, 0.0633875f, 1e-5f), "the banner's damping ratio is 0.0633875");

        DanglyRig? rope = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (rope == null) { check.Expect(false, "the rope builds a rig"); return; }
        check.Expect(rope.Columns == 1, "a rope 0.04 wide over 2 long is one column");
        check.Expect(rope.Segments == 4, "2 long is four half-metre segments");
        check.Expect(rope.BoneCount == 5, "one column of four segments, plus the part");
        for (int j = 1; j <= 4; j++)
            check.Expect(DanglySelfCheck.Near((rope.JointRest[j] - rope.JointRest[j - 1]).Length(), 0.5f, 1e-5f),
                         "every rope segment is 0.5 long at rest");
    }

    /// <summary>Oracle: hand arithmetic on the blob's authored numbers -- <c>w = sqrt(0.5 * 150 * 3) = 15</c>,
    /// <c>z = 1.5 * 8 / 30 = 0.4</c>, <c>G = 3 * 8 * 20 / ((1/60) * 150 * 3) = 64</c> -- and the fixture's
    /// deliberately uncorrelated heights.</summary>
    private static void Blob(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Blob());
        if (r == null) { check.Expect(false, "the blob builds a rig"); return; }
        check.Expect(r.Class == DanglyClass.Jiggle, "a well-damped blob with no height trend is Jiggle");
        check.Expect(DanglySelfCheck.Near(r.Omega, 15f, 1e-4f), "the blob's spring rate is 15 rad/s");
        check.Expect(DanglySelfCheck.Near(r.Zeta, 0.4f, 1e-5f), "the blob's damping ratio is 0.4");
        check.Expect(DanglySelfCheck.Near(r.WindGain, 64f, 1e-3f), "the blob's wind gain is 64");
        check.Expect(r.BoneCount == 2 && r.Stretch && !r.Shear, "Jiggle is one stretchy segment");
        check.Expect(DanglySelfCheck.Near(r.JointRest[1], new Vector3(0f, 0f, 0.1f), 1e-5f),
                     "the tip sits at the mobility-weighted centroid (0, 0, 0.1)");
        check.Expect(DanglySelfCheck.Near(r.JointCap[1], 0.1235294f, 1e-5f),
                     "the tip's excursion cap is 0.3 * (1 - 150/255)");
    }

    /// <summary>Oracle: a mesh with no mobile vertex has nothing to move, so the rig the spec asks for is
    /// the empty one, not a null the caller would have to read as "unusable".</summary>
    private static void Static(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.AllAnchors());
        if (r == null) { check.Expect(false, "an all-anchor node still builds a rig"); return; }
        check.Expect(r.Class == DanglyClass.Static, "every vertex pinned is Static");
        check.Expect(r.BoneCount == 0, "a Static rig has no bones");
        check.Expect(r.JointRest.Length == 0, "a Static rig has no joints");
        check.Expect(r.Skin == null, "a Static rig needs no skin weights");
    }

    /// <summary>Oracle: the census families' own medians, one synthetic node each, with the class the spec's
    /// decision tree gives for those numbers written out by hand.</summary>
    private static void Census(Checker check)
    {
        check.Expect(DanglySelfCheck.Rig(DanglySelfCheck.Card())?.Class == DanglyClass.Sway,
                     "vegetation at 0.3 / 1 / 0.8, free at the top, is Sway");
        check.Expect(DanglySelfCheck.Rig(DanglySelfCheck.BodyPart())?.Class == DanglyClass.Jiggle,
                     "a body part at 0.03 / 3.5 / 7, a quarter movable, is Jiggle");
        check.Expect(DanglySelfCheck.Rig(DanglySelfCheck.Banner())?.Class == DanglyClass.Swing,
                     "cloth at 0.3 / 0.1 / 0.2, free at the bottom, is Swing");
        check.Expect(DanglySelfCheck.Rig(DanglySelfCheck.Rope())?.Class == DanglyClass.Swing,
                     "rigging at 0.1 / 0.1 / 0.1, long and thin, is Swing");
    }

    /// <summary>Oracle: the skinning contract itself -- four weights summing to 1, the byte packing
    /// <see cref="SkinDef.Bones"/> documents, and an anchor riding the part's own bone.</summary>
    private static void Weights(Checker check)
    {
        ModelDef[] all = { DanglySelfCheck.Card(), DanglySelfCheck.Banner(), DanglySelfCheck.Rope(),
                           DanglySelfCheck.Blob(), DanglySelfCheck.BodyPart() };
        foreach (ModelDef m in all)
        {
            DanglyRig? r = DanglySelfCheck.Rig(m);
            SkinDef? skin = r?.Skin;
            if (r == null || skin == null) { check.Expect(false, "a moving dangly node generates skin weights"); continue; }
            int nv = m.Nodes[0].Mesh!.Positions.Length;
            bool sums = skin.Weights.Length == nv, packed = skin.Bones.Length == nv,
                 wide = skin.BoneIndices.Length == nv * 4, inf = skin.Influences.Length == nv,
                 anchored = true, inRange = true;
            for (int i = 0; i < nv && sums; i++)
            {
                Vector4 w = skin.Weights[i];
                sums = DanglySelfCheck.Near(w.X + w.Y + w.Z + w.W, 1f, 1e-5f);
            }
            for (int i = 0; i < nv && packed && wide; i++)
            {
                uint b = skin.Bones[i];
                for (int k = 0; k < 4; k++)
                {
                    uint slot = (b >> (k * 8)) & 0xffu;
                    packed &= slot == skin.BoneIndices[i * 4 + k];
                    inRange &= slot < (uint)r.BoneCount;
                }
            }
            for (int i = 0; i < nv; i++)
                if (r.Mobility[i] == 0f)
                {
                    Vector4 w = skin.Weights[i];
                    uint slot = skin.Bones[i] & 0xffu;
                    anchored &= slot == 0u && DanglySelfCheck.Near(w.X, 1f, 1e-5f);
                }
            check.Expect(sums, "every vertex's four weights sum to 1");
            check.Expect(packed, "the packed bone bytes are the wide bone slots, byte per influence");
            check.Expect(wide && inf, "the skin carries four wide slots and an influence count per vertex");
            check.Expect(inRange, "no vertex names a bone slot the rig does not have");
            check.Expect(anchored, "an anchor rides the part's own bone, at weight 1");
        }
    }
}
