using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The rain occlusion map -- one orthographic depth view of the still casters along the
/// precipitation vector, baked the way a sun cascade is and held the same way: the texel-snapped fit matrix
/// is the key, so it re-bakes on a focus latch, a wind change or an invalidate and on no other frame. It is
/// the layer past the cascades in the sun array, sampled through <c>uSunShadow</c> with no unit of its own.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Extra layers the sun array carries past the cascades: the cover map.</summary>
    private const int CoverLayers = 1;
    /// <summary>Focus drift, as a fraction of the extent, before the fit re-latches.</summary>
    private const float CoverSlack = 0.25f;

    /// <summary>World -&gt; the cover view's clip space, as the layer was rendered. Zero when not fresh.</summary>
    internal static Matrix4x4 CoverVp;
    /// <summary>The map's side: the sun array's.</summary>
    internal static int CoverMapSize => _mapSize;
    /// <summary>The sun-array layer holding the map (the cascade count).</summary>
    internal static int CoverLayer => _cascades;
    internal static bool CoverFresh => _coverStamp == _frameSeq && !_vk.Cascades.IsNull;
    /// <summary>The world point the latched cover fit is centred on -- R2's wetness map dispatches over the
    /// map rect this window covers, and takes its z as the height its forward projection starts from.</summary>
    internal static Vector3 CoverCentre => _coverC;
    internal static int CoverBakes, CoverHeld;
    private static Matrix4x4 _coverKeyVp;
    private static Vector3 _coverC;
    private static bool _coverBaked, _coverCValid, _coverNew;
    private static int _coverStamp = -1;
    /// <summary>The cover light's <see cref="ShadowMath.LatchUpX"/> latch: rain falls within a few degrees of vertical.</summary>
    private static bool _coverUpX;

    /// <summary>The shadow bracket's cover slot: bake or hold. <paramref name="sun"/> is the cascades' own
    /// exterior gate -- the array only exists past it, and an interior has no sky to fall from.</summary>
    private static void CoverPass(Vector3 focus, bool sun)
    {
        if (!sun || !Weather.CoverWanted || !VkArmed) { CoverVp = default; return; }
        Vector3 dir = Weather.CoverDir;
        float radius = Weather.CoverRange.V, margin = radius;
        float slack = radius * CoverSlack;
        Vector3 c = _coverCValid ? _coverC : focus;
        if (!_coverCValid || (focus - c).LengthSquared() > slack * slack) c = focus;
        Matrix4x4 vp = ShadowMath.DirectionalLightViewProj(dir, c, radius, margin, _mapSize,
                                                         ShadowMath.LatchUpX(ref _coverUpX, dir));
        bool stale = _frameInvalidate || !_coverBaked || vp != _coverKeyVp;
        if (!stale) { CoverHeld++; CoverVp = vp; _coverStamp = _frameSeq; return; }
        // CX-50: full detail, like the lightmap's scratch cube. This map is not a shadow -- the weather
        // reads it as geometry, for where rain and snow stop, where a puddle may form and whether a grass
        // blade is sheltered -- and a simplified roof edge moves all three. Its fit is the whole
        // precipitation range, so its texels are coarse and nearly every caster would thin.
        if (RenderView(CasterMap.SunStatic, _mapSize, CoverLayer, &vp, dir, c, radius, margin, point: false,
                       set: CasterGather.SetStatic, detail: false))
        {
            _coverBaked = true; _coverKeyVp = vp; _coverC = c; _coverCValid = true; _coverNew = true;
            CoverVp = vp; _coverStamp = _frameSeq; CoverBakes++;
        }
        else CoverVp = default;
    }

    private static void CoverRelease()
    {
        _coverBaked = false; _coverCValid = false; _coverNew = false; CoverVp = default; Weather.EsmRelease();
        _vk.CoverReadGen++; _vk.CoverReadVp = default;
    }

    private static readonly Action<int, bool> _coverReadDone = (gen, ran) =>
    {
        CasterVkState s = _vk;
        s.CoverWriting = false;
        if (!ran || gen != s.CoverReadGen) return;
        (s.CoverHeld, s.CoverNext) = (s.CoverNext, s.CoverHeld);
        s.CoverReadVp = s.CoverPendingVp;
    };

    /// <summary>Copy the bake just recorded to where a footfall can read it, into the buffer no footfall reads; it
    /// takes over when the copy has landed. A later bake waits for that, since one flip per copy is what keeps the
    /// held view with the held bytes.</summary>
    private static void CoverReadQueue(VkBackend vk, CommandList cl)
    {
        CasterVkState s = _vk;
        if (s.CoverWriting || !vk.DescOf(s.Cascades, out ImageDesc d)) return;
        _coverNew = false;
        int window = CoverWindow(d.W, Weather.CoverRange.V), off = (d.W - window) / 2;
        if (s.CoverNext.IsNull || s.CoverReadSize != d.W || s.CoverReadWindow != window)
        {
            vk.Destroy(s.CoverHeld);
            vk.Destroy(s.CoverNext);
            s.CoverReadVp = default;
            s.CoverReadSize = d.W;
            s.CoverReadWindow = window;
            s.CoverHeld = CoverReadBuffer(vk, window);
            s.CoverNext = CoverReadBuffer(vk, window);
            if (s.CoverHeld.IsNull || s.CoverNext.IsNull)
            {
                s.CoverReadSize = 0;
                _log?.Always("CasterPass: a cover readback buffer was refused -- footfalls count as open until a later bake gets both");
                return;
            }
        }
        cl.CopyImageToBuffer(new CopyToBufferCmd(s.Cascades, new ImageRegion(0, off, off, CoverLayer), window, window, s.CoverNext, 0));
        s.CoverPendingVp = _coverKeyVp;
        s.CoverWriting = true;
        cl.WhenDone(_coverReadDone, s.CoverReadGen);
    }

    private static BufferId CoverReadBuffer(VkBackend vk, int size)
        => vk.CreateBuffer(new BufferDesc((long)size * size * sizeof(float), BufferUse.CopyDst, MemoryKind.Readback, "vk cover readback"));

    /// <summary>The bias <c>CxCovered</c> compares the cover map with.</summary>
    internal const float CoverBias = 0.002f;
    /// <summary>Metres a foot is read above where it stands, the lift <c>CxWetOpen</c> starts from: the bias is a share
    /// of the cover range, and at a short range it is less than a creature can sit under the drawn floor.</summary>
    private const float CoverLiftM = 0.12f;

    /// <summary>Can the weather reach <paramref name="p"/>? The map the stages read, one texel of it: the snow's own read
    /// feathers about 0.4 m around an eave, so the two can differ there. No map, or past its edge, is open.</summary>
    internal static bool CoverOpen(Vector3 p)
    {
        CasterVkState s = _vk;
        Matrix4x4 vp = CoverVp.M44 == 0f ? default : s.CoverReadVp;
        if (!CoverTexel(vp, s.CoverReadSize, s.CoverReadWindow, p, out int at, out float z) || s.Dev is not VkBackend vk || s.CoverHeld.IsNull) return true;
        float stored = 0f;
        return !vk.ReadMapped(s.CoverHeld, at * (long)sizeof(float), MemoryMarshal.AsBytes(new Span<float>(ref stored))) || CoverClears(z, stored);
    }

    /// <summary><see cref="CoverOpen"/> over a depth map in memory.</summary>
    internal static bool CoverOpenIn(ReadOnlySpan<float> depth, int size, int window, in Matrix4x4 vp, Vector3 p)
        => !CoverTexel(vp, size, window, p, out int at, out float z) || CoverClears(z, depth[at]);

    /// <summary>Metres from the player past which a footfall is not heard; the square kept for footfalls reaches this
    /// far even when the player has walked to the edge of a re-fit. ponytail: a creature further off reads as open, so
    /// it can press a print under a roof; widen the square if one shows.</summary>
    private const float CoverHearingM = 15f;

    /// <summary>The side of the square a <paramref name="size"/> map keeps for footfalls, at a cover range of <paramref name="radius"/>.</summary>
    internal static int CoverWindow(int size, float radius)
    {
        float half = CoverHearingM + radius * CoverSlack;
        if (half >= radius) return size;
        int w = (int)MathF.Ceiling(size * half / radius);
        return Math.Min(w + (w & 1), size);
    }

    /// <summary>Does a point at map depth <paramref name="z"/> clear what the map holds? GL's depth, as the map's sampler compares it.</summary>
    private static bool CoverClears(float z, float stored) => z - CoverBias <= stored;

    /// <summary>Where <paramref name="p"/> falls in a <paramref name="size"/> square cover map, and its depth there, as
    /// <c>CxCovered</c> works them out. False with no map, or past the square of side <paramref name="window"/> kept at its centre.</summary>
    private static bool CoverTexel(in Matrix4x4 vp, int size, int window, Vector3 p, out int at, out float z)
    {
        at = 0; z = 0f;
        if (vp.M44 == 0f || size <= 0) return false;
        Vector4 c = Vector4.Transform(new Vector4(p.X, p.Y, p.Z + CoverLiftM, 1f), vp);
        float x = c.X / c.W * 0.5f + 0.5f, y = c.Y / c.W * 0.5f + 0.5f;
        if (!(x >= 0f && x <= 1f && y >= 0f && y <= 1f)) return false;
        z = c.Z / c.W * 0.5f + 0.5f;
        int off = (size - window) / 2;
        int col = Math.Min((int)(x * size), size - 1) - off, row = Math.Min((int)(y * size), size - 1) - off;
        if (col < 0 || row < 0 || col >= window || row >= window) return false;
        at = row * window + col;
        return true;
    }

    /// <summary>The cover-edge knob moved: filter the held bake again at its new width, and copy it out again with it.</summary>
    internal static void RefilterCover() { if (_coverBaked) _coverNew = true; }

    internal static string CoverStats() => $"cover {CoverBakes} bakes / {CoverHeld} held / {Weather.EsmBakes} filtered";
}
