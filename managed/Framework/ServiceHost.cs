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
        EngineLink.Wire(Native.UserDirectory, Native.AliasPath, static () => Native.Sym(SymbolMap.AppManager) != IntPtr.Zero);
        var log = new ExtenderLog();
        // Which build produced the lines below. build.sh already stamps this from `git describe`, and a log
        // that does not say it is a log you cannot act on: half a debugging round trip is spent establishing
        // whether the run even contains the fix being discussed.
        log.Info(CX.Services.Update.UpdateNotice.BuildLine(Product.Version, Settings.Peek(CX.Services.Update.UpdateKnobs.FoundKey)));
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
            ExtenderLog.FlushNow();   // Always only enqueues, and nothing else in this process will drain it
            return;
        }

        // The boot read, and this is as early as it goes: ahead of DISCOVERY, not just ahead of
        // Initialize. Constructing a service runs its type initializer, and a Restart master binds its Boot
        // in one -- so a load after this line would pin it to its default and no-op it silently.
        Settings.Load(log);
        KnobGroup.Log = log;   // ADR 0025: a knob binds from the dictionary Load just filled, on first touch
        var hooks = new HookService(log);   // after Load: it reads hookdebug

        // Constructed by name (GetTypes() order is unstable, and construction declares knobs), initialized by InitOrder.
        var all = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IExtenderService).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name)
            .Select(t => (IExtenderService)Activator.CreateInstance(t)!)
            .OrderBy(s => s.InitOrder)
            .ToList();

        // nothing is filtered here any more. Alloy was the last Linux-only namespace and it came off
        // with the stand-down lift; its Windows refusal, if any, is EngineLayout.Require inside AlloyGate.
        log.Info($"host: {all.Count} service(s) discovered");
        log.Info($"host: init order: {string.Join(", ", all.Select(s => s.GetType().Name))}");
        CheckInitOrder(all, log);
        // Shared engine accessors, before any service can reach for one. AurTex is the only real init-time
        // dependency in this host so far -- four services call it and none of them owns it.
        AurTex.Attach(log);
        Native.Attach(log);
        CommandRegistry.Global.Log = log;
        CX.Graphics.Rhi.LooseBlock.Attach(log);
        bool session = Native.Sym(SymbolMap.AppManager) != IntPtr.Zero;
        SelfCheckUnits.RunAll(SelfCheckUnits.Discover(SuitePhase.BeforeServices), log, session);
        KnobSurface.Register();
        EngineLayout.Register();
        SymbolMap.Register();
        List<IExtenderService> admitted = all.Where(s => SelfCheckUnits.Admits(s.GetType().Name)).ToList();
        log.Info($"host: initializing {admitted.Count} service(s)");
        foreach (IExtenderService service in admitted)
        {
            SelfCheckUnits.Run(service.GetType().Name, () =>
            {
                try { service.Initialize(log, hooks); }
                catch (Exception e) { log.Info($"host: {service.GetType().Name} init threw: {e}"); }
            });
        }
        CX.Ui.TextRig.WarmStyles();   // after every Initialize: the styles are declared in the services' knob groups

        // ADR 0025: after every Initialize, so the walk covers every group declared this session -- a User
        // row without help, a layout knob that is not a dropdown, a Restart knob whose Boot moved, all fail here.
        SelfCheckUnits.RunAll(SelfCheckUnits.Discover(SuitePhase.AfterServices), log, session);
        CX.Alloy.Db.DbFeed.SessionBegins();
        CX.Alloy.Db.DbSceneSelfCheck.NoFixtureOutlived(log);   // the game has made no scene yet, so one still live is a suite's

        // the frame walk must be immutable before the present hook can fire. Sealing here is the
        // happens-before -- a registration after this point is refused, not silently dropped.
        FrameRegistry.Global.Seal();
        foreach (FramePhase p in new[] { FramePhase.Start, FramePhase.PrePresent, FramePhase.PostPresent })
            log.Info($"host: frame {p} -> {string.Join(", ", FrameRegistry.Global.Names(p))}");
        // a skip is not a pass: a suite that stood down has to say so where the boot lines are read
        if (Checker.Skipped > 0) log.Always($"host: {Checker.Skipped} self-check suite(s) SKIPPED");
        hooks.Commit();
        // Boot is over here, so a set from now on is a player's: every group has declared and every self-check
        // has put back what it moved.
        KnobFile.BootEnded(log);
        log.Info($"KnobFile: the debounce runs on {KnobFile.ClockName}");
        log.Info($"TexRegistry: the upload retry runs on {CX.Alloy.Db.TexRegistry.RetryClockName}");
        ExtenderLog.FlushNow();   // boot lines on disk before the host returns: the demo exits right after
    }

    /// <summary>Each service initializes after the one before it by InitOrder, then by name, and the seam goes first.
    /// Oracle: the rule checked pair by pair, apart from the sorts that made the list.</summary>
    private static void CheckInitOrder(List<IExtenderService> all, ExtenderLog log)
    {
        var check = new Checker(log);
        for (int i = 1; i < all.Count; i++)
        {
            IExtenderService a = all[i - 1], b = all[i];
            int byName = Comparer<string>.Default.Compare(a.GetType().Name, b.GetType().Name);
            check.Expect(a.InitOrder < b.InitOrder || (a.InitOrder == b.InitOrder && byName < 0),
                $"init order: {b.GetType().Name} ({b.InitOrder}) comes after {a.GetType().Name} ({a.InitOrder})");
        }
        check.Expect(all.Count > 0 && all[0] is CX.Alloy.Seam.SeamService, "init order: the seam does not initialize first");
    }
}
