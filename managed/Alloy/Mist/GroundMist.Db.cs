using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;

namespace CX.Alloy;

internal static partial class GroundMist
{
    /// <summary>A column with no floor, as the fog stage reads it: far below anything, so nothing stands over it.</summary>
    private const float NoFloor = -1e9f;

    /// <summary>Frame <paramref name="frame"/> of the live field in <paramref name="scene"/>: the tile walkmeshes seated
    /// since the last, the field recentred on <paramref name="feet"/>, the area's fog, then <paramref name="dt"/>
    /// seconds of it. A new scene starts a fresh field that keeps the sources queued before its first frame. Once per
    /// frame: a second view of the same frame changes nothing.</summary>
    internal static void Frame(DbId scene, int frame, Vector3 feet, bool fogOn, float fogAmount, float dt, Vector2 wind)
    {
        GroundMistState s = _s;
        if (s.Scene != scene.Value)
        {
            if (s.Scene != 0)
            {
                GroundMistState old = s;
                Reset();
                s = _s;
                (s.Source, s.Sources) = (old.Source, old.Sources);
            }
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

    /// <summary>Write the field and the air's dust and mist for the fog stage into <paramref name="into"/>: a head (the
    /// corner, the side, 1 while either shows; the <paramref name="sky"/>'s fog colour and the mist's height), then a
    /// <see cref="Cell"/> per column. Returns the bytes written, 0 when <paramref name="gen"/> says the stage already
    /// has them; with nothing showing, the head alone.</summary>
    internal static int Pack(ref int gen, Span<byte> into, Vector3 sky = default)
    {
        GroundMistState s = _s;
        bool mist = Shows() && s.Laid, dust = AirScene.Lies && s.Laid, shows = mist || dust;
        if (dust && s.AirGen != AirScene.Gen) { s.AirGen = AirScene.Gen; s.Gen++; }
        if (sky != s.Sky) { s.Sky = sky; s.Gen++; }
        int now = shows ? s.Gen : -1;
        if (now == gen) return 0;
        gen = now;
        MemoryMarshal.Write(into, new Vector4(s.Ox, s.Oy, Side, shows ? 1f : 0f));
        MemoryMarshal.Write(into[(Head / 2)..], new Vector4(sky, AirScene.MistTop));
        if (!shows) return Head;
        Span<Cell> cells = MemoryMarshal.Cast<byte, Cell>(into[Head..]);
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int c = y * Side + x;
                float wx = s.Ox + x + 0.5f, wy = s.Oy + y + 0.5f;
                (float lies, float top) = dust ? AirScene.Shown(wx, wy) : (0f, 0f);
                float wet = dust ? AirScene.MistAt(wx, wy) : 0f;
                cells[c] = new Cell(float.IsNaN(s.Floor[c]) ? NoFloor : s.Floor[c], wet, lies, top);
            }
        return PackedBytes;
    }

    /// <summary>One column as the fog stage reads it: its floor, its mist's depth, and its dust's depth and the height
    /// that dust reaches over the floor, metres.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Cell(float Floor, float Mist, float Dust, float Top);

    /// <summary>The bytes of the field's head, of one <see cref="Cell"/>, and of the whole field as <see cref="Pack"/> writes it.</summary>
    internal const int Head = sizeof(float) * 8, CellBytes = sizeof(float) * 4, PackedBytes = Head + Side * Side * CellBytes;
}
