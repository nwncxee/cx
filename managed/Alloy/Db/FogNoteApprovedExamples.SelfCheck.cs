using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>When the fog line writes (approved examples E14, E15). Oracle: the engine's fade arithmetic, written here by
/// hand from <c>CNWCArea::Update</c>, which rewrites the colour every frame while a fade runs.</summary>
internal static class FogNoteApprovedExamples
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FogNoteApprovedExamples.SelfCheck");
        var col = new Vector3(0.678f, 0.796f, 0.949f);
        var said = new SceneFeed.FogNoteState();
        int Say(Vector3 c) => said.Changed(1, true, c, 70f, false) ? 1 : 0;

        // Given a player standing still in an area whose fog does not change
        // When a minute passes
        // Then cx.log gains no fog line
        Say(col);
        Vector3 target = col, cur = col;
        int lines = 0;
        for (uint remaining = 300_000, dt = 17; remaining > dt; remaining -= dt)
        {
            float t = (float)dt / remaining, keep = 1f - t;
            cur = new Vector3(target.X * t + cur.X * keep, target.Y * t + cur.Y * keep, target.Z * t + cur.Z * keep);
            lines += Say(cur);
        }
        check.Expect(lines == 0, $"E14 a fade to the colour it already has writes no line, wrote {lines}");

        // Given a player in an area
        // When the area's fog colour changes
        // Then cx.log gains one fog line
        var next = new Vector3(0.306f, 0.318f, 0.322f);
        check.Expect(Say(next) == 1, "E15 a real fog change writes a line");
        check.Expect(Say(next) == 0, "E15 that change writes only one");

        if (check.Ok) log.Info("FogNoteApprovedExamples.SelfCheck: passed");
        return check.Ok;
    }
}
