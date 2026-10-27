using System;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Loading;

/// <summary>The engine half of the one loading screen -- the load screen's bar, shared between the engine's load
/// and the lightmap bake hold (<see cref="LoadScreen"/>). Everything else about the screen is the engine's own panel,
/// drawn by Alloy's GUI path. Alloy mode only: in vanilla nothing is hooked.</summary>
public sealed unsafe class LoadBarService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!Renderer.On) return;
        LoadScreen.Orig = hooks.InstallStatusBarSetStatus(&OnSetStatus);
        if (LoadScreen.Orig == null)
            log.Always("LoadBar: CGuiStatusBar::SetStatus did not hook -- the load bar is the engine's alone and the bake hold shows no progress on it");
    }

    /// <summary>Every status bar's fill. Cold: a handful of calls per load, and one compare for any other bar.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetStatus(IntPtr bar, float percent)
        => LoadScreen.Orig(bar, LoadScreen.EngineStatus(bar, percent, LightmapBake.HoldHooked));
}
