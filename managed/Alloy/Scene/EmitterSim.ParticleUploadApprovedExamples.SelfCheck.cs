using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>The particle record upload's approved examples (approved-spec): simulated emitters stepped through
    /// the real pool and drawn through the Vulkan pass's staging. Oracles: particle counts and positions set by hand.</summary>
    private static void ParticleUploadExamples(ExtenderLog log)
    {
        var check = new Checker(log, "EmitterSim.ParticleUploadApprovedExamples.SelfCheck");
        UploadRig rig = UploadRig.Up(check);
        if (rig.Ok)
        {
            UploadFollowsDraw(check, rig);
            GrowingKeepsMotion(check, rig);
            ArrivalKeepsMotion(check, rig);
            BusyAreaNeverBlinks(check, rig);
            ParticleUploadRegressions(check, rig);
        }
        rig.Down();
        log.Info(check.Ok ? "EmitterSim.ParticleUploadApprovedExamples.SelfCheck: passed" : "EmitterSim.ParticleUploadApprovedExamples.SelfCheck: FAILED");
    }

    // Given a simulated emitter that grew from 10 particles to 2,000 and fell back to 10, and a second emitter that has gone off screen
    // When the next frame is drawn
    // Then the particle records upload (vk particle records in cx.log's top uploads) holds the 10 drawn particles, and nothing for the 2,000 the first once held or for the off-screen one
    private static void UploadFollowsDraw(Checker check, UploadRig rig)
    {
        rig.Fresh();
        foreach (int n in new[] { 10, 100, 500, 1000, 2000 })
        {
            rig.Populate(0, n, 0f, 0f);
            rig.Populate(1, 50, 0f, 1000f);
            rig.Frame(0, 1);
        }
        rig.Populate(0, 10, 0f, 0f);
        int at = rig.Frame(0);
        int recs = Particles.VkRecords.Length;
        check.Expect(at == 10 && recs == 10,
            $"E1: a frame drawing 10 particles uploads 10 records (drew {at}, uploaded {recs})");
    }

    // Given a simulated emitter that births new particles every frame, its older particles moving
    // When the next frame is drawn
    // Then each particle it drew the frame before carries the step it moved since, not a zero step
    private static void GrowingKeepsMotion(Checker check, UploadRig rig)
    {
        rig.Fresh();
        int kept = 0, held = 0;
        for (int f = 0; f < 11; f++)
        {
            int n = 10 + f;
            rig.Populate(0, n, f * 0.5f, 0f);
            rig.Frame(0);
            if (f == 0) continue;
            for (int i = 0; i < n - 1; i++)
            {
                held++;
                if (rig.PrevOf(UploadRig.At(i, f * 0.5f, 0f)) is Vector3 prev && Near(prev, UploadRig.At(i, (f - 1) * 0.5f, 0f))) kept++;
            }
        }
        check.Expect(held == 145 && kept == held,
            $"E3: every particle drawn the frame before carries its 0.5 m step while the emitter births ({kept} of {held})");
    }

    // Given a particle drawn last frame that has moved since
    // When a new emitter comes on screen in the same frame, and the record store grows to hold it
    // Then that particle still carries the step it moved, not a zero step
    private static void ArrivalKeepsMotion(Checker check, UploadRig rig)
    {
        rig.Fresh();
        Particles.ContextLost();
        rig.Populate(0, 10, 0f, 0f);
        rig.Frame(0);
        rig.Populate(1, 3000, 0f, 1000f);
        rig.Populate(0, 10, 0.5f, 0f);
        int placed = rig.Frame(0, 1);
        int kept = 0;
        for (int i = 0; i < 10; i++)
            if (rig.PrevOf(UploadRig.At(i, 0.5f, 0f)) is Vector3 prev && Near(prev, UploadRig.At(i, 0f, 0f))) kept++;
        check.Expect(kept == 10, $"E4: the 10 moving particles keep their step across the record store's grow ({kept} of 10, {placed} placed)");
        int still = 0;
        for (int i = 0; i < 3000; i += 300)
            if (rig.PrevOf(UploadRig.At(i, 0f, 1000f)) is Vector3 prev && Near(prev, UploadRig.At(i, 0f, 1000f))) still++;
        check.Expect(still == 10, $"E4: a particle on its first frame on screen carries a zero step ({still} of 10)");
    }

    // Given a simulated emitter whose particle count climbs from 10 to 2,000 over a minute of frames
    // When every frame of that minute is drawn
    // Then the particles line in cx.log reports no block-allocator resets, and every frame draws its particles
    private static void BusyAreaNeverBlinks(Checker check, UploadRig rig)
    {
        rig.Fresh();
        Particles.ResetPeriod();
        const int Frames = 3600;
        int missed = 0;
        for (int f = 0; f < Frames; f++)
        {
            int n = 10 + 1990 * f / (Frames - 1);
            rig.Populate(0, n, 0f, 0f);
            if (rig.Frame(0) != n) missed++;
        }
        string line = Particles.Stats(Frames);
        check.Expect(!line.Contains("BLOCK-ALLOCATOR RESETS", StringComparison.Ordinal),
            $"E5: a minute of an emitter climbing from 10 to 2,000 resets no block: {line}");
        check.Expect(missed == 0, $"E5: ...and every frame draws all of its particles ({missed} frames short)");
    }

    /// <summary>Two scene-database emitters on one hand-built model, each a sim slot in the real pool, drawn
    /// through <see cref="Particles.StageVk"/> with a stand-in texture name.</summary>
    private sealed class UploadRig
    {
        /// <summary>Any nonzero texture name: the pass declines an emitter whose texture has not arrived.</summary>
        private const uint Tex = 7u;
        public bool Ok;
        private readonly DbId _obj;
        private readonly DbId[] _parts = new DbId[2];
        private readonly int[] _slots = new int[2];
        private int _at;

        private UploadRig(DbId obj) => _obj = obj;

        public static UploadRig Up(Checker check)
        {
            Reset();
            Particles.Forget();
            DbId obj = SceneDb.Objects.Alloc();
            DbFeed.InitObject(obj.Index);
            var rig = new UploadRig(obj);
            ModelDef m = TwoEmitters(11f, 22f);
            SceneDb.ObjScale[obj.Index] = 1f;
            SceneDb.ObjModel[obj.Index] = m;
            SceneDb.ObjWorld[obj.Index] = Matrix4x4.Identity;
            SceneDb.ObjRot[obj.Index] = Quaternion.Identity;
            for (int k = 0; k < 2; k++)
            {
                rig._parts[k] = SceneDb.Parts.Alloc();
                int part = rig._parts[k].Index;
                DbFeed.InitPart(part);
                SceneDb.PartObj[part] = obj;
                SceneDb.PartModel[part] = m;
                SceneDb.PartNode[part] = 1 + k;
                SceneDb.PartScale[part] = 1f;
                SceneDb.PartWorld[part] = Matrix4x4.Identity;
            }
            FrameInputs.HasCamera = true;
            FrameInputs.CameraRot = Quaternion.Identity;
            check.Expect(Particles.VkOn, "the rig: the particle pass is on in this host");
            rig.Ok = Particles.VkOn;
            return rig;
        }

        /// <summary>The scene-database part emitter <paramref name="k"/> draws.</summary>
        public int Part(int k) => _parts[k].Index;

        /// <summary>Particles the simulation's arena holds from the start: a grow of the arena drops every block and
        /// the pass's table with it, which is not the record store's grow these examples are about.</summary>
        private const int ArenaRoom = 1 << 14;

        /// <summary>Start from an empty table, pool and record store, as an area does.</summary>
        public void Fresh()
        {
            Reset();
            Reserve(ArenaRoom);
            Particles.Forget();
            for (int k = 0; k < 2; k++)
            {
                _slots[k] = FindDb(_parts[k].Index, _parts[k].Gen);
                _tab[_slots[k]].Mode = ModeFountain;
            }
        }

        /// <summary>Emitter <paramref name="k"/> holds <paramref name="n"/> live particles, particle i at
        /// <see cref="At"/>(i, dx, dy), its block grown the way the simulation grows one.</summary>
        public void Populate(int k, int n, float dx, float dy)
        {
            ref Slot slot = ref _tab[_slots[k]];
            Emitter e = Params(ModeFountain);
            e.BirthRate = n; e.Life = 1f;
            Grow(ref slot, in e, 0f);
            slot.Live = n;
            for (int i = 0; i < n; i++)
            {
                var p = new Particle(_ptrs[slot.Base + i]);
                p.Pos = At(i, dx, dy);
                p.Age = 0f; p.Frame = 0; p.Splat = 0;
            }
        }

        /// <summary>One frame that draws the emitters named, in that order. The particles it placed.</summary>
        public int Frame(params int[] drawn)
        {
            AlloyRenderer.PassGen++;
            foreach (int k in drawn) EnrollSlot(ref _tab[_slots[k]], Tex);
            Matrix4x4 view = Matrix4x4.Identity;
            _at = Particles.StageVk(&view, false, out _, out _, out _);
            return _at;
        }

        /// <summary>The previous centre the drawn record centred at <paramref name="centre"/> carries, or null when
        /// no record the frame's draw order references is centred there.</summary>
        public Vector3? PrevOf(Vector3 centre)
        {
            ReadOnlySpan<ParticleRec> recs = Particles.VkRecords;
            foreach (uint r in Particles.VkIndices(_at))
                if (r < recs.Length && Near(recs[(int)r].Centre, centre)) return recs[(int)r].Prev;
            return null;
        }

        /// <summary>Particle i's hand-placed position: a row along X, shifted by (dx, dy).</summary>
        public static Vector3 At(int i, float dx, float dy) => new(i * 0.01f + dx, dy, 1f);

        public void Down()
        {
            FrameInputs.HasCamera = false;
            Reset();
            Particles.Forget();
            SceneDb.ObjModel[_obj.Index] = null;
            foreach (DbId p in _parts) SceneDb.Parts.Free(p);
            SceneDb.Objects.Free(_obj);
            SceneDb.Parts.TakeDirty();
            SceneDb.Objects.TakeDirty();
        }
    }
}
