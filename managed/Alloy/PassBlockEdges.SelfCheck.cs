using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Inputs a pass block follows that move inside one frame: a second scene republishes the area light,
/// the cube count the program reads must be the block's, and the async fog list uploads its own copy. Oracle:
/// hand-written values, build counts and arena offsets.</summary>
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

        // the fog's async list runs beside the graphics list that uploaded the first copy, so it may not reuse it
        _vkc.ArenaUsed = 0; _vkc.PassAt = -1;
        PassBinds2(0, 0, 640, 480, 1);
        int graphics = VkArenaPass();
        int own = _vkc.ArenaUsed;
        PassBinds2(0, 0, 640, 480, 1);
        int async = VkArenaPass(since: own);
        check.Expect(graphics >= 0 && async >= own, $"the async list reused the copy at {graphics}, below its own upload at {own}");
        check.Expect(VkArenaPass() == async, "a graphics bind after the async list took a third copy");
        _vkc.ArenaUsed = 0; _vkc.PassAt = -1;

        CasterPass.CubeCount = cubesWas;
        FrameInputs.HasAreaAmbient = hasWas; FrameInputs.AreaAmbient = ambWas;
        Diag._ambientFallbackLogged = latchWas;
        _frameSeq = frameWas;
        ResetPassPeriod();
        log.Info(check.Ok ? "AlloyRenderer.PassBlockEdges.SelfCheck: passed" : "AlloyRenderer.PassBlockEdges.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
