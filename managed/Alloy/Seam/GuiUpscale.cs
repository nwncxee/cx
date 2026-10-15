using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>Where a GUI view goes at the game's UI scale, asked of <c>Camera::UpscaleViewport</c> itself: its
/// table of ~40 <c>sceneGUI_*</c> names and placement modes is GUI layout policy, not render arithmetic. A
/// database camera has no engine <c>Camera</c>, so the question goes through a scratch one. GAME thread.</summary>
internal static unsafe class GuiUpscale
{
    /// <summary>Entries in the scratch vtable, every one answering <see cref="AsCAurCamera"/>. Past both slots
    /// the function calls: Linux slot 33 off <c>+0</c>, Windows slot 1 off the <c>Gob</c> sub-object.</summary>
    private const int TableSlots = 64;
    /// <summary>Scene-name bytes after <c>Scene+0xf0</c>; the longest name the function compares is 37.</summary>
    private const int NameBytes = 128;
    /// <summary>Floats <c>UpscaleViewport</c> writes: rect 0..3, mode 4, offsets 5/6, centring 7/8, scale 9.
    /// Sized above it -- a miscount here is a stack overwrite.</summary>
    private const int OutFloats = 16;
    /// <summary>The engine's own first test: a UI scale this close to 1 is no scale at all.</summary>
    private const float Epsilon = 0.09f;

    /// <summary>The scratch camera's bytes: just past the scene word, where the real camera shell for
    /// <see cref="AsCAurCamera"/> is parked. The engine function reads only the vtables and the scene word.</summary>
    private static readonly int ShellAt = EngineLayout.CameraScene + sizeof(nint);
    private static readonly nint Cam = Build(out NameBlock);
    private static readonly nint NameBlock;

    private static nint Build(out nint nameBlock)
    {
        var table = (nint*)NativeMemory.AllocZeroed((nuint)(TableSlots * sizeof(nint)));
        for (int k = 0; k < TableSlots; k++) table[k] = (nint)(delegate* unmanaged<nint, nint>)&AsCAurCamera;
        nameBlock = (nint)NativeMemory.AllocZeroed((nuint)(EngineLayout.SceneName + NameBytes));
        nint cam = (nint)NativeMemory.AllocZeroed((nuint)(ShellAt + sizeof(nint)));
        *(nint**)cam = table;
        *(nint**)new Camera(cam).GobSub = table;
        new Camera(cam).Scene = new Scene(nameBlock);
        return cam;
    }

    /// <summary>The scratch camera's <c>AsCAurCamera</c>: the real shell, which is what
    /// <c>CGuiMan::FindCameraInActivePanels</c> compares the open panels' cameras against.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCAurCamera(nint self) => new Camera(Cam).ParkedShell;

    /// <summary>Say once, at boot, that panels cannot follow the UI scale. Called from <see cref="SeamFrame"/>.</summary>
    internal static void Announce(ExtenderLog log)
    {
        if (EngineFn.CameraUpscaleViewport == null)
            log.Always("GuiUpscale: Camera::UpscaleViewport unresolved -- the game's GUI renders at UI scale 1 "
                     + "whatever the game's own GUI scale says");
    }

    /// <summary><c>UpscaleViewport</c>'s answer for this camera in this scene against the draw rect, or an
    /// invalid answer when the scale is 1, the function did not resolve, the camera has no shell, or it declined.</summary>
    internal static void Ask(DbId cam, float vx, float vy, float vw, float vh, float guiScale,
                             out DbCamera.ViewUpscale up)
    {
        up = default;
        if (MathF.Abs(guiScale - 1f) < Epsilon) return;
        var fn = EngineFn.CameraUpscaleViewport;
        if (fn == null || !SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        DbId scene = SceneDb.CamScene[i];
        if (!SceneDb.Scenes.Valid(scene) || SceneDb.CamFacade[i] == 0) { Said(i, scene, 1, in up); return; }
        byte* dst = (byte*)Unsafe.AsPointer(ref new Scene(NameBlock).Name);
        dst[SceneDb.SceneNameBytes(scene, new Span<byte>(dst, NameBytes))] = 0;
        new Camera(Cam).ParkedShell = SceneDb.CamFacade[i];
        float* vp = stackalloc float[4] { vx, vy, vw, vh };
        float* rect = stackalloc float[4] { SceneDb.CamViewX[i], SceneDb.CamViewY[i], SceneDb.CamViewW[i], SceneDb.CamViewH[i] };
        float* o = stackalloc float[OutFloats];
        for (int k = 0; k < OutFloats; k++) o[k] = 0f;
        if (fn(Cam, vp, rect, o) != 0)
        {
            up.Valid = true;
            up.X = o[0]; up.Y = o[1]; up.W = o[2]; up.H = o[3];
            up.Mode = o[4]; up.OffX = o[5]; up.OffY = o[6]; up.PickX = o[7]; up.PickY = o[8]; up.Scale = o[9];
        }
        Said(i, scene, up.Valid ? (byte)(Answered + (int)up.Mode) : Declined, in up);
    }

    /// <summary>What each camera's last answer was, by <see cref="Said"/>'s code; a change is one cx.log line.</summary>
    private static readonly byte[] _last = new byte[SceneDb.MaxCameras];
    private const byte Declined = 2, Answered = 3;

    /// <summary>Log a camera's answer when it differs from its last one: cold, a few lines per scale change.</summary>
    private static void Said(int cam, DbId scene, byte code, in DbCamera.ViewUpscale up)
    {
        if (_last[cam] == code) return;
        _last[cam] = code;
        string name = SceneDb.Scenes.Valid(scene) ? SceneDb.SceneName[scene.Index] ?? "" : "-";
        Facade.Log?.Always(code == 1 ? $"GuiUpscale: cam {cam} \"{name}\" has no scene or no shell -- drawn at UI scale 1"
                  : !up.Valid ? $"GuiUpscale: cam {cam} \"{name}\" -- UpscaleViewport declined, drawn at UI scale 1"
                  : $"GuiUpscale: cam {cam} \"{name}\" mode {up.Mode} rect ({up.X},{up.Y},{up.W},{up.H}) scale {up.Scale}");
    }
}
