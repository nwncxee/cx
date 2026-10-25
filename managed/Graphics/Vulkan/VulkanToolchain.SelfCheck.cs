using CX.Framework;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics;

public static unsafe partial class VulkanToolchain
{
    /// <summary>SPIR-V's magic word, transcribed from the specification's module header. Independent of
    /// shaderc: a compiler that emits anything else did not emit a SPIR-V module.</summary>
    private const uint SpirvMagic = 0x07230203;   // not-an-offset: the first word of a SPIR-V module

    /// <summary>The version word the specification pins the Vulkan 1.3 environment to -- SPIR-V 1.6, major
    /// and minor in the middle two bytes.</summary>
    private const uint SpirvVersion16 = 0x00010600;   // not-an-offset: a SPIR-V version word

    /// <summary>A compute shader with one storage buffer, enough to make shaderc parse, bind and emit.</summary>
    private const string ProbeSource = """
        #version 450
        layout(local_size_x = 8) in;
        layout(std430, binding = 0) buffer Out { uint v[]; };
        void main() { v[gl_GlobalInvocationID.x] = gl_GlobalInvocationID.x * 3u + 1u; }
        """;

    /// <summary>The staged toolchain, proven where a run can prove it: the libraries load from the install
    /// folder, the shipped shaderc turns GLSL into a module whose header the SPIR-V specification predicts
    /// and refuses a broken source, and the machine's Vulkan loader hands back an instance that is then
    /// destroyed. Off-device only: a game session creates no Vulkan instance and compiles no SPIR-V yet.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        {
            check.Skip("on-device: nothing in a game session reaches the Vulkan toolchain yet");
            return;
        }

        bool staged = true;
        foreach (string leaf in Libraries)
        {
            bool loaded = Load(leaf);
            staged &= loaded;
            check.Expect(loaded, $"{leaf} loads from the folder the extender was installed into");
        }
        if (staged)
        {
            CheckSpirv(check);
            CheckInstance(check, log);
        }
        log.Info(check.Ok ? "VulkanToolchain.SelfCheck: passed" : "VulkanToolchain.SelfCheck: FAILED");
    }

    /// <summary>Oracles: the SPIR-V specification's header -- magic word, the version Vulkan 1.3 requires,
    /// a whole number of 32-bit words -- and shaderc's own refusal of source that is not GLSL.</summary>
    private static void CheckSpirv(Checker check)
    {
        byte[]? spirv = Compile(ProbeSource, "cx_vk_probe.comp", ShaderKind.ComputeShader, out string error);
        check.Expect(spirv != null, $"the staged shaderc compiles a compute stage ({error})");
        if (spirv == null) return;

        check.Expect(spirv.Length >= 20 && spirv.Length % 4 == 0,
            $"a SPIR-V module is a whole number of words past its five-word header, not {spirv.Length} bytes");
        if (spirv.Length < 20) return;

        check.Expect(BitConverter.ToUInt32(spirv, 0) == SpirvMagic,
            $"the module starts with SPIR-V's magic word, not 0x{BitConverter.ToUInt32(spirv, 0):x8}");
        check.Expect(BitConverter.ToUInt32(spirv, 4) == SpirvVersion16,
            $"targeting Vulkan 1.3 emits SPIR-V 1.6, not 0x{BitConverter.ToUInt32(spirv, 4):x8}");
        // The header's fourth word is the id bound: every id the module defines is below it, so it cannot be 0.
        check.Expect(BitConverter.ToUInt32(spirv, 12) > 0, "the module declares an id bound");

        byte[]? refused = Compile("#version 450\nvoid main() { this is not glsl; }", "cx_vk_bad.comp",
            ShaderKind.ComputeShader, out string why);
        check.Expect(refused == null, "source that is not GLSL is refused rather than compiled");
        check.Expect(why.Length > 0, "a refusal carries shaderc's own diagnostic");
        CheckCache(check);
    }

    /// <summary>The module cache under a corrupt entry and a good one. Oracle: SPIR-V's magic word -- a word the corrupt
    /// entry does not start with -- and the bytes written into the cache by hand.</summary>
    private static void CheckCache(Checker check)
    {
        string src = ProbeSource + $"// cache probe {Environment.ProcessId}\n";
        string path = System.IO.Path.Combine(ProgramBinary.CacheDir, $"vk-{CacheKey(src, ShaderKind.ComputeShader):x16}.spv");
        try
        {
            System.IO.Directory.CreateDirectory(ProgramBinary.CacheDir);
            System.IO.File.WriteAllBytes(path, new byte[24]);
            byte[]? fresh = Compile(src, "cx_vk_cache.comp", ShaderKind.ComputeShader, out string e);
            check.Expect(fresh != null && BitConverter.ToUInt32(fresh, 0) == SpirvMagic,
                $"a cached entry that is not a module is refused and the stage compiled ({e})");
            byte[] planted = [.. BitConverter.GetBytes(SpirvMagic), .. new byte[20]];
            System.IO.File.WriteAllBytes(path, planted);
            byte[]? hit = Compile(src, "cx_vk_cache.comp", ShaderKind.ComputeShader, out _);
            check.Expect(hit != null && hit.AsSpan().SequenceEqual(planted), "a cached module is served as written, without a compile");
        }
        catch (System.IO.IOException x) { check.Expect(false, $"the cache folder is writable ({x.Message})"); }
        finally { try { System.IO.File.Delete(path); } catch (System.IO.IOException) { } }
    }

    /// <summary>The results the specification lets vkCreateInstance return for a request with no layers and
    /// no extensions that the machine cannot satisfy: no driver, a stale or half-installed one, or no memory
    /// to hold the instance. Host state, never our code, so the check skips on each of them.</summary>
    private static readonly VkResult[] NoUsableDriver =
    [
        VkResult.ErrorIncompatibleDriver,
        VkResult.ErrorInitializationFailed,
        VkResult.ErrorOutOfHostMemory,
        VkResult.ErrorOutOfDeviceMemory,
    ];

    /// <summary>Oracle: Vulkan's own contract -- a loader that initializes reports an instance version, and
    /// a create with no layers and no extensions either succeeds with a live handle or returns one of the
    /// results the specification reserves for a machine with no usable driver. No loader, no ICD and each of
    /// those results skip rather than fail, so a headless runner and a stale driver both stay green.</summary>
    private static void CheckInstance(Checker check, ExtenderLog log)
    {
        VkResult init;
        try { init = vkInitialize(); }
        catch (DllNotFoundException) { check.Skip("this machine has no Vulkan loader"); return; }
        if (init != VkResult.Success)
        {
            check.Skip($"the Vulkan loader would not initialize ({init})");
            return;
        }
        try
        {
            VkVersion api = vkEnumerateInstanceVersion();
            check.Expect(api.Major >= 1, $"the loader reports a Vulkan 1.x instance version, not {api}");

            var app = new VkApplicationInfo { apiVersion = VkVersion.Version_1_0 };
            var create = new VkInstanceCreateInfo { pApplicationInfo = &app };
            VkResult made = vkCreateInstance(&create, null, out VkInstance instance);
            if (Array.IndexOf(NoUsableDriver, made) >= 0)
            {
                check.Skip($"this machine has a Vulkan loader but no usable driver behind it ({made})");
                return;
            }
            check.Expect(made == VkResult.Success, $"a layerless, extensionless instance is created ({made})");
            if (made != VkResult.Success) return;
            check.Expect(instance.IsNotNull, "a created instance comes back as a live handle");
            new VkInstanceApi(in instance).vkDestroyInstance(null);
            log.Info($"VulkanToolchain: loader reports Vulkan {api}, instance created and destroyed");
        }
        finally { vkShutdown(); }
    }
}
