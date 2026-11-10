using System.Text;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>The sent-line history on disk (<c>chat.histsave</c>): <see cref="FileName"/> beside <c>cx.cfg</c>, one
/// entry per line, oldest first, with backslash and line breaks escaped so a pasted multi-line entry stays one
/// line. Cold: read once at init, written off the input thread after a send, deleted when the knob turns off.</summary>
public static class HistoryFile
{
    public const string FileName = "cx-chat-history.txt";

    private static readonly object _gate = new();

    /// <summary>The file beside <paramref name="cfgPath"/>, or "" when there is no settings file path.</summary>
    public static string PathBeside(string cfgPath)
        => cfgPath.Length == 0 ? "" : Path.Combine(Path.GetDirectoryName(cfgPath) ?? "", FileName);

    public static string Encode(string line)
        => line.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    public static string Decode(string line)
    {
        if (line.IndexOf('\\') < 0) return line;
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c != '\\' || i + 1 >= line.Length) { sb.Append(c); continue; }
            char n = line[++i];
            sb.Append(n switch { 'n' => '\n', 'r' => '\r', _ => n });
        }
        return sb.ToString();
    }

    /// <summary>The saved entries, or none when the file is missing or unreadable.</summary>
    public static string[] Load(string path, ExtenderLog? log)
    {
        if (path.Length == 0 || !File.Exists(path)) return Array.Empty<string>();
        try
        {
            lock (_gate)
            {
                string[] raw = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < raw.Length; i++) raw[i] = Decode(raw[i]);
                return raw;
            }
        }
        catch (Exception e) { log?.Info($"[chat] history: {path} unreadable, starting empty ({e.Message})"); return Array.Empty<string>(); }
    }

    /// <summary>Write <paramref name="lines"/> on a pool thread: a temp file renamed over the old one, serialized
    /// so two quick sends can't interleave.</summary>
    public static void SaveAsync(string path, IReadOnlyList<string> lines, ExtenderLog? log)
    {
        if (path.Length == 0) return;
        string[] snap = new string[lines.Count];
        for (int i = 0; i < snap.Length; i++) snap[i] = Encode(lines[i]);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (_gate)
                {
                    File.WriteAllLines(path + ".tmp", snap, new UTF8Encoding(false));
                    File.Move(path + ".tmp", path, overwrite: true);
                }
            }
            catch (Exception e) { log?.Info($"[chat] history: could not write {path} ({e.Message})"); }
        });
    }

    public static void Delete(string path, ExtenderLog? log)
    {
        if (path.Length == 0) return;
        try { lock (_gate) File.Delete(path); }
        catch (Exception e) { log?.Info($"[chat] history: could not delete {path} ({e.Message})"); }
    }

    /// <summary>Off-device proof that escaping round-trips every character a chat line can carry.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string[] lines = { "*nods*", "a\\b", "two\nlines", "cr\r\nlf", "trailing\\", "\\n literal", "" };
        foreach (string l in lines)
        {
            string e = Encode(l);
            check.Expect(e.IndexOf('\n') < 0 && e.IndexOf('\r') < 0, $"encoded \"{e}\" is one line");
            check.Expect(Decode(e) == l, $"\"{e}\" decodes back");
        }
        check.Expect(PathBeside(Path.Combine("nwn", "cx.cfg")) == Path.Combine("nwn", FileName) && PathBeside("") == "", "the file sits beside cx.cfg");
        log.Info(check.Ok ? "HistoryFile.SelfCheck PASS" : "HistoryFile.SelfCheck had FAILURES");
    }
}
