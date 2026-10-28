using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The weather's precomputed surface terms: what the lit stage, the grass and the water used to work
/// out per pixel per frame for a world that had not changed. Three textures -- the shelter disk and snow drift
/// per cover bake, the value-noise tile per context, the ripple tile per rainy frame -- all read through
/// <c>layout(binding)</c> units past the 0..15 budget (<see cref="TextureSlots"/>' reason).</summary>
internal static unsafe partial class Weather
{
    internal static Knob<bool> ShelterCache = null!;

    internal const uint ShelterUnit = 23, NoiseUnit = 24, RippleUnit = 25;
    private const int ShelterSizeMax = 2048, NoiseSize = 1024, RippleSize = 1024, ShelGroup = 8;

    /// <summary>The parameters the cache was baked with; a knob that moves them re-bakes it.</summary>
    private static Vector4 _shelBaked;
    private static int _shelBakes, _ripBakes;

    private static void DeclareShelterKnobs()
        => ShelterCache = G.Toggle("shelcache", "Cached shelter", true, Tier.ChatOnly,
                                   help: "Work out the shelter edge and the snow drift once per cover update instead of per pixel per frame. Off runs the per-pixel test everywhere (for comparing).");

    /// <summary>The bake's parameters now: uv per metre, the disk radius, the breakup, the drift width in uv
    /// (0 while no snow lies or falls).</summary>
    private static Vector4 ShelterParams()
    {
        float radius = MathF.Max(CoverRange.V, 1f);
        bool snow = CurrentKind == Kind.Snow || _snow > 0f;
        return new Vector4(0.5f / radius, MathF.Max(CoverSoft.V * EdgeKernelShare, EdgeKernelMinM), EdgeBreak.V,
                           snow && SnowPile.V > 0f ? SnowPileWidth.V * 0.5f / radius : 0f);
    }

    /// <summary>Per lit / grass program bind: whether the cache belongs to the cover view the stage is reading.</summary>
    private static void UploadCache(int locCache, bool cover)
    {
        if (locCache >= 0) Gl.Uniform1f(locCache, cover && ShelterCache.V && ShelOk ? 1f : 0f);
        Bind(ShelterUnit, _gl.ShelValid ? _gl.ShelTex : 0);
    }

    /// <summary>The weather noise tile and this frame's ripple field, onto their own units.</summary>
    private static void BindTiles() { Bind(NoiseUnit, _gl.NoiseDone ? _gl.NoiseTex : 0); BindRipple(); }

    internal static void BindRipple() => Bind(RippleUnit, _gl.RipTex);

    private static void Bind(uint unit, uint tex)
    {
        if (Renderer.OnVulkan) return;
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The cover bake is gone (area edge, flush failure): so is the cache baked from it.</summary>
    private static void ShelterRelease() { _gl.ShelValid = false; _vks.ShelValid = false; }

    private static string ShelterStats() => $", shelter cache {(ShelOk ? "held" : "none")} ({_shelBakes} bakes), ripple tiles {_ripBakes}";
}
