using System.Collections.Generic;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The scene audit: a live tile whose object is not a member of the tile's own scene draws nothing and
/// has no floor, with nothing else saying so. Checked when the area's geometry is finished and on every period
/// line, and named per object, because the column that disagrees is the evidence.</summary>
public static partial class TileBuild
{
    /// <summary>Audit counts and the (tile, object) pairs already named, so a stuck pair is one line.</summary>
    internal sealed class TileAuditState
    {
        internal readonly HashSet<(int Tile, uint Obj)> Named = new();
        internal int OutOfScene, NotJoined, LinesLeft = AuditLines;
    }

    /// <summary>Lines the audit writes per session; the counts go on past it.</summary>
    private const int AuditLines = 64;

    private static TileAuditState _audit = new();

    /// <summary>Every live tile whose object is missing, stamped with another scene, or absent from its scene's
    /// member list. Returns how many are out right now.</summary>
    public static int AuditScenes(string when)
    {
        TileAuditState st = _audit;
        int bad = 0;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t)) continue;
            DbId scene = SceneDb.TileScene[t], obj = SceneDb.TileObj[t];
            if (scene.IsNull) continue;   // the self-check fixtures place under no scene by design
            bool objOk = SceneDb.Objects.Valid(obj);
            DbId stamped = objOk ? SceneDb.ObjScene[obj.Index] : default;
            bool member = objOk && SceneDb.Scenes.Valid(scene)
                       && SceneDb.SceneObjects[scene.Index]?.IndexOf(obj) >= 0;
            if (objOk && stamped.Value == scene.Value && member) continue;
            bad++;
            if (st.LinesLeft <= 0 || !st.Named.Add((t, obj.Value))) continue;
            st.LinesLeft--;
            _log?.Always($"TileBuild: [audit {when}] tile {t} '{SceneDb.TileModel[t]?.Name}' cell ({SceneDb.TileGridX[t]},"
                         + $"{SceneDb.TileGridY[t]}) of scene {scene.Value} (valid={SceneDb.Scenes.Valid(scene)}): object "
                         + $"{obj.Value} valid={objOk}, stamped scene {stamped.Value} (valid={SceneDb.Scenes.Valid(stamped)}), "
                         + $"member={member} -- it draws nothing and has no floor");
        }
        st.OutOfScene = bad;
        return bad;
    }

    /// <summary>A fresh tile object that the join left outside <paramref name="scene"/>: named with what refused.</summary>
    private static void NoteNotJoined(DbId scene, DbId obj, ModelDef def)
    {
        TileAuditState st = _audit;
        st.NotJoined++;
        if (st.LinesLeft <= 0) return;
        st.LinesLeft--;
        _log?.Always($"TileBuild: [audit spawn] '{def.Name}' object {obj.Value} did NOT join scene {scene.Value} "
                     + $"(scene valid={SceneDb.Scenes.Valid(scene)}, object valid={SceneDb.Objects.Valid(obj)}); "
                     + $"its column says {SceneDb.ObjScene[obj.Index].Value} ({st.NotJoined} so far)");
    }

    /// <summary>The period line's clause: empty while every tile is where it belongs.</summary>
    private static string AuditStats()
    {
        int bad = AuditScenes("period");
        return (bad > 0 ? $", {bad} TILE OBJECTS OUT OF THEIR SCENE" : "")
             + (_audit.NotJoined > 0 ? $", {_audit.NotJoined} TILE OBJECTS NEVER JOINED" : "");
    }
}
