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
    public static readonly int GuiViewportRecord = Field<IntPtr>("CGuiViewport", "record");
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

    public static readonly int AppearBodyParts = Field<ushort>("CCAppearanceInfo", "bodyParts", count: 19);
    public static readonly int AppearColors = Field<byte>("CCAppearanceInfo", "colors", count: 6);
    public static readonly int AppearPartColors = Field<byte>("CCAppearanceInfo", "partColors", count: 114);
    /// <summary>The helmet's game-object id (the invalid id when none), the flag that makes the body use <c>armorParts</c> (zero uses
    /// <c>bodyParts</c>), and the cloak's model and six colours (as words).</summary>
    public static readonly int AppearArmorWorn = Field<int>("CCAppearanceInfo", "armorWorn");
    public static readonly int AppearCloakColors = Field<ushort>("CCAppearanceInfo", "cloakColors", count: 6);
    public static readonly int AppearCloakModel = Field<ushort>("CCAppearanceInfo", "cloakModel");

    /// <summary>The 2DA table's <c>visualeffects.2da</c> (+0x280), <c>cloakmodel.2da</c> (+0x1d8) and item visual effects table (+0x260),
    /// the same two hops off <c>g_pRules</c> on both builds: <c>CNWCVisualEffectOnObject::LoadVisualEffect</c> (Linux nm @0x8186b0, Windows
    /// rva 0x96cf10), <c>CNWCMessage::CreateItem</c> and <c>CNWCItem::LoadVisualEffect</c> (Windows rva 0x83e040).</summary>
    public static readonly int TwoDimVisualEffects = Field<IntPtr>("CTwoDimArrays", "visualEffects");
    /// <inheritdoc cref="TwoDimVisualEffects"/>
    public static readonly int TwoDimCloakModel = Field<IntPtr>("CTwoDimArrays", "cloakModel");
    /// <inheritdoc cref="TwoDimVisualEffects"/>
    public static readonly int TwoDimItemVisualEffects = Field<IntPtr>("CTwoDimArrays", "itemVisualEffects");

    /// <summary>A <c>CNWBaseItem</c>'s model type (0 simple, 1 layered, 2 composite, 3 armour) and its <c>ItemClass</c> resref prefix (16
    /// bytes, zero-ended): the two things <c>CNWBaseItem::GetModelResRef</c> builds a model name from, at the same offsets on both builds
    /// (Linux nm @0x11a92a0, Windows rva 0x29dd90).</summary>
    public static readonly int BaseItemModelType = Field<byte>("CNWBaseItem", "modelType");
    /// <inheritdoc cref="BaseItemModelType"/>
    public static readonly int BaseItemClass = Field<byte>("CNWBaseItem", "itemClass", count: 16);

    /// <summary>A <c>C2DA</c>'s row count (+0x50 on both builds), the bound <c>GetINTEntry</c> and <c>GetCExoStringEntry</c> check a row against.</summary>
    public static readonly int TwoDaRows = Field<int>("C2DA", "rows");

    /// <summary>CNWCObject -> its list of visual effects (<c>CExoLinkedListInternal*</c>, never null), the one
    /// <c>CNWCObject::AddVisualEffect</c> appends to and <c>KillAllVisualEffects</c> drains. Same offset on both builds
    /// (Linux nm @0x7e2f20, Windows rva 0x763a50).</summary>
    public static readonly int ObjEffects = Field<CX.Engine.CExoLinkedListInternal>("CNWCObject", "effects");
    /// <summary>The engine's list: first node at +0, node count at +0x10. A node holds previous at +0, next at +8 and its object at +0x10
    /// (<c>AddTail</c> on both builds).</summary>
    public static readonly int ListHead = Field<CX.Engine.CExoLinkedListNode>("CExoLinkedListInternal", "head");
    /// <inheritdoc cref="ListHead"/>
    public static readonly int NodeNext = Field<CX.Engine.CExoLinkedListNode>("CExoLinkedListNode", "next");
    /// <inheritdoc cref="ListHead"/>
    public static readonly int NodeData = Field<CX.Engine.CNWCVisualEffectOnObject>("CExoLinkedListNode", "data");
    /// <summary>CNWCVisualEffectOnObject, read off <c>LoadVisualEffect</c>, <c>ShowVisualEffect</c> and <c>HideVisualEffect</c> (Linux nm @0x8186b0,
    /// @0x81aaa0, @0x816f60; Windows rva 0x96cf10): the <c>visualeffects.2da</c> row (ushort), the flag <c>Show</c> sets and <c>Hide</c> clears (int),
    /// the flag that is 1 for an effect that lasts and 0 for a one-shot (byte), and the id of the object a beam comes from (the invalid id when none).</summary>
    public static readonly int EffectRow = Field<ushort>("CNWCVisualEffectOnObject", "row");
    /// <inheritdoc cref="EffectRow"/>
    public static readonly int EffectShown = Field<int>("CNWCVisualEffectOnObject", "shown");
    /// <inheritdoc cref="EffectRow"/>
    public static readonly int EffectLasting = Field<byte>("CNWCVisualEffectOnObject", "lasting");
    /// <inheritdoc cref="EffectRow"/>
    public static readonly int EffectSource = Field<uint>("CNWCVisualEffectOnObject", "source");
    /// <summary>The effect's <c>CExoString</c> that names the node the body part chose (<c>handconjure</c>, <c>impact</c>, <c>monsterN</c> or <c>root</c>),
    /// as the pointer to its zero-ended text. Both builds keep it at the same place; only an effect with no source writes the part's own name there.</summary>
    public static readonly int EffectNode = Field<IntPtr>("CNWCVisualEffectOnObject", "node");
}
