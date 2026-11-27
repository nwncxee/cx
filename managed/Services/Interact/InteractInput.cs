using CX.Ui;

namespace CX.Services.Interact;

/// <summary>What Interact remembers between presses: the world it reads, the objects its keys tried that may be
/// passed over, and the key held down. One lives in <see cref="InteractInput"/>.</summary>
internal sealed class InteractState
{
    internal IInteractWorld? World;
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
        if (world != null) UiHost.AddChordConsumer(Chord);
        else UiHost.RemoveChordConsumer(Chord);
    }

    /// <summary>A press from WinMessageHandler: an SDL key code, or -4 / -5 for the thumb buttons. True when Interact
    /// took it, so the game never sees it.</summary>
    internal static bool KeyDown(int code, bool repeat) => false;

    /// <summary>Every default action the engine runs on an object, a mouse click's as well as Interact's own.</summary>
    internal static void DefaultActionSeen(uint id) { }

    /// <summary>Whether TabTarget's click-to-lock takes the object a default action just ran on: false when the action
    /// was an Interact press, which never moves the lock.</summary>
    internal static bool ClickLocks(uint id) => true;

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
        public bool WantsKey(int key) => false;
        public void Fire(uint key, int sdlMods) { }
        public void FireMouse(int button, int sdlMods) { }
    }
}
