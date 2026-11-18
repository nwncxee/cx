namespace CX.Framework;

/// <summary>Where CX's own files live: its caches, its logs and diagnostic dumps, and its settings, each in the folder
/// the game keeps for that kind of file.</summary>
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

    /// <summary>The folder cx.log, funchook.log and the diagnostic dumps go in.</summary>
    internal static string Logs => _st.Logs ?? (OperatingSystem.IsWindows() ? Native.DefaultUserDirectory() : HostEnv.Home);

    /// <summary>The folder CX's caches go in.</summary>
    internal static string Cache => _st.Cache ?? (Native.UserDirectory() ?? Native.DefaultUserDirectory());

    /// <summary>The removal of the cache folders an older CX left beside nwn.ini, while it runs.</summary>
    internal static Thread? Removal => _st.Removal;

    /// <summary>Settle the folders from the engine's own aliases. MAIN THREAD.</summary>
    internal static bool Settle(ExtenderLog log) => false;

    /// <summary>Settle the folders from <paramref name="alias"/>, for a user directory <paramref name="userDir"/>,
    /// carrying funchook.log over from <paramref name="fromLogs"/>.</summary>
    internal static bool Settle(Func<string, string?> alias, string userDir, string fromLogs, ExtenderLog log) => false;

    /// <summary>Forget the settled folders.</summary>
    internal static void Reset() => _st = new();
}
