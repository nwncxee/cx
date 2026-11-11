using System;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>The silhouette look the loaded module wears: the server's <c>cx_silhouette.shd</c> once it is let in, and
/// what its declaration lines say. The module edge replaces it.</summary>
internal sealed class UnseenLookState
{
    internal string? File;
    internal Unseen.Look Look = Unseen.Look.Stock;
    internal bool Asked;
}

internal static unsafe partial class Unseen
{
    /// <summary>The resref of the silhouette's look, the one library a server replaces to restyle silhouettes.</summary>
    internal const string LookRef = "cx_silhouette";

    /// <summary>What a look's declaration lines say: how solid a whole silhouette is, 0 to 1, and whether its glowing
    /// parts draw.</summary>
    internal readonly record struct Look(float Opacity, bool Glow)
    {
        /// <summary>The stock look: solid, with no glowing parts.</summary>
        internal static readonly Look Stock = new(1f, false);
    }

    private static UnseenLookState _look = new();

    /// <summary>Forget the look: the module that supplied it is gone.</summary>
    internal static void Undress() => _look = new UnseenLookState();

    /// <summary>Take <paramref name="file"/> as the module's look, or the stock one for null. False when the file is
    /// refused, which leaves the stock look.</summary>
    internal static bool Wear(string? file, ExtenderLog? log)
    {
        _look = new UnseenLookState { Asked = true };
        if (file == null) return true;
        string why = "";
        // its declaration lines first, then the file compiled on its own: a look the lit stage cannot compile costs the whole lit world
        if (Declared(file, out why) is { } look
            && VulkanToolchain.Compile(VkGlsl.Version + "layout(local_size_x = 1) in;\n" + LookText("#line 1\n" + file) + Probe, LookRef + ".shd", ShaderKind.ComputeShader, out why) != null)
        {
            _look.File = file;
            _look.Look = look;
            return true;
        }
        log?.Always($"Unseen: {LookRef}.shd is refused and silhouettes keep the stock look -- {why.Trim()}");
        return false;
    }

    /// <summary>The line that opens a look's declaration, and the stage a look is compiled in on its own: it hands the
    /// look a surface and reads back everything the lit stage reads.</summary>
    private const string Pragma = "cx_silhouette",
        Probe = "void main() { float own; vec3 c = CxSilhouetteApply(CxSilhouetteSurface(vec3(1.0), vec3(1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0), 0.5, 0.0), own); }\n";

    /// <summary>What <paramref name="file"/>'s <c>#pragma cx_silhouette</c> lines declare, or null with
    /// <paramref name="why"/> for a line that is not <c>opacity &lt;0..100&gt;</c> or <c>glow</c>.</summary>
    private static Look? Declared(string file, out string why)
    {
        Look look = Look.Stock;
        why = "";
        foreach (string line in file.Split('\n'))
        {
            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2 || t[0] != "#pragma" || t[1] != Pragma) continue;
            if (t.Length == 4 && t[2] == "opacity" && int.TryParse(t[3], out int percent) && percent is >= 0 and <= 100)
                look = look with { Opacity = percent / 100f };
            else if (t.Length == 3 && t[2] == "glow")
                look = look with { Glow = true };
            else
            {
                why = $"'{line.Trim()}' is not 'opacity <0..100>' or 'glow'";
                return null;
            }
        }
        return look;
    }

    /// <summary>The look file the lit stage splices, or null for the stock one.</summary>
    internal static string? LookFile(ExtenderLog? log)
    {
        if (!_look.Asked) Wear(ShaderResource.Override(LookRef, log), log);
        return _look.File;
    }

    /// <summary>The silhouette library as the lit stage carries it, with <paramref name="file"/> as its look.</summary>
    internal static string LookText(string? file)
        => ShaderSource.Content(ShaderSource.Expand("#include \"cx_silhouettemix.glsl\"\n", "silhouette look"), r => r == LookRef ? file : null);

    /// <summary>The silhouette library with the look now worn.</summary>
    internal static string Worn() => LookText(_look.File);
}
