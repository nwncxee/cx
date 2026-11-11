using System;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.UnseenFixture;

namespace CX.Alloy;

/// <summary>More silhouettes than Alloy holds (approved-spec, E19). Oracle: the limit of 256 written here by hand as
/// the number of perception entries, and the log lines the check itself counts.</summary>
internal static class UnseenLimitApprovedExamples
{
    internal static bool SelfCheck(ExtenderLog log)
        => Run(log, "UnseenLimitApprovedExamples.SelfCheck", 2, (check, o) =>
        {
            DbId crowd = o[0], last = o[1];
            GameAlpha(crowd, 0.5f);
            GameAlpha(last, 0.5f);
            var sights = new Unseen.Sight[257];
            for (int k = 0; k < sights.Length; k++)
                sights[k] = new Unseen.Sight((uint)(5000 + k), Unseen.Heard, k < 256 ? crowd : last);
            int said = 0;
            Action<string>? tap = log.Tap;
            log.Tap = line => { if (line.Contains("heard-but-unseen")) said++; };
            try
            {
                // Given more than 256 creatures my character hears but does not see
                // When I look at them
                // Then the ones past the limit draw see-through as the stock client has them, and cx.log says so once
                Unseen.Frame(sights, Whole);
                Unseen.Frame(sights, Whole);
            }
            finally { log.Tap = tap; }
            check.Expect(Marked(crowd.Index) && !Marked(last.Index) && Drawn(last) == 0.5f && said == 1,
                         $"E19 the creature past the limit draws see-through and the log says so once: drew at {Drawn(last)}, said {said} time(s)");
        });
}
