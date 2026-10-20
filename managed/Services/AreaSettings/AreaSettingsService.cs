using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Services.AreaSettings;

/// <summary>The area's <c>cx.*</c> local variables hold their knobs while the player is in it
/// (<see cref="Framework.AreaSettings"/>). Read from the <c>.git</c> the engine's resource chain holds, which is the
/// loaded module's in a local game; a multiplayer client has none, and the server sends the same holds itself.</summary>
public sealed unsafe class AreaSettingsService : IExtenderService
{
    private static AreaSettingsState _s = new();

    /// <summary>Low in the LoadArea chain: the holds need the resref the original reads, so they follow it.</summary>
    private const int ChainPriority = 10;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        Framework.AreaSettings.SelfCheck(log);
        if (!EngineLayout.Require(log, "AreaSettings", "CNWCArea") || !SymbolMap.Require(log, "AreaSettings", SymbolMap.AreaLoadArea)) return;
        _s.Load = hooks.ChainAreaLoadArea(&OnLoadArea, ChainPriority);
        if (_s.Load == null) log.Always("AreaSettings: the LoadArea chain was refused -- areas' cx.* variables hold nothing");
    }

    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr areaPtr, IntPtr camera, IntPtr loadscreen)
    {
        int ok = (*_s.Load)(areaPtr, camera, loadscreen);
        if (ok != 0 && areaPtr != IntPtr.Zero) Enter(new CNWCArea(areaPtr));
        return ok;
    }

    /// <summary>Cold, once an area: release what the last area held and the new one does not, then hold the new one's.</summary>
    private static void Enter(CNWCArea area)
    {
        AreaSettingsState s = _s;
        string resref = Native.FixedText(area.Resref).ToLowerInvariant();

        var holds = new List<(string Key, string Value)>();
        string? why = "the area sent no resref";
        byte[]? bytes = resref.Length == 0 ? null : TexRegistry.ReadResourceBytes(resref, ResTypes.Git, out why);
        string readWhy = why ?? "not in the resource chain";
        if (bytes != null)
        {
            if (Gff.TryParse(bytes, out why) is Gff git)
                try { holds = Framework.AreaSettings.Holds(Framework.AreaSettings.Read(git), KnobGroup.Find); }
                catch (FormatException e) { s.Log?.Always($"AreaSettings: {resref}.git is malformed ({e.Message}) -- it holds nothing"); }
            else s.Log?.Always($"AreaSettings: {resref}.git is no GFF ({why}) -- it holds nothing");
        }

        foreach (string key in s.Held)
            if (!holds.Exists(h => h.Key == key)) ServerPolicy.Set(key, "");
        s.Held.Clear();
        var said = new StringBuilder();
        foreach (var (key, value) in holds)
            if (ServerPolicy.Set(key, value)) { s.Held.Add(key); said.Append(' ').Append(key).Append('=').Append(value); }
        if (holds.Count > 0 || bytes == null)
            s.Log?.Info(bytes == null
                ? $"AreaSettings: '{resref}' has no .git this client can read ({readWhy}; a multiplayer client never has one) -- nothing held"
                : $"AreaSettings: '{resref}' holds {s.Held.Count}/{holds.Count} knob(s):{said}");
    }
}

/// <summary>The lifetime owner: the chain slot and the keys the current area holds.</summary>
internal sealed unsafe class AreaSettingsState
{
    internal ExtenderLog? Log;
    internal AreaLoadAreaFn* Load;
    internal readonly List<string> Held = new();
}
