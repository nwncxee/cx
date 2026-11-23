using System.Numerics;

namespace CX.Alloy;

/// <summary>Which emitters Alloy draws as ground mist: every emitter of a stock fog model, picked by model resref, and
/// in any other model a node whose name starts with <c>cxmist</c>, any case. ponytail: a seam; takes none yet.</summary>
internal static class MistEmitters
{
    /// <summary>One frame of the emitter <paramref name="node"/> of <paramref name="model"/> at <paramref name="at"/>:
    /// true when the fog takes it as mist, which it then feeds, and its sprites do not draw.</summary>
    internal static bool Take(string model, string node, Vector3 at, float reach) => false;
}
