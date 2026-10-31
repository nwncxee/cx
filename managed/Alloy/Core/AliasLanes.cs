namespace CX.Alloy;

/// <summary>
/// The alias lanes: render-size targets that share memory, each lane's members in frame order. A member lives from
/// its first write to its last read inside one frame, no two in a lane live at once, none is read by a later frame,
/// and none is reached by the async queue; a member written in place of another starts from nothing.
/// </summary>
internal static class AliasLanes
{
    /// <summary>G-buffer 0 (G-buffer to deferred), the a2c colour, the grass ground copy, the SSR resolve, the OIT
    /// accumulation, the water colour copy, post stack 0.</summary>
    internal const string Wide = "lane wide";
    /// <summary>G-buffer 1, the a2c depth, the SSR hi-Z, the OIT revealage, post stack 1.</summary>
    internal const string Second = "lane second";
    /// <summary>The SSR normal record (G-buffer to reflections), the water depth copy, the velocity.</summary>
    internal const string Record = "lane record";
    /// <summary>G-buffer 2, the SSR trace, the SMAA edges.</summary>
    internal const string Narrow = "lane narrow";
    /// <summary>The SSR weight record (G-buffer to reflections), bloom level 0, the SMAA weights. Apart from
    /// <see cref="Narrow"/>: the weight pass reads the edges while it writes the weights.</summary>
    internal const string Weight = "lane weight";
    /// <summary>The raw AO before its blur, the shadow record key, the SSR colour chain.</summary>
    internal const string Small = "lane small";
}
