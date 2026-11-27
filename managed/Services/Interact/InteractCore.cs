using System;
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
    /// <summary>The player's own associate (henchman, companion, familiar, summon): its master is the player.</summary>
    public bool Party;
    /// <summary>Another player's character.</summary>
    public bool OtherPlayer;
    public Attitude Attitude;
    /// <summary>An item whose pickup the client sent and the server has not answered: the character may still be
    /// walking to it.</summary>
    public bool Pending;
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
    /// <summary>Walks the player to <paramref name="point"/> with <paramref name="id"/> as the walk's target, as a click
    /// on an area transition does, so the transition fires on arrival.</summary>
    void WalkTo(uint id, Vector3 point);
    void ShowLine(string text);
    void PlaySound(string resref);
}

/// <summary>Interact's pure decisions over a world snapshot: which object a key acts on, or none.</summary>
internal static class InteractCore
{
    /// <summary>How far, in metres, a rule reaches from the player.</summary>
    internal const float Reach = 10f;
    /// <summary>How far, in metres, the Transition rule reaches.</summary>
    internal const float TransitionReach = 2f;

    /// <summary>The rules in the fixed order the Interact key runs them.</summary>
    private static readonly InteractKey[] Rules = { InteractKey.Transition, InteractKey.Loot, InteractKey.PickUp, InteractKey.Door, InteractKey.Talk };

    /// <summary>The object <paramref name="key"/> acts on in <paramref name="world"/>, or null. An object under the
    /// crosshair, else a tab-target lock, in the area is the only object the press may act on, and
    /// <paramref name="refused"/> says it qualified under no rule. A tried object no longer listed is forgotten.</summary>
    internal static Thing? Pick(InteractKey key, IInteractWorld world, InteractState state, out bool refused)
    {
        IReadOnlyList<Thing> things = world.Things;
        state.Tried.RemoveWhere(id => Find(things, id) == null);
        refused = false;
        if ((Find(things, world.Crosshair) ?? Find(things, world.Lock)) is { } pointed)
        {
            Thing? taken = PassedOver(pointed, state) ? null : Pointed(key, pointed, world.Player);
            refused = taken == null;
            if (taken is { } t) Record(t, state);
            return taken;
        }
        foreach (InteractKey rule in Rules)
        {
            if (key == InteractKey.Interact ? !InInteract(rule) : key != rule) continue;
            if (Nearest(rule, things, world, state) is { } t) return t;
        }
        return null;
    }

    /// <summary>Pointed target <paramref name="t"/> when a rule <paramref name="key"/> runs takes it, at any distance but
    /// an area transition's; else nothing.</summary>
    private static Thing? Pointed(InteractKey key, Thing t, Vector3 player)
    {
        foreach (InteractKey rule in Rules)
        {
            if (key == InteractKey.Interact ? !InInteract(rule) : key != rule) continue;
            if (!Qualifies(rule, t)) continue;
            if (rule == InteractKey.Transition && Vector3.Distance(MeasuredAt(t, player), player) > TransitionReach) continue;
            return t;
        }
        return null;
    }

    private static Thing? Find(IReadOnlyList<Thing> things, uint id)
    {
        if (id == TypingSignal.ObjectInvalid) return null;
        for (int i = 0; i < things.Count; i++) if (things[i].Id == id) return things[i];
        return null;
    }

    /// <summary>Where a rule measures <paramref name="t"/> from: a trigger's outline point nearest the player, else its position.</summary>
    private static Vector3 MeasuredAt(in Thing t, Vector3 player) => t.Outline is { } outline ? NearestOnOutline(outline, player) : t.Pos;

    /// <summary>Whether the Interact key runs <paramref name="rule"/>.</summary>
    private static bool InInteract(InteractKey rule) => rule switch
    {
        InteractKey.Transition => InteractKnobs.Transitions.V,
        InteractKey.Loot => InteractKnobs.Loot.V,
        InteractKey.PickUp => InteractKnobs.PickUp.V,
        InteractKey.Door => InteractKnobs.Doors.V,
        InteractKey.Talk => InteractKnobs.Talk.V,
        _ => false,
    };

    /// <summary>The nearest object of <paramref name="things"/> in reach and in sight that <paramref name="rule"/> takes. A locked winner, an
    /// item or a creature asked to talk is recorded as tried and passed over after that.</summary>
    private static Thing? Nearest(InteractKey rule, IReadOnlyList<Thing> things, IInteractWorld world, InteractState state)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < things.Count; i++)
        {
            Thing t = things[i];
            if (PassedOver(t, state)) continue;
            if (!Qualifies(rule, t)) continue;
            Vector3 at = MeasuredAt(t, world.Player);
            float dist = Vector3.Distance(at, world.Player);
            if (dist > (rule == InteractKey.Transition ? TransitionReach : Reach) || dist >= bestDist || !world.Sees(world.Player, at)) continue;
            best = i; bestDist = dist;
        }
        if (best < 0) return null;
        Record(things[best], state);
        return things[best];
    }

    /// <summary>Records <paramref name="t"/>, which a press acts on, as tried when it is locked, an item or a creature.</summary>
    private static void Record(in Thing t, InteractState state)
    {
        if (t.Locked || t.Kind is ThingKind.Item or ThingKind.Creature) state.Tried.Add(t.Id);
    }

    /// <summary>Whether a press passes over <paramref name="t"/> as tried: a locked object tried while locked, an item
    /// tried once, still on the ground and no longer pending, or a living creature asked to talk that opened no
    /// conversation. An object read unlocked leaves the tried memory.</summary>
    private static bool PassedOver(in Thing t, InteractState state)
    {
        if (t.Kind == ThingKind.Item) return !t.Pending && state.Tried.Contains(t.Id);
        if (t.Kind == ThingKind.Creature && !t.Dead) return state.Tried.Contains(t.Id);
        if (!t.Locked) state.Tried.Remove(t.Id);
        return t.Locked && state.Tried.Contains(t.Id);
    }

    /// <summary>How far past a trigger's nearest edge a walk into it heads, in metres.</summary>
    internal const float Inset = 0.5f;

    /// <summary>Where a walk into area transition <paramref name="t"/> heads: just inside a trigger's nearest edge, or
    /// a door's own position.</summary>
    internal static Vector3 WalkPoint(in Thing t, Vector3 player)
    {
        if (t.Outline is not { } outline) return t.Pos;
        Vector3 q = NearestOnOutline(outline, player), dir = new(q.X - player.X, q.Y - player.Y, 0f);
        float len = dir.Length();
        return len > 0f ? q + dir * (Inset / len) : q;
    }

    /// <summary>The point of the closed outline <paramref name="outline"/> nearest <paramref name="from"/>, on the ground
    /// plane. A repeated point makes a zero-length edge whose NaN distance never wins.</summary>
    internal static Vector3 NearestOnOutline(Vector3[] outline, Vector3 from)
    {
        Vector3 best = outline[0];
        float bestDist = float.MaxValue;
        for (int i = 0; i < outline.Length; i++)
        {
            Vector3 a = outline[i], ab = outline[(i + 1) % outline.Length] - a;
            float f = Math.Clamp(((from.X - a.X) * ab.X + (from.Y - a.Y) * ab.Y) / (ab.X * ab.X + ab.Y * ab.Y), 0f, 1f);
            Vector3 q = a + ab * f;
            float d = (q.X - from.X) * (q.X - from.X) + (q.Y - from.Y) * (q.Y - from.Y);
            if (d < bestDist) { best = q; bestDist = d; }
        }
        return best;
    }

    private static bool Qualifies(InteractKey rule, in Thing t) => rule switch
    {
        InteractKey.Transition => (t.Kind == ThingKind.Transition || t.Kind == ThingKind.Door) && t.LeadsAway && !t.TrapFound,
        InteractKey.Talk => t.Kind == ThingKind.Creature && !t.Dead && t.Attitude != Attitude.Hostile && !t.Party && !t.OtherPlayer,
        InteractKey.Loot => (t.Kind == ThingKind.BodyBag || (t.Kind == ThingKind.Creature && t.Dead)) && t.Lootable && !t.TrapFound,
        InteractKey.PickUp => t.Kind == ThingKind.Item,
        InteractKey.Door => t.Kind == ThingKind.Door && !t.LeadsAway && !t.Open && !t.Destroyed && !t.TrapFound,
        _ => false,
    };
}
