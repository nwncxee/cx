using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Inputs a pass block follows that move inside one frame: a second scene republishes the area light,
/// and the cube count the program reads must be the block's. Oracle: hand-written values and build counts.</summary>
public sealed unsafe partial class AlloyRenderer
{
    internal static bool PassBlockEdgesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.PassBlockEdges.SelfCheck");
        int frameWas = _frameSeq, cubesWas = CasterPass.CubeCount;
        bool hasWas = FrameInputs.HasAreaAmbient, latchWas = Diag._ambientFallbackLogged;
        Vector3 ambWas = FrameInputs.AreaAmbient;
        _frameSeq = 7;
        ResetPassPeriod();

        FrameInputs.HasAreaAmbient = true; FrameInputs.AreaAmbient = new Vector3(0.25f);
        PassBinds2(0, 0, 640, 480, 1);
        FrameInputs.AreaAmbient = new Vector3(0.5f);
        PassBinds2(0, 0, 640, 480, 1);
        check.Expect(PassNoteOne().Contains("2 built/frame"), "a second scene's area light inside one frame reused the first scene's block");
        check.Expect(PassStagedBlock()->Ambient != new Vector3(0.25f * AlloyLights.AmbientScale), "the block kept the first scene's ambient");

        CasterPass.CubeCount = cubesWas + 3;
        PassBinds2(0, 0, 640, 480, 1);
        check.Expect(PassNoteOne().Contains("3 built/frame"), "a cube count that moved inside one frame reused a block staged at the old count");

        CasterPass.CubeCount = cubesWas;
        FrameInputs.HasAreaAmbient = hasWas; FrameInputs.AreaAmbient = ambWas;
        Diag._ambientFallbackLogged = latchWas;
        _frameSeq = frameWas;
        ResetPassPeriod();
        log.Info(check.Ok ? "AlloyRenderer.PassBlockEdges.SelfCheck: passed" : "AlloyRenderer.PassBlockEdges.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
