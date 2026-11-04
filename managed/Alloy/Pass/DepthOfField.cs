using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The game's depth of field option on the Vulkan frame.</summary>
internal static unsafe partial class DepthOfField
{
    /// <summary>The frame's focus inputs, in the stock shader's terms: the camera, and the depths read under the mouse and ahead of it.</summary>
    internal readonly record struct Scene(float CamDist, float PitchDeg, bool Dialog, bool Cutscene, float MouseDepth,
                                          float AheadU, float AheadL, float AheadR);

    /// <summary>The sharp band in metres, and the distance the blur is measured against.</summary>
    internal readonly record struct Band(float Near, float Far, float Reference);

    internal static void Attach(ExtenderLog log) { }
    internal static void Revoke() { }
    internal static void Forget() { }

    internal static Band Focus(in Scene s) => default;
    internal static Band Ease(in Band cur, in Band target, float dt) => target;
    internal static bool Wanted => false;
    internal static void Refuse(string why, ExtenderLog log) { }
    internal static bool Refused => false;

    internal static DofCmd Command(ImageId color, ImageId depth, ImageId output, in Band band, int height, float near, float far) => default;

    internal static void Record(VkBackend vk, CommandList cl, ref ImageId hdr, ImageId depth, int w, int h, in Scene s, float dt,
                                float near, float far, float fovY, ExtenderLog log) { }

    internal static void AskDepths(VkBackend vk, CommandList cl, ImageId depth, int dw, int dh, int rw, int rh, in Scene s, int mouseX, int mouseY, float near, float far) { }
    internal static float MouseDepth => 0f;

    /// <summary>Destroys what the pass made on <paramref name="vk"/>, and forgets it.</summary>
    internal static void Drop(VkBackend vk) { }

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DepthOfField.SelfCheck");
        int skipped = Checker.Skipped;
        // the examples log a test refusal and read cx.log back: run-demo's own log, never a player's
        if (Native.Sym(SymbolMap.AppManager) != System.IntPtr.Zero) check.Skip("in a session: the depth of field examples run in run-demo");
        else Examples(check, log);
        log.Info(!check.Ok ? "DepthOfField.SelfCheck: FAILED" : Checker.Skipped > skipped ? "DepthOfField.SelfCheck: ran the rest (a skip above)" : "DepthOfField.SelfCheck: passed");
    }

    /// <summary>The approved examples that need no device.</summary>
    static partial void Examples(Checker check, ExtenderLog log);
}
