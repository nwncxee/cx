using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The <c>CAurScene</c> interface, implemented by CX: 51 slots measured off <c>_ZTV9CAurScene</c>
/// @0x186c510 and the Windows vftable at rva 0xd8db98, <see cref="Table"/> indexed by LINUX slot, each an
/// <see cref="UnmanagedCallersOnlyAttribute"/> static resolving <c>this</c> via <see cref="Facade.SceneId"/>.
/// Slot 39 returns a <c>Vector</c> by value: SysV in xmm0:xmm1, Win64 <c>(this=rcx, sret=rdx, ...)</c>.</summary>
internal static unsafe class SceneSlots
{
    /// <summary>Slots in the <c>CAurScene</c> interface.</summary>
    internal const int Count = 51;

    private static ExtenderLog? _log;
    private static long _calls, _nullScene;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The slot table, Linux slot index -> entry point. Built once; null entries are a programming
    /// error and seam-obj's vtable builder refuses a table with one (a NULL slot in a live vtable is the
    /// crash ADR 0028's poison table exists to replace).</summary>
    internal static readonly IntPtr[] Table = Build();

    private static IntPtr[] Build()
    {
        var t = new IntPtr[Count];
        t[0] = (IntPtr)(delegate* unmanaged<nint, void>)&Dtor;
        t[1] = (IntPtr)(delegate* unmanaged<nint, void>)&DtorDeleting;
        t[2] = (IntPtr)(delegate* unmanaged<nint, byte*, nint>)&GetObject;
        t[3] = (IntPtr)(delegate* unmanaged<nint, nint, float>)&GetObjectScale;
        t[4] = (IntPtr)(delegate* unmanaged<nint, void>)&Render;
        t[5] = (IntPtr)(delegate* unmanaged<nint, float, void>)&Animate;
        t[6] = (IntPtr)(delegate* unmanaged<nint, int, int, int, byte, float, void>)&InitBSP;
        t[7] = (IntPtr)(delegate* unmanaged<nint, byte*, Vec3*, int, int, Quat*, nint>)&SpawnTile;
        t[8] = (IntPtr)(delegate* unmanaged<nint, Vec3*, Vec3*, Vec3*, void>)&SetTileLights;
        t[9] = (IntPtr)(delegate* unmanaged<nint, Vec3*, nint, nint, void>)&SetTileFlames;
        t[10] = (IntPtr)(delegate* unmanaged<nint, Vec3*, byte*, byte*, void>)&ReplaceTileTexture;
        t[11] = (IntPtr)(delegate* unmanaged<nint, Vec3*, void>)&DeleteTile;
        t[12] = (IntPtr)(delegate* unmanaged<nint, VectorArg, VectorArg, Vec3*, nint, int>)&HitCheck;
        t[13] = (IntPtr)(delegate* unmanaged<nint, void>)&ComputeStaticLighting;
        t[14] = (IntPtr)(delegate* unmanaged<nint, void>)&ComputeStaticProjections;
        t[15] = (IntPtr)(delegate* unmanaged<nint, void>)&ReloadAreaGrass;
        t[16] = (IntPtr)(delegate* unmanaged<nint, void>)&ResetAreaTileFading;
        t[17] = (IntPtr)(delegate* unmanaged<nint, float*, float*, void>)&GetFogRange;
        t[18] = (IntPtr)(delegate* unmanaged<nint, float, float, void>)&SetFogRange;
        t[19] = (IntPtr)(delegate* unmanaged<nint, void>)&DisableFog;
        t[20] = (IntPtr)(delegate* unmanaged<nint, void>)&EnableFog;
        t[21] = (IntPtr)(delegate* unmanaged<nint, Vec3*, void>)&SetFogColor;
        t[22] = (IntPtr)(delegate* unmanaged<nint, Vec3*, void>)&SetSceneFocus;
        t[23] = (IntPtr)(delegate* unmanaged<nint, int, byte>)&SetGeomFadeProperties;
        t[24] = (IntPtr)(delegate* unmanaged<nint, byte>)&IsGeomFadeEnabled;
        t[25] = (IntPtr)(delegate* unmanaged<nint, void>)&FinishSceneGeometry;
        t[26] = (IntPtr)(delegate* unmanaged<nint, void>)&EnableAnimations;
        t[27] = (IntPtr)(delegate* unmanaged<nint, void>)&DisableAnimations;
        t[28] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&SetBlindLights;
        t[29] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&SetShadowGob;
        t[30] = (IntPtr)(delegate* unmanaged<nint, Vec3*, int>)&GetNumTileFlames;
        t[31] = (IntPtr)(delegate* unmanaged<nint, Vec3*, int>)&GetNumTileLights;
        t[32] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&RenderGobFirst;
        t[33] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&RenderGobLast;
        t[34] = (IntPtr)(delegate* unmanaged<nint, nint, nint, void>)&RenderGobAfter;
        t[35] = (IntPtr)(delegate* unmanaged<nint, nint, nint, void>)&RenderGobBefore;
        t[36] = (IntPtr)(delegate* unmanaged<nint, nint, Vec3*, void>)&AddHighlightGob;
        t[37] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&RemoveHighlightGob;
        t[38] = (IntPtr)(delegate* unmanaged<nint, byte*, void>)&SetHighlightGobTexture;
#if WINDOWS
        t[39] = (IntPtr)(delegate* unmanaged<nint, Vec3*, nint, nint>)&GetLightingValueAtFocusWin;
#else
        t[39] = (IntPtr)(delegate* unmanaged<nint, nint, Vec3>)&GetLightingValueAtFocusSysV;
#endif
        t[40] = (IntPtr)(delegate* unmanaged<nint, nint, nint, nint, void>)&AddLinesToRender;
        t[41] = (IntPtr)(delegate* unmanaged<nint, void>)&ClearLinesToRender;
        t[42] = (IntPtr)(delegate* unmanaged<nint, Vec3*, float, float, void>)&SetGlobalWind;
        t[43] = (IntPtr)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&SetSkyBox;
        t[44] = (IntPtr)(delegate* unmanaged<nint, float, int, void>)&SetSkyAlpha;
        t[45] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&SetSkyFade;
        t[46] = (IntPtr)(delegate* unmanaged<nint, void>)&TurnOffSkyBox;
        t[47] = (IntPtr)(delegate* unmanaged<nint, void>)&TurnOnSkyBox;
        t[48] = (IntPtr)(delegate* unmanaged<nint, void>)&ClearSkyBox;
        t[49] = (IntPtr)(delegate* unmanaged<nint, byte, void>)&SetRenderAABB;
        t[50] = (IntPtr)(delegate* unmanaged<nint, byte>)&GetRenderAABB;
        return t;
    }

    /// <summary>The slot's scene handle, or null when the shell no longer names a live record (which the
    /// generation check answers without a death notice). A null one is counted, not silently ignored.</summary>
    private static DbId Id(nint self)
    {
        _calls++;
        DbId id = Facade.SceneId(self);
        if (!SceneDb.Scenes.Valid(id)) { _nullScene++; return default; }
        return id;
    }

    // ---- lifetime ------------------------------------------------------------------------------------

    /// <summary>Slot 0, the complete-object destructor: the scene destroy edge (ADR 0028 section 3). Every
    /// object still in the scene dies first, then the record; the shell itself is retired by seam-obj, which
    /// owns the two-frame quarantine and the poison table.</summary>
    [UnmanagedCallersOnly]
    private static void Dtor(nint self) => Die(self);

    /// <summary>Slot 1, the DELETING destructor (<c>D0</c>): same edge, and the shell's memory goes back too --
    /// which <see cref="Facade.Retire"/> already does after the quarantine, so the two slots are one body.
    /// Both must be filled: the interface leaves them NULL, and <c>delete</c> through a <c>CAurScene*</c> goes
    /// through slot 1.</summary>
    [UnmanagedCallersOnly]
    private static void DtorDeleting(nint self) => Die(self);

    /// <summary>The scene destroy edge itself. Both destructor slots share it: an
    /// <c>[UnmanagedCallersOnly]</c> static cannot be called from managed code, so neither slot can forward
    /// to the other and the body lives here.</summary>
    private static void Die(nint self)
    {
        DbId id = Facade.SceneId(self);
        DbScene.Destroy(id);   // frees the record, its members and the shell's member mirror
        Facade.Retire(self);
    }

    // ---- membership and the frame --------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static nint GetObject(nint self, byte* name)
    {
        DbId s = Id(self);
        if (s.IsNull || name == null) return 0;
        DbId o = DbScene.Find(s, Marshal.PtrToStringUTF8((IntPtr)name) ?? "");
        return SceneDb.Objects.Valid(o) ? SceneDb.ObjFacade[o.Index] : 0;
    }

    /// <summary>Slot 3, <c>Scene::GetObjectScale</c> @0x55e240: a TAIL-CALL to the object's own
    /// <c>GetAdjustedScale</c> (CAurObject vtbl +0x2d8) and nothing else -- so it is the chain-folded scale,
    /// not the object's own column. The scene does not even look at itself.</summary>
    [UnmanagedCallersOnly]
    private static float GetObjectScale(nint self, nint obj)
    {
        DbId o = Facade.ObjectId(obj);
        if (!SceneDb.Objects.Valid(o)) return 1f;
        return ObjectBridge.ObjectAdjustedScale is { } f ? f(o) : SceneDb.ObjScale[o.Index];
    }

    /// <summary>Slot 4, <c>Scene::Render</c>. A scene does not draw itself -- the camera recorded the view and
    /// the renderer draws it from the frame's snapshot -- but it is no no-op: @0x5762d0 is the ONLY caller of
    /// <c>RenderInterface::CheckAndUpdateUniformDirtyDataForFrame(DeltaT)</c>, which refills the world clock,
    /// wind and area flags every owned stage animates on. CPU-side, so it is made here, on the game thread.</summary>
    [UnmanagedCallersOnly]
    private static void Render(nint self)
    {
        DbId id = Id(self);
        if (!id.IsNull) UniformsForFrame(SceneDb.SceneFacade[id.Index]);
    }

    /// <summary>The refresh itself, with the <c>currentscene</c> latch the engine holds around it: the body
    /// reads the global's <c>WindManager</c> (<c>currentscene+0x160</c>) and three scene floats with NO null
    /// check, so calling it unlatched segfaults on the first GUI view.</summary>
    /// <inheritdoc cref="Render"/>
    internal static void UniformsForFrame(nint shell)
    {
        if (!_uniformsReady)
        {
            _uniformsReady = true;
            _checkUniforms = EngineFn.RiCheckUniformData;
            _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
            _pCurrentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
            if (_checkUniforms == null || _deltaT == null || _pCurrentScene == null)
                _log?.Always("SceneSlots: "
                           + (_checkUniforms == null ? SymbolMap.RiCheckUniformData
                              : _deltaT == null ? SymbolMap.DeltaT : SymbolMap.CurrentScene)
                           + " unresolved -- the engine's per-frame shader runtime block is never refreshed, so "
                           + "every owned stage animates on a clock stuck at zero (still water, still rain "
                           + "impacts, still grass) and reads a dead area wind");
        }
        if (_checkUniforms == null || _deltaT == null || _pCurrentScene == null || shell == 0) return;
        IntPtr was = *_pCurrentScene;
        *_pCurrentScene = shell;
        _checkUniforms(*_deltaT);
        *_pCurrentScene = was;
    }

    /// <inheritdoc cref="UniformsForFrame"/>
    private static RiCheckUniformDataFn _checkUniforms;
    /// <inheritdoc cref="UniformsForFrame"/>
    private static float* _deltaT;
    /// <inheritdoc cref="UniformsForFrame"/>
    private static IntPtr* _pCurrentScene;
    /// <inheritdoc cref="UniformsForFrame"/>
    private static bool _uniformsReady;

    /// <summary>Slot 5, <c>Scene::Animate(float)</c> -> the animation runtime over this scene's records. It
    /// does NOT gate on the scene's animate flag, because <c>Scene::Animate</c> @0x469810 does not either:
    /// <c>Scene+0xb9</c> is written only by <c>EnableAnimations</c>/<c>DisableAnimations</c> and read only by
    /// <c>Scene::IsAnimating</c> -- a question the game layer asks, not a switch the engine animates behind.</summary>
    [UnmanagedCallersOnly]
    private static void Animate(nint self, float dt)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        DbAnim.Animate(s, dt);
        // CX-120: and this scene's CAMERAS. A camera is not in the scene's object list -- the engine animates
        // it separately, through its own behaviour -- so a loop over the scene's gobs never reaches it, and
        // DbAnimCamera.Animate had no caller at all. That is why the world camera sat at the origin for every
        // DB-mode run: the chase behaviour that places it behind the player was never stepped, the view matrix
        // was built at (0,0,0), and an area that lies entirely in front of that camera rendered nothing.
        DbId lastCam = default;
        for (int c = 1; c < SceneDb.Cameras.High; c++)
        {
            if (!SceneDb.Cameras.LiveAt(c) || SceneDb.CamScene[c].Value != s.Value) continue;
            lastCam = SceneDb.Cameras.IdAt(c);
            DbAnimCamera.Animate(lastCam, dt);
        }
        // CX-116: the tile fade, on the SAME dt and after the cameras have moved -- the engine builds its
        // fade plane through the followed gob (Scene::SetSceneFocus's position) facing the way the camera
        // looks, so a plane built from last frame's camera would ramp the wrong roofs. This is the only
        // per-frame tile work in DB mode and it is change-driven: a settled tile marks nothing.
        if (!lastCam.IsNull && TileBuild.FadeTarget(s, lastCam, out Vector3 fadeTarget))
            TileBuild.TickFade(s, fadeTarget, DbCamera.PosOf(lastCam), dt, SceneDb.GeomFade, lastCam);
    }

    /// <summary>Slots 26/27. One flag, the way the engine has one: <c>Scene+0xb9</c>, ctor default 1, written
    /// here and read by <c>Scene::IsAnimating</c>.</summary>
    [UnmanagedCallersOnly]
    private static void EnableAnimations(nint self) => DbScene.SetAnimating(Id(self), true);

    /// <inheritdoc cref="EnableAnimations"/>
    [UnmanagedCallersOnly]
    private static void DisableAnimations(nint self) => DbScene.SetAnimating(Id(self), false);

    // ---- tiles: the tiles stream's TileBuild ---------------------------------------------------------

    /// <summary>Slot 6, <c>Scene::InitBSP(int, int, int, bool, float)</c>: the area's tile grid. This is also
    /// what makes a scene a WORLD scene -- the engine's own discriminator is the static tile BSP root at
    /// <c>Scene+0x188</c>, and a GUI panel scene never calls this.</summary>
    [UnmanagedCallersOnly]
    private static void InitBSP(nint self, int w, int h, int border, byte interior, float heightTransition)
    {
        DbId s = Id(self);
        // Said once per session, loudly, because its ABSENCE is a whole-session symptom with no other voice:
        // a DB run whose log never carries this line has no world scene at all, every scene reads as a GUI
        // panel, and the game sits on "Loading Area..." for ever (measured on Windows, CX-120). A slot index
        // that is off by one lands this handler somewhere else and leaves exactly that silence.
        if (!_initBspSaid)
        {
            _initBspSaid = true;
            _log?.Always($"SceneSlots: Scene::InitBSP reached the facade (grid {w}x{h}, border {border}, "
                       + (interior != 0 ? "interior)" : "exterior)")
                       + (s.IsNull ? " -- but the scene did NOT resolve to a record" : ""));
        }
        if (s.IsNull) return;
        SceneDb.SceneIsWorld[s.Index] = true;
        SeamShell.NoteScene(SceneDb.SceneFacade[s.Index], world: true);
        TileBuild.InitGrid(s, w, h, border, heightTransition);
        SceneDb.TouchScene(s, Dirty.Tree);
    }

    /// <inheritdoc cref="InitBSP"/>
    private static bool _initBspSaid;

    /// <summary>Slot 7, <c>Scene::SpawnTile(char const*, Vector const&amp;, int, int, Quaternion*)</c>. <b>A
    /// non-null <c>Quaternion*</c> is a STATIC PLACEABLE, not a grid tile</b>: off the grid, several can share
    /// a cell, and filing one as a tile overwrites the cell's real tile. The engine orders a quaternion
    /// <c>(w, x, y, z)</c> and <see cref="Quat"/> swizzles here; the return is 0 for a tile, else its shell.</summary>
    [UnmanagedCallersOnly]
    private static nint SpawnTile(nint self, byte* model, Vec3* at, int orientation, int border, Quat* rot)
    {
        DbId s = Id(self);
        if (s.IsNull || at == null) return 0;
        string name = Marshal.PtrToStringUTF8((IntPtr)model) ?? "";
        var pos = new Vector3(at->X, at->Y, at->Z);
        // The engine stores (w, x, y, z); System.Numerics is (x, y, z, w). Quat's conversion is the swizzle
        // and it belongs HERE, at the facade, and nowhere deeper.
        Quaternion? q = rot != null ? (Quaternion)(*rot) : null;
        int tile = TileBuild.SpawnTile(s, name, pos, orientation, border, q);
        // A grid tile answers its gob only when vanilla's SpawnPartsForTile makes one (an animated tile);
        // CNWCTile keeps it at +0x38 and plays Day/Night/AnimLoop on it. A static placeable IS a record, and
        // TileBuild left it in StaticObject.
        if (tile != TileBuild.StaticTile) return tile < 0 ? 0 : TileShell(TileBuild.AnimatedTileObject(tile));
        DbId o = TileBuild.StaticObject;
        return SceneDb.Objects.Valid(o) ? SceneDb.ObjFacade[o.Index] : 0;
    }

    /// <summary>The shell for an animated tile's object, minted on first ask: the gob the game layer animates.
    /// TileBuild retires it when it frees the tile.</summary>
    private static nint TileShell(DbId o)
    {
        if (o.IsNull) return 0;
        nint shell = SceneDb.ObjFacade[o.Index];
        if (shell != 0) return shell;
        shell = Facade.Mint(FacadeKind.Object, o);
        if (shell == 0) return 0;
        ObjectFns.SeedShell(shell);
        Facade.SetXform(o, shell, SceneDb.ObjPos[o.Index], SceneDb.ObjRot[o.Index]);
        _tileShells++;
        return shell;
    }

    private static int _tileShells;
    /// <summary>Animated-tile gob shells minted (<see cref="TileShell"/>), for the seam period line.</summary>
    internal static int TileShells => _tileShells;

    [UnmanagedCallersOnly]
    private static void SetTileLights(nint self, Vec3* at, Vec3* c1, Vec3* c2)
    {
        DbId s = Id(self);
        if (s.IsNull || at == null) return;
        TileBuild.SetTileLights(s, V(at), V(c1), V(c2));
    }

    [UnmanagedCallersOnly]
    private static void SetTileFlames(nint self, Vec3* at, nint a, nint b)
    {
        DbId s = Id(self);
        if (s.IsNull || at == null) return;
        TileBuild.SetTileFlames(s, V(at), Facade.ObjectId(a), Facade.ObjectId(b));
    }

    [UnmanagedCallersOnly]
    private static void ReplaceTileTexture(nint self, Vec3* at, byte* from, byte* to)
    {
        DbId s = Id(self);
        if (s.IsNull || at == null) return;
        // The engine's argument order is (newTexture, oldTexture) -- Scene::ReplaceTileTexture's second
        // string is the one being replaced, which is also TileBuild's order.
        TileBuild.ReplaceTileTexture(s, V(at), Marshal.PtrToStringUTF8((IntPtr)from) ?? "",
                                     Marshal.PtrToStringUTF8((IntPtr)to) ?? "");
    }

    [UnmanagedCallersOnly]
    private static void DeleteTile(nint self, Vec3* at)
    {
        DbId s = Id(self);
        if (s.IsNull || at == null) return;
        TileBuild.DeleteTile(s, V(at));
    }

    [UnmanagedCallersOnly]
    private static int GetNumTileFlames(nint self, Vec3* at)
    {
        DbId s = Id(self);
        return s.IsNull || at == null ? 0 : TileBuild.NumTileFlames(s, V(at));
    }

    [UnmanagedCallersOnly]
    private static int GetNumTileLights(nint self, Vec3* at)
    {
        DbId s = Id(self);
        return s.IsNull || at == null ? 0 : TileBuild.NumTileLights(s, V(at));
    }

    /// <summary>Slot 25, <c>Scene::FinishSceneGeometry</c>: the area's tiles are all in. The build is
    /// incremental -- every tile is merged and filed as it spawns, and its ring is placed with it -- so there
    /// is nothing left to seal; the slot marks the scene so the visible-set index refiles once and the bake
    /// sees a complete area.</summary>
    [UnmanagedCallersOnly]
    private static void FinishSceneGeometry(nint self)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.TouchScene(s, Dirty.Tree);
        TileBuild.AuditScenes("finish");
    }

    [UnmanagedCallersOnly]
    private static void ResetAreaTileFading(nint self)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        TileBuild.ResetFading(s);
        SceneFeed.FadeReset(s);
    }

    // ---- hit tests: the hit stream's HitTest ---------------------------------------------------------

    /// <summary>Slot 12, <c>Scene::HitCheck(Vector, Vector, Vector*, CHitInfo*)</c>. Both <c>Vector</c>
    /// arguments arrive BY VALUE, which is the one shape both ABIs lower differently --
    /// <see cref="VectorArg"/> is the type that names it once for both (SysV the value, Win64 the
    /// caller-made copy's pointer).</summary>
    [UnmanagedCallersOnly]
    private static int HitCheck(nint self, VectorArg from, VectorArg to, Vec3* at, nint info)
    {
        DbId s = Id(self);
        if (s.IsNull) return 0;
        Vec3 a = from.Value, b = to.Value;
        return (int)SeamHit.Cast(s, new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), at, info);
    }

    // ---- area settings: Alloy's own inputs ----------------------------------------------------------

    /// <summary>The fog words live in the SHELL, at the engine's own offsets, not in a DB column: the game
    /// sets an area's fog as <c>FuncInterp("mainscene.fog*")</c> text that writes those words directly
    /// (<see cref="SceneFns"/>'s <c>FindScene</c> remarks), so a column would hold a second, staler answer.
    /// These slots keep the columns in step for <c>SceneDb</c>'s own readers and the fixtures.</summary>
    private static Scene Shell(DbId s) => new Scene(s.IsNull ? 0 : SceneDb.SceneFacade[s.Index]);

    [UnmanagedCallersOnly]
    private static void GetFogRange(nint self, float* near, float* far)
    {
        DbId s = Id(self);
        Scene sc = Shell(s);
        if (near != null) *near = sc.IsNull ? 0f : sc.FogStart;
        if (far != null) *far = sc.IsNull ? 0f : sc.FogEnd;
    }

    [UnmanagedCallersOnly]
    private static void SetFogRange(nint self, float near, float far)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneFogNear[s.Index] = near;
        SceneDb.SceneFogFar[s.Index] = far;
        Scene sc = Shell(s);
        if (!sc.IsNull) { sc.FogStart = near; sc.FogEnd = far; }
        SceneFeed.FogChanged(s);
    }

    [UnmanagedCallersOnly]
    private static void SetFogColor(nint self, Vec3* c)
    {
        DbId s = Id(self);
        if (s.IsNull || c == null) return;
        SceneDb.SceneFogColor[s.Index] = V(c);
        Scene sc = Shell(s);
        if (!sc.IsNull) sc.FogColor = V(c);
        SceneFeed.FogChanged(s);
    }

    [UnmanagedCallersOnly]
    private static void EnableFog(nint self) => Fog(Id(self), true);

    [UnmanagedCallersOnly]
    private static void DisableFog(nint self) => Fog(Id(self), false);

    private static void Fog(DbId s, bool on)
    {
        if (s.IsNull) return;
        SceneDb.SceneFogOn[s.Index] = on;
        Scene sc = Shell(s);
        if (!sc.IsNull) sc.FogEnable = on ? 1 : 0;
        SceneFeed.FogChanged(s);
    }

    [UnmanagedCallersOnly]
    private static void SetSceneFocus(nint self, Vec3* p)
    {
        DbId s = Id(self);
        if (s.IsNull || p == null) return;
        SceneDb.SceneFocus[s.Index] = V(p);
    }

    /// <summary>Slot 23, <c>Scene::SetGeomFadeProperties(int)</c> @0x55e1d0: accepts 1, 2 or 4 and NOTHING
    /// else (the body is <c>if (param-1u &gt; 1 &amp;&amp; param != 4) return 0;</c>), writes a GLOBAL, and
    /// returns whether it took the value.</summary>
    [UnmanagedCallersOnly]
    private static byte SetGeomFadeProperties(nint self, int props)
    {
        Id(self);
        if (props != 1 && props != 2 && props != 4) return 0;
        SceneDb.GeomFade = props;
        SceneFeed.GeomFadeChanged();
        return 1;
    }

    /// <summary>Slot 24, <c>Scene::IsGeomFadeEnabled</c> @0x5778b0: <c>geomFadeEnabled == 1</c> EXACTLY --
    /// modes 2 and 4 are set but not "enabled" by this question.</summary>
    [UnmanagedCallersOnly]
    private static byte IsGeomFadeEnabled(nint self)
    {
        Id(self);
        return (byte)(SceneDb.GeomFade == 1 ? 1 : 0);
    }

    [UnmanagedCallersOnly]
    private static void SetGlobalWind(nint self, Vec3* dir, float magnitude, float gustiness)
    {
        DbId s = Id(self);
        if (s.IsNull || dir == null) return;
        SceneDb.SceneWindDir[s.Index] = V(dir);
        SceneDb.SceneWindMag[s.Index] = magnitude;
        SceneDb.SceneWindGust[s.Index] = gustiness;
        SceneFeed.WindChanged(s);
    }

    /// <summary>Slot 15, <c>CAurScene::ReloadAreaGrass</c>: the area's grass info changed
    /// (<c>AurGrassAddInfo</c>/<c>AurGrassClearInfo</c> have been called), so the grass field rebuilds.</summary>
    [UnmanagedCallersOnly]
    private static void ReloadAreaGrass(nint self)
    {
        DbId s = Id(self);
        if (!s.IsNull) SceneFeed.GrassChanged(s);
    }

    /// <summary>Slots 13/14, <c>CAurScene::ComputeStaticLighting</c> / <c>ComputeStaticProjections</c>: the
    /// engine's own static-light and decal bake. Alloy owns both (the lightmap bake, keyed on CONTENT), so
    /// these mark the scene's static set stale and the bake picks it up at its own edge -- the
    /// one thing they must not do is run a bake synchronously inside a seam call.</summary>
    [UnmanagedCallersOnly]
    private static void ComputeStaticLighting(nint self)
    {
        DbId s = Id(self);
        if (!s.IsNull) SceneFeed.StaticLightingStale(s);
    }

    [UnmanagedCallersOnly]
    private static void ComputeStaticProjections(nint self)
    {
        DbId s = Id(self);
        if (!s.IsNull) SceneFeed.StaticProjectionsStale(s);
    }

    /// <summary><c>Scene::SetBlindLights</c> @0x45e010: from now on only <paramref name="obj"/>'s lights light the
    /// scene (null = every light again). Cold -- an effect's apply and remove -- so each change is a log line.</summary>
    [UnmanagedCallersOnly]
    private static void SetBlindLights(nint self, nint obj)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        DbId o = Facade.ObjectId(obj);
        if (SceneDb.SceneBlindLights[s.Index] == o) return;
        SceneDb.SceneBlindLights[s.Index] = o;
        SceneFeed.LightsChanged(s);
        _log?.Info(o.IsNull
            ? $"SceneSlots: blind lights OFF on scene {s.Index} -- every light lights it again"
            : $"SceneSlots: blind lights ON on scene {s.Index} -- only '{SceneDb.ObjModel[o.Index]?.Name ?? "?"}' (object {o.Index}) lights it: no other light, no area ambient or sun, no baked pages, black fog");
    }

    [UnmanagedCallersOnly]
    private static void SetShadowGob(nint self, nint obj)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneShadowGob[s.Index] = Facade.ObjectId(obj);
        SceneDb.TouchScene(s, Dirty.Visible);
    }

    // ---- draw order ---------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static void RenderGobFirst(nint self, nint obj) => DbScene.RenderFirst(Id(self), Facade.ObjectId(obj));

    [UnmanagedCallersOnly]
    private static void RenderGobLast(nint self, nint obj) => DbScene.RenderLast(Id(self), Facade.ObjectId(obj));

    [UnmanagedCallersOnly]
    private static void RenderGobAfter(nint self, nint obj, nint anchor)
        => DbScene.RenderAfter(Id(self), Facade.ObjectId(obj), Facade.ObjectId(anchor));

    [UnmanagedCallersOnly]
    private static void RenderGobBefore(nint self, nint obj, nint anchor)
        => DbScene.RenderBefore(Id(self), Facade.ObjectId(obj), Facade.ObjectId(anchor));

    // ---- highlight ----------------------------------------------------------------------------------

    /// <summary>Slot 36, <c>Scene::AddHighlightGob(CAurObject*, Vector&amp;)</c>: the scene's highlight list,
    /// which the glow and outline passes walk, with the per-object colour.</summary>
    [UnmanagedCallersOnly]
    private static void AddHighlightGob(nint self, nint obj, Vec3* colour)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        DbScene.AddHighlight(s, Facade.ObjectId(obj), colour != null ? V(colour) : Vector3.One);
    }

    [UnmanagedCallersOnly]
    private static void RemoveHighlightGob(nint self, nint obj)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        DbId o = Facade.ObjectId(obj);
        DbScene.RemoveHighlight(s, o);
    }

    [UnmanagedCallersOnly]
    private static void SetHighlightGobTexture(nint self, byte* name)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneHighlightTex[s.Index] = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        SceneFeed.HighlightChanged(s);
    }

    // ---- sky ----------------------------------------------------------------------------------------

    /// <summary>Slot 43, <c>Scene::SetSkyBox(CAurObject*, CAurObject*, CAurObject*, CAurObject*)</c>
    /// @0x55e260: store FOUR objects (<c>Scene+0x18/+0x20/+0x28/+0x30</c> -- <c>+0x38</c> is
    /// <c>SetSkyFade</c>'s, which is a different field), mark each one a sky object, zero the cross-fade and
    /// clear the OFF byte.</summary>
    [UnmanagedCallersOnly]
    private static void SetSkyBox(nint self, nint a, nint b, nint c, nint d)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        Sky(s, 0, a); Sky(s, 1, b); Sky(s, 2, c); Sky(s, 3, d);
        SceneDb.SceneSkyT[s.Index] = 0f;
        SceneDb.SceneSkyOff[s.Index] = false;
        SceneFeed.SkyChanged(s);
    }

    /// <summary>One skybox slot, plus the engine's per-object "this gob is sky" mark
    /// (<c>obj+0x18 = 1</c>) -- which is what keeps a sky object out of the ordinary draw.</summary>
    private static void Sky(DbId scene, int slot, nint shell)
    {
        DbId o = Facade.ObjectId(shell);
        SceneDb.SetSceneSky(scene, slot, o);
        if (SceneDb.Objects.Valid(o)) SceneDb.ObjFlags[o.Index] |= ObjFlag.Sky;
    }

    /// <summary>Slot 44, <c>Scene::SetSkyAlpha(float, int)</c> @0x55f100 -- a three-field state machine, not a
    /// setter, reproduced branch for branch: the mode already showing does nothing, a negative alpha zeroes the
    /// blend, an alpha at or past 1 COMPLETES the transition (<c>cur = mode</c>, blend back to 0), and anything
    /// else makes the mode the DESIRED one with the blend where it got to.</summary>
    [UnmanagedCallersOnly]
    private static void SetSkyAlpha(nint self, float alpha, int mode)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        int i = s.Index;
        if (SceneDb.SceneSkyCur[i] == mode) return;
        if (alpha < 0f) SceneDb.SceneSkyT[i] = 0f;
        else
        {
            SceneDb.SceneSkyT[i] = alpha;
            if (alpha >= 1f)
            {
                SceneDb.SceneSkyCur[i] = mode;
                SceneDb.SceneSkyT[i] = 0f;
                SceneFeed.SkyChanged(s);
                return;
            }
        }
        SceneDb.SceneSkyDesired[i] = mode;
        SceneFeed.SkyChanged(s);
    }

    [UnmanagedCallersOnly]
    private static void SetSkyFade(nint self, nint obj)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        DbId fade = Facade.ObjectId(obj);
        SceneDb.SceneSkyFade[s.Index] = fade;
        // The engine's own second store: SetSkyFade @0x55e2b0 marks the object obj+0x19 = 1, which is the
        // byte PartTriMesh::Mat reads to paint it in the fog colour instead of its own texture's.
        if (SceneDb.Objects.Valid(fade)) SceneDb.ObjFlags[fade.Index] |= ObjFlag.FogTint;
        SceneFeed.SkyChanged(s);
    }

    [UnmanagedCallersOnly]
    private static void TurnOnSkyBox(nint self)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneSkyOff[s.Index] = false;
        SceneFeed.SkyChanged(s);
    }

    [UnmanagedCallersOnly]
    private static void TurnOffSkyBox(nint self)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneSkyOff[s.Index] = true;
        SceneFeed.SkyChanged(s);
    }

    /// <summary>Slot 48, <c>Scene::ClearSkyBox</c> @0x55e2f0. It zeroes the blend and sets the OFF byte and
    /// does NOT forget the four objects -- which is why a cleared sky can be turned back on without the area
    /// re-supplying them. Dropping them here (as this slot first did) loses the sky permanently.</summary>
    [UnmanagedCallersOnly]
    private static void ClearSkyBox(nint self)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneSkyT[s.Index] = 0f;
        SceneDb.SceneSkyOff[s.Index] = true;
        SceneFeed.SkyChanged(s);
    }

    // ---- the one by-value return --------------------------------------------------------------------

#if WINDOWS
    /// <summary>Slot 39 on Win64: <c>(this=rcx, sret=rdx, obj=r8)</c>, and the buffer's address comes back in
    /// rax as well (<c>mov %rdx,%rax</c> in every one of the engine's own by-value returns).</summary>
    [UnmanagedCallersOnly]
    private static nint GetLightingValueAtFocusWin(nint self, Vec3* ret, nint obj)
    {
        Vector3 c = LightingAtFocus(Id(self), obj);
        if (ret != null) { ret->X = c.X; ret->Y = c.Y; ret->Z = c.Z; }
        return (nint)ret;
    }
#else
    /// <summary>Slot 39 on SysV: no hidden pointer at all -- a 12-byte all-float aggregate is classified SSE
    /// in both eightbytes and comes back in xmm0:xmm1, so <c>this</c> stays in rdi and the object in rsi.</summary>
    [UnmanagedCallersOnly]
    private static Vec3 GetLightingValueAtFocusSysV(nint self, nint obj)
    {
        Vector3 c = LightingAtFocus(Id(self), obj);
        return new Vec3 { X = c.X, Y = c.Y, Z = c.Z };
    }
#endif

    /// <summary>The light at the scene's focus. <b>The engine's own body @0x562aa0 does something else</b>: it
    /// REGISTERS the object as <c>m_pFocusObject</c> and returns one global <c>focuslighting</c> value for the
    /// whole scene. CX answers per object instead -- a listed difference from vanilla, and the three callers
    /// only tint a GUI portrait.</summary>
    private static Vector3 LightingAtFocus(DbId scene, nint obj)
    {
        if (scene.IsNull) return Vector3.Zero;
        DbId o = Facade.ObjectId(obj);
        return SceneFeed.LightingAt(scene, o);
    }

    // ---- debug lines --------------------------------------------------------------------------------

    /// <summary>Slot 40, <c>Scene::AddLinesToRender(List&lt;Vector&gt;, List&lt;unsigned int&gt;,
    /// List&lt;int&gt;)</c>. The three arguments are <c>List</c> OBJECTS passed by value, each an (array,
    /// count) pair; taken as three opaque pointers and read through <see cref="DbLineList"/>, whose layout is
    /// the engine's measured one.</summary>
    [UnmanagedCallersOnly]
    private static void AddLinesToRender(nint self, nint points, nint colours, nint strips)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        DbLines lines = SceneDb.SceneLines[s.Index] ??= new DbLines();
        var p = new DbLineList(points);
        var c = new DbLineList(colours);
        var k = new DbLineList(strips);
        lines.Set((Vector3*)p.Items, (uint*)c.Items, (int*)k.Items, p.Count, k.Count);
        SceneDb.TouchScene(s, Dirty.Visible);
        // ponytail: on Win64 the callee owns a by-value class parameter's copy, so strictly these three
        // arrays should be freed here. No game-layer caller of this slot exists on 8193.37-17 (the only
        // mention of AddLinesToRender outside Scene.c is none), so the leak cannot be reached; if a caller
        // ever appears, free each List's array under #if WINDOWS.
    }

    [UnmanagedCallersOnly]
    private static void ClearLinesToRender(nint self)
    {
        DbId s = Id(self);
        if (s.IsNull) return;
        SceneDb.SceneLines[s.Index]?.Clear();
        SceneDb.TouchScene(s, Dirty.Visible);
    }

    // ---- the AABB debug draw ------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static void SetRenderAABB(nint self, byte on)
    {
        DbId s = Id(self);
        if (!s.IsNull) SceneDb.SceneRenderAabb[s.Index] = on != 0;
    }

    [UnmanagedCallersOnly]
    private static byte GetRenderAABB(nint self)
    {
        DbId s = Id(self);
        return (byte)(!s.IsNull && SceneDb.SceneRenderAabb[s.Index] ? 1 : 0);
    }

    private static Vector3 V(Vec3* p) => p == null ? Vector3.Zero : new Vector3(p->X, p->Y, p->Z);

    internal static string Stats(int _)
        => $"scene slots: {_calls} calls" + (_nullScene > 0 ? $", {_nullScene} on a DEAD scene handle" : "");
}

/// <summary>The engine's <c>List&lt;T&gt;</c> as the three <c>AddLinesToRender</c> arguments carry it: an array
/// pointer at +0 and an element count at +8, measured in <c>Scene::AddLinesToRender</c> @0x573200, which reads
/// all three that way. Each list arrives by invisible reference on both OSes (a non-trivial destructor puts the
/// aggregate in MEMORY class), so one signature serves.</summary>
internal readonly unsafe struct DbLineList
{
    private readonly nint _p;
    public DbLineList(nint p) => _p = p;
    public nint Items => _p == 0 ? 0 : *(nint*)_p;
    public int Count => _p == 0 ? 0 : *(int*)(_p + 8);
}
