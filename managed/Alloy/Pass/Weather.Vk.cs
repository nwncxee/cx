using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static unsafe partial class Weather
{
    /// <summary>What falls this frame on Vulkan, under <see cref="Draw"/>'s gates, the fall and phase stepped: the
    /// instance count (-1 when nothing at all is drawn, not even a splash), and the roof drips (rain's program, kind 3)
    /// while a cover map finds the lips.</summary>
    internal static int StageVk(Vector2 wind, out bool snow, out int drips)
    {
        snow = CurrentKind == Kind.Snow;
        _count = 0; _drips = 0; drips = 0;
        if (_off || _failed || !Enabled.V || FrameInputs.Interior) return -1;
        int count = CurrentKind != Kind.None && Density > 0f ? (int)(MaxCount.V * Density * DensityScale.V * (snow ? 0.5f : 1f)) : 0;
        drips = !snow && Cover.V && CasterPass.CoverFresh ? (int)(MaxCount.V * Drips.V * _wet) : 0;
        if (count <= 0 && drips <= 0 && SplashCount() + FootLive(TrackNow) <= 0) return -1;
        Tick(wind);
        _count = count; _drips = drips; _drawn += count + drips;
        return count;
    }

    /// <summary>The stage pair for rain or snow, as GL builds it.</summary>
    internal static (string Vs, string Fs) VkTexts(bool snow, ExtenderLog log)
    {
        string fs = snow ? ShdSnowFs : ShdRainFs;
        GlProgram.Texts(ShdWeatherVs, ShaderStages.Source(ShdWeatherVs), fs, ShaderStages.Source(fs), log, "Weather",
                        out string v, out string f);
        return (v, f);
    }

    /// <summary>The draw's locations in a program resolved through the uniform shim.</summary>
    internal static Loc VkLocs(uint prog) => new()
    {
        View = GlProgram.Loc(prog, "uView"), Proj = GlProgram.Loc(prog, "uProj"), Eye = GlProgram.Loc(prog, "uEye"),
        Centre = GlProgram.Loc(prog, "uCentre"), Fall = GlProgram.Loc(prog, "uFall"), Phase = GlProgram.Loc(prog, "uPhase"),
        Time = GlProgram.Loc(prog, "uTime"), Box = GlProgram.Loc(prog, "uBox"), Size = GlProgram.Loc(prog, "uSize"),
        Kind = GlProgram.Loc(prog, "uKind"), Drift = GlProgram.Loc(prog, "uDrift"), Tint = GlProgram.Loc(prog, "uTint"),
        Alpha = GlProgram.Loc(prog, "uAlpha"), Soft = GlProgram.Loc(prog, "uSoft"), Viz = GlProgram.Loc(prog, "uViz"),
        CoverLayer = GlProgram.Loc(prog, "uCoverLayer"), Lit = GlProgram.Loc(prog, "uLit"), Back = GlProgram.Loc(prog, "uBack"),
        CoverVp = GlProgram.Loc(prog, "uCoverVp"), CoverSoft = GlProgram.Loc(prog, "uCoverSoft"),
    };

    /// <summary>The draw's terms into the open capture, as <see cref="Submit"/> sets them, for <paramref name="kind"/>
    /// (1 rain, 2 snow, 3 drips).</summary>
    internal static void WriteVk(in Loc l, int kind, Matrix4x4* view, Matrix4x4* proj, Vector3 eye, bool lit)
    {
        bool snow = kind == 2;
        VkUniformShim.Set(l.View, *view);
        VkUniformShim.Set(l.Proj, *proj);
        VkUniformShim.Set(l.Eye, [eye.X, eye.Y, eye.Z]);
        Vector3 centre = FrameInputs.HasFocus ? FrameInputs.Focus : eye;
        VkUniformShim.Set(l.Centre, [centre.X, centre.Y, centre.Z]);
        VkUniformShim.Set(l.Fall, [_fall.X, _fall.Y, _fall.Z]);
        VkUniformShim.Set(l.Phase, (float)_phase);
        VkUniformShim.Set(l.Time, _wobble);
        VkUniformShim.Set(l.Box, [Range.V, Range.V]);
        VkUniformShim.Set(l.Size, [snow ? FlakeSize.V : kind == 3 ? StreakLen.V * 0.25f : StreakLen.V, snow ? FlakeSize.V : kind == 3 ? 0.03f : 0.02f]);
        VkUniformShim.Set(l.Kind, kind);
        VkUniformShim.Set(l.Drift, snow ? 0.4f : 0f);
        float c = snow ? Brightness.V * 1.67f : Brightness.V;
        VkUniformShim.Set(l.Tint, [c, c, c]);
        VkUniformShim.Set(l.Lit, lit ? 1 : 0);
        VkUniformShim.Set(l.Back, snow ? Backscatter.V : 0f);
        VkUniformShim.Set(l.Alpha, snow ? 0.9f : kind == 3 ? MathF.Min(Opacity.V * 2f, 1f) : Opacity.V);
        VkUniformShim.Set(l.Soft, Soft.V);
        VkUniformShim.Set(l.Viz, Viz.V);
        bool cover = Cover.V && CasterPass.CoverFresh;
        VkUniformShim.Set(l.CoverVp, cover ? CasterPass.CoverVp : default);
        VkUniformShim.Set(l.CoverLayer, (float)CasterPass.CoverLayer);
        VkUniformShim.Set(l.CoverSoft, CoverSoft.V);
    }

    /// <summary>The splash stage pair, as GL builds it.</summary>
    internal static (string Vs, string Fs) VkSplashTexts(ExtenderLog log)
    {
        GlProgram.Texts(ShdSplashVs, ShaderStages.Source(ShdSplashVs), ShdSplashFs, ShaderStages.Source(ShdSplashFs), log,
                        "Weather splash", out string v, out string f);
        return (v, f);
    }

    /// <summary>The splash draw's locations in a program resolved through the uniform shim.</summary>
    internal static SpLoc VkSplashLocs(uint prog) => new()
    {
        View = GlProgram.Loc(prog, "uView"), Proj = GlProgram.Loc(prog, "uProj"), Eye = GlProgram.Loc(prog, "uEye"),
        Lit = GlProgram.Loc(prog, "uLit"), P = GlProgram.Loc(prog, "uSplashP"), C = GlProgram.Loc(prog, "uSplashC"),
        V = GlProgram.Loc(prog, "uSplashV"), CoverVp = GlProgram.Loc(prog, "uCoverVp"), CoverVpInv = GlProgram.Loc(prog, "uCoverVpInv"),
        Tint = GlProgram.Loc(prog, "uTint"), Alpha = GlProgram.Loc(prog, "uAlpha"), Rain = GlProgram.Loc(prog, "uSplashRain"),
        Foot = GlProgram.Loc(prog, "uFoot"), FootW = GlProgram.Loc(prog, "uFootW"),
        WxMapXf = GlProgram.Loc(prog, "uWxMapXf"), WxMapP = GlProgram.Loc(prog, "uWxMapP"),
    };

    /// <summary>The splash draw's terms into the open capture, as <see cref="DrawSplash"/> sets them: its instance count,
    /// 0 when nothing lands this frame. Called after <see cref="StageVk"/>, which stepped the splash clock.</summary>
    internal static int WriteSplashVk(in SpLoc l, Matrix4x4* view, Matrix4x4* proj, int* viewport, Vector3 eye, bool lit)
    {
        _splashes = 0; _footSplashes = 0;
        int crowns = SplashCount(), feet = FootLive(TrackNow);
        Matrix4x4 vp = default, inv = default;
        bool cover = Cover.V && CasterPass.CoverFresh && CoverDepthOk && Matrix4x4.Invert(CasterPass.CoverVp, out inv);
        if (cover) vp = CasterPass.CoverVp;
        else crowns = 0;
        if (crowns + feet <= 0 || !AlloyLights.TryPerspective(*proj, out _, out float p11, out _, out _)) return 0;
        VkUniformShim.Set(l.View, *view);
        VkUniformShim.Set(l.Proj, *proj);
        VkUniformShim.Set(l.Eye, [eye.X, eye.Y, eye.Z]);
        Vector3 centre = FrameInputs.HasFocus ? FrameInputs.Focus : eye;
        VkUniformShim.Set(l.C, [centre.X, centre.Y, CasterPass.CoverCentre.Z]);
        Vector2 vel = SplashVelocity(new Vector2(centre.X, centre.Y));
        VkUniformShim.Set(l.V, [vel.X, vel.Y]);
        VkUniformShim.Set(l.P, [_splashT, SplashLifeSec, ImpactRange.V, p11 * viewport[3] * 0.5f]);
        VkUniformShim.Set(l.CoverVp, vp);
        VkUniformShim.Set(l.CoverVpInv, inv);
        VkUniformShim.Set(l.Rain, crowns * SplashParts);
        VkUniformShim.Set(l.Foot, System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(_footLive.AsSpan(0, feet)));
        VkUniformShim.Set(l.FootW, [WetShown, FootSplash.V]);
        UploadMap(l.WxMapXf, l.WxMapP, false);
        float c = Brightness.V;
        VkUniformShim.Set(l.Tint, [c, c, c]);
        VkUniformShim.Set(l.Alpha, MathF.Min(Opacity.V * 1.5f, 1f));
        VkUniformShim.Set(l.Lit, lit ? 1 : 0);
        _splashes = crowns; _footSplashes = feet;
        int n = (crowns + feet * FootCrowns) * SplashParts;
        _drawn += n;
        return n;
    }
}
