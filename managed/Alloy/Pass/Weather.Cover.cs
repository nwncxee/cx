using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The weather side of the rain occlusion map -- the dials, the "is one wanted" gate the shadow
/// bracket reads, and the two consumers' uploads: the precipitation vertex stage (a covered instance is not
/// drawn) and the lit stage, where exposure is what wets and snows a surface. One matrix, one layer index,
/// one freshness gate; the map itself is <see cref="CasterPass.CoverVp"/>'s layer of the sun array.</summary>
internal static unsafe partial class Weather
{
    internal const int VizMax = 4;
    internal static Knob<bool> Cover = null!, WaterRoof = null!;
    internal static Knob<float> CoverRange = null!, CoverSoft = null!;

    private static void DeclareCoverKnobs()
    {
        G.Section("Cover");
        Cover = G.Toggle("cover", "Shelter under roofs", true, Tier.Advanced,
                         help: "Nothing falls under a roof, an awning or a doorway, and what they shelter stays dry. Off, it rains indoors.");
        G.Section("");
        WaterRoof = G.Toggle("waterroof", "Water sheds snow", true, Tier.ChatOnly,
                             help: "Water surfaces count as a roof: nothing wets or snows the bed beneath them.");
        CoverSoft = G.Slider("coversoft", "Cover edge", 0f, 4f, 0.05f, 1f, "m", Tier.ChatOnly,
                             help: "How far a roof's shelter feathers at its edge: wind and splash make no hard line.",
                             changed: CasterPass.RefilterCover);
        CoverRange = G.Slider("coverrange", "Cover range", 16f, 128f, 4f, 60f, "m", Tier.ChatOnly,
                              help: "Half-width of the cover map around the player. Resolution is the sun shadow map's (one allocation).");
    }

    private static void InstallCover(ExtenderLog log)
        => log.Info("Weather: cover map rides the sun array's spare layer; /weather viz 2 paints exposure on the world");

    /// <summary>The shadow bracket bakes a cover map while there is weather to shelter from, and holds it while
    /// anything is still soaked: the wet/snowed ground keeps its sheltered patches dry and the drips their
    /// edges after the stop edge.</summary>
    internal static bool CoverWanted => !_off && Enabled.V && Cover.V && !FrameInputs.Interior
        && ((CurrentKind != Kind.None && Density > 0f) || _wet > 0f || _snow > 0f);

    private static string CoverStats() => Cover.V ? $", {CasterPass.CoverStats()}" : "";
}
