namespace CX.Framework;

/// <summary>Where CX's own files live: caches in the game's CACHE folder, logs and diagnostic dumps in its LOGS folder,
/// each where nwn.ini's [Alias] puts it. Settings stay beside nwn.ini.</summary>
internal static class UserFolders
{
    /// <summary>The folders this session settled on; replaced whole by <see cref="Reset"/>.</summary>
    internal sealed class UserFoldersState
    {
        internal string? Logs;
        internal string? Cache;
        internal Thread? Removal;
    }

    private static UserFoldersState _st = new();

    /// <summary>The game's own folder names under the user directory, the defaults its aliases start from.</summary>
    private const string LogsName = "logs", CacheName = "cache";

    /// <summary>CX's folder inside the game's cache folder, so its files never mix with the game's.</summary>
    private const string Own = "cx";

    /// <summary>The cache folders an older CX kept beside nwn.ini, which nothing reads any more.</summary>
    private static readonly string[] Retired = ["cx-shaders", "cx-charts"];

    /// <summary>The folder cx.log, funchook.log and the diagnostic dumps go in: the game's LOGS, or before the game has
    /// read nwn.ini, the logs folder of the default user directory.</summary>
#pragma warning disable RS0030 // the pre-main logs folder: before the engine names its own, this is the one CX can know
    internal static string Logs => _st.Logs ?? Path.Combine(Native.DefaultUserDirectory(), LogsName);
#pragma warning restore RS0030

    /// <summary>The folder CX's caches go in: <c>cx</c> inside the game's CACHE.</summary>
    internal static string Cache => _st.Cache ?? Native.ConfigPath(Path.Combine(CacheName, Own));

    /// <summary>The removal of the cache folders an older CX left beside nwn.ini, while it runs.</summary>
    internal static Thread? Removal => _st.Removal;

    /// <summary>Settle the folders from the engine's own aliases and move this launch's logs there; false (and the
    /// defaults stand) until the engine has loaded its aliases, and on a build CX cannot read them on. MAIN THREAD.</summary>
    internal static bool Settle(ExtenderLog log)
    {
        if (_st.Logs != null) return true;
        string? user = Native.UserDirectory();
        if (user == null || !Settle(Native.AliasPath, user, Logs, log, ExtenderLog.MoveTo, HostEnv.Home))
        {
            log.Info($"UserFolders: the game's folders are not readable yet{(user == null ? " (no user directory)" : " (no LOGS alias)")}; logs stay in {Logs}, caches in {Cache}");
            return false;
        }
        log.Info($"UserFolders: logs in {Logs}, caches in {Cache}");
        return true;
    }

    /// <summary>Settle the folders from <paramref name="alias"/> (the engine's aliases) for <paramref name="userDir"/>:
    /// carry funchook.log over from <paramref name="fromLogs"/>, hand the logs folder to <paramref name="follow"/>, and
    /// clear what an older CX left beside nwn.ini and in <paramref name="home"/> (null: the home folder is not read).</summary>
    internal static bool Settle(Func<string, string?> alias, string userDir, string fromLogs, ExtenderLog log, Action<string>? follow = null,
                                string? home = null)
    {
        string? logs = Folder(alias("LOGS"));
        if (logs == null) return false;
        string cache = Folder(alias("CACHE")) ?? Path.Combine(userDir, CacheName);
        Carry("funchook.log", fromLogs, logs, log);
        RemoveOldLogs(logs, userDir, home, log);
        _st = new UserFoldersState { Logs = logs, Cache = Path.Combine(cache, Own), Removal = RemoveRetired(userDir, log) };
        follow?.Invoke(logs);
        return true;
    }

    /// <summary>Forget the settled folders.</summary>
    internal static void Reset() => _st = new();

    private static string? Folder(string? path) => string.IsNullOrEmpty(path) ? null : Path.TrimEndingDirectorySeparator(path);

    /// <summary>Whether two paths name one folder, as the OS compares them.</summary>
    internal static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Copy <paramref name="name"/> from <paramref name="from"/> into <paramref name="to"/>. The original stays:
    /// funchook keeps its trace open for the session and writes its teardown there.</summary>
    private static void Carry(string name, string from, string to, ExtenderLog log)
    {
        string src = Path.Combine(from, name);
        if (Same(from, to) || !File.Exists(src)) return;
        try
        {
            Directory.CreateDirectory(to);
            File.Copy(src, Path.Combine(to, name), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Info($"UserFolders: {name} stays in {from} -- {e.Message}");
        }
    }

    /// <summary>Delete the logs an older CX wrote outside the logs folder, saying once what went: cx.log and its parts beside
    /// nwn.ini and in <paramref name="home"/> when their first line is a CX line, and funchook.log beside nwn.ini. A
    /// place that is the logs folder now keeps everything.</summary>
    private static void RemoveOldLogs(string logs, string userDir, string? home, ExtenderLog log)
    {
        var gone = new List<string>();
        foreach (string dir in home == null ? [userDir] : new[] { userDir, home })
        {
            if (Same(dir, logs)) continue;
            for (int i = 0; i < LogParts.MaxParts; i++)
            {
                string path = Path.Combine(dir, i == 0 ? "cx.log" : $"cx.{i}.log");
                if (WrittenByCx(path) && Delete(path, log)) gone.Add(path);
            }
        }
        string trace = Path.Combine(userDir, "funchook.log");
        if (!Same(userDir, logs) && File.Exists(trace) && Delete(trace, log)) gone.Add(trace);
        if (gone.Count > 0) log.Info($"UserFolders: removed the logs an older CX left outside {logs}: {string.Join(", ", gone)}");
    }

    /// <summary>Whether <paramref name="path"/> is a log CX wrote: its first line carries CX's <c>[cx]</c> tag.</summary>
    private static bool WrittenByCx(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var f = new StreamReader(path);
            return f.ReadLine()?.Contains(" [cx] ", StringComparison.Ordinal) == true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Delete(string path, ExtenderLog log)
    {
        try { File.Delete(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Info($"UserFolders: the older CX log {path} could NOT be removed -- {e.Message}");
            return false;
        }
    }

    /// <summary>Delete the folders an older CX kept its caches in, on a thread of its own (they can hold thousands of
    /// files), saying once what went; null when there are none.</summary>
    private static Thread? RemoveRetired(string userDir, ExtenderLog log)
    {
        string[] found = Retired.Select(n => Path.Combine(userDir, n)).Where(Directory.Exists).ToArray();
        if (found.Length == 0) return null;
        var t = new Thread(() =>
        {
            Affinity.ClearThis();
            long bytes = 0;
            var gone = new List<string>();
            foreach (string dir in found)
                try
                {
                    bytes += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    Directory.Delete(dir, recursive: true);
                    gone.Add(Path.GetFileName(dir));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Info($"UserFolders: the older CX cache folder {dir} could NOT be removed -- {e.Message}");
                }
            if (gone.Count > 0)
                log.Info($"UserFolders: removed {string.Join(" and ", gone)} beside nwn.ini ({bytes >> 20} MiB), the caches an older CX kept there");
        })
        { IsBackground = true, Name = "cx-folders", Priority = ThreadPriority.BelowNormal };
        t.Start();
        return t;
    }
}
