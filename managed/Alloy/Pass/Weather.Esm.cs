using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The filtered cover map: the exposure read the lit stage makes is one bilinear fetch of a variance
/// shadow map (depth and depth squared, Gaussian-blurred) rather than a set of compares -- fixed taps band
/// across the feather, rotated ones grain. Shares the snapshot-depth unit, re-bound at every lit program
/// bind; the OIT accumulate takes that unit back, so those draws fall back to a hard compare.</summary>
internal static unsafe partial class Weather
{
    /// <summary>Metres of height a receiver is forgiven -- the blur mixes neighbouring ground heights across the feather.</summary>
    private const float EsmBiasM = 0.3f;
    /// <summary>Metres of depth spread below which the variance is clamped: a flat roof's texels all agree, and a
    /// zero variance would put the whole penumbra on one texel.</summary>
    private const float EsmMinSpreadM = 0.1f;
    private const int EsmSizeMax = 1024;

    private static float _esmC, _esmBias;
    internal static int EsmBakes;
    /// <summary>The cover map's side at its last bake: what a knob-driven shelter re-bake sizes the cache by.</summary>
    private static int _coverMapSize;

    /// <summary>Per lit program bind: the filtered map on its unit (0 = none, and the stage's parameter says so).
    /// Under OIT the unit is NOT touched: the pass bound the opaque depth there and the list program's early-Z
    /// fetches it by pixel -- a top-down map in its place discards in a screen-glued shape. Those draws take
    /// the hard compare.</summary>
    internal static void BindEsm(int locParams, bool oit)
    {
        bool on = !oit && EsmOk && Cover.V && CasterPass.CoverFresh;
        if (locParams >= 0) Gl.Uniform4f(locParams, _esmC, _esmBias, on ? 1f : 0f, 0f);
    }

    /// <summary>The cover map is gone with its bake (area edge, flush failure): so is the filtered copy.</summary>
    internal static void EsmRelease() { _vks.EsmValid = false; _vks.FilteredBake = -1; ShelterRelease(); }

    /// <summary>A plain-depth copy of this cover bake is held, on whichever backend draws.</summary>
    private static bool CoverDepthOk => _vks.EsmValid;

    /// <summary>One blur axis of the two moments, a Gaussian over 13 taps at uStep. The first axis reads the
    /// depth view (uFirst: r is the depth, its square is made here); the second reads the moment pair.</summary>
    internal static string EsmFs => ShaderSource.Get("cx_esm_f.glsl");
}
