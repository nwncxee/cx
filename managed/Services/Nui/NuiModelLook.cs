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
        string[] p = v.Split(',');
        if (p.Length != 2 || !Num(p[0], out float x) || !Num(p[1], out float y)) return false;
        x = Math.Clamp(x, lo, hi);
        y = Math.Clamp(y, lo, hi);
        if (x > y) return false;
        a = x; b = y;
        return true;
    }

    /// <summary><c>x,y,z</c>, each clamped into <paramref name="lo"/>..<paramref name="hi"/>. False when it is not three numbers.</summary>
    internal static bool Triple(string v, float lo, float hi, out Vector3 r)
    {
        r = default;
        string[] p = v.Split(',');
        if (p.Length != 3 || !Num(p[0], out float x) || !Num(p[1], out float y) || !Num(p[2], out float z)) return false;
        r = new Vector3(Math.Clamp(x, lo, hi), Math.Clamp(y, lo, hi), Math.Clamp(z, lo, hi));
        return true;
    }

    private static bool Num(string s, out float f) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);

    /// <summary>The light the four <c>light</c> settings describe. Anything but <c>server</c> in <paramref name="mode"/> is the engine's.</summary>
    internal static ModelLight Light(string mode, string color, string dir, string ambient)
    {
        if (mode.Trim() != "server") return EngineLight;
        Vector3 c = Triple(color, 0f, MaxLight, out Vector3 cv) ? cv : Vector3.One;
        Vector3 d = Triple(dir, -1000f, 1000f, out Vector3 dv) && dv.LengthSquared() > 1e-8f ? Vector3.Normalize(dv) : KeyDir;
        Vector3 a = Triple(ambient, 0f, MaxLight, out Vector3 av) ? av : new Vector3(DefaultAmbient);
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
        check.Expect(MathF.Abs(Light("server", "", "0,0,0", "").Dir.Z - 0.795f) < 1e-3f, "a zero direction falls back to the key direction");
    }
}
