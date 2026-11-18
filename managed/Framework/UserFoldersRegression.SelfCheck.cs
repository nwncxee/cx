using CX.Alloy;
using CX.Services.Hotkeys;

namespace CX.Framework;

/// <summary>Regression tests for approved examples E11 and E12, which held before CX's caches and logs moved: settings
/// and the modders' shader dump stay beside nwn.ini whatever the aliases say. Oracle: the folder names written here.</summary>
internal static class UserFoldersRegression
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UserFoldersRegression.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) return check.Skip("on-device: the check settles the folders to its own and a game session has its real ones");
        string beside = UserFoldersApprovedExamples.StockUserDir();
        string root = Path.Combine(Path.GetTempPath(), $"cx-userfolders-still-{Environment.ProcessId}");
        try
        {
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(beside, ("CACHE", Path.Combine(root, "NWNCache")), ("LOGS", Path.Combine(root, "NWNLogs"))),
                               beside, Path.Combine(beside, "logs"), log);

            // Given CACHE and LOGS both set elsewhere in nwn.ini
            // When the player changes a setting in /settings and binds a hotkey
            // Then cx.cfg and cx-hotkeys.cfg are saved beside nwn.ini, as before
            check.Expect(Native.ConfigPath(Settings.FileName) == Path.Combine(beside, "cx.cfg"), $"E11 cx.cfg is saved beside nwn.ini, not {Native.ConfigPath(Settings.FileName)}");
            check.Expect(HotkeyStore.Path == Path.Combine(beside, "cx-hotkeys.cfg"), $"E11 cx-hotkeys.cfg is saved beside nwn.ini, not {HotkeyStore.Path}");

            // Given alloy.dumpshaders=1 in cx.cfg, and LOGS set elsewhere
            // When the game starts
            // Then the shaders are written to cx_shader_dump beside nwn.ini, as before
            check.Expect(ShaderStages.DumpDir == Path.Combine(beside, "cx_shader_dump"), $"E12 the shader dump is beside nwn.ini, not {ShaderStages.DumpDir}");
        }
        finally
        {
            UserFolders.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "UserFoldersRegression.SelfCheck: passed" : "UserFoldersRegression.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
