using System;
using System.IO;
using System.Linq;
using CX.Framework;
using Vortice.ShaderCompiler;

namespace CX.Graphics;

/// <summary>Regression tests for approved examples E17 and E21: a clean cache says nothing, and a module with no cache
/// header is compiled again once and read on the next launch. Oracle: a fresh compile's bytes and the toolchain's count
/// of fresh compiles.</summary>
internal static class CacheFileRegression
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "CacheFileRegression.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) return check.Skip("on-device: the check writes cache files and a game session has its real ones");
        bool shaderc = true;
        foreach (string leaf in VulkanToolchain.Libraries) shaderc &= VulkanToolchain.Load(leaf);
        if (!shaderc) return check.Skip("the staged shaderc did not load");
        string root = Path.Combine(Path.GetTempPath(), $"cx-cachefile-still-{Environment.ProcessId}");
        string ud = Path.Combine(root, "Neverwinter Nights");
        Directory.CreateDirectory(ud);
        UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), log);
        CacheFile.Reset();
        byte[]? Compile(string source) => VulkanToolchain.Compile(source, "cachefile-still-probe", ShaderKind.ComputeShader, out _);
        try
        {
            // Given no damaged cache file
            // When the player plays an area
            // Then cx.log says nothing about damaged cache files
            string clean = UserFoldersApprovedExamples.Probe("e17");
            Compile(clean); Compile(clean);
            check.Expect(CacheFile.Report() == null, "E17 a clean cache says nothing");

            // Given cache files written by the CX release before this one
            // When the player starts the new release and loads an area
            // Then the area looks the same as it does with an empty cache, and the caches are rebuilt for the next launch
            string s21 = UserFoldersApprovedExamples.Probe("e21");
            var before = CacheFilesApprovedExamples.Listing(ShaderCache.Dir);
            byte[]? good = Compile(s21);
            string? file = CacheFilesApprovedExamples.Added(ShaderCache.Dir, before).FirstOrDefault(f => f.EndsWith(".spv"));
            if (good != null && file != null)
            {
                File.WriteAllBytes(file, good);
                int before21 = VulkanToolchain.FreshCompiles;
                byte[]? got = Compile(s21);
                check.Expect(VulkanToolchain.FreshCompiles == before21 + 1, "E21 a module the previous release wrote is compiled again, not served as it lies");
                int fresh = VulkanToolchain.FreshCompiles;
                byte[]? next = Compile(s21);
                check.Expect(got != null && got.AsSpan().SequenceEqual(good), "E21 a module the previous release wrote gives the module a fresh compile gives");
                check.Expect(VulkanToolchain.FreshCompiles == fresh && next != null && next.AsSpan().SequenceEqual(good), "E21 the module is rebuilt once and read on the next launch");
                check.Expect(CacheFile.Report() == null, "E21 an older release's file is not counted as damaged");
            }
            else check.Expect(false, "E21 the probe stage compiled and left a cache file");
        }
        finally
        {
            CacheFile.Reset();
            UserFolders.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "CacheFileRegression.SelfCheck: passed" : "CacheFileRegression.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
