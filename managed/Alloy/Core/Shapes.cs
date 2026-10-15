using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>A bounding sphere as the cull stages read it: centre, then radius.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Sphere
{
    /// <summary>The centre.</summary>
    public Vector3 Center;
    /// <summary>The radius; <see cref="AlloyRenderer.NeverCull"/> exempts the owner from the cull.</summary>
    public float Radius;
}

/// <summary>An axis-aligned box: the corner with the smallest coordinates, then the one with the largest.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Box
{
    /// <summary>The low corner.</summary>
    public Vector3 Min;
    /// <summary>The high corner.</summary>
    public Vector3 Max;
}

/// <summary>One bindless handle per material slot of a draw: the diffuse, then slots 1 to 7.</summary>
[InlineArray(MaterialSlots)]
internal struct TexHandles
{
    /// <summary>Handles per draw.</summary>
    public const int MaterialSlots = 8;
    private ulong _e;
}

/// <summary>The engine texture object bound on each unit of one sky draw.</summary>
[InlineArray(AlloyRenderer.MaxEngTex)]
internal struct SkyTexUnits { private int _e; }
