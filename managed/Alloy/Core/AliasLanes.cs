namespace CX.Alloy;

/// <summary>
/// The alias lanes: render-size targets that share memory, each lane sized to its largest live member. A member lives
/// from its first write to its last read inside one frame, no two in a lane live at once, none is read by a later
/// frame, and none is reached by the async queue; a member written in place of another starts from nothing.
/// </summary>
internal static class AliasLanes
{
    /// <summary>A pass's widest colour target, handed to the pass after it.</summary>
    internal const string Wide = "lane wide";
    /// <summary>The second target of a pass that writes a pair of them at once.</summary>
    internal const string Second = "lane second";
    /// <summary>What a pass records about the surfaces themselves for a later pass to read: normals, a depth copy,
    /// motion.</summary>
    internal const string Record = "lane record";
    /// <summary>A target of fewer channels than the lanes above: what a pass traces or detects into.</summary>
    internal const string Narrow = "lane narrow";
    /// <summary>What a pass writes while it reads the <see cref="Narrow"/> one, and the chains that start where one
    /// left off: the two cannot share a lane.</summary>
    internal const string Weight = "lane weight";
    /// <summary>A target smaller than the render size: the reduced chains and keys.</summary>
    internal const string Small = "lane small";
}
