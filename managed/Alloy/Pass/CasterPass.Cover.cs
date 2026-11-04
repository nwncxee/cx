using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Graphics;
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
        if (ran && gen == _vk.CoverReadGen) _vk.CoverReadVp = _vk.CoverPendingVp;
    };

    /// <summary>Copy the bake just recorded to where a footfall can read it. A refused buffer is logged, and footfalls with
    /// no copy count as open.
    /// ponytail: one buffer, so a footfall in the two frames a copy is in flight may read the new bake under the old
    /// view; a second buffer flipped on completion if a step ever lands wrong at a re-fit.</summary>
    private static void CoverReadQueue(VkBackend vk, CommandList cl)
    {
        CasterVkState s = _vk;
        _coverNew = false;
        if (!vk.DescOf(s.Cascades, out ImageDesc d)) return;
        if (s.CoverRead.IsNull || s.CoverReadSize != d.W)
        {
            vk.Destroy(s.CoverRead);
            s.CoverReadVp = default;
            s.CoverReadSize = d.W;
            s.CoverRead = vk.CreateBuffer(new BufferDesc((long)d.W * d.W * sizeof(float), BufferUse.CopyDst, MemoryKind.Readback, "vk cover readback"));
            if (s.CoverRead.IsNull)
            {
                s.CoverReadSize = 0;
                _log?.Always("CasterPass: the cover readback buffer was refused -- footfalls count as open until a later bake gets one");
                return;
            }
        }
        cl.CopyImageToBuffer(new CopyToBufferCmd(s.Cascades, new ImageRegion(0, 0, 0, CoverLayer), d.W, d.W, s.CoverRead, 0));
        s.CoverPendingVp = _coverKeyVp;
        cl.WhenDone(_coverReadDone, s.CoverReadGen);
    }

    /// <summary>The view the CPU's copy of the cover map holds, zero with none or with the cover off.</summary>
    internal static Matrix4x4 CoverReadVp => CoverVp.M44 == 0f ? default : _vk.CoverReadVp;
    internal static int CoverReadSize => _vk.CoverReadSize;

    /// <summary>One texel of the CPU's copy of the cover map. False when there is none to read.</summary>
    internal static bool CoverDepthAt(int at, out float depth)
    {
        depth = 0f;
        return _vk.Dev is VkBackend vk && !_vk.CoverRead.IsNull
            && vk.ReadMapped(_vk.CoverRead, at * (long)sizeof(float), MemoryMarshal.AsBytes(new Span<float>(ref depth)));
    }

    /// <summary>The cover-edge knob moved: filter the held bake again at its new width.</summary>
    internal static void RefilterCover() { if (_coverBaked) _coverNew = true; }

    internal static string CoverStats() => $"cover {CoverBakes} bakes / {CoverHeld} held / {Weather.EsmBakes} filtered";
}
