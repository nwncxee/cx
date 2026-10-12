using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Seam;

namespace CX.Services.Nui;

/// <summary>
/// A throwaway creature in a free-standing GUI viewport, built the way character creation builds its preview: a
/// <c>CNWCCreature</c> of our own, registered like any object, dressed from a copy of a real creature's appearance
/// record, its model taken into a <c>CGuiViewport</c> named <see cref="ViewName"/>. The real creature is only read.
/// </summary>
internal static unsafe class NuiModelDoll
{
    /// <summary>The scene name the engine lights: <c>RenderOnlyGobs</c> and <c>GuiCapture.IsLitScene</c> both match
    /// <c>sceneGUI_guiModelView</c>, so this view gets the character-preview lighting on either path.</summary>
    internal const string ViewName = "guiModelView";
    /// <summary>The engine's invalid object id, the value an id slot holds before <c>AddInternalObject</c> fills it.</summary>
    private const uint InvalidId = 0x7f000000;   // not-an-offset: INVALID_OBJECT_ID
    /// <summary>Character creation first-build flags for <c>SetAppearance</c>: body (with its parts), head, tail, wing and cloak; 0x40 would build the base body only.</summary>
    private const byte BuildFlags = 0x2f;   // not-an-offset: SetAppearance flag bits
    /// <summary>The leading <c>CCAppearanceInfo</c> of a creature's appearance object, as the appearance update handler
    /// copies it (0x1f quad words and a word). ponytail: Linux-measured; the Windows copy count is unread.</summary>
    private const int InfoBytes = 250;   // not-an-offset: sizeof CCAppearanceInfo

    /// <summary>Everything one doll owns. Replaced whole by <see cref="Destroy"/>.</summary>
    internal sealed class DollState
    {
        public IntPtr Creature, Viewport, Camera, Scene, Model;
        public uint Id = InvalidId;
        public bool Registered, Enabled;
    }

    /// <summary>What this class keeps between calls: the log, the app manager's slot and the one live doll.</summary>
    private sealed class DollOwnerState
    {
        public ExtenderLog? Log;
        public IntPtr AppMgr;
        public int FadeMode = 1;
        public DollState Doll = new();
    }

    private static readonly DollOwnerState _o = new();

    internal static DollState Doll => _o.Doll;
    internal static void SetFadeMode(int m) => _o.FadeMode = m;

    internal static bool Init(ExtenderLog log)
    {
        _o.Log = log;
        if (!SymbolMap.Require(log, "NuiModelDoll", SymbolMap.AppManager, SymbolMap.OperatorNew, SymbolMap.OperatorDelete,
                               SymbolMap.NwcCreatureCtor, SymbolMap.NwcCreatureSetAppearance, SymbolMap.GetGameObject,
                               SymbolMap.GetGameObjectArray, SymbolMap.GetClientAIMaster, SymbolMap.AiMasterAddObject,
                               SymbolMap.GameObjectArrayAdd, SymbolMap.GameObjectArrayDelete, SymbolMap.GuiViewportCtor,
                               SymbolMap.GuiViewportDtor, SymbolMap.GuiViewportEnable, SymbolMap.GuiViewportDisable,
                               SymbolMap.GuiViewportSetViewport, SymbolMap.GuiViewportTakeModel, SymbolMap.GuiViewportGetCamera)
            || !EngineLayout.Require(log, "NuiModelDoll", "CNWCCreature", "CGuiViewport", "CAurCamera", "Gob", "CGameObject"))
            return false;
        _o.AppMgr = SymbolMap.Resolve(SymbolMap.AppManager, log);
        return _o.AppMgr != IntPtr.Zero;
    }

    private static IntPtr App()
    {
        IntPtr mgr = *(IntPtr*)_o.AppMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    /// <summary>The creature behind a client object id, or zero.</summary>
    internal static IntPtr Creature(uint id)
    {
        IntPtr app = App();
        if (app == IntPtr.Zero) return IntPtr.Zero;
        CGameObject go = new CClientExoApp(app).GetGameObject(id);
        return go.IsNull || go.Type != ObjTypeCreature ? IntPtr.Zero : go.AsNWCObject().Ptr;
    }

    /// <summary>The local player's creature, or zero outside a module.</summary>
    internal static IntPtr Player()
    {
        IntPtr app = App();
        if (app == IntPtr.Zero) return IntPtr.Zero;
        var ca = new CClientExoApp(app);
        return ca.Internal.IsNull ? IntPtr.Zero : Creature(ca.Internal.PlayerCreatureId);
    }

    private const byte ObjTypeCreature = 5;   // not-an-offset: CNWCObject type 5, the constructor's argument

    /// <summary>Build the doll as a copy of <paramref name="source"/>, in a viewport covering the screen rectangle.
    /// False = a step failed and the half-built doll was taken down again.</summary>
    internal static bool Build(IntPtr source, int x, int y, int w, int h, bool ai = true)
    {
        if (_o.Doll.Creature != IntPtr.Zero) Destroy();
        IntPtr app = App();
        if (app == IntPtr.Zero || source == IntPtr.Zero) return false;
        IntPtr appearance = new CNWCCreature(source).Pathfind.Ptr;
        if (appearance == IntPtr.Zero) return false;
        byte* info = stackalloc byte[InfoBytes];
        Buffer.MemoryCopy((void*)appearance, info, InfoBytes, InfoBytes);

        var d = new DollState();
        IntPtr mem = EngineFn.OperatorNew((nuint)EngineLayout.SizeOfCreature);
        if (mem == IntPtr.Zero) return false;
        NativeMemory.Clear((void*)mem, (nuint)EngineLayout.SizeOfCreature);
        EngineFn.NwcCreatureCtor(mem);
        d.Creature = mem;
        IntPtr array = EngineFn.GetGameObjectArray(app);
        uint id = InvalidId;
        if (array == IntPtr.Zero || EngineFn.GameObjectArrayAdd(array, &id, mem, 0) != 0)
        {
            _o.Log?.Always("NuiModelDoll: the engine would not register the throwaway creature");
            _o.Doll = d;
            Destroy();
            return false;
        }
        d.Id = id;
        d.Registered = true;
        new CNWCObject(mem).Id = id;
        IntPtr master = EngineFn.GetClientAIMaster(app);
        if (ai && master != IntPtr.Zero) EngineFn.AiMasterAddObject(master, mem);
        if (EngineFn.NwcCreatureSetAppearance(mem, (IntPtr)info, BuildFlags) == 0)
        {
            _o.Log?.Always("NuiModelDoll: SetAppearance failed on the throwaway creature");
            _o.Doll = d;
            Destroy();
            return false;
        }
        d.Model = new CNWCCreature(mem).GetModel(0xff, 1);
        if (_o.FadeMode != 0) EngineFn.ObjSetFadeState(mem, 1);
        if (_o.FadeMode != 0) EngineFn.ObjSetAlpha(mem, 1f, 1);
        if (d.Model != IntPtr.Zero) Opaque(d.Model);
        byte* name = stackalloc byte[ViewName.Length + 1];
        for (int i = 0; i < ViewName.Length; i++) name[i] = (byte)ViewName[i];
        name[ViewName.Length] = 0;
        IntPtr vp = EngineFn.OperatorNew((nuint)EngineLayout.SizeOfGuiViewport);
        NativeMemory.Clear((void*)vp, (nuint)EngineLayout.SizeOfGuiViewport);
        EngineFn.GuiViewportCtor(vp, name);
        d.Viewport = vp;
        IntPtr record = *(IntPtr*)(vp + EngineLayout.GuiViewportRecord);
        d.Scene = record == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)record;
        d.Camera = EngineFn.GuiViewportGetCamera(vp);
        _o.Doll = d;
        if (d.Model == IntPtr.Zero)
        {
            _o.Log?.Always("NuiModelDoll: the throwaway creature has no model");
            Destroy();
            return false;
        }
        EngineFn.GuiViewportTakeModel(vp, d.Model);
        EngineFn.GuiViewportSetViewport(vp, x, y, w, h);
        EngineFn.GuiViewportEnable(vp);
        d.Enabled = true;
        _o.Log?.Always($"NuiModelDoll: built -- creature 0x{mem:x} id 0x{id:x} model 0x{d.Model:x} viewport 0x{vp:x} scene 0x{d.Scene:x} camera 0x{d.Camera:x}");
        return true;
    }

    /// <summary>Take the doll down the way the engine's own creature preview does: leave the view, drop the id, delete the
    /// creature, then the view with its scene and camera.</summary>
    internal static void Destroy()
    {
        DollState d = _o.Doll;
        _o.Doll = new DollState();
        IntPtr app = App();
        if (d.Viewport != IntPtr.Zero && d.Enabled) EngineFn.GuiViewportDisable(d.Viewport);
        if (d.Creature != IntPtr.Zero)
        {
            if (d.Registered && app != IntPtr.Zero)
            {
                IntPtr gone;
                EngineFn.GameObjectArrayDelete(EngineFn.GetGameObjectArray(app), d.Id, &gone);
            }
            IntPtr dtor = new CNWCCreature(d.Creature).DeleteFn;
            if (OperatingSystem.IsWindows()) ((delegate* unmanaged<IntPtr, uint, IntPtr>)dtor)(d.Creature, 1);
            else ((delegate* unmanaged<IntPtr, void>)dtor)(d.Creature);
        }
        if (d.Viewport != IntPtr.Zero)
        {
            EngineFn.GuiViewportDtor(d.Viewport);
            EngineFn.OperatorDelete(d.Viewport);
        }
        _o.Log?.Always("NuiModelDoll: taken down");
    }

    /// <summary>The colour shift the fade machinery leaves a new creature at: alpha 1, colour white, to the attached parts too.</summary>
    internal static void Opaque(IntPtr model)
    {
        IntPtr fn = new Gob(model).Ptr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(*(IntPtr*)model + EngineLayout.GobSetColorShiftingSlot);
        ((delegate* unmanaged<IntPtr, EVec, float, int, void>)fn)(model, new EVec { X = 1, Y = 1, Z = 1 }, 1f, 1);
    }

    /// <summary>Stand the model up at <paramref name="pos"/> facing <paramref name="yawDeg"/> degrees round the screen's vertical:
    /// the creature's up axis is Z, so it is first laid back 90 degrees to stand on the view's Y.</summary>
    internal static void Place(Vector3 pos, float yawDeg)
    {
        IntPtr m = _o.Doll.Model;
        if (m == IntPtr.Zero) return;
        var g = new Gob(m);
        var v = new EVec { X = pos.X, Y = pos.Y, Z = pos.Z };
        var q = EQuat.From(Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
                                                  Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDeg * MathF.PI / 180f)));
        EVec rv;
        EQuat rq;
        if (OperatingSystem.IsWindows())
        {
            ((delegate* unmanaged<IntPtr, EVec*, EVec, IntPtr>)g.SetPositionFn)(m, &rv, v);
            ((delegate* unmanaged<IntPtr, EQuat*, EQuat, IntPtr>)g.SetOrientationFn)(m, &rq, q);
        }
        else
        {
            ((delegate* unmanaged<IntPtr, EVec, EVec>)g.SetPositionFn)(m, v);
            ((delegate* unmanaged<IntPtr, EQuat, EQuat>)g.SetOrientationFn)(m, q);
        }
    }

    /// <summary>The creature's fade bytes and the model's colour-shift alpha, for working out why it is hidden.</summary>
    private const int FA = 0xa8, FB = 0xa9, FC = 0xb0, FD = 0xf4, FE = 0x104, FF = 0x108, FG = 0xf6;   // not-an-offset: throwaway probe of the creature fade bytes
    /// <summary>Per frame: the creature's own AI turns its fade off again (it is in no area), so the doll is shown again
    /// whenever the model's alpha has dropped.</summary>
    internal static void KeepShown()
    {
        DollState d = _o.Doll;
        if (d.Creature == IntPtr.Zero || d.Model == IntPtr.Zero || *(float*)(d.Model + FF) >= 1f) return;
        if (_o.FadeMode != 0) EngineFn.ObjSetFadeState(d.Creature, 1);
        Opaque(d.Model);
    }

    internal static string FadeLine()
    {
        IntPtr c = _o.Doll.Creature, m = _o.Doll.Model;
        if (c == IntPtr.Zero) return "no creature";
        return $"fade state {*(byte*)(c + FA)} desired {*(byte*)(c + FB)} final {*(float*)(c + FC)} f4 {*(int*)(c + FD)} 104 {*(int*)(c + FE)} 108 {*(int*)(c + FF)}"
             + (m == IntPtr.Zero ? "" : $" | model alpha {*(float*)(m + FF)} enable {*(short*)(m + FG)}");
    }

    /// <summary>The camera's state as one log line, for working out where to put the model.</summary>
    internal static string CameraLine()
    {
        IntPtr c = _o.Doll.Camera;
        if (c == IntPtr.Zero) return "no camera";
        var cam = new CAurCamera(c);
        string s = $"angle {cam.GetViewAngle()}";
        if (OperatingSystem.IsWindows()) return s + " (position and orientation unread on Windows)";
        EVec p = ((delegate* unmanaged<IntPtr, EVec>)cam.GetPositionFn)(c);
        EQuat q = ((delegate* unmanaged<IntPtr, EQuat>)cam.GetOrientationFn)(c);
        return s + $" pos ({p.X:F2},{p.Y:F2},{p.Z:F2}) rot (w{q.W:F3},{q.X:F3},{q.Y:F3},{q.Z:F3})";
    }
}
