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
    /// <summary>The engine's world, which <see cref="InteractInput.Resume"/> puts back after a self-check's double.</summary>
    internal IInteractWorld? Live;
    /// <summary>The locked objects an Interact press tried, passed over until they read unlocked.</summary>
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
    /// leaves the chord layer.</summary>
    internal static void Use(IInteractWorld? world)
    {
        S.World = world;
        S.Tried.Clear();
        if (world != null) UiHost.AddChordConsumer(Chord);
        else UiHost.RemoveChordConsumer(Chord);
    }

    /// <summary>Installs the engine's world, the one <see cref="Resume"/> returns to.</summary>
    internal static void Live(IInteractWorld world)
    {
        S.Live = world;
        Use(world);
    }

    /// <summary>Puts the engine's world back once a self-check's double stood down; the pump calls it on every event.</summary>
    internal static void Resume()
    {
        if (S.World == null && S.Live != null) Use(S.Live);
    }

    /// <summary>A press from WinMessageHandler: an SDL key code, or -4 / -5 for the thumb buttons. True when Interact
    /// took it, so the game never sees it.</summary>
    internal static bool KeyDown(int code, bool repeat)
    {
        if (Bound(code) is not { } key || S.World is not { KeysFree: true } world) return false;
        if (repeat) return true;
        uint id = InteractCore.Pick(key, world, S);
        if (id == TypingSignal.ObjectInvalid) return true;
        S.Acting = true;
        try { world.DefaultAction(id); }
        finally { S.Acting = false; }
        return true;
    }

    /// <summary>The key <paramref name="code"/> presses, when a world is in and the master is on; null otherwise.</summary>
    private static InteractKey? Bound(int code)
    {
        if (S.World == null || !InteractKnobs.Enabled.V || code == 0) return null;
        if (code == KeyNames.Code(InteractKnobs.Key.V)) return InteractKey.Interact;
        if (code == KeyNames.Code(InteractKnobs.LootKey.V)) return InteractKey.Loot;
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
