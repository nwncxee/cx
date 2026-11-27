using System.Collections.Generic;
using System.Numerics;
using CX.Services.Chat.Core;

namespace CX.Services.Interact;

/// <summary>The key a press came in on: Interact runs every rule in its fixed order, each other key its one rule.</summary>
internal enum InteractKey { Interact, Transition, Loot, PickUp, Door, Talk }

/// <summary>What a nearby object is, as far as Interact's rules tell objects apart. A lootable corpse is a dead
/// <see cref="Creature"/>; a chest or a lever is a <see cref="Placeable"/>.</summary>
internal enum ThingKind { Transition, Door, BodyBag, Item, Creature, Placeable }

/// <summary>A creature's attitude to the player, as the attitude byte gives it.</summary>
internal enum Attitude { Friend, Neutral, Hostile }

/// <summary>One object near the player, as the engine side reads it on a press. A trap counts once it is found and a
/// lock once a try showed it; a trap nobody found is read too, so a rule can be seen to ignore it.</summary>
internal struct Thing
{
    public uint Id;
    public ThingKind Kind;
    public Vector3 Pos;
    /// <summary>A trigger's outline on the ground, measured by its nearest edge; null measures from <see cref="Pos"/>.</summary>
    public Vector3[]? Outline;
    public bool Locked, TrapFound, Open, Destroyed, Dead, Lootable, Useable;
    /// <summary>A trap on it the player has not found: what the player cannot know.</summary>
    public bool TrapUnfound;
    /// <summary>A door or trigger that leads to another area.</summary>
    public bool LeadsAway;
    /// <summary>The player's own party member or henchman.</summary>
    public bool Party;
    /// <summary>Another player's character.</summary>
    public bool OtherPlayer;
    public Attitude Attitude;
}

/// <summary>Everything a press reads and does, so the same entry runs on the engine and on a self-check's double.
/// Reads happen at the press; the acts are the engine's own.</summary>
internal interface IInteractWorld
{
    Vector3 Player { get; }
    IReadOnlyList<Thing> Things { get; }
    /// <summary>The object under the action camera's crosshair, <see cref="TypingSignal.ObjectInvalid"/> when none or off.</summary>
    uint Crosshair { get; }
    /// <summary>The tab-target lock, <see cref="TypingSignal.ObjectInvalid"/> when none.</summary>
    uint Lock { get; }
    /// <summary>No text box, chat line or CX window owns the keyboard: a press may run.</summary>
    bool KeysFree { get; }
    /// <summary>Line of sight over the area walkmesh alone: doors, placeables and creatures never block it.</summary>
    bool Sees(Vector3 from, Vector3 to);
    bool SoundExists(string resref);
    /// <summary>The game's Key Mapping action bound to <paramref name="code"/>, by its Key Mapping name; null when none.</summary>
    string? GameKeyAction(int code);
    /// <summary>The object's own default action, as a click on it runs it.</summary>
    void DefaultAction(uint id);
    void ShowLine(string text);
    void PlaySound(string resref);
}

/// <summary>Interact's pure decisions over a world snapshot: which object a key acts on, or none.</summary>
internal static class InteractCore
{
    /// <summary>How far, in metres, a rule reaches from the player.</summary>
    internal const float Reach = 10f;

    /// <summary>The rules in the fixed order the Interact key runs them.</summary>
    private static readonly InteractKey[] Rules = { InteractKey.Loot };

    /// <summary>The object <paramref name="key"/> acts on in <paramref name="world"/>, or <see cref="TypingSignal.ObjectInvalid"/>.</summary>
    internal static uint Pick(InteractKey key, IInteractWorld world, InteractState state)
    {
        foreach (InteractKey rule in Rules)
        {
            if (key == InteractKey.Interact ? !InInteract(rule) : key != rule) continue;
            uint id = Nearest(rule, world, state);
            if (id != TypingSignal.ObjectInvalid) return id;
        }
        return TypingSignal.ObjectInvalid;
    }

    /// <summary>Whether the Interact key runs <paramref name="rule"/>.</summary>
    private static bool InInteract(InteractKey rule) => rule switch
    {
        InteractKey.Loot => InteractKnobs.Loot.V,
        _ => false,
    };

    /// <summary>The nearest object in reach and in sight that <paramref name="rule"/> takes. A locked winner is
    /// recorded as tried and passed over after that; an object read unlocked leaves the tried memory.</summary>
    private static uint Nearest(InteractKey rule, IInteractWorld world, InteractState state)
    {
        IReadOnlyList<Thing> things = world.Things;
        uint best = TypingSignal.ObjectInvalid;
        float bestDist = float.MaxValue;
        bool bestLocked = false;
        for (int i = 0; i < things.Count; i++)
        {
            Thing t = things[i];
            if (!t.Locked) state.Tried.Remove(t.Id);
            if (t.Locked && state.Tried.Contains(t.Id)) continue;
            if (!Qualifies(rule, t)) continue;
            float dist = Vector3.Distance(t.Pos, world.Player);
            if (dist > Reach || dist >= bestDist || !world.Sees(world.Player, t.Pos)) continue;
            best = t.Id; bestDist = dist; bestLocked = t.Locked;
        }
        if (bestLocked) state.Tried.Add(best);
        return best;
    }

    private static bool Qualifies(InteractKey rule, in Thing t) => rule switch
    {
        InteractKey.Loot => (t.Kind == ThingKind.BodyBag || (t.Kind == ThingKind.Creature && t.Dead)) && t.Lootable && !t.TrapFound,
        _ => false,
    };
}
