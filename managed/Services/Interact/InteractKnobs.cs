using CX.Framework;

namespace CX.Services.Interact;

/// <summary>The <c>interact</c> group in Controls, beside Hotkeys (ADR 0046, ADR 0051): the master, one press-to-bind
/// row per key, which rules the Interact key runs, and what a press says when nothing qualifies.</summary>
internal static class InteractKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<string> Key, TransitionKey, LootKey, PickUpKey, DoorKey, TalkKey;
    internal static readonly Knob<bool> Transitions, Loot, PickUp, Doors, Talk;
    internal static readonly Knob<bool> FeedbackText, FeedbackSound;
    internal static readonly Knob<string> Sound;

    /// <summary>The reaches as the help text writes them, from the rules' own constants.</summary>
    private static readonly string Near = Metres(InteractCore.TransitionReach), Far = Metres(InteractCore.Reach);

    private static string Metres(float m) => m.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "m";

    static InteractKnobs()
    {
        G = KnobGroup.Declare("interact", "Controls", "Interact", order: 545, master: "Enable the Interact keys",
            masterHelp: "One key acts on the nearest thing worth acting on: an exit, loot, an item, a door or someone to "
                      + "talk to, as clicking it would. Off, every Interact key reaches the game.");
        Enabled = G.Master!;
        G.Section("Keys");
        Key = G.KeyBind("key", "Interact", help: "Runs every rule below in order and acts on the first thing that qualifies.",
                       changed: InteractInput.KeysChanged);
        TransitionKey = G.KeyBind("transitionkey", "Transition", help: $"Takes an area transition within {Near}.",
                                  changed: InteractInput.KeysChanged);
        LootKey = G.KeyBind("lootkey", "Loot", help: $"Opens the nearest corpse or body bag within {Far}.", changed: InteractInput.KeysChanged);
        PickUpKey = G.KeyBind("pickupkey", "Pick up", help: $"Picks up the nearest item on the ground within {Far}.",
                              changed: InteractInput.KeysChanged);
        DoorKey = G.KeyBind("doorkey", "Door", help: $"Opens the nearest closed door within {Far}.", changed: InteractInput.KeysChanged);
        TalkKey = G.KeyBind("talkkey", "Talk", help: $"Talks to the nearest friendly or neutral creature within {Far}.",
                           changed: InteractInput.KeysChanged);
        G.Section("The Interact key runs");
        Transitions = G.Toggle("transition", "Area transitions", true, help: $"An exit within {Near} comes before everything else.");
        Loot = G.Toggle("loot", "Loot", true, help: $"A corpse or body bag you can loot, within {Far}.");
        PickUp = G.Toggle("pickup", "Pick up", true, help: $"An item on the ground, within {Far}.");
        Doors = G.Toggle("door", "Doors", true, help: $"A closed door, within {Far}.");
        Talk = G.Toggle("talk", "Talk", true, help: $"A friendly or neutral creature outside your party, within {Far}.");
        G.Section("When nothing qualifies");
        FeedbackText = G.Toggle("feedbacktext", "Show a line", true, help: "A short line at the top of the screen says why nothing happened.");
        FeedbackSound = G.Toggle("feedbacksound", "Play a sound", true, help: "A quiet sound plays.");
        Sound = G.Text("sound", "Sound", "gui_nowalk", help: "The sound's resref. The default is the one the game plays when it refuses a move.",
                       changed: InteractInput.SoundChanged);
        Sound.Note = InteractInput.SoundNote;
    }
}
