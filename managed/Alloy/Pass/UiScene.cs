using System.Numerics;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The world frame the 2D UI draws over: its camera and depth, noted after the composite and spent by the GUI
/// flush that closes the same frame. A frame with no world (a menu) has none, so the UI's scene test sits out.</summary>
internal static unsafe class UiScene
{
    /// <summary>One noted frame: its jittered view-projection, the projection's two depth terms and the depth
    /// image.</summary>
    private sealed class Frame
    {
        public Matrix4x4 Vp;
        public float K2z, K3z;
        public ImageId VkDepth;
    }

    /// <summary>Two frames written in turn and the one last published (-1: none), so a render-thread note never hands
    /// the flush one frame's camera with another's depth. ponytail: two notes before one read can still tear.</summary>
    private sealed class UiSceneState
    {
        public readonly Frame[] F = { new(), new() };
        public volatile int Cur = -1;
    }

    private static volatile UiSceneState _s = new();

    /// <summary>The world pass finished: <paramref name="vp"/> and <paramref name="proj"/> drew <paramref name="depth"/>
    /// at render size <paramref name="viewport"/>. Inside the frame, after the post stack. A projection the depth
    /// terms cannot read (not a perspective) publishes nothing.</summary>
    internal static void NoteVk(Matrix4x4* vp, Matrix4x4* proj, int* viewport, ImageId depth)
    {
        UiSceneState s = _s;
        if (depth.IsNull || viewport[2] <= 0 || viewport[3] <= 0 || proj->M34 != -1f) { s.Cur = -1; return; }
        int n = s.Cur == 0 ? 1 : 0;
        Frame f = s.F[n];
        f.Vp = *vp; f.K2z = proj->M33; f.K3z = proj->M43;
        f.VkDepth = depth;
        s.Cur = n;
    }

    /// <summary>This frame's world, if one drew, with its depth image: false at a menu, after
    /// <see cref="Spent"/>, or for a projection the depth terms cannot read.</summary>
    internal static bool GetVk(out Matrix4x4 vp, out Vector2 k, out ImageId depth)
    {
        UiSceneState s = _s;
        int c = s.Cur;
        if (c < 0 || s.F[c].VkDepth.IsNull) { vp = default; k = default; depth = default; return false; }
        Frame f = s.F[c];
        vp = f.Vp; k = new Vector2(f.K2z, f.K3z); depth = f.VkDepth;
        return true;
    }

    /// <summary>The frame's closing GUI flush has drawn: the next frame's UI waits for its own world.</summary>
    internal static void Spent() => _s.Cur = -1;

    /// <summary>The depth image went with the device.</summary>
    internal static void ContextLost() => _s = new UiSceneState();
}
