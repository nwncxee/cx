using System.Numerics;

namespace CX.Alloy.Db;

/// <summary>How often an object's POSE is advanced, by its distance from the world eye -- the half of
/// <see cref="VisibilityGateNotPorted"/>'s gate that needs no route stamp. Beyond <c>alloy.animdist</c> a step
/// runs every second or fourth pass with the time between handed over in one piece, which is one slow frame to
/// everything downstream; placement still composes every frame, so a creature walks where it walked.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Passes between pose steps in the two reduced bands.</summary>
    private const int MidStride = 2, FarStride = 4;
    /// <summary>The far band begins at this multiple of the detail distance. It is also where the dangly solve
    /// goes rigid: that solve costs what it SIMULATES, so a stride cannot make it cheaper -- only not solving
    /// can -- and the trade is only worth taking where a whole swing is a pixel of motion.</summary>
    private const float FarBand = 2f;
    /// <summary>Wall collision for hanging cloth is kept within this fraction of the detail distance: a cloak
    /// that does not swing through the wall behind it is a near-camera read.</summary>
    private const float WallBand = 0.5f;
    /// <summary>Band-edge hysteresis, as a fraction of the edge distance, so an object pacing an edge does not
    /// take its dangly parts down to rest and back every pass.</summary>
    private const float BandHyst = 0.15f;
    /// <summary>The longest animation time one reduced step may carry. A reduced step is one slow frame to the
    /// queue wrap, the event window and the dangly clamp, so past this the stride gives way rather than hand
    /// them a frame longer than the engine itself would.</summary>
    private const float MaxHeld = 0.1f;

    /// <summary>The band layer's whole state: the eye this pass bands against, the band and next-due pass of
    /// each object, the animation time each is holding, and the period counters.</summary>
    private sealed class RateState
    {
        internal readonly byte[] Band = new byte[SceneDb.MaxObjects];
        internal readonly int[] Due = new int[SceneDb.MaxObjects];
        internal readonly float[] Held = new float[SceneDb.MaxObjects];
        internal Vector3 Eye;
        internal bool On;
        /// <summary>World-scene animate passes, which is what a stride counts. NOT <see cref="_frame"/>: that
        /// counts every scene's animate, so with three panels open a stride of four would be one frame.</summary>
        internal int Pass;
        internal int Mid, Far, Deferred, Rigid, Walls, NoCamera;
    }

    private static RateState _rate = new();

    /// <summary>Latch the eye the member walk bands against -- the WORLD view's, as <see cref="LodChoose"/>
    /// takes it, and only for the world scene, so a GUI panel's models are posed at full rate whatever the
    /// camera is doing. No knob, no world scene or no measured world view stands the whole band down.</summary>
    private static void RateBegin(int sceneIndex)
    {
        _rate.On = false;
        if (!(Renderer.AnimDist.V > 0f)) return;
        if ((uint)sceneIndex >= SceneDb.MaxScenes || !SceneDb.SceneIsWorld[sceneIndex]) return;
        ref readonly AlloyView wv = ref AlloyCamera.WorldView;
        if (!wv.Valid || !Matrix4x4.Invert(wv.View, out Matrix4x4 inv)) { _rate.NoCamera++; return; }
        _rate.Eye = inv.Translation;
        _rate.Pass++;
        _rate.On = true;
    }

    /// <summary>End of the member walk: nothing outside it bands, so an external animate of a world object
    /// (<see cref="AnimateOneExternal"/>) steps at full rate as it always did.</summary>
    private static void RateEnd() => _rate.On = false;

    /// <summary>The animation time object <paramref name="i"/> steps by this pass -- <paramref name="dt"/> plus
    /// what it has been holding -- or a NEGATIVE number when this is a pass it skips. The one writer of
    /// <see cref="RateState.Band"/>. A behaviour is never reduced: an attach solver WRITES the object's
    /// placement from its parent's part, and a rider moving every fourth frame stutters at any distance.</summary>
    private static float RateStep(int i, float dt)
    {
        if (!_rate.On || _behavior[i] != 0)
        {
            _rate.Band[i] = 0;
            // The held time is handed over, never dropped: a band that stands down mid-session must not cost
            // the object the frames it was already holding.
            float owed = _rate.Held[i];
            _rate.Held[i] = 0f;
            return dt + owed;
        }
        float near = Renderer.AnimDist.V;
        int band = ChooseRateBand(RateDistance(i), near, near * FarBand, _rate.Band[i]);
        _rate.Band[i] = (byte)band;
        if (band == 1) _rate.Mid++;
        else if (band >= 2) _rate.Far++;
        int stride = band >= 2 ? FarStride : band == 1 ? MidStride : 1;
        float held = _rate.Held[i];
        if (stride > 1 && _rate.Pass < _rate.Due[i] && held + dt <= MaxHeld)
        {
            _rate.Held[i] = held + dt;
            _rate.Deferred++;
            return -1f;
        }
        _rate.Held[i] = 0f;
        _rate.Due[i] = _rate.Pass + stride;
        return dt + held;
    }

    /// <summary>How far object <paramref name="i"/>'s geometry is from the eye: its cull sphere's distance less
    /// its radius, because a 40 m tile gob whose origin is far off still has geometry at the camera. A record
    /// with no sphere yet has not composed once, so it reads as near and steps.</summary>
    private static float RateDistance(int i)
    {
        Vector4 s = SceneDb.ObjSphere[i];
        if (!(s.W > 0f)) return 0f;
        return (new Vector3(s.X, s.Y, s.Z) - _rate.Eye).Length() - s.W;
    }

    /// <summary>The band for <paramref name="dist"/>, from the current one, with hysteresis both ways and at
    /// most one band per call -- <see cref="ChooseLod"/>'s shape, for the same reason: popping on a boundary is
    /// the bug. Rising needs <c>dist &gt; E (1+h)</c>, falling needs <c>dist (1+h) &lt; E</c>. Pure.</summary>
    internal static int ChooseRateBand(float dist, float near, float far, int cur)
    {
        if (!(near > 0f) || !(far > near) || !(dist >= 0f)) return 0;
        if (cur < 0 || cur > 2) return 0;
        int plain = dist > far ? 2 : dist > near ? 1 : 0;
        if (plain == cur) return cur;
        float up = 1f + BandHyst;
        if (plain > cur) return dist > (cur == 0 ? near : far) * up ? cur + 1 : cur;
        return dist * up < (cur == 1 ? near : far) ? cur - 1 : cur;
    }

    /// <summary>Does object <paramref name="i"/> sit in the far band, where its dangly parts hang rigid at rest
    /// instead of being solved? Read by the part walk, after <see cref="RateStep"/> set the band.</summary>
    private static bool RateRestsDangly(int i) => _rate.On && _rate.Band[i] >= 2;

    /// <summary>Is <paramref name="part"/> near enough for its wall collision to be gathered and pushed
    /// against? Counted when it is not, because the push is a measurable share of the dangly solve.</summary>
    private static bool RateWallsNear(int part)
    {
        if (!_rate.On) return true;
        DbId o = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(o)) return true;
        if (RateDistance(o.Index) <= Renderer.AnimDist.V * WallBand) return true;
        _rate.Walls++;
        return false;
    }

    /// <summary>Forget object <paramref name="i"/>'s band: a recycled index must not inherit a stride, and its
    /// next step is due at once (a tree change owes a stitch and a palette, which the tail only pays on a step).</summary>
    private static void RateForget(int i)
    {
        _rate.Band[i] = 0;
        _rate.Due[i] = 0;
        _rate.Held[i] = 0f;
    }

    /// <summary>The band layer's period numbers. Objects are counted per visit, so they divide by frames; the
    /// skipped step and the rigid part counts are the saving itself.</summary>
    private static string RatePeriod(int frames)
    {
        RateState r = _rate;
        if (!(Renderer.AnimDist.V > 0f)) return "rate off (alloy.animdist 0)";
        int f = frames > 0 ? frames : 1;
        string s = $"rate: {r.Mid / f} object(s)/frame posed at 1/{MidStride}, {r.Far / f} at 1/{FarStride}, "
                 + $"{r.Deferred} pose step(s) skipped, {r.Rigid / f} dangly part(s)/frame held at rest, "
                 + $"{r.Walls / f} wall push(es)/frame skipped";
        if (r.NoCamera != 0) s += $", {r.NoCamera} pass(es) with NO WORLD VIEW measured (every object at full rate)";
        r.Mid = r.Far = r.Deferred = r.Rigid = r.Walls = r.NoCamera = 0;
        return s;
    }
}
