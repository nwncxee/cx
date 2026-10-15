using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One element of a std140 float array: the value, then the three words std140 pads it to.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PackedFloat
{
    /// <summary>The element's value.</summary>
    public float Value;
    private float _pad1, _pad2, _pad3;
}

/// <summary>One float per sun cascade.</summary>
[InlineArray(4)]
internal struct CascadeFloats { private float _e; }

/// <summary>One view-projection matrix per sun cascade.</summary>
[InlineArray(4)]
internal struct CascadeMatrices { private Matrix4x4 _e; }

/// <summary>One float per local shadow cube, at std140's 16-byte stride.</summary>
[InlineArray(64)]
internal struct CubeFloats { private PackedFloat _e; }

/// <summary>One position-and-reach vec4 per local shadow cube.</summary>
[InlineArray(64)]
internal struct CubeLights { private Vector4 _e; }

/// <summary>The members of the <c>CxPass</c> uniform block the renderer's own fill touches, at the std140 offsets
/// <c>AlloyRenderer.Pb*</c> pin against the shader. The rest of the block belongs to the light fill.</summary>
[StructLayout(LayoutKind.Explicit, Size = AlloyRenderer.PassBytes)]
internal struct PassBlock
{
    [FieldOffset(AlloyRenderer.PbLight)] public CubeLights Light;
    [FieldOffset(AlloyRenderer.PbDynPage)] public CubeFloats DynPage;
    [FieldOffset(AlloyRenderer.PbFade)] public CubeFloats Fade;
    [FieldOffset(AlloyRenderer.PbSunVp)] public CascadeMatrices SunVp;
    [FieldOffset(AlloyRenderer.PbSunTexel)] public CascadeFloats SunTexel;
    [FieldOffset(AlloyRenderer.PbSunBias)] public CascadeFloats SunBias;
    [FieldOffset(AlloyRenderer.PbSunDyn)] public CascadeFloats SunDyn;
    [FieldOffset(AlloyRenderer.PbSunRadius)] public CascadeFloats SunRadius;
    [FieldOffset(AlloyRenderer.PbLocalSoft)] public Vector4 LocalSoft;
    [FieldOffset(AlloyRenderer.PbLocalPcss)] public Vector4 LocalPcss;
    [FieldOffset(AlloyRenderer.PbSunFocus)] public Vector3 SunFocus;
    [FieldOffset(AlloyRenderer.PbSunCascades)] public int SunCascades;
    [FieldOffset(AlloyRenderer.PbAmbient)] public Vector3 Ambient;
    [FieldOffset(AlloyRenderer.PbDiffuse)] public Vector3 Diffuse;
    [FieldOffset(AlloyRenderer.PbDir)] public Vector3 Dir;
    [FieldOffset(AlloyRenderer.PbLocalFloor)] public float LocalFloor;
    [FieldOffset(AlloyRenderer.PbLocalPlane)] public Vector2 LocalPlane;
    [FieldOffset(AlloyRenderer.PbProbeMips)] public float ProbeMips;
    [FieldOffset(AlloyRenderer.PbProbeGain)] public float ProbeGain;
    [FieldOffset(AlloyRenderer.PbSceneEnv)] public int SceneEnv;
    [FieldOffset(AlloyRenderer.PbShBudget)] public float ShBudget;
    [FieldOffset(AlloyRenderer.PbLightFloor)] public float LightFloor;
    [FieldOffset(AlloyRenderer.PbFullModel)] public float FullModel;
    [FieldOffset(AlloyRenderer.PbSunPenum)] public float SunPenum;
    [FieldOffset(AlloyRenderer.PbSearchTaps)] public float SearchTaps;
    [FieldOffset(AlloyRenderer.PbTile)] public Vector4 Tile;
    [FieldOffset(AlloyRenderer.PbGrid)] public int GridX;
    [FieldOffset(AlloyRenderer.PbGrid + 4)] public int GridY;
    [FieldOffset(AlloyRenderer.PbGrid + 8)] public int GridZ;
    [FieldOffset(AlloyRenderer.PbReach)] public float Reach;
    [FieldOffset(AlloyRenderer.PbFalloff)] public Vector3 Falloff;
    [FieldOffset(AlloyRenderer.PbWrap)] public float Wrap;
    [FieldOffset(AlloyRenderer.PbZ)] public Vector2 Z;
    [FieldOffset(AlloyRenderer.PbAtten)] public Vector2 Atten;
    [FieldOffset(AlloyRenderer.PbHot)] public Vector2 Hot;
    [FieldOffset(AlloyRenderer.PbSss)] public Vector2 Sss;
    [FieldOffset(AlloyRenderer.PbWrapN)] public float WrapN;
    [FieldOffset(AlloyRenderer.PbDayDim)] public float DayDim;
    [FieldOffset(AlloyRenderer.PbRelevance)] public Vector2 Relevance;
    [FieldOffset(AlloyRenderer.PbBounds)] public float Bounds;
}
