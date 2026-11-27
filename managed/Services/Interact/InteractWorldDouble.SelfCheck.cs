using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Chat.Core;
using CX.Services.Dialog;
using CX.Ui;

namespace CX.Services.Interact;

/// <summary>The world Interact's approved examples press keys in: objects laid out around the player, area walls
/// that block sight, the game's own key map, and a record of every default action, line and sound. Each press goes
/// through <see cref="InteractInput.KeyDown"/>, the entry the key handler calls.</summary>
internal sealed class InteractWorldDouble : IInteractWorld
{
    /// <summary>The line Interact shows when nothing qualifies, and the one a pointed target that may not be acted on gets.</summary>
    internal const string Nothing = "Nothing to interact with here.", Refused = "You can't do that with your target.";
    /// <summary>The lines each single-rule key shows when its rule finds nothing.</summary>
    internal const string NoLoot = "Nothing to loot.", NoWayOut = "No way out within reach.", NoPickUp = "Nothing to pick up.",
                          NoDoor = "No door to open.", NoTalk = "No one to talk to.";

    private readonly List<Thing> _things = new();
    private readonly List<(Vector2 A, Vector2 B)> _walls = new();
    private uint _next = 0x100;  // not-an-offset: a made-up object id
    private float _angle;

    internal readonly List<uint> Acts = new();
    internal readonly List<string> Lines = new();
    /// <summary>Every point a walk into an area transition headed for, in order.</summary>
    internal readonly List<Vector3> Walks = new();
    internal readonly List<string> Sounds = new();
    internal readonly HashSet<string> Known = new() { "gui_nowalk", "gui_select" };
    internal readonly Dictionary<int, string> GameKeys = new();
    /// <summary>Creatures whose default action opens a conversation.</summary>
    internal readonly HashSet<uint> Talks = new();
    /// <summary>Creatures whose default action only speaks a line over their head.</summary>
    internal readonly HashSet<uint> Barks = new();
    /// <summary>Every conversation that opened, and whether it opened in the game's own window.</summary>
    internal readonly List<(uint Id, bool GameWindow)> Conversations = new();
    /// <summary>Every lock TabTarget's click-to-lock took after a default action.</summary>
    internal readonly List<uint> LockChanges = new();
    /// <summary>The default actions the server refused, which leave the object as it was.</summary>
    internal readonly List<uint> ServerRefused = new();
    /// <summary>CX's dialog is off: a conversation opens in the game's own window.</summary>
    internal bool GameDialog;
    /// <summary>The player possesses a familiar, and the server does not let associates interact.</summary>
    internal bool PossessedBarred;

    public Vector3 Player { get; set; }
    public IReadOnlyList<Thing> Things => _things;
    public uint Crosshair { get; set; } = TypingSignal.ObjectInvalid;
    public uint Lock { get; set; } = TypingSignal.ObjectInvalid;
    public bool KeysFree { get; set; } = true;

    public bool Sees(Vector3 from, Vector3 to)
    {
        var a = new Vector2(from.X, from.Y); var b = new Vector2(to.X, to.Y);
        foreach (var (c, d) in _walls) if (Crosses(a, b, c, d)) return false;
        return true;
    }

    public bool SoundExists(string resref) => Known.Contains(resref);
    public string? GameKeyAction(int code) => GameKeys.TryGetValue(code, out string? n) ? n : null;
    public void DefaultAction(uint id)
    {
        Acts.Add(id);
        InteractInput.DefaultActionSeen(id);
        if (PossessedBarred) ServerRefused.Add(id);
        if (TabTarget.Enabled.V && InteractInput.ClickLocks(id)) { LockChanges.Add(id); Lock = id; }
    }
    public void WalkTo(uint id, Vector3 point) { Acts.Add(id); Walks.Add(point); }
    public void ShowLine(string text) => Lines.Add(text);
    public void PlaySound(string resref) => Sounds.Add(resref);

    /// <summary>What the first <see cref="Install"/> found, which <see cref="Restore"/> puts back: the world, and each
    /// setting the suites touch as the player stored it, with its raw cx.cfg text.</summary>
    private sealed class SavedState
    {
        public IInteractWorld? World;
        public bool Held;
        public Knob[] Knobs = Array.Empty<Knob>();
        public string[] Stored = Array.Empty<string>();
        public string?[] Raw = Array.Empty<string?>();
    }

    private static readonly SavedState Saved = new();

    /// <summary>The settings the suites change: every Interact one, and the tab-target, camera and dialog ones some set.</summary>
    private static Knob[] Touched()
    {
        var all = new List<Knob>(InteractKnobs.G.Knobs) { TabTarget.Enabled, CameraModes.Chase.ActionKey, MouseLook.Action, DialogKnobs.Enabled };
        return all.ToArray();
    }

    /// <summary>A fresh double installed as the world, every setting the suites touch at its default, the master on and
    /// F bound to the Interact key. The first one keeps the player's settings for <see cref="Restore"/>.</summary>
    internal static InteractWorldDouble Install()
    {
        if (!Saved.Held)
        {
            Saved.Knobs = Touched();
            Saved.Stored = Array.ConvertAll(Saved.Knobs, k => k.FormatStored());
            Saved.Raw = Array.ConvertAll(Saved.Knobs, k => Settings.Raw(k.Key));
        }
        Defaults();
        InteractKnobs.Key.Set("F");
        var w = new InteractWorldDouble();
        IInteractWorld? was = InteractInput.Use(w);
        if (!Saved.Held) { Saved.World = was; Saved.Held = true; }
        return w;
    }

    /// <summary>Every setting the suites touch back as the player stored it, a value already there left unset so it arms
    /// no cx.cfg write, and the world the first <see cref="Install"/> replaced back in. A suite ends here.</summary>
    internal static void Restore()
    {
        for (int i = 0; i < Saved.Knobs.Length; i++)
        {
            Knob k = Saved.Knobs[i];
            if (k.FormatStored() != Saved.Stored[i]) k.Parse(Saved.Stored[i]);
            if (Saved.Raw[i] is { } raw) Settings.Read($"{k.Key}={raw}\n");
            else Settings.Forget(k.Key);
        }
        Saved.Knobs = Array.Empty<Knob>();
        InteractInput.Use(Saved.World);
        Saved.World = null;
        Saved.Held = false;
    }

    /// <summary>Every setting the suites touch at its default and the Interact master on: the state each example
    /// starts from.</summary>
    internal static void Defaults()
    {
        foreach (Knob k in Touched()) k.Reset();
        InteractKnobs.Enabled.Set(true);
    }

    /// <summary>One press of the letter key <paramref name="key"/>, whose SDL code is the lower-case letter, then the
    /// conversation any act opened. True when Interact took the press.</summary>
    internal bool Press(char key, bool repeat = false) => PressCode(char.ToLowerInvariant(key), repeat);

    /// <summary>One press of the SDL code <paramref name="code"/>, or -4 / -5 for a thumb button.</summary>
    internal bool PressCode(int code, bool repeat = false)
    {
        int before = Acts.Count;
        bool took = InteractInput.KeyDown(code, repeat);
        Report(before);
        return took;
    }

    /// <summary>The key <paramref name="key"/> comes up, as WinMessageHandler reports it.</summary>
    internal void Release(char key) => InteractInput.KeyUp(char.ToLowerInvariant(key));

    /// <summary>One press down the client's key path: CX's windows first, then the chord layer Interact shares with the
    /// action camera's toggle. The chord gate's focus and engine reads are left out, as the stub host has no window.
    /// True when a window took the press.</summary>
    internal bool PressUi(int code)
    {
        int before = Acts.Count;
        if (UiInputService.RouteKeyDown((uint)code, 0, false)) return true;
        UiHost.FireChords((uint)code, 0);
        Report(before);
        return false;
    }

    /// <summary>What the engine reports after the default actions from <paramref name="from"/> on: a conversation in
    /// CX's dialog or the game's window, or a line over a creature's head.</summary>
    private void Report(int from)
    {
        for (int i = from; i < Acts.Count; i++)
        {
            uint id = Acts[i];
            if (ServerRefused.Contains(id)) continue;
            if (Talks.Contains(id))
            {
                Conversations.Add((id, GameDialog));
                if (GameDialog) InteractInput.GameConversationOpened(id);
                else InteractInput.ConversationOpened(id);
            }
            else if (Barks.Contains(id)) InteractInput.Barked(id);
        }
    }

    /// <summary>True when the default actions run so far are exactly <paramref name="ids"/>, in order.</summary>
    internal bool Did(params uint[] ids)
    {
        if (Acts.Count != ids.Length) return false;
        for (int i = 0; i < ids.Length; i++) if (Acts[i] != ids[i]) return false;
        return true;
    }

    internal string Seen => $"acts [{string.Join(", ", Acts.ConvertAll(a => a.ToString("x")))}], lines [{string.Join(" | ", Lines)}], sounds [{string.Join(", ", Sounds)}]";

    /// <summary>A mouse click on <paramref name="id"/>: the engine runs its default action, Interact sees it pass.</summary>
    internal void Click(uint id) => InteractInput.DefaultActionSeen(id);

    internal uint Bag(float m, bool trap = false, bool locked = false, bool unfoundTrap = false)
        => Add(new Thing { Kind = ThingKind.BodyBag, Lootable = true, TrapFound = trap, Locked = locked, TrapUnfound = unfoundTrap }, m);
    internal uint Corpse(float m, bool lootable = true, Attitude attitude = Attitude.Hostile)
        => Add(new Thing { Kind = ThingKind.Creature, Dead = true, Lootable = lootable, Attitude = attitude }, m);
    internal uint Item(float m) => Add(new Thing { Kind = ThingKind.Item }, m);
    internal uint Chest(float m, bool locked = false) => Add(new Thing { Kind = ThingKind.Placeable, Useable = true, Locked = locked }, m);
    internal uint Lever(float m) => Add(new Thing { Kind = ThingKind.Placeable, Useable = true }, m);
    internal uint Door(float m, bool locked = false, bool open = false, bool trap = false, bool destroyed = false, bool leadsAway = false)
        => Add(new Thing { Kind = ThingKind.Door, Locked = locked, Open = open, TrapFound = trap, Destroyed = destroyed, LeadsAway = leadsAway }, m);
    internal uint Creature(float m, Attitude attitude = Attitude.Friend, bool talks = true, bool party = false, bool otherPlayer = false, bool barks = false)
    {
        uint id = Add(new Thing { Kind = ThingKind.Creature, Attitude = attitude, Party = party, OtherPlayer = otherPlayer }, m);
        if (talks) Talks.Add(id);
        if (barks) Barks.Add(id);
        return id;
    }

    /// <summary>An area-transition trigger whose outline's nearest edge is <paramref name="edge"/> away and whose
    /// centre is <paramref name="centre"/> away.</summary>
    internal uint Transition(float edge, float? centre = null)
    {
        float c = centre ?? edge + 0.5f, h = c - edge;
        Vector3 d = Dir(), p = new(-d.Y, d.X, 0f), mid = Player + d * c;
        var outline = new[] { mid - d * h - p * h, mid - d * h + p * h, mid + d * h + p * h, mid + d * h - p * h };
        uint id = _next++;
        _things.Add(new Thing { Id = id, Kind = ThingKind.Transition, Pos = mid, Outline = outline, LeadsAway = true });
        return id;
    }

    /// <summary>A short area wall across the line from the player to <paramref name="id"/>, halfway along it.</summary>
    internal void WallBefore(uint id)
    {
        Vector3 to = Of(id).Outline is { } o ? Nearest(o) : Of(id).Pos;
        Vector3 mid = (Player + to) * 0.5f, d = Vector3.Normalize(to - Player), p = new(-d.Y, d.X, 0f);
        _walls.Add((new Vector2(mid.X - p.X, mid.Y - p.Y), new Vector2(mid.X + p.X, mid.Y + p.Y)));
    }

    /// <summary>An area wall from <paramref name="a"/> to <paramref name="b"/>.</summary>
    internal void Wall(Vector3 a, Vector3 b) => _walls.Add((new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)));

    /// <summary>A closed door on the line from the player to <paramref name="id"/>, <paramref name="m"/> away.</summary>
    internal uint DoorBefore(uint id, float m)
    {
        Vector3 d = Vector3.Normalize(Of(id).Pos - Player);
        uint door = _next++;
        _things.Add(new Thing { Id = door, Kind = ThingKind.Door, Pos = Player + d * m });
        return door;
    }

    internal Thing Of(uint id) => _things[IndexOf(id)];
    internal void Change(uint id, Func<Thing, Thing> to) { int i = IndexOf(id); _things[i] = to(_things[i]); }
    internal void Remove(uint id) => _things.RemoveAt(IndexOf(id));
    /// <summary>Puts <paramref name="t"/> back on the list under its own id, as an object that left and came back.</summary>
    internal void Put(Thing t) => _things.Add(t);
    internal void Clear() { _things.Clear(); _walls.Clear(); }

    /// <summary>Moves the player so <paramref name="id"/> is <paramref name="m"/> away, on the far side from it.</summary>
    internal void StepBack(uint id, float m)
    {
        Vector3 at = Of(id).Pos, d = Vector3.Normalize(Player - at);
        Player = at + d * m;
    }

    private uint Add(Thing t, float m)
    {
        t.Id = _next++;
        t.Pos = Player + Dir() * m;
        _things.Add(t);
        return t.Id;
    }

    private Vector3 Dir()
    {
        _angle += 0.9f;
        return new Vector3(MathF.Cos(_angle), MathF.Sin(_angle), 0f);
    }

    private int IndexOf(uint id)
    {
        for (int i = 0; i < _things.Count; i++) if (_things[i].Id == id) return i;
        throw new InvalidOperationException($"no object {id:x}");
    }

    /// <summary>The point of outline <paramref name="o"/> nearest the player.</summary>
    internal Vector3 Nearest(Vector3[] o)
    {
        Vector3 best = o[0];
        for (int i = 0; i < o.Length; i++)
        {
            Vector3 a = o[i], b = o[(i + 1) % o.Length], ab = b - a;
            float t = Math.Clamp(Vector3.Dot(Player - a, ab) / ab.LengthSquared(), 0f, 1f);
            Vector3 q = a + ab * t;
            if (Vector3.Distance(q, Player) < Vector3.Distance(best, Player)) best = q;
        }
        return best;
    }

    private static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Side(Vector2 p, Vector2 q, Vector2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        return Side(a, b, c) * Side(a, b, d) < 0f && Side(c, d, a) * Side(c, d, b) < 0f;
    }
}
