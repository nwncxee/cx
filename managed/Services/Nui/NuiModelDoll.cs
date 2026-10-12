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
    private const int InfoMax = 256;   // not-an-offset: room for either OS's CCAppearanceInfo
    private static int InfoBytes => EngineLayout.SizeOfAppearanceInfo;

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
        /// <summary>The GL texture the view's layer is drawn into (0 = the screen), its background colour, and whether a
        /// frame has landed in it since the doll was built.</summary>
        public uint TargetTex;
        public int TargetW, TargetH;
        public readonly float[] Bg = new float[4];
        public bool Rendered;
        public DollState Doll = new();
    }

    private static readonly DollOwnerState _o = new();

    internal static DollState Doll => _o.Doll;
    /// <summary>The texture the view's layer draws into, or 0 to draw to the screen (the probe).</summary>
    internal static uint Redirect => _o.TargetTex;
    internal static void SetTarget(uint tex, int w, int h) { _o.TargetTex = tex; _o.TargetW = w; _o.TargetH = h; }
    internal static bool TargetSize(out int w, out int h) { w = _o.TargetW; h = _o.TargetH; return w > 0 && h > 0; }
    /// <summary>The layer was drawn into the target: the doll's first frame has landed.</summary>
    internal static void MarkRendered() => _o.Rendered = true;
    internal static bool Rendered => _o.Rendered;
    internal static void SetBackground(float r, float g, float b, float a) { _o.Bg[0] = r; _o.Bg[1] = g; _o.Bg[2] = b; _o.Bg[3] = a; }
    internal static void Background(float* c) { for (int i = 0; i < 4; i++) c[i] = _o.Bg[i]; }

    internal static bool Init(ExtenderLog log)
    {
        _o.Log = log;
        if (!SymbolMap.Require(log, "NuiModelDoll", SymbolMap.AppManager, SymbolMap.OperatorNew, SymbolMap.OperatorDelete,
                               SymbolMap.NwcCreatureCtor, SymbolMap.NwcCreatureSetAppearance, SymbolMap.GetGameObject,
                               SymbolMap.GetGameObjectArray, SymbolMap.GetClientAIMaster, SymbolMap.AiMasterAddObject,
                               SymbolMap.GameObjectArrayAdd, SymbolMap.GameObjectArrayDelete, SymbolMap.GuiViewportCtor,
                               SymbolMap.GuiViewportDtor, SymbolMap.GuiViewportEnable, SymbolMap.GuiViewportDisable,
                               SymbolMap.GuiViewportSetViewport, SymbolMap.GuiViewportTakeModel, SymbolMap.GuiViewportGetCamera)
            || !EngineLayout.Require(log, "NuiModelDoll", "CNWCCreature", "CGuiViewport", "CCAppearanceInfo", "CAurCamera", "Gob", "CGameObject"))
            return false;
        _o.AppMgr = SymbolMap.Resolve(SymbolMap.AppManager, log);
        return _o.AppMgr != IntPtr.Zero;
    }

    internal static IntPtr App()
    {
        IntPtr mgr = *(IntPtr*)_o.AppMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    /// <summary>Does the game-object array still hold <paramref name="obj"/> under <paramref name="id"/>? The array frees every
    /// registered object when the module unloads, so a doll that outlives the module finds its creature already gone.</summary>
    internal static bool Holds(uint id, IntPtr obj)
    {
        IntPtr app = App();
        if (app == IntPtr.Zero || obj == IntPtr.Zero) return false;
        CGameObject go = new CClientExoApp(app).GetGameObject(id);
        return !go.IsNull && go.Ptr == obj;
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
        byte* info = stackalloc byte[InfoMax];
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
        bool taken = d.Registered && !Holds(d.Id, d.Creature);
        NuiModelGear.Release(d.Creature, taken);
        _o.Doll = new DollState();
        _o.TargetTex = 0;
        _o.Rendered = false;
        IntPtr app = App();
        if (d.Viewport != IntPtr.Zero && d.Enabled) EngineFn.GuiViewportDisable(d.Viewport);
        if (taken) _o.Log?.Always("NuiModelDoll: the engine already freed the throwaway creature (module teardown) -- left alone");
        else if (d.Creature != IntPtr.Zero)
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

    /// <summary>Aim the view's camera at the creature, which stands at the origin facing +X: <paramref name="dist"/> away,
    /// <paramref name="height"/> up, <paramref name="yawDeg"/> degrees clockwise from its right side (90 its back, 270 its front).
    /// The camera moves, not the model: identity looks down -Z with Y up, laid back 90 degrees on X it looks along +Y.</summary>
    internal static void Orbit(float dist, float height, float yawDeg)
    {
        IntPtr c = _o.Doll.Camera;
        if (c == IntPtr.Zero) return;
        var cam = new CAurCamera(c);
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -yawDeg * MathF.PI / 180f);
        Vector3 p = Vector3.Transform(new Vector3(0, -dist, height), yaw);
        var v = new EVec { X = p.X, Y = p.Y, Z = p.Z };
        var q = EQuat.From(Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), yaw));
        EVec rv;
        EQuat rq;
        if (OperatingSystem.IsWindows())
        {
            ((delegate* unmanaged<IntPtr, EVec*, EVec, IntPtr>)cam.SetPositionFn)(c, &rv, v);
            ((delegate* unmanaged<IntPtr, EQuat*, EQuat, IntPtr>)cam.SetOrientationFn)(c, &rq, q);
        }
        else
        {
            ((delegate* unmanaged<IntPtr, EVec, EVec>)cam.SetPositionFn)(c, v);
            ((delegate* unmanaged<IntPtr, EQuat, EQuat>)cam.SetOrientationFn)(c, q);
        }
    }

    /// <summary>The appearance record <paramref name="source"/> holds now, as a span over the engine's own bytes.</summary>
    internal static ReadOnlySpan<byte> InfoOf(IntPtr source)
    {
        IntPtr a = source == IntPtr.Zero ? IntPtr.Zero : new CNWCCreature(source).Pathfind.Ptr;
        return a == IntPtr.Zero ? default : new ReadOnlySpan<byte>((void*)a, InfoBytes);
    }

    /// <summary>The doll takes <paramref name="source"/>'s appearance again, as character creation's panel does after each change:
    /// rebuild the body, fetch the model it made, and take that into the view.</summary>
    internal static bool Refresh(IntPtr source)
    {
        DollState d = _o.Doll;
        ReadOnlySpan<byte> now = InfoOf(source);
        if (d.Creature == IntPtr.Zero || now.IsEmpty) return false;
        byte* info = stackalloc byte[InfoMax];
        now.CopyTo(new Span<byte>(info, InfoBytes));
        if (EngineFn.NwcCreatureSetAppearance(d.Creature, (IntPtr)info, BuildFlags) == 0) return false;
        IntPtr model = new CNWCCreature(d.Creature).GetModel(0xff, 1);
        if (model == IntPtr.Zero) return false;
        d.Model = model;
        EngineFn.GuiViewportTakeModel(d.Viewport, model);
        Opaque(model);
        return true;
    }

    /// <summary>Per frame: the creature's own AI fades it out again (it is in no area), so the colour shift is put back to
    /// opaque white whenever the model's alpha has dropped.</summary>
    internal static void KeepShown()
    {
        DollState d = _o.Doll;
        if (d.Creature == IntPtr.Zero || d.Model == IntPtr.Zero) return;
        if (new CNWCObject(d.Creature).Visible != 1) EngineFn.ObjSetFadeState(d.Creature, 1);
        if (new Gob(d.Model).Alpha < 1f) Opaque(d.Model);
    }
}
