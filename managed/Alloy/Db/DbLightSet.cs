using System.Numerics;
using CX.Alloy.Seam;

namespace CX.Alloy.Db;

/// <summary>The two questions the Aurora seam asks of the <b>light set</b>: the largest radius an object's
/// lights reach, and a recolour of the light one named part owns. Both are cold, never per-frame, and both go
/// through <see cref="DbInputs"/> so they see one light list however a light arrived -- a gob's from its part
/// tree, a tile's from <see cref="TileBuild"/>'s registration.</summary>
internal static class DbLightSet
{
    /// <summary><c>Gob::GetMaximumLightRadius</c> @0x43fba0: <c>CollectLights</c> over the object, then the
    /// largest <c>PartLight+0x70</c> (<c>range</c>) among them, from 0, so no lights answers 0. The radius is
    /// the LIVE one, hence <see cref="DbInputs.Light"/>: it folds the animated column over the tile record
    /// over the node's rest key, the order in which a controller would have overwritten the engine's field.</summary>
    internal static float MaxRadius(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return 0f;
        float max = 0f;
        int n = DbInputs.LightCount;
        for (int k = 0; k < n; k++)
            if (DbInputs.Light(k, out DbLight l) && l.Obj == obj && l.Radius > max) max = l.Radius;
        // ...and the Lit rows of this object that no part backs. A tile light registered before its part
        // record existed is in that table and nowhere else, and the feed's light walk reads it from there.
        for (int i = 1; i < SceneDb.Lights.High; i++)
            if (SceneDb.Lights.LiveAt(i) && SceneDb.LitObj[i] == obj && SceneDb.LitPart[i] < 0
                && SceneDb.LitRange[i] > max) max = SceneDb.LitRange[i];
        return max;
    }

    /// <summary><c>Gob::ControlPartLightColor</c> @0x4436d0: recolour the light the named part owns. The
    /// engine resolves the part with <c>Gob::GetPart(name)</c> (vtable +0x238), casts it with
    /// <c>AsPartLight</c> and installs a CONTROLLER rather than writing the colour field, so the value lands
    /// in the animated column. A name that is no part, or no light, does nothing and is counted.</summary>
    internal static void SetPartColour(DbId obj, string? part, Vector3 colour)
    {
        int p = ObjectSlots.PartByName(obj, part);
        if (p < 0) { Missed++; return; }
        if (!DbInputs.IsLight(p)) { NotALight++; return; }
        DbAnim.ControlLightColour(p, colour);
        // BOTH places the renderer reads a colour from: the animated column (an object light's path, through
        // DbInputs.Light) and the Lit record (a TILE light's, which the feed's light walk enumerates
        // directly). A recolour that wrote only one of them would take effect for one kind and not the other.
        int lit = DbInputs.LitOf(p);
        if (lit >= 0)
        {
            SceneDb.LitColour[lit] = colour;
            SceneDb.Lights.Mark(lit, Dirty.Material);
        }
        Recoloured++;
    }

    /// <summary>Recolours applied, names that resolved to nothing, and names that resolved to a part with no
    /// light -- the last two are content questions, so they are counted and reported, not refused.</summary>
    internal static long Recoloured { get; private set; }
    /// <inheritdoc cref="Recoloured"/>
    internal static long Missed { get; private set; }
    /// <inheritdoc cref="Recoloured"/>
    internal static long NotALight { get; private set; }

    /// <summary><see cref="Stats"/> as a period-line clause: empty, or prefixed with a separator.</summary>
    internal static string StatsClause()
    {
        string s = Stats();
        return s.Length == 0 ? "" : " | " + s;
    }

    internal static string Stats()
        => Recoloured == 0 && Missed == 0 && NotALight == 0
            ? ""
            : $"lightset {Recoloured} recoloured"
              + (Missed > 0 ? $", {Missed} part names not found" : "")
              + (NotALight > 0 ? $", {NotALight} named parts are not light nodes" : "")
;
}
