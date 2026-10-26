using System;
using CX.Framework;
using CX.Rhi;

namespace CX.Graphics.Rhi;

/// <summary>The seam on GL 4.6: handles over GL names, pipelines linked through <see cref="GlProgram"/> (so the
/// binary cache and content overrides still apply), and a recorded list executed as GL calls in record order.
/// Made, used and dropped on the thread holding Alloy's context; a lost context drops the device whole.</summary>
internal sealed unsafe partial class GlDevice : IDevice
{
    private readonly record struct GlBuffer(uint Name, BufferDesc Desc);
    /// <summary><see cref="Borrowed"/>: a name another owner made and frees; <see cref="Destroy(ImageId)"/> only drops the id.</summary>
    private readonly record struct GlImage(uint Name, uint Target, ImageDesc Desc, bool Borrowed = false);
    private readonly record struct GlPipeline(uint Program, PipelineDesc Desc);

    /// <summary>Refusals logged in full before the rest are only counted, so a stale handle drawn every frame
    /// says so without flooding the log.</summary>
    private const int LoggedFaults = 16;

    private readonly ExtenderLog _log;
    private readonly int _thread;
    private readonly uint _emptyVao;
    private readonly HandleTable<GlBuffer> _buffers = new();
    private readonly HandleTable<GlImage> _images = new();
    private readonly HandleTable<uint> _samplers = new();
    private readonly HandleTable<ShaderDesc> _shaders = new();
    private readonly HandleTable<GlPipeline> _pipelines = new();
    private int _faults;

    public DeviceCaps Caps { get; }

    /// <summary>Refusals since the device was made.</summary>
    public int Faults => _faults;

    /// <summary>The device over the context current on this thread, or null -- logged -- when the driver lacks an
    /// entry point the backend calls, so a pass stands down instead of calling through null.</summary>
    public static GlDevice? TryCreate(ExtenderLog log)
    {
        if (Gl.Enablei == null || Gl.Disablei == null || Gl.BlendFuncSeparatei == null || Gl.BlendEquationi == null
            || Gl.ColorMaski == null || Gl.BindTextureUnit == null || Gl.BindImageTexture == null || Gl.CreateTextures == null
            || Gl.CreateBuffers == null || Gl.CreateVertexArrays == null || Gl.NamedBufferStorage == null
            || Gl.TextureStorage2D == null || Gl.TextureStorage3D == null || Gl.TextureStorage2DMultisample == null
            || Gl.GenSamplers == null || Gl.DeleteSamplers == null || Gl.ClearBufferfv == null || Gl.ClearBufferuiv == null
            || Gl.CopyImageSubData == null || Gl.MultiDrawElementsIndirect == null || Gl.MultiDrawElementsIndirectCount == null
            || Gl.DrawArraysInstancedBaseInstance == null || Gl.DrawElementsInstancedBaseVertexBaseInstance == null
            || Gl.VertexArrayElementBuffer == null || Gl.GetTextureLevelParameteriv == null || Gl.InvalidateBufferData == null
            || Gl.GetActiveUniform == null || Gl.GetUniformiv == null)
        {
            log.Always("GlDevice: REFUSED -- the driver lacks a GL 4.6 entry point the seam's backend calls");
            return null;
        }
        return new GlDevice(log);
    }

    private GlDevice(ExtenderLog log)
    {
        _log = log;
        _thread = Environment.CurrentManagedThreadId;
        uint vao;
        Gl.CreateVertexArrays(1, &vao);
        _emptyVao = vao;
        float aniso = 1;
        if (Gl.GetFloatv != null) Gl.GetFloatv(Gl.MAX_TEXTURE_MAX_ANISOTROPY, &aniso);
        Caps = new DeviceCaps(
            Int(Gl.MAX_TEXTURE_SIZE), Int(Gl.MAX_ARRAY_TEXTURE_LAYERS), Int(Gl.MAX_COLOR_ATTACHMENTS), Int(Gl.MAX_SAMPLES),
            Int(Gl.MAX_TEXTURE_IMAGE_UNITS), Int(Gl.MAX_COMBINED_TEXTURE_IMAGE_UNITS),
            Int(Gl.MAX_SHADER_STORAGE_BUFFER_BINDINGS), Int(Gl.MAX_VERTEX_SHADER_STORAGE_BLOCKS),
            Int(Gl.UNIFORM_BUFFER_OFFSET_ALIGNMENT), Int(Gl.SHADER_STORAGE_BUFFER_OFFSET_ALIGNMENT), aniso);
        log.Info($"GlDevice: {Caps}");
    }

    private static int Int(uint pname) { int v = 0; Gl.GetIntegerv(pname, &v); return v; }

    public BufferId CreateBuffer(in BufferDesc d)
    {
        if (!OnThread("CreateBuffer") || d.Bytes <= 0) return Refused<BufferId>($"buffer {d.Label}: {d.Bytes} bytes");
        uint b;
        Gl.CreateBuffers(1, &b);
        Gl.NamedBufferStorage(b, (IntPtr)d.Bytes, null, d.Memory == MemoryKind.Upload ? Gl.DYNAMIC_STORAGE_BIT : 0);
        return new BufferId(_buffers.Add(new GlBuffer(b, d)));
    }

    public ImageId CreateImage(in ImageDesc d)
    {
        GlFormats.Row f = GlFormats.Of(d.Fmt);
        if (!OnThread("CreateImage") || f.Internal == 0 || d.W <= 0 || d.H <= 0)
            return Refused<ImageId>($"image {d.Label}: {d.Fmt} {d.W}x{d.H}");
        bool ms = d.Kind == ImageKind.Tex2D && d.Samples > 1;
        uint target = d.Kind switch
        {
            ImageKind.Tex2DArray => Gl.TEXTURE_2D_ARRAY,
            ImageKind.Cube => Gl.TEXTURE_CUBE_MAP,
            ImageKind.CubeArray => Gl.TEXTURE_CUBE_MAP_ARRAY,
            ImageKind.Tex3D => Gl.TEXTURE_3D,
            _ => ms ? Gl.TEXTURE_2D_MULTISAMPLE : Gl.TEXTURE_2D,
        };
        int mips = Math.Max(1, d.Mips), layers = Math.Max(1, d.Layers);
        uint t;
        Gl.CreateTextures(target, 1, &t);
        switch (d.Kind)
        {
            case ImageKind.Tex2DArray: Gl.TextureStorage3D(t, mips, f.Internal, d.W, d.H, layers); break;
            case ImageKind.CubeArray: Gl.TextureStorage3D(t, mips, f.Internal, d.W, d.H, layers * 6); break;
            case ImageKind.Tex3D: Gl.TextureStorage3D(t, mips, f.Internal, d.W, d.H, Math.Max(1, d.D)); break;
            default:
                if (ms) Gl.TextureStorage2DMultisample(t, d.Samples, f.Internal, d.W, d.H, 1);
                else Gl.TextureStorage2D(t, mips, f.Internal, d.W, d.H);
                break;
        }
        int w = 0;
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_WIDTH, &w);
        if (w <= 0)
        {
            Gl.DeleteTextures(1, &t);
            return Refused<ImageId>($"image {d.Label}: the driver refused {d.Fmt} {d.W}x{d.H} storage -- video memory is full");
        }
        if (!ms)
        {
            // A 1-mip texture sampled with no sampler object is incomplete under the default mipmapped min filter.
            RenderTarget.Clamp(t, mips > 1 ? Gl.LINEAR_MIPMAP_LINEAR : Gl.NEAREST);
            if (d.Fmt.IsDepth()) Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_MODE, (int)Gl.NONE);
        }
        return new ImageId(_images.Add(new GlImage(t, target, d)));
    }

    /// <summary>A 2D texture a direct-GL owner made, under an id a recorded pass can bind: the bridge from a migrated
    /// pass to an unmigrated one's output. The device never frees it; a null id when <paramref name="name"/> is 0.</summary>
    internal ImageId Borrow(uint name, string label)
    {
        if (!OnThread("Borrow") || name == 0) return Refused<ImageId>($"borrowed image {label}: GL name {name}");
        var d = new ImageDesc(ImageKind.Tex2D, Format.Unknown, 0, 0, 1, 1, 1, 1, ImageUse.Sampled, label);
        return new ImageId(_images.Add(new GlImage(name, Gl.TEXTURE_2D, d, Borrowed: true)));
    }

    /// <summary>The GL name behind <paramref name="i"/>, for a direct-GL reader of a migrated pass's output; 0 when stale.</summary>
    internal uint Name(ImageId i) => _images.TryGet(i.Value, out GlImage g) ? g.Name : 0;

    public SamplerId CreateSampler(in SamplerDesc d)
    {
        if (!OnThread("CreateSampler")) return default;
        uint s;
        Gl.GenSamplers(1, &s);
        int min = d.Mip switch
        {
            MipMode.Nearest => d.Min == Filter.Linear ? (int)Gl.LINEAR_MIPMAP_NEAREST : (int)Gl.NEAREST_MIPMAP_NEAREST,
            MipMode.Linear => d.Min == Filter.Linear ? (int)Gl.LINEAR_MIPMAP_LINEAR : (int)Gl.NEAREST_MIPMAP_LINEAR,
            _ => d.Min == Filter.Linear ? (int)Gl.LINEAR : (int)Gl.NEAREST,
        };
        int wrap = d.Wrap == Wrap.Clamp ? (int)Gl.CLAMP_TO_EDGE : (int)Gl.REPEAT;
        Gl.SamplerParameteri(s, Gl.TEXTURE_MIN_FILTER, min);
        Gl.SamplerParameteri(s, Gl.TEXTURE_MAG_FILTER, d.Mag == Filter.Linear ? (int)Gl.LINEAR : (int)Gl.NEAREST);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_S, wrap);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_T, wrap);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_R, wrap);
        Gl.SamplerParameteri(s, Gl.TEXTURE_COMPARE_MODE, d.Compare == null ? (int)Gl.NONE : (int)Gl.COMPARE_REF_TO_TEXTURE);
        if (d.Compare is CompareOp c) Gl.SamplerParameteri(s, Gl.TEXTURE_COMPARE_FUNC, (int)Compare(c));
        Gl.SamplerParameterf(s, Gl.TEXTURE_MAX_ANISOTROPY, Math.Clamp(d.Aniso, 1, Caps.MaxAniso));
        Gl.SamplerParameterf(s, Gl.TEXTURE_LOD_BIAS, d.LodBias);
        return new SamplerId(_samplers.Add(s));
    }

    /// <summary>GL links whole programs, so a stage is only held here; <see cref="CreatePipeline"/> compiles it.</summary>
    public ShaderId CreateShader(in ShaderDesc d)
        => OnThread("CreateShader") ? new ShaderId(_shaders.Add(d)) : default;

    public PipelineId CreatePipeline(in PipelineDesc d)
    {
        if (!OnThread("CreatePipeline")) return default;
        string what = d.Label ?? "pipeline";
        if (d.AlphaToCoverage) return Refused<PipelineId>($"{what}: alpha-to-coverage is not on the GL backend");
        uint prog;
        if (!d.Cs.IsNull)
        {
            if (!_shaders.TryGet(d.Cs.Value, out ShaderDesc cs)) return Refused<PipelineId>($"{what}: stale compute stage");
            prog = cs.Ref != null ? GlProgram.Compute(cs.Ref, cs.Source, _log, what) : GlProgram.Compute(cs.Source, _log, what);
        }
        else
        {
            if (!_shaders.TryGet(d.Vs.Value, out ShaderDesc vs) || !_shaders.TryGet(d.Fs.Value, out ShaderDesc fs))
                return Refused<PipelineId>($"{what}: a graphics pipeline needs a live vertex and fragment stage");
            if (!d.Gs.IsNull)
            {
                if (!_shaders.TryGet(d.Gs.Value, out ShaderDesc gs)) return Refused<PipelineId>($"{what}: stale geometry stage");
                prog = GlProgram.Graphics(vs.Source, gs.Source, fs.Source, _log, what);
            }
            else if (vs.Ref != null && fs.Ref != null) prog = GlProgram.Graphics(vs.Ref, vs.Source, fs.Ref, fs.Source, _log, what);
            else prog = GlProgram.Graphics(vs.Source, fs.Source, _log, what);
        }
        if (prog == 0) return Refused<PipelineId>($"{what}: the program did not link (logged above)");
        if (SharedUnit(prog) is string clash)
        {
            Gl.DeleteProgram(prog);
            return Refused<PipelineId>($"{what}: {clash} -- a stage lost a layout(binding), and the seam sets no sampler uniform");
        }
        return new PipelineId(_pipelines.Add(new GlPipeline(prog, d)));
    }

    /// <summary>Two active samplers of <paramref name="prog"/> on one texture unit, named; null when each has its own.
    /// Cold. ponytail: sampler2D only, the one type a migrated pass samples; the others join with their first pass.</summary>
    private static string? SharedUnit(uint prog)
    {
        int n = 0;
        Gl.GetProgramiv(prog, Gl.ACTIVE_UNIFORMS, &n);
        byte* name = stackalloc byte[128];
        var first = new System.Collections.Generic.Dictionary<int, string>();
        for (uint u = 0; u < n; u++)
        {
            int len = 0, size = 0;
            uint type = 0;
            Gl.GetActiveUniform(prog, u, 128, &len, &size, &type, name);
            if (type != Gl.SAMPLER_2D || size != 1) continue;
            string s = System.Text.Encoding.ASCII.GetString(name, len);
            int unit = -1;
            Gl.GetUniformiv(prog, Gl.GetUniformLocation(prog, name), &unit);
            if (first.TryGetValue(unit, out string? had)) return $"{had} and {s} both sample unit {unit}";
            first[unit] = s;
        }
        return null;
    }

    public void Destroy(BufferId b)
    {
        if (OnThread("Destroy") && _buffers.Remove(b.Value, out GlBuffer g)) GpuGraveyard.Retire(GpuGraveyard.Kind.Buffer, g.Name);
    }

    public void Destroy(ImageId i)
    {
        if (!OnThread("Destroy") || !_images.Remove(i.Value, out GlImage g)) return;
        Evict(i);   // the framebuffers go ahead of it, so neither outlives the other's name
        if (!g.Borrowed) GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, g.Name);
    }

    public void Destroy(SamplerId s)
    {
        if (OnThread("Destroy") && _samplers.Remove(s.Value, out uint n)) GpuGraveyard.Retire(GpuGraveyard.Kind.Sampler, n);
    }

    public void Destroy(ShaderId s) { if (OnThread("Destroy")) _shaders.Remove(s.Value, out _); }

    public void Destroy(PipelineId p)
    {
        if (OnThread("Destroy") && _pipelines.Remove(p.Value, out GlPipeline g)) Gl.DeleteProgram(g.Program);
    }

    private bool OnThread(string what)
        => Environment.CurrentManagedThreadId == _thread
        || Fault($"{what} on thread {Environment.CurrentManagedThreadId}; the device belongs to thread {_thread}");

    private T Refused<T>(string why) where T : struct { Fault(why); return default; }

    /// <summary>Log a refusal (the first <see cref="LoggedFaults"/> in full) and return false.</summary>
    private bool Fault(string why)
    {
        if (++_faults <= LoggedFaults)
            _log.Always($"GlDevice: REFUSED {why}" + (_faults == LoggedFaults ? " -- further refusals are only counted" : ""));
        return false;
    }

    internal static uint Compare(CompareOp c) => c switch
    {
        CompareOp.Less => Gl.LESS,
        CompareOp.Equal => Gl.EQUAL,
        CompareOp.Greater => Gl.GREATER,
        CompareOp.Always => Gl.ALWAYS,
        _ => Gl.LEQUAL,
    };
}
