using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

internal static unsafe partial class Weather
{
    /// <summary>Where each example's creature stands, and the stamp clock of its first step.</summary>
    private static readonly Vector3 PrintAt = new(20f, 20f, 0f);
    private const float PrintNow = 10f;
    /// <summary>When a step's splash is read: just after the step, while it still rises.</summary>
    private const float SplashLook = PrintNow + 0.05f;
    /// <summary>The depth from which a texel counts as pressed, and the step a print's extent is walked in.</summary>
    private const float PressedMin = 0.1f, ExtentStep = 0.0125f;
    /// <summary>The footstep types and size categories the examples name; oracle: the stock footstepsounds.2da,
    /// creaturesize.2da and appearance.2da rows (wolf and basilisk 3 at size 3, giant spider 7 at 4, red dragon 2 at 5).</summary>
    private const int TypeNormal = 0, TypeDragon = 2, TypeSoft = 3, TypeHoof = 4, TypeSpider = 7, TypeWingA = 9, TypeWingB = 10,
                      TypeNone = 12, TypeHorse = 17, TypeInvisibleHorse = 30,
                      SizeTiny = 1, SizeSmall = 2, SizeMedium = 3, SizeLarge = 4, SizeHuge = 5;

    /// <summary>approved-spec: snow prints follow each creature's own feet. Oracle: the hand-built bodies'
    /// hand-written foot, paw and hoof places, read back from the print field and the splash ring.</summary>
    static partial void CreaturePrintApprovedExamples(Checker check) => WithStepScene(() =>
    {
        ExtenderLog? treeLog = PartTree.Log, arenaLog = MeshArena.Log;
        float splash = FootSplash.V;
        Vector4[] foot = (Vector4[])_foot.Clone();
        int next = _footNext;
        PartTree.Log = null; MeshArena.Log = null; FootSplash.V = FootSplash.Default;
        bool db = Services.Anim.PoseRig.Db, freshSeam = !Seam.Facade.Built;
        if (freshSeam && (_log == null || Seam.Facade.Build(new Seam.SeamRegistry(), _log, census: false) < 0)) freshSeam = false;
        check.Expect(Seam.Facade.Built, "the seam's vtable block is built, so each hand-built body takes a shell as the game's would");
        Services.Anim.PoseRig.Db = true;
        var placed = new List<DbId>();
        try
        {
            StepSoundGuards(check);
            BodyExamples(check, placed);
            NoFeetExamples(check);
            OwnFeetExamples(check, placed);
            SkinExamples(check, placed);
            FallbackExamples(check, placed);
            RiderExamples(check, placed);
            bool built = placed.Count > 0;
            foreach (DbId o in placed) built &= !o.IsNull;
            check.Expect(built, $"every hand-built body places and builds its part tree ({placed.Count} placed)");
        }
        finally
        {
            for (int i = placed.Count - 1; i >= 0; i--) Unplace(placed[i]);
            Services.Anim.PoseRig.Db = db;
            if (freshSeam) Seam.Facade.DropTables();
            PartTree.Log = treeLog; MeshArena.Log = arenaLog; FootSplash.V = splash;
            Array.Copy(foot, _foot, FootMax); _footNext = next;
        }
    });

    /// <summary>A fresh print field and splash ring, every creature's foot parity forgotten, snow or wet ground.</summary>
    private static void FreshGround(bool snow, bool wet)
    {
        _track = new TrackTexel[TrackSize * TrackSize];
        _side = new FootSideState();
        Array.Fill(_foot, new Vector4(0f, 0f, 0f, float.NegativeInfinity));
        _footNext = 0;
        _snow = snow ? 1f : 0f;
        _wet = wet ? 1f : 0f;
    }

    /// <summary>One footfall of a creature at <paramref name="at"/> facing +y under open sky.</summary>
    private static StepFacts OpenStep(DbId body, int own, int app, int size, Vector3 at, uint id = 7, bool soft = false)
        => new(at, Vector2.UnitY, id, true, own, app, size, soft, body);

    private static DbId Keep(List<DbId> placed, DbId o) { placed.Add(o); return o; }

    /// <summary>Whether the point <paramref name="rel"/> metres from <paramref name="at"/> shows a print.</summary>
    private static bool Pressed(Vector3 at, Vector2 rel, float now = PrintNow)
        => _track != null && TrackAt(_track, new Vector2(at.X, at.Y) + rel, now, 0.01f) > PressedMin;

    /// <summary>Whether any print within <paramref name="r"/> of <paramref name="at"/> was stamped after <paramref name="t"/>:
    /// read just after it at a refill so fast that only a later stamp still shows.</summary>
    private static bool PrintAfter(Vector3 at, float r, float t)
    {
        float cell = TrackPeriodM / TrackSize;
        if (_track == null) return false;
        for (float y = -r; y <= r; y += cell)
            for (float x = -r; x <= r; x += cell)
                if (TrackAt(_track, new Vector2(at.X + x, at.Y + y), t + 0.001f, 1e6f) > 0f) return true;
        return false;
    }

    /// <summary>A print's length through <paramref name="rel"/> along <paramref name="dir"/>: the pressed run either way of it.</summary>
    private static float Extent(Vector3 at, Vector2 rel, Vector2 dir, float now = PrintNow)
    {
        if (!Pressed(at, rel, now)) return 0f;
        float run = ExtentStep;
        for (float d = ExtentStep; d < 1.5f && Pressed(at, rel + dir * d, now); d += ExtentStep) run += ExtentStep;
        for (float d = ExtentStep; d < 1.5f && Pressed(at, rel - dir * d, now); d += ExtentStep) run += ExtentStep;
        return run;
    }

    /// <summary>Whether a splash still rising lands within <paramref name="r"/> of <paramref name="rel"/> from <paramref name="at"/>.</summary>
    private static bool SplashNear(Vector3 at, Vector2 rel, float r)
    {
        int n = FootLive(SplashLook);
        for (int i = 0; i < n; i++)
            if (Vector2.Distance(new Vector2(_footLive[i].X, _footLive[i].Y), new Vector2(at.X, at.Y) + rel) < r) return true;
        return false;
    }

    /// <summary>Whether a human sole shows where today's stamp sits, ahead of the centre on either side.</summary>
    private static bool SoleAhead(Vector3 at)
        => Pressed(at, new Vector2(FootSide, FootAhead + ToeAt)) || Pressed(at, new Vector2(-FootSide, FootAhead + ToeAt));

    /// <summary>The planted and lifted feet of a fixture: planted ones show a print, lifted ones none.</summary>
    private static bool FeetRead(Vector3 at, Vector2[] feet, bool[] planted)
    {
        bool ok = true;
        for (int k = 0; k < feet.Length; k++) ok &= Pressed(at, feet[k]) == planted[k];
        return ok;
    }

    /// <summary>Guards on today's step sound: its footstepsounds.2da row and its priority group, read off the step's
    /// facts. Oracle: the stock footstepsounds.2da rows (3 soft, 4 hoof) and creaturesize.2da categories.</summary>
    private static void StepSoundGuards(Checker check)
    {
        // Given a creature typed hoof by its own footstep type over a normal appearance, stepping softly
        // When its step sound's row is chosen
        // Then it plays the soft row, 3
        check.Expect(StepRow(OpenStep(default, TypeHoof, TypeNormal, SizeMedium, PrintAt, soft: true)) == TypeSoft,
                     "a soft step plays footstepsounds.2da's soft row");

        // Given a creature whose own footstep type is hoof (4) and whose appearance's is normal (0)
        // When it steps
        // Then its own type's row plays, and with no own type the appearance's does
        check.Expect(StepRow(OpenStep(default, TypeHoof, TypeNormal, SizeMedium, PrintAt)) == TypeHoof
                     && StepRow(OpenStep(default, -1, TypeNormal, SizeMedium, PrintAt)) == TypeNormal,
                     "the creature's own footstep type wins over its appearance's, and an unset one takes the appearance's");

        // Given creatures of each size category, and the player's own creature
        // When a step sound plays
        // Then tiny to medium play in the small group, large and huge in the large group, and the player's in the player's
        bool groups = true;
        foreach (int size in (int[])[SizeTiny, SizeSmall, SizeMedium])
            groups &= StepGroup(OpenStep(default, -1, TypeNormal, size, PrintAt), player: false) == GroupSmall;
        foreach (int size in (int[])[SizeLarge, SizeHuge])
            groups &= StepGroup(OpenStep(default, -1, TypeNormal, size, PrintAt), player: false) == GroupLarge;
        groups &= StepGroup(OpenStep(default, -1, TypeNormal, SizeHuge, PrintAt), player: true) == GroupPlayer;
        check.Expect(groups, "a step sound's group: small for tiny to medium, large for large and huge, the player's own for the player");
    }

    /// <summary>A stand-in creature: its vtable, zeroed room past every creature field a step's facts read, and the
    /// shell its <c>GetModel</c> answers.</summary>
    private struct StandIn
    {
        public IntPtr Vtbl;
        public StandInRoom Fields;
        public IntPtr Shell;
    }

    /// <summary>The zeroed room a stand-in creature keeps where a creature's fields sit.</summary>
    [System.Runtime.CompilerServices.InlineArray(65536)]
    private struct StandInRoom { private byte _first; }

    /// <summary>A stand-in creature's <c>GetModel</c>: the shell it holds.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static IntPtr StandInModel(IntPtr self, byte part, int flag) => ((StandIn*)self)->Shell;

    /// <summary>The live step's facts on a stand-in creature whose model is a placed body. Oracle: the body placed.</summary>
    private static void BodyExamples(Checker check, List<DbId> placed)
    {
        // Given a creature whose model is a placed scene object
        // When its footstep's facts are read, as the footstep hook reads them
        // Then they name that object as its body
        DbId wolf = Keep(placed, Place(Wolf(), PrintAt));
        nint shell = Seam.Facade.ObjectShell(wolf);
        int slot = EngineLayout.CreatureGetModelSlot;
        DbId body = default;
        bool grounded = false;
        if (shell != 0 && slot >= 0)
        {
            var creature = (StandIn*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(StandIn));
            var table = (IntPtr*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(slot + sizeof(IntPtr)));
            bool armed = _wetArmed;
            try
            {
                _wetArmed = false;
                table[slot / sizeof(IntPtr)] = (IntPtr)(delegate* unmanaged<IntPtr, byte, int, IntPtr>)&StandInModel;
                creature->Vtbl = (IntPtr)table;
                creature->Shell = shell;
                StepFacts read = Facts((IntPtr)creature, 7, PrintAt, Vector2.UnitY, true);
                body = read.Body;
                grounded = Grounded(in read);
            }
            finally
            {
                _wetArmed = armed;
                System.Runtime.InteropServices.NativeMemory.Free(creature);
                System.Runtime.InteropServices.NativeMemory.Free(table);
            }
        }
        check.Expect(!wolf.IsNull && body == wolf, $"a footstep's facts name the creature's placed body (shell {shell != 0}, GetModel slot {slot})");

        // Given a creature whose footstep tables cannot be read
        // When its footstep's facts are read
        // Then it steps as a normal walker, so its print is not lost
        check.Expect(body == wolf && grounded, "unread footstep tables: the step reads as a normal walker's");
    }

    private static void NoFeetExamples(Checker check)
    {
        // Given lying snow under open sky And a creature whose appearance.2da FOOTSTEPTYPE is 12 (none), 9 or 10 (wings), unset, or 30 (the invisible horse rows)
        // When its walk fires a footstep event
        // Then no print is pressed into the snow and no splash rises And its footstep sound plays as it does today
        int[] types = [TypeNone, TypeWingA, TypeWingB, -1, TypeInvisibleHorse];
        foreach (int type in types)
        {
            FreshGround(snow: true, wet: true);
            StepFacts s = OpenStep(default, -1, type, SizeMedium, PrintAt);
            StepDown(in s, PrintNow);
            bool pressed = SoleAhead(PrintAt) || Pressed(PrintAt, Vector2.Zero);
            int splashes = FootLive(SplashLook);
            check.Expect(!pressed && splashes == 0, $"footstep type {type}: no print and no splash (print {pressed}, splashes {splashes})");
            check.Expect(StepRow(in s) == type, $"footstep type {type}: the sound's row stays the appearance's ({StepRow(in s)})");
        }

        // Given the same snow And a creature whose FOOTSTEPTYPE is 0 (normal)
        // When its walk fires a footstep event
        // Then a print is pressed as it is today
        FreshGround(snow: true, wet: false);
        StepFacts normal = OpenStep(default, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in normal, PrintNow);
        check.Expect(Pressed(PrintAt, new Vector2(FootSide, FootAhead + ToeAt)), "footstep type 0: today's sole, ahead of the centre");

        // Given lying snow under open sky And a creature whose appearance is typed normal (0) but whose own footstep type is set to none (12)
        // When it steps
        // Then no print is pressed and no splash rises
        FreshGround(snow: true, wet: true);
        StepFacts ownNone = OpenStep(default, TypeNone, TypeNormal, SizeMedium, PrintAt);
        StepDown(in ownNone, PrintNow);
        check.Expect(!SoleAhead(PrintAt) && FootLive(SplashLook) == 0, "own type none over a normal appearance: no print, no splash");

        // Given the same snow And a creature whose appearance is typed none (12) but whose own footstep type is set to normal (0)
        // When it steps
        // Then a print is pressed
        FreshGround(snow: true, wet: false);
        StepFacts ownNormal = OpenStep(default, TypeNormal, TypeNone, SizeMedium, PrintAt);
        StepDown(in ownNormal, PrintNow);
        check.Expect(SoleAhead(PrintAt), "own type normal over a none appearance: a print");

        // Given lying snow under open sky And a creature typed none (12) that is stepping softly
        // When it steps
        // Then no print is pressed
        FreshGround(snow: true, wet: false);
        StepFacts soft = OpenStep(default, -1, TypeNone, SizeMedium, PrintAt, soft: true);
        StepDown(in soft, PrintNow);
        check.Expect(!SoleAhead(PrintAt) && !Pressed(PrintAt, Vector2.Zero), "a soft step of a creature typed none: no print");
    }

    private static void OwnFeetExamples(Checker check, List<DbId> placed)
    {
        bool[] fourPlanted = [true, false, false, true];

        // Given lying snow under open sky And a wolf (c_wolf) walking across it
        // When a footstep event fires
        // Then a paw-sized print, about 0.10 by 0.12 m, is pressed under each paw planted at that moment And no human sole is pressed ahead of the wolf's centre
        ModelDef wolfDef = Wolf();
        DbId wolf = Keep(placed, Place(wolfDef, PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts w = OpenStep(wolf, -1, TypeSoft, SizeMedium, PrintAt);
        StepDown(in w, PrintNow);
        float across = Extent(PrintAt, WolfPaws[0], Vector2.UnitX), along = Extent(PrintAt, WolfPaws[0], Vector2.UnitY);
        check.Expect(!wolf.IsNull && FeetRead(PrintAt, WolfPaws, fourPlanted), "a wolf: a print under each planted paw, none under a lifted one");
        check.Expect(across is > 0.06f and < 0.16f && along is > 0.08f and < 0.18f && !Pressed(PrintAt, WolfPaws[0] + new Vector2(0.15f, 0f)),
                     $"a wolf's print is paw-sized ({across:F3} across, {along:F3} along)");
        check.Expect(!SoleAhead(PrintAt), "a wolf presses no human sole ahead of its centre");

        // Given a giant spider (c_spidgiant) walking across the snow
        // When a footstep event fires
        // Then a small round print is pressed under each planted leg tip, up to about 1.6 m from the spider's centre
        DbId spider = Keep(placed, Place(Spider(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts sp = OpenStep(spider, -1, TypeSpider, SizeLarge, PrintAt);
        StepDown(in sp, PrintNow);
        bool tips = !spider.IsNull;
        for (int k = 0; k < 8; k++)
        {
            Vector3 tip = SpiderTip(k);
            var rel = new Vector2(tip.X, tip.Y);
            bool down = (k & 1) == 0;
            float a = Extent(PrintAt, rel, Vector2.UnitX), b = Extent(PrintAt, rel, Vector2.UnitY);
            tips &= down ? Pressed(PrintAt, rel) && a < 0.2f && b < 0.2f && MathF.Abs(a - b) < 0.05f : !Pressed(PrintAt, rel);
        }
        check.Expect(tips, "a giant spider: a small round print under each planted leg tip, out to 1.6 m, none under a lifted one");

        // Given a basilisk (c_basilisk) walking across the snow
        // When a footstep event fires on a frame its calves dip near the ground
        // Then only its feet leave prints, not its calves
        DbId basilisk = Keep(placed, Place(Basilisk(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts bs = OpenStep(basilisk, -1, TypeSoft, SizeMedium, PrintAt);
        StepDown(in bs, PrintNow);
        bool feet = !basilisk.IsNull, calves = false;
        for (int k = 0; k < 4; k++) { feet &= Pressed(PrintAt, BasiliskFeet[k]); calves |= Pressed(PrintAt, BasiliskCalf(k)); }
        check.Expect(feet && !calves, $"a basilisk: its feet print and its dipping calves do not (feet {feet}, calves {calves})");

        // Given a humanoid in a robe (pmh0_robe001) walking across the snow
        // When a footstep event fires
        // Then the print is shaped and placed by the foot on the ground And no ring is pressed where the hem hangs
        DbId robed = Keep(placed, Place(Biped("cx_pr_robe", StockLegs, RightUp, LeftDown, hem: true), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts rb = OpenStep(robed, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in rb, PrintNow);
        var left = new Vector2(LeftDown.X, LeftDown.Y);
        check.Expect(!robed.IsNull && SoleUnder(PrintAt, left), "a robed humanoid: the sole lies under the planted foot");
        check.Expect(!HemPressed(PrintAt, left), "a robed humanoid: no ring where the hem hangs");

        // Given rain-wet ground and a wolf walking across it
        // When a footstep event fires
        // Then the splash rises at a planted paw, not ahead of the wolf's centre
        FreshGround(snow: false, wet: true);
        StepDown(in w, PrintNow);
        check.Expect(SplashNear(PrintAt, WolfPaws[0], 0.08f) || SplashNear(PrintAt, WolfPaws[3], 0.08f), "a wolf on wet ground: the splash rises at a planted paw");

        // Given a wolf standing still in the snow
        // When no footstep event fires
        // Then no new print appears
        FreshGround(snow: true, wet: false);
        int before = _prints;
        StepDown(in w, PrintNow);
        int stepped = _prints;
        bool newer = PrintAfter(PrintAt, 1.5f, PrintNow), any = PrintAfter(PrintAt, 1.5f, PrintNow - 1f);
        check.Expect(stepped == before + 1 && any && _prints == stepped && !newer,
                     $"a wolf standing still, no footstep event: its one step's prints and none newer ({stepped - before} footfalls, newer {newer})");

        // Given 50 creatures walking across the snow
        // When their footstep events fire
        // Then those calls allocate nothing on the managed heap
        FreshGround(snow: true, wet: false);
        var pack = new StepFacts[50];
        bool packPrinted = true;
        for (int i = 0; i < pack.Length; i++)
        {
            var at = new Vector3(13.5f + 1.2f * (i % 10), 14f + 2f * (i / 10), 0f);
            pack[i] = OpenStep(Keep(placed, Place(wolfDef, at)), -1, TypeSoft, SizeMedium, at, (uint)(100 + i));
            StepDown(in pack[i], PrintNow);
            packPrinted &= !pack[i].Body.IsNull && Pressed(at, WolfPaws[0]) && Pressed(at, WolfPaws[3]);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < pack.Length; i++) StepDown(in pack[i], PrintNow + 1f);
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        check.Expect(packPrinted, "50 wolves: each presses its planted paws, so the measured calls do the work");
        check.Expect(bytes == 0, $"50 wolves' footfalls allocate nothing ({bytes} bytes)");

        // Given lying snow under open sky And a humanoid in plain clothes walking across it
        // When a footstep event fires
        // Then one sole is pressed under the foot on the ground And none under the foot in the air
        DbId plain = Keep(placed, Place(Biped("cx_pr_plain", StockLegs, RightUp, LeftDown), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts pl = OpenStep(plain, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in pl, PrintNow);
        check.Expect(!plain.IsNull && SoleUnder(PrintAt, left), "a humanoid: one sole under the foot on the ground");
        check.Expect(!Pressed(PrintAt, new Vector2(RightUp.X, RightUp.Y)), "a humanoid: no sole under the foot in the air");

        // Given lying snow under open sky And a humanoid whose foot bones carry no mesh, its boots hung from them
        // When a footstep event fires
        // Then the sole is pressed under the boot on the ground, not today's stamp ahead of its centre
        DbId booted = Keep(placed, Place(Biped("cx_pr_booted", StockLegs, RightUp, LeftDown, bareFeet: true), PrintAt));
        bool hung = !Keep(placed, HangBoot(booted, StockLegs.Left.Foot, PrintAt + LeftDown)).IsNull
                    & !Keep(placed, HangBoot(booted, StockLegs.Right.Foot, PrintAt + RightUp)).IsNull;
        FreshGround(snow: true, wet: false);
        StepFacts bt = OpenStep(booted, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in bt, PrintNow);
        check.Expect(hung && SoleUnder(PrintAt, left) && !SoleAhead(PrintAt),
                     "a humanoid with boots hung from bare foot bones: the sole under the planted boot, not today's stamp");

        // Given lying snow under open sky And a humanoid whose walk has no footstep events
        // When a foot plants
        // Then the sole lies under that foot, not 0.15 m ahead of its centre
        var rightDown = new Vector3(0.1f, -0.1f, 0f);
        DbId quiet = Keep(placed, Place(Biped("cx_pr_quiet", StockLegs, rightDown, new Vector3(-0.1f, 0.2f, 0.12f)), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts q = OpenStep(quiet, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in q, PrintNow);
        check.Expect(!quiet.IsNull && SoleUnder(PrintAt, new Vector2(rightDown.X, rightDown.Y)) && !SoleAhead(PrintAt),
                     "a humanoid with no footstep events: the sole under the planted foot, not ahead of the centre");

        // Given a wolf walking across a snow slope
        // When a footstep event fires
        // Then every planted paw, uphill and downhill, is printed
        DbId slope = Keep(placed, Place(SlopeWolf(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts sl = OpenStep(slope, -1, TypeSoft, SizeMedium, PrintAt);
        StepDown(in sl, PrintNow);
        check.Expect(!slope.IsNull && FeetRead(PrintAt, WolfPaws, [true, true, true, true]), "a wolf on a slope: every planted paw, uphill and down, prints");

        // Given a custom humanoid whose rig table names foot parts other than the stock ones
        // When a footstep event fires
        // Then the print lies under those named feet
        var bip = new BipedNames("bip_pelvis", new("bip_r_thigh", "bip_r_calf", "bip_r_foot"), new("bip_l_thigh", "bip_l_calf", "bip_l_foot"));
        var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["cx_pr_custom"] = ["bip_r_thigh", "bip_r_calf", "bip_r_foot", "bip_l_thigh", "bip_l_calf", "bip_l_foot", "torso_g", "", "",
                                "bip_pelvis", "", "", "", "", "", ""],
        };
        DbId custom = Keep(placed, Place(Biped("cx_pr_custom", bip, RightUp, LeftDown, hem: true), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts cu = OpenStep(custom, -1, TypeNormal, SizeMedium, PrintAt);
        Dictionary<string, string[]>? map = RigMap.SwapRows(rows);
        try { StepDown(in cu, PrintNow); }
        finally { RigMap.SwapRows(map); }
        check.Expect(!custom.IsNull && SoleUnder(PrintAt, left) && !HemPressed(PrintAt, left), "a custom rig: the print under the foot its rig row names");

        // Given rain-wet ground and a humanoid walking across it
        // When a footstep event fires
        // Then the splash rises at the planted foot
        FreshGround(snow: false, wet: true);
        StepDown(in pl, PrintNow);
        check.Expect(SplashNear(PrintAt, left, 0.1f), "a humanoid on wet ground: the splash rises at the planted foot");

        // Given a wolf mid-lunge, its two raised forepaws on a line rising ahead that its planted forepaw sinks below
        // When a footstep event fires
        // Then the planted paws print and the raised ones do not
        DbId lunge = Keep(placed, Place(LungeWolf(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts lg = OpenStep(lunge, -1, TypeSoft, SizeMedium, PrintAt);
        StepDown(in lg, PrintNow);
        check.Expect(!lunge.IsNull && FeetRead(PrintAt, LungePaws, [false, false, true, true]),
                     "a lunging wolf: the planted paws print, not the raised ones a line through them would plant");

        // Given a wolf whose footstep event fires twice within one stride
        // When both events fire
        // Then one footfall is pressed
        FreshGround(snow: true, wet: false);
        StepFacts twice = OpenStep(wolf, -1, TypeSoft, SizeMedium, PrintAt, 900);
        int once = _prints;
        StepDown(in twice, PrintNow);
        StepDown(in twice, PrintNow + 0.05f);
        check.Expect(_prints == once + 1, $"a wolf's two events in one stride: one footfall ({_prints - once})");

        // Given a wolf climbing a 1-in-10 rise, one hind paw drawn in under its centre and the rest planted up the rise
        // When a footstep event fires
        // Then every paw prints, not only the one on the level under its centre
        DbId climb = Keep(placed, Place(ClimbWolf(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts cl = OpenStep(climb, -1, TypeSoft, SizeMedium, PrintAt, 901);
        StepDown(in cl, PrintNow);
        check.Expect(!climb.IsNull && FeetRead(PrintAt, ClimbPaws, [true, true, true, true]),
                     "a wolf climbing a rise: every paw on the rise prints, not only the one under its centre");

        // Given a wolf walking down a slope, one hind paw planted behind it and both forepaws lifted 3 cm beside its centre
        // When a footstep event fires
        // Then only the planted hind paw prints
        DbId descent = Keep(placed, Place(DescentWolf(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts ds = OpenStep(descent, -1, TypeSoft, SizeMedium, PrintAt, 902);
        StepDown(in ds, PrintNow);
        check.Expect(!descent.IsNull && FeetRead(PrintAt, DescentPaws, [false, false, true, false]),
                     "a wolf walking downhill: its planted hind paw prints, not the forepaws lifted beside its centre");
    }

    /// <summary>Whether a sole-sized print lies under the foot at <paramref name="rel"/>: pressed there, as long as a
    /// sole along the creature's heading and as narrow across it.</summary>
    private static bool SoleUnder(Vector3 at, Vector2 rel)
    {
        float along = Extent(at, rel, Vector2.UnitY), across = Extent(at, rel, Vector2.UnitX);
        return along is > 0.18f and < 0.34f && across is > 0.06f and < 0.16f;
    }

    /// <summary>Whether any point of the hem's ellipse shows a print, outside the planted sole at <paramref name="foot"/>,
    /// which the hem's line crosses.</summary>
    private static bool HemPressed(Vector3 at, Vector2 foot)
    {
        for (int k = 0; k < 16; k++)
        {
            float a = MathF.Tau * k / 16f;
            var p = new Vector2(MathF.Cos(a) * HemAcross, MathF.Sin(a) * HemAlong);
            if (MathF.Abs(p.X - foot.X) < Sole.X && MathF.Abs(p.Y - foot.Y) < Sole.Y) continue;
            if (Pressed(at, p)) return true;
        }
        return false;
    }

    /// <summary>Whether a dragon's planted paws print at their full size and its lifted paws and tail do not.</summary>
    private static bool DragonPrinted(Vector3 at)
    {
        bool ok = true;
        for (int k = 0; k < 4; k++)
        {
            Vector3 p = DragonPose[DragonFirstPaw + k];
            var c = new Vector2(p.X, p.Y);
            if (p.Z > 0.1f) { ok &= !Pressed(at, c); continue; }
            ok &= Pressed(at, c) && Pressed(at, c + new Vector2(0.25f, 0f)) && Pressed(at, c - new Vector2(0.25f, 0f))
                  && Pressed(at, c + new Vector2(0f, 0.35f)) && Pressed(at, c - new Vector2(0f, 0.35f))
                  && !Pressed(at, c + new Vector2(0.6f, 0f)) && !Pressed(at, c - new Vector2(0.6f, 0f))
                  && !Pressed(at, c + new Vector2(0f, 0.7f)) && !Pressed(at, c - new Vector2(0f, 0.7f));
        }
        return ok;
    }

    /// <summary>Whether any point along a dragon's tail shows a print.</summary>
    private static bool TailPressed(Vector3 at)
    {
        for (float y = -2.6f; y > -5.4f; y -= 0.1f)
            if (Pressed(at, new Vector2(0f, y))) return true;
        return false;
    }

    private static void SkinExamples(Checker check, List<DbId> placed)
    {
        // Given lying snow under open sky And a red dragon (c_drgred) walking across it
        // When a footstep event fires
        // Then a print the size of each planted paw, about 0.7 by 0.9 m, is pressed under it And no print is pressed along its tail
        ModelDef dragonDef = Dragon(bones: true);
        DbId dragon = Keep(placed, Place(dragonDef, PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts d = OpenStep(dragon, -1, TypeDragon, SizeHuge, PrintAt);
        StepDown(in d, PrintNow);
        check.Expect(!dragon.IsNull && DragonPrinted(PrintAt), "a red dragon: a paw-sized print under each planted paw, where its bones put it");
        check.Expect(!TailPressed(PrintAt), "a red dragon: no print along its tail");

        // Given a red dragon wearing a rigid claw guard, a mesh of its own read after its skin, resting on the snow
        // When a footstep event fires
        // Then the claw guard prints as well as the planted paws
        DbId guarded = Keep(placed, Place(Dragon(bones: true, guard: true), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts gd = OpenStep(guarded, -1, TypeDragon, SizeHuge, PrintAt, 903);
        StepDown(in gd, PrintNow);
        check.Expect(!guarded.IsNull && Pressed(PrintAt, ClawGuardAt) && DragonPrinted(PrintAt),
                     "a dragon's rigid claw guard prints beside its paws: the skin's unused bone slots are no feet");

        // Given 20 red dragons walking across the snow
        // When their footstep events fire
        // Then those calls allocate nothing on the managed heap
        FreshGround(snow: true, wet: false);
        var flight = new StepFacts[20];
        bool flightPrinted = true;
        for (int i = 0; i < flight.Length; i++)
        {
            var at = new Vector3(8f + 4f * (i % 5), 10f + 9f * (i / 5), 0f);
            flight[i] = OpenStep(Keep(placed, Place(dragonDef, at)), -1, TypeDragon, SizeHuge, at, (uint)(200 + i));
            StepDown(in flight[i], PrintNow);
            Vector3 fl = DragonPose[DragonFirstPaw];
            flightPrinted &= !flight[i].Body.IsNull && Pressed(at, new Vector2(fl.X, fl.Y));
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < flight.Length; i++) StepDown(in flight[i], PrintNow + 1f);
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        check.Expect(flightPrinted, "20 red dragons: each presses its planted paw, so the measured calls do the work");
        check.Expect(bytes == 0, $"20 red dragons' footfalls allocate nothing ({bytes} bytes)");

        // Given a skinned creature whose skin binds no bones
        // When its footstep event fires
        // Then it presses the fallback stamp And no print at its rest pose
        DbId boneless = Keep(placed, Place(Dragon(bones: false), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts bl = OpenStep(boneless, -1, TypeDragon, SizeHuge, PrintAt);
        StepDown(in bl, PrintNow);
        bool rest = false;
        for (int k = 0; k < 4; k++) rest |= Pressed(PrintAt, new Vector2(DragonBind[DragonFirstPaw + k].X, DragonBind[DragonFirstPaw + k].Y));
        check.Expect(!boneless.IsNull && RoundPad(PrintAt, new Vector2(FootSide, FootAhead)), "a skin binding no bones: the fallback round pad where today's sole sits");
        check.Expect(!rest, "a skin binding no bones: no print at its rest pose");

        // Given rain-wet ground and a red dragon walking across it
        // When a footstep event fires
        // Then the splash rises at a planted paw
        FreshGround(snow: false, wet: true);
        StepDown(in d, PrintNow);
        Vector3 pfl = DragonPose[DragonFirstPaw], pbr = DragonPose[DragonFirstPaw + 3];
        check.Expect(SplashNear(PrintAt, new Vector2(pfl.X, pfl.Y), 0.45f) || SplashNear(PrintAt, new Vector2(pbr.X, pbr.Y), 0.45f),
                     "a red dragon on wet ground: the splash rises at a planted paw");
    }

    /// <summary>Whether a round pad lies about <paramref name="rel"/>: as long along the heading as it is wide across it.</summary>
    private static bool RoundPad(Vector3 at, Vector2 rel)
    {
        float along = Extent(at, rel, Vector2.UnitY), across = Extent(at, rel, Vector2.UnitX);
        return along > 0f && MathF.Abs(along - across) <= 0.25f * MathF.Max(along, across);
    }

    private static void FallbackExamples(Checker check, List<DbId> placed)
    {
        var sole = new Vector2(FootSide, FootAhead);
        DbId bare = Keep(placed, Place(Bare(), PrintAt));

        // Given lying snow under open sky And a creature whose model gives no foot geometry
        // When its footstep event fires
        // Then it presses a stamp chosen by its footstep type and size: a hoof creature (type 4) a hoof mark, a normal one (type 0) today's sole And a creature of a larger size category presses a larger stamp
        FreshGround(snow: true, wet: false);
        StepFacts hoof = OpenStep(bare, -1, TypeHoof, SizeMedium, PrintAt);
        StepDown(in hoof, PrintNow);
        float hoofAlong = Extent(PrintAt, sole, Vector2.UnitY);
        check.Expect(!bare.IsNull && hoofAlong is > 0.06f and < 0.2f, $"no foot geometry, a hoof type: a hoof mark, shorter than a sole ({hoofAlong:F3} m)");
        FreshGround(snow: true, wet: false);
        StepFacts medium = OpenStep(bare, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in medium, PrintNow);
        float mediumAlong = Extent(PrintAt, sole, Vector2.UnitY);
        check.Expect(mediumAlong is > 0.2f and < 0.32f && Pressed(PrintAt, sole + new Vector2(0f, ToeAt)), $"no foot geometry, normal: today's sole ({mediumAlong:F3} m)");
        FreshGround(snow: true, wet: false);
        StepFacts large = OpenStep(bare, -1, TypeNormal, SizeLarge, PrintAt);
        StepDown(in large, PrintNow);
        float largeAlong = Extent(PrintAt, sole, Vector2.UnitY);
        check.Expect(largeAlong > mediumAlong + 0.02f, $"no foot geometry: a larger size category presses a larger stamp ({largeAlong:F3} against {mediumAlong:F3} m)");

        // Given lying snow under open sky And an ox (appearance row 142, typed normal) walking across it
        // When a footstep event fires
        // Then hoof prints are pressed under its planted hooves And no human sole
        DbId ox = Keep(placed, Place(Ox(), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts o = OpenStep(ox, -1, TypeNormal, SizeLarge, PrintAt);
        StepDown(in o, PrintNow);
        float oxAlong = Extent(PrintAt, OxHooves[0], Vector2.UnitY);
        check.Expect(!ox.IsNull && FeetRead(PrintAt, OxHooves, [true, false, false, true]) && oxAlong is > 0.08f and < 0.2f,
                     $"an ox: a hoof print under each planted hoof ({oxAlong:F3} m along)");
        check.Expect(!SoleAhead(PrintAt), "an ox presses no human sole");

        // Given a creature with no foot geometry whose footstep type is neither hoof nor normal
        // When it steps
        // Then it presses a round pad sized by its size category
        FreshGround(snow: true, wet: false);
        StepFacts small = OpenStep(bare, -1, TypeDragon, SizeSmall, PrintAt);
        StepDown(in small, PrintNow);
        bool smallRound = RoundPad(PrintAt, sole);
        float smallAcross = Extent(PrintAt, sole, Vector2.UnitX);
        FreshGround(snow: true, wet: false);
        StepFacts big = OpenStep(bare, -1, TypeDragon, SizeLarge, PrintAt);
        StepDown(in big, PrintNow);
        float bigAcross = Extent(PrintAt, sole, Vector2.UnitX);
        check.Expect(smallRound && RoundPad(PrintAt, sole) && bigAcross > smallAcross + 0.02f,
                     $"no foot geometry, neither hoof nor normal: a round pad sized by size category ({smallAcross:F3} against {bigAcross:F3} m)");

        // Given a creature with no foot geometry walking
        // When it steps
        // Then the stamp sits where today's sole sits, ahead of its centre and on alternate sides
        FreshGround(snow: true, wet: false);
        StepDown(in medium, PrintNow);
        StepDown(in medium, PrintNow + 0.5f);
        check.Expect(Pressed(PrintAt, new Vector2(FootSide, FootAhead + ToeAt), PrintNow + 0.5f)
                     && Pressed(PrintAt, new Vector2(-FootSide, FootAhead + ToeAt), PrintNow + 0.5f),
                     "no foot geometry: the stamp ahead of the centre, on alternate sides");
    }

    private static void RiderExamples(Checker check, List<DbId> placed)
    {
        var rightStirrup = new Vector3(0.25f, 0.1f, 1f);
        var leftStirrup = new Vector3(-0.25f, 0.1f, 1f);
        Vector2[] hooves = [new(Hooves[0].X, Hooves[0].Y), new(Hooves[1].X, Hooves[1].Y), new(Hooves[2].X, Hooves[2].Y), new(Hooves[3].X, Hooves[3].Y)];
        bool[] down = [true, false, false, true];
        DbId rider = Keep(placed, Place(Biped("cx_pr_rider", StockLegs, rightStirrup, leftStirrup, hooves: true), PrintAt));

        // Given lying snow under open sky And a rider mounted on a horse (c_horse1) walking across it
        // When a footstep event fires
        // Then a hoof-sized print, about 0.10 by 0.14 m, is pressed under each hoof planted at that moment And no print is pressed under the rider's own feet And no human sole is pressed
        FreshGround(snow: true, wet: false);
        StepFacts mounted = OpenStep(rider, TypeHorse, TypeNormal, SizeMedium, PrintAt);
        StepDown(in mounted, PrintNow);
        float along = Extent(PrintAt, hooves[0], Vector2.UnitY), across = Extent(PrintAt, hooves[0], Vector2.UnitX);
        check.Expect(!rider.IsNull && FeetRead(PrintAt, hooves, down) && along is > 0.08f and < 0.2f && across is > 0.06f and < 0.16f,
                     $"a mounted rider: a hoof-sized print under each planted hoof ({across:F3} by {along:F3} m)");
        check.Expect(!Pressed(PrintAt, new Vector2(rightStirrup.X, rightStirrup.Y)) && !Pressed(PrintAt, new Vector2(leftStirrup.X, leftStirrup.Y))
                     && !SoleAhead(PrintAt), "a mounted rider: no print under the rider's own feet and no human sole");

        // Given a mounted rider whose appearance takes the invisible horse row (30)
        // When a footstep event fires
        // Then the horse's hoof prints are still pressed
        FreshGround(snow: true, wet: false);
        StepFacts invisible = OpenStep(rider, -1, TypeInvisibleHorse, SizeMedium, PrintAt);
        StepDown(in invisible, PrintNow);
        check.Expect(FeetRead(PrintAt, hooves, down), "a mounted rider on the invisible horse row: the hooves still print");

        // Given a rider who dismounts and walks on across the snow
        // When their footstep event fires
        // Then their own sole prints again And no hoof print
        DbId afoot = Keep(placed, Place(Biped("cx_pr_afoot", StockLegs, RightUp, LeftDown), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts walk = OpenStep(afoot, -1, TypeNormal, SizeMedium, PrintAt);
        StepDown(in walk, PrintNow);
        check.Expect(!afoot.IsNull && SoleUnder(PrintAt, new Vector2(LeftDown.X, LeftDown.Y)) && !Pressed(PrintAt, hooves[0]) && !Pressed(PrintAt, hooves[3]),
                     "a dismounted rider: their own sole prints again, no hoof");

        // Given rain-wet ground and a mounted rider
        // When a footstep event fires
        // Then the splash rises at a planted hoof
        FreshGround(snow: false, wet: true);
        StepDown(in mounted, PrintNow);
        check.Expect(SplashNear(PrintAt, hooves[0], 0.1f) || SplashNear(PrintAt, hooves[3], 0.1f), "a mounted rider on wet ground: the splash rises at a planted hoof");

        // Given lying snow and a walking biped whose footstep type is a wing row (9), its own feet planted
        // When a footstep event fires
        // Then nothing prints: a body is let past the feetless rows only when it carries a horse's hooves
        FreshGround(snow: true, wet: false);
        StepFacts winged = OpenStep(afoot, -1, TypeWingA, SizeMedium, PrintAt, 905);
        StepDown(in winged, PrintNow);
        check.Expect(!SoleUnder(PrintAt, new Vector2(LeftDown.X, LeftDown.Y)) && !SoleAhead(PrintAt),
                     "an unmounted body on a wing row presses nothing: only a mounted body passes the feetless rows");

        // Given lying snow and a walking biped whose model names one hoof node of the four
        // When a footstep event fires
        // Then its own sole prints and no hoof: one stray node is no mount
        DbId stray = Keep(placed, Place(Biped("cx_pr_stray", StockLegs, RightUp, LeftDown, strayHoof: true), PrintAt));
        FreshGround(snow: true, wet: false);
        StepFacts st = OpenStep(stray, -1, TypeNormal, SizeMedium, PrintAt, 904);
        StepDown(in st, PrintNow);
        check.Expect(!stray.IsNull && SoleUnder(PrintAt, new Vector2(LeftDown.X, LeftDown.Y)) && !Pressed(PrintAt, hooves[0]),
                     "a biped naming one hoof node of the four prints its own sole, no hoof");
    }
}
