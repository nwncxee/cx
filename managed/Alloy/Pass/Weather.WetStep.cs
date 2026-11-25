using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Footsteps in weather sound of it. Stock footstepsounds.2da carries a <c>Puddles0..2</c> sound no stock
/// step can reach: <c>PlayFootstep</c> builds its column from surfacemat.2da's <c>Name</c> plus a digit, and the Puddles
/// row is named <c>Puddle</c>. A footfall on wet, open ground plays that sound and one on lying snow <c>Snow0..2</c>,
/// resolved the way <c>PlayFootstep</c> resolves its row.</summary>
internal static unsafe partial class Weather
{
    /// <summary>surfacemat.2da rows that sound dry and lie open to the weather: Dirt 1, Grass 3, Stone 4, Wood 5,
    /// Metal 10, Leaves 14, Sand 20, StoneBridge 22. ponytail: stock row ids; a server that renumbers surfacemat.2da
    /// gets the dry sound on the moved rows, read the <c>Label</c> column if one ever does.</summary>
    private const ulong DrySurfaces = (1UL << 1) | (1UL << 3) | (1UL << 4) | (1UL << 5) | (1UL << 10) | (1UL << 14) | (1UL << 20) | (1UL << 22);
    /// <summary>Wetness from which a step sounds wet: the ground reads wet well before it pools.</summary>
    private const float WetStepMin = 0.35f;
    /// <summary>Snow cover from which a step sounds of snow: the ground reads white well before it drifts.</summary>
    private const float SnowStepMin = 0.35f;
    /// <summary><c>PlayFootstep</c>'s priority groups: the player's, then a non-player's by size category.</summary>
    private const byte GroupPlayer = 0x11, GroupSmall = 0x12, GroupLarge = 0x10;   // not-an-offset: CExoSound priority group ids
    /// <summary>A CExoString: <c>{char* buf; uint len; uint cap}</c>. not-an-offset: a caller-owned value's size.</summary>
    private const int ExoSize = 16;

    internal static Knob<bool> WetSteps = null!, SnowFootsteps = null!;

    private static readonly IntPtr _soundSym = Native.Sym(SymbolMap.ExoSound), _rulesSym2 = Native.Sym(SymbolMap.Rules);
    private static IntPtr _colType, _colSize;
    private static readonly IntPtr[] _colPud = new IntPtr[3], _colSnow = new IntPtr[3];
    private static bool _wetArmed;
    /// <summary>What a footfall sounds of: the engine's own step for the surface, or a footstepsounds.2da column.</summary>
    private enum StepKind { Engine, Puddles, Snow }
    /// <summary>The weather steps taken, and every footfall the cover map sheltered, for the stats line.</summary>
    private sealed class StepCountState { public int Wet, Snow, Sheltered; }
    private static StepCountState _stepCounts = new();

    private static void DeclareWetStepKnob()
    {
        WetSteps = G.Toggle("wetsteps", "Wet footstep sounds", true, Tier.Advanced,
                            help: "Steps on rain-wet ground make the game's own puddle sound for that creature, until the ground dries. Not under a roof.");
        SnowFootsteps = G.Toggle("snowfootsteps", "Snow footstep sounds", true, Tier.Advanced,
                                 help: "Steps on lying snow make the game's own snow sound for that creature, until the snow melts. Not under a roof.");
    }

    /// <summary>Resolve what the wet step calls and build its column names once. Missing anything = the steps stay
    /// the engine's, said once.</summary>
    private static void ArmWetStep(ExtenderLog log)
    {
        if (!SymbolMap.Require(log, "Weather.WetStep", SymbolMap.C2DAGetCExoStringEntry, SymbolMap.C2DAGetINTEntry,
                               SymbolMap.AreaGetSurfaceMaterial, SymbolMap.SoundPlay3DOneShot, SymbolMap.ExoSound,
                               SymbolMap.Rules, SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor)
            || !EngineLayout.Require(log, "Weather.WetStep", "CTwoDimArrays", "CPathfindInformation")
            || EngineFn.ExoStringInit == null)
            return;
        _colType = Column("FootstepType"u8);
        _colSize = Column("SIZECATEGORY"u8);
        for (int i = 0; i < _colPud.Length; i++) { _colPud[i] = Column(StepColumnName(StepKind.Puddles, i)); _colSnow[i] = Column(StepColumnName(StepKind.Snow, i)); }
        _wetArmed = true;
        static IntPtr Column(ReadOnlySpan<byte> name)
        {
            IntPtr c = (IntPtr)NativeMemory.AllocZeroed(ExoSize);
            fixed (byte* n = name) EngineFn.ExoStringInit(c, n, uint.MaxValue);
            return c;
        }
    }

    /// <summary>What a footfall knows of the creature that took it: where and which way, its own and its appearance's
    /// footstep type (-1 unset), size category (0 unread), soft step, and its body's scene object (default: none).</summary>
    internal readonly record struct StepFacts(Vector3 At, Vector2 Facing, uint Id, bool Open, int OwnType, int AppearanceType,
                                              int Size, bool Soft, DbId Body);

    /// <summary>A footfall, from <see cref="Services.Anim.Footfall"/>'s <c>PlayFootstep</c> forward: the print and the
    /// splash, and the weather sound. True = the weather sound played, so the caller skips the engine's dry one. Cold: a
    /// few per second per walking creature.</summary>
    internal static bool Footstep(IntPtr creature)
    {
        if (_off || !Enabled.V || FrameInputs.Interior || creature == IntPtr.Zero) return false;
        var o = new CNWCObject(creature);
        Vector3 p = o.Position, f = o.Orientation;
        Vec3 at = new() { X = p.X, Y = p.Y, Z = p.Z };
        bool open = CasterPass.CoverOpen(p);
        if (!open) _stepCounts.Sheltered++;
        StepFacts s = Facts(creature, o.Id, p, new Vector2(f.X, f.Y), open);
        if (Shown(o)) StepDown(in s, TrackNow);
        int surface = open && _wetArmed && o.AreaPtr != IntPtr.Zero ? EngineFn.AreaGetSurfaceMaterial(o.AreaPtr, VectorArg.Of(&at)) : -1;
        StepKind kind = ChooseStep(open, surface);
        return kind != StepKind.Engine && StepSound(creature, o, at, kind, in s);
    }

    /// <summary>One footfall's print and splash: the one entry the hook and the self-checks share.</summary>
    internal static void StepDown(in StepFacts s, float now)
    {
        if (!Grounded(in s) && !Mounted(s.Body)) return;
        if (!OwnFeet(in s, now)) FootDown(s.At, s.Facing, s.Id, now, s.Open, Stamp(in s));
    }

    /// <summary>Whether <paramref name="body"/> carries its horse's hooves, which stand on the ground whatever footstep
    /// type the rider's appearance gives.</summary>
    private static bool Mounted(DbId body)
    {
        Span<int> hooves = stackalloc int[MountHoofNodes.Length];
        return !body.IsNull && MountHooves(body, hooves);
    }

    /// <summary>footstepsounds.2da types whose creature has no feet on the ground: none, the two wing rows and the
    /// invisible horse.</summary>
    private const int FootNone = 12, FootWingA = 9, FootWingB = 10, FootInvisibleHorse = 30;

    /// <summary>The footstepsounds.2da row an appearance reads as when the tables cannot be read: a normal walker.</summary>
    private const int FootUnread = 0;

    /// <summary>True when the creature's footstep type, its own over its appearance's, puts feet on the ground: unset
    /// (-1 both) and the no-feet rows press nothing.</summary>
    private static bool Grounded(in StepFacts s)
        => (s.OwnType != -1 ? s.OwnType : s.AppearanceType) is not (-1 or FootNone or FootWingA or FootWingB or FootInvisibleHorse);

    /// <summary>The creature's step facts, its type and size read where <c>PlayFootstep</c> reads them; a normal
    /// walker's while the step's tables are not reachable.</summary>
    private static StepFacts Facts(IntPtr creature, uint id, Vector3 p, Vector2 facing, bool open)
    {
        int own = -1, app = FootUnread, size = 0;
        bool soft = false;
        IntPtr tables = _wetArmed ? StepTables() : IntPtr.Zero;
        if (tables != IntPtr.Zero)
        {
            var t = new CTwoDimArrays(tables);
            var c = new CNWCCreature(creature);
            CPathfindInformation pf = c.Pathfind;
            int row = pf.IsNull ? -1 : pf.Appearance;
            own = c.FootstepType;
            soft = c.SoftStep == 1;
            if (row < 0) app = -1;
            else
            {
                if (EngineFn.C2DAGetINTEntry(t.Appearance, row, _colType, &app) == 0) app = -1;
                EngineFn.C2DAGetINTEntry(t.Appearance, row, _colSize, &size);
            }
        }
        return new StepFacts(p, facing, id, open, own, app, size, soft, BodyOf(creature));
    }

    /// <summary>The rules' 2DA tables a step reads; zero while the rules are not reachable.</summary>
    private static IntPtr StepTables()
    {
        IntPtr rules = _rulesSym2 == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_rulesSym2;
        return rules == IntPtr.Zero ? IntPtr.Zero : new CNWRules(rules).TwoDimArrays;
    }

    /// <summary>The scene object behind a creature's model, reached as the pose layer reaches it (its Gob, then the
    /// record behind that shell); none off the scene database's path.</summary>
    private static DbId BodyOf(IntPtr creature)
    {
        if (!Services.Anim.PoseRig.Db || creature == IntPtr.Zero) return default;
        IntPtr gob = Services.Anim.PoseService.GobOf(creature);
        return gob == IntPtr.Zero ? default : Services.Anim.PoseRig.Obj(gob);
    }

    /// <summary>The footstepsounds.2da row a step plays: the creature's own type over its appearance's, a soft step's
    /// row 3, and -1 when neither names one.</summary>
    internal static int StepRow(in StepFacts s)
    {
        int row = s.OwnType != -1 ? s.OwnType : s.AppearanceType;
        return row == -1 ? -1 : s.Soft ? SoftStepRow : row;
    }

    /// <summary>The sound priority group a step plays in: the player's own, small for size categories 1 to 3, else large.</summary>
    internal static byte StepGroup(in StepFacts s, bool player) => player ? GroupPlayer : (uint)(s.Size - 1) < 3 ? GroupSmall : GroupLarge;

    /// <summary>The footstepsounds.2da row a soft step plays.</summary>
    private const int SoftStepRow = 3;

    /// <summary>Does the game draw this creature at all? One it has faded out (invisible to the player) leaves no
    /// print and no splash, or the snow would show where it walks.</summary>
    internal static bool Shown(CNWCObject o) => o.FadeAlpha > 0f;

    /// <summary>What a footfall on <paramref name="surface"/> sounds of. Only dry-sounding ground open to the sky
    /// changes its sound.</summary>
    private static StepKind ChooseStep(bool open, int surface)
    {
        if (!open || (uint)surface >= 64 || (DrySurfaces & (1UL << surface)) == 0) return StepKind.Engine;
        if (SnowFootsteps.V && SnowShown > SnowStepMin) return StepKind.Snow;
        return WetSteps.V && WetShown > WetStepMin ? StepKind.Puddles : StepKind.Engine;
    }

    /// <summary>The footstepsounds.2da column <paramref name="kind"/> plays, one of three by <paramref name="pick"/>.</summary>
    private static ReadOnlySpan<byte> StepColumnName(StepKind kind, int pick)
        => kind == StepKind.Snow
            ? pick switch { 0 => "Snow0"u8, 1 => "Snow1"u8, _ => "Snow2"u8 }
            : pick switch { 0 => "Puddles0"u8, 1 => "Puddles1"u8, _ => "Puddles2"u8 };

    /// <summary>Play this creature's <paramref name="kind"/> step, as <c>PlayFootstep</c> would play its surface's.
    /// False = nothing to play: the engine's own step goes ahead.</summary>
    private static bool StepSound(IntPtr creature, CNWCObject o, Vec3 at, StepKind kind, in StepFacts s)
    {
        if (o.FootstepSkip != 0) return false;
        IntPtr sound = _soundSym == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_soundSym;
        IntPtr tables = StepTables();
        if (sound == IntPtr.Zero || tables == IntPtr.Zero) return false;
        var t = new CTwoDimArrays(tables);
        int row = StepRow(in s);
        if (row == -1) return false;
        if (!Cell(t.FootstepSounds, row, (kind == StepKind.Snow ? _colSnow : _colPud)[Random.Shared.Next(3)], out ResRef res)) return false;
        EngineFn.SoundPlay3DOneShot(sound, &res, VectorArg.Of(&at), 0f, StepGroup(in s, creature == PlayerIdentity.Creature()), 0, 0);
        if (kind == StepKind.Snow) _stepCounts.Snow++; else _stepCounts.Wet++;
        return true;
    }

    /// <summary>A footstepsounds.2da cell as a CResRef in <paramref name="res"/>; false when it is empty.</summary>
    private static bool Cell(IntPtr twoda, int row, IntPtr column, out ResRef res)
    {
        res = default;
        if (twoda == IntPtr.Zero) return false;
        byte* exo = stackalloc byte[ExoSize];
        NativeMemory.Clear(exo, (nuint)ExoSize);   // the engine frees a non-null buffer first
        bool ok = false;
        if (EngineFn.C2DAGetCExoStringEntry(twoda, row, column, (IntPtr)exo) != 0)
            ok = CellResRef(*(byte**)exo, out res);
        EngineFn.ExoStringDtor((IntPtr)exo);
        return ok;
    }

    /// <summary>A cell's text as a lower-case CResRef in <paramref name="res"/>; false when it names no sound, 2DA's
    /// <c>****</c> included.</summary>
    private static bool CellResRef(byte* s, out ResRef res)
    {
        res = ResRef.Of(s, lower: true);
        int n = res.Length;
        return n > 0 && !(n == 4 && res[0] == '*' && res[1] == '*' && res[2] == '*' && res[3] == '*');
    }

    private static string WetStepStats() => $", {_stepCounts.Wet} wet steps, {_stepCounts.Snow} snow steps, {_stepCounts.Sheltered} sheltered";

    /// <summary>The approved snow-step examples; implemented in <c>SnowStepApprovedExamples.cs</c>.</summary>
    static partial void SnowStepApprovedExamples(Checker check);

    /// <summary>The approved creature-print examples; implemented in <c>CreaturePrintApprovedExamples.cs</c>.</summary>
    static partial void CreaturePrintApprovedExamples(Checker check);

    /// <summary>surfacemat.2da rows the step checks stand on; oracle: the stock table.</summary>
    private const int StoneRow = 4, WaterRow = 6, CarpetRow = 9, SnowRow = 19;

    /// <summary>Runs <paramref name="body"/> with the weather on and every knob a footfall reads at its full value,
    /// on bare dry ground, and restores all of it: the player's cx.cfg is live here.</summary>
    private static void WithStepScene(Action body)
    {
        float snow = _snow, wet = _wet, ws = WetStrength.V, ss = SnowStrength.V, fill = TrackFill.V;
        bool off = _off, en = Enabled.V, wetOn = WetSteps.V, snowOn = SnowFootsteps.V, tr = Tracks.V;
        TrackTexel[]? track = _track; FootSideState sides = _side; int prints = _prints;
        _off = false; Enabled.V = true; WetSteps.V = true; SnowFootsteps.V = true; Tracks.V = true;
        WetStrength.V = 1f; SnowStrength.V = 1f; TrackFill.V = TrackFill.Default; _snow = 0f; _wet = 0f;
        try { body(); }
        finally
        {
            _snow = snow; _wet = wet; WetStrength.V = ws; SnowStrength.V = ss; TrackFill.V = fill;
            _off = off; Enabled.V = en; WetSteps.V = wetOn; SnowFootsteps.V = snowOn; Tracks.V = tr;
            _track = track; _side = sides; _prints = prints;
        }
    }

    /// <summary>The approved examples that hold before snow steps exist, so they cannot go red first. Oracle: the
    /// hand-written expected kinds, the stock table's text for an empty cell, a hand-built awning map for cover, and
    /// the stage's GLSL for its bias.</summary>
    private static void StepSelfCheck(Checker check) => WithStepScene(() =>
    {
        const int CoverSize = 64;
        Matrix4x4 coverVp = ShadowMath.DirectionalLightViewProj(new Vector3(0f, 0f, -1f), new Vector3(20f, 20f, 0f), 16f, 16f, 0);
        var coverDepth = new float[CoverSize * CoverSize];
        Vector4 ground = Vector4.Transform(new Vector4(20f, 20f, 0f, 1f), coverVp);
        Array.Fill(coverDepth, ground.Z / ground.W * 0.5f + 0.5f);
        // a 4 m high awning over x,y 16..24, written cell by cell through the matrix the map was baked with
        for (float wy = 16f; wy < 24f; wy += 0.1f)
        for (float wx = 16f; wx < 24f; wx += 0.1f)
        {
            Vector4 c = Vector4.Transform(new Vector4(wx, wy, 4f, 1f), coverVp);
            int cx = (int)((c.X / c.W * 0.5f + 0.5f) * CoverSize), cy = (int)((c.Y / c.W * 0.5f + 0.5f) * CoverSize);
            int at = cy * CoverSize + cx;
            coverDepth[at] = c.Z / c.W * 0.5f + 0.5f;
        }
        check.Expect(!CasterPass.CoverOpenIn(coverDepth, CoverSize, CoverSize, coverVp, new Vector3(20f, 20f, 0f)), "the map shelters a foot under the awning");
        check.Expect(CasterPass.CoverOpenIn(coverDepth, CoverSize, CoverSize, coverVp, new Vector3(28f, 20f, 0f)), "the map leaves ground beside the awning open");
        check.Expect(CasterPass.CoverOpenIn(coverDepth, CoverSize, CoverSize, coverVp, new Vector3(20f, 20f, 4.1f)), "the map leaves the awning's own top open");
        check.Expect(CasterPass.CoverOpenIn(coverDepth, CoverSize, CoverSize, coverVp, new Vector3(28f, 20f, -0.15f)), "a foot a little under the drawn floor still counts as open");
        var allRoof = new float[CoverSize * CoverSize];
        check.Expect(!CasterPass.CoverOpenIn(allRoof, CoverSize, CoverSize, coverVp, new Vector3(20f, 20f, 0f))
          && CasterPass.CoverOpenIn(allRoof, CoverSize, CoverSize, coverVp, new Vector3(200f, 20f, 0f)), "past the map's edge a foot never borrows the edge texel's roof");
        check.Expect(CasterPass.CoverOpenIn(coverDepth, CoverSize, CoverSize, default, new Vector3(20f, 20f, 0f)), "with no map a foot counts as open, as the stage reads it");
        check.Expect(ShaderSource.Get("cx_wx_place.glsl").Contains("n.z - " + CasterPass.CoverBias.ToString("0.000", CultureInfo.InvariantCulture)), "the footfall reads the map with the stage's own bias");

        check.Expect(ShaderSource.Get("cx_wx_shape.glsl").Contains("CX_WX_EDGE_BIAS_M = " + CasterPass.CoverLiftM.ToString("0.00", CultureInfo.InvariantCulture)), "the footfall lifts as far as the snow's own read does");
        check.Expect(CasterPass.CoverWindow(2048, 60f) == 1024 && CasterPass.CoverWindow(2048, 128f) == 752 && CasterPass.CoverWindow(2048, 16f) == 2048,
          "the map keeps for footfalls 15 m of hearing plus the walk before a re-fit, and all of a short range");
        const int CoverHalf = CoverSize / 2;
        var halfRoof = new float[CoverHalf * CoverHalf];
        check.Expect(!CasterPass.CoverOpenIn(halfRoof, CoverSize, CoverHalf, coverVp, new Vector3(20f, 20f, 0f)), "a foot inside the kept square reads it");
        check.Expect(CasterPass.CoverOpenIn(halfRoof, CoverSize, CoverHalf, coverVp, new Vector3(30f, 20f, 0f)), "a foot on the map past the kept square counts as open");
        Vector4 picked = Vector4.Transform(new Vector4(17f, 22f, 0f, 1f), coverVp);
        int pickedAt = ((int)((picked.Y / picked.W * 0.5f + 0.5f) * CoverSize) - CoverHalf / 2) * CoverHalf + (int)((picked.X / picked.W * 0.5f + 0.5f) * CoverSize) - CoverHalf / 2;
        var oneRoof = new float[CoverHalf * CoverHalf];
        Array.Fill(oneRoof, 1f);
        oneRoof[pickedAt] = 0f;
        check.Expect(!CasterPass.CoverOpenIn(oneRoof, CoverSize, CoverHalf, coverVp, new Vector3(17f, 22f, 0f))
          && CasterPass.CoverOpenIn(oneRoof, CoverSize, CoverHalf, coverVp, new Vector3(22f, 17f, 0f)), "the kept square holds each texel at the map's own place");

        _track = new TrackTexel[TrackSize * TrackSize];
        _side = new FootSideState();
        _snow = 1f;
        FootDown(new Vector3(20f, 20f, 0f), Vector2.UnitX, 9, 1f, false, in HumanSole);
        check.Expect(MathF.Max(TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f - FootSide), 1f, 0.01f),
                               TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f + FootSide), 1f, 0.01f)) == 0f,
          "a sheltered foot presses no print, so none shows on the roof above it");

        // Given the same ground after the snow has melted away
        // When my character walks across it
        // Then each step makes the ordinary stone sound
        _snow = 0f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "melted snow: an ordinary step");

        // Given snow has only just started falling and the ground still looks bare
        // When my character walks across it
        // Then each step makes the ordinary sound for that ground
        _snow = 0.1f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "a first dusting of snow: an ordinary step");

        // Given snow lying in the open around a building with a roof over its porch
        // When my character walks under the roof
        // Then each step makes the ordinary sound for that ground
        _snow = 1f;
        check.Expect(ChooseStep(false, StoneRow) == StepKind.Engine, "under a roof: an ordinary step");

        // Given a creature the game has no snow footstep sound for
        // When it walks through lying snow
        // Then it makes whatever sound it makes on that ground without the snow
        const string Hard = "fs_x0snw_hard1";
        fixed (byte* empty = "****\0"u8, none = "\0"u8, hard = "FS_x0snw_hard1\0"u8)
        {
            bool heard = CellResRef(hard, out ResRef res);
            check.Expect(!CellResRef(empty, out _) && !CellResRef(none, out _) && !CellResRef(null, out _) && heard
              && Encoding.ASCII.GetString((byte*)&res, Hard.Length) == Hard, "an empty footstep cell plays nothing, so the engine's step goes ahead");
        }

        // Given lying snow in an area with a stream, a carpet and a tile built as snow
        // When my character walks on each
        // Then each makes the same sound it makes with no weather
        check.Expect(ChooseStep(true, WaterRow) == StepKind.Engine && ChooseStep(true, CarpetRow) == StepKind.Engine
          && ChooseStep(true, SnowRow) == StepKind.Engine, "water, carpet and a snow tile keep their own steps under lying snow");

        // Given lying snow and Snow footstep sounds switched off
        // When my character walks across it
        // Then each step makes the ordinary sound for that ground
        SnowFootsteps.V = false;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "the setting off: an ordinary step");

        // Given lying snow and Snow footstep sounds switched off
        // When my character walks across it
        // Then footprints still press into the snow
        _track = new TrackTexel[TrackSize * TrackSize];
        _side = new FootSideState();
        Vector2 h = Vector2.UnitX;
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 1f, true, in HumanSole);
        check.Expect(MathF.Max(TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f - FootSide), 1f, 0.01f),
                               TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f + FootSide), 1f, 0.01f)) > 0.5f,
          "the setting off: footprints still press");
        SnowFootsteps.V = true;

        // Given snow falling and Snow cover set to 0, so nothing shows on the ground
        // When my character walks across it
        // Then each step makes the ordinary sound for that ground
        SnowStrength.V = 0f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "snow cover at zero: an ordinary step");
        SnowStrength.V = 1f;
    });
}
