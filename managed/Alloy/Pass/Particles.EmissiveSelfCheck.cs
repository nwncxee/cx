using System;
using System.Text;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class Particles
{
    /// <summary>A database emitter's <c>fEmissive</c> comes from the <c>.mtr</c> its texture names. Oracle: the
    /// hand-written material texts below and the values they state, read through a stand-in resource provider.</summary>
    internal static void EmissiveSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Particles.EmissiveSelfCheck");
        Func<string, byte[]?>? bytes = MtrDef.Bytes;
        Func<string, bool>? answered = MtrDef.Answered;
        var m = new ModelDef { Name = "cx_emisfix" };
        m.Nodes = [new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy },
                   new NodeDef { Name = "flare", Index = 1, Parent = 0, Kind = NodeKind.Emitter,
                                 Emitter = new EmitterDef { Texture = "fx_hotflare" } }];
        DbId ph = SceneDb.Parts.Alloc();
        int part = ph.Index;
        DbFeed.InitPart(part);
        SceneDb.PartModel[part] = m;
        SceneDb.PartNode[part] = 1;
        string text = "parameter float fEmissive 4.0\n";
        bool ready = true;
        try
        {
            MtrDef.Forget();
            MtrDef.Bytes = n => n == "fx_hotflare" ? Encoding.ASCII.GetBytes(text) : null;
            MtrDef.Answered = _ => ready;
            float v = EmissiveOfPart(part, out bool settled);
            check.Expect(v == 4f && settled, $"an emitter whose texture's .mtr states fEmissive 4 draws at 4 (got {v})");

            MtrDef.Forget();
            text = "parameter float fEmissive 50\n";
            check.Expect(EmissiveOfPart(part, out _) == 10f, "a declared 50 is capped at ten");
            MtrDef.Forget();
            text = "parameter int fEmissive 4\n";
            check.Expect(EmissiveOfPart(part, out _) == 1f, "an int-typed fEmissive is not the float the stage reads");

            MtrDef.Forget();
            MtrDef.Bytes = _ => null;
            v = EmissiveOfPart(part, out settled);
            check.Expect(v == 1f && settled, "an emitter with no material draws at 1, and nothing is left to wait for");
            ready = false;
            v = EmissiveOfPart(part, out settled);
            check.Expect(v == 1f && !settled, "...but one whose material has not been answered for is asked again later");
        }
        finally
        {
            MtrDef.Bytes = bytes;
            MtrDef.Answered = answered;
            MtrDef.Forget();
            SceneDb.PartModel[part] = null;
            SceneDb.Parts.Free(ph);
            SceneDb.Parts.TakeDirty();
        }
        log.Info(check.Ok ? "Particles.EmissiveSelfCheck: passed" : "Particles.EmissiveSelfCheck: FAILED");
    }
}
