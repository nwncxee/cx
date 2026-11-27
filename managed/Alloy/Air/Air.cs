using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>air</c> group: what hangs in an area's air and how it moves -- dust, mist, motes, pollen, haze,
/// wisps and leaves, chosen by the area's place. On by default and drawn with Volumetric fog on or off; every key is
/// one a server may hold. Touched from <see cref="RenderCommands.Register"/>.</summary>
internal static class Air
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<string> Quality, Place;
    internal static readonly Knob<float> Dust;

    /// <summary>The <see cref="Quality"/> values, cheapest first.</summary>
    internal static readonly string[] Levels = { "low", "medium", "high" };

    /// <summary>Does the air lie still at the user's Air detail: at Low no gust, blast or walker stirs it.</summary>
    internal static bool Still => Quality.V == Levels[0];
    /// <summary>The <see cref="Place"/> values: <c>auto</c> reads the area, the rest name a place's look.</summary>
    internal static readonly string[] Places = ["auto", .. Array.ConvertAll(AirPlaces.Kinds, k => k.Name)];

    static Air()
    {
        G = KnobGroup.Declare("air", "Graphics", "World", master: "Air", masterDefault: true,
            masterHelp: "What hangs in an area's air and how it moves: dust in crypts, mist by night water, motes in lamplight, "
                      + "pollen and leaves outdoors. It drifts on the wind, gusts when the wind changes and parts around walkers and blasts.",
            order: 348, title: "Air");
        Enabled = G.Master!;
        Quality = G.Dropdown("quality", "Air detail", Levels, "medium", new[] { "Low", "Medium", "High" },
                             tier: Tier.Advanced, help: "How much of the air moves. At Low it drifts slowly, and gusts, blasts and walkers no longer stir it.")
                  .Ladder(CX.Alloy.Quality.Effects, "low", "low", "medium", "high");
        Dust = G.Slider("dust", "Dust", 0f, 2f, 0.05f, 1f, tier: Tier.Advanced,
                        help: "How much dust lies on the floor where an area lays it. 0 lays none.");
        Place = G.Dropdown("place", "Place", Places, "auto", new[] { "From the area", "None", "Crypt", "City", "Forest", "Meadow" },
                           tier: Tier.ChatOnly, help: "Which place's air the area takes; From the area reads its tileset and tiles.");
        ServerPolicy.AllowKnobs(G.Name + ".");
    }
}
