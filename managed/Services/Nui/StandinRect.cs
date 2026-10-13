using System;
using System.Globalization;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The part of a stand-in image a model view shows, <c>x,y,w,h</c> in the stand-in's own pixels counted from its top-left
/// corner, as an image viewer shows it. The engine's rasters are bottom-up, so <see cref="Cut"/> flips the rows.
/// </summary>
internal readonly record struct StandinRect(int X, int Y, int W, int H)
{
    /// <summary>The rectangle <paramref name="text"/> names, or none for empty or malformed text.</summary>
    internal static StandinRect Parse(string text)
    {
        string[] p = text.Split(',');
        if (p.Length != 4) return default;
        Span<int> v = stackalloc int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return default;
        return new StandinRect(v[0], v[1], v[2], v[3]);
    }

    /// <summary>Is there a rectangle, and does it lie inside a <paramref name="sw"/> x <paramref name="sh"/> image?</summary>
    internal bool Fits(int sw, int sh) => W > 0 && H > 0 && X >= 0 && Y >= 0 && W <= sw - X && H <= sh - Y;

    /// <summary>The rectangle's pixels out of a bottom-up <paramref name="src"/> of <paramref name="sw"/> x <paramref name="sh"/>, bottom-up too.</summary>
    internal uint[] Cut(uint[] src, int sw, int sh)
    {
        var o = new uint[W * H];
        for (int row = 0; row < H; row++)
        {
            int from = sh - Y - H + row;
            Array.Copy(src, from * sw + X, o, row * W, W);
        }
        return o;
    }

    /// <summary>Oracle: a hand-written 4x3 image, rows given top to bottom as a viewer shows them.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // The picture as a viewer shows it, top row first:  1 2 3 4 / 5 6 7 8 / 9 10 11 12. Stored bottom-up.
        uint[] up = { 9, 10, 11, 12, 5, 6, 7, 8, 1, 2, 3, 4 };
        uint[] got = new StandinRect(1, 0, 2, 2).Cut(up, 4, 3);
        check.Expect(got.AsSpan().SequenceEqual(new uint[] { 6, 7, 2, 3 }), "a crop of the top two rows, columns 2-3, comes out bottom-up");
        check.Expect(Parse("0, 0,256,400") == new StandinRect(0, 0, 256, 400), "four comma-separated integers parse, spaces allowed");
        check.Expect(Parse("1,2,3") == default && Parse("a,b,c,d") == default && Parse("") == default, "three fields, letters and nothing are no rectangle");
        check.Expect(new StandinRect(0, 0, 4, 3).Fits(4, 3) && !new StandinRect(1, 0, 4, 3).Fits(4, 3) && !new StandinRect(0, 0, 0, 3).Fits(4, 3) && !default(StandinRect).Fits(4, 3),
                     "a rectangle fits only inside the image and with a size");
        check.Expect(!new StandinRect(int.MaxValue, 0, 1, 1).Fits(4, 3) && !new StandinRect(0, int.MaxValue, 1, 1).Fits(4, 3) && !new StandinRect(1, 1, int.MaxValue, 1).Fits(4, 3),
                     "a corner or size near int.MaxValue does not wrap round into the image");
    }
}
