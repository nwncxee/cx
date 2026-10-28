using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static unsafe partial class GuiContent
{
    /// <summary>One content pair on Vulkan: the translated program beside the engine-term locations resolved against
    /// its loose block, so <see cref="Feed"/> fills that block with the writes it makes on GL.</summary>
    internal sealed record VkPair(GuiVkProg Program, Prog Terms);

    /// <summary>The samplers a content stage may declare without a binding, at the units the engine's program sets:
    /// <c>GLRender</c> binds a GUI part's texture on unit 0 and a GUI content stage reads no other, so a pair that
    /// declares one is refused rather than drawn against a texture nobody bound.</summary>
    private static readonly Dictionary<string, uint> VkUnits = new(StringComparer.Ordinal) { ["texUnit0"] = 0 };

    /// <summary>The engine's attribute declarations, which carry no location of their own: Vulkan has no attribute
    /// binding by name, so each is given the location the mesh arena's layout feeds that stream at.</summary>
    private static readonly Regex AttribDecl =
        new(@"^[ \t]*attribute[ \t]+(?<t>\w+)[ \t]+(?<n>\w+)[ \t]*;", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Key <paramref name="key"/>'s program for Vulkan, or null when the pair has no program of its own or
    /// a step refused it (each logged where it happens). The caller then draws these parts through Alloy's own GUI
    /// program, exactly as GL does for a key whose build returned null.</summary>
    internal static VkPair? VkBuild(VkBackend vk, int key, ExtenderLog log)
    {
        if (!Texts(key, log, out string v, out string f)) return null;
        Prog? terms = null;
        GuiVkProg? p = GuiVkProg.Build(vk, Vulkan(Located(v)), Vulkan(f), VkUnits, What(key),
                                       q => terms = Locs(), log);
        if (p == null || terms == null)
        {
            log.Always($"{What(key)}: NOT RUN on Vulkan (logged above) -- these GUI parts draw through Alloy's own GUI program");
            return null;
        }
        log.Info($"{What(key)}: built for Vulkan -- {p.Members.Length} engine terms in its loose block");
        return new VkPair(p, terms);
    }

    /// <summary>The engine's own version line (<c>#version 330 core</c>, from <see cref="Prologue"/>) raised to the
    /// one the loose block's <c>binding</c> qualifier needs. The prologue's own defines carry 330's spellings --
    /// <c>attribute</c>, <c>varying</c>, <c>texture2D</c>, <c>gl_FragColor</c> -- so the stage's text is unchanged.</summary>
    internal static string Vulkan(string stage) => Version.Replace(stage, "#version 460 core\n", 1);

    private static readonly Regex Version = new(@"^[ \t]*#version[ \t]+\d+(?:[ \t]+\w+)?[ \t]*\r?\n", RegexOptions.CultureInvariant);

    /// <summary>A vertex stage's <c>attribute</c> declarations as located Vulkan inputs, by the same table GL's build
    /// binds by name (<see cref="Attribs"/>). A name the table does not carry is left alone, so it reaches the
    /// translator as an input with no location and the program is refused there rather than drawn against the wrong
    /// stream.</summary>
    internal static string Located(string vs)
        => AttribDecl.Replace(vs, m =>
        {
            foreach ((string name, uint loc) in Attribs)
                if (name == m.Groups["n"].Value)
                    return $"layout(location = {loc}) in {m.Groups["t"].Value} {name};";
            return m.Value;
        });
}
