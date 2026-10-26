using System.Numerics;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The world frame the 2D UI draws over: its camera and depth, noted after the composite and spent by the GUI
/// flush that closes the same frame. A frame with no world (a menu) has none, so the UI's scene test sits out.</summary>
internal static unsafe class UiScene
{
    /// <summary>One noted frame: its jittered view-projection, the projection's two depth terms and the depth texture
    /// (GL's name, or under <c>alloy.backend vk</c> Vulkan's image).</summary>
    private sealed class Frame
    {
        public Matrix4x4 Vp;
        public float K2z, K3z;
        public uint Depth;
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

    /// <summary>The world pass finished: <paramref name="vp"/> and <paramref name="proj"/> drew the depth at render size
    /// <paramref name="viewport"/>. Inside the bracket, after the post stack. A projection the depth terms cannot
    /// read (not a perspective) publishes nothing.</summary>
    internal static void Note(Matrix4x4* vp, Matrix4x4* proj, int* viewport) => Note(vp, proj, viewport, PostChain.DepthTex, default);

    /// <summary><see cref="Note(Matrix4x4*, Matrix4x4*, int*)"/> for the Vulkan frame, whose depth is
    /// <paramref name="depth"/>.</summary>
    internal static void NoteVk(Matrix4x4* vp, Matrix4x4* proj, int* viewport, ImageId depth)
    {
        if (depth.IsNull) { _s.Cur = -1; return; }
        Note(vp, proj, viewport, 0, depth);
    }

    private static void Note(Matrix4x4* vp, Matrix4x4* proj, int* viewport, uint depth, ImageId vkDepth)
    {
        UiSceneState s = _s;
        if (depth == 0 && vkDepth.IsNull || viewport[2] <= 0 || viewport[3] <= 0 || proj->M34 != -1f) { s.Cur = -1; return; }
        int n = s.Cur == 0 ? 1 : 0;
        Frame f = s.F[n];
        f.Vp = *vp; f.K2z = proj->M33; f.K3z = proj->M43;
        f.Depth = depth;
        f.VkDepth = vkDepth;
        s.Cur = n;
    }

    /// <summary>This frame's world, if one drew: false at a menu or after <see cref="Spent"/>.</summary>
    internal static bool Get(out Matrix4x4 vp, out Vector2 k, out uint depth)
    {
        UiSceneState s = _s;
        int c = s.Cur;
        if (c < 0) { vp = default; k = default; depth = 0; return false; }
        Frame f = s.F[c];
        vp = f.Vp; k = new Vector2(f.K2z, f.K3z); depth = f.Depth;
        return true;
    }

    /// <summary><see cref="Get"/> for the Vulkan frame: its depth image, null id with no world.</summary>
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

    /// <summary>The depth texture went with the context.</summary>
    internal static void ContextLost() => _s = new UiSceneState();
}
