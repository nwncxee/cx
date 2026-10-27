using System.Numerics;
using CX.Graphics;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Everything the overhead sky view remembers between frames: the fit it was rendered with, the
/// caster generation and frame it was rendered at, and the census.</summary>
internal sealed class SkyPassState
{
    public SkyFit Fit, Key;
    public int KeyGen = -1, Gen = -1, GenAt = -1;
    public bool Baked;
    /// <summary>A render landed this frame and its map has not been baked from it yet.</summary>
    public bool Pending;
    /// <summary>The <c>CNWCArea*</c> the map was rendered for. IDENTITY ONLY, and the whole of its validity:
    /// a frame that loses the sun gate keeps the map, a frame in another area does not.</summary>
    public nint Area;
    public int Bakes, Held;
}

/// <summary>The overhead sky view -- one orthographic depth render of the still casters, straight down,
/// fitted to the AREA's own extent rather than to the camera, so it is rendered once per area and again when
/// that geometry changes. Not a shadow: <see cref="SkyView"/> reads it as a height field. Its own target, so
/// the sun array's layers and the map's resolution are independent of <c>shadow.size</c>.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Frames a re-render waits after the caster generation last moved. An area load enrols parts a
    /// few a frame, and a full-area depth render per enrolment would be the load screen's whole budget.</summary>
    private const int SkySettleFrames = 30;

    private static readonly SkyPassState _sky = new();

    /// <summary>The fit the live map was rendered with. <see cref="SkyFit.IsNone"/> when there is none.</summary>
    internal static SkyFit SkyMapFit => _sky.Fit;
    /// <summary>The map still describes the area being drawn. Keyed on the AREA, not on a per-frame stamp:
    /// a frame that happens to lose the sun gate renders no view, and a one-frame flash back to the flat
    /// ambient would read as a pop under every roof in sight.</summary>
    internal static bool SkyFresh => _sky.Baked && !_sky.Fit.IsNone
                                  && _sky.Area == FrameInputs.AreaPtr && !FrameInputs.Interior;

    /// <summary>The shadow bracket's sky slot. <paramref name="sun"/> is the cascades' exterior gate: an
    /// interior has no sky, so the term is off there and the area ambient stands as the author wrote it.</summary>
    private static void SkyPass(bool sun)
    {
        // Every early-out here LEAVES the held map alone; SkyFresh is what decides whether the lit stage may
        // read it, so nothing below can cost a frame its term.
        if (!sun || !SkyView.Wanted) return;
        SkyFit fit = SkyMath.Fit(FrameInputs.AreaExtent, SkyView.MapSize);
        if (fit.IsNone) return;
        // The static set grows as parts enrol, so the caster generation IS "the geometry changed" -- and the
        // settle is what keeps an area load to one render instead of one per enrolment.
        int gen = AlloyRenderer.CasterGen;
        if (gen != _sky.Gen) { _sky.Gen = gen; _sky.GenAt = _frameSeq; }
        bool settled = _frameSeq - _sky.GenAt >= SkySettleFrames;
        // ponytail: a tile fade turns its caster off, so a re-render landing while the camera has faded a
        // roof away misses it and the space under it brightens until the next one. The settle makes that
        // coincidence rare; the upgrade is a submit that ignores the fade for this view alone.
        bool stale = !SkyFresh || !fit.Vp.Equals(_sky.Key.Vp) || (gen != _sky.KeyGen && settled);
        if (!stale) { _sky.Held++; return; }
        Matrix4x4 vp = fit.Vp;
        // Full detail and the STATIC set, for the cover map's reason: this is read as geometry, not as a
        // shadow, and a simplified roof edge moves the boundary a whole alley's ambient turns on.
        if (RenderView(CasterMap.Sky, _gl.SkyDepth, SkyView.MapSize, 0, &vp, SkyMath.Down, fit.Centre, fit.Radius, fit.Radius,
                       point: false, set: CasterGather.SetStatic, detail: false))
        {
            _sky.Baked = true; _sky.Key = fit; _sky.KeyGen = gen; _sky.Pending = true;
            _sky.Fit = fit; _sky.Area = FrameInputs.AreaPtr; _sky.Bakes++;
        }
    }


    /// <summary>Area edge: the map and the key it was rendered under both describe an area that has gone, even
    /// when the client hands the next area the same <c>CNWCArea</c>, which would otherwise read as still fresh.</summary>
    internal static void SkyAreaEdge() => SkyRelease();

    /// <summary>The caster set went away (area edge, module unload, a failed flush): so did the world the
    /// map was baked from.</summary>
    private static void SkyRelease()
    {
        _sky.Baked = false; _sky.Pending = false; _sky.Key = default; _sky.KeyGen = -1; _sky.Gen = -1;
        _sky.Fit = default; _sky.Area = 0;
        SkyView.Revoke();
    }


    /// <summary>The overhead view the colour frame's flush just landed, for the Vulkan bake, once; false when there is
    /// none this frame or it no longer describes the area being drawn.</summary>
    internal static bool VkSkyTake(out ImageId depth, out SkyFit fit)
    {
        depth = _vk.Sky; fit = _sky.Fit;
        if (!_sky.Pending) return false;
        _sky.Pending = false;
        return SkyFresh && !depth.IsNull;
    }

    internal static string SkyStats()
        => _disarmed ? "the caster pass is DISARMED, so no overhead view is rendered"
         : $"sky view {_sky.Bakes} rendered / {_sky.Held} held";
}
