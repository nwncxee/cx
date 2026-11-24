using System;
using System.Numerics;
using CX.Framework;
using CX.Services.Sound;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The look an area with no <c>cx.air</c> variables takes from its own features. Oracle, by hand: the stock
/// tilesets' .set files (their names, interior flags and corner terrains, read from the game data) and the place table
/// the council settled, not the resolver's own rows.</summary>
internal static class AirPlaceApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirPlaceApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        try
        {
            CryptTileset(check); RuralMeadow(check); ForestTileset(check); CityByWater(check); PlaceableMist(check);
            CustomTrees(check); CityInterior(check); HakRuralForest(check); RuralWinter(check); MeadowFogged(check);
            StreamStaysMeadow(check); DungeonOrCustom(check); NaturalUnderground(check); UnderdarkNoPollen(check);
            CaveAgrees(check); HakVeto(check); HakMerges(check); HakWinterMeadow(check); TableDropped(check);
            NoTableKeepsDefaults(check); SeaCavesNoNightMist(check); CastleRuralTrees(check); WinterWisps(check);
            SiblingPlaces(check);
        }
        finally { AirPlaces.Load(null); }
        log.Info(check.Ok ? "AirPlaceApprovedExamples.SelfCheck: passed" : "AirPlaceApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Corners for <paramref name="n"/> of 64 tiles of <paramref name="some"/>, the rest of <paramref name="rest"/>.</summary>
    private static AreaFeatures Share(string tileset, uint flags, int n, string some, string rest)
        => Features(tileset, flags, 64, i => i < n ? new[] { some, rest, rest, rest } : new[] { rest, rest, rest, rest },
                    _ => new[] { "", "", "", "" });

    // Given an indoor area on a crypt tileset with no cx.air variables
    // When the player enters it
    // Then it takes the crypt look.
    private static void CryptTileset(Checker check)
    {
        AirLook look = LookOf(Plain("tdc01", Interior, "Floor"));
        check.Expect(look.Place == AirPlace.Crypt, $"E78 an indoor Crypt (tdc01) area takes {look.Place} (want Crypt)");
    }

    // Given an outdoor rural area with no trees or water among its tiles and no cx.air variables
    // When the player enters it
    // Then it takes the meadow look.
    private static void RuralMeadow(Checker check)
    {
        AirLook look = LookOf(Plain("ttr01", 0, "Grass"));
        check.Expect(look.Place == AirPlace.Meadow && !look.Water, $"E79 an all-grass Rural (ttr01) area takes {look.Place}, water {look.Water} (want Meadow, none)");
    }

    // Given an outdoor area on a forest tileset with no cx.air variables
    // When the player enters it
    // Then it takes the forest look.
    private static void ForestTileset(Checker check)
    {
        AirLook look = LookOf(Plain("ttf01", 0, "Forest"));
        check.Expect(look.Place == AirPlace.Forest, $"E80 a Forest (ttf01) area takes {look.Place} (want Forest)");
    }

    // Given an outdoor area on a city tileset beside water with no cx.air variables
    // When the player enters it
    // Then it takes the city look.
    private static void CityByWater(Checker check)
    {
        AirLook look = LookOf(Share("tcn01", 0, 16, "Water", "Cobble"));
        check.Expect(look.Place == AirPlace.City && look.Water, $"E81 a City Exterior (tcn01) area by water takes {look.Place}, water {look.Water} (want City, by water)");
    }

    // Given a meadow holding a stock fog placeable (silm_fog_50_lo)
    // When the player stands beside it
    // Then mist lies around the placeable within its reach and nowhere else on the meadow.
    private static void PlaceableMist(Checker check)
    {
        const int frames = 300;
        Vector3 at = new(3f, 0f, Knee);
        Enter(Meadow, Flat(), 0f, false);
        EmitterSim.MistEmit("silm_fog_50_lo", new[] { "AuroraEmitter01" }, new[] { at }, frames);
        float near = Near(Lying, 3f, 0f, 2f), far = Near(Lying, 30f, 30f, 4f);
        check.Expect(near > 0f && far == 0f, $"E82 the placeable lays {near:F3} m beside it and {far:F3} m 30 m off (want some, none)");
    }

    // Given an outdoor area on a custom tileset CX has no row for, with trees among its tiles
    // When the player enters it
    // Then it takes the forest look.
    private static void CustomTrees(Checker check)
    {
        AirLook look = LookOf(Share("zcx01", 0, 40, "Trees", "Grass"));
        check.Expect(look.Place == AirPlace.Forest, $"E96 a custom set with trees on 40 of 64 tiles takes {look.Place} (want Forest)");
    }

    // Given an indoor area on the City Interior tileset with no cx.air variables
    // When the player enters it
    // Then no air look shows.
    private static void CityInterior(Checker check)
    {
        AirLook look = LookOf(Plain("tin01", Interior, "Livingroom"));
        check.Expect(look.Place == AirPlace.None && look.Layers(false) == AirLayer.None && look.Layers(true) == AirLayer.None,
            $"E97 a City Interior (tin01) area takes {look.Place}, layers {look.Layers(false)} by day, {look.Layers(true)} by night (want none)");
    }

    // Given a hak whose place table makes the Rural tileset a forest
    // When the player enters a rural area with no cx.air variables
    // Then it takes the forest look.
    private static void HakRuralForest(Checker check)
    {
        AirPlaces.Load(new[] { ("ttr01", "forest") });
        AirLook look = LookOf(Plain("ttr01", 0, "Grass"));
        AirPlaces.Load(null);
        check.Expect(look.Place == AirPlace.Forest, $"E98 under a ttr01=forest row a rural area takes {look.Place} (want Forest)");
    }

    // Given an outdoor area on the Rural Winter tileset with no cx.air variables
    // When the player stands in it by day
    // Then wisps drift overhead and no pollen shows.
    private static void RuralWinter(Checker check)
    {
        AirLayer day = LookOf(Plain("tts01", 0, "Snow")).Layers(false);
        check.Expect(day.HasFlag(AirLayer.Wisps) && !day.HasFlag(AirLayer.Pollen), $"E99 Rural Winter (tts01) by day lays {day} (want wisps, no pollen)");
    }

    // Given a meadow whose fog amount is 15
    // When the player walks it
    // Then nothing lies on the ground, as in E57.
    private static void MeadowFogged(Checker check)
    {
        Enter(Meadow, Flat(), 15f, false);
        Go(3f, Vector2.Zero);
        float lies = Near(Lying, 0f, 0f, 8f);
        check.Expect(lies == 0f, $"E100 a meadow at fog amount 15 lays {lies:F3} m by the player (want none)");
    }

    // Given an outdoor rural area with a stream and no trees among its tiles, and no cx.air variables
    // When the player enters it
    // Then it takes the meadow look, not the city's.
    private static void StreamStaysMeadow(Checker check)
    {
        AreaFeatures f = Features("ttr01", 0, 64, i => i % 4 == 0 ? new[] { "Water", "Grass", "Grass", "Grass" } : new[] { "Grass", "Grass", "Grass", "Grass" },
                                  i => i % 4 == 0 ? new[] { "Stream", "", "Stream", "" } : new[] { "", "", "", "" });
        AirLook look = LookOf(f);
        check.Expect(look.Place == AirPlace.Meadow && look.Water, $"E101 a rural area with a stream takes {look.Place}, water {look.Water} (want Meadow, its water marked)");
    }

    // Given an indoor area on the Dungeon tileset, or a custom one with no row, not flagged natural, with no cx.air variables
    // When the player enters it
    // Then it takes the crypt look.
    private static void DungeonOrCustom(Checker check)
    {
        AirLook dungeon = LookOf(Plain("tde01", Interior, "Floor")), custom = LookOf(Plain("zcx02", Interior, "Floor"));
        check.Expect(dungeon.Place == AirPlace.Crypt && custom.Place == AirPlace.Crypt,
            $"E160 an indoor Dungeon (tde01) area takes {dungeon.Place}, an indoor custom one {custom.Place} (want Crypt both)");
    }

    // Given an indoor area flagged underground and natural, with no cx.air variables
    // When the player enters it
    // Then it takes the crypt look until the cave look lands.
    private static void NaturalUnderground(Checker check)
    {
        AirLook look = LookOf(Plain("zcx02", Interior | Underground | Natural, "Floor"));
        check.Expect(look.Place == AirPlace.Crypt && look.Cave, $"E161 a natural underground interior takes {look.Place}, cave {look.Cave} (want Crypt, marked a cave)");
    }

    // Given an area on the Underdark tileset whose own flags mark it underground
    // When the player enters it
    // Then it takes no meadow look and no pollen shows.
    private static void UnderdarkNoPollen(Checker check)
    {
        AirLook look = LookOf(Plain("ttu01", Underground, "Floor"));
        check.Expect(look.Place != AirPlace.Meadow && !look.Layers(false).HasFlag(AirLayer.Pollen),
            $"E162 an underground Underdark (ttu01) area takes {look.Place}, layers {look.Layers(false)} (want no meadow, no pollen)");
    }

    // Given an interior flagged natural but not underground
    // When the player enters it
    // Then the air's place and the sound's material agree on whether it is a cave.
    private static void CaveAgrees(Checker check)
    {
        foreach ((uint flags, bool cave) in Caves)
        {
            bool air = LookOf(Plain("zcx02", flags, "Floor")).Cave, sound = SoundScene.Material(flags) == SoundScene.CaveRock;
            check.Expect(air == cave && sound == cave, $"E163 flags {flags}: the air says cave {air}, the sound {sound} (want {cave} both)");
        }
    }

    /// <summary>Every set of an area's three flags, each marked by hand as the decision reads it: a cave is natural, and
    /// interior or underground.</summary>
    private static readonly (uint Flags, bool Cave)[] Caves =
    {
        (0, false), (Interior, false), (Underground, false), (Interior | Underground, false), (Natural, false),
        (Natural | Interior, true), (Natural | Underground, true), (Natural | Interior | Underground, true),
    };

    // Given a hak whose place table marks the Forest tileset none
    // When the player enters a forest area with no cx.air variables
    // Then no air look shows.
    private static void HakVeto(Checker check)
    {
        AirPlaces.Load(new[] { ("ttf01", "none") });
        AirLook look = LookOf(Plain("ttf01", 0, "Forest"));
        AirPlaces.Load(null);
        check.Expect(look.Place == AirPlace.None && look.Layers(false) == AirLayer.None, $"E164 under a ttf01=none row a forest area takes {look.Place} (want None)");
    }

    // Given a hak whose place table holds only a Rural row
    // When the player enters a City Interior area
    // Then no air look shows, as in E97, and an Early Winter 2 area keeps its winter look.
    private static void HakMerges(Checker check)
    {
        AirPlaces.Load(new[] { ("ttr01", "forest") });
        AirLook inside = LookOf(Plain("tin01", Interior, "Livingroom")), winter = LookOf(Plain("trs02", 0, "Grass"));
        AirPlaces.Load(null);
        check.Expect(inside.Place == AirPlace.None, $"E165 a City Interior area under a Rural-only table takes {inside.Place} (want None)");
        check.Expect(winter.Winter && !winter.Layers(false).HasFlag(AirLayer.Pollen), $"E165 an Early Winter 2 (trs02) area takes winter {winter.Winter} (want its winter look kept)");
    }

    // Given a hak whose place table makes Early Winter 2 a meadow
    // When the player enters it by day
    // Then pollen shows.
    private static void HakWinterMeadow(Checker check)
    {
        AirPlaces.Load(new[] { ("trs02", "meadow") });
        AirLayer day = LookOf(Plain("trs02", 0, "Grass")).Layers(false);
        AirPlaces.Load(null);
        check.Expect(day.HasFlag(AirLayer.Pollen), $"E166 under a trs02=meadow row Early Winter 2 by day lays {day} (want pollen)");
    }

    // Given a player who left a module whose hak makes Rural a forest
    // When they join a module with no place table and enter a rural area with no trees
    // Then it takes the meadow look.
    private static void TableDropped(Checker check)
    {
        AirPlaces.Load(new[] { ("ttr01", "forest") });
        AirPlaces.Load(null);
        AirLook look = LookOf(Plain("ttr01", 0, "Grass"));
        check.Expect(look.Place == AirPlace.Meadow, $"E167 after a module with no table a rural area takes {look.Place} (want Meadow)");
    }

    // Given content with no cx_airplace.2da
    // When the player enters several areas
    // Then the game's log gains no failed-to-load line for cx_airplace.
    private static void NoTableKeepsDefaults(Checker check)
    {
        AirPlaces.Load(null);
        AirLook crypt = LookOf(Plain("tdc01", Interior, "Floor")), forest = LookOf(Plain("ttf01", 0, "Forest"));
        check.Expect(crypt.Place == AirPlace.Crypt && forest.Place == AirPlace.Forest,
            $"E168 (the part a self-check reaches) with no table the defaults stand: Crypt {crypt.Place}, Forest {forest.Place}");
    }

    // Given an indoor area on Sea Caves with water among its tiles, at night
    // When the player stands by the water
    // Then no night mist gathers over it.
    private static void SeaCavesNoNightMist(Checker check)
    {
        AirLayer night = LookOf(Share("tdt01", Interior, 18, "Water", "Floor")).Layers(true);
        check.Expect(!night.HasFlag(AirLayer.NightMist), $"E169 Sea Caves (tdt01) with water at night lays {night} (want no night mist)");
    }

    // Given an outdoor Castle Exterior, Rural area built from its raised tree tiles
    // When the player enters it
    // Then it takes the forest look.
    private static void CastleRuralTrees(Checker check)
    {
        AirLook look = LookOf(Share("tno01", 0, 48, "Trees+", "Grass"));
        check.Expect(look.Place == AirPlace.Forest, $"E170 a Castle Exterior, Rural (tno01) area of raised tree tiles takes {look.Place} (want Forest)");
    }

    // Given an outdoor area on Early Winter 2 by day, or on a custom tileset with snow corners and no row
    // When the player enters it
    // Then wisps drift and no pollen shows.
    private static void WinterWisps(Checker check)
    {
        AirLayer early = LookOf(Plain("trs02", 0, "Grass")).Layers(false), custom = LookOf(Share("zcx03", 0, 20, "snow", "Grass")).Layers(false);
        check.Expect(early.HasFlag(AirLayer.Wisps) && !early.HasFlag(AirLayer.Pollen), $"E171 Early Winter 2 (trs02) by day lays {early} (want wisps, no pollen)");
        check.Expect(custom.HasFlag(AirLayer.Wisps) && !custom.HasFlag(AirLayer.Pollen), $"E171 a custom set with snow corners by day lays {custom} (want wisps, no pollen)");
    }

    // Given an outdoor area on Desert, Sea Ships or Frozen Wastes by day
    // When the player enters it
    // Then no air look shows until the sibling chart's looks land.
    private static void SiblingPlaces(Checker check)
    {
        foreach ((string set, string corner) in new[] { ("ttd01", "Desert"), ("tss13", "Rural"), ("tti01", "Floor") })
        {
            AirLook look = LookOf(Plain(set, 0, corner));
            check.Expect(look.Place == AirPlace.None && look.Layers(false) == AirLayer.None, $"E172 {set} by day takes {look.Place}, {look.Layers(false)} (want none)");
        }
    }
}
