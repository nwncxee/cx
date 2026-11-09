using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>A cube caster as its receiver samples it: the position its six faces were baked from, and the
/// far reach their depth is scaled by. Laid out as the <c>vec4</c> the shaders read, so a table of these
/// copies straight into the pass block; a reach at or under the near plane publishes the slot as inactive.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CubeCaster(Vector3 At, float Reach);
