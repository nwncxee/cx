using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// One emitter's step, ported from <c>PartEmitter::AnimateParticles</c> @0x4ac660, the four <c>Update</c>
/// overrides, the three movers, <c>chkParticleLife</c>, <c>chkRandom</c>, <c>RecursiveFractal</c> and the
/// spawn. Behaviour, not transcription: the engine's arithmetic is kept exactly where it IS the feature (the
/// bezier weights, gravity, inherited-velocity decay, the fractal displacement) and not where it is not.
/// </summary>
internal static unsafe partial class EmitterSim
{
    /// <summary>Step one non-chunk emitter: the inherit basis, then its mode, then its mover.</summary>
    private static void Advance(ref Slot slot, ref Emitter e, IntPtr scene, float dt)
    {
        // AnimateParticles' own opening exit: a fading Gob or tile does not step at all. Keeping it matters
        // for more than parity -- a tile mid-fade is being unloaded, and its emitter's Gob is about to go.
        // The factor is TileFadeRamp's: the engine's TileData+0xc8 has no writer once the BSP visitor stands
        // down, so a tile fogged at load would keep its flames dark after it was explored.
        if (e.Faded) { _fadeExit++; return; }

        Rebase(ref e);
        Basis(ref e);

        // Room for what the parameters now ask for: a controller animates birthrate and lifespan, so an
        // emitter's population ceiling moves while it lives.
        if (!Grow(ref slot, in e, dt)) return;

        // ORDER, and it is the engine's: every Update ages and spins what is already alive, runs the MOVER,
        // and births only then. Spawning first would integrate a newborn over the step it was born in -- an
        // extra dt of gravity and velocity, plus a bounce test against a floor it has not travelled toward
        // yet -- so a fountain's stream would start one frame downwind of where the model put it.
        switch (slot.Mode)
        {
            case ModeFountain:
                FountainAge(ref slot, ref e, dt);
                Move(ref slot, ref e, scene, dt);
                FountainBirth(ref slot, ref e, dt);
                break;
            case ModeExplosion:
                ExplosionAge(ref slot, ref e, dt);
                Move(ref slot, ref e, scene, dt);
                ExplosionBirth(ref slot, ref e, scene, dt);
                break;
            // Lightning places every particle itself and takes no mover at all.
            case ModeLightning: Lightning(ref slot, ref e, dt); break;
            // Single's one birth RETURNS before the mover, so a newborn sits still for its first frame --
            // which is the same rule, expressed by the engine as an early exit rather than as an ordering.
            default:
                if (Single(ref slot, ref e, dt))
                    Move(ref slot, ref e, scene, dt);
                break;
        }
    }

    /// <summary>The inherit basis, from <c>AnimateParticles</c>. Two distinct records move here and conflating
    /// them is the easy bug: <see cref="EmitMut.PrevWorldPos"/> tracks the OWNER's motion (what an
    /// <c>inherit_local</c> particle is dragged by), <see cref="EmitMut.PrevPos"/> the frame the particles are
    /// expressed in. Order is the engine's: record off the OLD transform, only then refresh it.</summary>
    private static void Rebase(ref Emitter e)
    {
        int flags = e.Flags;
        bool local = (flags & FInheritLocal) != 0, ofPart = (flags & FInheritPart) != 0;
        Vector3 delta = Vector3.Zero;
        if (local && e.HasOwner && e.M.Seeded != 0)
        {
            delta = e.OwnerPos - e.M.PrevWorldPos;
        }
        else if (e.M.Seeded == 0)
        {
            World(ref e);
            if (e.HasOwner && (ofPart || local)) { e.M.PrevWorldPos = e.OwnerPos; e.M.PrevWorldRot = e.OwnerRot; }
            if (ofPart) { e.M.PrevWorldPos = e.M.Pos; e.M.PrevWorldRot = e.M.Orient; }
            e.M.Seeded = 1;
        }
        e.M.PrevPos = local ? e.M.Pos + delta : e.M.Pos;
        e.M.PrevRot = e.M.Orient;
        if (e.HasOwner) World(ref e);
    }

    /// <summary><c>Part::GetWorld</c> into the emitter's own two fields, which is what the engine's
    /// <c>vcall +0x20</c> does at three points in <c>AnimateParticles</c>. The transform itself was taken once
    /// by the snapshot -- it cannot move between those three points inside one frame.</summary>
    private static void World(ref Emitter e)
    {
        e.M.Pos = e.WorldPos; e.M.Orient = e.WorldRot;
    }

    /// <summary>Rebuild the 3x3 spawn basis from the emitter's quaternion, column-major -- X axis, Y axis,
    /// Z axis, which is the frame <c>randomPosition</c> lays its rectangle in and <c>randomDirection</c>
    /// launches along.</summary>
    private static void Basis(ref Emitter e)
    {
        Quat o = e.M.Orient;
        float w = o.W, x = o.X, y = o.Y, z = o.Z;
        e.M.Bx = new Vector3(1f - 2f * (y * y + z * z), 2f * (x * y + z * w), 2f * (x * z - w * y));
        e.M.By = new Vector3(2f * (x * y - z * w), 1f - 2f * (z * z + x * x), 2f * (y * z + w * x));
        e.M.Bz = new Vector3(2f * (x * z + w * y), 2f * (y * z - w * x), 1f - 2f * (x * x + y * y));
    }

    // ---- the four update modes -----------------------------------------------------------------------

    /// <summary><c>FountainEmitter::Update</c> @0x4b1c70, in its two halves: age and spin what is alive, and --
    /// after the mover has run -- birth this frame's share. The accumulator is why a slow emitter works at
    /// all: 3 births a second at 60fps is 0.05 of a particle a frame, and the remainder is carried in
    /// <see cref="EmitMut.SpawnAcc"/> until it reaches one whole spacing.</summary>
    private static void FountainAge(ref Slot slot, ref Emitter e, float dt)
    {
        e.M.Birthing = (byte)(e.BirthRate > 0f ? 1 : 0);
        Reap(ref slot, ref e, dt);
        Blur(ref slot, ref e, dt);
        Spin(ref slot, ref e, dt);
    }

    /// <inheritdoc cref="FountainAge"/>
    private static void FountainBirth(ref Slot slot, ref Emitter e, float dt)
    {
        float rate = e.BirthRate;
        int n = (int)rate;
        if (n <= 0) { _rate0++; return; }

        if (!e.Trail)
        {
            // Time-metered birth. The engine divides the accumulated count by n+1 and births the remainder,
            // which is the same thing as "one particle every 1/rate seconds" with the fraction carried --
            // said the second way because the first is an artefact of integer division.
            float acc = e.M.SpawnAcc + dt;
            float spacing = 1f / rate;
            if (acc < spacing) { e.M.SpawnAcc = acc; return; }
            // Room, not Cap: the block already holds this emitter's standing population, and clamping the
            // BURST against the whole block let a full emitter ask for more than it had room for and lose the
            // difference with nothing said. Want sizes the block for this burst, so a loss here is real.
            int births = Room(ref slot, (int)(acc * rate));
            for (int i = 0; i < births; i++) Spawn(ref slot, ref e, dt, 1f);
            e.M.SpawnAcc = 0f;
            return;
        }

        // A trail (spawntype 1; FountainEmitter::Update @0x4b1d9f tests it, not p2p): birth along the distance the
        // emitter moved, evenly spaced, carrying the leftover in P2pLeftover so the trail stays continuous.
        Vector3 lead = e.M.Pos - e.M.PrevPos + e.M.P2pLeftover;
        float len = lead.Length();
        Vector3 dir = len > 0f ? lead / len : Vector3.Zero;
        // The one burst no sizing can cover: a chain is (int)(rate x distance travelled) long and a teleport
        // makes that unbounded. Clamped, and the particles it costs are counted like any other loss.
        int count = Room(ref slot, (int)(rate * len));
        float step = 1f / rate;
        for (int i = 0; i < count; i++)
        {
            int before = slot.Live;
            Spawn(ref slot, ref e, dt, 1f);
            // A birth the cap refused must not re-place the PREVIOUS particle, which is what reading
            // `Live - 1` unconditionally would do -- the chain's last link would jump instead of the new one.
            if (slot.Live == before) break;
            Particle p = At(ref slot, slot.Live - 1);
            // The FIRST particle of a chain is placed one whole span back from the emitter; every later one
            // walks forward from where the previous frame's chain ended.
            p.Pos = slot.Live < 2 ? e.M.Pos - dir * len
                                  : e.M.PrevPos - e.M.P2pLeftover + dir * ((i + 1) * step);
        }
        e.M.P2pLeftover = lead - dir * (count * step);
    }

    /// <summary><c>SingleEmitter::Update</c> @0x4b2740: exactly one particle, re-seeded at the end of its
    /// life when the node declares <c>loop</c> and frozen at age zero when it does not. Returns whether the
    /// mover should run -- false on the frame the one particle is born or removed, which is where the engine
    /// takes its own early exit.</summary>
    private static bool Single(ref Slot slot, ref Emitter e, float dt)
    {
        if ((int)e.BirthRate == 0)
        {
            if (slot.Live > 0) Kill(ref slot, 0);
            return false;
        }
        if (slot.Live == 0) { Spawn(ref slot, ref e, dt, 1f); return false; }
        Particle p = At(ref slot, 0);
        float life = e.Life;
        float age = p.Age + dt;
        p.Age = age;
        if (life > 0f ? age > life && e.Loop : age < 0f) p.Age = 0f;
        Roll(p, e.Spin, dt);
        Blur(ref slot, ref e, dt);
        return true;
    }

    /// <summary><c>ExplosionEmitter::Update</c> @0x4b23a0, in its two halves: nothing until the model's
    /// <c>Detonate</c> event fires, then the whole population at once, then nothing again. The byte is the
    /// REQUEST, written by the gob callback and cleared here -- left set, the explosion re-fires every frame;
    /// the latch is "already fired", so one detonate event cannot spawn twice.</summary>
    private static void ExplosionAge(ref Slot slot, ref Emitter e, float dt)
    {
        Reap(ref slot, ref e, dt);
        Blur(ref slot, ref e, dt);
        Spin(ref slot, ref e, dt);
    }

    /// <inheritdoc cref="ExplosionAge"/>
    private static void ExplosionBirth(ref Slot slot, ref Emitter e, IntPtr scene, float dt)
    {
        int n = (int)e.BirthRate;
        bool asked = e.Detonate;
        float latch = 0f;
        if (asked)
        {
            latch = 1f;
            if (n > 0 && e.M.ModeClock < 1f)
            {
                float gust = e.WindStrength;
                if (gust != 0f) Gust(in e, scene, gust, e.WindFalloff);
                n = Room(ref slot, n);
                for (int i = 0; i < n; i++) Spawn(ref slot, ref e, dt, 1f);
            }
        }
        e.M.ModeClock = latch;
        e.Detonate = false;
    }

    /// <summary>A detonation's point-source gust. There is no engine Scene to reach the
    /// <c>WindManager</c> through, so it goes straight into Alloy's own list -- the same list, the same four
    /// arguments, and the strength <c>ExplosionEmitter::Update</c> @0x4b259e pins at 1.0.</summary>
    private static void Gust(in Emitter e, IntPtr scene, float radius, float falloff)
    {
        {
            if (!AlloyWind.Owned) { _gustRefused++; return; }
            Vector3 p = e.M.Pos;
            AlloyWind.AddGust(p, radius, falloff, 1f);
            return;
        }
        IntPtr wm = new CX.Engine.Scene(scene).WindManager;
        if (wm == IntPtr.Zero) return;
        Vector3 at = e.M.Pos;
        AurXform.Gust(wm, new AurXform.V3 { X = at.X, Y = at.Y, Z = at.Z }, radius, falloff);
    }

    private static int _gustRefused;

    /// <summary><c>LightningEmitter::Update</c>: a fixed chain of <c>birthRate</c> particles from the emitter
    /// to its target, re-fractured every <c>boltDelay</c> seconds and rigid-transformed
    /// in between so a bolt does not crawl while its ends move.</summary>
    private static void Lightning(ref Slot slot, ref Emitter e, float dt)
    {
        int want = (int)e.BirthRate;
        if (want <= 0 || !e.HasTarget)
        {
            if (!e.HasTarget && want > 0) _p2pNoTarget++;
            while (slot.Live > 0) Kill(ref slot, slot.Live - 1);
            return;
        }
        if (want > slot.Cap) { CapLost(want - slot.Cap); want = slot.Cap; }
        e.M.ModeClock += dt;

        while (slot.Live < want) Seed(ref slot, ref e);
        while (slot.Live > want) Kill(ref slot, slot.Live - 1);

        // Every particle ages, and one that has not advanced since it was placed starts from zero: the bolt's
        // segments fade along their own life the way any other emitter's do.
        for (int i = 0; i < slot.Live; i++)
        {
            Particle p = At(ref slot, i);
            if (p.Age == p.FrameTime) { p.Age = 0f; p.FrameTime = 0f; }
            p.Age += dt;
        }
        Blur(ref slot, ref e, dt);

        Vector3 tip = e.TargetPos;
        if (e.M.ModeClock <= e.BoltDelay)
        {
            // Between fractals: carry the existing shape rigidly onto the new endpoints instead of
            // rebuilding it, which is what stops a live bolt from boiling.
            Vector3 delta = tip - e.M.Pos;
            Quat arc = RotationArc(Norm(e.M.BoltLastDelta), Norm(delta));
            Vector3 from = e.M.BoltLastPos;
            for (int i = 0; i < slot.Live; i++)
            {
                Particle p = At(ref slot, i);
                p.Pos = e.M.Pos + Rotate(arc, p.Pos - from);
            }
            e.M.BoltLastDelta = delta;
            e.M.BoltLastPos = e.M.Pos;
            return;
        }

        // A fresh bolt: the near end at the emitter (jittered by the spawn rectangle), the far end at the
        // target, the middle by midpoint displacement, then one zigzag pass along it.
        float scale = e.Scale;
        Particle head = At(ref slot, 0);
        if (e.SpawnX == 0f && e.SpawnY == 0f) head.Pos = e.M.Pos;
        else RandomPosition(ref slot, ref e, head, scale);
        Particle tail = At(ref slot, slot.Live - 1);
        tail.Pos = tip;

        Vector3 span = head.Pos - tail.Pos;
        if (span == Vector3.Zero) return;
        e.M.ModeClock = 0f;
        Vector3 axis = Norm(span);
        Vector3 perpA = Perp(axis);
        Vector3 perpB = Vector3.Cross(axis, perpA);
        int mid = slot.Live / 2;
        Fractal(ref slot, ref e, perpA, perpB, mid, mid, scale);

        float seg = span.Length() / slot.Live * 0.5f;
        if (slot.Live > 2 && seg >= Epsilon)
        {
            float amp = e.PartScale * e.ZigZag;
            for (int i = 1; i < slot.Live - 1; i++)
            {
                Vector3 d = Coin(ref slot.Rng) ? perpA : -perpA;
                float off = Rnd(ref slot.Rng, (int)(seg * 100f)) * Centi;
                At(ref slot, i).Pos += d * (off * amp);
            }
        }
        e.M.BoltLastDelta = tip - e.M.Pos;
        e.M.BoltLastPos = e.M.Pos;
    }

    /// <summary><c>RecursiveFractal</c>: midpoint displacement between two already-placed particles, halving
    /// the span each level. Iterative on the right half and recursive on the left, exactly as the engine's
    /// own loop-plus-call is -- which bounds the depth at log2(count).</summary>
    private static void Fractal(ref Slot slot, ref Emitter e, Vector3 a, Vector3 b,
                                int mid, int half, float scale)
    {
        float radius = e.BoltRadius;
        while (true)
        {
            if (mid - half < 0 || mid + half >= slot.Live || mid <= 0 || mid >= slot.Live) return;
            Vector3 p = (At(ref slot, mid - half).Pos + At(ref slot, mid + half).Pos) * 0.5f;
            if (radius >= Epsilon)
            {
                Vector3 d1 = Coin(ref slot.Rng) ? a : -a;
                p += d1 * (Rnd(ref slot.Rng, (int)(radius * 100f)) * Centi * scale);
                Vector3 d2 = Coin(ref slot.Rng) ? b : -b;
                p += d2 * (Rnd(ref slot.Rng, (int)(radius * 100f)) * Centi * scale);
            }
            At(ref slot, mid).Pos = p;
            if (half == 0) return;
            half = half / 2;
            Fractal(ref slot, ref e, a, b, mid - half, half, scale);
            mid += half;
        }
    }

    // ---- shared per-frame passes ---------------------------------------------------------------------

    /// <summary><c>chkParticleLife</c>: age every particle and retire the ones past their life, stably -- the
    /// live slice stays oldest-first, because an index-domain fade and the linked render types read it as an
    /// ORDER. A negative lifespan is the engine's "never die", and an age it would drive below zero is pinned
    /// at zero along with the UV clock, which freezes a landed splat's animation.</summary>
    private static void Reap(ref Slot slot, ref Emitter e, float dt)
    {
        float life = e.Life;
        int w = 0;
        for (int i = 0; i < slot.Live; i++)
        {
            Particle p = At(ref slot, i);
            float age = p.Age;
            if (life >= 0f && age >= life) { _died++; continue; }
            float next = age + dt;
            if (next < 0f) { p.Age = 0f; p.FrameTime = 0f; }
            else p.Age = next;
            if (w != i) Swap(ref slot, w, i);
            w++;
        }
        // The retired pointers are still in the slice, past the new live count -- which IS the free list.
        slot.Live = w;
    }

    /// <summary>Render type 6's trailing position, lerped toward the current one over the emitter's
    /// <c>blurLength</c>. Owning the vertex build already meant owning this field; the simulation is where it
    /// belongs.</summary>
    private static void Blur(ref Slot slot, ref Emitter e, float dt)
    {
        if (e.Type != 6) return;
        float len = e.BlurLength;
        if (len <= 0f) return;
        float t = MathF.Min(dt, len) / len, it = 1f - t;
        for (int i = 0; i < slot.Live; i++)
        {
            Particle p = At(ref slot, i);
            p.PrevPos = p.PrevPos * it + p.Pos * t;
        }
    }

    /// <summary>Roll, wrapped into ±2π every step so the angle never grows into a range where a float loses
    /// the precision <c>sincos</c> needs.</summary>
    private static void Spin(ref Slot slot, ref Emitter e, float dt)
    {
        float rate = e.Spin;
        if (rate == 0f) return;
        for (int i = 0; i < slot.Live; i++) Roll(At(ref slot, i), rate, dt);
    }

    private static void Roll(Particle p, float rate, float dt)
    {
        float r = p.Roll + rate * dt;
        p.Roll = r > TwoPi ? r - TwoPi : r < -TwoPi ? r + TwoPi : r;
    }

    // ---- the pointer slice ---------------------------------------------------------------------------

    /// <summary>Live particle <paramref name="i"/> of this emitter. The pool records are laid out as engine
    /// <c>Particle</c>s, so the view is the engine's own.</summary>
    private static Particle At(ref Slot slot, int i) => new(_ptrs[slot.Base + i]);

    private static void Swap(ref Slot slot, int a, int b)
    {
        IntPtr t = _ptrs[slot.Base + a];
        _ptrs[slot.Base + a] = _ptrs[slot.Base + b];
        _ptrs[slot.Base + b] = t;
    }

    /// <summary>Retire one particle, preserving the order of the ones before it: the pointer goes to the back
    /// of the slice, which is the free list.</summary>
    private static void Kill(ref Slot slot, int i)
    {
        IntPtr dead = _ptrs[slot.Base + i];
        for (int k = i; k < slot.Live - 1; k++) _ptrs[slot.Base + k] = _ptrs[slot.Base + k + 1];
        _ptrs[slot.Base + slot.Live - 1] = dead;
        slot.Live--;
        _died++;
    }

    /// <summary>Take the next free record, or null when the block is full -- which the caller treats as "no
    /// birth this frame" and which is ALWAYS a particle vanilla would have drawn: the engine births from
    /// <c>operator new</c> with nothing to stop it, so a full block is a burst <see cref="Want"/> failed to
    /// size for. Counted and named once.</summary>
    private static Particle Take(ref Slot slot)
    {
        if (slot.Live >= slot.Cap)
        {
            CapLost(1);
            return new Particle(IntPtr.Zero);
        }
        Particle p = new(_ptrs[slot.Base + slot.Live]);
        slot.Live++;
        _spawned++;
        return p;
    }
}
