using System.Collections.Generic;

namespace CX.Services.Anim;

internal static unsafe partial class RigMap
{
    /// <summary>Seats <paramref name="rows"/> as this module's map, forgetting every resolved rig and head, and hands
    /// back the map it replaced: a self-check names its own skeletons where <see cref="Of"/> reads them.</summary>
    internal static Dictionary<string, string[]>? SwapRows(Dictionary<string, string[]>? rows)
    {
        Edge();
        Dictionary<string, string[]>? was = _s.Rows;
        _s.Rows = rows;
        _s.Rigs.Clear();
        _s.Heads.Clear();
        return was;
    }
}
