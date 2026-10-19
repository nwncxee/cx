using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The words a <c>CAurScene</c> facade shell carries at the ENGINE's offsets, kept current because
/// game code reads them with no vtable getter: <c>+0x130</c>/<c>+0x138</c> are the member list's array pointer
/// and count (<c>GetAurSceneObjectCount</c> @0x68b380 reads +0x138 raw). The mirror holds object-shell
/// POINTERS, rebuilt on cold edges only; the <c>+0x08</c>/<c>+0x10</c>/<c>+0x14</c> SafePointers are the game's.</summary>
public static unsafe class SceneShell
{
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Per scene, the native array behind <see cref="ObjListPtr"/> and its capacity in entries.</summary>
    private static readonly nint[] _mirror = new nint[SceneDb.MaxScenes];
    private static readonly int[] _mirrorCap = new int[SceneDb.MaxScenes];

    /// <summary>Per scene, "the native array no longer matches the list".</summary>
    private static readonly bool[] _stale = new bool[SceneDb.MaxScenes];

    /// <summary>The scene's member list changed (a join, a leave, or a <c>RenderGob*</c> move). O(1): write
    /// the COUNT word, which is the only one anything reads, and mark the array stale -- rebuilding it here
    /// makes an area load O(N^2) over 1155 tiles, and the one game-layer reader of
    /// <c>GetAurSceneObjectCount</c> compares the count against 1 and never walks the array.</summary>
    public static void CountChanged(DbId scene)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int i = scene.Index;
        _stale[i] = true;
        nint shell = SceneDb.SceneFacade[i];
        if (shell == 0) return;
        new CX.Engine.Scene(shell).GobCount = SceneDb.SceneObjects[i]?.Count ?? 0;
    }

    /// <summary>Bring the native array at <c>Scene+0x130</c> up to date if anything is about to read it.
    /// Called from the <c>GetAurSceneObjectCount</c> hook, because the game's one array walk follows its
    /// count call immediately.</summary>
    public static void Flush(DbId scene)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int i = scene.Index;
        if (!_stale[i]) return;
        _stale[i] = false;
        DbList? m = SceneDb.SceneObjects[i];
        int n = m?.Count ?? 0;
        if (n > _mirrorCap[i])
        {
            int want = n < 16 ? 16 : n * 2;
            nint grown = (nint)NativeMemory.Realloc((void*)_mirror[i], (nuint)want * (nuint)sizeof(nint));
            if (grown == 0)
            {
                _log?.Always($"SceneShell: could not grow scene {i}'s member mirror to {want} entries -- "
                           + "the game's direct read of the scene object list will be SHORT");
                return;
            }
            _mirror[i] = grown;
            _mirrorCap[i] = want;
        }
        nint* a = (nint*)_mirror[i];
        for (int k = 0; k < n; k++)
        {
            DbId o = m![k];
            a[k] = SceneDb.Objects.Valid(o) ? SceneDb.ObjFacade[o.Index] : 0;
        }
        nint shell = SceneDb.SceneFacade[i];
        if (shell == 0) return;
        new CX.Engine.Scene(shell).Gobs = _mirror[i];
        new CX.Engine.Scene(shell).GobCount = n;
    }

    /// <summary>The shell was just minted: write every mirrored word at once.</summary>
    public static void Refresh(DbId scene) { CountChanged(scene); Flush(scene); }

    /// <summary>The scene record is dying: hand the mirror back, after nulling the two shell words that point
    /// at it. The order matters -- the shell outlives this call by its quarantine, and a shell left naming a
    /// freed array would hand a late <c>GetAurSceneObjectCount</c> a dangling pointer instead of a zero count,
    /// which is the one failure the poison table cannot describe.</summary>
    public static void Release(DbId scene)
    {
        int i = scene.Index;
        if ((uint)i >= SceneDb.MaxScenes || _mirror[i] == 0) return;
        nint shell = SceneDb.SceneFacade[i];
        if (shell != 0)
        {
            new CX.Engine.Scene(shell).Gobs = 0;
            new CX.Engine.Scene(shell).GobCount = 0;
        }
        NativeMemory.Free((void*)_mirror[i]);
        _mirror[i] = 0;
        _mirrorCap[i] = 0;
    }
}
