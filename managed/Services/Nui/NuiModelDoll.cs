using System;
using System.Collections.Generic;
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
/// There are <see cref="MaxViews"/> <see cref="ModelSlot"/>s, one doll each; a view claims one for as long as it shows.
/// </summary>
internal static unsafe class NuiModelDoll
{
    /// <summary>The scene name every doll asks for. The engine, and <c>DbScene.UniqueName</c> beside it, keep scene names unique by
    /// appending a number, so the second live doll's scene is <c>sceneGUI_guiModelView1</c>. A slot is told apart by its scene's
    /// pointer, never its name. <c>RenderOnlyGobs</c> lights the exact name only, so <see cref="NuiModelEngine"/> shows it that.</summary>
    internal const string ViewName = "guiModelView";
    /// <summary>The most dolls alive at once, across NUI windows, cards and the held image. A view past the cap keeps its stand-in.</summary>
    internal const int MaxViews = 8;
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

    /// <summary>One doll's place: the doll, the texture its scene is drawn into, its background and light, and the hands it holds.
    /// A view claims a slot, fills it, and gives it back.</summary>
    internal sealed class ModelSlot
    {
        public readonly int Index;
        public DollState Doll = new();
        /// <summary>The GL texture the doll's layer is drawn into (0 = the screen), its size, its background colour, and whether a
        /// frame has landed in it since the doll was built.</summary>
        public uint TargetTex;
        public int TargetW, TargetH;
        public readonly float[] Bg = new float[4];
        public bool Rendered, Claimed;
        public ModelLight Light = NuiModelLook.EngineLight;
        public readonly NuiModelGear.GearState Gear = new();
        public ModelSlot(int index) => Index = index;
    }

    /// <summary>What this class keeps between calls: the log, the app manager's slot and the slots.</summary>
    private sealed class DollOwnerState
    {
        public ExtenderLog? Log;
        public IntPtr AppMgr;
        public readonly ModelSlot[] Slots = MakeSlots();
    }

    private static ModelSlot[] MakeSlots()
    {
        var a = new ModelSlot[MaxViews];
        for (int i = 0; i < a.Length; i++) a[i] = new ModelSlot(i);
        return a;
    }

    private static readonly DollOwnerState _o = new();

    /// <summary>A free slot, now claimed, or null at the cap.</summary>
    internal static ModelSlot? Claim()
    {
        foreach (ModelSlot s in _o.Slots) if (!s.Claimed) { s.Claimed = true; return s; }
        return null;
    }

    /// <summary>The slot goes back: its doll is taken down and its texture, background and light forgotten.</summary>
    internal static void Free(ModelSlot s)
    {
        if (s.Doll.Creature != IntPtr.Zero) Destroy(s);
        s.TargetTex = 0; s.TargetW = s.TargetH = 0;
        for (int i = 0; i < 4; i++) s.Bg[i] = 0f;
        s.Light = NuiModelLook.EngineLight;
        s.Claimed = false;
    }

    internal static ReadOnlySpan<ModelSlot> Slots => _o.Slots;

    /// <summary>The slot whose doll's scene is <paramref name="scene"/> (an engine <c>Scene</c> or Alloy's facade for one), or null.</summary>
    internal static ModelSlot? OfScene(IntPtr scene)
    {
        if (scene == IntPtr.Zero) return null;
        foreach (ModelSlot s in _o.Slots) if (s.Doll.Scene == scene) return s;
        return null;
    }

    /// <summary>The claimed slot that draws into <paramref name="tex"/>, or null.</summary>
    internal static ModelSlot? OfTexture(uint tex)
    {
        if (tex == 0) return null;
        foreach (ModelSlot s in _o.Slots) if (s.Claimed && s.TargetTex == tex) return s;
        return null;
    }

    internal static void SetTarget(ModelSlot s, uint tex, int w, int h) { s.TargetTex = tex; s.TargetW = w; s.TargetH = h; }
    internal static bool TargetSize(ModelSlot s, out int w, out int h) { w = s.TargetW; h = s.TargetH; return w > 0 && h > 0; }
    /// <summary>The slot's layer was drawn into its target: the doll's first frame has landed.</summary>
    internal static void MarkRendered(ModelSlot s) => s.Rendered = true;
    internal static void SetLight(ModelSlot s, ModelLight l) => s.Light = l;
    internal static void SetBackground(ModelSlot s, float r, float g, float b, float a) { s.Bg[0] = r; s.Bg[1] = g; s.Bg[2] = b; s.Bg[3] = a; }
    internal static void Background(ModelSlot s, float* c) { for (int i = 0; i < 4; i++) c[i] = s.Bg[i]; }

    /// <summary>Oracle: the documented cap (8 views) and hand-written scene pointers. Claim, find and free act on the slots only.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var taken = new List<ModelSlot>();
        for (ModelSlot? s = Claim(); s != null; s = Claim()) taken.Add(s);
        check.Expect(taken.Count == 8, "eight views can be alive at once, and the ninth finds no slot");
        taken[2].Doll.Scene = (IntPtr)0x1000;
        taken[5].Doll.Scene = (IntPtr)0x2000;
        taken[5].TargetTex = 77;
        check.Expect(OfScene((IntPtr)0x2000) == taken[5] && OfScene((IntPtr)0x1000) == taken[2] && OfScene((IntPtr)0x3000) == null && OfScene(IntPtr.Zero) == null,
            "a scene belongs to the slot whose doll made it, whatever its name");
        check.Expect(OfTexture(77) == taken[5] && OfTexture(78) == null && OfTexture(0) == null, "a texture belongs to the slot that draws into it");
        Free(taken[5]);
        check.Expect(OfTexture(77) == null && Claim() == taken[5], "a slot given back is found by no texture and is the next one claimed");
        foreach (ModelSlot s in taken) { s.Doll = new DollState(); Free(s); }
        check.Expect(Claim() == taken[0], "after every view is gone the first slot is free again");
        Free(taken[0]);
        log.Info(check.Ok ? "NuiModelDoll.SelfCheck: passed" : "NuiModelDoll.SelfCheck: FAILED");
    }

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

    /// <summary>Build the slot's doll as a copy of <paramref name="source"/>, in a viewport covering the screen rectangle.
    /// False = a step failed and the half-built doll was taken down again.</summary>
    internal static bool Build(ModelSlot slot, IntPtr source, int x, int y, int w, int h, bool ai = true)
    {
        if (slot.Doll.Creature != IntPtr.Zero) Destroy(slot);
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
            slot.Doll = d;
            Destroy(slot);
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
            slot.Doll = d;
            Destroy(slot);
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
        slot.Doll = d;
        if (d.Model == IntPtr.Zero)
        {
            _o.Log?.Always("NuiModelDoll: the throwaway creature has no model");
            Destroy(slot);
            return false;
        }
        EngineFn.GuiViewportTakeModel(vp, d.Model);
        EngineFn.GuiViewportSetViewport(vp, x, y, w, h);
        EngineFn.GuiViewportEnable(vp);
        d.Enabled = true;
        _o.Log?.Always($"NuiModelDoll: slot {slot.Index} built -- creature 0x{mem:x} id 0x{id:x} model 0x{d.Model:x} viewport 0x{vp:x} scene 0x{d.Scene:x} camera 0x{d.Camera:x}");
        return true;
    }

    /// <summary>Take the slot's doll down the way the engine's own creature preview does: leave the view, drop the id, delete the
    /// creature, then the view with its scene and camera.</summary>
    internal static void Destroy(ModelSlot slot)
    {
        DollState d = slot.Doll;
        bool taken = d.Registered && !Holds(d.Id, d.Creature);
        NuiModelGear.Release(slot.Gear, d.Creature, taken);
        if (!taken) NuiModelVfx.Clear(d.Creature);
        slot.Doll = new DollState();
        slot.Rendered = false;
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
        _o.Log?.Always($"NuiModelDoll: slot {slot.Index} taken down");
    }

    /// <summary>The colour shift the fade machinery leaves a new creature at: alpha 1, colour white, to the attached parts too.</summary>
    internal static void Opaque(IntPtr model)
    {
        IntPtr fn = new Gob(model).Ptr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(*(IntPtr*)model + EngineLayout.GobSetColorShiftingSlot);
        ((delegate* unmanaged<IntPtr, EVec, float, int, void>)fn)(model, new EVec { X = 1, Y = 1, Z = 1 }, 1f, 1);
    }

    /// <summary>The turn from behind a model to its right side: a model faces +Y in its own frame, so its right is +X.</summary>
    private const float RightSideDeg = 90f;

    /// <summary>Aim the slot's camera at the doll's model, in the model's own frame: <paramref name="dist"/> away, <paramref name="height"/>
    /// up, <paramref name="yawDeg"/> degrees clockwise from its right side (90 its back, 270 its front), wherever its AI stood and turned it,
    /// tipped <paramref name="pitchDeg"/> degrees down in place (negative looks up).
    /// Identity looks down -Z with Y up; laid back 90 degrees on X it looks along +Y.</summary>
    internal static void Orbit(ModelSlot slot, float dist, float height, float yawDeg, float pitchDeg = 0f)
    {
        IntPtr c = slot.Doll.Camera, m = slot.Doll.Model;
        if (c == IntPtr.Zero || m == IntPtr.Zero) return;
        var cam = new CAurCamera(c);
        var model = new Gob(m);
        Quaternion turn = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (RightSideDeg - yawDeg) * MathF.PI / 180f), model.Rot);
        Vector3 p = model.Position + Vector3.Transform(new Vector3(0, -dist, height), turn);
        var v = new EVec { X = p.X, Y = p.Y, Z = p.Z };
        var q = EQuat.From(Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2 - pitchDeg * MathF.PI / 180f), turn));
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

    /// <summary>The slot's doll model's place and turn, for the log.</summary>
    internal static string Pose(ModelSlot slot)
    {
        if (slot.Doll.Model == IntPtr.Zero) return "no model";
        var g = new Gob(slot.Doll.Model);
        Quat r = g.Rot;
        return $"at {g.Position} turned (w {r.W:0.###}, z {r.Z:0.###})";
    }

    /// <summary>The appearance record <paramref name="source"/> holds now, as a span over the engine's own bytes.</summary>
    internal static ReadOnlySpan<byte> InfoOf(IntPtr source)
    {
        IntPtr a = source == IntPtr.Zero ? IntPtr.Zero : new CNWCCreature(source).Pathfind.Ptr;
        return a == IntPtr.Zero ? default : new ReadOnlySpan<byte>((void*)a, InfoBytes);
    }

    /// <summary>The slot's doll takes <paramref name="source"/>'s appearance again, as character creation's panel does after each change:
    /// rebuild the body, fetch the model it made, and take that into the view.</summary>
    internal static bool Refresh(ModelSlot slot, IntPtr source)
    {
        DollState d = slot.Doll;
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
    internal static void KeepShown(ModelSlot slot)
    {
        DollState d = slot.Doll;
        if (d.Creature == IntPtr.Zero || d.Model == IntPtr.Zero) return;
        if (new CNWCObject(d.Creature).Visible != 1) EngineFn.ObjSetFadeState(d.Creature, 1);
        if (new Gob(d.Model).Alpha < 1f) Opaque(d.Model);
    }
}
