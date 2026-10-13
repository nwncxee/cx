using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The <c>Scene</c>-only vtable slots (Linux indices 51..87). CX fills the five with a caller outside
/// the engine's renderer module -- 64 <c>AddObject</c>, 65 <c>ChildAdd</c>, 66 <c>ChildRemove</c>, 81
/// <c>IsAnimating</c>, 86 <c>GetShadowGob</c> -- and poisons the other 32: they are the engine renderer's own
/// entry points, so a call names itself in <c>cx.log</c> rather than jumping through a NULL word.</summary>
internal static unsafe class SceneOnlySlots
{
    /// <summary>Linux slot indices of the <c>Scene</c>-only block.</summary>
    internal const int First = 51, Last = 87;

    /// <summary>Slot index -> entry point for the five CX fills; every other index in the block is 0, which
    /// seam-obj's vtable builder fills with the poison entry for that slot NAME. <c>ResetTileFading</c> (75)
    /// and <c>ReloadGrass</c> (74) are poisoned because CX answers the engine's outer forms instead -- the
    /// interface's <c>ResetAreaTileFading</c> (16) and <c>ReloadAreaGrass</c> (15).</summary>
    internal static readonly IntPtr[] Table = Build();

    private static long _childAdds, _childRemoves, _addObjects;

    private static IntPtr[] Build()
    {
        var t = new IntPtr[Last + 1 - First];
        t[64 - First] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&AddObject;
        t[65 - First] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&ChildAdd;
        t[66 - First] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&ChildRemove;
        t[81 - First] = (IntPtr)(delegate* unmanaged<nint, byte>)&IsAnimating;
        t[86 - First] = (IntPtr)(delegate* unmanaged<nint, nint>)&GetShadowGob;
        return t;
    }

    [UnmanagedCallersOnly]
    private static void AddObject(nint self, nint obj)
    {
        _addObjects++;
        DbId s = Facade.SceneId(self), o = Facade.ObjectId(obj);
        if (!TileBuild.RefuseMove(o, s, "Scene::AddObject") && !Facade.RefuseCrossJoin(o, s, "Scene::AddObject")) DbScene.Join(s, o);
    }

    /// <summary>Slot 65. <c>GobRenderLast</c> calls <see cref="ChildRemove"/> then this, so an add on an
    /// object already in the scene must APPEND (which is what <see cref="DbScene.Join"/> does) -- that
    /// remove/add pair IS the engine's "draw this last".</summary>
    [UnmanagedCallersOnly]
    private static void ChildAdd(nint self, nint obj)
    {
        _childAdds++;
        DbId s = Facade.SceneId(self), o = Facade.ObjectId(obj);
        if (!TileBuild.RefuseMove(o, s, "Scene::ChildAdd") && !Facade.RefuseCrossJoin(o, s, "Scene::ChildAdd")) DbScene.Join(s, o);
    }

    [UnmanagedCallersOnly]
    private static void ChildRemove(nint self, nint obj)
    {
        _childRemoves++;
        DbId s = Facade.SceneId(self), o = Facade.ObjectId(obj);
        if (TileBuild.OwnScene(o).Value == s.Value && TileBuild.RefuseMove(o, default, "Scene::ChildRemove")) return;
        DbScene.Leave(s, o);
    }

    [UnmanagedCallersOnly]
    private static byte IsAnimating(nint self) => (byte)(DbScene.IsAnimating(Facade.SceneId(self)) ? 1 : 0);

    [UnmanagedCallersOnly]
    private static nint GetShadowGob(nint self)
    {
        DbId s = Facade.SceneId(self);
        if (!SceneDb.Scenes.Valid(s)) return 0;
        DbId o = SceneDb.SceneShadowGob[s.Index];
        return SceneDb.Objects.Valid(o) ? SceneDb.ObjFacade[o.Index] : 0;
    }

    internal static string Stats(int _)
        => $"scene-only: {_addObjects} AddObject, {_childAdds} ChildAdd, {_childRemoves} ChildRemove";
}
