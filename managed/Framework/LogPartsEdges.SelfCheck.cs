namespace CX.Framework;

/// <summary>The edges of <see cref="LogParts"/> the approved examples leave out, in a folder of the check's own. Oracle:
/// the literals written here and the files' own bytes.</summary>
internal static class LogPartsEdges
{
    private const int TestCap = 100;

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string d = Path.Combine(Path.GetTempPath(), $"cx-logparts-edges-{Environment.ProcessId}");
        string? Read(string name) => File.Exists(Path.Combine(d, name)) ? File.ReadAllText(Path.Combine(d, name)) : null;
        void Fresh()
        {
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
            Directory.CreateDirectory(d);
        }
        void Launch(params string[] lines)
        {
            var p = new LogParts(d, TestCap);
            foreach (string l in lines) p.Write(l);
            p.Close();
        }
        try
        {
            foreach (string wide in new[] { "x", "€", "\U0001F600" })
            {
                Fresh();
                Launch(string.Concat(Enumerable.Repeat(wide, 500)));
                string? kept = Read("cx.log");
                check.Expect(new FileInfo(Path.Combine(d, "cx.log")).Length is > TestCap - 5 and <= TestCap, $"a line longer than the cap keeps what fits: {wide}");
                check.Expect(kept != null && !kept.Contains('�'), $"a cut never splits a character: {wide}");
            }

            Fresh();
            Launch(new string('x', 500), "");
            check.Expect(Read("cx.log") == "\n" && Read("cx.1.log")?.Length == TestCap, "a cut line counts its newline, so an empty line after it rolls");

            Fresh();
            string full = new('f', TestCap - 1);
            Launch(full, full);
            check.Expect(Read("cx.1.log") == full + "\n" && Read("cx.log") == full + "\n", "a line of exactly the cap fits, and a part exactly at the cap rolls whole");

            Fresh();
            File.WriteAllText(Path.Combine(d, "cx.log"), new string('n', 150) + new string('m', TestCap));
            Launch("now");
            check.Expect(Read("cx.1.log") == new string('m', TestCap), "an over-cap log with no line break keeps exactly its last bytes");

            Fresh();
            File.WriteAllText(Path.Combine(d, "cx.log"), "last\n");
            File.WriteAllText(Path.Combine(d, "cx.2.log"), "older\n");
            string block = Path.Combine(d, "cx.1.log");
            Directory.CreateDirectory(block);
            var blocked = new LogParts(d, TestCap);
            blocked.Write("to stderr");
            blocked.Write("to stderr");
            check.Expect(Read("cx.log") == "last\n" && Read("cx.2.log") == "older\n", "a move that fails loses nothing, however often it is retried");
            Directory.Delete(block);
            blocked.Write("now");
            blocked.Close();
            check.Expect(Read("cx.log") == "now\n" && Read("cx.1.log") == "last\n" && Read("cx.2.log") == "older\n", "the next line after a failed move rotates");
        }
        finally
        {
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { }
        }
        if (check.Ok) log.Info("LogPartsEdges.SelfCheck: passed");
        return check.Ok;
    }
}
