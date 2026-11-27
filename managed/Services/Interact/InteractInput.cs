using System.Collections.Generic;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Interact;

/// <summary>What Interact remembers between presses: the world it reads, the objects its keys tried that may be
/// passed over, and the key held down. One lives in <see cref="InteractInput"/>.</summary>
internal sealed class InteractState
{
    internal IInteractWorld? World;
    /// <summary>The press code each key setting binds, by <see cref="InteractKey"/>, read at a setting's edit; 0 unbound.</summary>
    internal readonly int[] Codes = new int[Bindable];
    /// <summary>The keys with a row in <see cref="Codes"/>: Interact, Transition, Loot, Pick up and Door.</summary>
    internal const int Bindable = (int)InteractKey.Door + 1;
    /// <summary>The locked objects and the items an Interact press tried: a locked one is passed over until it reads
    /// unlocked, an item while it lies on the ground.</summary>
    internal readonly HashSet<uint> Tried = new();
    /// <summary>An Interact press is running its default action, so the click-to-lock leaves the lock alone.</summary>
    internal bool Acting;
}

/// <summary>The entries the engine side calls: the key handler's press, the chord layer's, the default-action detour
/// and the conversation and area reports. A self-check calls the same entries on a double.</summary>
internal static class InteractInput
{
    private static readonly InteractState S = new();

    /// <summary>Interact's place in the chord layer, where a key that no window took is offered.</summary>
    internal static readonly IChordConsumer Chord = new InteractChord();

    /// <summary>Installs the world presses read and act on, and forgets what the last one tried; null stands down and
    /// leaves the chord layer. Returns the world it replaced.</summary>
    internal static IInteractWorld? Use(IInteractWorld? world)
    {
        IInteractWorld? was = S.World;
        S.World = world;
        S.Tried.Clear();
        if (world != null) { KeysChanged(); UiHost.AddChordConsumer(Chord); }
        else UiHost.RemoveChordConsumer(Chord);
        return was;
    }

    /// <summary>Reads each key setting's press code into <see cref="InteractState.Codes"/>. Cold: a setting's edit.</summary>
    internal static void KeysChanged()
    {
        S.Codes[(int)InteractKey.Interact] = KeyNames.Code(InteractKnobs.Key.V);
        S.Codes[(int)InteractKey.Transition] = KeyNames.Code(InteractKnobs.TransitionKey.V);
        S.Codes[(int)InteractKey.Loot] = KeyNames.Code(InteractKnobs.LootKey.V);
        S.Codes[(int)InteractKey.PickUp] = KeyNames.Code(InteractKnobs.PickUpKey.V);
        S.Codes[(int)InteractKey.Door] = KeyNames.Code(InteractKnobs.DoorKey.V);
    }

    /// <summary>A press from WinMessageHandler: an SDL key code, or -4 / -5 for the thumb buttons. True when Interact
    /// took it, so the game never sees it.</summary>
    internal static bool KeyDown(int code, bool repeat)
    {
        if (Bound(code) is not { } key || S.World is not { KeysFree: true } world) return false;
        if (repeat) return true;
        if (InteractCore.Pick(key, world, S) is not { } picked) return true;
        S.Acting = true;
        try { Act(world, picked); }
        finally { S.Acting = false; }
        return true;
    }

    /// <summary>Runs the act a press picked: a walk into an area transition, else the object's default action.</summary>
    private static void Act(IInteractWorld world, in Thing t)
    {
        if (t.LeadsAway && (t.Kind != ThingKind.Door || t.Open)) world.WalkTo(t.Id, InteractCore.WalkPoint(t, world.Player));
        else world.DefaultAction(t.Id);
    }

    /// <summary>The key <paramref name="code"/> presses, when a world is in and the master is on; null otherwise.</summary>
    private static InteractKey? Bound(int code)
    {
        if (S.World == null || !InteractKnobs.Enabled.V || code == 0) return null;
        for (int i = 0; i < InteractState.Bindable; i++)
            if (S.Codes[i] == code) return (InteractKey)i;
        return null;
    }

    /// <summary>The game's Key Mapping action bound to press <paramref name="code"/>, by its Key Mapping name; null
    /// when none. Cold: a key row's bind.</summary>
    internal static string? GameKeyAction(int code) => S.World?.GameKeyAction(code);

    /// <summary>Every default action the engine runs on an object, a mouse click's as well as Interact's own.</summary>
    internal static void DefaultActionSeen(uint id) { }

    /// <summary>Whether TabTarget's click-to-lock takes the object a default action just ran on: false when the action
    /// was an Interact press, which never moves the lock.</summary>
    internal static bool ClickLocks(uint id) => !S.Acting;

    /// <summary>A conversation window opened with <paramref name="id"/> in CX's dialog.</summary>
    internal static void ConversationOpened(uint id) { }

    /// <summary>A conversation opened with <paramref name="id"/> in the game's own window, CX's dialog being off.</summary>
    internal static void GameConversationOpened(uint id) { }

    /// <summary>A creature's default action spoke a line over its head and opened no conversation.</summary>
    internal static void Barked(uint id) { }

    /// <summary>The player arrived in an area and it finished loading.</summary>
    internal static void AreaLoaded() { }

    /// <summary>Interact's keys in the chord layer, ranked above the action camera's toggle (priority 1), so a key
    /// bound to both runs Interact.</summary>
    private sealed class InteractChord : IChordConsumer
    {
        public int Priority => 2;
        public bool WantsKey(int key) => Bound(key) != null;
        public void Fire(uint key, int sdlMods) => KeyDown((int)key, false);
        public void FireMouse(int button, int sdlMods) => KeyDown(-button, false);
    }
}
