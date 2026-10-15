using System.Numerics;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Everything the overhead sky view remembers between frames: the fit it was rendered with, the
/// caster generation and frame it was rendered at, and the census.</summary>
internal sealed class SkyPassState
{
    public SkyFit Fit, Key;
    public int KeyGen = -1, Gen = -1, GenAt = -1, Stamp = -1;
    public bool Baked, Fresh;
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
    /// <summary>The map describes THIS frame's world: the lit stage must not scale ambient by another
    /// area's geometry.</summary>
    internal static bool SkyFresh => _sky.Stamp == _frameSeq && _gl.SkyDepth != 0;

    /// <summary>The shadow bracket's sky slot. <paramref name="sun"/> is the cascades' exterior gate: an
    /// interior has no sky, so the term is off there and the area ambient stands as the author wrote it.</summary>
    private static void SkyPass(bool sun)
    {
        if (!sun || !SkyView.Wanted) { _sky.Fit = default; return; }
        SkyFit fit = SkyMath.Fit(FrameInputs.AreaExtent, SkyView.MapSize);
        if (fit.IsNone) { _sky.Fit = default; return; }
        // The static set grows as parts enrol, so the caster generation IS "the geometry changed" -- and the
        // settle is what keeps an area load to one render instead of one per enrolment.
        int gen = AlloyRenderer.CasterGen;
        if (gen != _sky.Gen) { _sky.Gen = gen; _sky.GenAt = _frameSeq; }
        bool settled = _frameSeq - _sky.GenAt >= SkySettleFrames;
        // ponytail: a tile fade turns its caster off, so a re-render landing while the camera has faded a
        // roof away misses it and the space under it brightens until the next one. The settle makes that
        // coincidence rare; the upgrade is a submit that ignores the fade for this view alone.
        bool stale = !_sky.Baked || !fit.Vp.Equals(_sky.Key.Vp) || (gen != _sky.KeyGen && settled);
        if (!stale) { _sky.Held++; _sky.Fit = _sky.Key; _sky.Stamp = _frameSeq; return; }
        if (!EnsureSkyDepth()) { _sky.Fit = default; return; }
        Matrix4x4 vp = fit.Vp;
        // Full detail and the STATIC set, for the cover map's reason: this is read as geometry, not as a
        // shadow, and a simplified roof edge moves the boundary a whole alley's ambient turns on.
        if (RenderView(_gl.SkyDepth, SkyView.MapSize, 0, &vp, SkyMath.Down, fit.Centre, fit.Radius, fit.Radius,
                       point: false, set: CasterGather.SetStatic, detail: false))
        {
            _sky.Baked = true; _sky.Key = fit; _sky.KeyGen = gen; _sky.Fresh = true;
            _sky.Fit = fit; _sky.Stamp = _frameSeq; _sky.Bakes++;
        }
        else _sky.Fit = default;
    }

    /// <summary>After the layered flush landed this frame's renders: a new depth view gets its map
    /// (<see cref="SkyView.Bake"/>), which is the only thing the lit stage ever reads.</summary>
    private static void SkyBaked()
    {
        if (!_sky.Fresh) return;
        _sky.Fresh = false;
        if (!SkyFresh) return;
        SkyView.Bake(_gl.SkyDepth, _sky.Fit);
    }

    /// <summary>The caster set went away (area edge, module unload, a failed flush): so did the world the
    /// map was baked from.</summary>
    private static void SkyRelease()
    {
        _sky.Baked = false; _sky.Fresh = false; _sky.Key = default; _sky.KeyGen = -1; _sky.Gen = -1;
        _sky.Fit = default;
        SkyView.Revoke();
    }

    /// <summary>The overhead view's own depth target: one layer, compare mode OFF (the bake reads a stored
    /// height, not a verdict), and allocated here rather than through <c>DepthArray</c> so a refusal stands
    /// the sky term down by name instead of disarming every shadow in the frame.</summary>
    private static bool EnsureSkyDepth()
    {
        if (!EnsureCommon()) return false;
        if (_gl.SkyDepth != 0) return true;
        int size = SkyView.MapSize;
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D_ARRAY, 1, &t);
        Gl.DrainErrors();   // an earlier pass's error must not be read as this allocation's
        Gl.TextureStorage3D(t, 1, Gl.DEPTH_COMPONENT24, size, size, 1);
        if (Gl.GetError() != 0)
        {
            Gl.DeleteTextures(1, &t);
            SkyView.NoDepthTarget(size);
            return false;
        }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_MODE, (int)Gl.NONE);
        _gl.SkyDepth = t;
        _sky.Baked = false;
        return true;
    }

    internal static string SkyStats() => $"sky view {_sky.Bakes} rendered / {_sky.Held} held";
}
