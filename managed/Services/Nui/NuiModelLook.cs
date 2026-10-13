using System;
using System.Globalization;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>The light a model view's doll is shown in. <see cref="Server"/> false is the engine's own character-preview light.
/// A server light shades as texture * (ambient + colour * max(n.dir, 0)) + emissive, with the direction in the view's own frame
/// (x right, y up, z toward the viewer).</summary>
internal readonly record struct ModelLight(bool Server, Vector3 Color, Vector3 Dir, Vector3 Ambient);

/// <summary>
/// The parse and clamp of the model view's look and behaviour settings that are more than one number: pairs, vectors and the
/// light. Every default is today's value, and a value that does not parse or does not fit its range is the default.
/// </summary>
internal static class NuiModelLook
{
    /// <summary>The camera's nearest and farthest clip distance, in metres.</summary>
    internal const float DefaultNear = 0.1f, DefaultFar = 2000f, MaxFar = 100000f;
    /// <summary>The most degrees the camera tips up or down.</summary>
    internal const float MaxPitch = 89f;
    /// <summary>The widest a drag turn may range either side of <c>yaw</c>, in degrees.</summary>
    internal const float MaxTurn = 360f;
    /// <summary>The metres one scroll notch moves the camera, and its range.</summary>
    internal const float DefaultZoomStep = 0.25f, MinZoomStep = 0.01f, MaxZoomStep = 10f;
    /// <summary>The ceiling of a light colour or ambient channel.</summary>
    internal const float MaxLight = 4f;
    /// <summary>The ambient a server light has until it names one, and the key light the engine's character preview uses.</summary>
    internal const float DefaultAmbient = 0.25f;
    internal static readonly Vector3 KeyDir = Vector3.Normalize(new Vector3(0.35f, 0.5f, 0.8f));

    internal static readonly ModelLight EngineLight = new(false, Vector3.One, KeyDir, new Vector3(DefaultAmbient));

    /// <summary><c>a,b</c> with both numbers in <paramref name="lo"/>..<paramref name="hi"/> (each clamped) and a no greater than b.
    /// False, and neither out value set, when it is anything else.</summary>
    internal static bool Pair(string v, float lo, float hi, out float a, out float b)
    {
        a = b = 0;
        Span<float> n = stackalloc float[2];
        if (!Numbers(v, n, lo, hi) || n[0] > n[1]) return false;
        a = n[0]; b = n[1];
        return true;
    }

    /// <summary><c>x,y,z</c>, each clamped into <paramref name="lo"/>..<paramref name="hi"/>. False when it is not three numbers.</summary>
    internal static bool Triple(string v, float lo, float hi, out Vector3 r)
    {
        r = default;
        Span<float> n = stackalloc float[3];
        if (!Numbers(v, n, lo, hi)) return false;
        r = new Vector3(n[0], n[1], n[2]);
        return true;
    }

    /// <summary>Exactly <paramref name="into"/>.Length comma-separated finite numbers, each clamped into <paramref name="lo"/>..<paramref name="hi"/>.</summary>
    private static bool Numbers(string v, Span<float> into, float lo, float hi)
    {
        string[] p = v.Split(',');
        if (p.Length != into.Length) return false;
        for (int i = 0; i < p.Length; i++)
        {
            if (!Num(p[i], out float f)) return false;
            into[i] = Math.Clamp(f, lo, hi);
        }
        return true;
    }

    private static bool Num(string s, out float f) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);

    /// <summary>The widest a <c>rot</c> turn may be about each axis, in degrees.</summary>
    internal const float MaxRot = 360f;

    /// <summary>The turn <c>rot</c> names, <c>x,y,z</c> degrees, each clamped to -360..360: about the model's x axis, then its y, then its z. Anything that
    /// is not three numbers, and the empty value, is no turn.</summary>
    internal static Quaternion Rotation(string v)
    {
        if (!Triple(v, -MaxRot, MaxRot, out Vector3 d)) return Quaternion.Identity;
        const float Rad = MathF.PI / 180f;
        return Quaternion.Concatenate(Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, d.X * Rad),
                                                             Quaternion.CreateFromAxisAngle(Vector3.UnitY, d.Y * Rad)),
                                      Quaternion.CreateFromAxisAngle(Vector3.UnitZ, d.Z * Rad));
    }

    /// <summary>Where a scroll of <paramref name="notches"/> (up is positive, nearer) moves a camera standing at <paramref name="from"/>,
    /// <paramref name="step"/> metres a notch, kept inside <paramref name="min"/>..<paramref name="max"/>.</summary>
    internal static float ZoomTo(float from, int notches, float step, float min, float max) => Math.Clamp(from - notches * step, min, max);

    /// <summary>The player's zoom after the policy changed: back to the server's <c>dist</c> (NaN) when <c>zoom</c> is gone or the player had
    /// not scrolled, else pulled inside the new range.</summary>
    internal static float ZoomAfterPolicy(float at, bool hasZoom, float min, float max)
        => !hasZoom || float.IsNaN(at) ? float.NaN : Math.Clamp(at, min, max);

    /// <summary>The player's turn after a drag of <paramref name="dx"/> pixels at <paramref name="perPixel"/> degrees a pixel: inside
    /// <paramref name="min"/>..<paramref name="max"/> when the server gave a range, else free and wrapped to one turn.</summary>
    internal static float TurnBy(float yaw, int dx, float perPixel, bool ranged, float min, float max)
    {
        float turn = yaw + dx * perPixel;
        return ranged ? Math.Clamp(turn, min, max) : turn % 360f;
    }

    /// <summary>The player's turn after the policy changed: pulled inside a range that is set, else as it was.</summary>
    internal static float TurnAfterPolicy(float yaw, bool ranged, float min, float max) => ranged ? Math.Clamp(yaw, min, max) : yaw;

    /// <summary>A light colour: <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the alpha is ignored), or <c>r,g,b</c> each clamped to 0..<see cref="MaxLight"/>. False for anything else.</summary>
    internal static bool Colour(string v, out Vector3 r)
    {
        r = default;
        if (v.Length > 0 && v[0] == '#')
        {
            if (!HexColour.TryParse(v, out uint rgba)) return false;
            Span<float> ch = stackalloc float[4];
            HexColour.Channels(rgba, ch);
            r = new Vector3(ch[0], ch[1], ch[2]);
            return true;
        }
        return Triple(v, 0f, MaxLight, out r);
    }

    /// <summary>The backdrop <c>bg</c> names into <paramref name="into"/> (four floats, <c>r,g,b,a</c>): <c>#RRGGBB</c> (opaque) or <c>#RRGGBBAA</c>, or up to four numbers
    /// in 0-1, where a missing or unreadable one is 0. A malformed hex value is all zero, like an empty one.</summary>
    internal static void Background(string v, Span<float> into)
    {
        into.Clear();
        if (v.Length > 0 && v[0] == '#')
        {
            if (HexColour.TryParse(v, out uint rgba)) HexColour.Channels(rgba, into);
            return;
        }
        string[] p = v.Split(',');
        for (int i = 0; i < 4 && i < p.Length; i++)
            if (float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) into[i] = f;
    }

    /// <summary>The light the four <c>light</c> settings describe. Anything but <c>server</c> in <paramref name="mode"/> is the engine's.</summary>
    internal static ModelLight Light(string mode, string color, string dir, string ambient)
    {
        if (mode.Trim() != "server") return EngineLight;
        Vector3 c = Colour(color, out Vector3 cv) ? cv : Vector3.One;
        Vector3 d = Triple(dir, -1000f, 1000f, out Vector3 dv) && dv.LengthSquared() > 1e-8f ? Vector3.Normalize(dv) : KeyDir;
        Vector3 a = Colour(ambient, out Vector3 av) ? av : new Vector3(DefaultAmbient);
        return new ModelLight(true, c, d, a);
    }

    /// <summary>Oracle: hand-written values and the ranges the contract gives.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Pair("1.5,4", 0.01f, 100f, out float a, out float b) && a == 1.5f && b == 4f, "a pair in range parses");
        check.Expect(Pair("-5,500", 0.01f, 100f, out a, out b) && a == 0.01f && b == 100f, "a pair is clamped end by end");
        check.Expect(!Pair("4,1.5", 0.01f, 100f, out _, out _) && !Pair("4", 0.01f, 100f, out _, out _) && !Pair("a,b", 0.01f, 100f, out _, out _)
                     && !Pair("1,2,3", 0.01f, 100f, out _, out _), "a pair with min above max, one number, text or three is refused");
        check.Expect(Pair("-45, 45", -360f, 360f, out a, out b) && a == -45f && b == 45f, "a pair keeps negatives and trims spaces");
        check.Expect(!Pair("NaN,1", 0f, 1f, out _, out _) && !Pair("0,Infinity", 0f, 1f, out _, out _), "a pair refuses a non-finite number");
        check.Expect(Triple("1,0.5,9", 0f, 4f, out Vector3 t) && t == new Vector3(1f, 0.5f, 4f), "a triple clamps each channel");
        check.Expect(!Triple("1,2", 0f, 4f, out _), "two numbers are not a triple");
        check.Expect(Light("", "2,2,2", "", "") == EngineLight && Light("engine", "2,2,2", "1,0,0", "1,1,1") == EngineLight,
                     "the engine light ignores the server's colour, direction and ambient");
        ModelLight s = Light("server", "", "", "");
        check.Expect(s.Server && s.Color == Vector3.One && s.Ambient == new Vector3(0.25f) && MathF.Abs(s.Dir.Length() - 1f) < 1e-5f
                     && MathF.Abs(s.Dir.Z - 0.795f) < 1e-3f, "a bare server light is white, 0.25 ambient and the key direction");
        ModelLight m = Light("server", "1,0.5,0", "0,0,2", "0.1,0.1,0.2");
        check.Expect(m.Color == new Vector3(1f, 0.5f, 0f) && m.Dir == new Vector3(0, 0, 1f) && m.Ambient == new Vector3(0.1f, 0.1f, 0.2f),
                     "a server light normalises its direction and keeps its colours");
        check.Expect(Colour("#FF8000", out Vector3 hx) && hx.X == 1f && Math.Abs(hx.Y - 0.5019608f) < 1e-6f && hx.Z == 0f, "a hex light colour reads 0-1 per channel");
        check.Expect(Colour("#FF800040", out hx) && hx.X == 1f && hx.Z == 0f, "the alpha of a hex light colour is ignored");
        check.Expect(Colour("2,0.5,9", out hx) && hx == new Vector3(2f, 0.5f, 4f), "the number form still clamps to 4");
        check.Expect(!Colour("#12345", out _) && !Colour("#GGGGGG", out _) && !Colour("", out _) && !Colour("#1234567890", out _), "a malformed light colour is refused");
        ModelLight hl = Light("server", "#FF0000", "", "#000033");
        check.Expect(hl.Color == new Vector3(1f, 0f, 0f) && hl.Ambient.X == 0f && Math.Abs(hl.Ambient.Z - 0.2f) < 1e-6f, "Light takes hex for lightcolor and lightambient");
        ModelLight bl = Light("server", "#GGGGGG", "", "#12345");
        check.Expect(bl.Color == Vector3.One && bl.Ambient == new Vector3(0.25f), "a malformed hex falls back to the default, as a bad number does");
        Span<float> bg = stackalloc float[4];
        Background("#FF000080", bg);
        check.Expect(bg[0] == 1f && bg[1] == 0f && bg[2] == 0f && Math.Abs(bg[3] - 0.5019608f) < 1e-6f, "bg takes #RRGGBBAA");
        Background("#000000", bg);
        check.Expect(bg[0] == 0f && bg[3] == 1f, "bg #RRGGBB is opaque");
        Background("0.1,0.2,0.3,0.4", bg);
        check.Expect(bg[0] == 0.1f && bg[1] == 0.2f && bg[2] == 0.3f && bg[3] == 0.4f, "bg numbers still read");
        Background("0.5,x", bg);
        check.Expect(bg[0] == 0.5f && bg[1] == 0f && bg[3] == 0f, "bg numbers: a missing or bad one is 0");
        Background("#12345", bg);
        check.Expect(bg[0] == 0f && bg[1] == 0f && bg[2] == 0f && bg[3] == 0f, "a malformed hex bg is clear");
        Background("", bg);
        check.Expect(bg[3] == 0f, "an empty bg is clear");
        check.Expect(ZoomTo(4f, 2, 0.25f, 1f, 10f) == 3.5f && ZoomTo(4f, -100, 0.25f, 1f, 10f) == 10f && ZoomTo(1.2f, 5, 0.25f, 1f, 10f) == 1f,
                     "a scroll up comes nearer a step a notch and stops at the range's ends");
        check.Expect(ZoomAfterPolicy(8f, true, 1f, 5f) == 5f && float.IsNaN(ZoomAfterPolicy(float.NaN, true, 1f, 5f))
                     && float.IsNaN(ZoomAfterPolicy(3f, false, 1f, 5f)), "a zoom that changes pulls the scrolled distance inside, and none left is the server's");
        check.Expect(TurnBy(40f, 10, 1f, true, -45f, 45f) == 45f && TurnBy(-40f, -10, 1f, true, -45f, 45f) == -45f && TurnBy(0f, 3, 0.5f, true, -45f, 45f) == 1.5f,
                     "a ranged drag turns degrees a pixel and stops at the range's ends");
        check.Expect(TurnBy(350f, 20, 1f, false, 0f, 0f) == 10f && TurnBy(0f, 720, 1f, false, 0f, 0f) == 0f && TurnBy(10f, -3, 2f, false, 0f, 0f) == 4f,
                     "a free drag wraps at a full turn");
        check.Expect(TurnAfterPolicy(90f, true, -45f, 45f) == 45f && TurnAfterPolicy(90f, false, 0f, 0f) == 90f, "a dragrange that changes pulls the turn inside it");
        check.Expect(MathF.Abs(Light("server", "", "0,0,0", "").Dir.Z - 0.795f) < 1e-3f, "a zero direction falls back to the key direction");
        Vector3 up = Vector3.Transform(Vector3.UnitY, Rotation("90,0,0"));
        check.Expect(MathF.Abs(up.X) < 1e-5f && MathF.Abs(up.Y) < 1e-5f && MathF.Abs(up.Z - 1f) < 1e-5f, "a quarter turn about x stands the model's y axis up");
        Vector3 side = Vector3.Transform(Vector3.UnitX, Rotation("0,0,90"));
        check.Expect(MathF.Abs(side.X) < 1e-5f && MathF.Abs(side.Y - 1f) < 1e-5f, "a quarter turn about z carries x to y");
        Vector3 order = Vector3.Transform(Vector3.UnitY, Rotation("90,90,0"));
        check.Expect(MathF.Abs(order.X - 1f) < 1e-5f && MathF.Abs(order.Y) < 1e-5f && MathF.Abs(order.Z) < 1e-5f, "x turns first, then y: y goes up, then up goes to x");
        Vector3 clamped = Vector3.Transform(Vector3.UnitY, Rotation("450,0,0"));
        check.Expect(MathF.Abs(clamped.Y - 1f) < 1e-5f && MathF.Abs(clamped.Z) < 1e-5f, "a turn past 360 is clamped to a full turn, which is none");
        check.Expect(Rotation("") == Quaternion.Identity && Rotation("1,2") == Quaternion.Identity && Rotation("a,b,c") == Quaternion.Identity && Rotation("NaN,0,0") == Quaternion.Identity,
                     "an empty value, two numbers, text or a non-finite number is no turn");
    }
}
