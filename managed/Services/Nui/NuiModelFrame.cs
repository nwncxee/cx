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
    /// as the camera should frame it, no wider than the slice is tall and no wider than the image's own shape (<paramref name="aspect"/>, width over height) leaves room for, and the height of its middle. A slice that is not 0 to 1 in order is the whole model.</summary>
    internal static Vector3 Slice(Vector3 extent, float lo, float hi, out float middle, float aspect = 1f)
    {
        if (!(lo >= 0f && hi <= 1f && lo < hi)) { middle = extent.Z * 0.5f; return extent; }
        float tall = (hi - lo) * extent.Z;
        middle = (lo + hi) * 0.5f * extent.Z;
        float wide = tall * Math.Clamp(aspect, 0.1f, 1f);
        return new Vector3(MathF.Min(extent.X, wide), MathF.Min(extent.Y, wide), tall);
    }

    /// <summary>Axes whose extents are within this fraction of each other count as equal when an item is stood up, so a nearly cubic item keeps the pose it was authored in.</summary>
    private const float TieFraction = 0.15f;

    /// <summary>The turn that stands an item up the way a viewer expects, from the size of its box in the model's own axes: its longest
    /// side vertical (z), its thinnest toward the viewer (y), the middle one across (x), a right-handed set. Sides within
    /// <see cref="TieFraction"/> of each other are equal and keep their own axis, so a model already upright, or round, is not turned.</summary>
    internal static Quaternion Upright(Vector3 extent)
    {
        float[] e = { extent.X, extent.Y, extent.Z };
        float big = MathF.Max(e[0], MathF.Max(e[1], e[2]));
        int up = e[2] >= big * (1f - TieFraction) ? 2 : e[1] >= e[0] ? 1 : 0;
        int r1 = up == 0 ? 1 : 0, r2 = up == 2 ? 1 : 2;
        int toward = e[r1] <= e[r2] ? r1 : r2;
        if (r1 == 1 || r2 == 1)
        {
            int rest = r1 == 1 ? r2 : r1;
            if (e[1] <= e[rest] * (1f + TieFraction)) toward = 1;
        }
        int across = 3 - up - toward;
        var m = new Matrix4x4();
        m.M11 = Axis(across, 0); m.M12 = Axis(across, 1); m.M13 = Axis(across, 2);
        m.M21 = Axis(toward, 0); m.M22 = Axis(toward, 1); m.M23 = Axis(toward, 2);
        m.M31 = Axis(up, 0); m.M32 = Axis(up, 1); m.M33 = Axis(up, 2);
        m.M44 = 1f;
        float det = m.GetDeterminant();
        if (det < 0f) { m.M11 = -m.M11; m.M12 = -m.M12; m.M13 = -m.M13; }
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(m)));
    }

    private static float Axis(int which, int component) => which == component ? 1f : 0f;

    /// <summary>The axis-aligned box of the box <paramref name="min"/>..<paramref name="max"/> after it is turned by <paramref name="turn"/> about its own centre:
    /// its size, with the centre left where it was.</summary>
    internal static Vector3 TurnedExtent(Vector3 min, Vector3 max, Quaternion turn)
    {
        Vector3 c = (min + max) * 0.5f, lo = new(float.MaxValue), hi = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            Vector3 p = Vector3.Transform(corner - c, turn);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        return hi - lo;
    }

    /// <summary>The framing of a model whose geometry fills <paramref name="min"/>..<paramref name="max"/> in its own space: the point to look at and the distance. The
    /// box is sliced to <paramref name="zoneLo"/>..<paramref name="zoneHi"/> of its height for a <paramref name="creature"/> (a slice that is not 0 to 1 in order is all of
    /// it), then shown turned by <paramref name="turn"/> about its middle, which is where the camera looks.</summary>
    internal static bool Frame(Vector3 min, Vector3 max, bool creature, float zoneLo, float zoneHi, Quaternion turn, float angleDeg, int width, int height,
                               float yawDeg, bool anyYaw, float pad, out Vector3 centre, out float dist)
    {
        Vector3 extent = max - min;
        centre = (min + max) * 0.5f;
        if (creature)
        {
            extent = Slice(extent, zoneLo, zoneHi, out float middle, width / (float)Math.Max(height, 1));
            centre.Z = min.Z + middle;
        }
        Vector3 shown = TurnedExtent(-extent * 0.5f, extent * 0.5f, turn);
        return Fit(shown, false, angleDeg, width, height, yawDeg, anyYaw, pad, out dist, out _);
    }

    /// <summary>The model type <c>appearance.2da</c> gives a creature whose body is built from parts (the player races). The engine keeps no bounds for such a model
    /// that cover the creature: its root has none, and its parts are separate objects.</summary>
    private const string PartsModelType = "P";
    /// <summary>The widest and tallest a creature's row may say it is, in metres, and the width taken from a height when the row gives none.</summary>
    private const float MaxBody = 100f, WidthPerHeight = 0.4f;

    /// <summary>The box a creature built from body parts stands in, taken from its <c>appearance.2da</c> row (<paramref name="modelType"/>,
    /// <paramref name="height"/> and <paramref name="personalSpace"/> cells): its feet on the floor at the origin, as tall as <c>HEIGHT</c>, as wide as twice
    /// <c>PERSPACE</c> (0.4 of its height when the row has none). False for any other model type, and for a height that is missing, not a number or not in
    /// 0 to <see cref="MaxBody"/>, so the model's own bounds are used instead.</summary>
    internal static bool BodyBox(string? modelType, string? height, string? personalSpace, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (!string.Equals(modelType?.Trim(), PartsModelType, StringComparison.OrdinalIgnoreCase) || !Cell(height, out float h) || h <= 0f || h > MaxBody) return false;
        float w = Cell(personalSpace, out float p) && p > 0f && p <= MaxBody ? 2f * p : WidthPerHeight * h;
        min = new Vector3(-w / 2f, -w / 2f, 0f);
        max = new Vector3(w / 2f, w / 2f, h);
        return true;
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;

    private static bool Cell(string? text, out float value)
        => float.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

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
        check.Expect(BodyBox("P", "2", "0.3", out Vector3 bmin, out Vector3 bmax) && bmin == new Vector3(-0.3f, -0.3f, 0f) && bmax == new Vector3(0.3f, 0.3f, 2f),
                     "a human row (P, height 2, personal space 0.3) is a box 0.6 wide and 2 tall on the floor");
        check.Expect(BodyBox(" p ", " 1.75 ", "0.3", out _, out bmax) && bmax.Z == 1.75f, "the model type is any case, and the cells are trimmed");
        check.Expect(BodyBox("P", "1.8", "", out bmin, out bmax) && MathF.Abs(bmax.X - 0.36f) < 1e-6f && bmin.X == -bmax.X, "no personal space is 0.4 of the height wide");
        check.Expect(BodyBox("P", "1.8", "-3", out _, out bmax) && MathF.Abs(bmax.X - 0.36f) < 1e-6f, "a negative personal space is the same");
        check.Expect(!BodyBox("S", "1", "0.6", out _, out _) && !BodyBox("", "2", "0.3", out _, out _) && !BodyBox(null, "2", "0.3", out _, out _),
                     "a hell hound row (S) and a row with no model type keep the model's own bounds");
        check.Expect(!BodyBox("P", null, "0.3", out _, out _) && !BodyBox("P", "tall", "0.3", out _, out _) && !BodyBox("P", "0", "0.3", out _, out _)
                     && !BodyBox("P", "-2", "0.3", out _, out _) && !BodyBox("P", "NaN", "0.3", out _, out _) && !BodyBox("P", "Infinity", "0.3", out _, out _)
                     && !BodyBox("P", "100.5", "0.3", out _, out _), "a height that is missing, text, zero, negative, not finite or over 100 is refused");
        Slice(bmax, 0.75f, 1f, out float headMid);
        check.Expect(MathF.Abs(headMid - 1.575f) < 1e-4f, "the head slice of a 1.8 metre body is centred 1.575 up");
        Quaternion sword = Upright(new Vector3(0.054f, 1.131f, 0.262f));
        Vector3 tip = Vector3.Transform(Vector3.UnitY, sword), guard = Vector3.Transform(Vector3.UnitZ, sword), edge = Vector3.Transform(Vector3.UnitX, sword);
        check.Expect(Near(tip, Vector3.UnitZ) && Near(guard, Vector3.UnitX) && Near(edge, Vector3.UnitY),
                     "a sword authored along y (a guard 0.26 wide in z, a blade 0.05 thin in x) stands up: its tip to z, its guard across x, its flat face to the viewer");
        check.Expect(Upright(new Vector3(0.3f, 0.2f, 1.8f)) == Quaternion.Identity && Upright(new Vector3(1f, 1f, 1f)) == Quaternion.Identity && Upright(new Vector3(1f, 1.1f, 1.05f)) == Quaternion.Identity,
                     "an upright model, a cube and a nearly cubic one keep their pose");
        Quaternion lying = Upright(new Vector3(1f, 0.1f, 0.3f));
        check.Expect(Near(Vector3.Transform(Vector3.UnitX, lying), Vector3.UnitZ) && Near(Vector3.Transform(Vector3.UnitY, lying), Vector3.UnitY),
                     "a model long in x stands up on x and keeps its thin side toward the viewer");
        Vector3 turned = TurnedExtent(new Vector3(-1, -2, -3), new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
        check.Expect(Near(turned, new Vector3(2, 6, 4)), "a box 2 by 4 by 6 turned a quarter about x is 2 by 6 by 4");
        Vector3 offMin = new(2, 3, -1), offMax = new(4, 7, 5);
        Quaternion stand = Upright(offMax - offMin);
        check.Expect(Frame(offMin, offMax, false, 0f, 0f, stand, 30f, 100, 100, 270f, false, 1f, out Vector3 fc, out float fd) && Near(fc, new Vector3(3, 5, 2))
                     && MathF.Abs(fd - (1f + 1.1f * 3f / 0.267949f)) < 0.01f,
                     "a box 2 by 4 by 6 centred at (3, 5, 2), stood up so it is 4 wide, 2 deep and 6 tall, is looked at in its middle from 1 + 1.1 * 3 / tan 15 metres");
        Quaternion lookTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        Vector3 eye = NuiModelDoll.Eye(new Vector3(3, 5, 2), lookTurn, Quaternion.Identity, Vector3.Zero, 10f, 270f, out _);
        check.Expect(Near(Vector3.Transform(eye - new Vector3(3, 5, 2), lookTurn), new Vector3(0, 10, 0)),
                     "the camera is 10 metres from the middle, and a model turned a quarter about x is seen from its front: its x turn applied to the eye's offset gives the front side (0, 10, 0)");
        check.Expect(!Frame(offMin, offMin, false, 0f, 0f, Quaternion.Identity, 30f, 100, 100, 0f, false, 1f, out _, out _), "a flat box with no size is not framed");
        Vector3 portrait = Slice(new Vector3(0.6f, 0.6f, 2f), 0.75f, 1f, out _, 0.75f);
        check.Expect(Near(portrait, new Vector3(0.375f, 0.375f, 0.5f)), "a head slice 0.5 tall in a 120 by 160 image is 0.375 wide, so the image is filled and not letterboxed");
        Vector3 wideSlice = Slice(new Vector3(0.6f, 0.6f, 2f), 0.75f, 1f, out _, 1.517f);
        check.Expect(Near(wideSlice, new Vector3(0.5f, 0.5f, 0.5f)), "in a wide image the slice is as wide as it is tall");
        check.Expect(BodyBox("P", "2", "0.3", out Vector3 hb0, out Vector3 hb1) && Frame(hb0, hb1, true, 0.75f, 1f, Quaternion.Identity, 30f, 120, 160, 270f, false, 1f, out Vector3 hc, out float hd)
                     && Near(hc, new Vector3(0, 0, 1.75f)) && MathF.Abs(hd - (0.1875f + 1.1f * 0.25f / 0.267949f)) < 0.01f,
                     "the head and shoulders of a human row (2 metres, 0.6 wide) in a 120 by 160 image: the camera looks at the middle of the top quarter, 1.75 up on the body's own axis, from 0.1875 + 1.1 * 0.25 / tan 15 metres");
        check.Expect(!Fit(default, true, 30f, 10, 10, 0f, false, 1f, out _, out _), "an empty box is not framed");
        check.Expect(!Fit(new Vector3(-1, 1, 1), true, 30f, 10, 10, 0f, false, 1f, out _, out _), "a negative size is not framed");
        check.Expect(!Fit(cube, true, 0f, 10, 10, 0f, false, 1f, out _, out _) && !Fit(cube, true, 30f, 0, 10, 0f, false, 1f, out _, out _), "no angle or no image is not framed");
    }
}
