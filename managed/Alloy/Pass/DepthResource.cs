namespace CX.Alloy;

/// <summary>
/// Whether the depth buffer <see cref="PassId.DepthPrepass"/> fills holds this pass's depth, published rather
/// than kept private to the colour pass because water's screen-space march and the ambient occlusion want
/// exactly this buffer. The buffer itself is Alloy's own depth texture (<see cref="PostChain.DepthTex"/>);
/// sampling it while the colour bracket has it attached is the feedback loop a consumer's copy avoids.
/// </summary>
internal static class DepthResource
{
    /// <summary><see cref="AlloyRenderer.PassGen"/> of the pass whose prepass last filled it; -1 = never.</summary>
    private static int _gen = -1;

    /// <summary>The prepass filled the depth attachment for pass <paramref name="gen"/>.</summary>
    internal static void Publish(int gen) => _gen = gen;

    /// <summary>Depth for <paramref name="gen"/> is present and complete.</summary>
    internal static bool Filled(int gen) => _gen == gen;

    /// <summary>The buffer's contents can no longer be trusted (context loss, the pass standing down).</summary>
    internal static void Revoke() => _gen = -1;
}
