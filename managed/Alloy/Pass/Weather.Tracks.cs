using System;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Footsteps in the weather: a creature's real foot contact presses a print into lying snow and kicks
/// a splash off ground the rain wet. Prints go into one world-tiling RGBA32F texture the CPU stamps into, only
/// the dirty rows uploaded; splashes ride the rain-splash draw, gated on the GPU by the area wetness map.
/// ponytail: hook and draw share these arrays with no fence -- double-buffer if a render thread ever starts.</summary>
internal static unsafe partial class Weather
{
    /// <summary>Texels across the track texture, and the metres it repeats over: 2.5 cm a texel. Pinned to
    /// <c>cx_wx_track</c>'s CX_TRACK_SIZE / CX_TRACK_PERIOD_M (<see cref="TrackSelfCheck"/>).</summary>
    internal const int TrackSize = 512;
    internal const float TrackPeriodM = 12.8f;
    /// <summary>Past the 0..15 budget, beside the other weather tiles (Weather.Shelter's units 23..25).</summary>
    internal const uint TrackUnit = 27;
    private const uint GlRgba32f = 0x8814;   // not-an-offset: GL_RGBA32F
    /// <summary>The sole, in metres along the heading: a toe lobe ahead of the foot's centre and a heel lobe
    /// behind it, each an ellipse (half-length along, half-width across).</summary>
    private const float ToeAt = 0.05f, ToeLen = 0.085f, ToeWide = 0.05f, HeelAt = -0.08f, HeelLen = 0.05f, HeelWide = 0.04f;
    /// <summary>Where the planted foot sits off the creature's centre: to one side, alternating per creature, and
    /// ahead, since a footfall is the leading foot coming down.</summary>
    private const float FootSide = 0.1f, FootAhead = 0.15f;
    /// <summary>Snow cover under which there is nothing to press a print into.</summary>
    private const float TrackMinSnow = 0.05f;
    /// <summary>Wetness under which a footfall throws nothing (the GPU's own per-point gate is finer).</summary>
    private const float FootMinWet = 0.05f;
    /// <summary>Footfalls the splash draw carries at once; CX_FOOT_MAX in <c>cx_wx_sphead</c>.</summary>
    internal const int FootMax = 16;
    /// <summary>Crowns one footfall throws; CX_FOOT_CROWNS in <c>cx_wx_spmain</c>.</summary>
    internal const int FootCrowns = 4;
    /// <summary>One foot crown's life and the spread of its births, seconds; CX_FOOT_LIFE / CX_FOOT_STAGGER.</summary>
    private const float FootLifeSec = 0.3f, FootStaggerSec = 0.06f;

    internal static Knob<bool> Tracks = null!;
    internal static Knob<float> TrackFill = null!, FootSplash = null!;

    private static TrackTexel[]? _track;
    /// <summary>Stamped texel rows not yet uploaded, in unwrapped texel coordinates; lo &gt; hi = none.</summary>
    private static int _trackLo = int.MaxValue, _trackHi = int.MinValue;
    private static long _trackEpoch = Stopwatch.GetTimestamp();
    /// <summary>Which foot each recent creature put down last, direct-mapped by id: a bucket that changes hands
    /// restarts its parity rather than sharing it.</summary>
    private static readonly uint[] _sideId = new uint[SideBuckets];
    private static ulong _sideBits;
    private const int SideBuckets = 64;
    /// <summary>Metres from the camera's follow target a print may land: past it, it could only take a texel from
    /// a print nearer the player.</summary>
    private const float TrackRangeM = 24f;
    /// <summary>xyz where a wet foot came down, w when (<see cref="TrackNow"/>'s clock); a ring.</summary>
    private static readonly Vector4[] _foot = new Vector4[FootMax];
    private static readonly Vector4[] _footLive = new Vector4[FootMax];
    private static int _footNext, _prints, _footSplashes;

    private static void DeclareTrackKnobs()
    {
        G.Section("Footsteps");
        Tracks = G.Toggle("tracks", "Footprints in snow", true, Tier.Advanced,
                          help: "Every step a creature takes presses a print into lying snow, which the snow slowly fills back in.");
        FootSplash = G.Slider("footsplash", "Footstep splashes", 0f, 1f, 0.05f, 0.8f, tier: Tier.Advanced,
                              help: "Water kicks up where a creature steps on rain-wet ground: a flick on damp stone, a sheet out of a puddle. Lasts until the ground dries. 0 = off.");
        G.Section("");
        TrackFill = G.Slider("trackfill", "Footprint refill time", 10f, 1800f, 10f, 240f, "s", Tier.ChatOnly,
                             help: "Seconds a footprint takes to fill back in.");
    }

    /// <summary>Seconds since the area edge: the clock prints and foot splashes are stamped in.</summary>
    private static float TrackNow => (float)((Stopwatch.GetTimestamp() - _trackEpoch) / (double)Stopwatch.Frequency);

    /// <summary>A foot coming down at a creature at <paramref name="p"/> facing <paramref name="facing"/>: a print
    /// while snow lies, a splash while the ground is wet. Split from the engine read so the self-check can drive it.</summary>
    private static void FootDown(Vector3 p, Vector2 facing, uint id, float now, bool open)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return;
        Vector2 h = facing.LengthSquared() > 1e-6f ? Vector2.Normalize(facing) : Vector2.UnitY;
        int bit = (int)(id % SideBuckets);
        if (_sideId[bit] != id) { _sideId[bit] = id; _sideBits &= ~(1UL << bit); }
        _sideBits ^= 1UL << bit;
        float side = ((_sideBits >> bit) & 1) != 0 ? FootSide : -FootSide;
        Vector2 at = new Vector2(p.X, p.Y) + h * FootAhead + new Vector2(h.Y, -h.X) * side;
        Vector2 focus = FrameInputs.HasFocus ? new Vector2(FrameInputs.Focus.X, FrameInputs.Focus.Y) : at;
        if (_track != null && Tracks.V && _snow > TrackMinSnow && SnowStrength.V > 0f && Vector2.Distance(at, focus) < TrackRangeM)
        {
            Print(_track, at, h, now, 1f / TrackFill.V, focus, ref _trackLo, ref _trackHi);
            _prints++;
        }
        if (open && FootSplash.V > 0f && WetShown > FootMinWet)
        {
            _foot[_footNext] = new Vector4(at.X, at.Y, p.Z, now);
            _footNext = (_footNext + 1) % FootMax;
        }
    }

    /// <summary>The sole's depth, 0..1, at <paramref name="rel"/> metres from the foot's centre along heading
    /// <paramref name="h"/> (unit): the deeper of the toe and heel lobes, soft over their outer half.</summary>
    internal static float PrintDepth(Vector2 rel, Vector2 h)
    {
        float along = Vector2.Dot(rel, h), across = h.Y * rel.X - h.X * rel.Y;
        return MathF.Max(Lobe(along - ToeAt, across, ToeLen, ToeWide), Lobe(along - HeelAt, across, HeelLen, HeelWide));
        static float Lobe(float a, float c, float len, float wide)
        {
            float r = MathF.Sqrt(a * a / (len * len) + c * c / (wide * wide));
            float t = Math.Clamp((r - 0.55f) / 0.45f, 0f, 1f);
            return 1f - t * t * (3f - 2f * t);
        }
    }

    /// <summary>Stamp one print into <paramref name="t"/>. A texel of the same tile repeat takes it where it is
    /// at least as deep as what the texel still shows; one holding another repeat's print takes it only when
    /// that print has faded or lies farther from <paramref name="focus"/>, so a step one period away never
    /// erases the trail at the player's feet. The rows touched widen <paramref name="lo"/>..<paramref name="hi"/>.</summary>
    internal static void Print(TrackTexel[] t, Vector2 at, Vector2 h, float now, float rate, Vector2 focus, ref int lo, ref int hi)
    {
        const float tpm = TrackSize / TrackPeriodM;
        int cx = (int)MathF.Floor(at.X * tpm), cy = (int)MathF.Floor(at.Y * tpm);
        int r = (int)MathF.Ceiling(MathF.Max(ToeAt + ToeLen, -HeelAt + HeelLen) * tpm) + 1;
        for (int gy = cy - r; gy <= cy + r; gy++)
        for (int gx = cx - r; gx <= cx + r; gx++)
        {
            float d = PrintDepth(new Vector2((gx + 0.5f) / tpm, (gy + 0.5f) / tpm) - at, h);
            if (d <= 0.01f) continue;
            int tx = FloorDiv(gx), ty = FloorDiv(gy);
            ref TrackTexel e = ref t[(gy - ty * TrackSize) * TrackSize + gx - tx * TrackSize];
            if (d < TexelShows(in e, tx, ty, now, rate)) continue;
            if ((e.TileX != tx || e.TileY != ty) && TexelShows(in e, (int)e.TileX, (int)e.TileY, now, rate) > 0f)
            {
                Vector2 held = new(((int)e.TileX * TrackSize + gx - tx * TrackSize + 0.5f) / tpm,
                                   ((int)e.TileY * TrackSize + gy - ty * TrackSize + 0.5f) / tpm);
                Vector2 mine = new((gx + 0.5f) / tpm, (gy + 0.5f) / tpm);
                if (Vector2.DistanceSquared(held, focus) <= Vector2.DistanceSquared(mine, focus)) continue;
            }
            e = new TrackTexel { Depth = d, Stamp = now, TileX = tx, TileY = ty };
            lo = Math.Min(lo, gy); hi = Math.Max(hi, gy);
        }
    }

    /// <summary>What texel <paramref name="e"/> shows to a point in tile (<paramref name="tx"/>, <paramref name="ty"/>):
    /// <c>CxTrackTexel</c>'s read, on the CPU.</summary>
    private static float TexelShows(in TrackTexel e, int tx, int ty, float now, float rate)
        => e.TileX != tx || e.TileY != ty ? 0f : e.Depth * Math.Clamp(1f - (now - e.Stamp) * rate, 0f, 1f);

    private static int FloorDiv(int g) => (int)MathF.Floor(g / (float)TrackSize);

    /// <summary>The print depth a point shows, nearest texel: the self-check's read of what the stage will see.</summary>
    private static float TrackAt(TrackTexel[] t, Vector2 p, float now, float rate)
    {
        const float tpm = TrackSize / TrackPeriodM;
        int gx = (int)MathF.Floor(p.X * tpm), gy = (int)MathF.Floor(p.Y * tpm), tx = FloorDiv(gx), ty = FloorDiv(gy);
        return TexelShows(in t[(gy - ty * TrackSize) * TrackSize + gx - tx * TrackSize], tx, ty, now, rate);
    }

    /// <summary>Per lit bind: the rows stamped since the last one go up, and the texture goes on its unit (0 while
    /// nothing was ever pressed, which the stage reads as no print through <c>uWxRun.w</c>).</summary>
    private static void BindTracks()
    {
        // allocated here, on the first snowy lit bind, never inside the footstep hook
        if (_track == null && Tracks.V && _snow > TrackMinSnow) _track = new TrackTexel[TrackSize * TrackSize];
        if (Renderer.OnVulkan) return;
        if (_track == null || !EnsureTrack()) { Bind(TrackUnit, 0); return; }
        if (_gl.TrackAll || _trackHi >= _trackLo) Upload();
        Bind(TrackUnit, _gl.TrackTex);
    }

    /// <summary>The dirty rows, or all of them, with no pixel-unpack buffer and a tight row length: the engine's
    /// state may hold either, and both reinterpret the client pointer.</summary>
    private static void Upload()
    {
        int svUnpack, svRow;
        Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpack);
        Gl.GetIntegerv(Gl.UNPACK_ROW_LENGTH, &svRow);
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        if (svRow != 0) Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, 0);
        if (_gl.TrackAll || _trackHi - _trackLo + 1 >= TrackSize) TrackRows(0, TrackSize);
        else
        {
            int a = _trackLo - FloorDiv(_trackLo) * TrackSize, b = _trackHi - FloorDiv(_trackHi) * TrackSize;
            if (a <= b) TrackRows(a, b - a + 1);
            else { TrackRows(a, TrackSize - a); TrackRows(0, b + 1); }
        }
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpack);
        if (svRow != 0) Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, svRow);
        _gl.TrackAll = false; _trackLo = int.MaxValue; _trackHi = int.MinValue;
    }

    private static void TrackRows(int row, int n)
    {
        fixed (TrackTexel* first = _track.AsSpan(row * TrackSize))
            Gl.TextureSubImage2D(_gl.TrackTex, 0, 0, row, TrackSize, n, Gl.RGBA, Gl.FLOAT, first);
    }

    private static bool EnsureTrack()
    {
        if (_gl.TrackTex != 0) return true;
        if (Gl.CreateTextures == null || Gl.TextureStorage2D == null || Gl.TextureSubImage2D == null || Gl.TextureParameteri == null) return false;
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.TextureStorage2D(t, 1, GlRgba32f, TrackSize, TrackSize);
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        _gl.TrackTex = t;
        _gl.TrackAll = true;
        return true;
    }

    /// <summary><c>uWxRun.zw</c>: the stamp clock now, and the refill rate (0 = no prints to read).</summary>
    private static Vector2 TrackUniform(bool on) => new(TrackNow, on && Tracks.V && _track != null ? 1f / TrackFill.V : 0f);

    /// <summary>The footfalls still throwing water, compacted into <see cref="_footLive"/> with w = their age.</summary>
    private static int FootLive(float now)
    {
        int n = 0;
        if (FootSplash.V <= 0f) return 0;
        foreach (Vector4 f in _foot)
        {
            float age = now - f.W;
            if (age >= 0f && age < FootLifeSec + FootStaggerSec) _footLive[n++] = new Vector4(f.X, f.Y, f.Z, age);
        }
        return n;
    }

    /// <summary>The area edge: its prints and its splashes stay behind, and the clock restarts.</summary>
    private static void TrackReset()
    {
        if (_track != null) Array.Clear(_track);
        _gl.TrackAll = true; _vks.TrackAll = true; _trackLo = int.MaxValue; _trackHi = int.MinValue;
        _trackEpoch = Stopwatch.GetTimestamp();
        Array.Fill(_foot, new Vector4(0f, 0f, 0f, float.NegativeInfinity));
    }

    private static string TrackStats() => $", {_prints} prints, {_footSplashes} foot splashes{WetStepStats()}";

    /// <summary>The library the lit splice reads the prints through.</summary>
    internal static string TrackLib => ShaderSource.Get("cx_wx_track.glsl");

    /// <summary>A print is shaped like a sole and lies along the heading; it is read back only in its own tile
    /// repeat and fades out over the refill time; consecutive steps alternate feet; a dry or snowless footfall
    /// leaves nothing; and the GLSL sizes are the C# ones.</summary>
    private static void TrackSelfCheck(Checker check)
    {
        Vector2 h = Vector2.UnitX, side = new(0f, -1f);
        check.Expect(PrintDepth(new Vector2(ToeAt, 0f), h) > 0.99f && PrintDepth(new Vector2(HeelAt, 0f), h) > 0.99f
          && PrintDepth(side * 0.08f, h) == 0f && PrintDepth(h * 0.12f, h) > 0f,
          "a print is a sole: deep at toe and heel, longer along the heading than across it");
        var t = new TrackTexel[TrackSize * TrackSize];
        int lo = int.MaxValue, hi = int.MinValue;
        Vector2 at = new(5.03f, 7.01f);
        Print(t, at, h, 10f, 0.01f, at, ref lo, ref hi);
        check.Expect(TrackAt(t, at + h * ToeAt, 10f, 0.01f) > 0.8f && TrackAt(t, at + new Vector2(TrackPeriodM, 0f), 10f, 0.01f) == 0f,
          "a print reads back where it was pressed and not one tile period away");
        check.Expect(TrackAt(t, at + h * ToeAt, 60f, 0.01f) is > 0.3f and < 0.6f && TrackAt(t, at + h * ToeAt, 111f, 0.01f) == 0f,
          "a print fills back in over the refill time");
        check.Expect(lo <= (int)MathF.Floor(at.Y * TrackSize / TrackPeriodM) && hi >= lo && hi - lo < 16,
          $"only the print's own rows are marked for upload ({lo}..{hi})");
        Print(t, new Vector2(0.02f, 0.02f), h, 10f, 0.01f, new Vector2(0.02f, 0.02f), ref lo, ref hi);
        check.Expect(TrackAt(t, new Vector2(-0.04f, 0.02f), 10f, 0.01f) > 0.5f,
          "a print straddling the tile edge lands on both sides of it");
        int lo2 = int.MaxValue, hi2 = int.MinValue;
        Print(t, at + new Vector2(TrackPeriodM, 0f), h, 11f, 0.01f, at, ref lo2, ref hi2);
        check.Expect(TrackAt(t, at + h * ToeAt, 11f, 0.01f) > 0.8f && hi2 < lo2,
          "a step one period away does not take the texels of a live print nearer the player");
        Print(t, at + new Vector2(TrackPeriodM, 0f), h, 11f, 0.01f, at + new Vector2(TrackPeriodM, 0f), ref lo2, ref hi2);
        check.Expect(TrackAt(t, at + new Vector2(TrackPeriodM, 0f) + h * ToeAt, 11f, 0.01f) > 0.8f,
          "...but does once the player stands nearer it");
        check.Expect(TrackLib.Contains($"const int CX_TRACK_SIZE = {TrackSize};") && GlslConst(TrackLib, "CX_TRACK_PERIOD_M") == TrackPeriodM
          && TrackLib.Contains($"layout(binding = {TrackUnit}) uniform sampler2D uWxTrack;"),
          "the stage reads the track texture at the size, period and unit it is stamped at");
        check.Expect((DrySurfaces & (1UL << 6)) == 0 && (DrySurfaces & (1UL << 11)) == 0 && (DrySurfaces & (1UL << 17)) == 0
          && (DrySurfaces & (1UL << 19)) == 0 && (DrySurfaces & (1UL << 9)) == 0 && (DrySurfaces & (1UL << 1)) != 0 && (DrySurfaces & (1UL << 4)) != 0,
          "a wet step replaces only dry-sounding ground: never water, puddles, deep water, snow or carpet");
        string v = SplashVs;
        check.Expect(v.Contains($"const int CX_FOOT_MAX = {FootMax};") && v.Contains($"const int CX_FOOT_CROWNS = {FootCrowns};")
          && GlslConst(v, "CX_FOOT_LIFE") == FootLifeSec && GlslConst(v, "CX_FOOT_STAGGER") == FootStaggerSec,
          "the foot crowns live as long in the stage as the CPU keeps them");

        TrackTexel[]? track = _track; float snow = _snow, wet = _wet; bool en = Enabled.V; ulong sides = _sideBits;
        Vector4[] foot = (Vector4[])_foot.Clone(); int next = _footNext, prints = _prints;
        // the player's cx.cfg is live here: pin every knob the path reads to its declared default
        bool tr = Tracks.V; float fill = TrackFill.V, fs = FootSplash.V, ws = WetStrength.V, ss = SnowStrength.V;
        Tracks.V = true; TrackFill.V = TrackFill.Default; FootSplash.V = FootSplash.Default; WetStrength.V = 1f; SnowStrength.V = 1f;
        _track = null; Enabled.V = true; Array.Clear(_sideId); _snow = 0f; _wet = 0f; Array.Fill(_foot, new Vector4(0f, 0f, 0f, float.NegativeInfinity));
        _track = new TrackTexel[TrackSize * TrackSize];
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 1f, true);
        check.Expect(TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f - FootSide), 1f, 0.01f) == 0f
          && TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f + FootSide), 1f, 0.01f) == 0f && FootLive(1f) == 0,
          "a footfall on dry, bare ground leaves no print and throws no water");
        _snow = 1f;
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 1f, true);
        Vector2 first = Vector2.Zero;
        for (float y = -0.2f; y <= 0.2f; y += 0.025f)
            if (_track != null && TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f + y), 1f, 0.01f) > 0.5f) first = new Vector2(0f, y);
        FootDown(new Vector3(20f, 22f, 0f), h, 7, 1.5f, true);
        Vector2 second = Vector2.Zero;
        for (float y = -0.2f; y <= 0.2f; y += 0.025f)
            if (_track != null && TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 22f + y), 1.5f, 0.01f) > 0.5f) second = new Vector2(0f, y);
        check.Expect(first.Y * second.Y < 0f, $"one creature's steps alternate feet (offsets {first.Y:F3} then {second.Y:F3})");
        _snow = 0f; _wet = 1f;
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 2f, false);
        check.Expect(FootLive(2.1f) == 0, "a wet footfall under shelter throws nothing");
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 2f, true);
        int live = FootLive(2.1f), gone = FootLive(2f + FootLifeSec + FootStaggerSec + 0.01f);
        check.Expect(live == 1 && gone == 0, $"a wet footfall throws one splash for its life and then none ({live}, {gone})");
        _track = track; _snow = snow; _wet = wet; Enabled.V = en; _sideBits = sides; _footNext = next; _prints = prints;
        Array.Copy(foot, _foot, FootMax);
        Tracks.V = tr; TrackFill.V = fill; FootSplash.V = fs; WetStrength.V = ws; SnowStrength.V = ss;
    }
}
