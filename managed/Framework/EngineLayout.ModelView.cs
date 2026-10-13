using System;

namespace CX.Framework;

/// <summary>The rows of <see cref="EngineLayout"/> the model view reads: a throwaway creature, the viewport it sits in and
/// that viewport's camera.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>The model view's creature and viewport: <c>operator new</c> sizes, the deleting destructor slot, and the
    /// viewport's scene-view record (<c>CAurScene*</c> at +0, <c>CAurCamera*</c> at +8, enabled byte at +0x10).</summary>
    public static readonly int SizeOfCreature = Field("CNWCCreature", "sizeof");
    public static readonly int SizeOfGuiViewport = Field("CGuiViewport", "sizeof");
    public static readonly int GuiViewportRecord = Field("CGuiViewport", "record");
    public static readonly int GobSetColorShiftingSlot = Field("Gob", "vtbl.SetColorShifting", Expect.VtblSlot);
    public static readonly int SizeOfAppearanceInfo = Field("CCAppearanceInfo", "sizeof");
    /// <summary>The object id of the item whose model is the creature's helmet (<c>0x7f000000</c> = none), read by <c>CreateHead</c>: the
    /// engine looks the item up by this id and attaches that item's own model to the head, so the id must name an item the doll owns.</summary>
    public static readonly int AppearanceHelmId = Field<uint>("CCAppearanceInfo", "helmId");
    /// <summary><c>CAurObject::PlayAnimation(char const* name, float speed, int flags, float fade)</c> -- plays a named animation on a model,
    /// 1 when it has one by that name. Flag bit 4 loops it. Itanium has two destructor slots before it, MSVC one.</summary>
    public static readonly int GobPlayAnimationSlot = Field("Gob", "vtbl.PlayAnimation", Expect.VtblSlot, sig: "int(IntPtr name, float speed, int flags, float fade)");
    public static readonly int CreatureDeleteSlot = Field("CNWCCreature", "vtbl.Delete", Expect.VtblSlot);
    /// <summary>The <c>CAurCamera</c> interface slots a free view's camera is set up through (<c>CameraSlots</c>' numbering;
    /// Windows is one lower from slot 2). The by-value ones are called per OS by <c>NuiModelDoll</c>.</summary>
    public static readonly int CameraSetPositionSlot = Field("CAurCamera", "vtbl.SetPosition", Expect.VtblSlot);
    public static readonly int CameraSetOrientationSlot = Field("CAurCamera", "vtbl.SetOrientation", Expect.VtblSlot);
    public static readonly int CameraSetClipDistSlot = Field("CAurCamera", "vtbl.SetClipDist", Expect.VtblSlot, sig: "void(float near, float far)");
    public static readonly int CameraSetViewAngleSlot = Field("CAurCamera", "vtbl.SetViewAngle", Expect.VtblSlot, sig: "void(float angle)");
    /// <summary>NkImage -> the GL texture name <c>GetImage</c> hands nuklear, and the frame count that gates it.</summary>
    public static readonly int NkImageGlTexture = Field<uint>("NkImage", "glTexture");

    /// <summary>The creature's hand-slot item ids (right 0x10, left 0x20 of the <c>INVENTORY_SLOT_*</c> masks), as
    /// <c>EquipToInventorySlot</c> stores them.</summary>
    public static readonly int CreatureRightHandItem = Field<uint>("CNWCCreature", "rightHandItem");
    public static readonly int CreatureLeftHandItem = Field<uint>("CNWCCreature", "leftHandItem");
    /// <summary>CNWCItem: the 19 model parts (a simple item's at [0], a composite weapon's three at [0..2]), the six colours and
    /// the gender byte, as <c>CreateItem</c> stores what the appearance update sent.</summary>
    public static readonly int ItemModels = Field<ushort>("CNWCItem", "models", count: 19);
    public static readonly int ItemColors = Field<byte>("CNWCItem", "colors", count: 6);
    public static readonly int ItemGender = Field<byte>("CNWCItem", "gender");

    /// <summary>CCAppearanceInfo, the record a creature's body is built from: two sets of 19 model parts, the one at 0 read while
    /// <c>armorWorn</c> is nonzero and the one at 0x26 while it is zero, the six armour colours and a per-part override for each
    /// (0xff keeps the base).</summary>
    public static readonly int AppearArmorParts = Field<ushort>("CCAppearanceInfo", "armorParts", count: 19);
    public static readonly int AppearBodyParts = Field<ushort>("CCAppearanceInfo", "bodyParts", count: 19);
    public static readonly int AppearColors = Field<byte>("CCAppearanceInfo", "colors", count: 6);
    public static readonly int AppearPartColors = Field<byte>("CCAppearanceInfo", "partColors", count: 114);
    /// <summary>The helmet's game-object id (the invalid id when none), the flag that makes the body use <c>armorParts</c> (zero uses
    /// <c>bodyParts</c>), and the cloak's model and six colours (as words).</summary>
    public static readonly int AppearArmorWorn = Field<int>("CCAppearanceInfo", "armorWorn");
    public static readonly int AppearCloakColors = Field<ushort>("CCAppearanceInfo", "cloakColors", count: 6);
    public static readonly int AppearCloakModel = Field<ushort>("CCAppearanceInfo", "cloakModel");
}
