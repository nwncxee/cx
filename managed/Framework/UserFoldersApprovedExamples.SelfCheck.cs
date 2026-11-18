using CX.Alloy;
using CX.Graphics;
using CX.Graphics.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Framework;

/// <summary>CX's files follow the game's own folders (approved examples E1-E10), against an alias table standing in for
/// nwn.ini's [Alias] in a user directory of the check's own. Oracle: the folder names written here and the files'
/// own bytes; the toolchain's count of fresh compiles for "compiled again".</summary>
internal static class UserFoldersApprovedExamples
{
    /// <summary>The engine's alias list: LOGS and CACHE always answer, from the user directory unless
    /// <paramref name="moved"/> names them elsewhere.</summary>
    internal static Func<string, string?> Aliases(string userDir, params (string Name, string Path)[] moved) => name =>
    {
        foreach (var m in moved) if (m.Name == name) return m.Path;
        return name switch { "LOGS" => Path.Combine(userDir, "logs"), "CACHE" => Path.Combine(userDir, "cache"), _ => null };
    };

    /// <summary>A compute stage no cache has seen, compiled through the toolchain.</summary>
    internal static string Probe(string tag) =>
        VkGlsl.Version + "layout(local_size_x = 1) in;\nvoid main() {}\n// " + tag + " " + Guid.NewGuid().ToString("N") + "\n";

    internal static string[] Files(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : [];

    internal static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(f).ReadToEnd();
    }

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UserFoldersApprovedExamples.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) return check.Skip("on-device: the check settles the folders to its own and a game session has its real ones");
        bool shaderc = true;
        foreach (string leaf in VulkanToolchain.Libraries) shaderc &= VulkanToolchain.Load(leaf);
        string root = Path.Combine(Path.GetTempPath(), $"cx-userfolders-{Environment.ProcessId}");
        int case_ = 0;
        string Fresh(string name)
        {
            string d = Path.Combine(root, (++case_).ToString(), name);
            Directory.CreateDirectory(d);
            return d;
        }
        bool Compile(string source) => VulkanToolchain.Compile(source, "userfolders-probe", ShaderKind.ComputeShader, out _) != null;
        try
        {
            // Given nwn.ini's [Alias] section sets CACHE to D:\NWNCache
            // When the player plays an area with Alloy on
            // Then CX's shader and chart caches are in D:\NWNCache\cx
            // And no cx-shaders or cx-charts folder appears beside nwn.ini
            string ud = Fresh("Neverwinter Nights"), moved = Fresh("NWNCache");
            UserFolders.Settle(Aliases(ud, ("CACHE", moved)), ud, Path.Combine(ud, "logs"), log);
            if (shaderc) check.Expect(Compile(Probe("e1")) && Files(Path.Combine(moved, "cx")).Any(f => f.EndsWith(".spv")),
                $"E1 a shader cache file is under the moved CACHE's cx folder: {string.Join(", ", Files(moved))}");
            MeshChart.Chart(MeshChart.Cube(2));
            check.Expect(Files(Path.Combine(moved, "cx")).Any(f => f.EndsWith(".bin")),
                $"E1 a chart cache file is under the moved CACHE's cx folder: {string.Join(", ", Files(moved))}");
            check.Expect(!Directory.Exists(Path.Combine(ud, "cx-shaders")) && !Directory.Exists(Path.Combine(ud, "cx-charts")),
                "E1 no cx-shaders or cx-charts folder beside nwn.ini");
            UserFolders.Reset();

            // Given nwn.ini sets no CACHE alias
            // When the player plays an area with Alloy on
            // Then CX's caches are in the cache\cx folder beside nwn.ini
            ud = Fresh("Neverwinter Nights");
            UserFolders.Settle(Aliases(ud), ud, Path.Combine(ud, "logs"), log);
            string own = Path.Combine(ud, "cache", "cx");
            if (shaderc) check.Expect(Compile(Probe("e2")) && Files(own).Any(f => f.EndsWith(".spv")), $"E2 a shader cache file is under cache{Path.DirectorySeparatorChar}cx: {string.Join(", ", Files(ud))}");
            MeshChart.Chart(MeshChart.Cube(2));
            check.Expect(Files(own).Any(f => f.EndsWith(".bin")), $"E2 a chart cache file is under cache{Path.DirectorySeparatorChar}cx: {string.Join(", ", Files(ud))}");
            UserFolders.Reset();

            // Given the player played an area once with CACHE set to D:\NWNCache
            // When they start the game again and load the same area
            // Then cx.log says no shader was compiled again
            if (shaderc)
            {
                ud = Fresh("Neverwinter Nights"); moved = Fresh("NWNCache");
                string area = Probe("e3");
                UserFolders.Settle(Aliases(ud, ("CACHE", moved)), ud, Path.Combine(ud, "logs"), log);
                Compile(area);
                UserFolders.Reset();
                UserFolders.Settle(Aliases(ud, ("CACHE", moved)), ud, Path.Combine(ud, "logs"), log);
                int fresh = VulkanToolchain.FreshCompiles;
                Compile(area);
                check.Expect(VulkanToolchain.FreshCompiles == fresh && Files(Path.Combine(moved, "cx")).Any(f => f.EndsWith(".spv")),
                    $"E3 the second launch compiled {VulkanToolchain.FreshCompiles - fresh} stage(s) the first left in the moved CACHE");
                UserFolders.Reset();
            }

            // Given cx-shaders and cx-charts folders beside nwn.ini from an older CX
            // When the new CX starts
            // Then both folders are gone, and cx.log says so once
            ud = Fresh("Neverwinter Nights");
            Directory.CreateDirectory(Path.Combine(ud, "cx-shaders"));
            Directory.CreateDirectory(Path.Combine(ud, "cx-charts"));
            File.WriteAllText(Path.Combine(ud, "cx-shaders", "vk-0123456789abcdef.spv"), "old");
            File.WriteAllText(Path.Combine(ud, "cx-charts", "0123456789abcdef.bin"), "old");
            var heard = new List<string>();
            UserFolders.Settle(Aliases(ud), ud, Path.Combine(ud, "logs"), new ExtenderLog { Tap = heard.Add });
            UserFolders.Removal?.Join(TimeSpan.FromSeconds(30));
            check.Expect(!Directory.Exists(Path.Combine(ud, "cx-shaders")) && !Directory.Exists(Path.Combine(ud, "cx-charts")),
                "E4 the old cx-shaders and cx-charts folders are gone");
            int said = heard.Count(l => l.Contains("cx-shaders") && l.Contains("cx-charts"));
            check.Expect(said == 1, $"E4 cx.log says so once, not {said} times: {string.Join(" | ", heard)}");
            UserFolders.Reset();

            // Given general.logs=1 in cx.cfg, and nwn.ini's [Alias] sets LOGS to D:\NWNLogs
            // When the player starts the game and reaches the main menu
            // Then cx.log is in D:\NWNLogs
            // And it starts with the launch's first lines
            ud = Fresh("Neverwinter Nights"); moved = Fresh("NWNLogs");
            var parts = new LogParts(Path.Combine(ud, "logs"));
            parts.Write("launch line");
            UserFolders.Settle(Aliases(ud, ("LOGS", moved)), ud, Path.Combine(ud, "logs"), log);
            parts.MoveTo(UserFolders.Logs);
            parts.Write("menu line");
            parts.Close();
            check.Expect(Read(Path.Combine(moved, "cx.log")) == "launch line\nmenu line\n", $"E5 cx.log in the moved LOGS holds the whole launch: {Read(Path.Combine(moved, "cx.log"))}");
            check.Expect(Read(Path.Combine(ud, "logs", "cx.log")) == null, "E5 no cx.log is left in the logs folder beside nwn.ini");
            UserFolders.Reset();

            // Given general.logs=1 in cx.cfg and no LOGS alias
            // When the player starts the game and reaches the main menu
            // Then cx.log is in the logs folder beside nwn.ini, on Linux as on Windows
            // And no new cx.log appears in Documents\Neverwinter Nights itself or in the Linux home folder
            string defaultLogs = Path.Combine(Native.DefaultUserDirectory(), "logs");
            check.Expect(ExtenderLog.Dir == defaultLogs, $"E6 this launch's cx.log is in {defaultLogs}, not {ExtenderLog.Dir}");
            ud = Fresh("Neverwinter Nights");
            UserFolders.Settle(Aliases(ud), ud, Path.Combine(ud, "logs"), log);
            check.Expect(UserFolders.Logs == Path.Combine(ud, "logs"), $"E6 with no LOGS alias the logs folder is beside nwn.ini, not {UserFolders.Logs}");
            UserFolders.Reset();
            DateTime started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            foreach (string dir in new[] { Native.DefaultUserDirectory(), HostEnv.Home })
            {
                string stray = Path.Combine(dir, "cx.log");
                check.Expect(!File.Exists(stray) || File.GetLastWriteTimeUtc(stray) < started, $"E6 this launch wrote no {stray}");
            }

            // Given general.logs=1, and LOGS set to D:\NWNLogs
            // When the game closes before it shows its first screen
            // Then cx.log, with that launch's lines, is in the logs folder beside nwn.ini
            string marker = $"UserFoldersApprovedExamples: E7 marker {Guid.NewGuid():N}";
            log.Always(marker);
            ExtenderLog.Flush();
            check.Expect(Read(Path.Combine(defaultLogs, "cx.log"))?.Contains(marker) == true,
                $"E7 before the game names its folders, this launch's lines are in {Path.Combine(defaultLogs, "cx.log")}");

            // Given a game build CX does not support
            // When the player starts the game
            // Then cx.log, in the logs folder beside nwn.ini, says why CX stood down
            check.Expect(!UserFolders.Settle(log) && UserFolders.Logs == defaultLogs && ExtenderLog.Dir == defaultLogs,
                $"E8 with no engine aliases to read, cx.log stays in {defaultLogs}: {UserFolders.Logs}, {ExtenderLog.Dir}");

            // Given general.hookdebug=1 in cx.cfg, and LOGS set to D:\NWNLogs
            // When the player starts the game
            // Then funchook.log is in D:\NWNLogs, beside cx.log
            ud = Fresh("Neverwinter Nights"); moved = Fresh("NWNLogs");
            Directory.CreateDirectory(Path.Combine(ud, "logs"));
            File.WriteAllText(Path.Combine(ud, "logs", "funchook.log"), "hook trace\n");
            UserFolders.Settle(Aliases(ud, ("LOGS", moved)), ud, Path.Combine(ud, "logs"), log);
            check.Expect(Read(Path.Combine(moved, "funchook.log")) == "hook trace\n", "E9 funchook.log is in the moved LOGS");
            UserFolders.Reset();

            // Given LOGS set to D:\NWNLogs
            // When the player types /alloy framedump, or /alloy snap
            // Then the dump is in D:\NWNLogs\cx_frame_dump, or the snap in D:\NWNLogs\cx_snap
            ud = Fresh("Neverwinter Nights"); moved = Fresh("NWNLogs");
            UserFolders.Settle(Aliases(ud, ("LOGS", moved)), ud, Path.Combine(ud, "logs"), log);
            check.Expect(FrameDump.Dir == Path.Combine(moved, "cx_frame_dump"), $"E10 the frame dump goes to the moved LOGS, not {FrameDump.Dir}");
            check.Expect(FrameTap.Dir == Path.Combine(moved, "cx_snap"), $"E10 the snap goes to the moved LOGS, not {FrameTap.Dir}");
        }
        finally
        {
            UserFolders.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "UserFoldersApprovedExamples.SelfCheck: passed" : "UserFoldersApprovedExamples.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
