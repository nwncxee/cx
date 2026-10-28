namespace CX.Alloy;

/// <summary>One std140 uniform block: a staging <typeparamref name="T"/> the caller writes field by field,
/// which the recording backend copies into the frame's arena. Allocates nothing after construction, so a
/// per-frame refill is GC-free.</summary>
/// <typeparam name="T">The block's staging struct, pinned to the GLSL by <see cref="Std140.Pin"/>.</typeparam>
internal sealed class UniformBlock<T> where T : unmanaged
{
    private T _stage;

    /// <summary>The staged block, written in place: <c>blk.Stage.Radius = ...</c>.</summary>
    internal ref T Stage => ref _stage;
}
