namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for Interact: what a press reads off a nearby placeable,
/// door or corpse to tell a safe container from a trapped or locked one.</summary>
public static unsafe partial class EngineLayout
{
    // Read 2026-11-27 off the same functions on both programs:
    // CNWCMessage::HandleServerToPlayerPlaceableUpdate_Add (Linux nm @0x7b5280, Windows rva 0x7a7800),
    // CGuiPlaceableMenu::ActivateRadialMenu (Linux nm @0x7ecfb0, Windows rva 0x9b19d0),
    // CGuiDoorMenu::ActivateRadialMenu (Linux nm @0x756d40, Windows rva 0x9b00e0) and
    // CClientExoAppInternal::PerformDefaultActionOnCreature (Linux nm @0x65e220, Windows rva 0x6efa40).

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

    // Read 2026-11-27 off both programs: CNWCMessage::HandleServerToPlayerTriggerUpdate_Add (Linux nm @0x7b5fb0,
    // Windows rva 0x7b1670), CNWCDoor::SetIsAreaTransition (Linux nm @0x754520, Windows rva 0x86e640),
    // CNWCDoor::GetState (Linux nm @0x754510, Windows rva 0x86df30) and the slot calls in
    // CClientExoAppInternal::PerformLButtonDownAction (Linux nm @0x6641f0, Windows rva 0x6f02c0). The state values
    // were read the same day off CNWCMessage::HandleServerToPlayerDoorUpdate_Add (Linux nm @0x7b4790, Windows rva
    // 0x796dd0), CNWCDoor::AnimationUpdate (Linux nm @0x7545d0) and CClientExoAppInternal::PerformDefaultActionOnDoor
    // (Linux nm @0x65f100; inlined in PerformDefaultActionOnObject, Windows rva 0x6efec0).

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

    // Read 2026-11-27 off both programs: CClientExoAppInternal::PerformDefaultActionOnItem (Linux nm @0x65e500,
    // inlined on Windows in PerformDefaultActionOnObject rva 0x6efec0), CNWCItem::SetEquippedBy (Linux nm @0x7cb850)
    // and CNWCItem::GetEquippedBy (Linux nm @0x7cb880, Windows rva 0x83d7e0).

    /// <summary>CNWCItem -&gt; the id of the creature wearing it, 0x7f000000 when none. An item's default action on a worn
    /// item runs the wearer's, which attacks a hostile one.</summary>
    public static readonly int ItemEquippedBy = Field<uint>("CNWCItem", "equippedBy");
}
