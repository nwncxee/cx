using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The members of the <c>CxPass</c> uniform block the renderer's own fill touches, at the std140 offsets
/// <c>AlloyRenderer.Pb*</c> pin against the shader. The rest of the block belongs to the light fill.</summary>
[StructLayout(LayoutKind.Explicit, Size = AlloyRenderer.PassBytes)]
internal unsafe struct PassBlock
{
    [FieldOffset(AlloyRenderer.PbLight)] public fixed float Light[256];
    [FieldOffset(AlloyRenderer.PbDynPage)] public fixed float DynPage[256];
    [FieldOffset(AlloyRenderer.PbFade)] public fixed float Fade[256];
    [FieldOffset(AlloyRenderer.PbSunVp)] public fixed float SunVp[64];
    [FieldOffset(AlloyRenderer.PbSunVp + 48)] public Vector4 SunVp0Col3;
    [FieldOffset(AlloyRenderer.PbSunTexel)] public fixed float SunTexel[4];
    [FieldOffset(AlloyRenderer.PbSunBias)] public fixed float SunBias[4];
    [FieldOffset(AlloyRenderer.PbSunDyn)] public fixed float SunDyn[4];
    [FieldOffset(AlloyRenderer.PbSunRadius)] public fixed float SunRadius[4];
    [FieldOffset(AlloyRenderer.PbSunFocus)] public Vector3 SunFocus;
    [FieldOffset(AlloyRenderer.PbSunCascades)] public int SunCascades;
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
}
