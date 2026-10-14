using CX.Framework;

namespace CX.Services.Sound;

/// <summary>The <c>sound</c> group. Every row changes playback only -- no audio file is touched, and a vanilla player
/// hears what they always did. The panel carries the output, the speaker test, walls, reverb and the per-kind faders;
/// the repetition and loudness repairs of the stock data, and every simulation internal, are Chat-only keys (ADR 0030).
/// Touched from <see cref="SoundService.Initialize"/>, so the group declares whether or not the service arms.</summary>
internal static class SoundKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Crossfade, Pathing, PitchVariance, RandomStart, Fix3d, Compressor, Limiter, PlaceLoops;
    internal static readonly Knob<int> Output, Layout, Occlusion, Reverb, Curve, Rays, Bounces, Order, DupCap, Voices3d, Voices2d;
    internal static readonly Knob<float> ReverbDb, RainDb, Duration, Rolloff, Taper, Air, Detune, DriftDb, Normalize, ShelfDb, DuckDb, Master;
    internal static readonly Knob<float> Ambient, Footsteps, Combat, Spells, Voices, Other;
    internal static readonly Knob<string> Resampler;

    /// <summary>Output: what the first bus fixes (Restart).</summary>
    internal const int OutAuto = 0, OutHeadphones = 1, OutSpeakers = 2;

    /// <summary>Each category's loudness target (gated RMS, dBFS) for <see cref="Normalize"/>: ambient beds sit well
    /// under the one-shots that happen in front of them. In <see cref="SoundCore.Config.CatGainDb"/> order.
    /// ponytail: constants, not knobs -- the per-category volume rows are what a player moves.</summary>
    internal static readonly float[] Targets = { -30f, -26f, -20f, -22f, -20f, -22f };

    static SoundKnobs()
    {
        G = KnobGroup.Declare("sound", "Audio", "Sound", master: "Enable CX sound (Steam Audio)",
            masterHelp: "Positional sounds are rendered by Steam Audio: heard from their real direction, muffled by walls, "
                      + "echoing off the area's own geometry. Off, every sound plays as in the stock game.",
            masterChanged: SoundService.Master, order: 400);
        Enabled = G.Master!;
        Output = G.Dropdown("output", "Output", new[] { OutAuto, OutHeadphones, OutSpeakers }, OutAuto,
            new[] { "Automatic", "Headphones (HRTF)", "Speakers" }, effect: Effect.Restart,
            help: "Headphones place every sound around your head (HRTF). Speakers pan across your speaker layout, stereo "
                + "to 7.1. Automatic asks the system whether headphones are connected.");
        Layout = G.Dropdown("layout", "Speaker layout", new[] { 0, 2, 4, 6, 8 }, 0,
            new[] { "Automatic", "Stereo", "Quad", "5.1", "7.1" }, effect: Effect.Restart,
            help: "The speakers to pan across when Output is Speakers. Automatic takes what the system reports; pick one "
                + "only if the system reports it wrong. Play the speaker test after changing it.");
        G.Button("test", "Play speaker test", SoundService.TestTone,
            help: "A burst of noise from ahead, then right, behind and left of your character's view, one second each. "
                + "If one comes from the wrong place, your output or speaker layout is set wrong.");
        G.Section("Space");
        Occlusion = G.Dropdown("occlusion", "Walls", new[] { 0, 1, 2 }, 2,
            new[] { "No effect", "Block sound", "Muffle sound" }, changed: SoundService.Push,
            help: "What a wall between you and a sound does. Muffle lets some of it through, dull, as a real wall does.");
        Reverb = G.Dropdown("reverb", "Reverb", new[] { 0, 1, 2, 3 }, 1,
            new[] { "Off", "Room", "Every sound", "Area preset" }, changed: SoundService.Push,
            help: "Echo from the area's walls, floors and ceilings. Room simulates it once around you; Every sound also "
                + "gives the four nearest sounds their own echoes, which costs noticeably more CPU. An area whose builder "
                + "chose its reverb type plays that one instead of either. Area preset plays the area's reverb type "
                + "everywhere, guessing one where the builder chose none.");
        ReverbDb = G.Slider("reverblevel", "Reverb level", -24f, 6f, 1f, 0f, "dB", Tier.Advanced, changed: SoundService.Push,
            help: "How loud the echoes are against the sound itself.");
        RainDb = G.Slider("rainreverb", "Reverb in rain", -24f, 0f, 1f, -9f, "dB", Tier.ChatOnly,
            help: "Rain soaks up echoes: the reverb drops by this much in a full downpour, less in light rain.");
        Pathing = G.Toggle("pathing", "Sound around corners", false, Tier.Advanced, changed: SoundService.Push,
            help: "A sound behind a wall still reaches you through the nearest opening, from that direction. Each area is "
                + "analysed in the background when you enter it, which takes a CPU core for a while in large areas.");
        G.Section("Distance");
        Curve = G.Dropdown("curve", "Distance falloff", new[] { 1, 0 }, 1,
            new[] { "Natural", "Stock" }, tier: Tier.ChatOnly, changed: SoundService.Push,
            help: "Natural keeps a sound at full volume inside the radius its author set, then halves it with every "
                + "doubling of distance. Stock fades everything in a straight line from one metre.");
        Air = G.Slider("air", "Distance dulling", 0f, 4f, 0.25f, 2f, "x", Tier.ChatOnly, changed: SoundService.Push,
            help: "Far sounds lose their treble, as they do through air. 0 turns it off.");
        // ponytail: off by default -- nothing tells a deliberate area-wide bed (e.g. rain: non-positional, volume 127,
        // the toolset's default 1/10 m) from a misplaced one; a per-area or per-resref list is the upgrade
        PlaceLoops = G.Toggle("placeloops", "Place area-wide loops", false, Tier.ChatOnly,
            help: "A looping placed sound its builder left non-positional plays everywhere in the area at one volume. "
                + "On, it plays from where it was placed instead, fading over the distances the builder set. That fixes a "
                + "misplaced loop that is heard through walls, and silences a deliberate area-wide one (rain) past a few metres.");
        Fix3d = G.Toggle("level3d", "Positional level fix", true, Tier.ChatOnly, changed: SoundService.Push,
            help: "The stock game plays positional sounds 6 dB quieter than interface sounds. This evens them out.");
        G.Section("Repetition");
        PitchVariance = G.Toggle("pitchvariance", "Pitch variation", true, Tier.ChatOnly, changed: SoundService.Push,
            help: "Footsteps, hits and creature sounds vary in pitch from play to play, as the game's data asks. The stock "
                + "game computes the variation and never applies it.");
        RandomStart = G.Toggle("randomstart", "Stagger loops", true, Tier.ChatOnly, changed: SoundService.Push,
            help: "Looping sounds start at a random point, so two copies of the same loop do not play in lockstep.");
        Detune = G.Slider("detune", "Loop detune", 0f, 0.05f, 0.005f, 0.03f, tier: Tier.ChatOnly, changed: SoundService.Push,
            help: "Each looping sound plays slightly sharp or flat, so copies of one loop do not reinforce each other.");
        DriftDb = G.Slider("drift", "Loop drift", 0f, 3f, 0.25f, 1.5f, "dB", Tier.ChatOnly, changed: SoundService.Push,
            help: "Looping sounds drift slowly in level and brightness instead of repeating exactly.");
        DupCap = G.Slider("copies", "Same sound, at most", 0, 8, 1, 2, tier: Tier.Advanced, changed: SoundService.Push,
            help: "How many copies of one sound play at once, nearest first. 0 plays every copy.");
        G.Section("Mix");
        Normalize = G.Slider("normalize", "Even out loudness", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced, changed: SoundService.Push,
            help: "Moves each sound part of the way to a sensible level for its kind, so a hot recording does not stab and "
                + "an ambient bed sits under the action. 0 leaves every sound as recorded.");
        ShelfDb = G.Slider("ambienttreble", "Ambient treble", -12f, 0f, 1f, -3f, "dB", Tier.ChatOnly, changed: SoundService.Push,
            help: "Takes the hiss and crackle off looping ambient sounds, fires especially.");
        DuckDb = G.Slider("duck", "Ambience ducking", -12f, 0f, 1f, -4f, "dB", Tier.ChatOnly, changed: SoundService.Push,
            help: "Positional ambient loops step back while fighting or speech is audible, and with the area music as "
                + "loud as it plays.");
        Crossfade = G.Toggle("crossfade", "Crossfade music and ambience", true, Tier.Advanced,
            help: "A new music track or area ambience fades in while the old one fades out. The stock game fades the old "
                + "one out completely first, then starts the new one from silence.");
        Ambient = G.Slider("ambient", "Ambience", -24f, 12f, 1f, 0f, "dB", changed: SoundService.Push,
            help: "Looping positional sounds: fires, water, crowds, placed ambience.");
        Footsteps = G.Slider("footsteps", "Footsteps", -24f, 12f, 1f, 0f, "dB", changed: SoundService.Push,
            help: "Every creature's footsteps.");
        Combat = G.Slider("combat", "Combat", -24f, 12f, 1f, 0f, "dB", changed: SoundService.Push,
            help: "Weapon hits, swings and projectiles.");
        Spells = G.Slider("spells", "Spells", -24f, 12f, 1f, 0f, "dB", changed: SoundService.Push,
            help: "Spell effects in the world.");
        Voices = G.Slider("voices", "Creature voices", -24f, 12f, 1f, 0f, "dB", changed: SoundService.Push,
            help: "Creature cries, battle calls and positional speech.");
        Other = G.Slider("other", "Everything else", -24f, 12f, 1f, 0f, "dB", changed: SoundService.Push,
            help: "Positional sounds that fit none of the above: scripted sounds, doors, placeables.");
        Master = G.Slider("master", "Master volume", 0f, 1.5f, 0.05f, 1f, changed: SoundService.PushDevice,
            help: "Everything the game plays, music and interface included. The stock game has no master volume.");
        G.Section("Simulation");
        Compressor = G.Toggle("compressor", "Soften peaks", true, Tier.ChatOnly, changed: SoundService.Push,
            help: "Gently compresses the positional mix so a loud moment does not jump out.");
        Limiter = G.Toggle("limiter", "Output limiter", true, Tier.ChatOnly, changed: SoundService.PushDevice,
            help: "OpenAL's final limiter, which keeps the whole mix from clipping.");
        Resampler = G.Dropdown("resampler", "Resampler", new[] { "", "23rd order Sinc", "47th order Sinc" }, "23rd order Sinc",
            new[] { "OpenAL default", "Sinc, 24 point", "Sinc, 48 point" }, tier: Tier.ChatOnly, changed: SoundService.PushResampler,
            help: "How music, interface and other non-positional sounds are converted to your output rate.");
        Rays = G.Slider("rays", "Reverb rays", 512, 8192, 512, 1024, tier: Tier.ChatOnly, changed: SoundService.Push,
            help: "Rays traced to find echoes. More is smoother and costs CPU.");
        Bounces = G.Slider("bounces", "Reverb bounces", 1, 32, 1, 16, tier: Tier.ChatOnly, changed: SoundService.Push,
            help: "How many times each ray reflects: the echo lasts only as long as its bounces carry it, so few sound like a small room anywhere. A cave triples it and a dungeon doubles it. More costs CPU on the sound simulation thread.");
        Duration = G.Slider("duration", "Reverb length", 0.5f, 2f, 0.1f, 1f, "s", Tier.ChatOnly, changed: SoundService.Push,
            help: "The longest echo tail simulated.");
        Order = G.Slider("order", "Reverb directionality", 1, 2, 1, 1, tier: Tier.ChatOnly, effect: Effect.Restart,
            help: "Ambisonic order of the echoes: 2 places them more precisely and costs more CPU.");
        Rolloff = G.Slider("rolloff", "Falloff strength", 0.25f, 2f, 0.05f, 1f, "x", Tier.ChatOnly, changed: SoundService.Push,
            help: "How fast Natural falloff loses level past a sound's full-volume radius.");
        Taper = G.Slider("fade", "Fade-out start", 0.5f, 1f, 0.05f, 0.75f, "x far", Tier.ChatOnly, changed: SoundService.Push,
            help: "Where, as a fraction of a sound's audible range, it starts fading to silence so it never cuts off.");
        Voices3d = G.Slider("voices3d", "Positional voices", 16, 64, 8, 32, tier: Tier.ChatOnly, effect: Effect.Restart,
            help: "How many positional sounds may play at once. The stock game allows 16.");
        Voices2d = G.Slider("voices2d", "Interface voices", 16, 64, 8, 24, tier: Tier.ChatOnly, effect: Effect.Restart,
            help: "How many non-positional sounds may play at once. The stock game allows 16.");
    }
}
