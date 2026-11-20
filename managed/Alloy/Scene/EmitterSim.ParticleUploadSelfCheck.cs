using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>The particle upload's regression checks, which already held before the upload followed the draw: every
    /// drawn particle is sent, and blended particles still draw back to front. Oracles: hand-placed positions and counts.</summary>
    private static void ParticleUploadRegressions(Checker check, UploadRig rig)
    {
        rig.Fresh();
        rig.Populate(0, 10, 0f, 0f);
        rig.Populate(1, 2000, 0f, 1000f);
        int at = rig.Frame(0, 1);
        int found = 0;
        for (int i = 0; i < 10; i++) if (rig.PrevOf(UploadRig.At(i, 0f, 0f)) != null) found++;
        for (int i = 0; i < 2000; i++) if (rig.PrevOf(UploadRig.At(i, 0f, 1000f)) != null) found++;
        check.Expect(at == 2010 && Particles.VkRecords.Length == 2010 && found == 2010,
            $"E2: two emitters of 10 and 2,000 on screen upload and draw all 2,010 ({at} placed, "
          + $"{Particles.VkRecords.Length} uploaded, {found} found at their own positions)");

        EmitterDef d = SceneDb.PartModel[rig.Part(0)]!.Nodes[1].Emitter!;
        string blend = d.Blend;
        d.Blend = "Normal";
        rig.Fresh();
        rig.Populate(0, 20, 0f, 0f);
        at = rig.Frame(0);
        ReadOnlySpan<ParticleRec> recs = Particles.VkRecords;
        ReadOnlySpan<uint> order = Particles.VkIndices(at);
        int backwards = 0;
        for (int k = 1; k < order.Length; k++)
            if (recs[(int)order[k]].Centre.LengthSquared() > recs[(int)order[k - 1]].Centre.LengthSquared()) backwards++;
        d.Blend = blend;
        check.Expect(Particles.Sort.V && at == 20 && backwards == 0,
            $"blended particles draw farthest first ({at} placed, {backwards} drawn nearer before farther)");
    }
}
