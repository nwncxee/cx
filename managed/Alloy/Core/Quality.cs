using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The graphics quality presets (ADR 0030): one preset, a dropdown per area laddered on it, and every real knob
/// of an area laddered on that area (<see cref="Knob{T}.Ladder"/>). A ladder member's default IS its rung, so
/// <c>cx.cfg</c> keeps only what the player changed from the preset, and a later release's retune reaches every
/// knob nobody touched. High is the shipped defaults.
/// </summary>
internal static class Quality
{
    internal static readonly KnobGroup G;
    /// <summary>The stored values of every area dropdown, index-aligned with the rung arrays area members pass.</summary>
    internal static readonly string[] Levels = { "low", "medium", "high", "ultra" };
    private static readonly string[] LevelNames = { "Low", "Medium", "High", "Ultra" };
    /// <summary>The preset's own values: Classic below the four area levels, which every area takes as Low.
    /// A knob that ladders on <see cref="Preset"/> passes five rungs (<see cref="Classic{T}"/>).</summary>
    internal static readonly string[] PresetLevels = { "classic", "low", "medium", "high", "ultra" };
    private static readonly string[] PresetNames = { "Classic", "Low", "Medium", "High", "Ultra" };
    /// <summary>The preset index of High, the shipped defaults: what a check of a knob's shipped value reads.</summary>
    internal static readonly int Shipped = System.Array.IndexOf(PresetLevels, "high");
    internal static readonly Knob<string> Preset, Shadows, Lighting, Reflections, Ao, Detail, Grass, Weather, Effects;

    static Quality()
    {
        G = KnobGroup.Declare("quality", "Graphics", "Quality", order: 300, title: "Preset",
            header: "Pick a preset, then adjust any area. A changed setting elsewhere in Graphics keeps a blue dot "
                  + "until you pick a preset again.");
        G.PanelStatus = Status;
        Preset = G.Dropdown("preset", "Graphics quality", PresetLevels, "high", PresetNames,
            help: "Sets every area below at once. Classic is closest to the stock game's look at Low's cost; Low is for "
                + "integrated graphics; Ultra raises shadow and reflection detail past the defaults. Some changes take "
                + "effect at the next launch.");
        G.Section("Areas");
        Shadows = Area("shadows", "Shadows", "Shadow resolution, distance, softness and how many lights cast them.");
        Lighting = Area("lighting", "Lighting", "How many lights reach each pixel and how far their light carries.");
        Reflections = Area("reflections", "Reflections", "Reflections of the scene on glossy and wet surfaces.");
        Ao = Area("ao", "Ambient occlusion", "Contact darkening in corners and under objects (the game's own "
                                            + "Ambient Occlusion option still turns it on or off).");
        Detail = Area("detail", "Model detail", "How far models keep full detail, and how small a model may get before it is skipped.");
        Grass = Area("grass", "Grass", "How dense grass is and how far it draws.");
        Weather = Area("weather", "Weather", "Rain and snow density, splashes, and the wet and snowed ground.");
        Effects = Area("effects", "Effects", "Particles, light halos and other glows.");
    }

    private static Knob<string> Area(string key, string label, string help)
        => G.Dropdown(key, label, Levels, "high", LevelNames, help: help).Ladder(Preset, "low", "low", "medium", "high", "ultra");

    /// <summary>Preset rungs for a knob Classic sets: <paramref name="classic"/> under Classic, <paramref name="rest"/>
    /// under every other preset.</summary>
    internal static T[] Classic<T>(T classic, T rest) => new[] { classic, rest, rest, rest, rest };

    /// <summary>"High · 3 settings changed": the members (recursively) that left their rung.</summary>
    private static string Status()
    {
        int n = Changed(Preset);
        string level = PresetNames[System.Math.Max(0, Preset.ChoiceIndex)];
        return n == 0 ? $"{level}, as set." : $"{level}, {n} setting{(n == 1 ? "" : "s")} changed from it.";
    }

    private static int Changed(Knob driver)
    {
        if (driver.Members == null) return 0;
        int n = 0;
        foreach (Knob m in driver.Members) n += (m.StoredIsDefault ? 0 : 1) + Changed(m);
        return n;
    }

    /// <summary>Classic's rungs against the preset table its work item specifies -- hand-written literals, the oracle.
    /// Reads rungs without setting the preset, so the player's <c>cx.cfg</c> is never touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        const int classic = 0, low = 1;
        check.Expect(Preset.ChoiceCount == 5 && System.Array.IndexOf(Preset.Values, "classic") == classic, "Classic is the first of the preset's five choices");
        foreach (Knob<string> area in new[] { Shadows, Lighting, Reflections, Ao, Detail, Grass, Weather, Effects })
            check.Expect(area.RungValue(classic) == "low" && area.RungValue(low) == "low", $"{area.Key}: Classic takes the Low rung");
        check.Expect(CX.Alloy.Grass.Style.RungValue(classic) == 1 && CX.Alloy.Grass.Style.RungValue(Shipped) == 0,
          "grass.style: game textures under Classic, blades otherwise");
        check.Expect(!CX.Alloy.Weather.Enabled.RungValue(classic) && CX.Alloy.Weather.Enabled.RungValue(Shipped), "weather.enabled: off under Classic");
        check.Expect(!Fog.Enabled.RungValue(classic) && Fog.Enabled.RungValue(Shipped), "fog.enabled: off under Classic");
        check.Expect(Post.Bloom.RungValue(classic) == 0f && Post.Bloom.RungValue(Shipped) == 0.06f, "post.bloom: 0 under Classic");
        check.Expect(Post.Aa.RungValue(classic) == "smaa" && Post.Aa.RungValue(Shipped) == "taa", "post.aa: SMAA under Classic, TAA shipped");
        check.Expect(Post.Tonemap.RungValue(classic) == 1 && Post.Tonemap.RungValue(Shipped) == 2, "post.tonemap: shoulder under Classic");
        check.Expect(Post.TmKnee.RungValue(classic) == 1f && Post.TmKnee.RungValue(Shipped) == 0.8f, "post.tmknee: 1 under Classic");
        check.Expect(!CX.Alloy.Lighting.Brdf.RungValue(classic) && CX.Alloy.Lighting.Brdf.RungValue(Shipped), "light.brdf: off under Classic");
        // Classic leaves these to the Low area rung, which must keep them off.
        check.Expect(!CX.Alloy.Lighting.Probe.RungValue(0) && !CX.Alloy.Lighting.Ssr.RungValue(0) && !CX.Alloy.Lighting.SsrPlanar.RungValue(0)
          && CX.Alloy.Effects.Halos.RungValue(0) == 0f, "light.probe/ssr/ssrplanar and effects.halos: off at the Low area rung");
        log.Info(check.Ok ? "Quality.SelfCheck: passed" : "Quality.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Declare the group: the renderer touches it before any member can.</summary>
    internal static void Touch() { }
}
