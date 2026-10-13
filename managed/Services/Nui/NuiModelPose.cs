using System;
using System.Globalization;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>How a model view animates its doll: the creature's own idle (<see cref="Idle"/>), no animation at all, or one the model has by name.</summary>
internal enum PoseMode { Idle, Still, Named }

/// <summary>
/// The <c>anim</c>, <c>animspeed</c> and <c>animloop</c> keys: which animation a doll plays. A named animation is played on the
/// model through the engine's own <c>PlayAnimation</c>, so any animation the model carries works, as does one an override adds.
/// </summary>
internal static unsafe class NuiModelPose
{
    internal const float MinSpeed = 0.05f, MaxSpeed = 8f;
    private const int MaxName = 32;
    /// <summary>The run flag that loops an animation.</summary>
    private const int LoopFlag = 4;   // not-an-offset: AnimRun flag bit 4

    /// <summary>An <c>anim</c> value: 0 is still, an animation name (a letter, then letters, digits and underscores) is that animation, and
    /// anything else, 1 and the empty value included, is the creature's own idle.</summary>
    internal static PoseMode Parse(string value, out string name)
    {
        string v = value.Trim();
        name = "";
        if (v.Length == 0) return PoseMode.Idle;
        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f == 0f ? PoseMode.Still : PoseMode.Idle;
        if (v.Length > MaxName || !char.IsAsciiLetter(v[0])) return PoseMode.Idle;
        foreach (char c in v) if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return PoseMode.Idle;
        name = v;
        return PoseMode.Named;
    }

    /// <summary>Play <paramref name="name"/> on <paramref name="model"/> at <paramref name="speed"/> times its own rate, looping or once. False = the
    /// model has no animation by that name.</summary>
    internal static bool Play(IntPtr model, string name, float speed, bool loop)
    {
        if (model == IntPtr.Zero) return false;
        Span<byte> b = stackalloc byte[MaxName + 1];
        NuiModelTarget.AsciiZ(name, b);
        fixed (byte* p = b) return new Gob(model).PlayAnimation((IntPtr)p, Math.Clamp(speed, MinSpeed, MaxSpeed), loop ? LoopFlag : 0, 0f) != 0;
    }

    /// <summary>Oracle: hand-written <c>anim</c> values and what each asks for.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Parse("", out _) == PoseMode.Idle && Parse("1", out _) == PoseMode.Idle && Parse("2.5", out _) == PoseMode.Idle, "empty and any non-zero number are the idle");
        check.Expect(Parse("0", out _) == PoseMode.Still && Parse("0.0", out _) == PoseMode.Still, "0 is still");
        check.Expect(Parse("creadyr", out string n) == PoseMode.Named && n == "creadyr", "a name is an animation");
        check.Expect(Parse("cast_1", out n) == PoseMode.Named && n == "cast_1", "digits and underscores are part of a name");
        check.Expect(Parse("1abc", out _) == PoseMode.Idle && Parse("a b", out _) == PoseMode.Idle && Parse("a-b", out _) == PoseMode.Idle, "a name starts with a letter and has no spaces or dashes");
        check.Expect(Parse(new string('a', 33), out _) == PoseMode.Idle && Parse(new string('a', 32), out _) == PoseMode.Named, "a name is at most 32 letters");
    }
}
