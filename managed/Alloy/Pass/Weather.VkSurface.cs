using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>The weather's surface terms on Vulkan: the decisions GL's ticks make (when to filter, latch, integrate and
/// re-bake) and the terms each compute or filter stage reads, written into the open capture. The images and the
/// recording are the renderer's (<c>AlloyRenderer.VkWxSurface</c>); what they hold is said back here, so the lit
/// stage's gates read one answer on either backend.</summary>
internal static unsafe partial class Weather
{
    /// <summary>What the Vulkan maps hold, replaced whole with the device.</summary>
    private sealed class WeatherVkState
    {
        public bool EsmValid, ShelValid, NoiseDone, MapFailed, RoofSaid;
        /// <summary>The wetness map's side as made; 0 = none.</summary>
        public int MapSize;
        /// <summary>The cover bake the filtered copy holds, and the edge knob it was filtered at.</summary>
        public int FilteredBake = -1;
        public float FilteredSoft = float.NaN;
        /// <summary>The whole print field owes an upload: a new image holds nothing.</summary>
        public bool TrackAll = true;
    }

    private static WeatherVkState _vks = new();

    /// <summary>The filtered cover map holds this cover bake, on whichever backend draws.</summary>
    private static bool EsmOk => _vks.EsmValid;

    /// <summary>The shelter cache holds this cover bake.</summary>
    private static bool ShelOk => _vks.ShelValid;

    /// <summary>The wetness map exists at its fit.</summary>
    private static bool MapHeld => _vks.MapSize > 0;

    /// <summary>A new device: its maps hold nothing, and the wetness map latches again.</summary>
    internal static void VkReset() { _vks = new WeatherVkState(); _mapClear = true; }

    /// <summary>The surface terms are wanted this frame on Vulkan: GL's draw gate, ahead of its map and shelter ticks.</summary>
    internal static bool SurfacesVk => !_off && Enabled.V && !FrameInputs.Interior;

    /// <summary>The cover bake to filter, or -1 when the held copy is current: a new bake, or the edge knob moved.
    /// <paramref name="size"/> is the map's side and <paramref name="layer"/> the sun-array layer it rides.</summary>
    internal static int CoverToFilterVk(out int size, out int layer)
    {
        size = CasterPass.CoverMapSize; layer = CasterPass.CoverLayer;
        if (!Cover.V || !CasterPass.CoverFresh || size <= 0) return -1;
        if (WaterRoof.V && !_vks.RoofSaid)
        {
            _vks.RoofSaid = true;
            _log?.Always("Weather: water does NOT shed snow on Vulkan -- the cover bake has no water roofs there, so the bed "
                       + "under a pond wets and snows (weather.waterroof)");
        }
        int bake = CasterPass.CoverBakes;
        return bake != _vks.FilteredBake || CoverSoft.V != _vks.FilteredSoft ? bake : -1;
    }

    /// <summary>The filter's side for a <paramref name="mapSize"/> cover map, as GL sizes it.</summary>
    internal static int EsmSizeFor(int mapSize) => mapSize < EsmSizeMax ? mapSize : EsmSizeMax;

    /// <summary>One blur axis's terms: the first reads the depth, the second the moment pair.</summary>
    internal static void WriteEsmVk(int locStep, int locFirst, bool first)
    {
        float half = CoverSoft.V * 0.25f / MathF.Max(CoverRange.V, 1f);
        if (first) LooseBlock.Set(locStep, [half / 6f, 0f]);
        else LooseBlock.Set(locStep, [0f, half / 6f]);
        LooseBlock.Set(locFirst, first ? 1 : 0);
    }

    /// <summary>The bake <paramref name="bake"/> was filtered (<paramref name="ok"/>) on Vulkan: the lit stage's
    /// compare terms follow it.</summary>
    internal static void FilteredVk(int bake, bool ok)
    {
        _vks.FilteredBake = bake; _vks.FilteredSoft = CoverSoft.V;
        _vks.EsmValid = ok; _vks.ShelValid = false;
        if (!ok) return;
        EsmTerms(MathF.Max(CoverRange.V, 1f));
        _coverMapSize = CasterPass.CoverMapSize;
        EsmBakes++;
    }

    /// <summary>The compare's spread floor and receiver bias, in the cover view's depth units.</summary>
    private static void EsmTerms(float radius)
    {
        float spread = EsmMinSpreadM / (CoverDepthRadii * radius);
        _esmC = spread * spread;
        _esmBias = EsmBiasM / (CoverDepthRadii * radius);
    }

    /// <summary>The wetness map's step this frame: 0 nothing, 1 clear,
    /// 2 integrate over <paramref name="dt"/>. <paramref name="size"/> is the side the map must have.</summary>
    internal static int MapStepVk(out int size, out float dt)
    {
        size = 0; dt = 0f;
        if (!Map.V || _vks.MapFailed || !SurfacesVk) return 0;
        if (_vks.MapSize == 0 && !(CurrentKind != Kind.None && Density > 0f) && _wet <= 0f && _snow <= 0f) return 0;
        size = MapFit(FrameInputs.AreaExtent, MapRes.V, out float tpm);
        if (size <= 0) return 0;
        if (_vks.MapSize != size)
        {
            _vks.MapSize = size; _mapClear = true;
            _log?.Info($"Weather: wetness map {size}x{size} RGBA32F on Vulkan over {FrameInputs.AreaExtent.X:F0}x{FrameInputs.AreaExtent.Y:F0} m ({tpm:F2} texels/m)");
        }
        _mapTpm = tpm;
        if (_mapArea != FrameInputs.AreaPtr) _mapClear = true;
        long now = Stopwatch.GetTimestamp();
        if (_mapClear)
        {
            _mapClear = false; _mapArea = FrameInputs.AreaPtr; _mapTicks = now;
            return 1;
        }
        if (++_mapFrame < MapCadence.V) return 0;
        _mapFrame = 0;
        dt = MathF.Min((float)(now - _mapTicks) / Stopwatch.Frequency, 0.25f * MapCadence.V);
        _mapTicks = now;
        return dt > 0f ? 2 : 0;
    }

    /// <summary>The device refused the map's image: the map stands down for the session, said once.</summary>
    internal static void MapRefusedVk()
    {
        _vks.MapFailed = true; _vks.MapSize = 0;
        _log?.Always("Weather: the Vulkan wetness map was REFUSED (logged above) -- wetness falls back to the per-fragment estimate");
    }

    /// <summary>The integrate stage's terms, <see cref="Integrate"/>'s on GL.</summary>
    internal static void WriteIntegrateVk(Dictionary<string, int> n, float dt, bool clear)
    {
        int size = _vks.MapSize;
        LooseBlock.Set(Member(n, "uMapSize"), [size, size]);
        bool rain = CurrentKind == Kind.Rain && Density > 0f;
        float soak = rain ? Density / MathF.Max(AccumSec.V, 1e-3f) : 0f;
        float dry = 1f / MathF.Max(DrySec.V, 1e-3f);
        float mode = clear ? -1f : _mapArrive && rain && Arrive.V ? 1f : 0f;
        if (mode > 0f) _mapArrive = false;
        LooseBlock.Set(Member(n, "uStep"), [dt, soak, dry, mode]);
        LooseBlock.Set(Member(n, "uFill"), [PuddleThreshold.V, 1f / MathF.Max(AccumSec.V, 1e-3f), 0.5f / MathF.Max(DrySec.V, 1e-3f), 0f]);
        _mapTicksDone++;
    }

    /// <summary>The scatter's terms for this cover bake, <see cref="MapScatter"/>'s on GL; false when the bake's window
    /// misses the map. <paramref name="x0"/>/<paramref name="y0"/> and the size are the dispatch rect in texels.</summary>
    internal static bool WriteScatterVk(Dictionary<string, int> n, out int x0, out int y0, out int w, out int h)
    {
        x0 = y0 = w = h = 0;
        if (!Map.V || !MapLive || !Matrix4x4.Invert(CasterPass.CoverVp, out Matrix4x4 inv)) return false;
        Vector2 c = new(CasterPass.CoverCentre.X, CasterPass.CoverCentre.Y);
        float reach = CoverRange.V * MapRectSlack;
        int size = _vks.MapSize;
        x0 = Math.Max((int)MathF.Floor((c.X - reach) * _mapTpm), 0); y0 = Math.Max((int)MathF.Floor((c.Y - reach) * _mapTpm), 0);
        int x1 = Math.Min((int)MathF.Ceiling((c.X + reach) * _mapTpm), size), y1 = Math.Min((int)MathF.Ceiling((c.Y + reach) * _mapTpm), size);
        w = x1 - x0; h = y1 - y0;
        if (w <= 0 || h <= 0) return false;
        LooseBlock.Set(Member(n, "uCoverVp"), CasterPass.CoverVp);
        LooseBlock.Set(Member(n, "uCoverVpInv"), inv);
        LooseBlock.Set(Member(n, "uMapXf"), [0f, 0f, _mapTpm, CasterPass.CoverCentre.Z]);
        LooseBlock.Set(Member(n, "uMapSize"), [size, size]);
        LooseBlock.Set(Member(n, "uRectMin"), [x0, y0]);
        LooseBlock.Set(Member(n, "uBasin"), [Basin.V, MathF.Max(PuddleDepth.V * 4f, 0.05f)]);
        _mapScatters++;
        return true;
    }

    /// <summary>The shelter cache's side for a <paramref name="mapSize"/> cover map, or 0 while the cache is off.</summary>
    internal static int ShelterSizeVk(int mapSize) => ShelterCache.V ? Math.Min(mapSize, ShelterSizeMax) : 0;

    /// <summary>Does the held cache want a re-bake: none held, or a knob moved its parameters.</summary>
    internal static bool ShelterStaleVk => ShelterCache.V && _vks.EsmValid && (!_vks.ShelValid || ShelterParams() != _shelBaked);

    /// <summary>The shelter stage's terms over a <paramref name="size"/>-texel cache, <see cref="ShelterBake"/>'s on GL.</summary>
    internal static bool WriteShelterVk(Dictionary<string, int> n, int size)
    {
        if (!Matrix4x4.Invert(CasterPass.CoverVp, out Matrix4x4 inv)) return false;
        Vector4 p = ShelterParams();
        LooseBlock.Set(Member(n, "uCoverVp"), CasterPass.CoverVp);
        LooseBlock.Set(Member(n, "uCoverVpInv"), inv);
        LooseBlock.Set(Member(n, "uSize"), [size, size]);
        LooseBlock.Set(Member(n, "uEdge"), [p.X, p.Y, p.Z, p.W]);
        LooseBlock.Set(Member(n, "uHollow"), HollowStepM / (CoverDepthRadii * MathF.Max(CoverRange.V, 1f)));
        _shelBaked = p;
        return true;
    }

    /// <summary>The cache was baked: the lit stage may read it.</summary>
    internal static void ShelteredVk() { _vks.ShelValid = true; _shelBakes++; }

    /// <summary>The noise tile's side and whether it is still owed.</summary>
    internal static int NoiseSizeVk => NoiseSize;
    internal static bool NoiseOwedVk => !_vks.NoiseDone;
    internal static void NoiseDoneVk() => _vks.NoiseDone = true;

    internal static void WriteNoiseVk(Dictionary<string, int> n) => LooseBlock.Set(Member(n, "uSize"), NoiseSize);

    /// <summary>This frame's ripple rate, 0 while nothing rings; the tile's side.</summary>
    internal static float RippleRateVk => SplashRate(false);
    internal static int RippleSizeVk => RippleSize;

    internal static void WriteRippleVk(Dictionary<string, int> n, float rate)
    {
        LooseBlock.Set(Member(n, "uTimeOfDay"), AlloyRenderer.TimeOfDay);
        LooseBlock.Set(Member(n, "uSize"), RippleSize);
        LooseBlock.Set(Member(n, "uRate"), rate);
        _ripBakes++;
    }

    /// <summary>The print field to upload this frame, whole, or null: nothing pressed, or nothing new.
    /// ponytail: the whole 4 MB field per dirty frame; row ranges as GL sends them if a snowy crowd shows in the frame.</summary>
    internal static ReadOnlySpan<TrackTexel> TrackDirtyVk(bool fresh)
    {
        if (_track == null) return default;
        if (fresh) _vks.TrackAll = true;
        if (!_vks.TrackAll && _trackHi < _trackLo) return default;
        _vks.TrackAll = false; _trackLo = int.MaxValue; _trackHi = int.MinValue;
        return _track;
    }

    /// <summary>The print field's side.</summary>
    internal static int TrackSizeVk => TrackSize;

    /// <summary>A stage's member index for <paramref name="name"/>, or -1 where it declares none.</summary>
    private static int Member(Dictionary<string, int> n, string name) => n.TryGetValue(name, out int i) ? i : -1;
}
