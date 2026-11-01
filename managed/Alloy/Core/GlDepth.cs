using CX.Rhi;

namespace CX.Alloy;

/// <summary>GL's depth convention, which casters, shadow cascades and the GUI keep (near 0, far 1). Only the files
/// CX0006 lists may name it; the world's own convention is <see cref="WorldDepth"/>.</summary>
internal static class GlDepth
{
    /// <summary>The test a fragment at or nearer than the stored depth passes.</summary>
    internal const CompareOp NearerOrEqual = CompareOp.LessEqual;

    /// <summary>What a GL-depth target is cleared to: the far plane.</summary>
    internal const float Clear = 1f;
}
