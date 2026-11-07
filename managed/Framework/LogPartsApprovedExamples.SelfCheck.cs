namespace CX.Framework;

/// <summary>Launch rotation, rolling and the part cap (approved examples E1-E10, E12), in a folder of the check's own
/// at a small cap, so a client boot writes kilobytes. Oracle: the literals written here and the files' own bytes.</summary>
internal static class LogPartsApprovedExamples
{
    private const int TestCap = 100;

    /// <summary>A line of <paramref name="bytes"/> bytes counting its newline.</summary>
    private static string Line(char c, int bytes) => new(c, bytes - 1);

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LogPartsApprovedExamples.SelfCheck");
        string root = Path.Combine(Path.GetTempPath(), $"cx-logparts-{Environment.ProcessId}");
        int case_ = 0;
        string Fresh(params (string name, string text)[] seed)
        {
            string d = Path.Combine(root, (++case_).ToString());
            Directory.CreateDirectory(d);
            foreach (var (name, text) in seed) File.WriteAllText(Path.Combine(d, name), text);
            return d;
        }
        static string? Read(string d, string name)
        {
            string p = Path.Combine(d, name);
            if (!File.Exists(p)) return null;
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        }
        static int Count(string d) => Directory.GetFiles(d, "cx*.log").Length;
        static void Launch(string d, params string[] lines)
        {
            var p = new LogParts(d, TestCap);
            foreach (string l in lines) p.Write(l);
            p.Close();
        }

        try
        {
            // Given a cx.log from the last launch
            // When the client launches
            // Then cx.log holds only this launch's lines and cx.1.log holds the last launch's
            string d = Fresh(("cx.log", "last\n"));
            Launch(d, "now");
            check.Expect(Read(d, "cx.log") == "now\n" && Read(d, "cx.1.log") == "last\n", $"E1 a launch starts a fresh log: cx.log={Read(d, "cx.log")} cx.1.log={Read(d, "cx.1.log")}");

            // Given no log files
            // When the client launches
            // Then cx.log exists and there is no cx.1.log
            d = Fresh();
            Launch(d, "now");
            check.Expect(Read(d, "cx.log") == "now\n" && Read(d, "cx.1.log") == null, "E2 the first launch has nothing to move");

            // Given a cx.log and a cx.1.log
            // When the client launches
            // Then what was cx.1.log is now cx.2.log
            d = Fresh(("cx.log", "last\n"), ("cx.1.log", "older\n"));
            Launch(d, "now");
            check.Expect(Read(d, "cx.2.log") == "older\n" && Read(d, "cx.1.log") == "last\n", "E3 older runs move up one number");

            // Given a cx.log close to 10 MB during a session
            // When the next line would take it past 10 MB
            // Then that line goes into a new cx.log and no part holds more than 10 MB
            d = Fresh();
            string a = Line('a', 60), b = Line('b', 41);
            Launch(d, a, b);
            check.Expect(Read(d, "cx.1.log") == a + "\n" && Read(d, "cx.log") == b + "\n", "E4 a part never passes the cap");

            // Given a cx.log during a session
            // When the next line takes it to exactly 10 MB
            // Then that line stays in the same cx.log and nothing rolls
            d = Fresh();
            b = Line('b', 40);
            Launch(d, a, b);
            check.Expect(Read(d, "cx.log") == a + "\n" + b + "\n" && Read(d, "cx.1.log") == null, "E5 a line that just fits stays");
            check.Expect(new LogParts(d).Cap == 10_000_000, "E5 10 MB is 10,000,000 bytes");

            // Given a session that has rolled twice
            // When the session ends
            // Then cx.log holds its last lines, and cx.2.log, cx.1.log and cx.log read in that order hold every line whole
            d = Fresh();
            string l1 = Line('1', 60), l2 = Line('2', 60), l3 = Line('3', 60), l4 = Line('4', 30);
            Launch(d, l1, l2, l3, l4);
            check.Expect(Read(d, "cx.log") == l3 + "\n" + l4 + "\n", "E6 cx.log holds the session's last lines");
            check.Expect(Read(d, "cx.2.log") + Read(d, "cx.1.log") + Read(d, "cx.log") == $"{l1}\n{l2}\n{l3}\n{l4}\n", "E6 the parts in order hold every line whole");

            // Given 5 log parts
            // When the client launches
            // Then there are 5 log parts and the oldest is gone
            (string, string)[] five = [("cx.log", "p0\n"), ("cx.1.log", "p1\n"), ("cx.2.log", "p2\n"), ("cx.3.log", "p3\n"), ("cx.4.log", "p4\n")];
            d = Fresh(five);
            Launch(d, "now");
            check.Expect(Count(d) == 5 && Read(d, "cx.log") == "now\n" && Read(d, "cx.1.log") == "p0\n" && Read(d, "cx.2.log") == "p1\n"
                         && Read(d, "cx.3.log") == "p2\n" && Read(d, "cx.4.log") == "p3\n", "E7 a launch keeps the cap");

            // Given 4 log parts
            // When the client launches
            // Then there are 5 log parts and none is gone
            d = Fresh(five[..4]);
            Launch(d, "now");
            check.Expect(Count(d) == 5 && Read(d, "cx.1.log") == "p0\n" && Read(d, "cx.2.log") == "p1\n"
                         && Read(d, "cx.3.log") == "p2\n" && Read(d, "cx.4.log") == "p3\n", "E8 under the cap nothing is deleted");

            // Given 5 log parts during a session
            // When cx.log rolls
            // Then there are 5 log parts and the oldest is gone
            d = Fresh(five);
            Launch(d, l1, l2);
            check.Expect(Count(d) == 5 && Read(d, "cx.log") == l2 + "\n" && Read(d, "cx.1.log") == l1 + "\n" && Read(d, "cx.2.log") == "p0\n"
                         && Read(d, "cx.3.log") == "p1\n" && Read(d, "cx.4.log") == "p2\n", "E9 a roll keeps the cap");

            // Given a 20 MB cx.log written by an older build
            // When the client launches
            // Then cx.1.log holds the last 10 MB of it
            string huge = "", tail = "";
            for (int i = 0; i < 20; i++)
            {
                string row = $"row{i:D2}0123\n";
                huge += row;
                if (i >= 10) tail += row;
            }
            d = Fresh(("cx.log", huge));
            Launch(d, "now");
            check.Expect(Read(d, "cx.1.log") == tail, $"E10 the end of an over-cap log survives: {Read(d, "cx.1.log")}");

            // Given a client running
            // When a second client launches from the same home folder
            // Then the two clients' lines never mix in one file
            d = Fresh();
            var first = new LogParts(d, TestCap);
            first.Write("first-1");
            first.Flush();
            var second = new LogParts(d, TestCap);
            second.Write("second-1");
            second.Flush();
            first.Write("first-2");
            first.Close();
            second.Write("second-2");
            second.Close();
            check.Expect(Read(d, "cx.log") == "second-1\nsecond-2\n" && Read(d, "cx.1.log") == "first-1\nfirst-2\n",
                         $"E12 two clients keep their own files: cx.log={Read(d, "cx.log")} cx.1.log={Read(d, "cx.1.log")}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        if (check.Ok) log.Info("LogPartsApprovedExamples.SelfCheck: passed");
        return check.Ok;
    }
}
