using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The one installer of the Aurora seam: the game layer's <c>NewCAurObject</c> / <c>NewCAurScene</c> /
/// <c>NewCAurCamera</c> / <c>NewCAurString</c> answer with CX facades, and no engine <c>Scene</c>, <c>Gob</c>
/// or <c>Part</c> is created for the session (ADR 0028). Installed whenever Alloy is (<c>alloy.enabled</c>,
/// read once at boot); a seam that cannot install stands Alloy down (<see cref="AlloyGate"/>).</summary>
public sealed unsafe class SeamService : IExtenderService
{
    /// <summary>True for the session when the seam is installed; never changes after <see cref="Initialize"/>.
    /// False with Alloy requested is a stand-down (<see cref="AlloyGate"/>).</summary>
    public static bool Db { get; private set; }

    private static ExtenderLog? _log;

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly is rooted; nothing trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Assembly is rooted; parameterless ctors preserved.")]
    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        Facade.Log = log;
        if (!Renderer.On)
        {
            log.Info("seam: alloy.enabled=0 -- vanilla, no seam installed");
            return;
        }
        if (!FacadeSelfCheck.Run(log))
        {
            log.Always("seam: the facade self-check FAILED -- the seam will not install and Alloy stands down. The shell layout, "
                     + "the per-OS slot order or the quarantine is wrong on this build, and a seam built on a "
                     + "wrong layout corrupts the game's memory rather than refusing.");
            return;
        }

        // The object side the scene seam calls into (Db/ObjectBridge.cs). Filled BEFORE any provider
        // registers, so a scene destroy can always destroy its members.
        ObjectBridge.ObjectDestroy = ObjectSlots.DestroyRecord;
        ObjectBridge.ObjectSceneDestroyed = ObjectSlots.LeaveDeadScene;
        ObjectBridge.ObjectName = static o => { nint sh = Facade.ObjectShell(o); return sh == 0 ? "" : Facade.NameOf(sh); };
        // Gob::GetAdjustedScale @0x437cf0: the CalculateFinalScale product times the visual transform's
        // ScaleX lerp. The scene seam folds it per gob the way Gob::Render's attachment walk did.
        ObjectBridge.ObjectVisualAdjust = static (DbId o, ref Vector3 p, ref Quaternion q, ref float s, out bool lerping) =>
        {
            lerping = false;
            nint sh = Facade.ObjectShell(o);
            if (sh != 0) VisualXform.Adjust(sh, ref p, ref q, ref s, out lerping);
        };
        ObjectBridge.ObjectVisualScale = static o => { nint sh = Facade.ObjectShell(o); return sh == 0 ? 1f : VisualXform.Scale(sh); };
        ObjectBridge.ObjectAdjustedScale = static o =>
        {
            nint sh = Facade.ObjectShell(o);
            return sh == 0 ? 1f : new Gob(sh).ScaleBase * VisualXform.Scale(sh);
        };

        SeamBridge.ObjectBounds = ObjectSlots.PartRangeBounds;
        TexSeam.Wire(log);   // CX-117: the six texture / material-parameter entries
        SeamBridge.HasSkinnedMesh = static m =>
        {
            foreach (CX.Alloy.Mdl.NodeDef n in m.Nodes) if (n.Skin != null) return true;
            return false;
        };
        // MaxLightRadius is Gob::GetMaximumLightRadius's CollectLights + max; SetPartLightColour is
        // ControlPartLightColor, which installs a controller rather than poking a field.
        SeamBridge.MaxLightRadius = DbLightSet.MaxRadius;
        SeamBridge.SetPartLightColour = DbLightSet.SetPartColour;
        SeamBridge.PartArea = static p =>
        {
            System.Numerics.Vector3 d = SceneDb.PartBMax[p] - SceneDb.PartBMin[p];
            return 2f * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
        };

        SceneProvider.Log = log;
        SceneProvider.Hooks = hooks;

        var reg = new SeamRegistry();
        List<ISeamProvider> providers = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(ISeamProvider).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (ISeamProvider)Activator.CreateInstance(t)!)
            .ToList();
        foreach (ISeamProvider p in providers)
        {
            reg.Owner = p.GetType().Name;
            try { p.Register(reg); }
            catch (Exception e) { reg.Refusals.Add($"{reg.Owner}: Register threw: {e.Message}"); }
        }
        log.Info($"seam: {providers.Count} provider(s): {string.Join(", ", providers.Select(p => p.GetType().Name))}");

        // The seam is ALL OR NOTHING. The game hands an object it got from NewCAurObject straight to the
        // scene it got from NewCAurScene, so if CX answers one factory and the engine answers the other, the
        // engine dereferences a CX shell as a Gob -- measured: `CGuiMan::Initialize` faults at boot. A mode
        // that is half installed is not a mode, so it stands down with the missing factories named.
        var absent = new List<string>();
        foreach (string sym in new[] { SymbolMap.NewCAurObject, SymbolMap.NewCAurString })
        {
            bool claimed = false;
            foreach (SeamRegistry.Replacement rep in reg.Replacements) claimed |= rep.Symbol == sym;
            if (!claimed) absent.Add(SymbolName(sym));
        }
        if (!SceneProvider.SceneHooksInstalled) absent.Add("NewCAurScene/NewCAurCamera (SceneFns)");
        if (absent.Count > 0)
        {
            log.Always($"seam: no stream answers {string.Join(", ", absent)} -- STANDING DOWN, and Alloy with it: "
                     + "the engine renders. The object and scene factories have to be installed together: the game "
                     + "hands a CX object to whatever scene it was given, and an engine scene would dereference it.");
            foreach (string refusal in reg.Refusals) log.Always("seam: " + refusal);
            return;
        }

        int missing = Facade.Build(reg, log);
        if (missing < 0) return;
        foreach (Action a in reg.AfterBuild)
        {
            try { a(); }
            catch (Exception e) { log.Always($"seam: an AfterTablesBuilt action threw: {e.Message}"); }
        }

        int installed = 0;
        unsafe
        {
            foreach (SeamRegistry.Replacement r in reg.Replacements)
            {
                if (r.Priority == int.MinValue)
                {
                    if (hooks.Install(r.Symbol, r.Handler) != IntPtr.Zero) installed++;
                    else reg.Refusals.Add($"{r.Owner}: {r.Symbol} did not resolve on this client");
                }
                else if (hooks.Chain(r.Symbol, r.Handler, r.Priority) != null) installed++;
                else reg.Refusals.Add($"{r.Owner}: {r.Symbol} did not resolve on this client");
            }
        }
        foreach (string refusal in reg.Refusals) log.Always("seam: " + refusal);
        log.Info($"seam: {installed} of {reg.Replacements.Count} engine function(s) replaced, "
               + $"{reg.Refusals.Count} refusal(s), {missing} unimplemented slot(s)");
        FrameRegistry.Global.Register(FramePhase.PostPresent, 40, Frame, "seam.quarantine", log);
        _log = log;
        FrameRegistry.Global.Register(FramePhase.PostPresent, 41, Period, "seam.period", log);
        Db = true;
    }

    /// <summary>The engine factory a symbol is, for the stand-down line: the mangled name is not something a
    /// log should make a reader demangle.</summary>
    private static string SymbolName(string symbol)
        => symbol == SymbolMap.NewCAurObject ? "NewCAurObject"
         : symbol == SymbolMap.NewCAurString ? "NewCAurString"
         : symbol == SymbolMap.NewCAurScene ? "NewCAurScene"
         : symbol == SymbolMap.NewCAurCamera ? "NewCAurCamera"
         : symbol;

    private static int _periodFrames;
    private const int PeriodFrames = 600;

    /// <summary>The seam's census, once every <see cref="PeriodFrames"/> frames: the DB tables' live counts
    /// beside the shells', then whatever the seam has REFUSED. A record count that only grows is a lifetime
    /// bug, and this is where it shows.</summary>
    private static void Period()
    {
        if (++_periodFrames < PeriodFrames) return;
        _periodFrames = 0;
        _log?.Info($"[seam] objects={SceneDb.Objects.Live} parts={SceneDb.Parts.Live} scenes={SceneDb.Scenes.Live} "
                 + $"cameras={SceneDb.Cameras.Live} | {Facade.Stats()} | parts-shells={PartShell.Alive} "
                 + $"(quarantined {PartShell.Quarantined}, gui-held {PartShell.Held}) | callbacks={CallbackList.Alive}"
                 + $" | {ObjectFns.Stats()} | {ObjectSlots.TreeStats()} | tile-gobs={SceneSlots.TileShells}{Refused()}");
    }

    /// <summary>What the seam turned away, named only when there is something: a call the game made that CX
    /// answered with the engine's refusal rather than a plausible value. Cold -- once per period line.</summary>
    private static string Refused()
    {
        var sb = new System.Text.StringBuilder();
        void Add(long n, string what)
        {
            if (n > 0) sb.Append(sb.Length == 0 ? " | REFUSED: " : ", ").Append(n).Append(' ').Append(what);
        }
        Add(Facade.UnimplementedCalls, "call(s) on a slot no stream implements");
        Add(SeamBridge.Refused, "call(s) to an unwired subsystem");
        Add(SceneSlots.NullScene, "scene slot(s) on a dead handle");
        Add(CameraSlots.NullCamera, "camera slot(s) on a dead handle");
        Add(PartShell.AfterDeath, "Part::GetWorld on a retired shell");
        Add(ObjectSlots.DegenerateBounds, "object(s) whose measured box the game cannot divide by");
        Add(StringSlots.UnresolvedAttach, "label attach(es) naming a part the target has not got");
        return sb.ToString();
    }

    /// <summary>Advance the shell quarantine's clock. Registered on the frame walk, so it ticks on every
    /// published frame: without it <c>FrameNo</c> stays 0, no retired shell is ever reused or freed, and the
    /// two-frame quarantine is a leak with a nice name.</summary>
    public static void Frame() => SeamShell.Frame();

}
