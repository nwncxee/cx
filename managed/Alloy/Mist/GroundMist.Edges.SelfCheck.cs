using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The field's edges in time and in the record tables: a new area's first upload, the bytes the fog stage
/// decodes, the lowest fog detail, a recycled creature record, a late tile, the live walk and an emitter's sprites.</summary>
internal static partial class GroundMistSelfCheck
{
    private static void Edges(Checker check)
    {
        NextUpload(check);
        Decodes(check);
        LowDetail(check);
        Recycled(check);
        LateTile(check);
        LiveWalk(check);
        Sprites(check);
    }

    /// <summary>A new area's field goes up on its first frame, even when the old area's field changed as often.</summary>
    private static void NextUpload(Checker check)
    {
        var into = new byte[GroundMist.PackedBytes];
        int gen = int.MinValue;
        Fogged(Flat(), Vector3.Zero);
        GroundMist.Pack(ref gen, into);
        Fogged(Flat(), new Vector3(100f, 0f, 0f));
        int sent = GroundMist.Pack(ref gen, into);
        float corner = MemoryMarshal.Read<Vector4>(into).X;
        check.Expect(sent == GroundMist.PackedBytes && corner == GroundMist.CornerFor(100f),
            $"the next area's first frame uploads {sent} bytes with its corner at x {corner} (want {GroundMist.PackedBytes} at {GroundMist.CornerFor(100f)})");
    }

    /// <summary>The upload as the fog stage decodes it: the head, a floored column, a wall's column, and the head alone once mist stops showing.</summary>
    private static void Decodes(Checker check)
    {
        var into = new byte[GroundMist.PackedBytes];
        int gen = int.MinValue;
        Fogged(Ground(20f, 1f, (_, _) => 0f, (x, _) => x >= 4f && x < 6f ? Nonwalk : Stone), Vector3.Zero);
        int sent = GroundMist.Pack(ref gen, into);
        Vector4 head = MemoryMarshal.Read<Vector4>(into);
        Span<GroundMist.Cell> cells = MemoryMarshal.Cast<byte, GroundMist.Cell>(into.AsSpan(GroundMist.Head));
        int side = GroundMist.Side, ox = (int)head.X, oy = (int)head.Y;
        Span<GroundMist.Cell> row = cells.Slice(-oy * side, side);
        GroundMist.Cell ground = row[-ox], wall = row[4 - ox];
        int hidden;
        float shows;
        CX.Alloy.Air.Enabled.V = false;
        try
        {
            GroundMist.Step(Frame, Vector2.Zero);
            hidden = GroundMist.Pack(ref gen, into);
            shows = MemoryMarshal.Read<Vector4>(into).W;
        }
        finally { CX.Alloy.Air.Enabled.V = true; }
        check.Expect(sent == GroundMist.PackedBytes && head == new Vector4(GroundMist.CornerFor(0f), GroundMist.CornerFor(0f), side, 1f)
                && ground.Floor == 0f && ground.Mist == 0f && wall.Floor < -1e8f && wall.Mist == 0f && hidden == GroundMist.Head && shows == 0f,
            $"the upload is {sent} bytes, head {head}, ground {ground}, wall {wall}, then {hidden} bytes showing {shows} with the air off (want the whole field, a shown head at the corner, the area's floor at 0 m with no mist the area's fog laid, no floor and no mist on the wall, then the head alone showing 0)");
    }

    /// <summary>At the lowest fog detail the mist lies still: a walker and a blast leave it as it was.</summary>
    private static void LowDetail(Checker check)
    {
        string quality = VolumetricFog.Quality.V;
        VolumetricFog.Quality.V = VolumetricFog.Levels[0];
        try
        {
            Fogged(Flat(), Vector3.Zero);
            float floor = GroundMist.At(0.5f, 0.5f);
            GroundMist.Part(new Vector3(0f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f));
            float parted = GroundMist.At(0.5f, 0.5f);
            GroundMist.Blast(new Vector3(20f, 0.5f, 0f), 5f);
            float blasted = GroundMist.At(20.5f, 0.5f);
            check.Expect(parted == floor && blasted == floor,
                $"at the lowest detail a walker leaves {parted:F4} m and a blast {blasted:F4} m of the {floor:F4} m floor (want it untouched)");
        }
        finally { VolumetricFog.Quality.V = quality; }
    }

    /// <summary>A creature seated in a recycled record parts nothing on its first step, wherever the record's last tenant stood.</summary>
    private static void Recycled(Checker check)
    {
        Fogged(Flat(), Vector3.Zero);
        DbId obj = SceneDb.Objects.Alloc();
        int o = obj.Index;
        try
        {
            SceneDb.ObjMistPrev[o] = new Vector3(0f, 0.5f, 0f);
            DbFeed.InitObject(o);
            SceneDb.ObjFlags[o] = ObjFlag.Creature;
            SceneDb.ObjWorld[o] = Matrix4x4.CreateTranslation(0.5f, 0.5f, 0f);
            DbAnim.MovedMist(o);
        }
        finally
        {
            SceneDb.ObjFlags[o] = 0;
            SceneDb.Objects.Free(obj);
        }
        float fresh = GroundMist.At(0.5f, 0.5f);
        check.Expect(fresh == AreaDepth, $"a creature in a recycled record parts {AreaDepth - fresh:F4} m on its first step (want none)");
    }

    /// <summary>A tile seated after the field was laid floors its columns on the next follow, the player standing still.</summary>
    private static void LateTile(Checker check)
    {
        Lay(Flat(), new Vector3(2f, 2f, 1f), false, 0f);
        var deck = new System.Collections.Generic.List<Tile>();
        Deck(deck, 0f, 0f, 4f, 4f, 1f, Stone);
        GroundMist.TakeWalk(deck[0].Mesh, deck[0].World);
        GroundMist.Follow(new Vector3(2f, 2f, 1f));
        float floor = GroundMist.FloorAt(2.5f, 2.5f);
        check.Expect(floor == 1f, $"a deck seated after the field was laid floors at {floor} m under a player who did not move (want 1 m)");
    }

    /// <summary>The live frame walks the scene's tile parts once, keeps the sources queued before a new scene's first
    /// frame, and a frame of the next scene floors only on that scene's tiles.</summary>
    private static void LiveWalk(Checker check)
    {
        try
        {
            DbId a = HitTestCheck.Fixture.NewScene(), b = HitTestCheck.Fixture.NewScene();
            SeatTiles(a, 0f);
            SeatTiles(b, 1f);
            var src = new Vector3(0.5f, 0.5f, 0f);
            GroundMist.Reset();
            GroundMist.Frame(a, 101, Vector3.Zero, false, 0f, Frame, Vector2.Zero);
            float onA = GroundMist.FloorAt(0.5f, 0.5f);
            GroundMist.Source(src with { Z = 1f }, 3f);
            GroundMist.Frame(b, 102, new Vector3(0f, 0f, 1f), false, 0f, 1f, Vector2.Zero);
            float onB = GroundMist.FloorAt(0.5f, 0.5f), laid = GroundMist.At(0.5f, 0.5f);
            check.Expect(onA == 0f && onB == 1f && laid > 0f,
                $"the live walk floors scene A at {onA} m and scene B at {onB} m, and B's first frame lays {laid:F4} m from a source queued before it (want 0, 1 and some)");
        }
        finally { HitTestCheck.Fixture.Tear(); }
    }

    /// <summary>Seat a 40 m square of flat stone tiles at <paramref name="z"/> in <paramref name="scene"/>, as the area's tile parts.</summary>
    private static void SeatTiles(DbId scene, float z)
    {
        foreach (Tile t in Ground(20f, 2f, (_, _) => z, (_, _) => Stone))
        {
            DbId o = HitTestCheck.Fixture.Obj(scene, "mistwalk", 0, true, ObjFlag.Tile, t.Mesh);
            int p = SceneDb.ObjPartFirst[o.Index];
            SceneDb.PartKindOf[p] = PartKind.Aabb;
            SceneDb.PartMeshDef[p] = t.Mesh;
            SceneDb.PartWorld[p] = t.World;
        }
    }

    /// <summary>A fog emitter's sprites already in the air go when the air turns on and the mist takes it.</summary>
    private static void Sprites(Checker check)
    {
        CX.Alloy.Air.Enabled.V = false;
        try
        {
            Lay(Flat(), Vector3.Zero, false, 0f);
            int[,] live = EmitterSim.MistEmit("silm_fog_50_lo", new[] { "AuroraEmitter01" }, new[] { new Vector3(3f, 0f, 0.5f) }, 300,
                f => { if (f == 150) CX.Alloy.Air.Enabled.V = true; });
            int before = live[0, 149], after = live[0, 299];
            check.Expect(before > 0 && after == 0, $"a fog emitter holds {before} sprites with the air off, then {after} once it turns on (want some, then none)");
        }
        finally { CX.Alloy.Air.Enabled.V = true; }
    }
}
