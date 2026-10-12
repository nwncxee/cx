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
    public static readonly int CreatureDeleteSlot = Field("CNWCCreature", "vtbl.Delete", Expect.VtblSlot);
    /// <summary>The <c>CAurCamera</c> interface slots a free view's camera is set up through (<c>CameraSlots</c>' numbering;
    /// Windows is one lower from slot 2). The by-value ones are called per OS by <c>NuiModelDoll</c>.</summary>
    public static readonly int CameraSetPositionSlot = Field("CAurCamera", "vtbl.SetPosition", Expect.VtblSlot);
    public static readonly int CameraSetOrientationSlot = Field("CAurCamera", "vtbl.SetOrientation", Expect.VtblSlot);
    public static readonly int CameraSetClipDistSlot = Field("CAurCamera", "vtbl.SetClipDist", Expect.VtblSlot, sig: "void(float near, float far)");
    public static readonly int CameraSetViewAngleSlot = Field("CAurCamera", "vtbl.SetViewAngle", Expect.VtblSlot, sig: "void(float angle)");
    /// <summary>NkImage -> the GL texture name <c>GetImage</c> hands nuklear, and the frame count that gates it.</summary>
    public static readonly int NkImageGlTexture = Field<uint>("NkImage", "glTexture");
}
