using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The air's columns follow the mist grid as it re-centres on the player or lays its floors for a climb: what
/// they held moves with the ground under it, and a column new to the grid lies as an area entered there would.</summary>
internal static partial class AirScene
{
    /// <summary>Lay the columns over the mist grid's floors as they now stand. A column that keeps its floor keeps its
    /// dust, mist, lift and gap; every other one lies at rest. Once a re-centre; allocates nothing.</summary>
    private static void Recentre(AirSceneState s)
    {
        s.Lays = GroundMist.Lays;
        if (!GroundMist.Corner(out int ox, out int oy, out Vector2 feet)) return;
        int dx = ox - s.Ox, dy = oy - s.Oy;
        Shift(s.Floor, dx, dy, float.NaN);
        Shift(s.Dust, dx, dy, float.NaN);
        Shift(s.Mist, dx, dy, float.NaN);
        Shift(s.Lift, dx, dy, 0f);
        Shift(s.Gap, dx, dy, 0f);
        (s.Ox, s.Oy, s.Feet) = (ox, oy, feet);
        const int n = GroundMist.Side;
        for (int c = 0; c < s.Floor.Length; c++)
        {
            float floor = GroundMist.FloorAt(ox + c % n + 0.5f, oy + c / n + 0.5f);
            if (!(s.Floor[c] == floor)) (s.Dust[c], s.Mist[c], s.Lift[c], s.Gap[c]) = (float.NaN, float.NaN, 0f, 0f);
            s.Floor[c] = floor;
        }
        LayRest(s);
        if (s.Columns == AirLayer.Haze)
            for (int t = 0; t < s.Leaves.Trunks; t++) Haze(s, s.Leaves.TrunkAt[t], false);
        if (s.Wets) LayMistRest(s);
        for (int c = 0; c < s.Floor.Length; c++)
        {
            if (float.IsNaN(s.Dust[c])) s.Dust[c] = s.Rest[c];
            if (float.IsNaN(s.Mist[c])) s.Mist[c] = s.MistRest[c];
        }
        s.Gen++;
    }

    /// <summary>Move a column set in place by the corner's change (<paramref name="dx"/>, <paramref name="dy"/>): each
    /// column takes what lay at its world place, <paramref name="none"/> where that was off the old grid.</summary>
    private static void Shift(float[] a, int dx, int dy, float none)
    {
        const int n = GroundMist.Side;
        if (dx == 0 && dy == 0) return;
        for (int j = 0; j < n; j++)
        {
            int y = dy >= 0 ? j : n - 1 - j, py = y + dy, row = y * n, from = py * n;
            for (int i = 0; i < n; i++)
            {
                int x = dx >= 0 ? i : n - 1 - i, px = x + dx;
                a[row + x] = (uint)px < n && (uint)py < n ? a[from + px] : none;
            }
        }
    }
}
