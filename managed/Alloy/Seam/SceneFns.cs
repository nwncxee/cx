using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The scene- and camera-shaped <c>Aur*</c> free functions, and the decision for each: HOOKED are the
/// two factories and <c>CAurScene::SetAmbient</c>, an out-of-line member the game links NON-virtually and so
/// no facade slot can answer. The rest are left alone -- their bodies are pure vcalls or a read of a shell word
/// CX keeps current (<c>GetAurSceneObjectCount</c>'s <c>scene+0x138</c>), so the engine's own body works.</summary>
internal static unsafe class SceneFns
{
    /// <summary>Install the three hooks. False = one did not take, which is a seam that cannot be armed: the
    /// caller stands DB mode down rather than letting the engine's own factory build an object nothing owns.</summary>
    internal static bool Install(ExtenderLog log, HookService hooks)
    {
        bool ok = true;
        ok &= Hook(log, hooks, SymbolMap.NewCAurScene, (IntPtr)(delegate* unmanaged<byte*, nint>)&OnNewCAurScene);
        ok &= Hook(log, hooks, SymbolMap.NewCAurCamera, (IntPtr)(delegate* unmanaged<byte*, nint>)&OnNewCAurCamera);
        ok &= Hook(log, hooks, SymbolMap.CAurSceneSetAmbient, (IntPtr)(delegate* unmanaged<nint, VectorArg, void>)&OnSetAmbient);
        ok &= Hook(log, hooks, SymbolMap.FindScene, (IntPtr)(delegate* unmanaged<byte*, nint>)&OnFindScene);
        ok &= Hook(log, hooks, SymbolMap.AuroraSetTileFoggedByWar, (IntPtr)(delegate* unmanaged<Vec3*, byte, void>)&OnSetTileFoggedByWar);
        // The area's height step, chained off the tileset resolve: without it EVERY tile spawn refuses, so a
        // failure here is a failure of the whole tile build and is reported as one.
        ok &= TileSetSeam.Install(log, hooks);
        ok &= Hook(log, hooks, SymbolMap.SetAlphaObject, (IntPtr)(delegate* unmanaged<nint, float, void>)&OnSetAlpha);
        return ok;
    }

    private static bool Hook(ExtenderLog log, HookService hooks, string symbol, IntPtr handler)
    {
        if (hooks.Install(symbol, handler) != IntPtr.Zero) return true;
        log.Always($"SceneFns: {symbol} did not hook -- the CX scene database cannot answer for it, so DB mode "
                 + "stands down (the engine's own body would build an engine object instead)");
        return false;
    }

    /// <summary>The engine's scene-name array at <c>Scene+0xf0</c>: 0x40 bytes, up to the member list at +0x130.</summary>
    private const int SceneNameRoom = 0x40;   // not-an-offset: a length, the bytes from Scene+0xf0 to Scene+0x130

    /// <summary><c>NewCAurScene(char const*)</c>: a CX scene record and its facade. The original is NEVER
    /// called -- that is the whole point of the switch, and it is why a failed hook is a stand-down rather
    /// than a fallback.</summary>
    [UnmanagedCallersOnly]
    private static nint OnNewCAurScene(byte* name)
    {
        string n = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        DbId id = DbScene.Create(n);
        if (id.IsNull) return 0;
        nint shell = Facade.Mint(FacadeKind.Scene, id);
        if (shell == 0) { DbScene.Destroy(id); return 0; }
        SceneDb.SceneFacade[id.Index] = shell;
        byte* dst = (byte*)Unsafe.AsPointer(ref new Scene(shell).Name);
        dst[SceneDb.SceneNameBytes(id, new Span<byte>(dst, SceneNameRoom))] = 0;
        SeedFog(shell);
        SeedWind(shell);
        SceneShell.Refresh(id);
        return shell;
    }

    /// <summary><c>FindScene(char const*)</c>: answer the engine's registry lookup from the CX scene table,
    /// case-insensitively like the <c>strcasecmp</c> it replaces, null for no match (the engine's contract for
    /// "no such scene"). The engine's own array stays empty, and the game reaches a scene by NAME for the
    /// area's fog and for <c>AuroraSetTileFoggedByWar</c>, which would dereference null without this.</summary>
    [UnmanagedCallersOnly]
    private static nint OnFindScene(byte* name)
    {
        if (name == null) return 0;
        string n = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        for (int i = 0; i < SceneDb.MaxScenes; i++)
        {
            if (!SceneDb.Scenes.LiveAt(i)) continue;
            if (!string.Equals(SceneDb.SceneName[i], n, StringComparison.OrdinalIgnoreCase)) continue;
            return SceneDb.SceneFacade[i];
        }
        return 0;
    }

    /// <summary>Give the shell a real <c>WindManager</c> at <c>+0x160</c> -- zero there is a SIGSEGV on area
    /// load, since <c>setglobalwind</c> dereferences it unchecked -- by running the engine's own ctor over the
    /// block: it stores pointers INTO ITSELF at <c>+0x838</c>/<c>+0x840</c> and leaves the emitter count at
    /// <c>+0x868</c> zero, keeping <c>SetGlobalWind</c> off an emitter list CX has not. Released with the shell.</summary>
    private static void SeedWind(nint shell)
    {
        var sc = new Scene(shell);
        if (sc.IsNull) return;
        WindManagerCtorFn ctor = EngineFn.WindManagerCtor;
        int size = EngineLayout.WindManagerSize;
        if (ctor == null || size <= 0) return;
        nint block = (nint)NativeMemory.AllocZeroed((nuint)size);
        ctor(block);
        sc.WindManager = block;
    }

    /// <summary>Free the scene's <c>WindManager</c> block. Called from the scene's destroy, so the block's
    /// lifetime is the shell's.</summary>
    internal static void ReleaseWind(nint shell)
    {
        var sc = new Scene(shell);
        if (sc.IsNull) return;
        nint block = sc.WindManager;
        if (block == 0) return;
        sc.WindManager = 0;
        NativeMemory.Free((void*)block);
    }


    /// <summary>The fog words <c>Scene::Scene</c> @0x4724f0 leaves behind, written into a fresh shell. A CX
    /// shell is zero-filled and zero is not the engine's "no fog yet" -- the ctor leaves fog off, near 5, far
    /// 40, white -- and the game only overwrites SOME of those, so a shell left at zero would fog from 0 to 0
    /// the moment anything enabled it.</summary>
    private static void SeedFog(nint shell)
    {
        var sc = new Scene(shell);
        if (sc.IsNull) return;
        sc.FogEnable = 0;
        sc.FogStart = CtorFogNear;
        sc.FogEnd = CtorFogFar;
        sc.FogColor = Vector3.One;
    }

    /// <summary><c>Scene::Scene</c>'s own fog near, the high half of its <c>+0x140</c> store.</summary>
    private const float CtorFogNear = 5f;
    /// <summary><c>Scene::Scene</c>'s own fog far, the low half of its <c>+0x148</c> store.</summary>
    private const float CtorFogFar = 40f;

    /// <summary><c>NewCAurCamera(char const*)</c>. A camera is TWO records: the camera row and the
    /// <c>CAurObject</c> row its <c>+8</c> sub-object resolves to, which <c>GuiAttachViewport</c> and
    /// <c>Camera::AsCAurObject</c> both reach. seam-obj mints the object row with the shell
    /// (<see cref="FacadeParts.NewCameraObject"/>).</summary>
    [UnmanagedCallersOnly]
    private static nint OnNewCAurCamera(byte* name)
    {
        string n = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        DbId obj = FacadeParts.NewCameraObject(n);
        DbId id = DbCamera.Create(obj);
        if (id.IsNull) return 0;
        nint shell = Facade.Mint(FacadeKind.Camera, id);
        if (shell == 0) { DbCamera.Destroy(id); return 0; }
        SceneDb.CamFacade[id.Index] = shell;
        string unique = DbScene.UniqueName(n);
        SceneDb.CamName[id.Index] = unique;
        Facade.SetName(new CX.Engine.Camera(shell).GobSub, unique);
        // The object row's shell is the camera block's +8 INTERIOR -- the literal pointer the engine passes
        // as the camera's CAurObject* (GuiAttachViewport @0x68bda0 writes (CAurObject*)(camera + 8)), and the
        // address Facade.GobOf already maps back to this row. Without the forward half, every reader of
        // ObjFacade saw a camera as shell-less: BehaviorSeam.Attached then wrote no attach word, so the
        // engine's own CAurBehaviorSubwindow::Control failed its "am I still the owner's behaviour" test and
        // returned before Camera::SetViewPort -- every GUI viewport camera kept an empty rect.
        if (SceneDb.Objects.Valid(obj)) SceneDb.ObjFacade[obj.Index] = new CX.Engine.Camera(shell).GobSub;
        return shell;
    }

    /// <summary><c>CAurScene::SetAmbient(Vector)</c>: the column is the truth, and the shell does NOT mirror
    /// <c>+0x168</c> -- the only reader of that word is <c>Scene::Render</c>'s <c>SetLightModel</c>, engine
    /// renderer code that does not run in DB mode (<see cref="SceneShell"/>'s remarks).</summary>
    [UnmanagedCallersOnly]
    private static void OnSetAmbient(nint scene, VectorArg colour)
    {
        DbId s = Facade.SceneId(scene);
        if (!SceneDb.Scenes.Valid(s)) return;
        Vec3 c = colour.Value;
        SceneDb.SceneAmbient[s.Index] = new Vector3(c.X, c.Y, c.Z);
    }

    /// <summary><c>AuroraSetTileFoggedByWar(Vector&amp;, bool)</c> @0x453320: the fog of war, and the only tile
    /// call that is a free function rather than a scene slot -- it works on the engine's current scene, so CX
    /// answers it for the WORLD scene, which is the only scene a tile grid can belong to.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetTileFoggedByWar(Vec3* pos, byte fogged)
    {
        if (pos == null) return;
        DbId world = WorldScene();
        if (world.IsNull) return;
        TileBuild.SetFoggedByWar(world, new Vector3(pos->X, pos->Y, pos->Z), fogged != 0);
    }

    /// <summary>The session's one world scene, cached. <c>AuroraSetTileFoggedByWar</c> is called per TILE as
    /// the fog of war moves, so the linear scan this used to do was 256 slots per cell. The cache is
    /// invalidated by the generation check alone: a scene that dies takes its handle with it.</summary>
    private static DbId WorldScene()
    {
        if (SceneDb.Scenes.Valid(_world) && SceneDb.SceneIsWorld[_world.Index]) return _world;
        _world = default;
        for (int i = 1; i < SceneDb.Scenes.High; i++)
            if (SceneDb.Scenes.LiveAt(i) && SceneDb.SceneIsWorld[i]) { _world = SceneDb.Scenes.IdAt(i); break; }
        return _world;
    }

    private static DbId _world;


    /// <summary><c>setalpha(CAurObject*, float)</c> @0x4524d0 -- glue the game links non-virtually whose body
    /// reads <c>Gob+0x90</c> and RECURSES the part tree, writing each part's alpha. No vtable slot, so a facade
    /// cannot answer it and the engine body would walk CX memory as an engine <c>Part</c>. The port writes the
    /// object's whole part range. <c>ponytail:</c> seam-obj should reclaim this hook along with <c>Gob+0x90</c>.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetAlpha(nint obj, float alpha)
    {
        DbId o = Facade.ObjectId(obj);
        if (!SceneDb.Objects.Valid(o)) return;
        int first = SceneDb.ObjPartFirst[o.Index], n = SceneDb.ObjPartCount[o.Index];
        for (int i = 0; i < n; i++)
        {
            SceneDb.PartAlpha[first + i] = alpha;
            SceneDb.TouchPart(first + i, Dirty.Material);
        }
    }

}
