using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The door fade: a behaviour port of <c>Scene::FadeDoors</c> @0x467990's arithmetic -- integrator,
/// clamp and face test -- driven per door by <c>HitFeed</c> through <see cref="StepDb"/>. Not cosmetic: the
/// fade gates the PICK too, since <c>Gob::HitCheck</c> @0x4381d0 refuses a door whose alpha sits at
/// <c>mindooralpha</c>. GL-free, allocation-free.</summary>
internal static unsafe class DoorFade
{
    /// <summary>Seconds a door needs to cross the whole alpha range: the engine divides <c>DeltaT</c> by this
    /// (the 0.3f at 0x12ee9f0) and adds or subtracts the quotient.</summary>
    private const float FadeSeconds = 0.3f;
    /// <summary>How far the entered face's outward normal must lean toward the scene focus before the door is
    /// driven opaque -- the 0.1f at 0x12eeeac, compared as <c>0.1 &lt; dot</c>.</summary>
    private const float OpaqueDot = 0.1f;
    /// <summary>The engine's clamp, in its order: ceiling at 1 first, then the <c>mindooralpha</c> floor, so a
    /// floor above 1 wins. Both branches of the original land here.</summary>
    internal static float Clamp(float alpha, float floor) => MathF.Max(floor, MathF.Min(1f, alpha));

    /// <summary>One door object's new fade, into the two columns that carry it. The single writer on purpose:
    /// <see cref="SceneDb.HitObjFadeAlpha"/> is what the hit test gates a click on (<c>Gob::HitCheck</c>'s
    /// <c>mindooralpha</c> test), <c>SceneDb.PartFade</c> is what the draw multiplies in, and a door faded out
    /// of the picture but not the pick is the bug one writer prevents.</summary>
    internal static void StepDb(DbId obj, float step, bool opaque, float floor)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index;
        float a = Clamp(SceneDb.HitObjFadeAlpha[o] + (opaque ? step : -step), floor);
        SceneDb.HitObjFadeAlpha[o] = a;
        int first = SceneDb.ObjPartFirst[o];
        for (int i = first; i < first + SceneDb.ObjPartCount[o]; i++)
        {
            SceneDb.PartFade[i] = a;
            SceneDb.TouchPart(i, Dirty.Material);
        }
        SceneDb.Touch(obj, Dirty.Material);
    }

    /// <summary>Should this door be driven toward opaque? The engine's test from <c>Scene::FadeDoors</c>
    /// @0x467a6e..0x467d5b: a segment through the door's box, from the camera in the gob's frame to its mirror
    /// about the ROTATED centre; the entered face's outward normal must lean toward the scene focus, and a
    /// camera inside the box fades out. The mixed frames are parity: xy inverse-rotated, z the rotated centre's.</summary>
    internal static bool Opaque(Vector3 bmin, Vector3 bmax, Vector3 pos, Quaternion rot,
                               Vector3 camPos, Vector3 focus)
    {
        Vector3 centre = (bmin + bmax) * 0.5f;
        Vector3 rc = Vector3.Transform(centre, rot);
        Vector3 world = pos + rc;
        // `operator-(Quaternion const&)` @0x4b5d50 negates the STORED w and nothing else; for a rotation that
        // is the inverse, and Numerics reads the same four floats as (x, y, z, w).
        Vector3 local = Vector3.Transform(camPos - world, new Quaternion(rot.X, rot.Y, rot.Z, -rot.W));
        var a = new Vector3(local.X, local.Y, rc.Z);
        var b = new Vector3(2f * rc.X - local.X, 2f * rc.Y - local.Y, rc.Z);
        int face = BoxEnterFace(a, b, bmin, bmax);
        if (face < 0) return false;
        Vector3 n = default;
        // face 1..6 = -x, +x, -y, +y, -z, +z; face 0 (segment misses) takes the engine's own fallthrough,
        // axis 0 with a positive sign, and lets the dot decide.
        int axis = (face - 1) / 2;
        float sign = ((face - 1) & 1) != 0 ? 1f : -1f;
        if (axis == 0) n.X = sign; else if (axis == 1) n.Y = sign; else n.Z = sign;
        return Vector3.Dot(Vector3.Transform(n, rot), Normalize(focus - world)) > OpaqueDot;
    }

    /// <summary><c>normalize(Vector const&amp;)</c> @0x4b37d0: a zero vector comes back as (1, 0, 0).</summary>
    private static Vector3 Normalize(Vector3 v)
    {
        float len2 = v.LengthSquared();
        return len2 == 0f ? new Vector3(1f, 0f, 0f) : v * (1f / MathF.Sqrt(len2));
    }

    // Outcode bits, in the order BoxIntersect @0x4b7f20 ORs them (0x467f20 disassembly, @0x4b7f20..0x4b7fbc).
    private const int OutXLo = 0x20, OutXHi = 0x10, OutYLo = 0x08, OutYHi = 0x04, OutZLo = 0x02, OutZHi = 0x01;   // not-an-offset: the engine's own outcode bit values

    /// <summary><c>BoxIntersect(A, B, min, max)</c> @0x4b7f20: which face of the box the segment A-&gt;B
    /// enters. -1 when A is inside the box, 0 when the segment misses it, else 1..6 for -x, +x, -y, +y, -z,
    /// +z. Cohen-Sutherland outcodes, a trivial reject on the shared bits, then one slab crossing per outcode
    /// bit A carries, in x, y, z order -- the first crossing that lands inside the other two spans wins.</summary>
    internal static int BoxEnterFace(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
    {
        int ca = OutCode(a, min, max);
        if (ca == 0) return -1;
        if ((ca & OutCode(b, min, max)) != 0) return 0;
        if ((ca & OutXLo) != 0 && Crosses(a.X, b.X, min.X, a, b, min, max, 0)) return 1;
        if ((ca & OutXHi) != 0 && Crosses(a.X, b.X, max.X, a, b, min, max, 0)) return 2;
        if ((ca & OutYLo) != 0 && Crosses(a.Y, b.Y, min.Y, a, b, min, max, 1)) return 3;
        if ((ca & OutYHi) != 0 && Crosses(a.Y, b.Y, max.Y, a, b, min, max, 1)) return 4;
        if ((ca & OutZLo) != 0 && Crosses(a.Z, b.Z, min.Z, a, b, min, max, 2)) return 5;
        if ((ca & OutZHi) != 0 && Crosses(a.Z, b.Z, max.Z, a, b, min, max, 2)) return 6;
        return 0;
    }

    private static int OutCode(Vector3 p, Vector3 min, Vector3 max)
        => (p.X < min.X ? OutXLo : p.X > max.X ? OutXHi : 0)
         | (p.Y < min.Y ? OutYLo : p.Y > max.Y ? OutYHi : 0)
         | (p.Z < min.Z ? OutZLo : p.Z > max.Z ? OutZHi : 0);

    /// <summary>Does the segment cross the <paramref name="axis"/> plane at <paramref name="plane"/> inside
    /// the box's other two spans?</summary>
    private static bool Crosses(float pa, float pb, float plane, Vector3 a, Vector3 b,
                                Vector3 min, Vector3 max, int axis)
    {
        float t = (plane - pa) / (pb - pa);
        Vector3 hit = a + (b - a) * t;
        return (axis == 0 || (hit.X >= min.X && hit.X <= max.X))
            && (axis == 1 || (hit.Y >= min.Y && hit.Y <= max.Y))
            && (axis == 2 || (hit.Z >= min.Z && hit.Z <= max.Z));
    }

    /// <summary>Off-device (run-demo.sh): the fade integrator, the clamp and the face test. All three fail
    /// SILENTLY on the screen -- a wrong face normal picks the wrong side of the door, a wrong clamp order
    /// pins the alpha at the floor, and both read as "doors do not fade" or, worse, as doors that no longer
    /// take a click. False = do not arm the Alloy path, so the engine keeps its own FadeDoors.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DoorFade.SelfCheck");

        check.Expect(Clamp(1.5f, 0.25f) == 1f, "the ceiling is 1");
        check.Expect(Clamp(-1f, 0.25f) == 0.25f, "the floor is mindooralpha");
        check.Expect(Clamp(0.5f, 0.25f) == 0.5f, "an alpha inside the range is untouched");
        check.Expect(Clamp(0.5f, 1.5f) == 1.5f, "the floor is applied AFTER the ceiling, so a floor above 1 wins");
        // 0.3s edge to edge: a 60Hz frame moves one twentieth of the range
        check.Expect(MathF.Abs((1f / 60f) / FadeSeconds - 0.05555f) < 1e-4f, "a 60Hz frame steps 1/18 of the range");

        // A door leaf in the xz plane, thin across y, centred on its gob: the shape every NWN door has.
        var bmin = new Vector3(-1f, -0.1f, -1f);
        var bmax = new Vector3(1f, 0.1f, 1f);
        check.Expect(BoxEnterFace(new Vector3(0f, 5f, 0f), new Vector3(0f, -5f, 0f), bmin, bmax) == 4,
                     "a segment from +y enters the +y face");
        check.Expect(BoxEnterFace(new Vector3(0f, -5f, 0f), new Vector3(0f, 5f, 0f), bmin, bmax) == 3,
                     "...and from -y the -y face");
        check.Expect(BoxEnterFace(new Vector3(5f, 0f, 0f), new Vector3(-5f, 0f, 0f), bmin, bmax) == 2,
                     "+x enters the +x face");
        check.Expect(BoxEnterFace(new Vector3(-5f, 0f, 0f), new Vector3(5f, 0f, 0f), bmin, bmax) == 1,
                     "-x enters the -x face");
        check.Expect(BoxEnterFace(new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -5f), bmin, bmax) == 6,
                     "+z enters the +z face");
        check.Expect(BoxEnterFace(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 5f), bmin, bmax) == 5,
                     "-z enters the -z face");
        check.Expect(BoxEnterFace(default, new Vector3(0f, 5f, 0f), bmin, bmax) == -1,
                     "a start point inside the box is the engine's -1, which fades the door out");
        check.Expect(BoxEnterFace(new Vector3(9f, 5f, 0f), new Vector3(9f, -5f, 0f), bmin, bmax) == 0,
                     "a segment that misses the box is 0");
        check.Expect(BoxEnterFace(new Vector3(5f, 5f, 0f), new Vector3(9f, 9f, 0f), bmin, bmax) == 0,
                     "both endpoints outside the same faces is the trivial reject");

        // The target rule: opaque when camera and focus are on the SAME side of the leaf, faded when the
        // door stands between them. This is the whole feature -- get the sign wrong and doors go solid
        // exactly when they should clear.
        check.Expect(Opaque(bmin, bmax, default, Quaternion.Identity, new Vector3(0f, 5f, 0f), new Vector3(0f, 9f, 0f)),
                     "camera and focus on the same side: opaque");
        check.Expect(!Opaque(bmin, bmax, default, Quaternion.Identity, new Vector3(0f, 5f, 0f), new Vector3(0f, -9f, 0f)),
                     "the door between camera and focus: faded");
        check.Expect(Opaque(bmin, bmax, default, Quaternion.Identity, new Vector3(0f, -5f, 0f), new Vector3(0f, -9f, 0f)),
                     "...and the same from the other side");
        check.Expect(!Opaque(bmin, bmax, default, Quaternion.Identity, default, new Vector3(0f, 9f, 0f)),
                     "a camera inside the door's box fades it");
        // ...and with the leaf hinged 90 degrees about z, so the rotation and its inverse both have to be right
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        check.Expect(Opaque(bmin, bmax, default, yaw, new Vector3(5f, 0f, 0f), new Vector3(9f, 0f, 0f)),
                     "a yawed door is opaque with camera and focus on its same face");
        check.Expect(!Opaque(bmin, bmax, default, yaw, new Vector3(5f, 0f, 0f), new Vector3(-9f, 0f, 0f)),
                     "a yawed door between camera and focus fades");
        // the gob's own position has to move the box with it
        var away = new Vector3(100f, 0f, 0f);
        check.Expect(!Opaque(bmin, bmax, away, Quaternion.Identity, away + new Vector3(0f, 5f, 0f), away - new Vector3(0f, 9f, 0f)),
                     "the test is relative to the gob's world position");

        // Everything above reads the gob through the typed views; a transposed row is a frozen alpha, not an
        // error, so the two rows this tick writes and reads are exercised against a synthetic gob.
        byte* gob = stackalloc byte[0x500];
        for (int i = 0; i < 0x500; i++) gob[i] = 0;
        var gv = new Gob((IntPtr)gob);
        gv.Rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);
        // Read back as RAW floats, not through the view that wrote them: a quarter turn about X is
        // (w, x, y, z) = (h, h, 0, 0) and (x, y, z, w) = (h, 0, 0, h), so the component order shows here and
        // would cancel in a round trip through the view.
        float* raw = (float*)Unsafe.AsPointer(ref gv.Rot);
        float h = MathF.Sqrt(0.5f);
        check.Expect(MathF.Abs(raw[0] - h) < 1e-6f && MathF.Abs(raw[1] - h) < 1e-6f && raw[2] == 0f && raw[3] == 0f,
                     "Gob.Rot holds the orientation as the engine's (w, x, y, z), not System.Numerics' order");
        gv.FadeAlpha = Clamp(2f, 0.25f);
        check.Expect(gv.FadeAlpha == 1f,
                     "the integrator's output lands in Gob's fade alpha, the field the draw folds and HitCheck reads");

        log.Info(check.Ok ? "DoorFade.SelfCheck: passed"
                          : "DoorFade.SelfCheck: FAILED -- the Alloy path stands down so the engine keeps FadeDoors");
        return check.Ok;
    }
}
