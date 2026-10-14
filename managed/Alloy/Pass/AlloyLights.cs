using System;
using System.Globalization;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The world pass's point-light rig: the snapshot goes into an SSBO -- no 32-light UBO, no uniform-array
/// ceiling -- and a compute pass bins it into a froxel grid. <see cref="DeferredLights"/>' tiled pass walks it
/// for the opaque and cutout set (its own per-group list, or the froxel lists under
/// <c>light.deferredtiles</c>); the forward consumers -- water, grass, particles, weather, probe -- take the rest.
/// </summary>
internal static unsafe class AlloyLights
{
    /// <summary>Froxel grid: 32x18 tiles (~80px at 1440p), 24 exponential depth slices. The per-fragment cost
    /// of clustered shading is the LIGHT LIST LENGTH of the fragment's tile, so quartering the tile area
    /// roughly quarters how many torches share one list, while the build stays O(clusters * lights) on
    /// compute. More clusters = SHORTER per-fragment loops: this knob, not <see cref="ClusterCap"/>.</summary>
    private const int GridX = 32, GridY = 18, GridZ = 24;
    private const int ClusterCount = GridX * GridY * GridZ;
    /// <summary>Lights binned per cluster, boot-tunable (<c>alloy.clustercap</c>); 128 is the shipped value.
    /// NOT free: the fragment loop reads the cluster's COUNT, so a formerly-overflowing froxel pays up to
    /// twice the per-fragment loop, plus SSBO bytes. ponytail: fixed capacity, no per-cluster compaction --
    /// overflow is counted and logged, and a two-pass count-then-fill build is the upgrade.</summary>
    internal static readonly int ClusterCap = Renderer.ClusterCap.Boot;
    /// <summary>The cluster build's per-lane sort array size, deliberately NOT <see cref="ClusterCap"/>.
    /// A dynamically-indexed private array past ~64 entries spills to scratch (32 KB/wave at 128), and on
    /// radeonsi that scratch read faulted on area entry. <c>bincap</c> never indexes past 16; 32 is
    /// clear of the spill threshold. Compile-time shape, not a knob.</summary>
    internal const int SortCap = 32;
    /// <summary>Compute workgroup size; the dispatch is 1D over clusters and decodes (x,y,z) from the id.</summary>
    internal const int GroupSize = 64;
    /// <summary>Words of header per cluster: n | live, R4's refl | the live floor candidates, the cold candidates'
    /// end | the kept prefix (cx_lightbuild's layout). The <c>CxCluster*</c> accessors are the only readers.</summary>
    internal const int ClusterHeadWords = 3;
    /// <summary>Stats words: the overflow count, then a sampled build's binned / kept / candidate entry totals.</summary>
    private const int StatsWords = 4;
    /// <summary>One build in this many frames adds its entry totals to the stats: the counters stay far from
    /// wrapping and the atomics off every other frame.</summary>
    private const int TallyEvery = 16;
    /// <summary>SSBO binding points, shared by the compute and the lit fragment program.</summary>
    private const uint BindLights = SsboSlots.Lights, BindCounts = SsboSlots.ClusterCount, BindIndices = SsboSlots.ClusterIdx,
                       BindStats = SsboSlots.LightStats, BindMovers = SsboSlots.Movers;
    /// <summary>Point-light attenuation, as the ENGINE computes it: <c>f2 = d^2 / R^2</c> (R is
    /// <c>GetLightAdjustedRadius</c>, which is what we upload) and <c>att = (1 - f2) / (mi + ff * f2)</c>,
    /// verified against <c>inc_lighting.shd</c> at <c>GAMMA_CORRECTION == 1</c>. Range-NORMALISED, so the
    /// authored range is the shape and nothing here needs a global gain to compensate.</summary>
    private static float _gain => Lighting.Gain.V;
    private static float _srcRadius => Lighting.Radius.V;
    private static float _falloffPow => Lighting.Falloff.V;

    /// <summary>The near-field core: a COMPACT-SUPPORT bump, <c>(1 - f2/y^2)^2</c>, peaking at <c>x / mi</c>
    /// and exactly zero past <c>y</c> of the light's range, so it adds intensity close up without lifting the
    /// far field. Ships at 0 -- the engine's curve, no core term at all -- which is also what a content
    /// override that never declares <c>uHot</c> reads.</summary>
    private static float _hot => Lighting.Hot.V;
    private static float _hotSize => Lighting.HotSize.V;

    /// <summary>The engine's own two attenuation constants, latched per capture from
    /// <c>GLRender::m_globalShaderConstantsf</c> -- read rather than hardcoded, so a graphics-settings change
    /// moves the owned curve with the engine's. <c>x = lightMaxIntensityInv</c>, whose reciprocal is the peak
    /// every light reaches at its centre; <c>y = lightFalloffFactor</c>. The fallback is a peak of 1.</summary>
    private static Vector2 _atten = new(1f, 1f);
    /// <summary>A real pair has been latched: the lightmap's area key refuses to fold the fallback (the login
    /// load ran before the first light walk and keyed every area apart from its later visits).</summary>
    internal static bool AttenLatched { get; private set; }

    /// <summary>Latch the engine's attenuation constants from the three global shader params behind them.
    /// Called by the light feed, which already reads 0x12 for the radius.</summary>
    internal static void LatchAtten(float minIntensity, float maxIntensity, float cutoffMult)
    {
        if (!(minIntensity > 0f) || !(maxIntensity > 0f) || !(cutoffMult > 0f)) return;
        float mi = MathF.Pow(maxIntensity, -SrgbGamma);
        float ff = cutoffMult * cutoffMult * (MathF.Pow(minIntensity, -SrgbGamma) - mi);
        // A negative falloff factor would make the denominator shrink with distance -- a light that gets
        // BRIGHTER as it fades. It cannot happen with min <= max, so a settings state that produces it is
        // one to leave the last good pair standing for, not to render.
        if (!(mi > 0f) || ff < 0f || float.IsNaN(ff)) return;
        _atten = new Vector2(mi, ff);
        AttenLatched = true;
    }

    /// <summary>What the shader is running, for the period log and the /light echo.</summary>
    internal static Vector2 Atten => _atten;
    /// <summary>Sphere-light wrap: the light is treated as a SPHERE of radius <see cref="_srcRadius"/> times
    /// this, and the diffuse terminator is softened by the half-angle that sphere subtends at the fragment; 0
    /// restores the punctual <c>N.L</c>. At arm's length a punctual light swings N.L across a whole low-poly
    /// facet, which reads as the model cut in half; the wrap converges to plain N.L with distance.</summary>
    private static float _wrap => Lighting.Wrap.V;
    /// <summary>Wrapped diffuse: the distance-INDEPENDENT terminator softening, applied to the sun and as a
    /// floor under <see cref="_wrap"/> on every point light; 0 restores the punctual N.L everywhere. NWN's
    /// meshes are low-poly with smoothed vertex normals, so the terminator crosses a whole facet as one hard
    /// crease -- and the facets are the same size however far away the light is.</summary>
    private static float _wrapN => Lighting.WrapNdl.V;
    /// <summary>The stock SUBSURFACE term's wrap: content's inc_lighting.shd adds, per light,
    /// <c>L * att * max(0, dot(N, L) + w) * 2/(2 + PI*w)</c>, which is most of the "Alloy is darker than
    /// vanilla" gap. Ships at 0.25 rather than the content's 0.38 (brighter past the terminator reads as
    /// glossy), and on the MAPPED normal, not stock's vertex normal, which washes the map's contrast out.</summary>
    private static float _sss => Lighting.Sss.V;
    /// <summary><c>GetGlobalShaderParami(7)</c> = the preamble's <c>SHADER_QUALITY_MODE</c>
    /// (LoadAndCompileSingleShader @0x4d34b0); the subsurface term compiles in above 1.</summary>
    private const uint QualityParam = 7;
    private static RiGetGlobalShaderParamiFn _gspI;

    /// <summary>One of the engine's global shader params, int and float. Resolved once with the rest of the
    /// light rig's entry points; 0 before that, which is every caller's "the engine has not said yet".</summary>
    internal static int GlobalShaderParami(uint k) => _gspI == null ? 0 : _gspI(k);
    /// <inheritdoc cref="GlobalShaderParami"/>
    internal static float GlobalShaderParamf(uint k)
        => EngineFn.RiGetGlobalShaderParamf == null ? 0f : EngineFn.RiGetGlobalShaderParamf(k);
    /// <summary>The stock energy factor for the subsurface term at wrap <paramref name="w"/>:
    /// fEnergyPreservation / (1 + w) = 2 / (2 + PI * w).</summary>
    internal static float SssK(float w) => 2f / (2f + MathF.PI * w);
    private static bool SssOn => _sss > 0f && (_gspI == null || _gspI(QualityParam) > 1);
    /// <summary>Local-cube PCF radius, in cube texels at the sample distance. 0 = the single hardware
    /// PCF tap this shipped with; anything above widens the filter with four more taps on a disc
    /// perpendicular to the sample ray. Costs 4 extra cube fetches per shadowing light per fragment.</summary>
    private static float _shadowSoft => Shadows.Soft.V;
    /// <summary>The PCF disc's radius on SCREEN, in pixels, below which it stands down to the single hardware
    /// tap -- the disc is four of every five cube taps, and its width scales with distance to the LIGHT, so
    /// the test is per fragment rather than a view-depth line. 0 = the disc everywhere. ponytail: a hard
    /// threshold, no fade band across it.</summary>
    private static float _discPx => Shadows.DiscPx.V;
    /// <summary>Self-shadow near-field skip, as a multiple of <see cref="_srcRadius"/>. A world point
    /// closer to the light than this is reported lit outright, on creature (mover) fragments only -- a carried
    /// torch sits INSIDE the hand holding it, and a punctual test there shadows the carrier from its own light.</summary>
    private static float _shadowNear => Shadows.Near.V;
    /// <summary>Scales on the captured area terms, so daylight and interior fill can be dialled apart from
    /// the point lights. 1.0 = the engine's own captured value, untouched.</summary>
    internal static float AmbientScale => Lighting.Ambient.V;
    internal static float SunScale => Lighting.Sun.V;
    /// <summary>The daylight ratio's lamp side, <c>uDayDim</c>: 1 / k, where k eases from 1 to <c>light.daylight</c>
    /// as the area directional's linear luminance crosses <c>daylow</c>..<c>dayhigh</c>. The live and baked lamp
    /// terms take it, so the sun outweighs lamps by day with no rebake and no reliance on HDR or auto-exposure;
    /// halos keep vanilla's flare brightness. An interior has no sun: 1.</summary>
    private static float SunLum
    {
        get { Vector3 d = FrameInputs.AreaDiffuse; return 0.2126f * d.X + 0.7152f * d.Y + 0.0722f * d.Z; }
    }

    /// <summary>The <c>light</c> group's status: the sun luminance <see cref="DayDim"/> reads against
    /// <c>daylow</c>/<c>dayhigh</c>, and the lamp scale it gives -- the numbers the thresholds are tuned from.</summary>
    internal static string DayStatus() =>
        $"sun luminance {SunLum:F3} (daylow {Lighting.DayLow.V:F2}, dayhigh {Lighting.DayHigh.V:F2}) -> lamps x{DayDim:F2}" +
        (FrameInputs.Interior ? " [interior: never dimmed]" : !FrameInputs.HasAreaDiffuse ? " [no area sun read yet]" : "") +
        $" | diffuse {FrameInputs.AreaDiffuse.X:F3},{FrameInputs.AreaDiffuse.Y:F3},{FrameInputs.AreaDiffuse.Z:F3}" +
        $" ambient {FrameInputs.AreaAmbient.X:F3},{FrameInputs.AreaAmbient.Y:F3},{FrameInputs.AreaAmbient.Z:F3}";

    internal static float DayDim
    {
        get
        {
            if (FrameInputs.Interior || !FrameInputs.HasAreaDiffuse) return 1f;
            float lum = SunLum;
            float lo = Lighting.DayLow.V, hi = MathF.Max(Lighting.DayHigh.V, lo + 1e-3f);
            float t = Math.Clamp((lum - lo) / (hi - lo), 0f, 1f);
            return 1f / (1f + (Lighting.Daylight.V - 1f) * t * t * (3f - 2f * t));
        }
    }
    /// <summary>How much of an OVERBRIGHT light's surplus is kept as CENTRE INTENSITY, on top of the reach the
    /// engine already spent it on; applied by the feed as <c>1 + (maxChannel - 1) * this</c>. Overbright IS
    /// the carried-light discriminator: a module-placed light is authored at max channel &lt;= 1 and has no
    /// surplus at all. 0, which ships, is the engine's own behaviour -- an overbright light is wider.</summary>
    internal static float OverbrightScale => Lighting.Overbright.V;

    /// <summary>The scale an overbright colour (max channel past 1) takes: <c>GetLightAdjustedColor</c> @0x4c0b60
    /// divides it down to a max of 1 and spends the surplus on reach, and <see cref="OverbrightScale"/> keeps a
    /// share of that surplus as centre intensity, pre-compensated for the receiver's pow 2.2. 1 for any other.</summary>
    internal static float OverbrightK(float maxc)
    {
        if (!(maxc > 1f)) return 1f;
        float boost = MathF.Max(1f + (maxc - 1f) * OverbrightScale, 0f);
        return MathF.Pow(boost, 1f / SrgbGamma) / maxc;
    }

    /// <summary>The atten pair and the shader-quality gate, for the arm-time log.</summary>
    internal static string DescribeAtten() =>
        $"atten(mi {_atten.X:F3} ff {_atten.Y:F3}, peak {(_atten.X > 0f ? 1f / _atten.X : 0f):F2})  sss {_sss:F2}{(SssOn ? "" : " (off: quality<2)")}";

    /// <summary>cx_lightbuild's HotReach cone test, transcribed for <see cref="SelfCheck"/>: does the sphere (fc, fr)
    /// touch the cone from <paramref name="l"/> tangent to the sphere (c, rr)?</summary>
    private static bool HotConeReaches(Vector3 l, Vector3 c, float rr, Vector3 fc, float fr)
    {
        Vector3 ax = c - l; float d = ax.Length();
        if (d * d <= 2f * rr * rr) return true;
        ax /= d;
        float sn = rr / d, cs = MathF.Sqrt(1f - sn * sn);
        Vector3 v = fc - l; float along = Vector3.Dot(v, ax);
        if (along < -fr) return false;
        float perp = MathF.Sqrt(MathF.Max(Vector3.Dot(v, v) - along * along, 0f));
        return cs * perp - along * sn <= fr;
    }

    /// <summary><c>cx_lightfrag</c>'s CxLightImportance at squared distance <paramref name="dd"/> under the engine's
    /// own curve, transcribed for <see cref="SelfCheck"/>'s sweep of the build's relevance bounds.</summary>
    private static float BoundImportance(float radius, float luma, float dd, float reach)
    {
        float f2 = dd / MathF.Max(radius * radius, 1e-8f);
        if (f2 >= 1f) return 0f;
        float fade = 1f - f2, r2 = reach * reach;
        if (f2 > r2) return 0f;
        fade *= MathF.Min((r2 - f2) / (r2 * 0.3f), 1f);
        return fade / (0.41f + 136.33f * f2) * luma;
    }

    /// <summary><c>cx_lightfrag</c>'s sphere-light normalisation (Karis 2013), transcribed for
    /// <see cref="SelfCheck"/> -- the alpha a specular lobe widens to at <paramref name="dist"/> from a
    /// source sphere of <paramref name="radius"/>, the same sphere that wraps the diffuse terminator.</summary>
    internal static float SphereAlpha(float alpha, float radius, float dist)
        => MathF.Min(alpha + (radius <= 0f ? 0f : radius * 0.5f / MathF.Max(dist, 1e-3f)), 1f);

    /// <summary>...and the energy that widening leaves: <c>(alpha / alpha')^2</c>, which is what the
    /// alpha^2 numerator staying behind in <c>CxSpecBase</c> while the denominator takes alpha' amounts to.</summary>
    internal static float SphereEnergy(float alpha, float alphaP)
    { float k = alpha / MathF.Max(alphaP, 1e-4f); return k * k; }

    /// <summary>The pair's PEAK (at N.H = 1) in the shader's own split: <c>alpha^2</c> out of
    /// <c>CxSpecBase</c> over <c>alpha'^4</c> out of the widened GGX denominator.</summary>
    internal static float SpherePeak(float alpha, float alphaP)
    { float a2 = alphaP * alphaP; return alpha * alpha / MathF.Max(a2 * a2, 1e-20f); }

    /// <summary>Headless check of the lighting maths: the subsurface term is the stock one, the attenuation
    /// constants latch the way the engine computes them, and the curve has the shape the receiver assumes.
    /// Runs off-device; restores whatever it touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // The shader's curve, in C#, so the property checks below are about the SHAPE and not about a
        // second transcription of it: f2 in [0, 1], gain 1, rim-fade exponent 1.
        float Att(float f2, float gain) => gain * (1f - f2) / (_atten.X + _atten.Y * f2);
        var check = new Checker(log);
        // The subsurface term is the stock one or it is nothing: the energy factor is inc_lighting.shd's
        // fEnergyPreservation / (1 + w) and the wrap rides the MAPPED normal (stock: the vertex normal, which washes out normal maps).
        check.Expect(MathF.Abs(SssK(0.38f) - 0.6262f) < 1e-3f, "SssK(0.38) is 2/(2+PI*0.38) = 0.626");
        // CX-188 budget: cxLod is light.fullmodel's per-light weight, 1 at every caller but the walk's faint tail
        check.Expect(FragmentLib.Contains("c * (att * max(ndlRaw + uSss.x, 0.0) * uSss.y * cxLod)"), "the point loop carries the subsurface term on the MAPPED normal");
        check.Expect(FragmentLib.Contains("if (mirror) { spec += spc; continue; }")
          && FragmentLib.Contains("if (mSlot >= 0 && mSlot != aSlot && mSlot != bSlot)") && FragmentLib.Contains("if (l > mLum) { mLum = l; mSlot = slot; mNdl = ndlRaw; mSpc = spc; }")
          // CX-188 named the term `lmSpec`: the deferred twin carries that same value into lmShare
          && AlloyRenderer.OwnedLitFsMain.Contains("vec3 lmSpec = lm * (1.0 - CxGlossW(rough)) * (wxFilm")
          && AlloyRenderer.OwnedLitFsMain.Contains("pointSpec += lmSpec;")
          && AlloyRenderer.OwnedLitFsMain.Contains("float lmLobe = CxSpecLobe(N, V, lmDir, rough, spec);"),
          "a glossy lobe carries its light's shadow whatever the cap says, in the pages' place too");
        check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("max(dot(N, uAreaDir) + uSss.x, 0.0) * uSss.y"), "...and so does the sun");

        // R5: the sphere-light normalisation. A punctual GGX peak is 1/alpha^2, which on a water film's
        // 0.02 is fifteen hundred times the irradiance -- the "solar flare". The properties that make the
        // fix a redistribution and not a dimming knob: identity at radius 0, alpha' never under alpha,
        // energy never above 1, and convergence back to the punctual lobe with distance.
        check.Expect(SphereAlpha(0.2f, 0f, 1f) == 0.2f && SphereEnergy(0.2f, SphereAlpha(0.2f, 0f, 1f)) == 1f
          && MathF.Abs(SpherePeak(0.2f, SphereAlpha(0.2f, 0f, 1f)) - 1f / (0.2f * 0.2f)) < 1e-3f,
          "a source of radius 0 is the punctual lobe exactly: alpha' = alpha, energy 1, peak 1/alpha^2");
        bool mono = true, bounded = true;
        for (int i = 1; i <= 40; i++)
        {
            float alpha = 0.02f + i * 0.02f, d = 0.1f + i * 0.5f;
            float ap = SphereAlpha(alpha, 1f, d);
            mono &= ap >= alpha - 1e-6f && ap <= 1f;
            bounded &= SphereEnergy(alpha, ap) <= 1f + 1e-6f && SpherePeak(alpha, ap) <= 1f / (alpha * alpha) + 1e-3f;
        }
        check.Expect(mono, "alpha' is never under alpha and never over 1, at any roughness or distance");
        check.Expect(bounded, "the widening can only take energy OUT of the lobe, never add it");
        // ...and the case the owner reported: a film at a metre from a torch-sized sphere
        float apFilm = SphereAlpha(0.0256f, 1f, 1f);
        check.Expect(MathF.Abs(apFilm - 0.5256f) < 1e-4f && SphereEnergy(0.0256f, apFilm) < 0.01f,
          $"a water film a metre from a 1 m sphere keeps under a hundredth of its punctual peak energy (alpha' {apFilm:F4})");
        check.Expect(SphereAlpha(0.0256f, 1f, 1000f) - 0.0256f < 1e-3f, "...and converges back to the punctual lobe with distance");
        check.Expect(FragmentLib.Contains("float CxSrcSpread(float dist) { return uWrap <= 0.0 ? 0.0 : uWrap * uFalloff.y * 0.5 / max(dist, 1e-3); }")
          && FragmentLib.Contains("float ap = min(rough + spread, 1.0);")
          && FragmentLib.Contains("float den = 1.0 - NdotH * NdotH * (1.0 - ap * ap);")
          && FragmentLib.Contains("float spread = CxSrcSpread(sqrt(f2) * pr.w) * cxLod;")
          && FragmentLib.Contains("d *= 1.0 / mix(NdotL, 1.0, CxSmithK(rough));"),
          "the lobe widens by the source sphere in the DENOMINATOR alone, with the surface's own Smith k");
        check.Expect(AlloyRenderer.MaterialLib.Contains("float ap = min(a + spread, 1.0);")
          && AlloyRenderer.MaterialLib.Contains("cxCoat * CxCoatLobe(N, V, L, cxCoatRough, spread)")
          && AlloyRenderer.MaterialLib.Contains("spc = min(spc, vec3(CX_FILM_LIGHT_MAX));")
          && AlloyRenderer.MaterialLib.Contains("return lobe / (1.0 + lobe / CX_COAT_LOBE_MAX);")
          && AlloyRenderer.OwnedLitFsMain.Contains("cxCoatRough = sqrt(min(cxCoatRough * cxCoatRough + aaKernel, 1.0));"),
          "the coat's lobe widens with it, its film highlights are firefly-clamped, and it takes the specular-AA kernel");

        // #140: the engine's attenuation constants, and the property that actually matters -- the curve's
        // peak is the engine's own max-intensity setting in LINEAR space. Get the sign of the exponent
        // wrong and the peak inverts into a near-zero, which renders as "the lights all went out" with
        // nothing in the log to say why.
        Vector2 savedAtten = _atten; bool savedLatched = AttenLatched;
        LatchAtten(0.5f, 1.5f, 1f);
        float peak = _atten.X > 0f ? 1f / _atten.X : 0f;
        check.Expect(MathF.Abs(peak - MathF.Pow(1.5f, SrgbGamma)) < 1e-3f, "the curve's peak is param(0x14) linearised");
        check.Expect(MathF.Abs(_atten.Y - (MathF.Pow(0.5f, -SrgbGamma) - MathF.Pow(1.5f, -SrgbGamma))) < 1e-3f,
          "lightFalloffFactor is cutoff^2 * (min^-2.2 - max^-2.2)");
        // ...and at the range the curve is exactly 0, whatever the constants: that is what keeps a light
        // from popping against its own cull sphere.
        check.Expect(MathF.Abs(Att(1f, 1f)) < 1e-6f, "attenuation is exactly 0 at the authored range");
        check.Expect(Att(0f, 1f) > Att(0.5f, 1f) && Att(0.5f, 1f) > Att(0.9f, 1f), "...and monotonic on the way there");
        // The cutoff multiplier is squared into ff and NOT into mi: it moves the rim, never the peak.
        Vector2 narrow = _atten;
        LatchAtten(0.5f, 1.5f, 2f);
        Vector2 wide = _atten;
        check.Expect(MathF.Abs(wide.X - narrow.X) < 1e-6f, "the radius multiplier does not move the peak");
        check.Expect(MathF.Abs(wide.Y - narrow.Y * 4f) < 1e-3f, "...and enters the falloff factor squared");
        LatchAtten(0f, 1.5f, 1f);
        check.Expect(_atten == wide, "a zero intensity setting leaves the last good pair standing");
        LatchAtten(2f, 1f, 1f);
        check.Expect(_atten == wide, "...and so does a min above max, which would invert the falloff");
        _atten = savedAtten; AttenLatched = savedLatched;

        // The hot partition (cx_lightbuild HotReach) may only drop a froxel the lit stage's per-fragment ray test
        // would also have refused: every "near" point must sit inside the tangent cone the build tests.
        var rng = new Random(674);
        float R() => (float)rng.NextDouble() * 20f - 10f;
        int near = 0, escaped = 0;
        for (int t = 0; t < 20000; t++)
        {
            var l = new Vector3(R(), R(), R()); var c = new Vector3(R(), R(), R()); var p = new Vector3(R(), R(), R());
            float rr = (float)rng.NextDouble() * 2f + 0.3f;
            Vector3 dp = p - l; float dl = MathF.Max(dp.Length(), 1e-4f);
            Vector3 dc = c - l; float tt = Vector3.Dot(dc, dp) / dl;
            if (!(tt > -rr && tt < dl + rr && Vector3.Dot(dc, dc) - tt * tt <= rr * rr)) continue;
            near++;
            if (!HotConeReaches(l, c, rr, p, 0f)) escaped++;
        }
        check.Expect(near > 100 && escaped == 0, $"every point the lit stage calls near ({near}) is inside the build's cone ({escaped} escaped)");

        // light.relevance's bounds (cx_lightbuild): the build culls under the floor and searches the candidates
        // alone, which is exact only while both files measure importance with ONE curve that never rises with
        // distance. The text pins hold the two transcriptions together; the sweep holds the claim.
        string build = ShaderStages.Source(ShdClusterCs);
        check.Expect(build.Contains("fade *= min((r2 - f2) / (r2 * 0.3), 1.0);") && FragmentLib.Contains("fade *= min((r2 - f2) / (r2 * 0.3), 1.0);")
          && build.Contains("return fade / (at.x + at.y * f2) * dot(col, vec3(0.299, 0.587, 0.114));")
          && FragmentLib.Contains("return fade / (at.x + at.y * f2) * dot(uLights[li * 2u + 1u].rgb, vec3(0.299, 0.587, 0.114));")
          && build.Contains("if (u <= uRel.x * floorI && (uv < floorV || uv <= 0.0)) entry |= FLAG_CULL;")
          && FragmentLib.Contains("for (uint k = CxListLive(cl), e = CxListColdCand(cl); k < e; k++) relMax")
          // CX-188: ...and the walk's list source still resolves to the froxel table for every forward program,
          // so the generalisation cannot quietly detach the path these pins are about
          && FragmentLib.Contains("uint CxListLive(uint cl) { return CxClusterLive(cl); }")
          && FragmentLib.Contains("uint CxListColdCand(uint cl) { return CxClusterColdCand(cl); }")
          && FragmentLib.Contains("if (uBounds > 0.5) return min(uClCount[cl * 3u + 2u] >> 16u, CX_CLUSTER_CAP);"),
          "the build bounds the lit stage's own importance curve, and a fragment of the built view searches the candidates it names");
        int missed = 0, overCut = 0, culled = 0;
        const int n = 24;
        Span<Vector4> pr = stackalloc Vector4[n]; Span<float> luma = stackalloc float[n];
        for (int t = 0; t < 400; t++)
        {
            var lo = new Vector3(R(), R(), R()); var hi = lo + new Vector3(1f + rng.NextSingle() * 6f, 1f + rng.NextSingle() * 6f, 1f + rng.NextSingle() * 6f);
            float cut = t % 2 == 0 ? 0.01f : 0.1f;
            float floorI = 0f;
            for (int i = 0; i < n; i++)
            {
                pr[i] = new Vector4(R() * 2f, R() * 2f, R() * 2f, 2f + rng.NextSingle() * 30f);
                luma[i] = 0.05f + rng.NextSingle();
                Vector3 e = new(pr[i].X, pr[i].Y, pr[i].Z);
                Vector3 far = Vector3.Max(Vector3.Abs(e - lo), Vector3.Abs(e - hi));
                floorI = MathF.Max(floorI, BoundImportance(pr[i].W, luma[i], far.LengthSquared(), 0.8f));
            }
            for (int s = 0; s < 32; s++)
            {
                var p = lo + (hi - lo) * new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle());
                float full = 0f, cand = 0f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 e = new(pr[i].X, pr[i].Y, pr[i].Z);
                    float imp = BoundImportance(pr[i].W, luma[i], Vector3.DistanceSquared(e, p), 0.8f);
                    float u = BoundImportance(pr[i].W, luma[i], Vector3.DistanceSquared(e, Vector3.Clamp(e, lo, hi)), 0.8f);
                    full = MathF.Max(full, imp);
                    if (u >= floorI) cand = MathF.Max(cand, imp);
                }
                if (cand < full) missed++;
                for (int i = 0; i < n; i++)
                {
                    Vector3 e = new(pr[i].X, pr[i].Y, pr[i].Z);
                    float u = BoundImportance(pr[i].W, luma[i], Vector3.DistanceSquared(e, Vector3.Clamp(e, lo, hi)), 0.8f);
                    if (u > cut * floorI) continue;
                    culled++;
                    if (BoundImportance(pr[i].W, luma[i], Vector3.DistanceSquared(e, p), 0.8f) > cut * full) overCut++;
                }
            }
        }
        check.Expect(culled > 100 && missed == 0 && overCut == 0,
          $"a culled light ({culled} sampled) is under the cut at every point of its froxel ({overCut} not) and the candidates hold the brightest ({missed} missed)");

        // CX-188 budget (light.floor): the per-light perceptual reach cut, the skip rule behind a shorter binned
        // and walked radius. One solved expression, transcribed into the walk and into the build; the pins hold
        // the two texts together and the sweep holds the two claims the skip rests on -- that the cut is exactly
        // where the curve meets the floor (so the window's fade reaches 0 with the light, and there is no ring),
        // and that the build's deliberately looser copy can never bin FEWER lights than the walk lights.
        check.Expect(FragmentLib.Contains("return clamp((lum - uLightFloor * at.x) / max(lum + uLightFloor * at.y, 1e-8), 0.0, 1.0);")
          && build.Contains("return clamp((lum - uLightFloor.x * at.x) / max(lum + uLightFloor.x * at.y, 1e-8), 0.0, 1.0);")
          && build.Contains("float lum = uLightFloor.y * dot(max(c, c * c * c), vec3(0.2126, 0.7152, 0.0722));")
          && FragmentLib.Contains("float rw = min(uReach > 0.0 ? uReach * uReach : 1e30, CxReachCut(c));")
          // ...and a zero window is a refusal, not a 0/0 in the fade (a light already under the floor)
          && FragmentLib.Contains("if (rw <= 0.0) return false;")
          && build.Contains("float ReachR(vec4 pr, vec3 col) { return pr.w * min(uReach, sqrt(ReachCut(col))); }"),
          "light.floor's reach cut is one expression, in the walk and in the cluster build");
        int ringed = 0, narrower = 0;
        for (int t = 0; t < 400; t++)
        {
            float fl = 1e-5f + rng.NextSingle() * 0.01f;
            var at = new Vector2(0.2f + rng.NextSingle() * 2f, 0.2f + rng.NextSingle() * 2f);
            // ...and OVERBRIGHT channels among them: above 1 a channel linearises UP, so the plain sRGB value
            // is not an upper bound on the walk's luma and max(c, c^3) is what the build takes instead.
            float hi = t % 3 == 0 ? 3f : 1f;
            var srgb = new Vector3(rng.NextSingle() * hi, rng.NextSingle() * hi, rng.NextSingle() * hi);
            float lumS = 0.2126f * MathF.Max(srgb.X, srgb.X * srgb.X * srgb.X)
                       + 0.7152f * MathF.Max(srgb.Y, srgb.Y * srgb.Y * srgb.Y)
                       + 0.0722f * MathF.Max(srgb.Z, srgb.Z * srgb.Z * srgb.Z);
            float lumL = 0.2126f * MathF.Pow(srgb.X, 2.2f) + 0.7152f * MathF.Pow(srgb.Y, 2.2f)
                       + 0.0722f * MathF.Pow(srgb.Z, 2.2f);
            float cutW = Math.Clamp((lumL - fl * at.X) / MathF.Max(lumL + fl * at.Y, 1e-8f), 0f, 1f);
            float cutB = Math.Clamp((lumS - fl * at.X) / MathF.Max(lumS + fl * at.Y, 1e-8f), 0f, 1f);
            if (cutB < cutW - 1e-5f) narrower++;
            if (cutW > 0f && cutW < 1f && MathF.Abs(lumL * (1f - cutW) / (at.X + at.Y * cutW) - fl) > fl * 1e-3f) ringed++;
        }
        check.Expect(ringed == 0 && narrower == 0,
          $"light.floor cuts exactly where the curve meets the floor ({ringed} off) and the build's copy is never "
          + $"the narrower of the two, overbright colours included ({narrower} narrower)");

        // CX-188 budget: the deferred shadow budget's DISSOLVE, which is the skip rule's continuity proof.
        // Membership is a step -- the Kth strongest light at a pixel keeps its shadow, the (K+1)th does not --
        // and the pre-budget path hid that step behind a hardware tap for the (K+1)th. What hides it now is the
        // per-lane weight the record folds into its own visibilities: a lane fades to unshadowed as its score
        // falls to the best score that did NOT fit, so the light leaving the record had already dissolved.
        // The sweep drives one light's score straight through every other's and asserts the weight vector moves
        // continuously; the control is the same sweep under hard membership, which must jump, or the test is
        // asserting nothing.
        string shrec = ShaderSource.Get("cx_shrec.glsl");
        string defsh = ShaderSource.Get("cx_shpick.glsl");
        // the literal, not a formatted mirror: a float formatted under the user's own culture writes "2,0" here
        check.Expect(shrec.Contains("const float CX_SH_FADE = 2.0;") && DeferredShadows.RecordFade == 2f
          && shrec.Contains("return drop > 0.0 ? smoothstep(vec4(drop), vec4(drop * CX_SH_FADE), rank) : vec4(1.0);")
          && defsh.Contains("kw = CxShFade(kf, rDrop);")
          && defsh.Contains("return rDrop > 0.0 ? mix(vec4(1.0), vis, kw) : vis;")
          && defsh.Contains("? max(kN >= 4 ? k5 : kN == 3 ? kf.w : kN == 2 ? kf.z : kN == 1 ? kf.y : kf.x, 0.0)")
          && FragmentLib.Contains("return uShBudget > 0.0 ? att * dot(cLin, vec3(0.2126, 0.7152, 0.0722)) : -f2;"),
          "the shadow budget's edge dissolves inside the record, over the band C# mirrors here");
        static float Smooth(float a, float b, float x)
        { float t = Math.Clamp((x - a) / MathF.Max(b - a, 1e-8f), 0f, 1f); return t * t * (3f - 2f * t); }
        const int Lights = 6, Steps = 20000;
        int kMax = DeferredShadows.RecordLights;
        float band = DeferredShadows.RecordFade, worstStep = 0f, worstHard = 0f;
        Span<float> rk = stackalloc float[Lights];
        Span<int> ord = stackalloc int[Lights];
        Span<float> was = stackalloc float[Lights], now = stackalloc float[Lights];
        Span<float> wasH = stackalloc float[Lights], nowH = stackalloc float[Lights];
        for (int t = 0; t < 20; t++)
        {
            // the still lights sit well clear of zero, so the dissolve band is never degenerate
            for (int i = 1; i < Lights; i++) rk[i] = 0.5f + rng.NextSingle() * 0.5f;
            was.Clear(); wasH.Clear();
            for (int st = 0; st <= Steps; st++)
            {
                rk[0] = 2f * st / Steps;   // the sweeper, straight through every other light's score
                for (int i = 0; i < Lights; i++) ord[i] = i;
                for (int i = 1; i < Lights; i++)
                { int j = i; while (j > 0 && rk[ord[j - 1]] < rk[ord[j]]) { (ord[j - 1], ord[j]) = (ord[j], ord[j - 1]); j--; } }
                float rDrop = rk[ord[kMax]];   // the best score that did not fit
                now.Clear(); nowH.Clear();
                for (int i = 0; i < kMax; i++)
                {
                    now[ord[i]] = rDrop > 0f ? Smooth(rDrop, rDrop * band, rk[ord[i]]) : 1f;
                    nowH[ord[i]] = 1f;
                }
                if (st > 0)
                    for (int i = 0; i < Lights; i++)
                    {
                        worstStep = MathF.Max(worstStep, MathF.Abs(now[i] - was[i]));
                        worstHard = MathF.Max(worstHard, MathF.Abs(nowH[i] - wasH[i]));
                    }
                now.CopyTo(was); nowH.CopyTo(wasH);
            }
        }
        check.Expect(worstStep < 0.01f && worstHard > 0.9f,
          $"a light crossing the shadow budget's edge dissolves continuously (worst step {worstStep:F4}) where "
          + $"hard membership switches outright ({worstHard:F2})");
        log.Info(check.Ok ? "AlloyLights.SelfCheck: passed" : "AlloyLights.SelfCheck: FAILED");
        return check.Ok;
    }
    /// <summary>What the shader is actually running, for the period log -- read where it is USED, not at
    /// arm time (the engine's globals are still zero then, which is how a live-looking log line reported a
    /// value the frame never saw).</summary>
    internal static Vector3 LastFalloff;
    /// <summary>The curve, for the [alloy] period line: the engine's constants and the two dials that shape
    /// what is drawn from them.</summary>
    internal static string DescribeCurve() =>
        $"atten (1-f2)/({_atten.X:F3}+{_atten.Y:F3}*f2) body x{LastFalloff.X:F2} " +
        $"core +x{_hot:F2} over {_hotSize:P0} of range";
    /// <summary>Slice-0 depth floor. The engine's near plane can be tiny, and log(d/near) over a near of 0.01
    /// wastes half the slices on the first metre.</summary>
    private const float MinNear = 0.1f;

    private static ExtenderLog? _log;
    /// <summary>Every GL object the context owns and the upload bookkeeping sized against it, in one object:
    /// <see cref="Forget"/> replaces it.</summary>
    private sealed class LightsGlState
    {
        public uint Prog, BufLights, BufCounts, BufIndices, BufStats, BufMovers;
        public int MoversGen = -1;
        public int LocView = -1, LocProjXY = -1, LocZ = -1, LocCount = -1, LocGrid = -1, LocReach = -1, LocBinCap = -1, LocBinAtten = -1, LocReflK = -1;
        public int LocDynPage = -1, LocHot = -1, LocRel = -1, LocPad = -1, LocTally = -1, LocFloor = -1;
        public readonly uint[] Query = new uint[2];
        public bool QueryPending0, QueryPending1;
        /// <summary>Lights the SSBO is currently sized for; a denser area re-allocates it once.</summary>
        public int SsboLights;
        public int Uploaded, UploadFrame = -1;
        public GpuReadback StatsRead;
    }

    private static LightsGlState _gl = new();
    private static bool _failed;
    /// <summary>light.relevance as this frame's <see cref="Upload"/> read it. The build's bounds and the lit
    /// stage's <c>uRelevance</c> must cut at the SAME value, or the culled tail could hold a light the lit
    /// stage would have kept.</summary>
    private static float _rel;
    private static bool _tallyFrame;
    /// <summary>The sampled entry totals the last stats copy returned (a period late).</summary>
    private static uint _tallyBinned, _tallyKept, _tallyCand;
    /// <summary>The viewport and depth range the cluster table was last built for. A pass that draws through it
    /// with any other (the planar mirror reuses the screen's table) gets <c>uBounds</c> 0 and walks every entry.
    /// A pass drawing ANOTHER view through the same viewport and range must <see cref="Build"/> first, as the
    /// probe faces do.</summary>
    private static int _builtW, _builtH;
    /// <summary>Set around a pass that draws ANOTHER camera through the table the screen built (the planar mirror):
    /// its binds get <c>uBounds</c> 0 whatever their viewport says.</summary>
    internal static bool BorrowedTable;
    private static float _builtNear, _builtFar;
    private static int _queryAt;

    /// <summary>Light staging: two vec4 per light (pos+range, colour+pad), filled straight from the walk
    /// snapshot. Grown to whatever the snapshot holds and kept, so the per-frame upload allocates nothing in
    /// the steady state without a worst-case array standing behind it.</summary>
    private static float[] _stage = new float[InitialLights * 8];
    /// <summary>Starting light capacity of the staging buffer and the SSBO. Not a ceiling.</summary>
    private const int InitialLights = 256;

    /// <summary>Period counters, read by the Alloy draw log line.</summary>
    internal static int LastLightCount;
    internal static float LastBuildMs;

    /// <summary>True once the rig is usable: compute + SSBO entry points resolved and the program linked.</summary>
    internal static bool Ready => _gl.Prog != 0 && !_failed;

    /// <summary>Records the light SSBO holds this frame -- the <c>uLightCount</c> any pass that culls
    /// <c>uLights</c> for itself (the deferred pass's per-tile list) has to walk.</summary>
    internal static int UploadedLights => _gl.Uploaded;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _gspI = EngineFn.RiGetGlobalShaderParami;
        log.Info($"AlloyLights: {DescribeAtten()} -- /light retunes every dial live");
    }

    /// <summary>Drop every GL object (context loss / disarm). Safe to call outside the bracket only when the
    /// context is already gone -- the ids are then meaningless anyway.</summary>
    internal static void Forget()
    {
        _gl = new LightsGlState();
    }

    /// <summary>Fill + upload the light SSBO for this frame (idempotent within a frame: reflection subframes
    /// reuse the main frame's snapshot). Colours stay as the walk stored them -- the shader linearizes,
    /// exactly like the staging receiver does.</summary>
    internal static void Upload(int frame)
    {
        if (!Ensure()) return;
        if (_gl.UploadFrame == frame) return;
        _gl.UploadFrame = frame;
        _rel = Lighting.Relevance.V;
        _tallyFrame = frame % TallyEvery == 0;
        int n = FrameInputs.LightCount;
        if (n * 8 > _stage.Length) _stage = new float[n * 8];
        if (n > _gl.SsboLights) GrowLightBuffer(n);
        float rmin = float.MaxValue, rmax = 0f, rsum = 0f;
        fixed (float* s = _stage)
        {
            for (int i = 0; i < n; i++)
            {
                Vector4 pr = FrameInputs.AreaLightPosRange[i];
                Vector3 c = FrameInputs.AreaLightColor[i];
                rmin = MathF.Min(rmin, pr.W); rmax = MathF.Max(rmax, pr.W); rsum += pr.W;
                float* d = s + i * 8;
                d[0] = pr.X; d[1] = pr.Y; d[2] = pr.Z; d[3] = pr.W;
                d[4] = c.X; d[5] = c.Y; d[6] = c.Z;
                // the record's spare w is this light's shadow-cube slot (#95), -1 for a light with no cube.
                // Riding the existing pad is what keeps the cube receiver from needing a second buffer keyed
                // the same way -- and the walk already resolved the slot by light identity.
                d[7] = FrameInputs.AreaLightAmbient[i] ? FrameInputs.AmbientSlot : FrameInputs.AreaLightSlot[i];
                // #674: the static bit rides the same lane, +CX_LIGHT_STATIC; the shader decodes w >= 128.
                if (FrameInputs.AreaLightStatic[i] && !FrameInputs.AreaLightAmbient[i]) d[7] += LightStatic + LightSetStride * (FrameInputs.AreaLightSetIndex[i] + 1);
            }
            Gl.NamedBufferSubData(_gl.BufLights, IntPtr.Zero, (IntPtr)(n * 8 * sizeof(float)), s);
        }
        _gl.Uploaded = n;
        LastLightCount = n;
        _radMin = n > 0 ? rmin : 0f; _radMax = rmax; _radMean = n > 0 ? rsum / n : 0f;
    }

    /// <summary>Re-size the light SSBO for a denser area than any seen this session. Cold: an area load at
    /// worst, and never in a frame that did not need it. The contents are re-uploaded by the caller, which
    /// is why this only has to allocate.</summary>
    private static void GrowLightBuffer(int lights)
    {
        _gl.SsboLights = lights;
        Gl.NamedBufferData(_gl.BufLights, (IntPtr)((long)lights * 8 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _log?.Info($"AlloyLights: light SSBO grown to {lights} lights");
    }

    /// <summary>Read the froxel grid's whole projection input off the projection matrix itself, so the depth
    /// slicing and the froxel bounds can never disagree about which pass they belong to -- this pass may be
    /// rendering a probe cube, whose projection is its own. Raw bytes are row-major and GLSL reads them
    /// column-major, so <c>G[2][3] == -1</c> is CHECKED: an ortho or reversed-Z projection returns false.</summary>
    internal static bool TryPerspective(float* proj, out float p00, out float p11, out float near, out float far)
    {
        p00 = proj[0]; p11 = proj[5];
        near = 0f; far = 0f;
        if (!(p00 > 0f) || !(p11 > 0f)) return false;
        if (MathF.Abs(proj[11] + 1f) > WRowEpsilon) return false;
        float a = proj[10], b = proj[14];
        float da = a - 1f, db = a + 1f;
        if (MathF.Abs(da) < 1e-6f || MathF.Abs(db) < 1e-6f) return false;
        near = b / da; far = b / db;
        return near > 0f && far > near;
    }

    /// <summary>Tolerance on the projection's w row. A perspective GL projection puts exactly -1 there; the
    /// slack only absorbs float noise, never a different projection class.</summary>
    private const float WRowEpsilon = 1e-4f;

    /// <summary>Bin this pass's lights into the froxel grid for a <paramref name="vpW"/> x
    /// <paramref name="vpH"/> viewport. <paramref name="view"/> is the engine's view matrix as raw bytes
    /// (row-major v*M, read column-major by GLSL = the transpose); <paramref name="tally"/> lets a sampled
    /// frame's build add its entry totals to the period line -- the screen's build, not a probe face's.</summary>
    internal static bool Build(float* view, float p00, float p11, float near, float far, int vpW, int vpH, bool tally)
    {
        // NOT gated on the light count: with zero point lights the dispatch still writes an all-zero cluster
        // table, which is exactly right -- the lit parts then shade on area ambient + sun alone instead of
        // being dropped from the frame for want of a torch.
        if (!Ensure()) return false;
        if (!(p00 > 0f) || !(p11 > 0f) || !(far > near)) return false;
        if (near < MinNear) near = MinNear;
        if (far <= near) return false;

        int slot = _queryAt;
        bool timing = _gl.Query[slot] != 0 && !(slot == 0 ? _gl.QueryPending0 : _gl.QueryPending1);
        if (timing) Gl.BeginQuery(Gl.TIME_ELAPSED, _gl.Query[slot]);

        Gl.UseProgram(_gl.Prog);
        Gl.Uniform3i(_gl.LocGrid, GridX, GridY, GridZ);
        Gl.UniformMatrix4fv(_gl.LocView, 1, 0, view);
        Gl.Uniform2f(_gl.LocProjXY, p00, p11);
        Gl.Uniform2f(_gl.LocZ, near, far);
        Gl.Uniform1i(_gl.LocCount, _gl.Uploaded);
        Gl.Uniform1f(_gl.LocReach, _binReach);
        Gl.Uniform1i(_gl.LocBinCap, _binCap);
        Gl.Uniform2f(_gl.LocBinAtten, _atten.X, _atten.Y);
        // the hot partition reads the same mover spheres and dyn pages the lit stage will, so they go up first
        fixed (float* dp = CasterPass.CubeDynPage) Gl.Uniform1fv(_gl.LocDynPage, CasterPass.MaxCubes, dp);
        Gl.Uniform2f(_gl.LocHot, Lightmap.Movers.V ? 1f : 0f, Shadows.CubeDist.V);
        // R4: 0 unless a water film can exist this frame, and then the build is what it always was
        Gl.Uniform1i(_gl.LocReflK, Weather.ReflK);
        // a viewport under one pixel per tile breaks the tile-to-NDC mapping the bounds rest on: no bounds then
        bool boundsOk = vpW >= GridX && vpH >= GridY;
        _builtW = vpW; _builtH = vpH; _builtNear = near; _builtFar = far;
        Gl.Uniform2f(_gl.LocRel, boundsOk ? _rel : 0f, ReachUniform);
        // CX-188 budget: light.floor and the master gain, so the build's per-light reach cut is the lit stage's
        // -- conservatively: cx_lightbuild's ReachCut takes the sRGB luma and skips uDayDim, both of which make
        // its lum LARGER than the walk's, so the build can only ever bin a light the walk then windows out.
        Gl.Uniform2f(_gl.LocFloor, LightFloor, _gain);
        Gl.Uniform2f(_gl.LocPad, 2f / Math.Max(vpW, 1), 2f / Math.Max(vpH, 1));
        Gl.Uniform1i(_gl.LocTally, tally && _tallyFrame ? 1 : 0);
        UploadMovers();
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindLights, _gl.BufLights);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCounts, _gl.BufCounts);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIndices, _gl.BufIndices);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _gl.BufStats);
        Gl.DispatchCompute((ClusterCount + GroupSize - 1) / GroupSize, 1, 1);
        // BUFFER_UPDATE: TakeClusterOverflow copies _gl.BufStats out, which SHADER_STORAGE alone does not order (GL 4.6 7.13)
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);

        if (timing)
        {
            Gl.EndQuery(Gl.TIME_ELAPSED);
            if (slot == 0) _gl.QueryPending0 = true; else _gl.QueryPending1 = true;
            _queryAt = 1 - slot;
        }
        // harvest BOTH slots: a slot whose result is never collected stays pending forever and the timer
        // silently stops reporting
        HarvestQuery(0);
        HarvestQuery(1);
        return true;
    }

    /// <summary>Bind the cluster buffers for the lit draw (the compute pass leaves them bound, but a draw can
    /// follow a pass where the build was skipped).</summary>
    internal static void BindForDraw()
    {
        if (!Ready)
        {
            // shared points: left alone, the lit stages would read the last compute pass's scratch as lights
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindLights, 0);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCounts, 0);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIndices, 0);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMovers, 0);
            PairShadows.BindAtlas();
            return;
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindLights, _gl.BufLights);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCounts, _gl.BufCounts);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIndices, _gl.BufIndices);
        UploadMovers();
        // CX-188 step 4: the pair table and the tile atlas. Here rather than at one pass's bind, because
        // CxPairVis is reachable from every program that lights (cx_localshadow includes it) and the table's
        // own uPairN is the gate that keeps a frame with no pairs from reading either.
        PairShadows.BindAtlas();
    }

    /// <summary>The mover spheres, once per rewrite, bound where both the cluster build and the lit stage read them.</summary>
    private static void UploadMovers()
    {
        if (_gl.MoversGen != CasterPass.CubeDynSphereGen)
        {
            _gl.MoversGen = CasterPass.CubeDynSphereGen;
            fixed (float* sp = CasterPass.CubeDynSphere)
                Gl.NamedBufferSubData(_gl.BufMovers, IntPtr.Zero, (IntPtr)(CasterPass.CubeDynSphere.Length * sizeof(float)), sp);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMovers, _gl.BufMovers);
    }

    /// <summary>The cluster lookup (grid, tile, depth range) and the light curve into the pass block,
    /// for the viewport and depth range this pass draws with. Each curve term is its own member, so a lane
    /// added here never changes what an older one means.</summary>
    /// <returns>Whether the pass draws the view the cluster table was built for (<c>uBounds</c> 1).</returns>
    internal static bool FillDraw(byte* b, int* viewport, float near, float far)
    {
        if (near < MinNear) near = MinNear;
        *(Vector4*)(b + AlloyRenderer.PbTile) = new Vector4(viewport[0], viewport[1],
            MathF.Max((float)viewport[2] / GridX, 1f), MathF.Max((float)viewport[3] / GridY, 1f));
        int* grid = (int*)(b + AlloyRenderer.PbGrid);
        grid[0] = GridX; grid[1] = GridY; grid[2] = GridZ;
        *(float*)(b + AlloyRenderer.PbReach) = ReachUniform;
        LastFalloff = new Vector3(_gain, _srcRadius, _falloffPow);
        *(Vector3*)(b + AlloyRenderer.PbFalloff) = LastFalloff;
        *(float*)(b + AlloyRenderer.PbWrap) = _wrap;
        *(Vector2*)(b + AlloyRenderer.PbZ) = new Vector2(near, far > near ? far : near * 2f);
        *(Vector2*)(b + AlloyRenderer.PbAtten) = new Vector2(_atten.X, _atten.Y);
        *(Vector2*)(b + AlloyRenderer.PbHot) = new Vector2(_hot, MathF.Max(_hotSize, 1e-4f));
        *(Vector2*)(b + AlloyRenderer.PbSss) = new Vector2(_sss, SssOn ? SssK(_sss) : 0f);
        *(float*)(b + AlloyRenderer.PbWrapN) = _wrapN;
        *(float*)(b + AlloyRenderer.PbDayDim) = DayDim;
        *(Vector2*)(b + AlloyRenderer.PbRelevance) = new Vector2(_rel, _rel * 2f);
        bool own = !BorrowedTable && viewport[2] == _builtW && viewport[3] == _builtH && near == _builtNear
                   && (far > near ? far : near * 2f) == _builtFar;
        *(float*)(b + AlloyRenderer.PbBounds) = own ? 1f : 0f;
        return own;
    }

    /// <summary>The local-cube receiver's three softness terms -- PCF radius in texels, the screen
    /// radius in pixels below which that disc stands down, and the world distance inside which a fragment is
    /// reported lit outright. Read here so <c>/shadow</c> retunes all three live, like every other knob.</summary>
    internal static float ShadowSoft => _shadowSoft;
    /// <summary><c>uLocalFloor</c> -- the shadow floor scaled by the area ambient's luminance (a mid-grey
    /// ambient gives the full floor, a night area a fraction of it, so darkness stays dark). ponytail: a
    /// luminance scalar, not a per-channel ambient tint; go to a vec3 if a coloured ambient reads wrong.</summary>
    internal static float ShadowFloor
    {
        get
        {
            var a = FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient * AmbientScale : new Vector3(AlloyRenderer.DefaultAmbient);
            float lum = 0.2126f * a.X + 0.7152f * a.Y + 0.0722f * a.Z;
            return Shadows.Floor.V * Math.Clamp(2f * lum, 0f, 1f);
        }
    }
    internal static float ShadowDiscPx => _discPx;
    /// <summary>The penumbra scale -- the light's source radius (<c>/light radius</c>), in world units.</summary>
    internal static float ShadowSource => _srcRadius;
    /// <summary>Cube-shadow taps a fragment may pay, in cluster order; 0 = every shadowing light.</summary>
    internal static float ShadowCap => Shadows.Cap.V;
    /// <summary><c>shadow.perpixel</c> -- how many lights a PIXEL may shadow at all (uShBudget).
    /// Every light past the budget lights the pixel unshadowed; 0 = no budget.</summary>
    internal static float ShadowPerPixel => Shadows.PerPixel.V;
    /// <summary><c>light.floor</c> -- the absolute display-linear brightness a light's reach ends at
    /// (uLightFloor), as against <see cref="BinReach"/>'s share of its authored radius. 0 = no cut.</summary>
    internal static float LightFloor => Lighting.Floor.V;
    /// <summary><c>light.fullmodel</c> -- the share of a pixel's brightest light above which a light keeps the
    /// near-field core, the source-sized lobe and the subsurface leak (uFullModel). 0 = every light keeps all
    /// three, which is the pre-LOD body term for term.</summary>
    internal static float FullModel => Lighting.FullModel.V;
    /// <summary><c>shadow.cubedist</c>: the view depth past which a fragment reads no cube (uLocalPcss.w; 0 = none).</summary>
    internal static float ShadowDist => Shadows.CubeDist.V;
    /// <summary>EXPERIMENT: the fraction of a light's adjusted radius the cluster build bins it by. The
    /// engine's curve is under 1/255 of its peak past ~0.8R, so lights binned by the full R sit in froxels
    /// they cannot visibly light; every one of them still costs the full per-light body per fragment.
    /// Read with <c>/alloy viz 38</c> (lights per pixel). 1 = the full radius.</summary>
    private static float _binReach => Lighting.BinReach.V;
    /// <summary><c>/light reachfade</c>: 1 = the lit stage windows every light to its binned reach (see
    /// <c>uReach</c>), 0 = the bin cut alone.</summary>
    internal static float ReachUniform => Lighting.ReachFade.V ? _binReach : 0f;
    internal static float BinReach => _binReach;
    /// <summary>EXPERIMENT: the most lights a froxel keeps -- the K strongest by the engine's curve at the
    /// froxel's nearest point times the colour's luma, vanilla's per-object list made per froxel. 0 = all.
    /// The seam risk is a light dropped in one froxel and kept in its neighbour, which draws a grid edge: the
    /// weakest kept light must be invisible for the cap to be.</summary>
    private static int _binCap => Lighting.BinCap.V;
    /// <summary>Adjusted radius of the uploaded lights this frame -- min, mean, max -- for the period line.
    /// The plaza heatmap read 32+ lights per pixel; whether that is many lights or huge radii is this number.</summary>
    private static float _radMin, _radMean, _radMax;
    internal static float ShadowNearWorld => _shadowNear * _srcRadius;

    /// <summary>Non-blocking timer + overflow readback: only ever touched when the driver says the result is
    /// already there, so the render thread never stalls on the GPU.</summary>
    private static void HarvestQuery(int slot)
    {
        bool pending = slot == 0 ? _gl.QueryPending0 : _gl.QueryPending1;
        if (!pending || _gl.Query[slot] == 0 || Gl.GetQueryObjectiv == null) return;
        int avail; Gl.GetQueryObjectiv(_gl.Query[slot], Gl.QUERY_RESULT_AVAILABLE, &avail);
        if (avail == 0) return;
        ulong ns; Gl.GetQueryObjectui64v(_gl.Query[slot], Gl.QUERY_RESULT, &ns);
        LastBuildMs = ns * 1e-6f;
        if (slot == 0) _gl.QueryPending0 = false; else _gl.QueryPending1 = false;
    }

    /// <summary>Copy out + reset the GPU-side stats, and return the overflow count copied out two calls ago (a period
    /// late; the entry totals beside it land in the period line). Once a period; it was a <c>glGetBufferSubData</c>,
    /// which waited out the whole frame.</summary>
    internal static uint TakeClusterOverflow()
    {
        if (!Ready || !GpuReadback.EntryPoints || !_gl.StatsRead.Ensure(StatsWords * sizeof(uint))) return 0;
        int w = _statsSeq % GpuReadback.Slots, r = (_statsSeq + 1) % GpuReadback.Slots;
        _statsSeq++;
        _gl.StatsRead.Copy(_gl.BufStats, 0, w, 0, StatsWords * sizeof(uint));
        _gl.StatsRead.Fence(w);
        uint* zero = stackalloc uint[StatsWords] { 0, 0, 0, 0 };
        Gl.NamedBufferSubData(_gl.BufStats, IntPtr.Zero, (IntPtr)(StatsWords * sizeof(uint)), zero);
        uint* v = (uint*)_gl.StatsRead.Ready(r);
        if (v == null) return 0;
        _tallyBinned = v[1]; _tallyKept = v[2]; _tallyCand = v[3];
        return v[0];
    }
    private static int _statsSeq;

    /// <summary>One-line description of the rig's state for the Alloy draw period log.</summary>
    internal static string Diag(uint overflow) =>
        $"{DescribeCurve()}, " +
        $"lights {LastLightCount} ({FrameInputs.LocalLightCount} flagged castShadows, " +
        $"{FrameInputs.LightsWithCube} with a cube; adjusted radius {_radMin:F1}/{_radMean:F1}/{_radMax:F1}u min/mean/max)" +
        $", cluster build {LastBuildMs:F3}ms on {GridX}x{GridY}x{GridZ} (binreach {_binReach:F2}, bincap {_binCap})" +
        (_tallyBinned > 0
            ? $", relevance bounds: a fragment walks {100.0 * _tallyKept / _tallyBinned:F0}% of the binned entries at most and its brightest-light search {100.0 * _tallyCand / _tallyBinned:F0}% (sampled builds)"
            : "") +

        (overflow > 0 ? $", CLUSTER OVERFLOW {overflow}/period (cap {ClusterCap})" : "");

    private static bool Ensure()
    {
        if (_gl.Prog != 0) return true;
        if (_failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindBufferBase == null
            || Gl.GenQueries == null || Gl.GetQueryObjectui64v == null)
        { _failed = true; _log?.Info("AlloyLights: compute/SSBO entry points unresolved -- Alloy lighting unavailable"); return false; }

        uint prog = GlProgram.Compute(ShdClusterCs, ShaderStages.Source(ShdClusterCs), _log,
                                      "AlloyLights cluster build");
        if (prog == 0) { _failed = true; return false; }
        _gl.LocView = GlProgram.Loc(prog, "uView"); _gl.LocProjXY = GlProgram.Loc(prog, "uProjXY");
        _gl.LocZ = GlProgram.Loc(prog, "uZ"); _gl.LocCount = GlProgram.Loc(prog, "uLightCount");
        _gl.LocReach = GlProgram.Loc(prog, "uReach");
        _gl.LocBinCap = GlProgram.Loc(prog, "uBinCap"); _gl.LocBinAtten = GlProgram.Loc(prog, "uAtten");
        _gl.LocGrid = GlProgram.Loc(prog, "uGrid");
        _gl.LocDynPage = GlProgram.Loc(prog, "uDynPage"); _gl.LocHot = GlProgram.Loc(prog, "uHot");
        _gl.LocReflK = GlProgram.Loc(prog, "uReflK");
        _gl.LocRel = GlProgram.Loc(prog, "uRel"); _gl.LocPad = GlProgram.Loc(prog, "uPad"); _gl.LocTally = GlProgram.Loc(prog, "uTally");
        _gl.LocFloor = GlProgram.Loc(prog, "uLightFloor");

        uint b;
        Gl.CreateBuffers(1, &b); _gl.BufLights = b;
        Gl.NamedBufferData(_gl.BufLights, (IntPtr)(InitialLights * 8 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _gl.SsboLights = InitialLights;
        Gl.CreateBuffers(1, &b); _gl.BufCounts = b;
        Gl.NamedBufferData(_gl.BufCounts, (IntPtr)(ClusterCount * ClusterHeadWords * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        Gl.CreateBuffers(1, &b); _gl.BufIndices = b;
        Gl.NamedBufferData(_gl.BufIndices, (IntPtr)((long)ClusterCount * ClusterCap * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        // a reader can run before the first cluster build (the bake): stale memory there is a light count in the millions
        Gl.ZeroBuffer(_gl.BufCounts, (long)ClusterCount * ClusterHeadWords * sizeof(uint));
        Gl.ZeroBuffer(_gl.BufIndices, (long)ClusterCount * ClusterCap * sizeof(uint));
        Gl.CreateBuffers(1, &b); _gl.BufMovers = b;
        Gl.NamedBufferData(_gl.BufMovers, (IntPtr)(CasterPass.CubeDynSphere.Length * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _gl.MoversGen = -1;
        Gl.CreateBuffers(1, &b); _gl.BufStats = b;
        uint* zero = stackalloc uint[StatsWords] { 0, 0, 0, 0 };
        Gl.NamedBufferData(_gl.BufStats, (IntPtr)(StatsWords * sizeof(uint)), zero, Gl.DYNAMIC_DRAW);

        fixed (uint* q = _gl.Query) Gl.GenQueries(2, q);
        _gl.Prog = prog;
        _log?.Info($"AlloyLights: cluster build ready -- {GridX}x{GridY}x{GridZ} froxels, " +
                   $"{ClusterCap} lights/cluster, {InitialLights}-light SSBO (grown on demand -- no ceiling)");
        return true;
    }

    /// <summary>sRGB -&gt; linear exponent, verified against <c>RenderInterface::GetColorSpaceFromSRGB</c>
    /// (the constant it reads is 2.2). Shared with <see cref="FrameRead"/>, which applies
    /// the same curve CPU-side to the area terms.</summary>
    internal const float SrgbGamma = 2.2f;
    /// <summary>Added to a static light's cube slot in the record's w (GLSL <c>CX_LIGHT_STATIC</c>).</summary>
    internal const float LightStatic = 256f;
    /// <summary>A static light's set index PLUS ONE rides above the static bit in multiples of this (GLSL
    /// <c>CX_LIGHT_SET</c>); 0 = no set entry. Exact in a float up to 2^24 / 512 = 32768 lights.</summary>
    internal const float LightSetStride = 512f;

    /// <summary>The local-light cube receiver. Composed BEFORE <see cref="FragmentLib"/> because the
    /// per-light loop in there calls it; the cubes come from <see cref="CasterPass"/>, six faces per light in
    /// one <c>samplerCubeArrayShadow</c>. Every geometric input comes from <c>uLocalLight</c>, the position
    /// and far plane the cube was BAKED with -- never the live light, which has moved since.</summary>
    internal static string LocalShadowLib => ShaderSource.Get("cx_localshadow.glsl");

    /// <summary>GLSL the lit fragment stage includes verbatim: the cluster lookup plus the per-light loop.
    /// Kept here so the binning rule (grid dims, exponential slice mapping, SSBO layout) has exactly one
    /// source, and the attenuation curves are the engine's, so a seam between an owned and an engine-drawn
    /// part falls off the same way.</summary>
    internal static string FragmentLib => ShaderSource.Get("cx_lightfrag.glsl");

    /// <summary>The sun cascade receiver, included by the lit fragment stage after
    /// <see cref="FragmentLib"/>. The maps come from <see cref="CasterPass"/>, which publishes the matrix each
    /// layer was actually drawn with, so this only ever projects. Selection is "the first cascade whose box
    /// holds the fragment", innermost first: concentric fits nest, so the first hit is also the sharpest.</summary>
    internal static string SunShadowLib => ShaderSource.Get("cx_sunshadow.glsl");

    /// <summary>Resref the cluster build is looked up under before the embedded stage is used. An override
    /// may carry the same <c>$CAP$</c>/<c>$GROUP$</c> tokens the embedded source does -- see
    /// <see cref="ShaderResource.Tokens"/> -- because the CPU sizes its buffers from those same constants.</summary>
    internal const string ShdClusterCs = "cx_cluster_cs";
}
