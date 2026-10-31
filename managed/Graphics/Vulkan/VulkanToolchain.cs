using System;
using System.IO;
using System.Runtime.InteropServices;
using CX.Framework;
using Vortice.ShaderCompiler;

namespace CX.Graphics;

/// <summary>The GPU toolchain shipped beside the extender: shaderc, which turns a stage's GLSL into SPIR-V
/// while the game runs, and the Vulkan memory allocator. Nothing here renders -- the Vulkan backend is its
/// own work, and this is the loader and the compiler call it will be built on.</summary>
public static unsafe partial class VulkanToolchain
{
    /// <summary>The memory allocator's own leaf, named apart because its VIRTUAL blocks are CPU bookkeeping a
    /// caller may want before any backend has brought the rest up (the mesh arena's placement).</summary>
    internal static readonly string VmaLibrary = OperatingSystem.IsWindows() ? "vma.dll" : "libvma.so";

    /// <summary>The native libraries the build scripts stage beside us, by the leaf name each OS loads.
    /// The Vulkan loader itself is not here: it belongs to the machine, and the bindings find it.</summary>
    internal static readonly string[] Libraries = OperatingSystem.IsWindows()
        ? ["shaderc_shared.dll", VmaLibrary]
        : ["libshaderc_shared.so", VmaLibrary];

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

    /// <summary>Where each kind of GL binding point lands in the one descriptor set: GL numbers samplers, uniform
    /// blocks, storage blocks and images apart, Vulkan in one space, so each kind is shifted past the last.</summary>
    internal const uint SampledBase = 0, UniformBase = 48, StorageBase = 64, ImageBase = 80;

    /// <summary>The second descriptor set: one variable-length array of 2D samplers a bindless handle indexes
    /// (<c>VkBackend.Bindless</c>). Set 0 is the per-bind set the binding bases above lay out.</summary>
    internal const uint BindlessSet = 1;

    /// <summary>GLSL to a SPIR-V module through the staged shaderc, for the Vulkan 1.3 environment -- which
    /// the specification pins to SPIR-V 1.6 -- with GL's binding spaces shifted apart (<see cref="UniformBase"/>)
    /// and unlocated stage inputs and outputs numbered in declaration order. Returns null and fills
    /// <paramref name="error"/> with shaderc's own diagnostic when the source is refused. Cold: a compiler per call.</summary>
    internal static byte[]? Compile(string source, string name, ShaderKind kind, out string error)
    {
        string cached = Path.Combine(ShaderCache.Dir, $"vk-{CacheKey(source, kind):x16}.spv");
        if (Cached(cached) is { } hit) { error = ""; return hit; }
        CX.Alloy.LightmapBake.CompileNotice();
        byte[]? spirv = CompileFresh(source, name, kind, out error);
        if (spirv != null)
            try { Directory.CreateDirectory(ShaderCache.Dir); File.WriteAllBytes(cached, spirv); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        return spirv;
    }

    /// <summary>Bumped whenever <see cref="CompileFresh"/>'s options change: a module cached under the old ones is
    /// then never read.</summary>
    private const ulong CacheVersion = 1;

    /// <summary>The cache key of one stage: its exact text, its kind and <see cref="CacheVersion"/>.</summary>
    private static ulong CacheKey(string source, ShaderKind kind)
        => CX.Alloy.Mdl.MdlParser.ContentHash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(source.AsSpan()))
           ^ ((ulong)kind + 1) * 0x9E3779B97F4A7C15UL ^ CacheVersion;   // not-an-offset: golden-ratio hash multiplier

    /// <summary>A cached module, touched so the sweep keeps it; null when there is none or it is not a module.</summary>
    private static byte[]? Cached(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 20 || b.Length % 4 != 0 || BitConverter.ToUInt32(b, 0) != SpirvMagic) { File.Delete(path); return null; }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return b;
        }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The compile itself, uncached.</summary>
    private static byte[]? CompileFresh(string source, string name, ShaderKind kind, out string error)
    {
        using var compiler = new Compiler();
        var options = new CompilerOptions { ShaderStage = kind, TargetEnv = TargetEnvironmentVersion.Vulkan_1_3, AutoMapLocations = true };
        options.Bindings.Add(new BindingBase(UniformKind.Buffer, UniformBase));
        options.Bindings.Add(new BindingBase(UniformKind.StorageBuffer, StorageBase));
        options.Bindings.Add(new BindingBase(UniformKind.Image, ImageBase));
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
