using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

internal static partial class Halos
{
    /// <summary>The occlusion probe's compute stage.</summary>
    internal const string OccCs = "cx_haloocc_c";

    /// <summary><c>Scene::RenderLensFlares</c> @0x471860's own integration terms: the fade travels 3.0 per second on
    /// a delta floored at 0.02 s, so a flare takes a third of a second to appear or vanish however fast the frame runs.</summary>
    private const float FlareRate = 3.0f, FlareDtFloor = 0.02f;

    /// <summary>The probe's push constants, as <c>cx_haloocc_c</c>'s <c>CxHaloOcc</c> block lays them out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OccConsts
    {
        /// <summary>World to clip: the pass's view times its projection.</summary>
        public Matrix4x4 ViewProj;
        /// <summary>The projection's x and y scales (the tap spread) and the pair that linearises reversed depth.</summary>
        public Vector4 ProjTerms;
        /// <summary>Staged elements this frame.</summary>
        public int Count;
        /// <summary>How far a fade may travel this frame.</summary>
        public float Step;
    }

    /// <summary>The probe's constants for <paramref name="count"/> elements under the pass's own matrices.</summary>
    internal static OccConsts OccPush(in Matrix4x4 view, in Matrix4x4 proj, int count, float step)
        => new() { ViewProj = view * proj, ProjTerms = new Vector4(proj.M11, proj.M22, proj.M33, proj.M43), Count = count, Step = step };

    /// <summary>How far a fade travels over a frame of <paramref name="dt"/> seconds.</summary>
    internal static float FadeStep(float dt) => FlareRate * MathF.Max(dt, FlareDtFloor);

    /// <summary><paramref name="key"/>'s fade slot: the next free one the first time this area stages the light, the
    /// same one after, so a light that leaves the population and returns keeps its fade.</summary>
    internal static int FadeSlot(IntPtr key)
    {
        if (!_st.Slots.TryGetValue(key, out int slot)) _st.Slots[key] = slot = _st.Slots.Count;
        return slot;
    }

    /// <summary>Slots handed out this area: the fade buffer holds at least this many.</summary>
    internal static int FadeSlots => _st.Slots.Count;

    /// <summary>True once after each area edge: the fade buffer still holds the last area's fades, so zero it.</summary>
    internal static bool TakeFadeReset()
    {
        bool reset = _st.FadeReset;
        _st.FadeReset = false;
        return reset;
    }
}
