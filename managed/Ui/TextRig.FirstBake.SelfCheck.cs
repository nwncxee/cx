using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>An early face that sinks the first bake is dropped so the default face still bakes, and kept asked when
    /// nothing bakes. Oracle: a bake that refuses any list holding the face named here.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void FirstBakeSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TextRig.FirstBakeSelfCheck");
        FaceRequest main = new("cx-firstbake-default", [], 14f), sinks = new("cx-firstbake-sinks", [], 99f);
        var reqs = new List<FaceRequest> { main, sinks };
        string? sheet = FirstBake(reqs, r => r.Contains(sinks) ? null : "sheet");
        check.Expect(sheet == "sheet" && reqs.Count == 1 && reqs[0] == main,
                     $"an early face that sinks the first bake is dropped and the default face bakes: {sheet ?? "no sheet"}, {reqs.Count} request(s)");
        reqs = new List<FaceRequest> { main, sinks };
        sheet = FirstBake<string>(reqs, _ => null);
        check.Expect(sheet == null && reqs.Count == 2 && reqs[1] == sinks,
                     $"with no sheet either way the early face stays asked for the next bake: {reqs.Count} request(s)");
        log.Info(check.Ok ? "TextRig.FirstBakeSelfCheck: passed" : "TextRig.FirstBakeSelfCheck: FAILED");
    }
}
