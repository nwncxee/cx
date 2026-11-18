namespace CX.Framework;

/// <summary>Where CX's own files live: caches in the game's CACHE folder, logs and diagnostic dumps in its LOGS folder,
/// each where nwn.ini's [Alias] puts it. Settings stay beside nwn.ini (<see cref="Native.ConfigPath"/>).</summary>
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
    internal static string Logs => _st.Logs ?? Path.Combine(Native.DefaultUserDirectory(), LogsName);

    /// <summary>The folder CX's caches go in: <c>cx</c> inside the game's CACHE.</summary>
    internal static string Cache => _st.Cache ?? Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), CacheName, Own);

    /// <summary>The removal of the cache folders an older CX left beside nwn.ini, while it runs.</summary>
    internal static Thread? Removal => _st.Removal;

    /// <summary>Settle the folders from the engine's own aliases and move this launch's logs there; false (and the
    /// defaults stand) until the engine has loaded its aliases, and on a build CX cannot read them on. MAIN THREAD.</summary>
    internal static bool Settle(ExtenderLog log)
    {
        if (_st.Logs != null) return true;
        string? user = Native.UserDirectory();
        if (user == null || !Settle(Native.AliasPath, user, Logs, log)) return false;
        if (!Same(ExtenderLog.Dir, Logs)) ExtenderLog.MoveTo(Logs);
        HookService.FollowLogs(Logs);
        log.Info($"UserFolders: logs in {Logs}, caches in {Cache}");
        return true;
    }

    /// <summary>Settle the folders from <paramref name="alias"/> (the engine's alias list, which always answers LOGS
    /// once loaded) for the user directory <paramref name="userDir"/>, carrying funchook.log over from
    /// <paramref name="fromLogs"/> and starting the removal of the folders an older CX left.</summary>
    internal static bool Settle(Func<string, string?> alias, string userDir, string fromLogs, ExtenderLog log)
    {
        string? logs = Folder(alias("LOGS"));
        if (logs == null) return false;
        string cache = Folder(alias("CACHE")) ?? Path.Combine(userDir, CacheName);
        Carry("funchook.log", fromLogs, logs, log);
        _st = new UserFoldersState { Logs = logs, Cache = Path.Combine(cache, Own), Removal = RemoveRetired(userDir, log) };
        return true;
    }

    /// <summary>Forget the settled folders.</summary>
    internal static void Reset() => _st = new();

    private static string? Folder(string? path) => string.IsNullOrEmpty(path) ? null : Path.TrimEndingDirectorySeparator(path);

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Copy <paramref name="name"/> from <paramref name="from"/> into <paramref name="to"/> and delete the
    /// original when it lets go: funchook may still hold it open.</summary>
    private static void Carry(string name, string from, string to, ExtenderLog log)
    {
        string src = Path.Combine(from, name);
        if (Same(from, to) || !File.Exists(src)) return;
        try
        {
            Directory.CreateDirectory(to);
            File.Copy(src, Path.Combine(to, name), overwrite: true);
            try { File.Delete(src); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Info($"UserFolders: {name} stays in {from} -- {e.Message}");
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
