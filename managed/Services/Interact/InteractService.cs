using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Interact;

/// <summary>Interact's engine side: swallows a bound key at the SDL pump, so the game's own binding never sees it,
/// and installs <see cref="InteractEngineWorld"/> for presses to read. A thumb button arrives through the chord
/// layer instead (<see cref="InteractInput.Chord"/>).</summary>
public sealed unsafe class InteractService : IExtenderService
{
    /// <summary>The hook's link to the layer below, set once at install.</summary>
    private sealed class HookState { public WinMessageHandlerFn* Next; }

    private static readonly HookState S = new();

    /// <summary>Above tab-target (50), so a key bound to both runs Interact; below mouse-look (100).</summary>
    private const int ChainPriority = 60;
    private const uint SdlKeyDown = 0x300;  // not-an-offset: SDL key/event code

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "Interact", "CNWCObject", "CNWCArea", "CNWCCreature", "CNWCPlaceable", "CNWCDoor",
                                  "CNWCTrigger", "CNWCItem", "CGameObject", "CClientExoAppInternal", "CNWCModule", "CClientExoApp")) return;
        if (!SymbolMap.Require(log, "Interact", SymbolMap.WinMessageHandler, SymbolMap.PerformDefaultActionOnObject,
                               SymbolMap.ClearLineOfSightOneWay, SymbolMap.GetGameObject, SymbolMap.GetModule,
                               SymbolMap.AppManager, SymbolMap.WalkPlayerToPoint)) return;
        S.Next = hooks.ChainWinMessageHandler(&OnWinMessage, ChainPriority);
        if (S.Next == null) { log.Info("Interact: standing down (the WinMessageHandler chain was refused)"); return; }
        InteractInput.Live(new InteractEngineWorld());
        log.Info("Interact: ready; chained on WinMessageHandler");
    }

    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        InteractInput.Resume();
        if (ev != IntPtr.Zero && *(uint*)ev == SdlKeyDown
            && InteractInput.KeyDown(((SdlEvent*)ev)->Sym, ((SdlEvent*)ev)->Repeat != 0)) return 0;
        return (*S.Next)(ev);
    }
}

/// <summary>The live area as Interact reads it at a press: the objects of the player's current area, the walkmesh
/// sight test and the engine's own default action. Every read is cold, on the input thread.</summary>
internal sealed unsafe class InteractEngineWorld : IInteractWorld
{
    private const byte TypeItem = 6, TypeTrigger = 7;  // not-an-offset: object type bytes, the trigger's from the CNWCTrigger ctor
    /// <summary>The most points a trigger outline holds: the trigger add reads the count from one byte.</summary>
    private const int MaxOutline = 255;
    /// <summary>The height both ends of a sight line are raised by, as the server's
    /// <c>CNWSArea::CountVisibleToPlayers</c> raises its own.</summary>
    private const float EyeHeight = 1f;
    private const byte AttitudeNeutral = 0, AttitudeFriend = 1;
    /// <summary>WalkPlayerToPoint's flags 0: walk or run by the always-run setting and Shift, as a left click does.</summary>
    private const int WalkByRunSetting = 0;
    /// <summary>WalkPlayerToPoint's last argument: show the walk-to ring at the point (ShowWalkToRingVFX).</summary>
    private const int ShowRing = 1;

    private readonly List<Thing> _things = new(256);

    public Vector3 Player
    {
        get
        {
            IntPtr me = CameraService.PlayerCreature();
            return me == IntPtr.Zero ? default : new CNWCObject(me).Position;
        }
    }

    public IReadOnlyList<Thing> Things { get { Refresh(); return _things; } }

    public uint Crosshair => TypingSignal.ObjectInvalid;

    public uint Lock => TabTargetService.Lock is var id && id != 0 ? id : TypingSignal.ObjectInvalid;

    public bool KeysFree => GameGate.InGame && Area() != IntPtr.Zero && CameraService.PlayerCreature() != IntPtr.Zero
                            && UiInputService.ChordGateClear();

    public bool Sees(Vector3 from, Vector3 to)
    {
        IntPtr area = Area();
        if (area == IntPtr.Zero) return false;
        Vec3 a = new() { X = from.X, Y = from.Y, Z = from.Z + EyeHeight };
        Vec3 b = new() { X = to.X, Y = to.Y, Z = to.Z + EyeHeight };
        Vec3 hit = default;
        return new CNWCArea(area).ClearLineOfSightOneWay(VectorArg.Of(&a), VectorArg.Of(&b), &hit) == 1;
    }

    public bool SoundExists(string resref) => false;

    public string? GameKeyAction(int code) => null;

    public void DefaultAction(uint id)
    {
        IntPtr app = CameraService.App();
        if (app == IntPtr.Zero) return;
        var exo = new CClientExoApp(app);
        CGameObject go = exo.GetGameObject(id);
        if (go.IsNull) return;
        CNWCObject obj = go.AsNWCObject();
        if (!obj.IsNull) exo.Internal.PerformDefaultActionOnObject(obj.Ptr);
    }

    public void WalkTo(uint id, Vector3 point)
    {
        IntPtr app = CameraService.App();
        if (app != IntPtr.Zero) new CClientExoApp(app).Internal.WalkPlayerToPoint(point.X, point.Y, point.Z, WalkByRunSetting, id, ShowRing);
    }

    public void ShowLine(string text) { }

    public void PlaySound(string resref) { }

    private static IntPtr Area()
    {
        IntPtr module = CameraService.Module();
        return module == IntPtr.Zero ? IntPtr.Zero : new CNWCModule(module).CurrentArea.Ptr;
    }

    /// <summary>Reads the current area's objects into <see cref="_things"/>: what is on screen and every trigger, which
    /// is never drawn, the player left out.</summary>
    private void Refresh()
    {
        _things.Clear();
        IntPtr app = CameraService.App(), area = Area();
        if (app == IntPtr.Zero || area == IntPtr.Zero) return;
        var exo = new CClientExoApp(app);
        uint playerId = exo.Internal.PlayerCreatureId;
        var areaView = new CNWCArea(area);
        int n = areaView.ObjectCount;
        uint* ids = (uint*)areaView.ObjectIds;
        if (n <= 0 || ids == null) return;
        for (int i = 0; i < n; i++)
        {
            uint id = ids[i];
            if (id == playerId || id == TypingSignal.ObjectInvalid) continue;
            CGameObject go = exo.GetGameObject(id);
            if (go.IsNull) continue;
            CNWCObject obj = go.AsNWCObject();
            if (obj.IsNull || (obj.Visible == 0 && obj.Type != TypeTrigger)) continue;
            if (Read(go, obj, id) is { } t) _things.Add(t);
        }
    }

    /// <summary>A trigger that leads to another area, with its outline; null for a trap or any other trigger.</summary>
    private static Thing? ReadTrigger(CGameObject go, Thing t)
    {
        IntPtr cast = go.AsNWCTrigger();
        if (cast == IntPtr.Zero) return null;
        var g = new CNWCTrigger(cast);
        int n = g.VertexCount;
        Vec3* v = (Vec3*)g.Vertices;
        if (g.IsTrap != 0 || g.AreaTransition == 0 || v == null || n < 3 || n > MaxOutline) return null;
        var outline = new Vector3[n];
        for (int i = 0; i < n; i++) outline[i] = new Vector3(v[i].X, v[i].Y, v[i].Z);
        t.Kind = ThingKind.Transition;
        t.LeadsAway = true;
        t.Outline = outline;
        return t;
    }

    private static Thing? Read(CGameObject go, CNWCObject obj, uint id)
    {
        var t = new Thing { Id = id, Pos = obj.Position };
        switch (obj.Type)
        {
            case TabTargetCore.TypeCreature:
            {
                var c = new CNWCCreature(obj.Ptr);
                t.Kind = ThingKind.Creature;
                t.Dead = c.AiFlags == 0;
                t.Lootable = c.Lootable == 1;
                t.Attitude = c.Attitude switch { AttitudeFriend => Attitude.Friend, AttitudeNeutral => Attitude.Neutral, _ => Attitude.Hostile };
                return t;
            }
            case TypeItem:
            {
                IntPtr cast = go.AsNWCItem();
                if (cast == IntPtr.Zero) return null;
                var item = new CNWCItem(cast);
                if (item.EquippedBy != TypingSignal.ObjectInvalid) return null;
                t.Kind = ThingKind.Item;
                t.Pending = item.Ghost != 0;
                return t;
            }
            case TabTargetCore.TypePlaceable:
            {
                if (obj.Retired == 1) return null;
                CNWCObject cast = go.AsNWCPlaceable();
                if (cast.IsNull) return null;
                var p = new CNWCPlaceable(cast.Ptr);
                t.Kind = p.BodyBag != 0 ? ThingKind.BodyBag : ThingKind.Placeable;
                t.Locked = p.Locked != 0;
                t.TrapFound = p.TrapShown != 0;
                t.Useable = cast.Highlightable != 0;
                t.Lootable = t.Kind == ThingKind.BodyBag && t.Useable;
                return t;
            }
            case TabTargetCore.TypeDoor:
            {
                IntPtr cast = go.AsNWCDoor();
                if (cast == IntPtr.Zero) return null;
                var d = new CNWCDoor(cast);
                t.Kind = ThingKind.Door;
                t.Locked = d.Locked != 0;
                t.TrapFound = d.TrapShown != 0;
                t.Open = d.State == 0;
                t.LeadsAway = d.AreaTransition != 0;
                return t;
            }
            case TypeTrigger:
                return ReadTrigger(go, t);
            default:
                return null;
        }
    }
}
