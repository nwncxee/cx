using CX.Framework;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// FSR 3.1 temporal upscaling on the Vulkan frame: the world renders at <c>alloy.scale</c>, FSR rebuilds it at the
/// display size before the resolve, and the resolve and the post stack then run at the display size. It replaces
/// TAA and SMAA while it draws.
/// </summary>
internal static partial class Fsr
{
    /// <summary>FSR draws the world this frame: <c>post.fsr</c> is on and nothing refused it.</summary>
    internal static bool On => false;

    /// <summary>The cx.log line naming an upscale from <paramref name="rw"/> x <paramref name="rh"/> to
    /// <paramref name="dw"/> x <paramref name="dh"/>.</summary>
    internal static string Line(int rw, int rh, int dw, int dh) => "";

    /// <summary>Stand FSR down for the session: <paramref name="why"/> said once, on cx.log and on the panel, and
    /// the frame goes back to TAA.</summary>
    internal static void Refuse(string why, ExtenderLog log) { }

    /// <summary>The <c>post</c> panel's line: why FSR is off for the session, or empty.</summary>
    internal static string PanelLine() => "";

    /// <summary>True once after the world edge: the next dispatch starts FSR's history over.</summary>
    internal static bool TakeReset() => false;

    /// <summary>World edge: the next dispatch starts FSR's history over.</summary>
    internal static void Revoke() { }

    /// <summary>Context loss: the session's refusal and history start over.</summary>
    internal static void Forget() { }

    /// <summary>Off-device: the approved examples, and FSR off leaving the frame as it was.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Fsr.SelfCheck");
        bool fsr = Post.Fsr.V, taa = Post.Taa.V;
        float scale = Renderer.Scale.V;
        int skipped = Checker.Skipped;
        try
        {
            OffSelfCheck(check);
            Examples(check, log);
        }
        finally { Post.Fsr.V = fsr; Post.Taa.V = taa; Renderer.Scale.V = scale; }
        log.Info(!check.Ok ? "Fsr.SelfCheck: FAILED" : Checker.Skipped > skipped ? "Fsr.SelfCheck: ran the rest (a skip above)" : "Fsr.SelfCheck: passed");
    }

    /// <summary>The approved examples.</summary>
    static partial void Examples(Checker check, ExtenderLog log);

    /// <summary>E3, a regression: FSR off draws the frame as before -- TAA, its 8-frame jitter, no FSR.</summary>
    private static void OffSelfCheck(Checker check)
    {
        Post.Fsr.V = false; Post.Taa.V = true; Renderer.Scale.V = 0.67f;
        check.Expect(!On, "E3: FSR off, FSR does not draw");
        check.Expect(PostChain.TaaOn, "E3: FSR off, TAA runs in the post stack");
        check.Expect(PostChain.JitterPhases(2560) == 8, $"E3: FSR off, the jitter repeats after 8 (got {PostChain.JitterPhases(2560)})");
    }

    /// <summary>One dispatch in FSR's terms from Alloy's: the jitter as the projection took it (NDC), the velocity
    /// target in Alloy's convention, and the camera.</summary>
    internal static UpscaleCmd Command(ImageId color, ImageId depth, ImageId motion, ImageId exposure, ImageId output,
                                       int rw, int rh, float jitterNdcX, float jitterNdcY, float frameMs,
                                       float near, float far, float fovY, bool reset) => default;
}
