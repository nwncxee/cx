namespace CX.Alloy;

/// <summary>What both halves of order-independent transparency share: the knobs the route and the resolve read,
/// the resolve's own stage, and the period line's clause. The pass itself is <c>AlloyRenderer.VkOit</c>, which
/// draws the weighted-blended pair (McGuire &amp; Bavoil 2013) -- one unsorted pass, depth-tested and writing no
/// depth, composited in one full-screen draw.</summary>
internal static class OitPass
{
    /// <summary><c>/alloy oit</c>: 0 = the sorted, depth-writing pass (A/B instrument), 1 = weighted blended,
    /// 2 = per-pixel linked lists, which only GL ever had; the pair draws for both non-zero modes.</summary>
    internal static int Mode => Renderer.Oit.V;

    /// <summary><c>/alloy oitviz</c>: 1 = revealage as grey, 2 = accumulated mean colour, 3 = accumulated weight/10.
    /// Unblended.</summary>
    internal static int Viz => Renderer.OitViz.V;

    /// <summary>The define that compiles the append block into a fragment stage, after its #version line.</summary>
    internal static string WithLists(string fs)
    {
        int v = fs.IndexOf("#version", StringComparison.Ordinal);
        if (v < 0) return fs;
        return fs.Insert(fs.IndexOf('\n', v) + 1, "#define CX_OIT_LISTS 1\n");
    }

    /// <summary>Will the blended range go through the pair this frame, as far as the route can tell before it? The
    /// route then orders that range by state, not distance.</summary>
    internal static bool OrderFree => Mode != 0;

    /// <summary>Will the resolve composite each object id once this frame? Never: Vulkan has no lists, so an
    /// object's overlapping parts thicken it as the weighted modes do.</summary>
    internal static bool Dedupes => false;

    /// <summary>Composite: colour = accum.rgb / accum.a (the weighted mean), alpha = revealage, under
    /// ONE_MINUS_SRC_ALPHA / SRC_ALPHA.</summary>
    internal static string OitResolveFs => ShaderSource.Get("cx_oitresolve_f.glsl");

    internal static string Stats(int frames) => Mode == 0 ? "oit off" : "oit on";
}
