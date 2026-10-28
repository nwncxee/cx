using CX.Framework;
using CX.Graphics;
using NuiModelDoll = CX.Services.Nui.NuiModelDoll;

namespace CX.Alloy;

/// <summary>
/// A model view on a see-through background, drawn so an effect that only adds light covers what it lights. Additive draws write
/// their square's alpha along with their light, so the doll is drawn twice, over black and over white, into scratch images, and
/// <c>cx_mvsolve_f.glsl</c> reads each pixel's coverage from how much of the white the black one lacks. A view with no additive
/// draw, or with an opaque background, keeps the single pass.
/// </summary>
internal static unsafe class GuiModelSolve
{
    /// <summary>Does the run of layers drawing into one model texture, starting at <paramref name="first"/>, need the two-pass solve?
    /// <paramref name="end"/> is the layer after the run (or <paramref name="to"/>).</summary>
    internal static bool Wanted(int first, int to, NuiModelDoll.ModelSlot? slot, out int end)
    {
        uint target = GuiCapture.Layers[first].Target;
        end = first;
        bool additive = false;
        while (end < to && GuiCapture.Layers[end].Target == target)
        {
            ref GuiCapture.Layer l = ref GuiCapture.Layers[end];
            for (int di = l.First; !additive && di < l.First + l.Count; di++)
                additive = IsAdditive(in GuiCapture.Draws[di]);
            end++;
        }
        if (slot == null) return false;
        float* c = stackalloc float[4];
        NuiModelDoll.Background(slot, c);
        return additive && c[3] < 1f;
    }

    /// <summary>Does a draw add its light to what is under it, the way an effect's fire ring does? The blend factors are
    /// the engine's own, as the capture recorded them.</summary>
    internal static bool IsAdditive(in GuiCapture.Draw d) => d.Blend && d.Dst == Gl.ONE;

    /// <summary>Oracle: hand-written draws.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var add = new GuiCapture.Draw { Blend = true, Src = Gl.SRC_ALPHA, Dst = Gl.ONE };
        var over = new GuiCapture.Draw { Blend = true, Src = Gl.SRC_ALPHA, Dst = Gl.ONE_MINUS_SRC_ALPHA };
        var off = new GuiCapture.Draw { Blend = false, Dst = Gl.ONE };
        check.Expect(IsAdditive(in add), "blending onto the destination (src alpha, one) adds light");
        check.Expect(!IsAdditive(in over), "the usual over blend does not");
        check.Expect(!IsAdditive(in off), "a draw with blending off does not, whatever its stale factors");
        log.Info(check.Ok ? "GuiModelSolve.SelfCheck: passed" : "GuiModelSolve.SelfCheck: FAILED");
    }
}
