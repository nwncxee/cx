using System.Numerics;

namespace CX.Alloy;

/// <summary>The eviction goldens' fixture: a live record whose handle block holds one owned name, as its last staging left it.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Own the first slot past the live ones with diffuse <paramref name="gl"/>; the slot, or -1 when none is free.</summary>
    internal static int HoldFixture(uint gl)
    {
        int i = _ownCount;
        if (i >= MaxOwned || !PlaceFixture(i, 0, 0, Matrix4x4.Identity)) return -1;
        _ownCount = i + 1;
        _dTexH[i] = default;
        _dTexH[i][0] = gl;
        return i;
    }

    /// <summary>Give back the slot <see cref="HoldFixture"/> took.</summary>
    internal static void DropHoldFixture(int i)
    {
        if (i < 0) return;
        _dTexH[i] = default;
        ClearFixture(i);
        if (_ownCount == i + 1) _ownCount = i;
    }
}
