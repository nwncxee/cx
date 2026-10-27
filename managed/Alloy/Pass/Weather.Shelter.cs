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
    private static int _sShelVp, _sShelVpInv, _sShelSize, _sShelEdge, _sShelHollow, _sShelDepth, _sNoiseSize, _sRipTime, _sRipSize, _sRipRate;
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

    /// <summary>After a cover bake was filtered (the plain depth view in hand): bake the cache for it.</summary>
    private static void ShelterBake(uint depthView, int mapSize)
    {
        _gl.ShelValid = false;
        if (!ShelterCache.V || depthView == 0 || !EnsureShelter(Math.Min(mapSize, ShelterSizeMax))) return;
        if (!Matrix4x4.Invert(CasterPass.CoverVp, out Matrix4x4 inv)) return;
        Matrix4x4 vp = CasterPass.CoverVp;
        Vector4 p = ShelterParams();
        float radius = MathF.Max(CoverRange.V, 1f);
        Gl.UseProgram(_gl.ProgShel);
        Gl.BindImageTexture(0, _gl.ShelTex, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, depthView);
        Gl.Uniform1i(_sShelDepth, 0);
        Gl.UniformMatrix4fv(_sShelVp, 1, 0, (float*)&vp);
        Gl.UniformMatrix4fv(_sShelVpInv, 1, 0, (float*)&inv);
        Gl.Uniform2i(_sShelSize, _gl.ShelSize, _gl.ShelSize);
        Gl.Uniform4f(_sShelEdge, p.X, p.Y, p.Z, p.W);
        Gl.Uniform1f(_sShelHollow, HollowStepM / (CoverDepthRadii * radius));
        uint g = (uint)((_gl.ShelSize + ShelGroup - 1) / ShelGroup);
        Gl.DispatchCompute(g, g, 1);
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.UseProgram(0);
        _shelBaked = p; _gl.ShelValid = true; _shelBakes++;
    }

    /// <summary>Once per world frame from the weather draw: the noise tile on first need, a re-bake when a
    /// knob moved the cache's parameters, and this frame's ripple tile while it rains.</summary>
    private static void ShelterTick()
    {
        if (_gl.ShelFailed || !EnsurePrograms()) return;
        if (!_gl.NoiseDone && EnsureTile(ref _gl.NoiseTex, Gl.RGBA16F, NoiseSize, mips: true))
        {
            Gl.UseProgram(_gl.ProgNoise);
            Gl.BindImageTexture(0, _gl.NoiseTex, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
            Gl.Uniform1i(_sNoiseSize, NoiseSize);
            Gl.DispatchCompute(NoiseSize / ShelGroup, NoiseSize / ShelGroup, 1);
            Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.TEXTURE_UPDATE_BARRIER_BIT);
            Gl.GenerateTextureMipmap(_gl.NoiseTex);
            Gl.UseProgram(0);
            _gl.NoiseDone = true;
        }
        if (ShelterCache.V && CoverDepthView != 0 && (!_gl.ShelValid || ShelterParams() != _shelBaked))
            ShelterBake(CoverDepthView, _coverMapSize);
        float rate = SplashRate(false);
        if (rate > 0f && EnsureTile(ref _gl.RipTex, Gl.RG16F, RippleSize, mips: false))
        {
            Gl.UseProgram(_gl.ProgRip);
            Gl.BindImageTexture(0, _gl.RipTex, 0, 0, 0, Gl.WRITE_ONLY, Gl.RG16F);
            Gl.Uniform1i(_sRipTime, AlloyRenderer.TimeOfDay);
            Gl.Uniform1i(_sRipSize, RippleSize);
            Gl.Uniform1f(_sRipRate, rate);
            Gl.DispatchCompute(RippleSize / ShelGroup, RippleSize / ShelGroup, 1);
            Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
            Gl.UseProgram(0);
            _ripBakes++;
        }
    }

    /// <summary>Per lit / grass program bind: whether the cache belongs to the cover view the stage is reading.</summary>
    private static void UploadCache(int locCache, bool cover)
    {
        if (locCache >= 0) Gl.Uniform1f(locCache, cover && ShelterCache.V && ShelOk ? 1f : 0f);
        Bind(ShelterUnit, _gl.ShelValid ? _gl.ShelTex : 0);
    }

    /// <summary>The noise and ripple tiles on their units, for the lit stage; the ripple alone for the water.</summary>
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

    private static bool EnsurePrograms()
    {
        if (_gl.ProgShel != 0) return true;
        if (Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.MemoryBarrier == null || Gl.GenerateTextureMipmap == null)
        { _gl.ShelFailed = true; _log?.Info("Weather: no compute entry points -- the shelter cache, noise and ripple tiles stand down (shelter runs per pixel, no snow relief, no ripples)"); return false; }
        _gl.ProgShel = GlProgram.Compute(ShaderSource.Get("cx_wx_shel_c.glsl"), _log, "Weather shelter cache");
        _gl.ProgNoise = GlProgram.Compute(ShaderSource.Get("cx_wx_noise_c.glsl"), _log, "Weather noise tile");
        _gl.ProgRip = GlProgram.Compute(ShaderSource.Get("cx_wx_ripl_c.glsl"), _log, "Weather ripple tile");
        if (_gl.ProgShel == 0 || _gl.ProgNoise == 0 || _gl.ProgRip == 0)
        { _gl.ShelFailed = true; _gl.ProgShel = 0; _log?.Info("Weather: a surface-term program did not build -- shelter runs per pixel, no snow relief, no ripples"); return false; }
        _sShelVp = GlProgram.Loc(_gl.ProgShel, "uCoverVp"); _sShelVpInv = GlProgram.Loc(_gl.ProgShel, "uCoverVpInv");
        _sShelSize = GlProgram.Loc(_gl.ProgShel, "uSize"); _sShelEdge = GlProgram.Loc(_gl.ProgShel, "uEdge");
        _sShelHollow = GlProgram.Loc(_gl.ProgShel, "uHollow"); _sShelDepth = GlProgram.Loc(_gl.ProgShel, "uCoverDepth");
        _sNoiseSize = GlProgram.Loc(_gl.ProgNoise, "uSize");
        _sRipTime = GlProgram.Loc(_gl.ProgRip, "uTimeOfDay"); _sRipSize = GlProgram.Loc(_gl.ProgRip, "uSize"); _sRipRate = GlProgram.Loc(_gl.ProgRip, "uRate");
        return true;
    }

    private static bool EnsureShelter(int size)
    {
        if (_gl.ShelFailed || !EnsurePrograms()) return false;
        if (_gl.ShelTex == 0 || _gl.ShelSize != size)
        {
            RenderTarget.Create(ref _gl.ShelTex, Gl.RGBA16F, size, size, Gl.LINEAR);
            _gl.ShelSize = size;
            _log?.Info($"Weather: shelter cache {size}x{size} RGBA16F");
        }
        return true;
    }

    /// <summary>A world-tiling texture: REPEAT, bilinear, optionally a full mip chain.</summary>
    private static bool EnsureTile(ref uint tex, uint format, int size, bool mips)
    {
        if (tex != 0) return true;
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.TextureStorage2D(t, mips ? BitOperations.Log2((uint)size) + 1 : 1, format, size, size);
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, mips ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.REPEAT);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.REPEAT);
        tex = t;
        return true;
    }

    private static string ShelterStats() => $", shelter cache {(ShelOk ? "held" : "none")} ({_shelBakes} bakes), ripple tiles {_ripBakes}";
}
