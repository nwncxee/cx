using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Framework;

namespace CX.Alloy;

/// <summary>What the route walk may stop reading, and what must still reach the draw. Runs the real walk over three
/// seated parts and the real writers. Oracle: hand-written alphas, orders and counts.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int RigSlots = 3;

    /// <summary>One object of three parts, seated in slots 0..2 of an otherwise empty table, in scene 1.</summary>
    private sealed class RouteRig
    {
        public DbId Obj;
        public int O, First;
        public static readonly DbId Scene = DbId.Make(1, 1), Elsewhere = DbId.Make(2, 1);
    }

    private static RouteRig? RouteRigUp(Checker check)
    {
        var rig = new RouteRig { Obj = SceneDb.Objects.Alloc() };
        if (rig.Obj.IsNull || !SceneDb.Parts.AllocRange(RigSlots, out rig.First))
        { check.Expect(false, "the rig got no object or part records"); return null; }
        rig.O = rig.Obj.Index;
        DbFeed.InitObject(rig.O);
        SceneDb.ObjScene[rig.O] = RouteRig.Scene;
        SceneDb.ObjFlags[rig.O] = ObjFlag.Static | ObjFlag.Tile;
        SceneDb.ObjPartFirst[rig.O] = rig.First;
        SceneDb.ObjPartCount[rig.O] = RigSlots;
        SceneDb.HitObjFadeAlpha[rig.O] = 1f;
        Array.Clear(_clsStart); Array.Clear(_clsEnd);
        for (int k = 0; k < RigSlots; k++)
        {
            int p = rig.First + k;
            DbFeed.InitPart(p);
            SceneDb.PartObj[p] = rig.Obj;
            SceneDb.PartSlot[p] = k;
            _oTri[k] = (IntPtr)1; _oDbPart[k] = p;
            _oCls[k] = (byte)AlloyClass.Opaque; _oLit[k] = true; _oA2c[k] = false;
            _oBox[k] = new Box { Min = new Vector3(-1f, -1f, 0f), Max = new Vector3(1f, 1f, 2f) };
            UnstampChase(k);
            _order[k] = k;
        }
        _clsEnd[(int)AlloyClass.Opaque] = RigSlots;
        for (int c = (int)AlloyClass.Opaque + 1; c < AlloyClasses.Count; c++) { _clsStart[c] = RigSlots; _clsEnd[c] = RigSlots; }
        _orderCount = RigSlots;
        _dbPassScene = RouteRig.Scene;
        _passFrustumOk = false;
        _occHitN = 0;
        _frameSeq = 1;   // no slot's audit turn: (slot + 1) & 63 is never 0 for slots 0..2
        return rig;
    }

    private static void RouteRigDown(RouteRig rig)
    {
        for (int k = 0; k < RigSlots; k++)
        {
            SceneDb.PartSlot[rig.First + k] = -1;
            _oTri[k] = IntPtr.Zero; _oDbPart[k] = -1;
            UnstampChase(k);
        }
        Array.Clear(_clsStart); Array.Clear(_clsEnd);
        _orderCount = 0;
        SceneDb.Parts.FreeRange(rig.First, RigSlots);
        SceneDb.Objects.Free(rig.Obj);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty();
    }

    /// <summary>How many parts one walk took from the memo, as the period line says it.</summary>
    private static string WalkNote(out int routed)
    {
        ResetChasePeriod();
        int frames = Counters._frames;
        Counters._frames = 1;
        routed = RouteWalk();
        string note = ChaseNote();
        Counters._frames = frames;
        return note;
    }

    private static bool Solid(int routed)
    {
        bool ok = routed == RigSlots;
        for (int a = 0; a < routed; a++) ok &= _passAlpha[a] == 1f && _passCls[a] == (byte)AlloyClass.Opaque;
        return ok;
    }

    private static bool FromMemo(int count) => WalkNote(out _).Contains($"({count} from the memo");

    /// <summary>The change <paramref name="change"/> shows on the very next walk, its reverse brings the parts back
    /// solid, and the walk after that answers from the memo again.</summary>
    private static void ExpectTakesEffect(Checker check, RouteRig rig, string what, Action change, Func<int, bool> drawn, Action reverse, int settle = 1)
    {
        RouteWalk();
        check.Expect(FromMemo(RigSlots), $"E8 {what}: the rig is not answered from the memo before the change");
        change();
        int n = RouteWalk();
        check.Expect(drawn(n), $"E8 {what}: the next frame does not draw the change");
        reverse();
        for (int k = 0; k < settle; k++) n = RouteWalk();
        check.Expect(Solid(n), $"E8 {what}: reversed, the parts are not solid again");
        check.Expect(FromMemo(RigSlots), $"E8 {what}: reversed, the parts did not return to the memo");
    }

    internal static bool RouteMemoSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.RouteMemoApprovedExamples.SelfCheck");
        int frameWas = _frameSeq;
        DbId sceneWas = _dbPassScene;
        bool frustumWas = _passFrustumOk;
        float stepWas = _occT.Step, dtWas = _occT.Dt, floorWas = _occT.Floor;
        RouteRig? rig = RouteRigUp(check);
        if (rig == null) return false;
        int o = rig.O, p0 = rig.First;

        // Given a town with no fades, no keyhole and nothing in the camera's ray fan
        // When the [alloy] period line prints
        // Then it reports how many routed parts per frame came from the memo, and that number is nearly all of the routed parts
        {
            RouteWalk();
            check.Expect(FromMemo(RigSlots), $"E5: a settled view is not answered from the memo (got \"{WalkNote(out _)}\")");
            _occHitN = 1; _occHitObj[0] = 0xffffffffu;
            check.Expect(FromMemo(0), "E5: a frame with a placeable in the ray fan counts its opaque parts as from the memo");
            _occHitN = 0;
        }

        // Given a crowd of creatures walking and playing animations, none fading
        // When the [alloy] period line prints
        // Then the crowd's parts count as from the memo
        {
            SceneDb.ObjFlags[o] = ObjFlag.Creature;
            RouteWalk();
            for (int k = 0; k < RigSlots; k++) SceneDb.Parts.Mark(p0 + k, Dirty.Anim);
            SceneDb.Objects.Mark(o, Dirty.Anim);
            SceneDb.ObjWorld[o] = Matrix4x4.CreateTranslation(1f, 2f, 3f);
            check.Expect(FromMemo(RigSlots), "E6: an animating creature's parts lost the memo");
            ObjectSlots.WriteShift(rig.Obj, 0, Vector3.One, 0.5f, false);
            check.Expect(FromMemo(0), "E6: a creature fading in still counts as from the memo");
            ObjectSlots.WriteShift(rig.Obj, 0, Vector3.One, 1f, false);
            RouteWalk();
        }

        // Given a creature standing solid in view
        // When the server drops it from perception and it starts fading
        // Then it draws translucent on the first frame its fade moved, with no solid frame in between, and fades as smoothly as today
        {
            RouteWalk();
            ObjectSlots.WriteShift(rig.Obj, 0, Vector3.One, 0.5f, false);
            int n = RouteWalk();
            bool faded = n == RigSlots;
            for (int a = 0; a < n; a++) faded &= _passAlpha[a] == 0.5f && _passCls[a] == (byte)AlloyClass.Blended;
            check.Expect(faded, "E7: the first frame of the fade did not draw translucent");
            ObjectSlots.WriteShift(rig.Obj, 0, Vector3.One, 0.25f, false);
            n = RouteWalk();
            faded = n == RigSlots;
            for (int a = 0; a < n; a++) faded &= _passAlpha[a] == 0.25f;
            check.Expect(faded, "E7: the next step of the fade did not draw");
            ObjectSlots.WriteShift(rig.Obj, 0, Vector3.One, 1f, false);
            check.Expect(Solid(RouteWalk()), "E7: a finished fade-in is not solid");
            check.Expect(FromMemo(RigSlots), "E7: a creature that finished fading in did not go back to the memo");
        }
        SceneDb.ObjFlags[o] = ObjFlag.Static | ObjFlag.Tile;

        // Given a part answered by the memo
        // When any of these changes: a tile starts its camera fade, a door fades, an MDL alpha controller moves, the keyhole opens over a roof, a placeable enters the camera's ray fan, an LOD alternate swaps, or the object moves to another scene
        // Then the next frame draws it as today's build would (translucent, dissolved, the other alternate, or not at all in this view)
        {
            bool OneAt(int n, float alpha)
            {
                bool seen = false;
                for (int a = 0; a < n; a++) seen |= _pass[a] == 0 && _passAlpha[a] == alpha && _passCls[a] == (byte)AlloyClass.Blended;
                return seen && n == RigSlots;
            }
            ExpectTakesEffect(check, rig, "tile fade", () => SceneDb.SetPartFade(p0, 0.5f), n => OneAt(n, 0.5f), () => SceneDb.SetPartFade(p0, 1f));
            ExpectTakesEffect(check, rig, "door fade", () => DoorFade.StepDb(rig.Obj, 0.5f, false, 0f), n => OneAt(n, 0.5f), () => DoorFade.StepDb(rig.Obj, 0.5f, true, 0f));
            ExpectTakesEffect(check, rig, "alpha controller", () => SceneDb.SetPartAlpha(p0, 0.25f), n => OneAt(n, 0.25f), () => SceneDb.SetPartAlpha(p0, 1f));
            var order = new[] { 0, 1, 2 };
            ExpectTakesEffect(check, rig, "keyhole",
                () => DbKeyhole.Write(1, -10f, 0f, 0f, 1f, -5f, order, RigSlots, _oDbPart, default),
                n =>
                {
                    bool all = n == RigSlots;
                    for (int a = 0; a < n; a++) all &= _passDis[a] && _passAlpha[a] == 1f;
                    return all;
                },
                () => DbKeyhole.Write(0, 0f, 0f, 0f, 0f, 0f, order, RigSlots, _oDbPart, default));
            _occT.Step = 1f; _occT.Dt = 1000f; _occT.Floor = 0.25f;
            ExpectTakesEffect(check, rig, "ray fan",
                () => { _occHitN = 1; _occHitObj[0] = rig.Obj.Value; },
                n =>
                {
                    bool all = n == RigSlots;
                    for (int a = 0; a < n; a++) all &= _passAlpha[a] == 0.25f;
                    return all;
                },
                () => _occHitN = 0, settle: 3);
            SceneDb.PartLodTree[p0] = 1;
            ExpectTakesEffect(check, rig, "lod swap", () => SceneDb.SetObjLodShown(o, 1), n => n == RigSlots - 1, () => SceneDb.SetObjLodShown(o, 0));
            SceneDb.PartLodTree[p0] = 0;
            ExpectTakesEffect(check, rig, "another scene", () => SceneDb.SetObjScene(o, RouteRig.Elsewhere), n => n == 0, () => SceneDb.SetObjScene(o, RouteRig.Scene));
        }

        // Given a writer that changes a part's alpha without telling the memo
        // When the audit's turn reaches that part
        // Then cx.log names the slot and part in a route memo MISS line, the period line counts it, and the part draws correctly from then on
        {
            ExtenderLog? was = _log;
            _log = log;
            long missWas = RouteMemoMisses();
            string clean = WalkNote(out _);
            check.Expect(clean.Contains($"{missWas} memo misses"), $"E10: a period with no such writer reads no new misses (got \"{clean}\")");
            SceneDb.PartAlpha[p0] = 0.5f;
            _frameSeq = 64;   // slot 0's audit turn
            int n = 0;
            string text = AllOf(LinesFrom(log, () => n = RouteWalk()));
            _log = was;
            check.Expect(text.Contains($"route memo MISS slot 0 part {p0}"), $"E10: the audit did not name the part (got \"{text}\")");
            check.Expect(RouteMemoMisses() == missWas + 1, "E10: the audit did not count the miss");
            bool drawn = false;
            for (int a = 0; a < n; a++) drawn |= _pass[a] == 0 && _passAlpha[a] == 0.5f;
            check.Expect(drawn, "E10: the part did not draw correctly on the audit's frame");
            _frameSeq = 1;
            n = RouteWalk();
            drawn = false;
            for (int a = 0; a < n; a++) drawn |= _pass[a] == 0 && _passAlpha[a] == 0.5f;
            check.Expect(drawn, "E10: the part did not stay correct after the audit");
        }

        RouteRigDown(rig);
        _frameSeq = frameWas; _dbPassScene = sceneWas; _passFrustumOk = frustumWas;
        _occT.Step = stepWas; _occT.Dt = dtWas; _occT.Floor = floorWas; _occHitN = 0;
        log.Info(check.Ok ? "AlloyRenderer.RouteMemoApprovedExamples.SelfCheck: passed" : "AlloyRenderer.RouteMemoApprovedExamples.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
