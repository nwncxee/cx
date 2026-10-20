using System;
using System.Collections.Generic;

namespace CX.Framework;

public static partial class AreaSettings
{
    /// <summary>Oracle: a GIT written by neverwinter.nim's <c>nwn_gff</c> from hand-written JSON (five VarTable entries,
    /// one not ours, one upper-case prefix), and a throwaway knob group for the hold expansion.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Gff? git = Gff.TryParse(Convert.FromBase64String(Fixture), out string? why);
        check.Expect(git != null, $"the fixture parses ({why})");
        if (git is not Gff g) { log.Info("AreaSettings.SelfCheck: FAILED"); return; }
        check.Expect(g.FileType == "GIT ", "file type GIT");
        check.Expect(g.Top.Get("AreaProperties")?.Struct?.Get("MusicDay")?.Integer == 7, "a nested struct's int");
        check.Expect(g.Top.Get("Creature List") is { } cl && cl.Type == Gff.FieldType.List && !cl.List.GetEnumerator().MoveNext(), "an empty list");
        List<(string Key, string Value)> named = Read(g);
        check.Expect(string.Join(";", named.ConvertAll(n => n.Key + "=" + n.Value))
                     == "vfog.haze=0.3;vfog.enabled=1;vfog.quality=high;vfog.top=-40.5", "the cx. variables, in order, typed to text");
        check.Expect(Gff.TryParse(new byte[] { 1, 2, 3 }, out _) == null, "a short file is refused");
        byte[] cut = Convert.FromBase64String(Fixture)[..300];
        check.Expect(Gff.TryParse(cut, out _) == null, "a truncated file is refused");

        var t = new KnobGroup("cx-area-selfcheck", "Test", "Test", "on", "x", false, Effect.Live, null, null, "", register: false);
        var dial = t.Slider("dial", "dial", 0f, 1f, 0.1f, 0.25f, help: "x");
        var named2 = t.Slider("named", "named", 0f, 1f, 0.1f, 0.5f, help: "x");
        var preset = t.Dropdown("preset", "preset", new[] { "a", "b" }, "a", help: "x");
        t.Slider("rung", "rung", 0f, 1f, 0.1f, 0.5f, help: "x").Ladder(preset, 0.1f, 0.9f);
        Knob? Find(string key) => key.StartsWith("cx-area-selfcheck.", StringComparison.Ordinal) ? t.Get(key["cx-area-selfcheck.".Length..]) : null;
        check.Expect(Holds(new() { ("cx-area-selfcheck.named", "0.7") }, Find).Count == 1, "a group no feature opted in is not expanded");
        ServerPolicy.AllowKnobs("cx-area-selfcheck.");
        var holds = Holds(new() { ("cx-area-selfcheck.named", "0.7"), ("other.x", "1") }, Find);
        check.Expect(string.Join(";", holds.ConvertAll(h => h.Key + "=" + h.Value))
                     == "cx-area-selfcheck.named=0.7;other.x=1;cx-area-selfcheck.enabled=1;cx-area-selfcheck.dial=0.25;cx-area-selfcheck.preset=a",
                     "named keys first, then the group's master on and its other dials at their defaults, the ladder rung left out");
        ServerPolicy.ForbidKnobs("cx-area-selfcheck.");
        log.Info(check.Ok ? "AreaSettings.SelfCheck: passed" : "AreaSettings.SelfCheck: FAILED");
    }

    private const string Fixture =
        "R0lUIFYzLjI4AAAABwAAAIwAAAATAAAAcAEAAAcAAADgAQAAXAAAADwCAABIAAAAhAIAABwAAAD/////PAAAAAMAAAD/////AAAAAAEAAAAAAAAAAAAAAAMAAAAAAAAADAAAAAMAAAAAAAAAGAAAAAMAAAAAAAAAJAAAAAMAAAAAAAAAMAAAAAMAAAAFAAAAAQAAAAcAAAAOAAAAAAAAAAEAAAAPAAAAAgAAAAAAAAAKAAAABAAAAAAAAAAEAAAABQAAAAIAAAAIAAAABgAAAJqZmT4KAAAABAAAABAAAAAEAAAABQAAAAEAAAAFAAAABgAAAAUAAAAKAAAABAAAAB8AAAAEAAAABQAAAAEAAAAFAAAABgAAAAEAAAAKAAAABAAAADIAAAAEAAAABQAAAAMAAAAKAAAABgAAAEUAAAAKAAAABAAAAE0AAAAEAAAABQAAAAIAAAAIAAAABgAAAAAAIsIPAAAAAwAAAAQAAABBcmVhUHJvcGVydGllcwAATXVzaWNEYXkAAAAAAAAAAENyZWF0dXJlIExpc3QAAABWYXJUYWJsZQAAAAAAAAAATmFtZQAAAAAAAAAAAAAAAFR5cGUAAAAAAAAAAAAAAABWYWx1ZQAAAAAAAAAAAAAADAAAAGN4LnZmb2cuaGF6ZQsAAABxdWVzdF9zdGF0ZQ8AAABjeC52Zm9nLmVuYWJsZWQPAAAAQ1gudmZvZy5xdWFsaXR5BAAAAGhpZ2gLAAAAY3gudmZvZy50b3ADAAAABAAAAAUAAAAGAAAABwAAAAgAAAAJAAAACgAAAAsAAAAMAAAADQAAAA4AAAAPAAAAEAAAABEAAAABAAAAAgAAABIAAAAAAAAABQAAAAIAAAADAAAABAAAAAUAAAAGAAAA";
}
