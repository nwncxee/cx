using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Each term the fog line compares counts as a change, by a whole step of its last printed digit. Oracle: the steps
/// written here by hand, against the line's <c>F3</c> colour and <c>F1</c> range.</summary>
internal static class FogNoteEdges
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var col = new Vector3(0.5f, 0.5f, 0.5f);
        var said = new SceneFeed.FogNoteState();
        check.Expect(said.Changed(1, true, col, 70f, false), "the first fog says its line");
        check.Expect(!said.Changed(1, true, col, 70f, false), "the same fog says nothing");
        check.Expect(said.Changed(2, true, col, 70f, false), "a new source says its line");
        check.Expect(said.Changed(2, false, col, 70f, false), "the switch says its line");
        check.Expect(said.Changed(2, false, col, 70f, true), "the sky term says its line");
        check.Expect(said.Changed(2, false, col, 70.5f, true), "half a metre of range says its line");
        check.Expect(!said.Changed(2, false, col, 70.51f, true), "a hundredth of a metre says nothing");
        check.Expect(said.Changed(2, false, new Vector3(0.502f, 0.5f, 0.5f), 70.5f, true), "red by .002 says its line");
        check.Expect(said.Changed(2, false, new Vector3(0.502f, 0.502f, 0.5f), 70.5f, true), "green by .002 says its line");
        check.Expect(said.Changed(2, false, new Vector3(0.502f, 0.502f, 0.502f), 70.5f, true), "blue by .002 says its line");
        check.Expect(!said.Changed(2, false, new Vector3(0.5024f, 0.5024f, 0.5024f), 70.5f, true), "under the printed step says nothing");
        var nan = new Vector3(float.NaN, 0.502f, 0.502f);
        check.Expect(said.Changed(2, false, nan, 70.5f, true), "a NaN arriving says its line");
        check.Expect(!said.Changed(2, false, nan, 70.5f, true), "a NaN staying says nothing more");
        check.Expect(said.Changed(2, false, new Vector3(0.502f, 0.502f, 0.502f), 70.5f, true), "a NaN leaving says its line");
        if (check.Ok) log.Info("FogNoteEdges.SelfCheck: passed");
        return check.Ok;
    }
}
