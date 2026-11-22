using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>A face two owners hold stays on the sheet until both let go. Oracle: the two owners and the
    /// request, planted by hand and taken out after.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void HoldersSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TextRig.HoldersSelfCheck");
        string tf = FontRegistry.Effective("cx-holders-selfcheck");
        const float px = 97f;
        bool planted;
        lock (_bakeLock)
        {
            planted = _requests.Count == 0;
            if (planted) _requests.Add(new FaceRequest("cx-holders-first", [], 1f));
            _holders[(tf, px)] = new HashSet<string>(StringComparer.Ordinal) { "a", "b" };
            _requests.Add(new FaceRequest(tf, [], px));
        }
        ReleaseFace(tf, px, "a");
        bool kept;
        lock (_bakeLock) kept = _requests.Exists(r => r.Typeface == tf && r.Px == px);
        check.Expect(kept, "one holder letting go leaves the face its other holder still uses");
        ReleaseFace(tf, px, "b");
        lock (_bakeLock)
        {
            check.Expect(!_requests.Exists(r => r.Typeface == tf && r.Px == px), "the last holder letting go drops the face");
            _holders.Remove((tf, px));
            _requests.RemoveAll(r => r.Typeface == tf && r.Px == px);
            if (planted) _requests.RemoveAll(r => r.Typeface == "cx-holders-first");
        }
        log.Info(check.Ok ? "TextRig.HoldersSelfCheck: passed" : "TextRig.HoldersSelfCheck: FAILED");
    }
}
