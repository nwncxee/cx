using CX.Framework;
using static CX.Alloy.AirFixture;

namespace CX.Alloy;

/// <summary>The place resolver's plain regressions: the forest's half-share edge, an edge's water and an indoor one's, a
/// table row's case and a row naming no place, and which layers a look hangs by night, in snow, by water and in a crypt.
/// Oracle, by hand: the place rules the council settled and the tile counts each case builds.</summary>
internal static class AirPlaceRegressions
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirPlaceRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        try
        {
            HalfTrees(check); NoTilesRead(check); StreamEdgeWet(check); RowCase(check); RowNoPlace(check);
            NightPollen(check); SnowyForest(check); DryNight(check); WetDay(check); CityMist(check);
            IndoorDry(check); NoneByWater(check); CryptLayers(check);
        }
        finally { AirPlaces.Load(null); }
        log.Info(check.Ok ? "AirPlaceRegressions.SelfCheck: passed" : "AirPlaceRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>An outdoor custom area, its first <paramref name="n"/> of 64 tiles with one <paramref name="some"/> corner.</summary>
    private static AreaFeatures Share(int n, string some, string rest, string edge = "")
        => Features("zcx09", 0, 64, i => i < n ? new[] { some, rest, rest, rest } : new[] { rest, rest, rest, rest },
                    i => i < n ? new[] { edge, "", "", "" } : new[] { "", "", "", "" });

    // Given an outdoor area with trees on exactly half its tiles
    // When the player enters it
    // Then it takes the forest look, and one tile fewer takes the meadow's.
    private static void HalfTrees(Checker check)
    {
        AirPlace half = LookOf(Share(32, "Trees", "Grass")).Place, less = LookOf(Share(31, "Trees", "Grass")).Place;
        check.Expect(half == AirPlace.Forest && less == AirPlace.Meadow, $"32 of 64 tree tiles take {half}, 31 take {less} (want Forest, Meadow)");
    }

    // Given an outdoor area whose tiles could not be read
    // When the player enters it
    // Then it takes the meadow look.
    private static void NoTilesRead(Checker check)
    {
        AirPlace got = LookOf(AreaFeatures.Of("zcx09", 0, new(), new())).Place;
        check.Expect(got == AirPlace.Meadow, $"an outdoor area with no tiles read takes {got} (want Meadow)");
    }

    // Given an outdoor area with a stream edge and no water corner
    // When the player enters it
    // Then its water is marked.
    private static void StreamEdgeWet(Checker check)
    {
        AirLook look = LookOf(Share(4, "Grass", "Grass", "Stream"));
        check.Expect(look.Water, $"a stream edge with no water corner marks water {look.Water} (want true)");
    }

    // Given a hak whose place table spells a place in capitals
    // When the player enters an area on that tileset
    // Then the row picks its place.
    private static void RowCase(Checker check)
    {
        AirPlaces.Load(new[] { ("ttr01", "Forest") });
        AirPlace got = LookOf(Plain("ttr01", 0, "Grass")).Place;
        AirPlaces.Load(null);
        check.Expect(got == AirPlace.Forest, $"a ttr01=Forest row takes {got} (want Forest)");
    }

    // Given a hak whose place table names no place for City Interior
    // When the player enters a City Interior area
    // Then the shipped row stands and no air look shows.
    private static void RowNoPlace(Checker check)
    {
        AirPlaces.Load(new[] { ("tin01", "swamp") });
        AirPlace got = LookOf(Plain("tin01", Interior, "Livingroom")).Place;
        AirPlaces.Load(null);
        check.Expect(got == AirPlace.None, $"under a tin01=swamp row a City Interior area takes {got} (want None, the shipped row)");
    }

    // Given a meadow
    // When night falls
    // Then its pollen is gone and its wisps stay.
    private static void NightPollen(Checker check)
    {
        AirLayer night = new AirLook(AirPlace.Meadow, false, false, false).Layers(true);
        check.Expect(night == AirLayer.Wisps, $"a meadow at night lays {night} (want wisps alone)");
    }

    // Given a forest under snow
    // When the player stands in it by day
    // Then haze hangs and no leaves fall.
    private static void SnowyForest(Checker check)
    {
        AirLayer day = new AirLook(AirPlace.Forest, true, false, false).Layers(false);
        check.Expect(day == AirLayer.Haze, $"a snowy forest by day lays {day} (want haze alone)");
    }

    // Given a meadow with no water among its tiles
    // When night falls
    // Then no night mist gathers.
    private static void DryNight(Checker check)
    {
        AirLayer night = new AirLook(AirPlace.Meadow, false, false, false).Layers(true);
        check.Expect(!night.HasFlag(AirLayer.NightMist), $"a dry meadow at night lays {night} (want no night mist)");
    }

    // Given a meadow by water
    // When the player stands in it by day
    // Then no night mist lies.
    private static void WetDay(Checker check)
    {
        AirLayer day = new AirLook(AirPlace.Meadow, false, true, false).Layers(false);
        check.Expect(!day.HasFlag(AirLayer.NightMist), $"a meadow by water by day lays {day} (want no night mist)");
    }

    // Given a city by water
    // When night falls
    // Then night mist gathers, and none by day.
    private static void CityMist(Checker check)
    {
        var city = new AirLook(AirPlace.City, false, true, false);
        check.Expect(city.Layers(true) == AirLayer.NightMist && city.Layers(false) == AirLayer.None,
            $"a city by water lays {city.Layers(true)} at night, {city.Layers(false)} by day (want night mist, then nothing)");
    }

    // Given an indoor area with water among its tiles
    // When the player enters it
    // Then its water is not marked.
    private static void IndoorDry(Checker check)
    {
        AirLook look = LookOf(Features("zcx09", Interior, 4, _ => new[] { "Water", "Floor", "Floor", "Floor" }, _ => new[] { "", "", "", "" }));
        check.Expect(!look.Water, $"an indoor area with water corners marks water {look.Water} (want false)");
    }

    // Given an outdoor Sea Ships area with water among its tiles
    // When night falls
    // Then its water is marked and no night mist gathers.
    private static void NoneByWater(Checker check)
    {
        AirLook look = LookOf(Plain("tss13", 0, "Water"));
        AirLayer night = look.Layers(true);
        check.Expect(look.Place == AirPlace.None && look.Water && night == AirLayer.None,
            $"a Sea Ships area by water takes {look.Place}, water {look.Water}, and lays {night} at night (want None, true, nothing)");
    }

    // Given a crypt
    // When the player stands in it
    // Then dust lies and motes hang.
    private static void CryptLayers(Checker check)
    {
        AirLayer day = new AirLook(AirPlace.Crypt, false, false, false).Layers(false);
        check.Expect(day == (AirLayer.Dust | AirLayer.Motes), $"a crypt lays {day} (want Dust, Motes)");
    }
}
