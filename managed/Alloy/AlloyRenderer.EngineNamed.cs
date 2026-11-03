using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>One engine <c>windPointSourcesPosition</c> array under scalar block layout: an element costs its own
/// twelve bytes.</summary>
[InlineArray(AlloyRenderer.MaxWindUniforms)]
internal struct WindPosLane
{
    private Vector3 _e;
}

/// <summary>One engine <c>windPointSources*</c> float array under scalar block layout: an element costs its own
/// four bytes.</summary>
[InlineArray(AlloyRenderer.MaxWindUniforms)]
internal struct WindLane
{
    private float _e;
}

/// <summary>One <c>windPointSources*</c> array under std140, which pads every element out to a whole vec4. A float
/// array's lane uses <c>.X</c>, the position array's <c>.XYZ</c>.</summary>
[InlineArray(AlloyRenderer.MaxWindUniforms)]
internal struct WindPadLane
{
    private Vector4 _e;
}

/// <summary>Everything the <c>CxEngine</c> block holds past the wind lanes -- the mouse, the noise handle, the module
/// calendar, the frame counter and the weather. Every member is four-byte aligned and lands at the same offset within
/// this run under either block layout, so both staging structs share it.</summary>
internal struct EngineTail
{
    public Int2 Mouse;
    public UInt2 Noise;
    public int MouseButtons, WindCount;
    public int Year, Month, Day, Hour, Dawn, Dusk, MinutesPerHour, Frame, WeatherType;
    public float IntoTransition, TransitionTime, WeatherDensity;
}

/// <summary>The <c>CxEngine</c> block (<c>cx_envblk.glsl</c>) as std140 lays it out: the scene terms the engine's own
/// <c>SetUniforms</c> hands every program by NAME and only a content stage reads -- the module calendar, the frame
/// counter, the weather kind, the mouse, the wind's point sources, the engine's projection and the solid-noise
/// handle. A device without scalar block layout reads the block from here.</summary>
internal struct EngineBlock
{
    public Matrix4x4 Proj;
    public WindPadLane Pos, Radius, Duration, Intensity, Remaining;
    public EngineTail Tail;
}

/// <summary>The same block as scalar block layout lays it out, which is what the frame fills: the five parallel wind
/// lanes hold 512 B of data each instead of 2 KB, and the block is about 3.7 KB rather than 10.3 KB.</summary>
internal struct EngineBlockPacked
{
    public Matrix4x4 Proj;
    public WindPosLane Pos;
    public WindLane Radius, Duration, Intensity, Remaining;
    public EngineTail Tail;

    /// <summary>The filled block for a device without scalar block layout: each wind lane element padded out to a
    /// whole vec4, which is where std140 has the shader read it.</summary>
    internal static void ToStd140(in EngineBlockPacked src, ref EngineBlock dst)
    {
        dst.Proj = src.Proj;
        for (int i = 0; i < AlloyRenderer.MaxWindUniforms; i++)
        {
            dst.Pos[i] = new Vector4(src.Pos[i], 0f);
            dst.Radius[i] = new Vector4(src.Radius[i], 0f, 0f, 0f);
            dst.Duration[i] = new Vector4(src.Duration[i], 0f, 0f, 0f);
            dst.Intensity[i] = new Vector4(src.Intensity[i], 0f, 0f, 0f);
            dst.Remaining[i] = new Vector4(src.Remaining[i], 0f, 0f, 0f);
        }
        dst.Tail = src.Tail;
    }

    /// <summary>The GLSL against both staging structs, under both layouts a device may hand the block. A member
    /// moved in either file stages every term after it into its neighbour -- a stage would read the calendar out of
    /// the wind.</summary>
    internal static void Pin(Checker check)
    {
        string src = ShaderSource.Get("cx_envblk.glsl");
        var b = default(EngineBlockPacked);
        Std140.Pin(check, src, "CxEngine", UboSlots.Engine, Unsafe.SizeOf<EngineBlockPacked>(),
        [
            ("uEngProj", Std140.At(ref b, ref b.Proj)),
            ("uWindPos", Std140.At(ref b, ref b.Pos)), ("uWindRadius", Std140.At(ref b, ref b.Radius)),
            ("uWindDuration", Std140.At(ref b, ref b.Duration)), ("uWindIntensity", Std140.At(ref b, ref b.Intensity)),
            ("uWindRemain", Std140.At(ref b, ref b.Remaining)),
            .. TailPins(Std140.At(ref b, ref b.Tail), ref b.Tail),
        ], BlockLayout.Scalar);
        var p = default(EngineBlock);
        Std140.Pin(check, src, "CxEngine", UboSlots.Engine, Unsafe.SizeOf<EngineBlock>(),
        [
            ("uEngProj", Std140.At(ref p, ref p.Proj)),
            ("uWindPos", Std140.At(ref p, ref p.Pos)), ("uWindRadius", Std140.At(ref p, ref p.Radius)),
            ("uWindDuration", Std140.At(ref p, ref p.Duration)), ("uWindIntensity", Std140.At(ref p, ref p.Intensity)),
            ("uWindRemain", Std140.At(ref p, ref p.Remaining)),
            .. TailPins(Std140.At(ref p, ref p.Tail), ref p.Tail),
        ]);
    }

    /// <summary>The tail's members, each at <paramref name="at"/> plus its own offset inside <paramref name="t"/>.</summary>
    private static (string Name, int Offset)[] TailPins(int at, ref EngineTail t) =>
    [
        ("uMouse", at + Std140.At(ref t, ref t.Mouse)), ("uConNoise", at + Std140.At(ref t, ref t.Noise)),
        ("uMouseBtn", at + Std140.At(ref t, ref t.MouseButtons)), ("uWindCount", at + Std140.At(ref t, ref t.WindCount)),
        ("uModYear", at + Std140.At(ref t, ref t.Year)), ("uModMonth", at + Std140.At(ref t, ref t.Month)),
        ("uModDay", at + Std140.At(ref t, ref t.Day)), ("uModHour", at + Std140.At(ref t, ref t.Hour)),
        ("uModDawn", at + Std140.At(ref t, ref t.Dawn)), ("uModDusk", at + Std140.At(ref t, ref t.Dusk)),
        ("uModMinutes", at + Std140.At(ref t, ref t.MinutesPerHour)), ("uSceneFrame", at + Std140.At(ref t, ref t.Frame)),
        ("uWeatherType", at + Std140.At(ref t, ref t.WeatherType)),
        ("uModTransition", at + Std140.At(ref t, ref t.IntoTransition)),
        ("uModTransTime", at + Std140.At(ref t, ref t.TransitionTime)),
        ("uWeatherDensity", at + Std140.At(ref t, ref t.WeatherDensity)),
    ];
}

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The point sources the engine uploads at most (<c>SetUniforms</c> clamps the list to 0x80).</summary>
    internal const int MaxWindUniforms = 128;

    /// <summary>The block and what it was last filled from: the frame, this pass's projection and the noise
    /// handle, plus the count the gust walk returned. Only the projection moves within a frame -- the engine
    /// refreshes its runtime struct once at the frame's head, in
    /// <c>GLRender::CheckAndUpdateUniformDirtyDataForFrame</c> -- so an unchanged refill is skipped.</summary>
    private sealed class EngineNamedState
    {
        public readonly UniformBlock<EngineBlockPacked> Blk = new();
        /// <summary>Where the std140 expansion is staged, taken on the first stage for a device without scalar
        /// block layout and never on one that offers it, the block being 10 KB of padding to hold.</summary>
        public UniformBlock<EngineBlock>? Pad;
        public Matrix4x4 Proj;
        public ulong Noise;
        public int Frame = -1, Count;
    }

    private static EngineNamedState _engineU = new();

    /// <summary>Device loss: the staged block describes a frame that is gone, and so does the stamp saying it
    /// holds that frame's terms. The owner is replaced whole, as every other block's is.</summary>
    internal static void EngineBlockContextLost() => _engineU = new EngineNamedState();

    /// <summary>Off-device: the loss slot leaves a NEW owner with a clear fill stamp. Oracle: the reset's
    /// contract, written out here -- a stamp that still reads "filled this frame" would serve the dead
    /// device's terms to the next one.</summary>
    internal static bool EngineBlockSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        EngineNamedState was = _engineU;
        was.Frame = 0; was.Count = 7;
        EngineBlockContextLost();
        check.Expect(!ReferenceEquals(_engineU, was),
                     "the loss slot kept the CxEngine owner, so the dead device's terms survive the loss");
        check.Expect(!ReferenceEquals(_engineU.Blk, was.Blk), "...or it kept the block object inside it");
        check.Expect(_engineU.Frame == -1 && _engineU.Count == 0,
                     "the replacement owner's fill stamp is not clear, so the first fill after a loss would be skipped");
        log.Info(check.Ok ? "AlloyRenderer.EngineBlockSelfCheck: passed (the CxEngine owner goes with the context)"
                          : "AlloyRenderer.EngineBlockSelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Fill the <c>CxEngine</c> block out of the engine's live state, for the frame to stage on
    /// <see cref="UboSlots.Engine"/>. The calendar, weather and mouse come off <see cref="Rt"/>, the frame
    /// counter is the presented frame's, the projection this pass's and the point sources Alloy's own wind's.
    /// Allocates nothing.</summary>
    internal static void BindEngineBlock()
    {
        FillEngineBlock();   // Vulkan takes the staged block (EngineBlockStaged)
    }

    /// <summary>The <c>CxEngine</c> block <see cref="BindEngineBlock"/> fills, as staged.</summary>
    internal static ref EngineBlockPacked EngineBlockStaged()
    {
        FillEngineBlock();
        return ref _engineU.Blk.Stage;
    }

    /// <summary>The staged block's length, as the device's own block layout lays it out.</summary>
    internal static int EngineBytes => VkGlsl.ScalarBlocks ? sizeof(EngineBlockPacked) : sizeof(EngineBlock);

    /// <summary>Block <paramref name="b"/> as the bytes this device reads it from: the packed block itself where the
    /// device offers scalar block layout, else its std140 expansion, staged in the block's own owner.</summary>
    internal static ReadOnlySpan<byte> EngineBlockBytes(in EngineBlockPacked b)
    {
        if (VkGlsl.ScalarBlocks) return MemoryMarshal.AsBytes(new ReadOnlySpan<EngineBlockPacked>(in b));
        ref EngineBlock pad = ref (_engineU.Pad ??= new()).Stage;
        EngineBlockPacked.ToStd140(in b, ref pad);
        return MemoryMarshal.AsBytes(new ReadOnlySpan<EngineBlock>(in pad));
    }

    /// <summary>Stage the block for this frame and pass; true when nothing changed since the last stage.</summary>
    private static bool FillEngineBlock()
    {
        EngineNamedState e = _engineU;
        ulong h = Bindless ? HandleFor(FrameSnapshot.OwnNoise()) : 0ul;
        if (e.Frame == _frameSeq && e.Proj == _projMtx && e.Noise == h) return true;
        bool fresh = e.Frame != _frameSeq;
        e.Frame = _frameSeq; e.Proj = _projMtx; e.Noise = h;
        ref EngineBlockPacked b = ref e.Blk.Stage;
        ref EngineTail t = ref b.Tail;
        b.Proj = _projMtx;
        t.Frame = _frameSeq;
        t.Noise = new UInt2((uint)h, (uint)(h >> 32));
        if (_runtime != null)
        {
            ShaderExposedRuntimeData rt = Rt;
            t.Year = rt.ModuleYear; t.Month = rt.ModuleMonth; t.Day = rt.ModuleDay; t.Hour = rt.ModuleHour;
            t.Dawn = rt.ModuleDawnHour; t.Dusk = rt.ModuleDuskHour; t.MinutesPerHour = rt.ModuleMinutesPerHour;
            t.IntoTransition = rt.ModuleTimeIntoTransition; t.TransitionTime = rt.ModuleTransitionTime;
            t.WeatherType = rt.AreaWeatherType; t.WeatherDensity = rt.AreaWeatherDensity;
            t.Mouse = new Int2(rt.MouseX, rt.MouseY); t.MouseButtons = rt.MouseButtons;
        }
        if (fresh)
        {
            Span<Vector3> pos = stackalloc Vector3[MaxWindUniforms];
            Span<float> radius = stackalloc float[MaxWindUniforms], duration = stackalloc float[MaxWindUniforms],
                        intensity = stackalloc float[MaxWindUniforms], remaining = stackalloc float[MaxWindUniforms];
            e.Count = AlloyWind.PointSources(pos, radius, duration, intensity, remaining);
            for (int i = 0; i < MaxWindUniforms; i++)
            {
                bool live = i < e.Count;
                b.Pos[i] = live ? pos[i] : default;
                b.Radius[i] = live ? radius[i] : 0f;
                b.Duration[i] = live ? duration[i] : 0f;
                b.Intensity[i] = live ? intensity[i] : 0f;
                b.Remaining[i] = live ? remaining[i] : 0f;
            }
        }
        t.WindCount = e.Count;
        return false;
    }
}
