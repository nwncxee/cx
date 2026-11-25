using System;

namespace CX.Alloy;

/// <summary>A layer of what hangs in the air; a place's look is a set of them.</summary>
[Flags]
internal enum AirLayer
{
    None = 0, Dust = 1, NightMist = 2, Motes = 4, Pollen = 8, Haze = 16, Wisps = 32, Leaves = 64,
    /// <summary>The layers that lie on the ground and move with the air grid.</summary>
    Lying = Dust | NightMist,
}

/// <summary>The kind of place an area's air reads as.</summary>
internal enum AirPlace { None, Crypt, City, Forest, Meadow }

/// <summary>An area's air: its place and what about it changes the layers (snow, water close by, a cave).</summary>
internal readonly record struct AirLook(AirPlace Place, bool Winter, bool Water, bool Cave)
{
    /// <summary>The layers this look hangs in the air by day or by night.</summary>
    internal AirLayer Layers(bool night) => Place == AirPlace.Crypt ? AirLayer.Dust : AirLayer.None;
}

/// <summary>One particle of a layer: where it is and how it moves, in metres and metres a second.</summary>
internal readonly record struct AirParticle(System.Numerics.Vector3 Pos, System.Numerics.Vector3 Vel);

/// <summary>The wisps' shape: how far the wind stretches them, and how far it has carried them.</summary>
internal readonly record struct AirWisps(System.Numerics.Vector2 Stretch, System.Numerics.Vector2 Carried);

/// <summary>A gust front crossing the air: whether one is passing, how strong it is and which way it runs.</summary>
internal readonly record struct AirFront(bool Live, float Strength, System.Numerics.Vector2 Dir);
