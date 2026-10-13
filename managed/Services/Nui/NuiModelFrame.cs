using System;
using System.Numerics;

namespace CX.Services.Nui;

/// <summary>
/// Frames a doll so the whole model is in its image: the distance and height a camera needs, from the size of the model's bounding box,
/// the camera's view angle and the image's shape. A server's own <c>dist</c> and <c>height</c> replace the fitted ones.
/// </summary>
internal static class NuiModelFrame
{
    /// <summary>The room left round the model at <c>fit</c> 1, so it does not touch the image's edge. The server's <c>fit</c> multiplies it, so
    /// the room is already the server's to set.</summary>
    internal const float Margin = 1.1f;
    /// <summary>The smallest box side, in metres, that counts as a model with bounds.</summary>
    private const float MinExtent = 0.05f;

    /// <summary>The distance from the model's origin and the height above it at which a level camera sees a box of size <paramref name="extent"/>
    /// whole, standing on the floor (<paramref name="onFloor"/>) or centred on the origin. <paramref name="yawDeg"/> is the camera's turn as
    /// <c>Orbit</c> takes it, or with <paramref name="anyYaw"/> every turn is covered; <paramref name="pad"/> multiplies the room left round the model. The angle is the vertical one; a tall image fits the horizontal.</summary>
    internal static bool Fit(Vector3 extent, bool onFloor, float angleDeg, int width, int height, float yawDeg, bool anyYaw, float pad, out float dist, out float camHeight)
    {
        dist = camHeight = 0f;
        if (!(extent.X >= 0f && extent.Y >= 0f && extent.Z >= 0f) || extent.Length() < MinExtent || width <= 0 || height <= 0 || angleDeg <= 0f || angleDeg >= 180f) return false;
        float hx = extent.X * 0.5f, hy = extent.Y * 0.5f, hz = extent.Z * 0.5f;
        double a = (90.0 - yawDeg) * Math.PI / 180.0;
        float fx = (float)-Math.Sin(a), fy = (float)Math.Cos(a), rx = (float)Math.Cos(a), ry = (float)Math.Sin(a);
        float halfDepth = anyYaw ? MathF.Sqrt(hx * hx + hy * hy) : MathF.Abs(fx) * hx + MathF.Abs(fy) * hy;
        float halfWide = anyYaw ? halfDepth : MathF.Abs(rx) * hx + MathF.Abs(ry) * hy;
        double tanV = Math.Tan(angleDeg * Math.PI / 360.0);
        double tanH = tanV * width / height;
        dist = (float)(halfDepth + Margin * pad * Math.Max(halfWide / tanH, hz / tanV));
        camHeight = onFloor ? hz : 0f;
        return true;
    }

    /// <summary>The part of a standing model between <paramref name="lo"/> and <paramref name="hi"/> (fractions of its height, 0 the floor): its box
    /// as the camera should frame it, no wider than the slice is tall, and the height of its middle. A slice that is not 0 to 1 in order is the whole model.</summary>
    internal static Vector3 Slice(Vector3 extent, float lo, float hi, out float middle)
    {
        if (!(lo >= 0f && hi <= 1f && lo < hi)) { middle = extent.Z * 0.5f; return extent; }
        float tall = (hi - lo) * extent.Z;
        middle = (lo + hi) * 0.5f * extent.Z;
        return new Vector3(MathF.Min(extent.X, tall), MathF.Min(extent.Y, tall), tall);
    }

    /// <summary>Oracle: hand-worked cases. A 2-metre cube seen from the front has half-depth 1 and half-width 1 and half-height 1; at 30 degrees in a
    /// square image tan(15 degrees) = 0.26795, so the near face needs 1.1 / 0.26795 = 4.105 m beyond it: 5.105 m from the origin.</summary>
    internal static void SelfCheck(CX.Framework.ExtenderLog log)
    {
        var check = new CX.Framework.Checker(log);
        var cube = new Vector3(2, 2, 2);
        check.Expect(Fit(cube, true, 30f, 100, 100, 270f, false, 1f, out float d, out float h) && MathF.Abs(d - 5.105f) < 0.01f && MathF.Abs(h - 1f) < 1e-5f,
                     "a 2-metre cube on the floor, seen face on at 30 degrees in a square image, is framed from 5.105 m, 1 m up");
        Fit(cube, false, 30f, 100, 100, 270f, false, 1f, out d, out h);
        check.Expect(h == 0f, "a box centred on the origin is framed at the origin's height");
        Fit(new Vector3(4, 2, 2), true, 30f, 100, 100, 0f, false, 1f, out d, out _);
        check.Expect(MathF.Abs(d - 6.105f) < 0.01f, "seen along its length a box 4 long has half-depth 2 and half-width 1: 2 + 4.105 = 6.105 m");
        Fit(new Vector3(4, 2, 2), true, 30f, 100, 100, 270f, false, 1f, out d, out _);
        check.Expect(MathF.Abs(d - (1f + 1.1f * 2f / 0.267949f)) < 0.01f, "seen across its length the half-width is 2 and the half-depth 1: 1 + 1.1 * 2 / 0.26795 = 9.21 m");
        Fit(cube, true, 30f, 50, 100, 270f, false, 1f, out d, out _);
        check.Expect(MathF.Abs(d - (1f + 1.1f / 0.133975f)) < 0.01f, "an image half as wide frames from the horizontal angle, tan = 0.26795 / 2: 1 + 8.21 = 9.21 m");
        Fit(cube, true, 30f, 100, 50, 270f, false, 1f, out d, out _);
        check.Expect(MathF.Abs(d - 5.105f) < 0.01f, "a wide image keeps the vertical angle");
        Fit(cube, true, 30f, 100, 100, 270f, false, 2f, out d, out _);
        check.Expect(MathF.Abs(d - (1f + 2.2f / 0.267949f)) < 0.01f, "a pad of 2 doubles the room beyond the near face: 1 + 8.21 = 9.21 m");
        Fit(new Vector3(4, 2, 2), true, 30f, 100, 100, 0f, true, 1f, out d, out _);
        check.Expect(MathF.Abs(d - (MathF.Sqrt(5f) + 1.1f * MathF.Sqrt(5f) / 0.267949f)) < 0.02f, "a box that may turn is covered by its footprint's circle, radius sqrt(5)");
        Vector3 top = Slice(new Vector3(1, 1, 2), 0.75f, 1f, out float mid);
        check.Expect(MathF.Abs(top.Z - 0.5f) < 1e-5f && MathF.Abs(top.X - 0.5f) < 1e-5f && MathF.Abs(mid - 1.75f) < 1e-5f, "the top quarter of a 2-metre model is 0.5 tall and wide, its middle 1.75 up");
        check.Expect(Slice(new Vector3(1, 1, 2), 0.5f, 0.5f, out mid) == new Vector3(1, 1, 2) && mid == 1f, "an empty or reversed slice is the whole model");
        check.Expect(!Fit(default, true, 30f, 10, 10, 0f, false, 1f, out _, out _), "an empty box is not framed");
        check.Expect(!Fit(new Vector3(-1, 1, 1), true, 30f, 10, 10, 0f, false, 1f, out _, out _), "a negative size is not framed");
        check.Expect(!Fit(cube, true, 0f, 10, 10, 0f, false, 1f, out _, out _) && !Fit(cube, true, 30f, 0, 10, 0f, false, 1f, out _, out _), "no angle or no image is not framed");
    }
}
