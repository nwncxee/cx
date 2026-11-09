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
    internal sealed record VkPair(GuiVkProg Program, Prog Terms)
    {
        /// <summary>This pair runs inside the GUI's own program: the terms that program reads. Null for a pair compiled
        /// the way the engine compiles it.</summary>
        public TwinTerms? Twin;
    }

    /// <summary>The loose terms <c>cx_gui_v</c> and <c>cx_guicon_f</c> read: the GUI program's two, and the draw's row, slot
    /// mask and view size. <see cref="Feat"/> and <see cref="Epoch"/> are the pair and the lane layout it was translated against.</summary>
    internal sealed record TwinTerms(int BoneStart, int Gamma, int Row, int Slots, int Screen)
    {
        public int Feat, Epoch;
    }

    /// <summary>How a pair runs on a GUI part. <see cref="Route.Engine"/>: as the engine compiles it, fed the terms
    /// <see cref="Feed"/> writes. <see cref="Route.Twin"/>: inside the GUI's program, because it samples a material unit
    /// past the diffuse, which needs the lanes and maps <see cref="Feed"/> does not carry, and the GUI's vertex stage IS
    /// its stock vertex half. <see cref="Route.None"/>: such a pair with a vertex half of its own, which has no host.</summary>
    internal enum Route : byte { Engine, Twin, None }

    /// <summary>Does this stage text, with its includes resolved, declare a sampler <see cref="VkUnits"/> does not name?</summary>
    private static bool SamplesPastUnit0(string stage)
    {
        foreach ((_, string name, uint? binding, _) in VkGlsl.Opaques(stage))
            if (binding == null && !VkUnits.ContainsKey(name)) return true;
        return false;
    }

    /// <summary>The route for a pair of these two engine-compiled texts.</summary>
    internal static Route RouteOf(string vs, string fs, bool stockVertexHalf)
        => !SamplesPastUnit0(vs) && !SamplesPastUnit0(fs) ? Route.Engine : stockVertexHalf ? Route.Twin : Route.None;

    /// <summary>The line in <c>cx_guicon_f.glsl</c> the translated half replaces.</summary>
    private const string TwinMark = "//$GUI-CONTENT-REGION";

    /// <summary>A twin's fragment stage as it compiles, or null with <paramref name="why"/> when the half is refused.</summary>
    internal static string? TwinFs(string vs, string fs, ContentStage.Param[] lanes, Func<string, string?> chain, ExtenderLog log, out string why)
    {
        string head = ShaderSource.Get("cx_guicon_f.glsl");
        int at = head.IndexOf(TwinMark, StringComparison.Ordinal);
        if (at < 0) { why = "cx_guicon_f.glsl has lost its region"; return null; }
        ContentStage.Result r = ContentStage.GuiHalf(vs, fs, lanes, chain, head[..at], log);
        if (r.Refused != null) { why = r.Refused; return null; }
        why = "";
        string what = $"GuiContent twin {vs}/{fs}";
        string text = "#version 460 core\n" + head[..at] + r.Host + head[(at + TwinMark.Length)..];
        return ShaderResource.Tokens(VanillaShader.Splice(ShaderTexts.Lib(text, log, what), log, what, true, true));
    }

    /// <summary>The twin of pair <paramref name="vs"/>/<paramref name="fs"/> on <paramref name="lanes"/>; null (logged) when refused.</summary>
    internal static VkPair? VkTwin(VkBackend vk, string vs, string fs, ContentStage.Param[] lanes, Func<string, string?> chain, ExtenderLog log)
    {
        string what = $"GuiContent twin {vs}/{fs}";
        if (TwinFs(vs, fs, lanes, chain, log, out string why) is not { } text)
        {
            log.Always($"{what}: NOT RUN -- {why}. These GUI parts draw through Alloy's own GUI program");
            return null;
        }
        TwinTerms? terms = null;
        GuiVkProg? p = GuiVkProg.Build(vk, GuiPassVk.Text("cx_gui_v.glsl", log), text, new Dictionary<string, uint>(StringComparer.Ordinal), what,
                                       q => terms = new TwinTerms(q.Loc("uBoneStart"), q.Loc("uGamma"), q.Loc("uGuiRow"), q.Loc("uGuiSlots"), q.Loc("uGuiScreen")), log);
        if (p == null || terms == null)
        {
            log.Always($"{what}: NOT RUN on Vulkan (logged above) -- these GUI parts draw through Alloy's own GUI program");
            return null;
        }
        log.Always($"{what}: built -- its fragment half runs under the GUI's vertex stage and light, over {lanes.Length} lane(s)");
        return new VkPair(p, new Prog()) { Twin = terms };
    }

    /// <summary>The samplers a pair on the engine's route may declare: a pair that declares another takes
    /// <see cref="Route.Twin"/> or <see cref="Route.None"/>, so this route has nothing else to bind.</summary>
    private static readonly Dictionary<string, uint> VkUnits = new(StringComparer.Ordinal) { ["texUnit0"] = 0 };

    /// <summary>The engine's attribute declarations, which carry no location of their own: Vulkan has no attribute
    /// binding by name, so each is given the location the mesh arena's layout feeds that stream at.</summary>
    private static readonly Regex AttribDecl =
        new(@"^[ \t]*attribute[ \t]+(?<t>\w+)[ \t]+(?<n>\w+)[ \t]*;", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Key <paramref name="key"/>'s program for Vulkan, or null when the pair has no program of its own or
    /// a step refused it (each logged where it happens). The caller then draws these parts through Alloy's own GUI
    /// program, exactly as GL does for a key whose build returned null.</summary>
    internal static VkPair? VkBuild(VkBackend vk, int key, int feat, ExtenderLog log)
    {
        if (!Texts(key, log, out string v, out string f)) return null;
        (string vsName, string fsName) = Pair(key);
        string? vsChain = VanillaShader.ChainText(vsName);
        switch (RouteOf(v, f, vsChain != null && ContentStage.StockShaped(vsChain, false)))
        {
            case Route.None:
                log.Always($"{What(key)}: samples past texUnit0 with a vertex half of its own -- these GUI parts draw through Alloy's own GUI program");
                return null;
            case Route.Twin:
                if (!ContentPrograms.ReadFacts(feat))
                {
                    log.Always($"{What(key)}: its lanes are not known (feature {feat}; logged above) -- these GUI parts draw through Alloy's own GUI program");
                    return null;
                }
                VkPair? twin = VkTwin(vk, vsName, fsName, ContentPrograms.Params(feat), VanillaShader.ChainText, log);
                if (twin?.Twin is { } t) { t.Feat = feat; t.Epoch = ContentPrograms.Epoch(feat); }
                return twin;
        }
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
