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
    private sealed class HookState { public WinMessageHandlerFn* Next; public SendAreaLoadedFn* Loaded; }

    private static readonly HookState S = new();

    /// <summary>Above tab-target (50), so a key bound to both runs Interact; below mouse-look (100).</summary>
    private const int ChainPriority = 60;
    private const uint SdlKeyDown = 0x300, SdlKeyUp = 0x301;  // not-an-offset: SDL key/event code
    /// <summary>Above every other layer on the AreaLoaded send (TexStream 100), so Interact sees the engine's own send
    /// even when the lightmap hold keeps it from the layers below.</summary>
    private const int LoadedPriority = 110;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "Interact", "CNWCObject", "CNWCArea", "CNWCCreature", "CNWCPlaceable", "CNWCDoor",
                                  "CNWCTrigger", "CNWCItem", "CGameObject", "CClientExoAppInternal", "CNWCModule", "CClientExoApp",
                                  "CExoInput", "CExoInputInternal", "CExoInputDescLists", "CExoInputEventDesc", "CNWRules", "CTwoDimArrays")) return;
        if (!SymbolMap.Require(log, "Interact", SymbolMap.WinMessageHandler, SymbolMap.PerformDefaultActionOnObject,
                               SymbolMap.ClearLineOfSightOneWay, SymbolMap.GetGameObject, SymbolMap.GetModule,
                               SymbolMap.AppManager, SymbolMap.WalkPlayerToPoint)) return;
        S.Next = hooks.ChainWinMessageHandler(&OnWinMessage, ChainPriority);
        if (S.Next == null) { log.Info("Interact: standing down (the WinMessageHandler chain was refused)"); return; }
        FeedbackLine.Register();
        InteractInput.Use(new InteractEngineWorld());
        S.Loaded = hooks.ChainSendAreaLoaded(&OnAreaLoaded, LoadedPriority);
        log.Info(S.Loaded == null ? "Interact: ready; chained on WinMessageHandler; the AreaLoaded send is unhooked, so a key held through a load acts on arrival"
                                  : "Interact: ready; chained on WinMessageHandler and the AreaLoaded send");
    }

    /// <summary>A bound key's press, taken ahead of WinMessageHandler's body, which on both builds hands a key-down to
    /// CExoInput::AddEvent (the game's key bindings) and then HandleWMKeyDownMessage (the chord layer), Linux nm
    /// @0x42f960, Windows rva 0x45490: returning 0 keeps it from both. Any other key goes on untouched.</summary>
    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev != IntPtr.Zero && *(uint*)ev == SdlKeyDown
            && InteractInput.KeyDown(((SdlEvent*)ev)->Sym, ((SdlEvent*)ev)->Repeat != 0)) return 0;
        if (ev != IntPtr.Zero && *(uint*)ev == SdlKeyUp) InteractInput.KeyUp(((SdlEvent*)ev)->Sym);
        return (*S.Next)(ev);
    }

    [UnmanagedCallersOnly]
    private static void OnAreaLoaded(IntPtr msg)
    {
        InteractInput.AreaLoaded();
        (*S.Loaded)(msg);
    }
}

/// <summary>The live area as Interact reads it at a press: the objects of the player's current area, the walkmesh
/// sight test and the engine's own default action. Every read is cold, on the input thread.</summary>
internal sealed unsafe class InteractEngineWorld : IInteractWorld
{
    /// <summary>The most points a trigger outline holds: the trigger add reads the count from one byte.</summary>
    private const int MaxOutline = 255;
    /// <summary>The height both ends of a sight line are raised by, as the server's
    /// <c>CNWSArea::CountVisibleToPlayers</c> raises its own.</summary>
    private const float EyeHeight = 1f;
    private const byte AttitudeNeutral = 0, AttitudeFriend = 1;
    /// <summary>CExoSound's priority group for interface sounds, the one <c>PlayGuiSound</c> plays in.</summary>
    private const byte GuiGroup = 9;   // not-an-offset: CExoSound priority group id
    /// <summary>CNWCDoor's state byte past 0, the closed rest: open one way or the other, and destroyed, which a generic
    /// door with no visible model also reads.</summary>
    private const byte DoorOpened1 = 1, DoorOpened2 = 2, DoorDestroyed = 3;
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

    /// <summary>The object the engine's hover pick last found, which under the action camera is the crosshair's (or the
    /// aim assist's) point; nothing while the camera is off, when the hover is the free mouse cursor's.</summary>
    public uint Crosshair
    {
        get
        {
            IntPtr me = CameraService.PlayerCreature();
            if (!MouseLookService.ActionHeld || me == IntPtr.Zero) return TypingSignal.ObjectInvalid;
            uint id = new CNWCCreature(me).HoveredOid;
            return id == 0 ? TypingSignal.ObjectInvalid : id;
        }
    }

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

    public bool SoundExists(string resref)
    {
        IntPtr slot = Native.Sym(SymbolMap.ExoResMan);
        IntPtr resMan = slot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)slot;
        if (resMan == IntPtr.Zero || EngineFn.ResManExists == null || resref.Length == 0) return false;
        ResRef name = ResRef.Of(resref, lower: true);
        return EngineFn.ResManExists(resMan, &name, ResTypes.Wav, null) != 0;
    }

    public string? GameKeyAction(int code) => GameKeymap.Action(code);

    public void DefaultAction(uint id)
    {
        IntPtr app = CameraService.App();
        if (app == IntPtr.Zero) return;
        var exo = new CClientExoApp(app);
        CGameObject go = exo.GetGameObject(id);
        if (go.IsNull) return;
        CNWCObject obj = go.AsNWCObject();
        if (!obj.IsNull) TabTargetService.VanillaDefaultAction(exo.Internal.Ptr, obj.Ptr);
    }

    public void WalkTo(uint id, Vector3 point)
    {
        IntPtr app = CameraService.App();
        if (app != IntPtr.Zero) MouseLookService.WalkPastActionCamera(new CClientExoApp(app), point.X, point.Y, point.Z, WalkByRunSetting, id, ShowRing);
    }

    public void ShowLine(string text) => FeedbackLine.Show(text);

    public void PlaySound(string resref)
    {
        IntPtr slot = Native.Sym(SymbolMap.ExoSound);
        IntPtr sound = slot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)slot;
        if (sound == IntPtr.Zero || EngineFn.SoundPlayOneShot == null) return;
        ResRef name = ResRef.Of(resref, lower: true);
        EngineFn.SoundPlayOneShot(sound, &name, GuiGroup, 0, 0);
    }

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
            if (obj.IsNull || (obj.Visible == 0 && obj.Type != TabTargetCore.TypeTrigger)) continue;
            if (Read(go, obj, id, playerId) is { } t) _things.Add(t);
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

    /// <summary>Fills <paramref name="t"/>'s open and destroyed flags from CNWCDoor's state byte, what GetState returns.</summary>
    internal static void ReadDoorState(ref Thing t, byte state)
    {
        t.Open = state is DoorOpened1 or DoorOpened2;
        t.Destroyed = state == DoorDestroyed;
    }

    /// <summary>One area object as a <see cref="Thing"/>, null for one no rule acts on; a creature whose master is
    /// <paramref name="playerId"/> is the player's party.</summary>
    private static Thing? Read(CGameObject go, CNWCObject obj, uint id, uint playerId)
    {
        var t = new Thing { Id = id, Pos = obj.Position };
        switch (obj.Type)
        {
            case TabTargetCore.TypeCreature:
            {
                var c = new CNWCCreature(obj.Ptr);
                t.Kind = ThingKind.Creature;
                t.Dead = TabTargetService.Dead(obj);
                t.Lootable = c.Lootable == 1;
                t.Attitude = c.Attitude switch { AttitudeFriend => Attitude.Friend, AttitudeNeutral => Attitude.Neutral, _ => Attitude.Hostile };
                t.Party = c.MasterId == playerId;
                t.OtherPlayer = PlayerIdentity.IsPlayerCreature(id);
                return t;
            }
            case TabTargetCore.TypeItem:
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
                ReadDoorState(ref t, d.State);
                t.LeadsAway = d.AreaTransition != 0;
                return t;
            }
            case TabTargetCore.TypeTrigger:
                return ReadTrigger(go, t);
            default:
                return null;
        }
    }
}
