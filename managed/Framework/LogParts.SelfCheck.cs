namespace CX.Framework;

/// <summary>The failure edges of <see cref="LogParts"/>, in a folder of the check's own. Oracle: the literals written here
/// and the files' own bytes.</summary>
internal static class LogPartsEdges
{
    private const int TestCap = 100;

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LogPartsEdges.SelfCheck");
        string d = Path.Combine(Path.GetTempPath(), $"cx-logparts-edges-{Environment.ProcessId}");
        static string? Read(string d, string name) => File.Exists(Path.Combine(d, name)) ? File.ReadAllText(Path.Combine(d, name)) : null;
        try
        {
            Directory.CreateDirectory(d);
            var one = new LogParts(d, TestCap);
            one.Write(new string('x', 500));
            one.Close();
            check.Expect(new FileInfo(Path.Combine(d, "cx.log")).Length is > 0 and <= TestCap, "one line longer than the cap is cut, so no part passes it");
            Directory.Delete(d, recursive: true);

            Directory.CreateDirectory(d);
            var wide = new LogParts(d, TestCap);
            wide.Write(new string('\u20ac', 500));
            wide.Close();
            check.Expect(new FileInfo(Path.Combine(d, "cx.log")).Length is > 90 and <= TestCap, "a cut counts bytes, not characters");
            Directory.Delete(d, recursive: true);

            Directory.CreateDirectory(d);
            string full = new string('f', TestCap - 1);
            var exact = new LogParts(d, TestCap);
            exact.Write(full);
            exact.Write(full);
            exact.Close();
            check.Expect(Read(d, "cx.1.log") == full + "\n" && Read(d, "cx.log") == full + "\n", "a line of exactly the cap fits, and a part exactly at the cap rolls whole");
            Directory.Delete(d, recursive: true);

            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "cx.log"), new string('n', 150) + new string('m', TestCap));
            var noBreak = new LogParts(d, TestCap);
            noBreak.Write("now");
            noBreak.Close();
            check.Expect(Read(d, "cx.1.log") == new string('m', TestCap), "an over-cap log with no line break keeps exactly its last bytes");
            Directory.Delete(d, recursive: true);

            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "cx.log"), "last\n");
            File.WriteAllText(Path.Combine(d, "cx.2.log"), "older\n");
            string block = Path.Combine(d, "cx.1.log");
            Directory.CreateDirectory(block);
            var blocked = new LogParts(d, TestCap);
            blocked.Write("to stderr");
            blocked.Write("to stderr");
            check.Expect(Read(d, "cx.log") == "last\n" && Read(d, "cx.2.log") == "older\n", "a move that fails loses nothing, however often it is retried");
            Directory.Delete(block);
            blocked.Write("now");
            blocked.Close();
            check.Expect(Read(d, "cx.log") == "now\n" && Read(d, "cx.1.log") == "last\n" && Read(d, "cx.2.log") == "older\n", "the next line after a failed move rotates");
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
