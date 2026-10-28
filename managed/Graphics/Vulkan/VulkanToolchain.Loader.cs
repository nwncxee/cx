using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics;

/// <summary>
/// Bringing the machine's Vulkan loader up. The bindings load it by soname, which is what a game session wants;
/// a build shell whose glibc reads no <c>/etc/ld.so.cache</c> resolves no soname at all, and the one loader on
/// the box then looks like no loader. So a refused initialize is retried behind an absolute-path load -- which
/// says "loader, no driver" where the driver's own manifest names a soname too, instead of "no Vulkan here".
/// </summary>
public static unsafe partial class VulkanToolchain
{
    /// <summary>Where a Linux distribution keeps the loader, by absolute path, most common first. The nix dev
    /// shell's own glibc resolves no soname, so this is the only way it finds the box's loader.</summary>
    private static readonly string[] LoaderPaths =
    [
        "/usr/lib/libvulkan.so.1",
        "/usr/lib/x86_64-linux-gnu/libvulkan.so.1",
        "/usr/lib64/libvulkan.so.1",
        "/run/opengl-driver/lib/libvulkan.so.1",
    ];

    /// <summary><c>vkInitialize</c>, retried behind an absolute-path load of each known loader: dlopening one
    /// registers its SONAME, which the by-name load inside the bindings then answers from. Idempotent, and
    /// never throws -- a machine with no loader at all reports <see cref="VkResult.ErrorInitializationFailed"/>.</summary>
    internal static VkResult InitLoader()
    {
        VkResult first = Init();
        if (first == VkResult.Success || OperatingSystem.IsWindows()) return first;
        foreach (string path in LoaderPaths)
        {
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out _)) continue;
            if (Init() == VkResult.Success) return VkResult.Success;
        }
        return first;
    }

    /// <summary>One initialize attempt, a missing loader reported as a result rather than an exception.</summary>
    private static VkResult Init()
    {
        try { return vkInitialize(); }
        catch (DllNotFoundException) { return VkResult.ErrorInitializationFailed; }
    }
}
