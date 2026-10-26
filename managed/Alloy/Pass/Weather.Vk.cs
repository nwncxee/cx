using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static unsafe partial class Weather
{
    /// <summary>What falls this frame on Vulkan, under <see cref="Draw"/>'s gates, the fall and phase stepped: the
    /// instance count, 0 for nothing. The wetness map, the shelter, the drips and the splashes are GL's passes and are
    /// not on Vulkan, and nothing is covered (the rain cover map is not baked there).</summary>
    internal static int StageVk(Vector2 wind, out bool snow)
    {
        snow = CurrentKind == Kind.Snow;
        _count = 0; _drips = 0;
        if (_off || _failed || !Enabled.V || FrameInputs.Interior) return 0;
        int count = CurrentKind != Kind.None && Density > 0f ? (int)(MaxCount.V * Density * DensityScale.V * (snow ? 0.5f : 1f)) : 0;
        if (count <= 0) return 0;
        Tick(wind);
        _count = count; _drawn += count;
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

    /// <summary>The draw's terms into the open capture, as <see cref="Submit"/> sets them, with no cover.</summary>
    internal static void WriteVk(in Loc l, bool snow, Matrix4x4* view, Matrix4x4* proj, Vector3 eye, bool lit)
    {
        VkUniformShim.Set(l.View, *view);
        VkUniformShim.Set(l.Proj, *proj);
        VkUniformShim.Set(l.Eye, [eye.X, eye.Y, eye.Z]);
        Vector3 centre = FrameInputs.HasFocus ? FrameInputs.Focus : eye;
        VkUniformShim.Set(l.Centre, [centre.X, centre.Y, centre.Z]);
        VkUniformShim.Set(l.Fall, [_fall.X, _fall.Y, _fall.Z]);
        VkUniformShim.Set(l.Phase, (float)_phase);
        VkUniformShim.Set(l.Time, _wobble);
        VkUniformShim.Set(l.Box, [Range.V, Range.V]);
        VkUniformShim.Set(l.Size, [snow ? FlakeSize.V : StreakLen.V, snow ? FlakeSize.V : 0.02f]);
        VkUniformShim.Set(l.Kind, snow ? 2 : 1);
        VkUniformShim.Set(l.Drift, snow ? 0.4f : 0f);
        float c = snow ? Brightness.V * 1.67f : Brightness.V;
        VkUniformShim.Set(l.Tint, [c, c, c]);
        VkUniformShim.Set(l.Lit, lit ? 1 : 0);
        VkUniformShim.Set(l.Back, snow ? Backscatter.V : 0f);
        VkUniformShim.Set(l.Alpha, snow ? 0.9f : Opacity.V);
        VkUniformShim.Set(l.Soft, Soft.V);
        VkUniformShim.Set(l.Viz, Viz.V);
        VkUniformShim.Set(l.CoverLayer, -1f);
        VkUniformShim.Set(l.CoverSoft, CoverSoft.V);
    }
}
