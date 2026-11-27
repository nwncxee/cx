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
    /// <summary>The keys with a row in <see cref="Codes"/>: Interact and each rule's own.</summary>
    internal const int Bindable = (int)InteractKey.Talk + 1;
    /// <summary>The locked objects, items and creatures an Interact press tried: a locked one is passed over until it
    /// reads unlocked, an item while it lies on the ground, a creature until it opens a conversation.</summary>
    internal readonly HashSet<uint> Tried = new();
    /// <summary>Whether the world has the feedback sound's resref, as the last edit or press found it; null until one
    /// looked.</summary>
    internal bool? SoundFound;
    /// <summary>The bound key down now, from its press to its key-up; 0 when none. A thumb button sends no key-up, so
    /// only a key is kept.</summary>
    internal int Held;
    /// <summary>The key that was down when an area finished loading: its key-downs act on nothing until it comes up.</summary>
    internal int HeldThroughLoad;
}

/// <summary>The entries the engine side calls: the key handler's press, the chord layer's, and the conversation and
/// area reports. A self-check calls the same entries on a double.</summary>
internal static class InteractInput
{
    private static readonly InteractState S = new();

    /// <summary>Interact's place in the chord layer, where a key that no window took is offered.</summary>
    internal static readonly IChordConsumer Chord = new InteractChord();

    /// <summary>The line a press shows when its pointed target qualifies under none of the key's rules.</summary>
    internal const string RefusedLine = "You can't do that with your target.";
    /// <summary>The line a press shows when nothing within reach qualifies, by <see cref="InteractKey"/>.</summary>
    private static readonly string[] Lines =
    {
        "Nothing to interact with here.", "No way out within reach.", "Nothing to loot.", "Nothing to pick up.",
        "No door to open.", "No one to talk to.",
    };

    /// <summary>Installs the world presses read and act on, and forgets what the last one tried and whether it had the
    /// feedback sound; null stands down and leaves the chord layer. Returns the world it replaced.</summary>
    internal static IInteractWorld? Use(IInteractWorld? world)
    {
        IInteractWorld? was = S.World;
        S.World = world;
        S.Tried.Clear();
        S.SoundFound = null;
        S.Held = S.HeldThroughLoad = 0;
        if (world != null) { KeysChanged(); UiHost.AddChordConsumer(Chord); }
        else UiHost.RemoveChordConsumer(Chord);
        return was;
    }

    /// <summary>Reads each key setting's press code into <see cref="InteractState.Codes"/>. Cold: a setting's edit.</summary>
    internal static void KeysChanged()
    {
        for (int i = 0; i < InteractState.Bindable; i++) S.Codes[i] = KeyNames.Code(InteractKnobs.Keys[i].V);
    }

    /// <summary>The note the sound row shows when the game has no sound by its resref.</summary>
    internal const string SoundNotFound = "Not found: the game has no sound by this name, so none plays.";

    /// <summary>Looks the feedback sound's resref up in the world, once per edit. Cold: a setting's edit.</summary>
    internal static void SoundChanged() => S.SoundFound = S.World?.SoundExists(InteractKnobs.Sound.V);

    /// <summary>The sound row's note: <see cref="SoundNotFound"/> once a lookup missed, else none.</summary>
    internal static string? SoundNote() => S.SoundFound == false ? SoundNotFound : null;

    /// <summary>A press from WinMessageHandler: an SDL key code, or -4 / -5 for the thumb buttons. True when Interact
    /// took it, so the game never sees it; a modifier key acts and still reaches the game, so shift-click keeps working.</summary>
    internal static bool KeyDown(int code, bool repeat)
    {
        if (Bound(code) is not { } key || S.World is not { KeysFree: true } world) return false;
        bool took = !KeyNames.IsModifier(code);
        if (repeat || code == S.HeldThroughLoad) return took;
        if (code > 0) S.Held = code;
        Thing? picked = InteractCore.Pick(key, world, S, out bool refused);
        if (picked is not { } t) { Feedback(world, refused ? RefusedLine : Lines[(int)key]); return took; }
        Act(world, t);
        return took;
    }

    /// <summary>Says a press did nothing: the line, and the feedback sound when the game has it. The sound is looked up
    /// again here, since a module's haks can add or hide it after the setting's edit.</summary>
    private static void Feedback(IInteractWorld world, string line)
    {
        if (InteractKnobs.FeedbackText.V) world.ShowLine(line);
        if (!InteractKnobs.FeedbackSound.V) return;
        S.SoundFound = world.SoundExists(InteractKnobs.Sound.V);
        if (S.SoundFound == true) world.PlaySound(InteractKnobs.Sound.V);
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

    /// <summary>A conversation opened with <paramref name="id"/>, in CX's window or the game's own: it is no longer passed
    /// over. A creature that only speaks a line over its head opens none, so it stays tried.</summary>
    internal static void ConversationOpened(uint id) => S.Tried.Remove(id);

    /// <summary>The player arrived in an area and it finished loading.</summary>
    internal static void AreaLoaded() => S.HeldThroughLoad = S.Held;

    /// <summary>A key came up: an SDL key code from WinMessageHandler.</summary>
    internal static void KeyUp(int code)
    {
        if (code == 0) return;
        if (code == S.Held) S.Held = 0;
        if (code == S.HeldThroughLoad) S.HeldThroughLoad = 0;
    }

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
