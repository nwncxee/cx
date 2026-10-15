using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using static CX.Framework.EngineLayout;

namespace CX.Services.Camera;

/// <summary>One segment test through the engine's own <c>Scene::HitCheck</c> -- tile BSP parts then gobs, nearest
/// hit wins -- with the one <c>CHitInfo</c> its users share. The record is allocated once and never moves: a hit
/// links it into the gob's SafePointer list, so <see cref="Release"/> must unlink it before the frame ends; no
/// allocation per cast. With Alloy on it routes through <see cref="HitTest"/>: the engine's would run over a facade.</summary>
public static unsafe class SceneRay
{
    private static SceneHitCheckFn _hitCheck;
    /// <summary>Resolved once: a cast runs per creature per frame and up to 15 times per spring arm.</summary>
    private static HitInfoResetFn _reset;
    private static IntPtr* _scene;
    private static int* _statics;
    /// <summary>The CHitInfo, then one ignore-list slot after it.</summary>
    private static byte* _info;

    /// <summary>The engine's own nudge past a hit before re-casting (UpdateNormalCameraCollision's 0.001).</summary>
    public const float Nudge = 0.001f;

    public static bool Armed => CX.Alloy.Renderer.On || _info != null;

    public static void Init()
    {
        if (CX.Alloy.Renderer.On || _info != null) return;
        _hitCheck = EngineFn.SceneHitCheck;
        _reset = EngineFn.HitInfoReset;
        _scene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _statics = (int*)Native.Sym(SymbolMap.HitcheckStaticTrimesh);
        if (_hitCheck == null || _reset == null || _scene == null) return;
        _info = (byte*)NativeMemory.AllocZeroed((nuint)sizeof(HitInfoBlock));
    }

    /// <summary>The world scene being animated/rendered, or zero (no area, or a GUI panel's scene).</summary>
    public static IntPtr WorldScene()
    {
        if (CX.Alloy.Renderer.On) return DbWorldShell();
        IntPtr s = _scene == null ? IntPtr.Zero : *_scene;
        return s == IntPtr.Zero || new Scene(s).BspRoot == IntPtr.Zero ? IntPtr.Zero : s;
    }

    /// <summary>DB mode's world scene, as the facade the game holds for it. Cached on the handle, which the
    /// generation check invalidates by itself when the area changes.</summary>
    private static IntPtr DbWorldShell()
    {
        if (!SceneDb.Scenes.Valid(_dbWorld) || !SceneDb.SceneIsWorld[_dbWorld.Index])
        {
            _dbWorld = default;
            for (int i = 1; i < SceneDb.Scenes.High; i++)
                if (SceneDb.Scenes.LiveAt(i) && SceneDb.SceneIsWorld[i]) { _dbWorld = SceneDb.Scenes.IdAt(i); break; }
        }
        if (_dbWorld.IsNull) return IntPtr.Zero;
        nint shell = SceneDb.SceneFacade[_dbWorld.Index];
        if (shell == 0 && !_noShellSaid)
        {
            _noShellSaid = true;
            HitTest.Log?.Always("SceneRay: the DB world scene has no facade shell -- the camera spring arm, the "
                              + "occlusion fade and foot placement have nothing to cast against");
        }
        return (IntPtr)shell;
    }

    private static DbId _dbWorld;
    private static bool _noShellSaid;

    /// <summary>Nearest hit on <paramref name="from"/>-&gt;<paramref name="to"/>, skipping <paramref name="ignore"/>
    /// (the followed gob). <paramref name="gob"/> is the gob hit, zero for a tile part or a static placeable.
    /// <paramref name="statics"/> also tests static placeables (a boardwalk, a rug), as the engine's own
    /// footstep cast does -- a no-op on a client where the switch did not resolve.</summary>
    public static bool Cast(IntPtr scene, Vector3 from, Vector3 to, IntPtr ignore, out Vector3 hit, out IntPtr gob, bool statics = false)
    {
        if (CX.Alloy.Renderer.On) return DbCast(scene, from, to, ignore, out hit, out gob, statics);
        var info = new CHitInfo((IntPtr)_info);
        // hit-test mode 0 = BSP parts then gobs; -0x40000001 and box 0 are the camera's own arguments
        _reset((IntPtr)_info, 0, -0x40000001L, 0);
        var block = (HitInfoBlock*)_info;
        block->IgnoreSlot = ignore;
        info.IgnoreList = (IntPtr)(&block->IgnoreSlot);
        info.IgnoreCount = 1;
        Vec3 h;
        // Both ends of the segment cross BY VALUE: the locals are what VectorArg points Win64 at.
        Vec3 a = new() { X = from.X, Y = from.Y, Z = from.Z }, b = new() { X = to.X, Y = to.Y, Z = to.Z };
        int* sw = statics ? _statics : null;
        int was = sw == null ? 0 : *sw;
        if (sw != null) *sw = 1;
        int r = _hitCheck(scene, VectorArg.Of(&a), VectorArg.Of(&b), &h, (IntPtr)_info);
        if (sw != null) *sw = was;
        hit = new Vector3(h.X, h.Y, h.Z);
        gob = r == 0 ? IntPtr.Zero : info.Gob;
        return r != 0;
    }

    /// <summary>The ignore list, one slot, allocated once: a <see cref="HitQuery"/> carries it by reference and
    /// the spring arm casts fifteen times a frame.</summary>
    private static readonly DbId[] _dbIgnore = new DbId[1];

    /// <inheritdoc cref="Cast"/>
    private static bool DbCast(IntPtr scene, Vector3 from, Vector3 to, IntPtr ignore,
                              out Vector3 hit, out IntPtr gob, bool statics)
    {
        hit = default; gob = IntPtr.Zero;
        DbId s = Facade.SceneId(scene);
        if (s.IsNull) return false;
        HitQuery q = HitQuery.Ray(s, from, to, statics);
        _dbIgnore[0] = Facade.ObjectId(ignore);
        if (!_dbIgnore[0].IsNull) { q.Ignore = _dbIgnore; q.IgnoreCount = 1; }
        if (!HitTest.Cast(q, out HitResult r)) return false;
        hit = r.Point;
        // The callers speak in facade pointers (IsCreature / IsPlaceable read the shell's mirrored
        // Gob+externalType), so the hit object goes back out as its shell, as a static hit goes back as zero.
        gob = r.Obj.IsNull ? IntPtr.Zero : (IntPtr)Facade.ObjectShell(r.Obj);
        return true;
    }

    /// <summary>True when <paramref name="gob"/> is a creature's (never a camera obstacle, like the engine).</summary>
    public static bool IsCreature(IntPtr gob) => gob != IntPtr.Zero && new Gob(gob).ExternalType == GobKindCreature;
    public static bool IsPlaceable(IntPtr gob) => gob != IntPtr.Zero && new Gob(gob).ExternalType == GobKindPlaceable;

    /// <summary>Is the hit object tile geometry -- ground, for a floor cast? The engine answers it with the
    /// gob's tile-data pointer, which a facade shell does NOT mirror: in DB mode that offset reads zero and
    /// every tile gob would be classed "cast past". The database's own answer is the record's
    /// <see cref="ObjFlag.Tile"/> bit, written when the placement is fed.</summary>
    public static bool IsTile(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return false;
        if (!CX.Alloy.Renderer.On) return !new Gob(gob).TileData.IsNull;
        DbId id = Facade.ObjectId(gob);
        return !id.IsNull && (SceneDb.ObjFlags[id.Index] & ObjFlag.Tile) != 0;
    }

    /// <summary>Unlink the SafePointer the last hit left behind -- now, not when the gob dies mid-frame. Nothing
    /// to unlink in DB mode: a <see cref="HitResult"/> is a generation-checked handle, not a linked pointer.</summary>
    public static void Release() { if (!CX.Alloy.Renderer.On) _reset((IntPtr)_info, 0, -0x40000001L, 0); }
}
