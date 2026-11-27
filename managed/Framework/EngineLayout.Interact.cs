namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for Interact: what a press reads off a nearby placeable, door, trigger, item
/// or corpse, and the game's key table. Each is read alike on both programs off the object's update reader, its radial
/// menu and the engine's default action on it, or for the key rows off CExoInput's event descriptions.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>CNWCPlaceable -&gt; "this placeable is a body bag" (int): the dropped-loot container a dead creature
    /// leaves. The update reader sets it from the server's flag; the bag's owner id is the dword before it.</summary>
    public static readonly int PlaceableBodyBag = Field<int>("CNWCPlaceable", "bodyBag", Expect.Byte01);
    /// <summary>CNWCPlaceable -&gt; locked (int), what the radial tests to offer Unlock and Pick Lock.</summary>
    public static readonly int PlaceableLocked = Field<int>("CNWCPlaceable", "locked", Expect.Byte01);
    /// <summary>CNWCPlaceable -&gt; a trap on it the player has found (int), what the radial tests to offer Disarm.</summary>
    public static readonly int PlaceableTrapShown = Field<int>("CNWCPlaceable", "trapShown", Expect.Byte01);
    /// <summary>CNWCDoor -&gt; locked (int), the door radial's Unlock test.</summary>
    public static readonly int DoorLocked = Field<int>("CNWCDoor", "locked", Expect.Byte01);
    /// <summary>CNWCDoor -&gt; a trap on it the player has found (int), the door radial's Disarm test.</summary>
    public static readonly int DoorTrapShown = Field<int>("CNWCDoor", "trapShown", Expect.Byte01);
    /// <summary>Virtual slot of <c>CGameObject::AsNWCDoor()</c>, the cast the default action makes on type byte 10: the
    /// base answers null and <c>CNWCDoor</c>'s override is an identity <c>return this</c>. Linux <c>vtbl+0x30</c>,
    /// Windows <c>vtbl+0x28</c>.</summary>
    public static readonly int GameObjAsNWCDoorSlot = Field("CGameObject", "vtbl.AsNWCDoor", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary>CNWCCreature -&gt; lootable (int): the default action opens a dead creature's inventory when it is 1
    /// and <see cref="CreatureAiFlags"/> is 0.</summary>
    public static readonly int CreatureLootable = Field<int>("CNWCCreature", "lootable", Expect.Byte01);

    /// <summary>CNWCDoor -&gt; "this door leads to another area" (int), what SetIsAreaTransition writes. A click on a
    /// closed one opens it; a click on an open one walks the player through it.</summary>
    public static readonly int DoorAreaTransition = Field<int>("CNWCDoor", "areaTransition", Expect.Byte01);
    /// <summary>CNWCDoor -&gt; the door's state (ONE byte), what GetState returns: 0 closed, 1 and
    /// 2 open one way or the other, 3 destroyed, which the door add also writes for a generic door with no visible model.</summary>
    public static readonly int DoorState = Field<byte>("CNWCDoor", "state");
    /// <summary>CNWCTrigger -&gt; "this trigger is a trap" (int), set by the trigger add from the server's flag.</summary>
    public static readonly int TriggerIsTrap = Field<int>("CNWCTrigger", "isTrap", Expect.Byte01);
    /// <summary>CNWCTrigger -&gt; "this trigger leads to another area" (int); the trigger add sets it on a trigger that is
    /// not a trap.</summary>
    public static readonly int TriggerAreaTransition = Field<int>("CNWCTrigger", "areaTransition", Expect.Byte01);
    /// <summary>CNWCTrigger -&gt; how many points its outline holds (int; the message carries a byte).</summary>
    public static readonly int TriggerVertexCount = Field<int>("CNWCTrigger", "vertexCount");
    /// <summary>CNWCTrigger -&gt; its outline, <see cref="TriggerVertexCount"/> world-space points of three floats each
    /// (<c>Vec3*</c>), every height raised 0.05 over the ground.</summary>
    public static readonly int TriggerVertices = Field<IntPtr>("CNWCTrigger", "vertices");
    /// <summary>Virtual slot of <c>CGameObject::AsNWCTrigger()</c>: the base answers null and <c>CNWCTrigger</c>'s
    /// override is an identity <c>return this</c>. Linux <c>vtbl+0x80</c>, Windows <c>vtbl+0x78</c>.</summary>
    public static readonly int GameObjAsNWCTriggerSlot = Field("CGameObject", "vtbl.AsNWCTrigger", Expect.VtblSlot, sig: "IntPtr()");

    /// <summary>CNWCItem -&gt; the id of the creature wearing it, 0x7f000000 when none. An item's default action on a worn
    /// item runs the wearer's, which attacks a hostile one.</summary>
    public static readonly int ItemEquippedBy = Field<uint>("CNWCItem", "equippedBy");

    /// <summary>CExoInput -&gt; its CExoInputInternal, what every CExoInput call forwards to.</summary>
    public static readonly int ExoInputInternal = Field<CX.Engine.CExoInputInternal>("CExoInput", "internal");
    /// <summary>CExoInputInternal -&gt; its input event lists, an array of one CExoLinkedListInternal* per device, each
    /// holding the CExoInputEventDesc records SetEventDescriptions adds for that device.</summary>
    public static readonly int ExoInputDescLists = Field<CX.Engine.CExoInputDescLists>("CExoInputInternal", "descLists");
    /// <summary>The event list array -&gt; device 0's, the keyboard's: every description in it is a key's.</summary>
    public static readonly int DescListsKeyboard = Field<CX.Engine.CExoLinkedListInternal>("CExoInputDescLists", "keyboard");
    /// <summary>CExoInputInternal -&gt; the engine's key table: entry i is the SDL scancode of engine key i. AddEvent
    /// looks a key-down's scancode up in entries 10 to 0x69, the keyboard's keys.</summary>
    public static readonly int ExoInputKeyScancodes = Field<int>("CExoInputInternal", "keyScancodes", count: 0x6a);
    /// <summary>CExoInputEventDesc -&gt; the action it runs (uint): 37 to 108 are keymap.2da's rows Action37 to Action108.</summary>
    public static readonly int InputDescAction = Field<uint>("CExoInputEventDesc", "action");
    /// <summary>CExoInputEventDesc -&gt; its key (uint): the engine key in the low byte, the modifier above it (0 none).</summary>
    public static readonly int InputDescKey = Field<uint>("CExoInputEventDesc", "key");
    /// <summary>The 2DA table -&gt; keymap.2da, the table SetEventDescriptions and the Key Mapping panel read a
    /// remappable action's default key and its name strref from, the same hop on both builds.</summary>
    public static readonly int TwoDimKeymap = Field<IntPtr>("CTwoDimArrays", "keymap");
}
