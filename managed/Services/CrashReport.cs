using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services;

/// <summary>Marks the engine's crash report as one from a client running the extender and sends it to our tracker:
/// native/cxthunk.c stamps the report file's top and the crash dialog (<see cref="SymbolMap.ReportCrashToUser"/>),
/// and <see cref="Expose"/> files a line under the report's props. <c>/crashplease</c> tests it. Not a toggle: an unmarked report misroutes.</summary>
public sealed unsafe class CrashReport : IExtenderService
{
    /// <summary>What the dialog and the report file lead with.</summary>
    internal static string Banner =>
        $"NWN:EE crashed while {Product.Name} {Product.Version} was loaded.\n"
        + Update.UpdateNotice.CrashLine(Product.Version, Settings.Raw(Update.UpdateKnobs.FoundKey))
        + $"{Product.Name} is an unofficial, third-party client extender. It is not made, supported or endorsed by Beamdog, "
        + "and Beamdog is not responsible for crashes that happen while it is installed.\n\n"
        + $"Please report this crash to {Product.Name}, not to Beamdog:\n  {Product.Issues}\n\n"
        + $"If the crash still happens after you uninstall {Product.Name}, report it to Beamdog at https://nwn.beamdog.net/crash/.";

    private static bool _exposed;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        fixed (byte* b = Encoding.UTF8.GetBytes(Banner + "\0"))
        {
            var orig = hooks.InstallReportCrashToUser((ReportCrashToUserFn)cx_report_thunk(b));
            cx_report_set_orig(orig);
            log.Info(orig != null ? "CrashReport: crash reports name NWNCX:EE and point at its tracker"
                                  : "CrashReport: ReportCrashToUser did not hook -- a crash report will send users to Beamdog");
        }
        CommandRegistry.Global.Register("/crashplease", "crash the game on purpose, to test the crash report", _ =>
        {
            log.Always("CrashReport: /crashplease -- crashing on purpose");
            cx_crash();
            return null;
        });
    }

    /// <summary>Files the props line. At the first frame, not at boot: the engine's map is a static whose
    /// constructor has not run while the preload's constructor boots us.</summary>
    internal static void Expose()
    {
        if (_exposed) return;
        _exposed = true;
        if (EngineFn.CrashHandlerExpose == null) return;
        fixed (byte* k = "NWNCX:EE\0"u8)
        fixed (byte* v = Encoding.UTF8.GetBytes($"{Product.Version} loaded; report to {Product.Issues}\0"))
            EngineFn.CrashHandlerExpose(k, v, false);
    }

    [DllImport("cxthunk")] private static extern void cx_crash();
    [DllImport("cxthunk")] private static extern IntPtr cx_report_thunk(byte* banner);
    [DllImport("cxthunk")] private static extern void cx_report_set_orig(ReportCrashToUserFn orig);
}
