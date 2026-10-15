using System;
using System.Runtime.CompilerServices;
using CX.Framework;

namespace CX.Engine;

/// <summary>A <c>CExoArrayList</c> head as it sits in a gob or scene: array pointer, then count and capacity as two
/// ints. Every list the seam shells carry (back-references, callbacks, attachments) has this shape.</summary>
public readonly unsafe struct ExoArray(IntPtr ptr)
{
    /// <summary>The list head.</summary>
    public readonly IntPtr Ptr = ptr;
    /// <summary>The element array, or zero.</summary>
    public ref IntPtr Data => ref ((ExoArrayRec*)Ptr)->Items;
    /// <summary>Elements in use.</summary>
    public ref int Count => ref ((ExoArrayRec*)Ptr)->Count;
    /// <summary>Elements the array has room for.</summary>
    public ref int Cap => ref ((ExoArrayRec*)Ptr)->Alloc;
    /// <summary>Element <paramref name="i"/> of a list of pointers; no bounds check.</summary>
    public ref IntPtr this[int i] => ref ((IntPtr*)Data)[i];
}

/// <summary>A <c>SafePointer</c>: the target object's address, then the index of its slot in the target's
/// back-reference list.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct SafePointerRec
{
    /// <summary>The object it names.</summary>
    public IntPtr Target;
    /// <summary>Its slot in that object's back-reference list.</summary>
    public int Index;
}

public readonly unsafe ref partial struct Gob
{
    /// <summary>The <c>SafePointerList</c> of back-references to this gob.</summary>
    public ExoArray SafeArray => new(Ptr + EngineLayout.GobSafePointers);
    /// <summary>The <c>CExoArrayList</c> behind <c>RegisterCallBack</c>.</summary>
    public ExoArray CallbackArray => new(Ptr + EngineLayout.GobCallbacks);
    /// <summary>The attached gobs.</summary>
    public ExoArray AttachArray => new(Ptr + EngineLayout.SeamGobAttachList);
    /// <summary>The inline name array; its address is what <c>Gob::GetName</c> hands back.</summary>
    public byte* NameBytes => (byte*)(Ptr + EngineLayout.SeamGobName);
    /// <summary>The installed behaviour's address, or zero.</summary>
    public IntPtr Behavior { get => Attach.Ptr; set => Attach = new CAurBehavior(value); }
    /// <summary>Animated float <paramref name="i"/> of the embedded visual-transform record, which lays its
    /// <c>LerpFloat</c>s end to end.</summary>
    public LerpFloat VisualLerp(int i) => new(Ptr + EngineLayout.GobScaleLerp + i * EngineLayout.SeamLerpFloatSizeof);
    /// <summary>The two scale channels <c>Gob::ScaleObject</c> writes, side by side.</summary>
    public Span<float> ScaleChannels => new(Unsafe.AsPointer(ref SeamScaleChannel), 2);
}

public readonly unsafe partial struct Scene
{
    /// <summary>The <c>SafePointerList</c> of back-references to this scene.</summary>
    public ExoArray SafeArray => new(Ptr + EngineLayout.SeamSceneSafeList);
}

public readonly unsafe partial struct Camera
{
    /// <summary>The address of the <c>Gob</c> sub-object a camera carries.</summary>
    public IntPtr GobSub => Ptr + EngineLayout.SeamCameraGobSub;
    /// <summary>The word just past the scene pointer, where a stand-in camera parks the real shell.</summary>
    public ref IntPtr ParkedShell => ref *(IntPtr*)(Ptr + EngineLayout.CameraScene + sizeof(IntPtr));
}

public readonly unsafe partial struct StringGob
{
    /// <summary>The address of the <c>CAurString</c> sub-object a text object carries.</summary>
    public IntPtr StringSub => Ptr + EngineLayout.SeamStringGobStringSub;
}
