using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;

namespace CX.Alloy;

internal static partial class GroundMist
{
    /// <summary>Frame <paramref name="frame"/> of the live field in <paramref name="scene"/>: the tile walkmeshes seated
    /// since the last, the field recentred on <paramref name="feet"/>, the area's fog, then <paramref name="dt"/>
    /// seconds of it. Once per frame: a second view of the same frame changes nothing.</summary>
    internal static void Frame(DbId scene, int frame, Vector3 feet, bool fogOn, float fogAmount, float dt, Vector2 wind)
    {
        GroundMistState s = _s;
        if (s.Scene != scene.Value)
        {
            if (s.Scene != 0) { AreaEdge(); s = _s; }
            s.Scene = scene.Value;
        }
        if (s.Seq == frame) return;
        s.Seq = frame;
        int high = SceneDb.Parts.High;
        for (int p = s.Walked; p < high; p++)
        {
            if (!SceneDb.Parts.LiveAt(p) || !SceneDb.PartEnabled[p]) continue;
            GrassField.MeshDefHas(p, scene, out bool take);
            if (take) TakeWalk(SceneDb.PartMeshDef[p]!, SceneDb.PartWorld[p]);
        }
        s.Walked = high;
        Follow(feet);
        AreaFog(fogOn, fogAmount);
        Step(dt, wind);
    }

    /// <summary>The field as the fog stage reads it, into <paramref name="into"/>, when it changed since
    /// <paramref name="gen"/>: a head (corner x, corner y, side, 1 when mist shows), then each column's floor and
    /// depth while it shows. Returns the bytes written, 0 when nothing changed.</summary>
    internal static int Pack(ref int gen, Span<byte> into)
    {
        GroundMistState s = _s;
        bool shows = Shows() && s.Laid;
        int now = shows ? s.Gen : -1;
        if (now == gen) return 0;
        gen = now;
        MemoryMarshal.Write(into, new Vector4(s.Ox, s.Oy, Side, shows ? 1f : 0f));
        if (!shows) return Head;
        Span<Vector2> cells = MemoryMarshal.Cast<byte, Vector2>(into[Head..]);
        for (int c = 0; c < s.Floor.Length; c++)
            cells[c] = new Vector2(s.Floor[c], s.Depth[c]);
        return PackedBytes;
    }

    /// <summary>The bytes of the field's head, and of the whole field as <see cref="Pack"/> writes it.</summary>
    internal const int Head = sizeof(float) * 4, PackedBytes = Head + Side * Side * sizeof(float) * 2;
}
