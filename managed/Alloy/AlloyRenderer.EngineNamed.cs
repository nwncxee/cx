using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>One std140 lane array: an engine <c>windPointSources*</c> array, whose element std140 pads out to a
/// whole vec4. A float array's lane uses <c>.X</c>, the position array's <c>.XYZ</c>.</summary>
[InlineArray(AlloyRenderer.MaxWindUniforms)]
internal struct WindLane
{
    private Vector4 _e;
}

/// <summary>The <c>CxEngine</c> block (<c>cx_envblk.glsl</c>): the scene terms the engine's own <c>SetUniforms</c>
/// hands every program by NAME and only a content stage reads -- the module calendar, the frame counter, the
/// weather kind, the mouse, the wind's point sources, the engine's projection and the solid-noise handle. Every
/// content environment declares the block, so a stage reads what it would in the engine.</summary>
internal struct EngineBlock
{
    public Matrix4x4 Proj;
    public WindLane Pos, Radius, Duration, Intensity, Remaining;
    public Int2 Mouse;
    public UInt2 Noise;
    public int MouseButtons, WindCount;
    public int Year, Month, Day, Hour, Dawn, Dusk, MinutesPerHour, Frame, WeatherType;
    public float IntoTransition, TransitionTime, WeatherDensity;

    /// <summary>The GLSL against this struct. A member moved in either file stages every term after it into its
    /// neighbour -- a stage would read the calendar out of the wind.</summary>
    internal static void Pin(Checker check)
    {
        var b = default(EngineBlock);
        Std140.Pin(check, ShaderSource.Get("cx_envblk.glsl"), "CxEngine", UboSlots.Engine, Unsafe.SizeOf<EngineBlock>(),
        [
            ("uEngProj", Std140.At(ref b, ref b.Proj)),
            ("uWindPos", Std140.At(ref b, ref b.Pos)), ("uWindRadius", Std140.At(ref b, ref b.Radius)),
            ("uWindDuration", Std140.At(ref b, ref b.Duration)), ("uWindIntensity", Std140.At(ref b, ref b.Intensity)),
            ("uWindRemain", Std140.At(ref b, ref b.Remaining)),
            ("uMouse", Std140.At(ref b, ref b.Mouse)), ("uConNoise", Std140.At(ref b, ref b.Noise)),
            ("uMouseBtn", Std140.At(ref b, ref b.MouseButtons)), ("uWindCount", Std140.At(ref b, ref b.WindCount)),
            ("uModYear", Std140.At(ref b, ref b.Year)), ("uModMonth", Std140.At(ref b, ref b.Month)),
            ("uModDay", Std140.At(ref b, ref b.Day)), ("uModHour", Std140.At(ref b, ref b.Hour)),
            ("uModDawn", Std140.At(ref b, ref b.Dawn)), ("uModDusk", Std140.At(ref b, ref b.Dusk)),
            ("uModMinutes", Std140.At(ref b, ref b.MinutesPerHour)), ("uSceneFrame", Std140.At(ref b, ref b.Frame)),
            ("uWeatherType", Std140.At(ref b, ref b.WeatherType)),
            ("uModTransition", Std140.At(ref b, ref b.IntoTransition)),
            ("uModTransTime", Std140.At(ref b, ref b.TransitionTime)),
            ("uWeatherDensity", Std140.At(ref b, ref b.WeatherDensity)),
        ]);
    }
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
        public readonly UniformBlock<EngineBlock> Blk = new();
        public Matrix4x4 Proj;
        public ulong Noise;
        public int Frame = -1, Count;
    }

    private static EngineNamedState _engineU = new();

    /// <summary>Context loss: the block's buffer belonged to the context that went, and so did the stamp
    /// saying it holds this frame's terms. The owner is replaced whole, as every other block's is.</summary>
    internal static void EngineBlockContextLost() => _engineU = new EngineNamedState();

    /// <summary>Off-device: the context-loss slot leaves a NEW owner with a clear fill stamp. Oracle: the
    /// reset's contract, written out here -- a buffer minted by the dead context must not be rebound, and a
    /// stamp that still reads "filled this frame" is what would rebind it.</summary>
    internal static bool EngineBlockSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        EngineNamedState was = _engineU;
        was.Frame = 0; was.Count = 7;
        EngineBlockContextLost();
        check.Expect(!ReferenceEquals(_engineU, was),
                     "the context-loss slot kept the CxEngine owner, so its dead buffer name survives the loss");
        check.Expect(!ReferenceEquals(_engineU.Blk, was.Blk), "...or it kept the block object inside it");
        check.Expect(_engineU.Frame == -1 && _engineU.Count == 0,
                     "the replacement owner's fill stamp is not clear, so the first bind after a loss would rebind a dead buffer");
        log.Info(check.Ok ? "AlloyRenderer.EngineBlockSelfCheck: passed (the CxEngine owner goes with the context)"
                          : "AlloyRenderer.EngineBlockSelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Fill the <c>CxEngine</c> block out of the engine's live state and bind it on
    /// <see cref="UboSlots.Engine"/>. The calendar, weather and mouse come off <see cref="Rt"/>, the frame
    /// counter is the presented frame's, the projection this pass's and the point sources Alloy's own wind's.
    /// Allocates nothing.</summary>
    internal static void BindEngineBlock()
    {
        FillEngineBlock();   // Vulkan takes the staged block (EngineBlockStaged)
    }

    /// <summary>The <c>CxEngine</c> block <see cref="BindEngineBlock"/> binds, staged and not bound. The noise term is a
    /// GL bindless handle.</summary>
    internal static ref EngineBlock EngineBlockStaged()
    {
        FillEngineBlock();
        return ref _engineU.Blk.Stage;
    }

    /// <summary>Stage the block for this frame and pass; true when nothing changed since the last stage.</summary>
    private static bool FillEngineBlock()
    {
        EngineNamedState e = _engineU;
        ulong h = Bindless ? HandleFor(FrameSnapshot.OwnNoise()) : 0ul;
        if (e.Frame == _frameSeq && e.Proj == _projMtx && e.Noise == h) return true;
        bool fresh = e.Frame != _frameSeq;
        e.Frame = _frameSeq; e.Proj = _projMtx; e.Noise = h;
        ref EngineBlock b = ref e.Blk.Stage;
        b.Proj = _projMtx;
        b.Frame = _frameSeq;
        b.Noise = new UInt2((uint)h, (uint)(h >> 32));
        if (_runtime != null)
        {
            ShaderExposedRuntimeData rt = Rt;
            b.Year = rt.ModuleYear; b.Month = rt.ModuleMonth; b.Day = rt.ModuleDay; b.Hour = rt.ModuleHour;
            b.Dawn = rt.ModuleDawnHour; b.Dusk = rt.ModuleDuskHour; b.MinutesPerHour = rt.ModuleMinutesPerHour;
            b.IntoTransition = rt.ModuleTimeIntoTransition; b.TransitionTime = rt.ModuleTransitionTime;
            b.WeatherType = rt.AreaWeatherType; b.WeatherDensity = rt.AreaWeatherDensity;
            b.Mouse = new Int2(rt.MouseX, rt.MouseY); b.MouseButtons = rt.MouseButtons;
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
                b.Pos[i] = live ? new Vector4(pos[i], 0f) : default;
                b.Radius[i] = new Vector4(live ? radius[i] : 0f, 0f, 0f, 0f);
                b.Duration[i] = new Vector4(live ? duration[i] : 0f, 0f, 0f, 0f);
                b.Intensity[i] = new Vector4(live ? intensity[i] : 0f, 0f, 0f, 0f);
                b.Remaining[i] = new Vector4(live ? remaining[i] : 0f, 0f, 0f, 0f);
            }
        }
        b.WindCount = e.Count;
        return false;
    }
}
