using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

internal static partial class Halos
{
    /// <summary>The occlusion probe's compute stage.</summary>
    internal const string OccCs = "cx_haloocc_c";

    /// <summary>The probe's push constants, as <c>cx_haloocc_c</c>'s <c>CxHaloOcc</c> block lays them out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OccConsts
    {
        public Matrix4x4 ViewProj;
        public Vector4 ProjTerms;
        public int Count;
        public float Step;
    }

    internal static OccConsts OccPush(in Matrix4x4 view, in Matrix4x4 proj, int count, float step) => default;

    internal static float FadeStep(float dt) => 0f;

    internal static int FadeSlot(IntPtr key) => -1;

    internal static bool TakeFadeReset() => false;
}
