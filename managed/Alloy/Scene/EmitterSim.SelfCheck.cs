using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>
    /// Off-device: every part of the simulation that is a DECISION rather than a read. The step is driven
    /// through an <see cref="Emitter"/> snapshot and the pool is the real one, so it all runs with no client
    /// and no GL. NOT covered, and cannot be: <c>Part::GetWorld</c>, <c>Gob::GetAdjustedScale</c> and the
    /// wind call are engine calls with no seam, left to the live check.
    /// </summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        // ---- the maths the rest of the file stands on ------------------------------------------------
        // A transposed basis column sends every spawn sideways, and the symptom reads as "the effect is in
        // the wrong place" rather than as an algebra error.
        Emitter e = default;
        e.Part = -1;
        e.M.Orient = new Quat { W = 1f };
        Basis(ref e);
        check.Expect(e.M.Bx == new Vector3(1f, 0f, 0f) && e.M.By == new Vector3(0f, 1f, 0f)
                     && e.M.Bz == new Vector3(0f, 0f, 1f),
          "the identity quaternion gives the identity spawn basis, column-major");
        float h = MathF.Sqrt(0.5f);
        e.M.Orient = new Quat { W = h, Z = h };                 // a quarter turn about Z
        Basis(ref e);
        check.Expect(Near(e.M.Bx, new Vector3(0f, 1f, 0f)) && Near(e.M.Bz, new Vector3(0f, 0f, 1f)),
          "...and a quarter turn about Z carries the basis X onto world Y -- (w,x,y,z), not (x,y,z,w)");
        check.Expect(Near(QuatZ(new Quat { W = 1f }), new Vector3(0f, 0f, 1f))
                     && Near(QuatZ(AxisAngle(new Vector3(1f, 0f, 0f), HalfPi)), new Vector3(0f, -1f, 0f)),
          "a quarter turn about X tips the launch direction from +Z to -Y");
        check.Expect(Near(Rotate(AxisAngle(new Vector3(0f, 0f, 1f), HalfPi), new Vector3(1f, 0f, 0f)),
                          new Vector3(0f, 1f, 0f)),
          "Rotate agrees with the basis it is composed against");
        Quat arc = RotationArc(new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f));
        check.Expect(Near(Rotate(arc, new Vector3(1f, 0f, 0f)), new Vector3(0f, 1f, 0f)),
          "RotationArc carries its `from` exactly onto its `to` -- the bolt's between-fractal transform");
        check.Expect(Near(Rotate(RotationArc(Vector3.UnitX, -Vector3.UnitX), Vector3.UnitX), -Vector3.UnitX),
          "...including the antiparallel case, where the arc is ambiguous and a NaN would move every particle");
        check.Expect(Near(Nearest(Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(3f, 4f, 0f)),
                          new Vector3(3f, 0f, 0f))
                     && Nearest(Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(-5f, 0f, 0f)) == Vector3.Zero,
          "the p2p arrival test projects onto the SEGMENT and clamps to its ends");

        // ---- the spawn accumulator. A 3/s emitter at 60fps births 0.05 of a particle a frame; without the
        // carry it births none at all, forever, and the effect is simply missing.
        Reset();
        e = Params(ModeFountain);
        e.BirthRate = 3f; e.Life = 10f;
        int s = Slot0(in e);
        check.Expect(s == 0 && _tab[0].Mode == ModeFountain && _tab[0].Mover == MoveStandard,
          "the model's `update Fountain` with no p2p bit resolves to the fountain mode and the plain mover");
        check.Expect(_tab[0].Cap >= 32, "a 3/s emitter living 10s reserves room for its population");
        ref Slot slot = ref _tab[0];
        int frames = 0;
        // Age then birth, which is Advance's order minus the mover -- the mover needs a scene for wind and
        // collision and the accumulator is entirely FountainBirth's.
        while (slot.Live == 0 && frames < 60) { Tick(ref slot, ref e, 1f / 60f); frames++; }
        check.Expect(slot.Live == 1 && frames >= 19 && frames <= 21,
          "the accumulator births the first particle after ~1/3 s at 60fps, not on frame one and not never");
        for (int i = 0; i < 60; i++) Tick(ref slot, ref e, 1f / 60f);
        check.Expect(slot.Live == 4, "one more second of a 3/s emitter is three more particles");

        // ---- spawntype 1 is a trail: births follow the distance moved, and the p2p bit is no part of the
        // choice (FountainEmitter::Update @0x4b1d9f). Oracle: hand-counted births for a literal move.
        Reset();
        e = Params(ModeFountain);
        e.BirthRate = 10f; e.Life = 10f; e.Trail = true;
        s = Slot0(in e);
        slot = ref _tab[0];
        for (int i = 0; i < 30; i++) Tick(ref slot, ref e, 1f / 60f);
        check.Expect(slot.Live == 0, "a trail that does not move births nothing, however long it waits");
        e.M.Pos = new Vector3(1f, 0f, 0f);
        Tick(ref slot, ref e, 1f / 60f);
        check.Expect(slot.Live == 10, "...and moved one metre at 10 a metre births ten, with no p2p target at all");
        Reset();
        e = Params(ModeFountain);
        e.BirthRate = 3f; e.Life = 10f; e.Flags = FP2P;
        s = Slot0(in e);
        slot = ref _tab[0];
        for (int i = 0; i < 21; i++) Tick(ref slot, ref e, 1f / 60f);
        check.Expect(slot.Live == 1, "a p2p fountain of spawntype 0 births by time, target or not");

        // ---- lifetime and death, and the ORDER death leaves behind. An index-domain fade and the linked
        // render type both read the live slice as oldest-first, so a swap-remove would recolour the chain.
        Reset();
        e = Params(ModeFountain);
        e.BirthRate = 0f; e.Life = 1f;
        s = Slot0(in e);
        slot = ref _tab[0];
        slot.Cap = 8;
        for (int i = 0; i < 4; i++) { Particle p = Take(ref slot); p.Age = i * 0.3f; p.Roll = i; }
        check.Expect(slot.Live == 4, "four particles taken");
        // The death test reads the age the PREVIOUS step left and only then adds dt, which is the engine's
        // order: a particle lives out the whole frame it reaches its lifespan on.
        Reap(ref slot, ref e, 0.15f);            // -> 0.15 0.45 0.75 1.05
        check.Expect(slot.Live == 4 && At(ref slot, 0).Roll == 0f && At(ref slot, 3).Roll == 3f,
          "nothing dies under its lifespan and the order is untouched");
        Reap(ref slot, ref e, 0.5f);             // 1.05 is past; survivors -> 0.65 0.95 1.25
        check.Expect(slot.Live == 3 && At(ref slot, 0).Roll == 0f && At(ref slot, 1).Roll == 1f
                     && At(ref slot, 2).Roll == 2f,
          "the one past its life dies and the survivors keep their order");
        Reap(ref slot, ref e, 0f);               // 1.25 is past
        check.Expect(slot.Live == 2 && At(ref slot, 0).Roll == 0f && At(ref slot, 1).Roll == 1f,
          "...and compaction stays stable across more than one death (never a swap-remove)");
        Particle reborn = Take(ref slot);
        check.Expect(!reborn.IsNull && slot.Live == 3,
          "a dead particle's record is reused rather than allocated -- the slice's tail IS the free list");
        e.Life = -1f;
        Particle immortal = At(ref slot, 0);
        immortal.Age = 99f;
        Reap(ref slot, ref e, 0.1f);
        check.Expect(slot.Live == 3, "a negative lifespan is the engine's 'never dies' and is honoured");

        // ---- gravity and the inherited-velocity decay. Both are the engine's exact arithmetic, and both are
        // what a comparison shot would show first if they were wrong.
        Reset();
        e = Params(ModeFountain);
        e.Life = 10f; e.Gravity = 1f; e.BirthRate = 0f;
        s = Slot0(in e);
        slot = ref _tab[0];
        Particle q = Take(ref slot);
        Clear(q);
        q.Vel = new Vector3(1f, 0f, 0f);
        q.InhVel = new Vector3(0f, 1f, 0f);
        q.InhDecay = 1f;
        Standard(ref slot, ref e, IntPtr.Zero, 0.5f);
        check.Expect(Near(q.Pos, new Vector3(0.5f, 0.5f, 0f)),
          "one half-second step moves by velocity AND by the inherited velocity scaled by its own decay");
        check.Expect(MathF.Abs(q.Vel.Z - (-9.81f * 0.5f)) < 1e-4f,
          "gravity is mass * -9.81 * dt into the Z velocity, applied after the position");
        check.Expect(MathF.Abs(q.InhDecay - 0.5f) < 1e-6f,
          "the inherited velocity decays by |(1 - dt)| per step -- the engine's own frame-rate-dependent term");
        q.Splat = 1;
        Vector3 held = q.Pos;
        Standard(ref slot, ref e, IntPtr.Zero, 0.5f);
        check.Expect(q.Pos == held, "a landed splat does not integrate at all");

        // ---- the p2p bezier's shape. t=0 must sit exactly on the emitter and t=1 exactly on the target, or
        // a chain starts or ends in mid-air; combineTime 0 means "on the curve from birth".
        Vector3 c0 = new(1f, 2f, 3f), c3 = new(5f, 2f, 3f);
        check.Expect(Near(CubeAt(c0, c0, c3, c3, 0f), c0) && Near(CubeAt(c0, c0, c3, c3, 1f), c3),
          "the p2p curve begins on the emitter and ends on the target");
        check.Expect(Near(CubeAt(c0, c0, c3, c3, 0.5f), (c0 + c3) * 0.5f),
          "...and with both control points on the ends it is a straight line, so midway is the midpoint");

        // ---- the lightning fractal. Determinism is the property that matters: one emitter, one seed, one
        // shape -- otherwise a bolt reshapes itself every frame and reads as noise rather than as lightning.
        Reset();
        e = Params(ModeLightning);
        e.BoltRadius = 1f; e.ZigZag = 0f; e.BirthRate = 9f; e.Life = 1f;
        s = Slot0(in e);
        slot = ref _tab[0];
        slot.Cap = 9;
        for (int i = 0; i < 9; i++) { Particle p = Take(ref slot); Clear(p); p.Pos = new Vector3(i, 0f, 0f); }
        uint seed = slot.Rng;
        Fractal(ref slot, ref e, Vector3.UnitY, Vector3.UnitZ, 4, 4, 1f);
        Vector3 a1 = At(ref slot, 4).Pos, a2 = At(ref slot, 2).Pos;
        check.Expect(a1.X == 4f && (a1.Y != 0f || a1.Z != 0f),
          "the fractal displaces the midpoint off the axis without moving it along it");
        check.Expect(a2 != new Vector3(2f, 0f, 0f), "...and recurses into the halves");
        slot.Rng = seed;
        for (int i = 0; i < 9; i++) At(ref slot, i).Pos = new Vector3(i, 0f, 0f);
        Fractal(ref slot, ref e, Vector3.UnitY, Vector3.UnitZ, 4, 4, 1f);
        check.Expect(At(ref slot, 4).Pos == a1 && At(ref slot, 2).Pos == a2,
          "the same seed gives the same bolt -- the stream is per-emitter, not shared with the engine's rand()");
        e.BoltRadius = 0f;
        for (int i = 0; i < 9; i++) At(ref slot, i).Pos = new Vector3(i, 0f, 0f);
        Fractal(ref slot, ref e, Vector3.UnitY, Vector3.UnitZ, 4, 4, 1f);
        check.Expect(At(ref slot, 4).Pos == new Vector3(4f, 0f, 0f),
          "a zero radius leaves a straight bolt rather than dividing by a zero range");

        // ---- the motion-blur trail and the roll wrap.
        Reset();
        e = Params(ModeFountain);
        e.BirthRate = 0f; e.Life = 10f;
        s = Slot0(in e);
        slot = ref _tab[0];
        slot.Cap = 4;
        e.Type = 6; e.BlurLength = 1f;
        Particle b = Take(ref slot);
        Clear(b);
        b.Pos = new Vector3(4f, 0f, 0f); b.PrevPos = Vector3.Zero;
        Blur(ref slot, ref e, 0.5f);
        check.Expect(Near(b.PrevPos, new Vector3(2f, 0f, 0f)),
          "render type 6's trailing point lerps toward the head over blurLength");
        e.Type = 0;
        b.PrevPos = Vector3.Zero;
        Blur(ref slot, ref e, 0.5f);
        check.Expect(b.PrevPos == Vector3.Zero, "...and no other render type pays for it");
        b.Roll = TwoPi - 0.1f;
        Roll(b, 1f, 0.5f);
        check.Expect(b.Roll > -TwoPi && b.Roll < TwoPi,
          "roll wraps into +-2pi instead of growing until sin loses its precision");

        // ---- the flag bit map. Eleven bits read as one word; one off-by-one there turns every emitter into
        // a differently wrong one, silently. The two the DRAW reads are in the same word, measured on
        // MdlNodeEmitter::IsTinted @0x4aafc0 (>> 3) and IsSplat @0x4ab070 (>> 9).
        check.Expect(FP2P == 1 && FP2PSel == 2 && FWind == 4 && FBounce == 0x10 && FRandom == 0x20
                     && FInherit == 0x40 && FInheritVel == 0x80 && FInheritLocal == 0x100
                     && FInheritPart == 0x400 && FTinted == 8 && FSplat == 0x200,
          "the MdlNodeEmitter flag bits match the Is* accessors' shifts (0,1,2,3,4,5,6,7,8,9,10)");

        // ---- the update-mode string compare, which is what picks the whole behaviour.
        check.Expect(Is(Lit("Fountain"), "Fountain") && !Is(Lit("Fountainhead"), "Fountain")
                     && !Is(Lit("Single"), "Fountain") && Is(Lit("Lightning"), "Lightning"),
          "the update mode matches the model's whole literal, not a prefix of it");
        // ...and the DB side reads the same four names out of the model's own string, case-insensitively,
        // because the ASCII corpus spells them both ways and the engine's compare is strcasecmp.
        check.Expect(ModeOf("fountain") == ModeFountain && ModeOf("EXPLOSION") == ModeExplosion
                     && ModeOf("Lightning") == ModeLightning && ModeOf("anything else") == ModeSingle,
          "the DB filler picks the same four modes off the parsed literal, case-insensitively");
        check.Expect(RenderTypeOf("Normal") == 0 && RenderTypeOf("Billboard_to_Local_Z") == 1
                     && RenderTypeOf("Billboard_to_World_Z") == 2 && RenderTypeOf("Linked") == 3
                     && RenderTypeOf("Aligned_to_World_Z") == 4 && RenderTypeOf("Aligned_to_Particle_Dir") == 5
                     && RenderTypeOf("Motion_Blur") == 6 && RenderTypeOf("") == 0,
          "...and the seven render types in PartEmitter::Initialize's own order, unknown falling through to 0");
        check.Expect(BlendOf("Normal") == 0 && BlendOf("Punch-Through") == 1 && BlendOf("Lighten") == 2,
          "...and the three blend modes: normal 0, punch-through 1, anything else additive");

        // ---- the draw order. This is a DECISION with a history: the first cut sorted descending on a note
        // that called the engine's sort "back to front", and it is ascending. Getting it wrong recolours
        // every overlapping pair of alpha plumes and nothing else says so.
        Reset();
        _tabN = 4;
        for (int i = 0; i < 4; i++) { _tab[i] = default; _tab[i].Part = i + 1; }
        _tab[0].Dist = 9f; _tab[1].Dist = 1f; _tab[2].Dist = 5f; _tab[3].Dist = 5f;
        // The two at equal distance share a Gob, so the tiebreak reaches them; 3 declares the lower order.
        _tab[2].Gob = (IntPtr)0x1000; _tab[2].Order = 7;
        _tab[3].Gob = (IntPtr)0x1000; _tab[3].Order = 2;
        _orderN = 4;
        _order[0] = 0; _order[1] = 1; _order[2] = 2; _order[3] = 3;
        SortDrawOrder();
        check.Expect(_order[0] == 1 && _order[3] == 0,
          "the sort is ASCENDING squared distance -- NEAREST first, which is what the engine's comparator and "
          + "its forward draw loop do, not back to front");
        check.Expect(_order[1] == 3 && _order[2] == 2,
          "...and a tie between two emitters of one Gob breaks on the model's render order, ascending");
        _tab[3].Gob = (IntPtr)0x2000;
        _order[0] = 0; _order[1] = 1; _order[2] = 2; _order[3] = 3;
        SortDrawOrder();
        check.Expect(_order[1] == 2 && _order[2] == 3,
          "...but two UNRELATED emitters at one distance keep bucket order: the tiebreak needs a shared Gob");
        check.Expect(!Before(0, 0), "the comparator is irreflexive, or the insertion sort walks off the front");


        // ---- the block's edge. The engine has NO per-emitter ceiling -- it births from the recycle list and,
        // when that is empty, from operator new with nothing to stop it (FountainEmitter::Update @0x4b1c70) --
        // so every refusal here costs a particle vanilla would have drawn, whatever sized the block. One that
        // counts nothing is the silent fallback CX-45 forbids, and it is invisible in a frame.
        Slot edge = default;
        int lost0 = _capLost;
        bool said0 = _capSaid;
        _capSaid = true;                                   // the one-shot line is not this check's business
        edge.Cap = 4; edge.Live = 4;
        check.Expect(Take(ref edge).IsNull && _capLost == lost0 + 1,
          "a birth refused by a full block is counted, whatever sized the block");
        check.Expect(Room(ref edge, 3) == 0 && _capLost == lost0 + 4,
          "...and a burst clamped ahead of the loop counts every birth it clamped away");
        edge.Live = 1;
        check.Expect(Room(ref edge, 3) == 3 && _capLost == lost0 + 4, "a burst that fits costs nothing");
        check.Expect(Room(ref edge, 5) == 3 && _capLost == lost0 + 6, "...and one that half fits counts the rest");
        _capLost = lost0; _capSaid = said0;

        // ---- and the sizing that keeps those refusals from arising. Each term comes off a birth loop: an
        // explosion's whole population lands in ONE step, so sizing it by rate x life alone -- which is what
        // it used to do -- lost most of every bang from an emitter with a lifespan under a second.
        check.Expect(Want(ModeSingle, 100f, 10f, 1f) == 1, "a single emitter is one particle whatever it declares");
        check.Expect(Want(ModeLightning, 12f, 3f, 0.1f) == 12, "a bolt is its fixed chain of birthRate links");
        check.Expect(Want(ModeExplosion, 50f, 0.5f, 0.016f) >= 50,
          "an explosion has room for its whole burst even when rate x life is a fraction of it");
        check.Expect(Want(ModeFountain, 10f, 4f, 0.1f) >= 42,
          "a fountain holds its standing population plus the burst one step can meter");
        check.Expect(Want(ModeFountain, 10f, 4f, 1f) > Want(ModeFountain, 10f, 4f, 0.016f),
          "...and a longer step meters a bigger burst, so the block grows with it");
        check.Expect(Want(ModeFountain, 0f, 0f, 0f) >= 1 && Want(ModeLightning, 0f, 0f, 0f) == 1,
          "nothing ever asks for a block of zero");
        // A grow ABANDONS the old block (the arena is a bump allocator), so an emitter whose controller ramps
        // birthrate would burn ~N^2/2 of the arena growing by ones and take every other emitter down with it
        // at the reset.
        check.Expect(Block(1) == 1 && Block(32) == 32 && Block(33) == 64 && Block(MaxSlots) == MaxSlots,
          "a block is a power of two, so a ramping emitter grows O(log N) times, not N");
        check.Expect(Block(0) == 1 && Block(-5) == 1, "...and is never zero or negative, whatever Want returned");

        BothSources(check);
        ParamCacheSelfCheck(check);

        Reset();
        log.Info(check.Ok ? "EmitterSim.SelfCheck: passed" : "EmitterSim.SelfCheck: FAILED");
    }

    /// <summary><b>The filler, over one emitter:</b> thirty parameters stated as a parsed
    /// <see cref="EmitterDef"/> with the matching controller rest keys, read back through
    /// <see cref="FillDb"/>, then the slot keys the database re-tenants on -- plus the three field names that
    /// mean something else in the MDL, which are <see cref="FillDb"/>'s own mapping.</summary>
    private static void BothSources(Checker check)
    {
        Reset();
        DbInputs.Reset();

        // The parameter set, stated once. Deliberately awkward values: a rate that needs the accumulator, a
        // lifespan that reaps mid-run, a non-zero spawn rectangle and a spread so the RNG is exercised.
        const float Rate = 7f, Life = 1.5f, Vel = 3f, RandV = 0.5f, Spread = 0.8f, Spin = 2f, Mass = 1f,
                    XSize = 0.4f, YSize = 0.25f, Threshold = 0.3f, Blur = 0.6f, BoltDelay = 0.2f,
                    BoltRadius = 0.9f, BoltScale = 1.3f, Drag = 0.5f, Grav = 4f, Bez2 = 1f, Bez3 = 2f,
                    Comb = 3f, A0 = 0.9f, A1 = 0.1f, Fps = 12f, F0 = 1f, F1 = 5f,
                    SX0 = 0.5f, SX1 = 1.5f, SY0 = 0.2f, SY1 = 0.7f, Bnc = 0.4f;
        const int Flags = FRandom | FInheritVel;

        // ---- the database side ----
        ModelDef m = DbModel(Flags);
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        DbId ph = SceneDb.Parts.Alloc();
        int part = ph.Index;
        DbFeed.InitPart(part);
        SceneDb.PartObj[part] = obj;
        SceneDb.PartModel[part] = m;
        SceneDb.PartNode[part] = 1;
        SceneDb.PartScale[part] = 1f;
        SceneDb.ObjScale[obj.Index] = 1f;
        SceneDb.PartWorld[part] = Matrix4x4.CreateTranslation(10f, 20f, 3f);
        SceneDb.ObjWorld[obj.Index] = SceneDb.PartWorld[part];
        SceneDb.ObjRot[obj.Index] = Quaternion.Identity;

        Emitter d = default;
        check.Expect(FillDb(part, ref d), "the filler accepts the emitter");
        // The three field names that mean something else in the file. Getting any of them wrong is silent:
        // the emitter simply behaves as if the parameter were zero.
        check.Expect(d.ConeCap == 0.75f && d.WindStrength == 1.25f && d.WindFalloff == 2.5f,
          "the MDL's `deadspace`, `blastRadius` and `blastLength` are the engine's coneCap, windStrength and "
          + "windFalloff (node+0x9c/+0xa0/+0xa4, InternalParseField)");
        check.Expect(d.DeadSpace == Threshold,
          "...and the engine's deadSpace -- the p2p arrival radius -- is the MDL's `threshold` CONTROLLER "
          + "(file id 188 -> PartEmitter+0xd4)");

        // The slot keys: a DB part index the database re-tenants must not inherit the dead emitter's block.
        uint gen = SceneDb.PartObj[part].Gen;
        int k1 = FindDb(part, gen);
        check.Expect(FindDb(part, gen) == k1, "a DB slot is found again by (part, generation)");
        int k2 = FindDb(part, gen + 1);
        check.Expect(k2 != k1 || _tab[k2].Live == 0,
          "...and a re-tenanted part index takes a fresh slot rather than the dead emitter's particles");
        check.Expect(_slotOfPart[part] == k2 + 1,
          "...and the part -> slot map names the LIVE slot, so the O(1) lookup and the generation check agree");
        ForgetDbPart(part);
        check.Expect(_tab[k1].Part < 0 || _tab[k2].Part < 0,
          "a destroyed part releases its slot on the edge, not on a stale sweep");
        check.Expect(_slotOfPart[part] == 0,
          "...and a closed slot's map entry goes with it, or the next tenant of that part index would be "
          + "handed the dead emitter's slot without its generation ever being asked");
        MapSelfCheck(check);

        SceneDb.Parts.Free(ph);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty();
        SceneDb.Objects.TakeDirty();
        DbInputs.Reset();
        Reset();
    }

    /// <summary>The two per-frame skips the fill takes -- the rest keys memoised on the node and
    /// the cull that runs ahead of the snapshot. Each assertion is the smallest thing that fails if the skip
    /// ever hides a real change: a model or node swapped under a part, an animation's write, the memo's
    /// sharing between parts of one model, and the cull's verdict against the full fill's own numbers.</summary>
    private static void ParamCacheSelfCheck(Checker check)
    {
        Reset();

        check.Expect(_chIds.Length == ChCount && _chBlock.Length == ChCount
                  && _chIds[ChBirthRate] == Ctl.EmBirthRate && _chIds[ChLifeExp] == Ctl.EmLifeExp
                  && _chIds[ChColorStart] == Ctl.EmColorStart && _chIds[ChColorEnd] == Ctl.EmColorEnd
                  && _chIds[ChSizeEnd] == Ctl.EmSizeEnd && _chIds[ChSizeMidY] == Ctl.EmSizeMidY
                  && _chIds[ChDetonate] == Ctl.EmDetonate && _chIds[ChMass] == Ctl.EmMass,
          "every Ch* ordinal addresses its own controller id -- an ordinal off by one silently reads another "
          + "parameter's keys");
        check.Expect(_chBlock[ChSizeMid] < 0 && _chBlock[ChSizeMidY] < 0 && _chBlock[ChBirthRate] >= 0,
          "sizeMid/sizeMidY are no animatable channel of that sink, so they are always the node's rest key");

        ModelDef m = TwoEmitters(11f, 22f);
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        DbId ph = SceneDb.Parts.Alloc();
        int part = ph.Index;
        DbFeed.InitPart(part);
        SceneDb.PartObj[part] = obj;
        SceneDb.PartModel[part] = m;
        SceneDb.PartNode[part] = 1;
        SceneDb.PartScale[part] = 1f;
        SceneDb.ObjScale[obj.Index] = 1f;
        SceneDb.PartWorld[part] = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        SceneDb.ObjWorld[obj.Index] = SceneDb.PartWorld[part];
        SceneDb.ObjRot[obj.Index] = Quaternion.Identity;

        Emitter e = default;
        check.Expect(FillDb(part, ref e) && e.BirthRate == 11f, "the node's own rest keys fill the snapshot");

        check.Expect(m.Nodes[1].EmRest != null && m.Nodes[2].EmRest == null,
          "the keys are memoised on the NODE the fill read, and on no other");

        // The node of a part changes: a different NodeDef, so a different memo. Nothing has to be told.
        SceneDb.PartNode[part] = 2;
        check.Expect(FillDb(part, ref e) && e.BirthRate == 22f,
          "a part whose NODE changed reads the new node's keys -- a memo keyed by the PART would have kept the "
          + "old node's birth rate forever");
        SceneDb.PartNode[part] = 1;
        check.Expect(FillDb(part, ref e) && e.BirthRate == 11f, "...and changing it back reads back");

        // The model changes (a re-tenanted part index is the same thing): again a different NodeDef.
        ModelDef m2 = TwoEmitters(33f, 44f);
        SceneDb.PartModel[part] = m2;
        check.Expect(FillDb(part, ref e) && e.BirthRate == 33f,
          "a part whose MODEL changed reads the new model's keys, which is also what a recycled part index "
          + "does -- the memo cannot go stale, because it is a function of the node and nothing else");
        SceneDb.PartModel[part] = m;

        // One memo per NODE, shared: 400 torches of one model must not hold 400 identical blocks.
        DbId ph2 = SceneDb.Parts.Alloc();
        int twin = ph2.Index;
        DbFeed.InitPart(twin);
        SceneDb.PartObj[twin] = obj;
        SceneDb.PartModel[twin] = m;
        SceneDb.PartNode[twin] = 1;
        SceneDb.PartScale[twin] = 1f;
        Vector4[]? memo = m.Nodes[1].EmRest;
        Emitter t2 = default;
        check.Expect(FillDb(twin, ref t2) && t2.BirthRate == 11f
                  && ReferenceEquals(m.Nodes[1].EmRest, memo),
          "a second part of the same model reads the SAME memo rather than resolving a block of its own");

        // The animation. The rest keys are memoised; what a controller writes never is.
        FillDb(part, ref e);
        DbEmitterChannels.Write(part, Ctl.EmBirthRate, new Vector4(99f, 0f, 0f, 0f), 1f);
        check.Expect(FillDb(part, ref e) && e.BirthRate == 99f,
          "an animated channel reaches the very next fill -- the memo holds REST keys and the block is read "
          + "live, so an emitter whose rate is keyed still ramps");
        float animRadius = e.ViewRadius;
        DbEmitterChannels.Write(part, Ctl.EmSizeEnd, new Vector4(40f, 0f, 0f, 0f), 1f);
        FillDb(part, ref e);
        check.Expect(e.ViewRadius > animRadius, "...and an animated SIZE widens the view radius with it");

        // The cull pre-pass: its three outputs against the full fill's own.
        check.Expect(CullDb(part, out Vector3 cp, out float cr, out int cm)
                  && cp == e.WorldPos && cr == e.ViewRadius && cm == e.Mode,
          "the cull pre-pass and the full fill agree bit for bit on the position, the radius and the mode -- "
          + "two verdicts would cull an emitter the draw still expects, or step one the cull dropped");
        DbEmitterChannels.Forget(part);
        check.Expect(FillDb(part, ref e) && e.BirthRate == 11f && CullDb(part, out _, out cr, out _)
                  && cr == e.ViewRadius,
          "a dropped animation block leaves both reading the rest keys again, still in agreement");

        // ...and both refuse the same emitter, so the walk's no-def verdict still counts one thing.
        SceneDb.PartNode[part] = 0;
        check.Expect(!CullDb(part, out _, out _, out _) && !FillDb(part, ref e),
          "a node with no emitter declaration is refused by the cull and the fill alike");

        SceneDb.Parts.Free(ph);
        SceneDb.Parts.Free(ph2);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty();
        SceneDb.Objects.TakeDirty();
        Reset();
    }

    /// <summary>Two emitter nodes on one model, with different birth rates: the only way to state "the part's
    /// node changed" to the rest-key memo.</summary>
    private static ModelDef TwoEmitters(float rate1, float rate2)
    {
        var m = new ModelDef { Name = "cx_emitcache", ContentHash = 0xCAC4E };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        m.Nodes = new[] { root, EmitNode("puff", 1, rate1), EmitNode("puff2", 2, rate2) };
        m.NodeByName["root"] = 0; m.NodeByName["puff"] = 1; m.NodeByName["puff2"] = 2;
        return m;
    }

    private static NodeDef EmitNode(string name, int index, float rate)
        => new()
        {
            Name = name, Index = index, Parent = 0, Kind = NodeKind.Emitter,
            Emitter = new EmitterDef { Update = "Fountain", Render = "Normal", Blend = "Lighten" },
            Controllers = new[]
            {
                Ctl1(Ctl.EmBirthRate, rate), Ctl1(Ctl.EmLifeExp, 2f), Ctl1(Ctl.EmVelocity, 1f),
                Ctl1(Ctl.EmSizeStart, 0.5f), Ctl1(Ctl.EmSizeEnd, 1f), Ctl1(Ctl.EmMass, 1f),
            },
        };

    /// <summary>The part -> slot map against the one thing it must never do -- hand a part a slot that
    /// is not its own. The eviction arm is the dangerous one: a full table reclaims the emitter longest unseen,
    /// and if the evicted part kept its map entry it would later be given a slot another emitter is burning in,
    /// silently inheriting its particles and its texture reference.</summary>
    private static void MapSelfCheck(Checker check)
    {
        Reset();
        // A full table, so the next ask has to reclaim rather than grow. The parts need not be live: FindDb
        // keys on the index alone, which is exactly the property the map has to preserve.
        for (int p = 1; p <= MaxEmitters; p++) FindDb(p, 1u);
        check.Expect(_tabN == MaxEmitters && _slotOfPart[1] == 1 && _slotOfPart[MaxEmitters] == MaxEmitters,
          "a full table maps every part to its own slot");
        // Take only reclaims a slot nothing has walked for a frame; the walk's own counter is what says so.
        _frame += 4;
        int s = FindDb(MaxEmitters + 1, 1u);
        check.Expect(s == 0, "a full table gives the newcomer the slot longest unseen (the first, all tied)");
        check.Expect(_slotOfPart[1] == 0,
          "...and the evicted part's map entry is gone, not left pointing at the newcomer's slot");
        check.Expect(_slotOfPart[MaxEmitters + 1] == s + 1, "...which is the newcomer's own");
        check.Expect(FindDb(MaxEmitters + 1, 1u) == s && FindDb(2, 1u) == 1,
          "every other part still finds the slot it had");
        // The map is an INDEX, not the authority: Slot.Part and Slot.Gen are the keys. A part whose entry
        // drifted has to find its own slot through the scan's part match -- a second slot for a part that
        // already has one would draw its particles twice and leak the first slot's texture reference, on a
        // path with no counter to say so.
        _slotOfPart[3] = 0;
        int had = _tabN;
        check.Expect(FindDb(3, 1u) == 2 && _tabN == had && _slotOfPart[3] == 3,
          "a part whose map entry went missing finds its own slot in the scan and re-maps it, rather than "
          + "being handed a second one");
        _slotOfPart[4] = 1;
        check.Expect(FindDb(4, 1u) == 3 && _tabN == had && _slotOfPart[4] == 4,
          "...and an entry naming ANOTHER part's slot is no entry at all");
        Reset();
        check.Expect(_slotOfPart[2] == 0 && _slotOfPart[MaxEmitters] == 0,
          "an area edge or context loss empties the map with the table -- a surviving entry would point into "
          + "slots the next area re-tenants");
    }

    /// <summary>The database twin of <see cref="BothSources"/>'s engine emitter: one emitter node whose
    /// <see cref="EmitterDef"/> and controller REST keys state the same parameter set.</summary>
    private static ModelDef DbModel(int flags)
    {
        var m = new ModelDef { Name = "cx_bothsrc", ContentHash = 0xB0D15 };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var emit = new NodeDef
        {
            Name = "puff", Index = 1, Parent = 0, Kind = NodeKind.Emitter,
            Emitter = new EmitterDef
            {
                Update = "Fountain", Render = "Normal", Blend = "Lighten", Texture = "fxpa_flame",
                XGrid = 4, YGrid = 2, Loop = true, TwoSidedTex = true, Flags = (uint)flags,
                DeadSpace = 0.75f, BlastRadius = 1.25f, BlastLength = 2.5f,
            },
            Controllers = new[]
            {
                Ctl1(Ctl.EmBirthRate, 7f), Ctl1(Ctl.EmLifeExp, 1.5f), Ctl1(Ctl.EmVelocity, 3f),
                Ctl1(Ctl.EmRandVel, 0.5f), Ctl1(Ctl.EmSpread, 0.8f), Ctl1(Ctl.EmParticleRot, 2f),
                Ctl1(Ctl.EmMass, 1f), Ctl1(Ctl.EmXSize, 0.4f), Ctl1(Ctl.EmYSize, 0.25f),
                Ctl1(Ctl.EmThreshold, 0.3f), Ctl1(Ctl.EmBlurLength, 0.6f),
                Ctl1(Ctl.EmLightningDelay, 0.2f), Ctl1(Ctl.EmLightningRadius, 0.9f),
                Ctl1(Ctl.EmLightningScale, 1.3f), Ctl1(Ctl.EmDrag, 0.5f), Ctl1(Ctl.EmGrav, 4f),
                Ctl1(Ctl.EmP2PBezier2, 1f), Ctl1(Ctl.EmP2PBezier3, 2f), Ctl1(Ctl.EmCombineTime, 3f),
                Ctl1(Ctl.EmAlphaStart, 0.9f), Ctl1(Ctl.EmAlphaEnd, 0.1f), Ctl1(Ctl.EmFps, 12f),
                Ctl1(Ctl.EmFrameStart, 1f), Ctl1(Ctl.EmFrameEnd, 5f),
                Ctl1(Ctl.EmSizeStart, 0.5f), Ctl1(Ctl.EmSizeEnd, 1.5f),
                Ctl1(Ctl.EmSizeStartY, 0.2f), Ctl1(Ctl.EmSizeEndY, 0.7f), Ctl1(Ctl.EmBounceCo, 0.4f),
                Ctl3(Ctl.EmColorStart, 1f, 0.5f, 0.25f), Ctl3(Ctl.EmColorEnd, 0f, 0.25f, 1f),
            },
        };
        m.Nodes = new[] { root, emit };
        m.NodeByName["root"] = 0; m.NodeByName["puff"] = 1;
        return m;
    }

    private static ControllerTrack Ctl1(int type, float v)
        => new() { Type = type, Columns = 1, Times = new[] { 0f }, Values = new[] { v } };

    private static ControllerTrack Ctl3(int type, float x, float y, float z)
        => new() { Type = type, Columns = 3, Times = new[] { 0f }, Values = new[] { x, y, z } };

    /// <summary>A snapshot with the fixed declarations a check needs and nothing else: an owner at the origin,
    /// the identity orientation, no target and no tile fade.</summary>
    private static Emitter Params(int mode)
    {
        Emitter e = default;
        e.Part = -1;
        e.Mode = mode;
        e.Scale = 1f;
        e.PartScale = 1f;
        e.M.Orient = new Quat { W = 1f };
        e.M.PrevRot = new Quat { W = 1f };
        Basis(ref e);
        return e;
    }

    /// <summary>Open slot 0 for this snapshot, as the walk would. Returns its index.</summary>
    private static int Slot0(in Emitter e)
    {
        _tabN = 1;
        _tab[0] = default;
        _tab[0].Part = e.Part;
        _tab[0].Rng = 0x1234567u;
        Open(ref _tab[0], in e);
        return 0;
    }

    /// <summary>One fountain frame, in <see cref="Advance"/>'s order with the mover left out: the mover reads
    /// a live Scene for wind and collision, and nothing this check asserts goes through it.</summary>
    private static void Tick(ref Slot slot, ref Emitter e, float dt)
    {
        FountainAge(ref slot, ref e, dt);
        FountainBirth(ref slot, ref e, dt);
    }

    private static Span<byte> Lit(string s)
    {
        byte[] b = new byte[s.Length + 1];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;

    /// <summary>The cubic the p2p bezier mover evaluates, isolated so the check exercises the WEIGHTS rather
    /// than the engine calls around them.</summary>
    private static Vector3 CubeAt(Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3, float t)
    {
        float u = 1f - t, tt = t * t;
        return c0 * (u * u * u) + c1 * (3f * t * u * u) + c2 * (3f * tt * u) + c3 * (tt * t);
    }
}
