using System;
using System.Numerics;

namespace CX.Services.Nui;

/// <summary>
/// Frames a doll so the whole model is in its image: the distance and height a camera needs, from the model's bounding box, the
/// camera's view angle and the image's shape. A server's own <c>dist</c> and <c>height</c> replace the fitted ones.
/// </summary>
internal static class NuiModelFrame
{
    /// <summary>How much larger than the model the view is, so it does not touch the image's edge.</summary>
    internal const float Margin = 1.1f;
    /// <summary>The smallest box side, in metres, that counts as a model with bounds.</summary>
    private const float MinExtent = 0.05f;

    /// <summary>The distance from the model's origin and the height above it at which a level camera sees the box <paramref name="min"/>..<paramref name="max"/>
    /// whole. The box is covered by its bounding sphere, so the answer holds at any turn and tilt. <paramref name="angleDeg"/> is the vertical view
    /// angle; a tall image fits the narrower, horizontal one. False = the box is empty or not a box.</summary>
    internal static bool Fit(Vector3 min, Vector3 max, float angleDeg, int width, int height, out float dist, out float camHeight)
    {
        dist = camHeight = 0f;
        Vector3 size = max - min;
        if (!(size.X >= 0f && size.Y >= 0f && size.Z >= 0f) || size.Length() < MinExtent || width <= 0 || height <= 0 || angleDeg <= 0f || angleDeg >= 180f) return false;
        Vector3 center = (min + max) * 0.5f;
        float radius = size.Length() * 0.5f + MathF.Sqrt(center.X * center.X + center.Y * center.Y);
        double halfV = angleDeg * Math.PI / 360.0;
        double halfH = Math.Atan(Math.Tan(halfV) * width / height);
        dist = (float)(radius * Margin / Math.Sin(Math.Min(halfV, halfH)));
        camHeight = center.Z;
        return true;
    }

    /// <summary>Oracle: hand-worked cases. A box 2 by 2 by 2 on the floor has half-diagonal sqrt(3) and a centre 1 up; at 30 degrees and a square
    /// image the sphere needs sqrt(3) * 1.1 / sin(15 degrees) = 7.361.</summary>
    internal static void SelfCheck(CX.Framework.ExtenderLog log)
    {
        var check = new CX.Framework.Checker(log);
        check.Expect(Fit(new Vector3(-1, -1, 0), new Vector3(1, 1, 2), 30f, 100, 100, out float d, out float h) && MathF.Abs(d - 7.361f) < 0.01f && MathF.Abs(h - 1f) < 1e-5f,
                     "a 2-metre cube at 30 degrees in a square image is framed from 7.361 m, 1 m up");
        Fit(new Vector3(-1, -1, 0), new Vector3(1, 1, 2), 30f, 50, 100, out d, out _);
        check.Expect(MathF.Abs(d - 14.35f) < 0.05f, "an image half as wide frames from the horizontal angle, tan(halfH) = tan(15 degrees) / 2, about 14.35 m");
        Fit(new Vector3(-1, -1, 0), new Vector3(1, 1, 2), 30f, 100, 50, out d, out _);
        check.Expect(MathF.Abs(d - 7.361f) < 0.01f, "a wide image keeps the vertical angle");
        Fit(new Vector3(0, 2, 0), new Vector3(0, 2, 0.1f), 60f, 10, 10, out d, out h);
        check.Expect(MathF.Abs(h - 0.05f) < 1e-5f && d > 0f, "a thin box is framed at its own height");
        check.Expect(!Fit(default, default, 30f, 10, 10, out _, out _), "an empty box is not framed");
        check.Expect(!Fit(new Vector3(1, 1, 1), new Vector3(0, 0, 0), 30f, 10, 10, out _, out _), "an upside-down box is not framed");
        check.Expect(!Fit(Vector3.Zero, Vector3.One, 0f, 10, 10, out _, out _) && !Fit(Vector3.Zero, Vector3.One, 30f, 0, 10, out _, out _), "no angle or no image is not framed");
    }
}
