using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The spell-targeting ground shape as pure geometry -- the same frame, origin shift and signed
/// distances <c>cx_target.glsl</c> evaluates per fragment, in C# so <see cref="SelfCheck"/> can put points
/// inside, outside and on the rim of every shape. Nothing calls this per frame: the shader draws, and this is
/// the specification it was written from. The part worth checking is the FRAME; a sign error compiles fine.</summary>
internal static class TargetShape
{
    /// <summary>The engine's <c>TARGET_SHAPE_*</c> enum, as <c>GLRender::m_globalShaderConstantsi[27]</c>
    /// carries it (<c>ConnectShaderData</c> @0x4ce8e0 binds it to <c>targetShape</c>).</summary>
    internal const int None = 0, Sphere = 1, Rect = 2, Cone = 3, HSphere = 4;

    /// <summary>The outline's world-space floor, the engine's own; the shader widens the outline to a
    /// fragment and a half where that is thicker.</summary>
    internal const float Edge = 0.05f;

    /// <summary>What a point is to a shape. The shader's continuous weight collapses to these three: no
    /// weight, the interior wash, the throbbing rim.</summary>
    internal enum Where { Outside, Interior, Rim }

    /// <summary>Signed distance to a box, negative inside.</summary>
    internal static float SdBox(Vector3 p, Vector3 b)
    {
        Vector3 q = Vector3.Abs(p) - b;
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
    }

    /// <summary>Signed distance to a cone of slant height <paramref name="h"/> and half-angle
    /// (<paramref name="sin"/>, <paramref name="cos"/>), apex at the origin and opening toward -y, in the
    /// (radius, axis) plane.</summary>
    internal static float SdCone(Vector2 w, float sin, float cos, float h)
    {
        Vector2 q = h * new Vector2(sin / cos, -1f);
        Vector2 a = w - q * Math.Clamp(Vector2.Dot(w, q) / Vector2.Dot(q, q), 0f, 1f);
        Vector2 b = w - q * new Vector2(Math.Clamp(w.X / q.X, 0f, 1f), 1f);
        float k = MathF.Sign(q.Y);
        float s = MathF.Max(k * (w.X * q.Y - w.Y * q.X), k * (w.Y - q.Y));
        return MathF.Sqrt(MathF.Min(Vector2.Dot(a, a), Vector2.Dot(b, b))) * MathF.Sign(s);
    }

    /// <summary>The 60-degree cone every NWN cone spell projects, as (sin, cos) of its half-angle.</summary>
    private const float ConeSin = 0.5f, ConeCos = 0.86602540f;

    /// <summary>The signed distance from <paramref name="world"/> to the shape, negative inside; +infinity
    /// for a shape that draws nothing. <paramref name="caster"/> and <paramref name="cursor"/> are the two
    /// world points the engine hands the shader (<c>playerPosition</c> and <c>targetWorldCoords</c>).</summary>
    internal static float Distance(int shape, Vector3 world, Vector3 caster, Vector3 cursor,
                                  float sizeX, float sizeY, float range, bool onSelf)
    {
        Vector3 toCaster = caster - cursor;
        float reach = toCaster.Length();
        Vector3 axis = reach > 1e-4f ? toCaster / reach : Vector3.UnitY;
        Vector2 flat = new(axis.X, axis.Y);
        Vector2 axis2 = flat.Length() > 1e-4f ? Vector2.Normalize(flat) : Vector2.UnitY;
        float over = MathF.Max(reach - range, 0f);

        Vector3 origin = onSelf ? caster : cursor;
        if (onSelf) origin -= axis * ((shape == Rect ? sizeX * 0.5f : 0f) + over);

        Vector3 d = world - origin;
        Vector3 lp = new(axis2.Y * d.X - axis2.X * d.Y, d.X * axis2.X + d.Y * axis2.Y, d.Z);

        switch (shape)
        {
            case Sphere: return lp.Length() - sizeX;
            case Rect: return SdBox(lp, new Vector3(sizeY * 0.5f, sizeX * 0.5f, MathF.Max(1f, MathF.Abs(toCaster.Z))));
            case Cone: return SdCone(new Vector2(MathF.Sqrt(lp.X * lp.X + lp.Z * lp.Z), lp.Y), ConeSin, ConeCos, sizeX);
            case HSphere:
            {
                float r = lp.Length();
                return MathF.Max(r - sizeY, sizeX - r);
            }
            default: return float.PositiveInfinity;
        }
    }

    /// <summary>...and the same distance as the three things the shader paints, with the outline at its
    /// world-space floor (the shader widens it to a fragment and a half, which has no meaning here).</summary>
    internal static Where Classify(int shape, Vector3 world, Vector3 caster, Vector3 cursor,
                                   float sizeX, float sizeY, float range, bool onSelf)
    {
        float sd = Distance(shape, world, caster, cursor, sizeX, sizeY, range, onSelf);
        return sd > 0f ? Where.Outside : sd > -Edge ? Where.Rim : Where.Interior;
    }

    /// <summary>Off-device: every shape's frame, laid out from a caster at the origin with the cursor due
    /// north of it, so "toward the cursor" is +y in world terms and the shape's own -y.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Vector3 caster = Vector3.Zero;
        Vector3 cursor = new(0f, 10f, 0f);
        const float far = 1000f;

        // A sphere sits on the CURSOR: its centre is the cursor, its rim sizeX out, nothing beyond.
        check.Expect(Classify(Sphere, cursor, caster, cursor, 5f, 0f, far, false) == Where.Interior, "sphere: the cursor is inside");
        check.Expect(Classify(Sphere, cursor + new Vector3(4.98f, 0f, 0f), caster, cursor, 5f, 0f, far, false) == Where.Rim, "sphere: 2cm short of the radius is the rim");
        check.Expect(Classify(Sphere, cursor + new Vector3(5.5f, 0f, 0f), caster, cursor, 5f, 0f, far, false) == Where.Outside, "sphere: past the radius is nothing");
        check.Expect(Classify(Sphere, caster, caster, cursor, 5f, 0f, far, false) == Where.Outside, "sphere: the caster is outside a shape centred on the cursor 10m away");

        // A cone grows from the CASTER toward the cursor, 60 degrees wide: half-way along the axis it is
        // inside, the same distance behind the caster is not, and the flanks open by tan(30).
        check.Expect(Classify(Cone, new Vector3(0f, 5f, 0f), caster, cursor, 10f, 0f, far, true) == Where.Interior, "cone: half-way to the cursor is inside");
        check.Expect(Classify(Cone, new Vector3(0f, -5f, 0f), caster, cursor, 10f, 0f, far, true) == Where.Outside, "cone: behind the caster is outside");
        check.Expect(Classify(Cone, new Vector3(2.5f, 5f, 0f), caster, cursor, 10f, 0f, far, true) == Where.Interior, "cone: inside the 30-degree flank");
        check.Expect(Classify(Cone, new Vector3(4.5f, 5f, 0f), caster, cursor, 10f, 0f, far, true) == Where.Outside, "cone: past the 30-degree flank");

        // A self-origin rectangle is pushed forward by half its length, so its NEAR end is the caster and
        // its far end is sizeX away; sizeY is the width across.
        check.Expect(Classify(Rect, new Vector3(0f, 0.2f, 0f), caster, cursor, 8f, 2f, far, true) == Where.Interior, "rect: the caster's own tile is inside");
        check.Expect(Classify(Rect, new Vector3(0f, 7.8f, 0f), caster, cursor, 8f, 2f, far, true) == Where.Interior, "rect: just short of the far end is inside");
        check.Expect(Classify(Rect, new Vector3(0f, 8.5f, 0f), caster, cursor, 8f, 2f, far, true) == Where.Outside, "rect: past the far end is outside");
        check.Expect(Classify(Rect, new Vector3(0f, -0.5f, 0f), caster, cursor, 8f, 2f, far, true) == Where.Outside, "rect: behind the caster is outside");
        check.Expect(Classify(Rect, new Vector3(1.5f, 4f, 0f), caster, cursor, 8f, 2f, far, true) == Where.Outside, "rect: sizeY is the width, so 1.5m off the axis of a 2m rect is outside");

        // Out of range: the self-origin shape is pulled forward so the CURSOR sits exactly at maximum
        // range from it -- i.e. where the caster would have to stand.
        float sd = Distance(Sphere, new Vector3(0f, 6f, 0f), caster, cursor, 1f, 0f, 4f, true);
        check.Expect(MathF.Abs(sd + 1f) < 1e-3f, "out of range: a self-origin shape's origin moves to cursor-minus-range -- 6m along, one radius deep at its centre");
        check.Expect(Classify(Sphere, new Vector3(0f, 0f, 0f), caster, cursor, 1f, 0f, 4f, true) == Where.Outside, "out of range: the caster is no longer inside it");

        // The annulus (HSPHERE): between the two radii only.
        check.Expect(Classify(HSphere, cursor + new Vector3(4f, 0f, 0f), caster, cursor, 3f, 6f, far, false) == Where.Interior, "hsphere: between the radii");
        check.Expect(Classify(HSphere, cursor, caster, cursor, 3f, 6f, far, false) == Where.Outside, "hsphere: inside the hole");
        check.Expect(Classify(HSphere, cursor + new Vector3(7f, 0f, 0f), caster, cursor, 3f, 6f, far, false) == Where.Outside, "hsphere: past the outer radius");

        // ...and a shape id the engine never sets draws nothing, which is what keeps an unknown enum from
        // painting the whole area.
        check.Expect(float.IsPositiveInfinity(Distance(None, cursor, caster, cursor, 5f, 5f, far, false)), "shape 0 has no distance");
        check.Expect(float.IsPositiveInfinity(Distance(99, cursor, caster, cursor, 5f, 5f, far, false)), "an unknown shape id has no distance");

        log.Info(check.Ok ? "TargetShape.SelfCheck: passed" : "TargetShape.SelfCheck: FAILED");
        return check.Ok;
    }
}
