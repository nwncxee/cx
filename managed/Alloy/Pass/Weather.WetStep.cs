using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

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
    /// <summary>Metres above the foot the shelter ray starts (over a kerb or a doorstep) and how far it reaches.</summary>
    private const float ShelterLiftM = 0.5f, ShelterRayM = 40f;
    /// <summary>Creatures and items in the way are cast past, as the foot placement does, at most this often.</summary>
    private const int ShelterCasts = 6;
    /// <summary><c>PlayFootstep</c>'s priority groups: the player's, then a non-player's by size category.</summary>
    private const byte GroupPlayer = 0x11, GroupSmall = 0x12, GroupLarge = 0x10;   // not-an-offset: CExoSound priority group ids
    /// <summary>A CExoString: <c>{char* buf; uint len; uint cap}</c>. not-an-offset: a caller-owned value's size.</summary>
    private const int ExoSize = 16;
    /// <summary>A CResRef is sixteen chars, not terminated when full. not-an-offset: its size.</summary>
    private const int ResRefSize = 16;

    internal static Knob<bool> WetSteps = null!, SnowFootsteps = null!;

    private static readonly IntPtr _soundSym = Native.Sym(SymbolMap.ExoSound), _rulesSym2 = Native.Sym(SymbolMap.Rules);
    private static IntPtr _colType, _colSize;
    private static readonly IntPtr[] _colPud = new IntPtr[3], _colSnow = new IntPtr[3];
    private static bool _wetArmed;
    /// <summary>What a footfall sounds of: the engine's own step for the surface, or a footstepsounds.2da column.</summary>
    private enum StepKind { Engine, Puddles, Snow }
    /// <summary>The weather steps taken and refused, for the stats line.</summary>
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

    /// <summary>A footfall, from <see cref="Services.Anim.Footfall"/>'s <c>PlayFootstep</c> forward: the print and the
    /// splash, and the weather sound. True = the weather sound played, so the caller skips the engine's dry one. Cold: a
    /// few per second per walking creature.</summary>
    internal static bool Footstep(IntPtr creature)
    {
        if (_off || !Enabled.V || FrameInputs.Interior || creature == IntPtr.Zero) return false;
        var o = new CNWCObject(creature);
        Vector3 p = o.Position, f = o.Orientation;
        Vec3 at = new() { X = p.X, Y = p.Y, Z = p.Z };
        bool weather = StepInWeather;
        bool open = weather && Open(p);
        if (weather && !open) _stepCounts.Sheltered++;
        FootDown(p, new Vector2(f.X, f.Y), o.Id, TrackNow, open);
        int surface = open && _wetArmed && o.AreaPtr != IntPtr.Zero ? EngineFn.AreaGetSurfaceMaterial(o.AreaPtr, VectorArg.Of(&at)) : -1;
        StepKind kind = ChooseStep(open, surface);
        return kind != StepKind.Engine && StepSound(creature, o, at, kind);
    }

    /// <summary>Is there weather a footfall could answer to? False saves the shelter ray.</summary>
    private static bool StepInWeather => (WetShown > FootMinWet && (FootSplash.V > 0f || WetSteps.V)) || (SnowFootsteps.V && SnowShown > SnowStepMin);

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

    /// <summary>The segment weather takes to reach <paramref name="foot"/>, as <paramref name="from"/> to
    /// <paramref name="to"/>. Cast downward along <paramref name="step"/>, because the hit test is one-sided and a
    /// roof's faces look up.</summary>
    private static void ShelterRay(Vector3 foot, Vector3 fall, out Vector3 from, out Vector3 to, out Vector3 step)
    {
        Vector3 up = -fall;
        if (up.Z <= 0f) up = Vector3.UnitZ;
        to = foot + new Vector3(0f, 0f, ShelterLiftM);
        from = to + up * ShelterRayM;
        step = -up;
    }

    /// <summary>Can the rain reach <paramref name="p"/>? One ray down the cover bake's direction; a tile, a
    /// static or a placeable on it is a roof, anything else (a creature, a held item) is cast past.</summary>
    private static bool Open(Vector3 p)
    {
        IntPtr scene = SceneRay.Armed ? SceneRay.WorldScene() : IntPtr.Zero;
        if (scene == IntPtr.Zero) return true;
        ShelterRay(p, CoverDir, out Vector3 from, out Vector3 to, out Vector3 step);
        for (int i = 0; i < ShelterCasts; i++)
        {
            if (!SceneRay.Cast(scene, from, to, IntPtr.Zero, out Vector3 hit, out IntPtr g, statics: true)) break;
            if (g == IntPtr.Zero || SceneRay.IsTile(g) || SceneRay.IsPlaceable(g)) { SceneRay.Release(); return false; }
            from = hit + step * SceneRay.Nudge;
        }
        SceneRay.Release();
        return true;
    }

    /// <summary>Play this creature's <paramref name="kind"/> step, as <c>PlayFootstep</c> would play its surface's.
    /// False = nothing to play: the engine's own step goes ahead.</summary>
    private static bool StepSound(IntPtr creature, CNWCObject o, Vec3 at, StepKind kind)
    {
        if (!_wetArmed || o.FootstepSkip != 0 || o.AreaPtr == IntPtr.Zero) return false;
        IntPtr rules = _rulesSym2 == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_rulesSym2;
        IntPtr sound = _soundSym == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_soundSym;
        IntPtr tables = rules == IntPtr.Zero ? IntPtr.Zero : new CNWRules(rules).TwoDimArrays;
        if (sound == IntPtr.Zero || tables == IntPtr.Zero) return false;
        var t = new CTwoDimArrays(tables);
        var c = new CNWCCreature(creature);
        CPathfindInformation pf = c.Pathfind;
        int app = pf.IsNull ? -1 : pf.Appearance;
        int row = c.FootstepType;
        if (row == -1 && (app < 0 || EngineFn.C2DAGetINTEntry(t.Appearance, app, _colType, &row) == 0)) return false;
        if (c.SoftStep == 1) row = 3;
        byte* res = stackalloc byte[ResRefSize];
        if (!Cell(t.FootstepSounds, row, (kind == StepKind.Snow ? _colSnow : _colPud)[Random.Shared.Next(3)], res)) return false;
        byte group = GroupPlayer;
        if (creature != PlayerIdentity.Creature())
        {
            int size = 0;
            if (app >= 0) EngineFn.C2DAGetINTEntry(t.Appearance, app, _colSize, &size);
            group = (uint)(size - 1) < 3 ? GroupSmall : GroupLarge;
        }
        EngineFn.SoundPlay3DOneShot(sound, res, VectorArg.Of(&at), 0f, group, 0, 0);
        if (kind == StepKind.Snow) _stepCounts.Snow++; else _stepCounts.Wet++;
        return true;
    }

    /// <summary>A footstepsounds.2da cell as a CResRef in <paramref name="res"/>; false when it is empty.</summary>
    private static bool Cell(IntPtr twoda, int row, IntPtr column, byte* res)
    {
        if (twoda == IntPtr.Zero) return false;
        byte* exo = stackalloc byte[ExoSize];
        NativeMemory.Clear(exo, (nuint)ExoSize);   // the engine frees a non-null buffer first
        bool ok = false;
        if (EngineFn.C2DAGetCExoStringEntry(twoda, row, column, (IntPtr)exo) != 0)
            ok = CellResRef(*(byte**)exo, res);
        EngineFn.ExoStringDtor((IntPtr)exo);
        return ok;
    }

    /// <summary>A cell's text as a lower-case CResRef in <paramref name="res"/>; false when it names no sound, 2DA's
    /// <c>****</c> included.</summary>
    private static bool CellResRef(byte* s, byte* res)
    {
        int n = 0;
        for (; s != null && n < ResRefSize && s[n] != 0; n++) res[n] = (byte)char.ToLowerInvariant((char)s[n]);
        for (int i = n; i < ResRefSize; i++) res[i] = 0;
        return n > 0 && !(n == 4 && res[0] == '*' && res[1] == '*' && res[2] == '*' && res[3] == '*');
    }

    private static string WetStepStats() => $", {_stepCounts.Wet} wet steps, {_stepCounts.Snow} snow steps, {_stepCounts.Sheltered} sheltered";

    /// <summary>The approved snow-step examples; implemented in <c>SnowStepApprovedExamples.cs</c>.</summary>
    static partial void SnowStepApprovedExamples(Checker check);

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
    /// hand-written expected kinds, and for an empty cell the stock table's own text.</summary>
    private static void StepSelfCheck(Checker check) => WithStepScene(() =>
    {
        Vector3 foot = new(10f, 10f, 0f);
        Vector3 ra = new(5f, 5f, 4f), rb = new(15f, 5f, 4f), rc = new(10f, 15f, 4f);
        Vector3 fall = Vector3.Normalize(new Vector3(0.05f, 0f, -1f));
        ShelterRay(foot, fall, out Vector3 from, out Vector3 to, out _);
        check.Expect(HitTest.PolyHit(ra, rb, rc, from, ref to), "a roof whose faces look up shelters the foot below it");
        ShelterRay(foot + new Vector3(30f, 0f, 0f), fall, out from, out to, out _);
        check.Expect(!HitTest.PolyHit(ra, rb, rc, from, ref to), "ground beside that roof stays open to the sky");
        ShelterRay(foot, fall, out from, out to, out Vector3 step);
        check.Expect(step.Z < 0f && to.Z == ShelterLiftM, "the ray ends above the foot, so the ground under it is no roof");

        check.Expect(!StepInWeather, "dry bare ground casts no shelter ray");
        _snow = 1f;
        check.Expect(StepInWeather, "lying snow on dry ground asks whether the sky is open");

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
        byte* res = stackalloc byte[ResRefSize];
        const string Hard = "fs_x0snw_hard1";
        fixed (byte* empty = "****\0"u8, none = "\0"u8, hard = "FS_x0snw_hard1\0"u8)
            check.Expect(!CellResRef(empty, res) && !CellResRef(none, res) && !CellResRef(null, res) && CellResRef(hard, res)
              && Encoding.ASCII.GetString(res, Hard.Length) == Hard, "an empty footstep cell plays nothing, so the engine's step goes ahead");

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
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 1f, StepInWeather);
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
