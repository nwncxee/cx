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
}
