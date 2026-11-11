using System;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>What <see cref="Unseen"/> holds between frames: the creatures drawing as silhouettes.</summary>
internal sealed class UnseenState
{
    internal readonly Unseen.Mark[] Marks = new Unseen.Mark[Unseen.MaxMarks];
    internal int Count;
    internal bool Spilled;
}

/// <summary>Creatures the player character hears but does not see. The game draws them half see-through, exactly as
/// it draws one under Invisibility; Alloy draws them as a solid flat silhouette so the two read apart.</summary>
internal static class Unseen
{
    /// <summary>The perception marks of <c>CNWVisibilityNode</c>'s flag byte, as <c>CNWCObject::GetFinalFadeAlpha</c>
    /// tests them: bit 0 seen, bit 1 heard, bit 4 invisible.</summary>
    internal const byte Seen = 1, Heard = 2, Invisible = 16;

    /// <summary>The alpha <c>CNWCObject::GetFinalFadeAlpha</c> answers for a heard-but-unseen creature, which a
    /// silhouette draws solid at; under it the game is fading the creature in or out.</summary>
    internal const float EngineAlpha = 0.5f;

    /// <summary>The record's slot-mask bit for a silhouette, <c>CX_SLOT_SILHOUETTE</c>. Above the palette bits.</summary>
    internal const int SlotBit = 1 << 13;

    /// <summary>Silhouettes held at once. ponytail: past it the rest draw see-through as the game has them, logged once;
    /// a perception list this long is the cue for a table keyed by object.</summary>
    internal const int MaxMarks = 256;

    /// <summary>One perception entry: the game object it names, its marks, and the creature's scene object.</summary>
    internal readonly record struct Sight(uint Oid, byte Marks, DbId Root);

    /// <summary>One creature drawing as a silhouette.</summary>
    internal readonly record struct Mark(uint Oid, DbId Root);

    private static UnseenState _s = new();

    /// <summary>Forget every silhouette: an area change or a stand-down.</summary>
    internal static void Reset()
    {
        for (int k = 0; k < _s.Count; k++)
            if (SceneDb.Objects.Valid(_s.Marks[k].Root)) SceneDb.ObjUnseen[_s.Marks[k].Root.Index] = false;
        _s = new UnseenState();
    }

    /// <summary>Do these perception marks make a silhouette?</summary>
    internal static bool Silhouetted(byte marks) => false;

    /// <summary>Is this object part of a silhouette? A creature's pieces and everything riding it answer as it does.</summary>
    internal static bool Of(int obj)
    {
        DbId root = SceneDb.ObjRunRoot[obj];
        return SceneDb.ObjUnseen[SceneDb.Objects.Valid(root) ? root.Index : obj];
    }

    /// <summary>The object alpha a part of <paramref name="obj"/> draws with, from the game's.</summary>
    internal static float Alpha(int obj, float engineAlpha, bool lit) => engineAlpha;

    /// <summary>The slot mask a lit part of <paramref name="obj"/> carries.</summary>
    internal static int Slots(int obj, int slots) => Of(obj) ? slots | SlotBit : slots;

    /// <summary>This frame's perception entries: which creatures are silhouettes from here on.</summary>
    internal static void Frame(ReadOnlySpan<Sight> sights)
    {
    }
}
