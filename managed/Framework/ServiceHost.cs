using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace CX.Framework;

/// <summary>
/// The "hard work" contributors don't touch: discover every IExtenderService,
/// construct it, hand it the shared singletons, initialize, then commit all hooks.
/// </summary>
public static class ServiceHost
{
    // Reflection over our own assembly is trim-safe here: the csproj roots cx_managed
    // whole (TrimmerRootAssembly), so no service type or its ctor is ever removed.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly is rooted; nothing trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Assembly is rooted; parameterless ctors preserved.")]
    public static void Run()
    {
        var log = new ExtenderLog();
        // Which build produced the lines below. build.sh already stamps this from `git describe`, and a log
        // that does not say it is a log you cannot act on: half a debugging round trip is spent establishing
        // whether the run even contains the fix being discussed.
        log.Info("NWNCX:EE build " + (Assembly.GetExecutingAssembly()
                     .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
                     .InformationalVersion ?? "unknown"));
        log.Info($"host: GC server={System.Runtime.GCSettings.IsServerGC} latency={System.Runtime.GCSettings.LatencyMode} cpus={Environment.ProcessorCount}");
        // the one rot gate, and the whole extender is behind it -- both platforms, ahead of DISCOVERY
        // rather than ahead of Initialize, because constructing a service runs its type initializer and a
        // type initializer is enough to latch engine state. Nothing constructed, nothing hooked, so no
        // feature can half-fire against a client these offsets were never measured against. The build key is
        // the linker's build identity, the only lock that exists on both platforms (EngineLayout).
        if (!EngineLayout.KeyOk)
        {
            string rot = $"client build changed, extender standing down WHOLE -- including the Alloy renderer. " +
                         $"Observed build {EngineLayout.BuildKey}, known: {ClientBuilds.Known}. " +
                         "The game runs vanilla until a CX release for this build (docs/update-day.md).";
            log.Always("host: " + rot);
            log.Always("host: 0 service(s) discovered, 0 hook(s) installed");
            SymbolMap.RotNotice("NWNCX:EE: " + rot);   // Windows-only: a version.dll proxy has no log the player reads
            ExtenderLog.Flush();   // Always only enqueues, and nothing else in this process will drain it
            return;
        }

        // The boot read, and this is as early as it goes: ahead of DISCOVERY, not just ahead of
        // Initialize. Constructing a service runs its type initializer, and a Restart master binds its Boot
        // in one -- so a load after this line would pin it to its default and no-op it silently.
        Settings.Load(log);
        KnobGroup.Log = log;   // ADR 0025: a knob binds from the dictionary Load just filled, on first touch
        var hooks = new HookService(log);   // after Load: it reads hookdebug

        // OrderBy(Name): Assembly.GetTypes() order is not stable across builds/runtimes,
        // so sort for deterministic init order. Replace with an explicit list if a real
        // init-time dependency ever appears.
        var all = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IExtenderService).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name)
            .Select(t => (IExtenderService)Activator.CreateInstance(t)!)
            .ToList();

        // nothing is filtered here any more. Alloy was the last Linux-only namespace and it came off
        // with the stand-down lift; its Windows refusal, if any, is EngineLayout.Require inside AlloyGate.
        log.Info($"host: {all.Count} service(s) discovered");
        // Shared engine accessors, before any service can reach for one. AurTex is the only real init-time
        // dependency in this host so far -- four services call it and none of them owns it.
        AurTex.Attach(log);
        AurTex.SelfCheck(log);
        Native.SelfCheck(log);   // the std::string readback behind the outbound codepage transcode
        CommandRegistry.Global.Log = log;
        CommandRegistry.SelfCheck(log);   // registration/dispatch/enumeration proof, off-device
        ConfigFile.SelfCheck(log);   // the shared line walker, proven at the host
        ExtenderLog.SelfCheck(log);   // the disabled log evaluates nothing, the enabled one reaches the file
        CX.Graphics.BindlessLedger.SelfCheck(log);   // each AMD-strict bindless rule fires on its breach, not on the legal sequence
        CX.Graphics.VulkanToolchain.SelfCheck(log);   // the staged shaderc and the machine's loader, off-device only
        CX.Graphics.Rhi.VkBackend.SelfCheck(log);   // the pilot's stages compile for Vulkan with GL's bindings shifted apart
        CX.Graphics.Rhi.VkGlsl.SelfCheck(log);   // the GL-to-Vulkan GLSL rules on fixtures, and the std140 reflection
        CX.Alloy.VkPrograms.SelfCheck(log);   // the batch pass's programs translate and compile for Vulkan
        CX.Graphics.ProgramBinary.SelfCheck(log);   // the program-binary cache key: every stage's text, its type, the driver
        CX.Rhi.RhiSelfCheck.Run(log);   // handles, the command arena's round trip and growth, format traits
        CX.Alloy.VkParity.SelfCheck(log);   // the Vulkan A/B's comparator, against hand-worked differences
        CX.Alloy.VkStoodDown.SelfCheck(log);   // every term Vulkan stands down has a row saying what is not drawn
        CX.Alloy.AlloyRenderer.VkDecalSelfCheck(log);   // the Vulkan decal layer's targets, blends and clear against GL's
        CX.Alloy.AlloyRenderer.VkIdSelfCheck(log);   // the Vulkan prepass's vis-ID slot against the stages that write it
        CX.Alloy.VkTextures.SelfCheck(log);   // the GL tokens an owned texture is stored in, as the formats and sampler Vulkan mirrors
        CX.Graphics.Rhi.VkUniformShim.SelfCheck(log);   // GL's uniform setters land in a Vulkan block at the members' offsets
        CX.Graphics.Rhi.GlFormats.SelfCheck(log);   // the seam's formats against the GL headers' token values
        Settings.SelfCheck(log);   // the prefixed read leaves no probe behind
        KnobSurface.Register();
        EngineLayout.SelfCheck(log);   // the three resolution forms, the counter, and a probe that fires
        EngineLayout.Register();
        SymbolMap.Register();
        ClientBuilds.SelfCheck(log);   // every build file names real rows and symbols, and covers every RVA
        FrameRegistry.SelfCheck(log);   // walk order, post-seal refusal, and the zero-alloc AC
        CX.Ui.AtlasSheet.SelfCheck(log);   // pack-and-grow, ceiling refusal, face lookup -- no GL, no stbtt
        CX.Ui.TextStyle.SelfCheck(log);   // per-element text rows + the batch's halos-under-bodies run
        CX.Ui.LoadScreen.SelfCheck(log);   // the two load sources as one monotonic bar
        CX.Alloy.GuiContent.SelfCheck(log);   // the engine's content-stage prologue, m_texture, the pair key
        foreach (IExtenderService service in all)
        {
            try { service.Initialize(log, hooks); }
            catch (Exception e) { log.Info($"host: {service.GetType().Name} init threw: {e.Message}"); }
        }

        // ADR 0025: after every Initialize, so the walk covers every group declared this session -- a User
        // row without help, a layout knob that is not a dropdown, a Restart knob whose Boot moved, all fail here.
        KnobGroup.SelfCheck(log);
        TextList.SelfCheck(log);
        KnobFile.SelfCheck(log);

        // the frame walk must be immutable before the present hook can fire. Sealing here is the
        // happens-before -- a registration after this point is refused, not silently dropped.
        FrameRegistry.Global.Seal();
        foreach (FramePhase p in new[] { FramePhase.Start, FramePhase.PrePresent, FramePhase.PostPresent })
            log.Info($"host: frame {p} -> {string.Join(", ", FrameRegistry.Global.Names(p))}");
        // a skip is not a pass: a suite that stood down has to say so where the boot lines are read
        if (Checker.Skipped > 0) log.Always($"host: {Checker.Skipped} self-check suite(s) SKIPPED");
        hooks.Commit();
        ExtenderLog.Flush();   // boot lines on disk before the host returns: the demo exits right after
    }
}
