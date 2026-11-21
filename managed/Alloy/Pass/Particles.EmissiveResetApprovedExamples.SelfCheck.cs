using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

/// <summary>A server's override reaches a particle emitter's glow (approved-spec, E7). Oracle: the two
/// hand-written <c>.mtr</c> texts below, served by <see cref="StandInFiles"/>, and the <c>fEmissive</c> each states.</summary>
internal static unsafe partial class Particles
{
    internal static bool EmissiveResetApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Particles.EmissiveResetApprovedExamples.SelfCheck");
        Cx616E7(check);
        log.Info(check.Ok ? "Particles.EmissiveResetApprovedExamples.SelfCheck: passed"
                          : "Particles.EmissiveResetApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a particle emitter whose texture's .mtr sets fEmissive 2
    // When the server overrides that texture with one whose .mtr sets fEmissive 5
    // Then the emitter's particles glow at 5 from the next frame
    private static void Cx616E7(Checker check)
    {
        const string flare = "cx_rs_flare";
        var m = new ModelDef { Name = "cx_rs_flaremdl" };
        m.Nodes = [new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy },
                   new NodeDef { Name = "flare", Index = 1, Parent = 0, Kind = NodeKind.Emitter,
                                 Emitter = new EmitterDef { Texture = flare } }];
        DbId ph = SceneDb.Parts.Alloc();
        check.Expect(!ph.IsNull, "E7: the part table would not allocate");
        if (ph.IsNull) return;
        int part = ph.Index;
        DbFeed.InitPart(part);
        SceneDb.PartModel[part] = m;
        SceneDb.PartNode[part] = 1;
        IntPtr* none = stackalloc IntPtr[1];
        int slot = -1;
        try
        {
            using var files = new StandInFiles();
            files.Put(flare, StandInFiles.Mtr, "parameter float fEmissive 2\n");
            MtrFeed.Warm(m);
            slot = Capture((IntPtr)1, part, 0, none, 1, 1);
            check.Expect(slot >= 0 && _tab[slot].Emissive == 2f, $"E7: the emitter does not start at its .mtr's 2 ({(slot >= 0 ? _tab[slot].Emissive : -1f)})");
            if (slot < 0) return;
            files.Put(flare, StandInFiles.Mtr, "parameter float fEmissive 5\n");
            TexRegistry.QueueReset(flare);
            TexRegistry.Tick();
            Capture((IntPtr)1, part, 0, none, 1, 1);
            check.Expect(_tab[slot].Emissive == 5f, $"E7: the emitter glows at {_tab[slot].Emissive} after the override, not 5");
        }
        finally
        {
            if (slot >= 0) { _tab[slot].Ptr = IntPtr.Zero; _tab[slot].Part = -1; _tab[slot].EmissiveSettled = false; }
            _orderN = 0;
            SceneDb.PartModel[part] = null;
            SceneDb.Parts.Free(ph);
            SceneDb.Parts.TakeDirty();
        }
    }
}
