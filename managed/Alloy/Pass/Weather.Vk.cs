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

    /// <summary>A field held still: its fall, phase and cover view written in place of the frame's, so a fixture draws one
    /// field the same way twice.</summary>
    internal sealed class WxHoldState
    {
        public Vector3 Fall;
        public double Phase;
        public Matrix4x4 CoverVp;
    }

    private static WxHoldState? _hold;

    /// <summary>Holds the field at <paramref name="h"/>; null lets it fall with the frame again.</summary>
    internal static void Hold(WxHoldState? h) => _hold = h;

    internal const string ShdWxTask = "cx_wx_task", ShdWxMesh = "cx_wx_mesh";

    /// <summary>The task, mesh and fragment stages for rain or snow on the mesh path; a content override of the mesh and
    /// fragment pair is taken as the vertex and fragment pair is.</summary>
    internal static (string Ts, string Ms, string Fs) VkMeshTexts(bool snow, ExtenderLog log)
    {
        string fs = snow ? ShdSnowFs : ShdRainFs;
        string ts = ShaderResource.Override(ShdWxTask, log) is { } o ? ShaderSource.Content(o, log) : ShaderStages.Source(ShdWxTask);
        ShaderTexts.Pair(ShdWxMesh, ShaderStages.Source(ShdWxMesh), fs, ShaderStages.Source(fs), log, "Weather mesh",
                        out string m, out string f);
        return (ts, m, f);
    }

    /// <summary>The stage pair for rain or snow, as GL builds it.</summary>
    internal static (string Vs, string Fs) VkTexts(bool snow, ExtenderLog log)
    {
        string fs = snow ? ShdSnowFs : ShdRainFs;
        ShaderTexts.Pair(ShdWeatherVs, ShaderStages.Source(ShdWeatherVs), fs, ShaderStages.Source(fs), log, "Weather",
                        out string v, out string f);
        return (v, f);
    }

    /// <summary>The draw's members of the loose block being resolved.</summary>
    internal static Loc VkLocs() => new()
    {
        View = LooseBlock.Loc("uView"), Proj = LooseBlock.Loc("uProj"), Eye = LooseBlock.Loc("uEye"),
        Centre = LooseBlock.Loc("uCentre"), Fall = LooseBlock.Loc("uFall"), Phase = LooseBlock.Loc("uPhase"),
        Time = LooseBlock.Loc("uTime"), Box = LooseBlock.Loc("uBox"), Size = LooseBlock.Loc("uQuad"),
        Kind = LooseBlock.Loc("uKind"), Drift = LooseBlock.Loc("uDrift"), Tint = LooseBlock.Loc("uTint"),
        Alpha = LooseBlock.Loc("uAlpha"), Soft = LooseBlock.Loc("uSoft"), Viz = LooseBlock.Loc("uViz"),
        CoverLayer = LooseBlock.Loc("uCoverLayer"), Lit = LooseBlock.Loc("uLit"), Back = LooseBlock.Loc("uBack"),
        CoverVp = LooseBlock.Loc("uCoverVp"), CoverSoft = LooseBlock.Loc("uCoverSoft"),
        Count = LooseBlock.Loc("uCount"), Slot = LooseBlock.Loc("uSlot"), FallStep = LooseBlock.Loc("uFallStep"), Screen = LooseBlock.Loc("uScreen"),
        Speed = LooseBlock.Loc("uSpeed"), Blur = LooseBlock.Loc("uBlur"), PxScale = LooseBlock.Loc("uPxScale"),
    };

    /// <summary>The cull's own terms into the open capture: the <paramref name="count"/> lattice points it walks, the
    /// draw command <paramref name="slot"/> its survivors count into, and the metres <paramref name="fallStep"/> the field
    /// fell since the last frame at speed 1.</summary>
    internal static void WriteCullVk(in Loc l, int count, int slot, float fallStep)
    {
        LooseBlock.Set(l.FallStep, fallStep);
        LooseBlock.Set(l.Count, count);
        LooseBlock.Set(l.Slot, slot);
    }

    /// <summary>The draw's terms into the open capture for <paramref name="kind"/> (1 rain, 2 snow, 3 drips), drawn into a
    /// target <paramref name="screenH"/> pixels tall.</summary>
    internal static void WriteVk(in Loc l, int kind, Matrix4x4* view, Matrix4x4* proj, Vector3 eye, bool lit, float screenH)
    {
        bool snow = kind == 2;
        LooseBlock.Set(l.Speed, snow ? SnowSpeed.V : RainSpeed.V);
        LooseBlock.Set(l.Blur, Blur.V);
        LooseBlock.Set(l.PxScale, MathF.Abs(proj->M22) * screenH * 0.5f);
        LooseBlock.Set(l.View, *view);
        LooseBlock.Set(l.Proj, *proj);
        LooseBlock.Set(l.Eye, [eye.X, eye.Y, eye.Z]);
        LooseBlock.Set(l.Centre, [eye.X, eye.Y, eye.Z]);
        Vector3 fall = _hold?.Fall ?? _fall;
        LooseBlock.Set(l.Fall, [fall.X, fall.Y, fall.Z]);
        LooseBlock.Set(l.Phase, (float)(_hold?.Phase ?? _phase));
        LooseBlock.Set(l.Time, _wobble);
        LooseBlock.Set(l.Box, [Box, Box]);
        LooseBlock.Set(l.Size, [snow ? FlakeSize.V : kind == 3 ? StreakLen.V * 0.25f : StreakLen.V, snow ? FlakeSize.V : kind == 3 ? 0.03f : 0.02f]);
        LooseBlock.Set(l.Kind, kind);
        LooseBlock.Set(l.Drift, snow ? 0.4f : 0f);
        float c = snow ? Brightness.V * 1.67f : Brightness.V;
        LooseBlock.Set(l.Tint, [c, c, c]);
        LooseBlock.Set(l.Lit, lit ? 1 : 0);
        LooseBlock.Set(l.Back, snow ? Backscatter.V : 0f);
        LooseBlock.Set(l.Alpha, snow ? 0.9f : kind == 3 ? MathF.Min(Opacity.V * 2f, 1f) : Opacity.V);
        LooseBlock.Set(l.Soft, Soft.V);
        LooseBlock.Set(l.Viz, Viz.V);
        bool cover = Cover.V && CasterPass.CoverFresh;
        if (_hold != null)
        {
            LooseBlock.Set(l.CoverVp, _hold.CoverVp);
            LooseBlock.Set(l.CoverLayer, 0f);
        }
        else
        {
            LooseBlock.Set(l.CoverVp, cover ? CasterPass.CoverVp : default);
            LooseBlock.Set(l.CoverLayer, (float)CasterPass.CoverLayer);
        }
        LooseBlock.Set(l.CoverSoft, CoverSoft.V);
    }

    /// <summary>The splash stage pair, as GL builds it.</summary>
    internal static (string Vs, string Fs) VkSplashTexts(ExtenderLog log)
    {
        ShaderTexts.Pair(ShdSplashVs, ShaderStages.Source(ShdSplashVs), ShdSplashFs, ShaderStages.Source(ShdSplashFs), log,
                        "Weather splash", out string v, out string f);
        return (v, f);
    }

    /// <summary>The splash draw's members of the loose block being resolved.</summary>
    internal static SpLoc VkSplashLocs() => new()
    {
        View = LooseBlock.Loc("uView"), Proj = LooseBlock.Loc("uProj"), Eye = LooseBlock.Loc("uEye"),
        Lit = LooseBlock.Loc("uLit"), P = LooseBlock.Loc("uSplashP"), C = LooseBlock.Loc("uSplashC"),
        V = LooseBlock.Loc("uSplashV"), CoverVp = LooseBlock.Loc("uCoverVp"), CoverVpInv = LooseBlock.Loc("uCoverVpInv"),
        Tint = LooseBlock.Loc("uTint"), Alpha = LooseBlock.Loc("uAlpha"), Rain = LooseBlock.Loc("uSplashRain"),
        Foot = LooseBlock.Loc("uFoot"), FootW = LooseBlock.Loc("uFootW"),
        WxMapXf = LooseBlock.Loc("uWxMapXf"), WxMapP = LooseBlock.Loc("uWxMapP"),
        Late = LooseBlock.Loc("uLate"), Screen = LooseBlock.Loc("uScreen"),
    };

    /// <summary>The splash draw's terms into the open capture: its instance count,
    /// 0 when nothing lands this frame. Called after <see cref="StageVk"/>, which stepped the splash clock.</summary>
    internal static int WriteSplashVk(in SpLoc l, Matrix4x4* view, Matrix4x4* proj, int* viewport, Vector3 eye, bool lit)
    {
        _splashes = 0; _footSplashes = 0;
        int crowns = SplashCount(), feet = FootLive(TrackNow);
        Matrix4x4 vp = default, inv = default;
        bool cover = Cover.V && CasterPass.CoverFresh && CoverDepthOk && Matrix4x4.Invert(CasterPass.CoverVp, out inv);
        if (cover) vp = CasterPass.CoverVp;
        else crowns = 0;
        if (crowns + feet <= 0 || !AlloyLights.TryPerspective(WorldDepth.Unfold(*proj), out _, out float p11, out _, out _)) return 0;
        LooseBlock.Set(l.View, *view);
        LooseBlock.Set(l.Proj, *proj);
        LooseBlock.Set(l.Eye, [eye.X, eye.Y, eye.Z]);
        Vector3 centre = FrameInputs.HasFocus ? FrameInputs.Focus : eye;
        LooseBlock.Set(l.C, [centre.X, centre.Y, CasterPass.CoverCentre.Z]);
        Vector2 vel = SplashVelocity(new Vector2(centre.X, centre.Y));
        LooseBlock.Set(l.V, [vel.X, vel.Y]);
        LooseBlock.Set(l.P, [_splashT, SplashLifeSec, ImpactRange.V, p11 * viewport[3] * 0.5f]);
        LooseBlock.Set(l.CoverVp, vp);
        LooseBlock.Set(l.CoverVpInv, inv);
        LooseBlock.Set(l.Rain, crowns * SplashParts);
        LooseBlock.Set(l.Foot, System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(_footLive.AsSpan(0, feet)));
        LooseBlock.Set(l.FootW, [WetShown, FootSplash.V]);
        UploadMap(l.WxMapXf, l.WxMapP, false);
        float c = Brightness.V;
        LooseBlock.Set(l.Tint, [c, c, c]);
        LooseBlock.Set(l.Alpha, MathF.Min(Opacity.V * 1.5f, 1f));
        LooseBlock.Set(l.Lit, lit ? 1 : 0);
        _splashes = crowns; _footSplashes = feet;
        int n = (crowns + feet * FootCrowns) * SplashParts;
        _drawn += n;
        return n;
    }
}
