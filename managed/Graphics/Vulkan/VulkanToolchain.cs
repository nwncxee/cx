using System.Runtime.InteropServices;
using CX.Framework;
using Vortice.ShaderCompiler;

namespace CX.Graphics;

/// <summary>The GPU toolchain shipped beside the extender: shaderc, which turns a stage's GLSL into SPIR-V
/// while the game runs, and the Vulkan memory allocator. Nothing here renders -- the Vulkan backend is its
/// own work, and this is the loader and the compiler call it will be built on.</summary>
public static unsafe partial class VulkanToolchain
{
    /// <summary>The native libraries the build scripts stage beside us, by the leaf name each OS loads.
    /// The Vulkan loader itself is not here: it belongs to the machine, and the bindings find it.</summary>
    internal static readonly string[] Libraries = OperatingSystem.IsWindows()
        ? ["shaderc_shared.dll", "vma.dll"]
        : ["libshaderc_shared.so", "libvma.so"];

    /// <summary>The folder the extender was installed into: the one the launcher loaded it from, or the
    /// host's own when nothing says (the Windows proxy, which the game loads from beside nwmain.exe).</summary>
    internal static string InstallFolder =>
        HostEnv.ManagedLibrary is { Length: > 0 } m
            ? Path.GetDirectoryName(Path.GetFullPath(m)) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

    /// <summary>Loads one staged library by absolute path, so the by-soname load inside the bindings finds
    /// ours and no system copy can answer instead. Idempotent; the handle lives for the session.</summary>
    internal static bool Load(string leaf) =>
        NativeLibrary.TryLoad(Path.Combine(InstallFolder, leaf), out _);

    /// <summary>GLSL to a SPIR-V module through the staged shaderc, for the Vulkan 1.2 environment -- which
    /// the specification pins to SPIR-V 1.5. Returns null and fills <paramref name="error"/> with shaderc's
    /// own diagnostic when the source is refused. Cold: it builds a compiler per call.</summary>
    internal static byte[]? Compile(string source, string name, ShaderKind kind, out string error)
    {
        using var compiler = new Compiler();
        var options = new CompilerOptions { ShaderStage = kind, TargetEnv = TargetEnvironmentVersion.Vulkan_1_2 };
        CompileResult result = compiler.Compile(source, name, options);
        if (result.Status == CompilationStatus.Success)
        {
            error = "";
            return result.Bytecode;
        }
        error = string.IsNullOrEmpty(result.ErrorMessage) ? result.Status.ToString() : result.ErrorMessage;
        return null;
    }
}
