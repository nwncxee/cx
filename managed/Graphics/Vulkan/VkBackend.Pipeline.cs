using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>A compiled pipeline, shared by every desc that differs from another only in what a bind restates: its
    /// handle (the worker's optimised one once swapped in), how many ids name it, and the key it is shared under.</summary>
    private sealed class PipeObj(VkPipeline handle, string? key)
    {
        public VkPipeline Handle = handle;
        public readonly string? Key = key;
        public int Refs = 1;
        public bool Optimised, Dead;
        /// <summary>What a graphics pipeline was built from, with the two state bits a key folds in and the label its
        /// handle is named by, so the backend can build it again at another specialization. Unset for a compute one.</summary>
        public PipelineDesc Desc;
        public bool Dyn;
        public string What = "";
    }

    /// <summary>Compiled graphics pipelines by <see cref="PipeKey"/>: a second desc with the same key names the first one's.
    /// The key holds stage handles, whose generation changes when a slot is reused, so a stage made after another was
    /// destroyed never meets its pipelines; an entry goes with the last id naming it.</summary>
    private readonly Dictionary<string, PipeObj> _shared = new();

    /// <summary>The device makes blend enable, equation and write mask dynamic; and whether the backend uses it.</summary>
    private readonly bool _canDynBlend;
    private bool _dynBlendOn = true;

    /// <summary>Blend is dynamic state: one pipeline serves every blend mode, and a bind restates the desc's.</summary>
    internal bool DynamicBlend { get => _canDynBlend && _dynBlendOn; set => _dynBlendOn = value; }

    /// <summary>The open line's words for the blend path the device took.</summary>
    internal static string BlendPathLine(bool dynamic)
        => dynamic ? "blend is dynamic state, one pipeline for every blend mode" : "blend is baked into each pipeline";

    /// <summary>The specialization every stage takes: the bindless seat count on <see cref="VkGlsl.BindlessCapConstant"/>
    /// and whether a diagnostic view is drawn on <see cref="VkGlsl.VizOnConstant"/>, in a block on the caller's stack,
    /// which the create call reads and lets go. A stage without a constant ignores it.</summary>
    private struct SpecBlock
    {
        public VkSpecializationInfo Info;
        public VkSpecializationMapEntry Cap, Viz;
        public uint CapValue, VizValue;
    }

    /// <summary><paramref name="b"/> filled with the device's seat count and the view now drawn, its info pointing
    /// into itself.</summary>
    private void Specialize(SpecBlock* b)
    {
        b->CapValue = _bindlessCap;
        b->VizValue = _specViz;
        b->Cap = new VkSpecializationMapEntry { constantID = VkGlsl.BindlessCapConstant, offset = 0, size = sizeof(uint) };
        b->Viz = new VkSpecializationMapEntry { constantID = VkGlsl.VizOnConstant, offset = sizeof(uint), size = sizeof(uint) };
        b->Info = new VkSpecializationInfo
        {
            mapEntryCount = 2, pMapEntries = &b->Cap, dataSize = 2 * sizeof(uint), pData = &b->CapValue,
        };
    }

    /// <summary>1 while a diagnostic view is drawn, 0 otherwise: the value every pipeline in the cache carries now.</summary>
    private uint _specViz;

    /// <summary>Which turn of <see cref="_specViz"/> the pipelines in the cache were built at, so an optimised link
    /// the worker finished against the turn before it is dropped instead of swapped in.</summary>
    private int _specGen;

    /// <summary>A diagnostic view is being drawn, so every stage's view ladder has to be live. Off, the ladder and
    /// the terms only a view reads fold out of each pipeline, which is what the whole cache is rebuilt for when this
    /// turns over -- the cost of one chat command, paid once, and said out loud.</summary>
    internal bool ShaderViz
    {
        get => _specViz != 0;
        set
        {
            uint want = value ? 1u : 0u;
            if (want == _specViz || !OnThread("ShaderViz")) return;
            _specViz = want;
            _specGen++;
            Respecialize(value);
        }
    }

    /// <summary>Every shared graphics pipeline built again at the specialization now set, its handle swapped under the
    /// ids that name it the way an optimised link's is. The libraries go with them: a library carries the
    /// specialization of the build that made it. A pipeline whose stages are gone keeps the handle it has, and says so.</summary>
    private void Respecialize(bool viz)
    {
        LibraryState l = _lib;
        foreach (VkPipeline lib in l.Libs.Values) l.Grave.Add(lib);
        l.Libs.Clear();
        l.ByShader.Clear();
        int made = 0, kept = 0;
        foreach (PipeObj o in _shared.Values)
        {
            if (o.Dead) continue;
            if (!_shaders.TryGet((o.Desc.Ms.IsNull ? o.Desc.Vs : o.Desc.Ms).Value, out _) || !_shaders.TryGet(o.Desc.Fs.Value, out _))
            {
                kept++;
                _log.Always($"VkBackend: {o.What} keeps the view ladder it was built with -- the stages it was built from are gone");
                continue;
            }
            bool linked = Libraries && o.Desc.Ms.IsNull;
            VkPipeline fresh = linked ? Linked(o.Desc, o.Dyn, o.What) : Build(o.Desc, o.Dyn, Part.Whole, null, 0, o.What);
            if (fresh.IsNull) { kept++; continue; }
            VkPipeline old = o.Handle;
            o.Handle = fresh;
            o.Optimised = false;
            _pipeCacheDirty = true;
            Name(VkObjectType.Pipeline, fresh.Handle, o.What);
            Later(() => _vkd.vkDestroyPipeline(old));
            made++;
            if (linked) QueueOptimised(o, o.Desc, o.Dyn, o.What);
        }
        _log.Always($"VkBackend: a diagnostic view is {(viz ? "ON" : "off")} -- {made} pipeline(s) rebuilt with the view "
                  + $"ladder {(viz ? "live" : "folded out")}"
                  + (kept > 0 ? $"; {kept} could not be rebuilt (each said so above)" : ""));
    }

    /// <summary>A compute pipeline over <paramref name="src"/>; null (logged) when the text or the pipeline is
    /// refused. The module goes once the pipeline holds it. A caller whose stage content may override reads the
    /// override itself, where the defines that reach the text are applied.</summary>
    internal PipelineId CreateCompute(string src, string label)
    {
        ShaderId cs = CreateShader(new ShaderDesc(ShaderStage.Compute, src, label));
        if (cs.IsNull) return default;
        PipelineId pipe = CreatePipeline(new PipelineDesc { Cs = cs, Label = label });
        Destroy(cs);
        return pipe;
    }

    /// <summary>A compute pipeline over SPIR-V already compiled or loaded; null (logged) when the pipeline is refused.
    /// The module goes once the pipeline holds it, so a caller keeps the pipeline and no shader handle.</summary>
    internal PipelineId CreateCompute(byte[] spirv, string label)
    {
        ShaderId cs = CreateShader(ShaderStage.Compute, spirv, label);
        if (cs.IsNull) return default;
        PipelineId pipe = CreatePipeline(new PipelineDesc { Cs = cs, Label = label });
        Destroy(cs);
        return pipe;
    }

    /// <summary>A pipeline on the one shared layout. Graphics pipelines bake the target formats for dynamic rendering
    /// and leave viewport, scissor, depth, raster and (where the device can) blend dynamic, as a bind restates them; a
    /// desc whose compiled pipeline already exists shares it.</summary>
    public PipelineId CreatePipeline(in PipelineDesc d)
    {
        if (!OnThread("CreatePipeline")) return default;
        string what = d.Label ?? "pipeline";
        VkPipeline pipe;
        long since = Stopwatch.GetTimestamp();
        if (!d.Cs.IsNull)
        {
            if (!_shaders.TryGet(d.Cs.Value, out VkShader cs)) return Refused<PipelineId>($"{what}: stale compute stage");
            SpecBlock spec;
            Specialize(&spec);
            var ci = new VkComputePipelineCreateInfo { stage = Stage(cs, &spec.Info), layout = _pipeLayout };
            _pipeCacheDirty = true;
            if (_vkd.vkCreateComputePipelines(_pipeCache, 1, &ci, &pipe) != VkResult.Success)
                return Refused<PipelineId>($"{what}: vkCreateComputePipelines refused");
            Tallied(since, false, what);
            Name(VkObjectType.Pipeline, pipe.Handle, what);
            return new PipelineId(_pipelines.Add(new VkPipe(new PipeObj(pipe, null), true, default, default)));
        }
        bool mesh = !d.Ms.IsNull;
        if (mesh && !_mesh) return Refused<PipelineId>($"{what}: a mesh pipeline on a device without mesh shaders ({_meshWhy})");
        if (!_shaders.TryGet((mesh ? d.Ms : d.Vs).Value, out _) || !_shaders.TryGet(d.Fs.Value, out _))
            return Refused<PipelineId>($"{what}: a graphics pipeline needs a live vertex or mesh stage and a fragment stage");
        if (!d.Ts.IsNull && !_shaders.TryGet(d.Ts.Value, out _)) return Refused<PipelineId>($"{what}: stale task stage");
        if (!d.Gs.IsNull && !_shaders.TryGet(d.Gs.Value, out _)) return Refused<PipelineId>($"{what}: stale geometry stage");
        if (!d.Layout.IsNull && !_layouts.TryGet(d.Layout.Value, out _)) return Refused<PipelineId>($"{what}: stale vertex layout");
        if (d.Raster.DepthClamp && !_depthClamp) return Refused<PipelineId>($"{what}: this device has no depth clamp");
        bool dyn = DynamicBlend;
        string key = PipeKey(d, dyn);
        if (_shared.TryGetValue(key, out PipeObj? obj)) obj.Refs++;
        else
        {
            // ponytail: a mesh pipeline is built whole on the frame, once per device, against ADR 0054's library link; libraries would cover it once the pre-raster part takes task and mesh stages
            bool linked = Libraries && !mesh;
            _pipeCacheDirty = true;
            pipe = linked ? Linked(d, dyn, what) : default;
            if (linked && pipe.IsNull)
            {
                _librariesOn = linked = false;
                _log.Always($"VkBackend: REFUSED the pipeline library path at {what} (logged above) -- every pipeline is built "
                          + "whole on the frame from here on, as on a device without libraries");
            }
            if (!linked) pipe = Build(d, dyn, Part.Whole, null, 0, what);
            if (pipe.IsNull) return default;
            Tallied(since, d.Prewarmed, what);
            Name(VkObjectType.Pipeline, pipe.Handle, what);
            obj = new PipeObj(pipe, key) { Desc = d, Dyn = dyn, What = what };
            _shared[key] = obj;
            if (linked) QueueOptimised(obj, d, dyn, what);
        }
        return new PipelineId(_pipelines.Add(new VkPipe(obj, false, d.Depth, d.Raster, dyn, d.Blend, d.Targets.ColorCount, d.Targets.Colors[0])));
    }

    /// <summary>What a compiled pipeline is made of: its four parts' keys, so a pipeline and the libraries it links from
    /// are told apart by the same things. Depth, cull, winding and bias are restated per bind, and blend when dynamic.</summary>
    private static string PipeKey(in PipelineDesc d, bool dyn)
    {
        (string vi, string pr, string fs, string fo) = PartKeys(d, dyn);
        return $"{vi} | {pr} | {fs} | {fo}";
    }

    /// <summary>The key of each library part: vertex input, the vertex stages, the fragment stage, the output interface.</summary>
    private static (string Vi, string Pr, string Fs, string Fo) PartKeys(in PipelineDesc d, bool dyn)
    {
        return ($"vi {d.Layout.Value} {(int)d.Topo}",
                $"pr {d.Vs.Value} {d.Gs.Value} {d.Ts.Value} {d.Ms.Value} {(d.Raster.DepthClamp ? 'c' : '-')}",
                $"fs {d.Fs.Value} s{Math.Max(d.Targets.Samples, 1)}{(d.AlphaToCoverage ? " a2c" : "")}",
                OutputKey(d, dyn));
    }

    /// <summary>The fragment output interface's part of a key: formats, samples, coverage and the baked blend.</summary>
    private static string OutputKey(in PipelineDesc d, bool dyn)
    {
        var sb = new StringBuilder("o");
        for (int i = 0; i < d.Targets.ColorCount; i++) sb.Append(' ').Append((int)d.Targets.Colors[i]);
        sb.Append(" d").Append((int)d.Targets.Depth).Append(" s").Append(Math.Max(d.Targets.Samples, 1))
          .Append(d.AlphaToCoverage ? " a2c" : "").Append(dyn ? " dyn" : " baked");
        if (!dyn)
            for (int i = 0; i < d.Targets.ColorCount; i++)
            {
                AttachmentBlend a = d.Blend[i];
                sb.Append(' ').Append(a.Enable ? 1 : 0).Append((int)a.SrcRgb).Append((int)a.DstRgb).Append((int)a.SrcA)
                  .Append((int)a.DstA).Append((int)a.Op).Append((int)a.Masked);
            }
        return sb.ToString();
    }

    /// <summary>Which of a graphics pipeline's four library parts one build makes; <see cref="Whole"/> makes all at once.</summary>
    [Flags]
    private enum Part : byte
    {
        /// <summary>No part of its own: a link of libraries others built.</summary>
        None = 0,
        VertexInput = 1, PreRaster = 2, Fragment = 4, Output = 8,
        Whole = VertexInput | PreRaster | Fragment | Output,
    }

    /// <summary>The <paramref name="parts"/> of <paramref name="d"/>'s pipeline: a whole pipeline, or one library part
    /// that keeps what an optimised link needs. Given <paramref name="libs"/> it links those <paramref name="nlibs"/>
    /// libraries instead, optimised when <paramref name="parts"/> is <see cref="Part.Whole"/>. Null (logged) when refused.
    /// On the worker it writes only the cache object, which Vulkan synchronises; the frame thread marks it dirty.</summary>
    private VkPipeline Build(in PipelineDesc d, bool dyn, Part parts, VkPipeline* libs, int nlibs, string what)
    {
        SpecBlock spec;
        Specialize(&spec);
        VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[3];
        int ns = 0;
        if (libs == null && (parts & Part.PreRaster) != 0)
        {
            if (!d.Ts.IsNull) stages[ns++] = Stage(ShaderOf(d.Ts), &spec.Info);
            stages[ns++] = Stage(ShaderOf(d.Ms.IsNull ? d.Vs : d.Ms), &spec.Info);
            if (!d.Gs.IsNull) stages[ns++] = Stage(ShaderOf(d.Gs), &spec.Info);
        }
        if (libs == null && (parts & Part.Fragment) != 0) stages[ns++] = Stage(ShaderOf(d.Fs), &spec.Info);

        int colors = d.Targets.ColorCount;
        VkFormat* fmts = stackalloc VkFormat[RenderPassDesc.MaxColors];
        VkPipelineColorBlendAttachmentState* blend = stackalloc VkPipelineColorBlendAttachmentState[RenderPassDesc.MaxColors];
        for (int i = 0; i < colors; i++)
        {
            fmts[i] = Of(d.Targets.Colors[i]);   // Unknown -> Undefined: a slot the pass routes nowhere
            AttachmentBlend a = d.Blend[i];
            blend[i] = new VkPipelineColorBlendAttachmentState
            {
                blendEnable = a.Enable,
                srcColorBlendFactor = Factor(a.SrcRgb), dstColorBlendFactor = Factor(a.DstRgb),
                srcAlphaBlendFactor = Factor(a.SrcA), dstAlphaBlendFactor = Factor(a.DstA),
                colorBlendOp = Equation(a.Op), alphaBlendOp = Equation(a.Op), colorWriteMask = Mask(a),
            };
        }
        var rendering = new VkPipelineRenderingCreateInfo
        {
            colorAttachmentCount = (uint)colors, pColorAttachmentFormats = fmts,
            depthAttachmentFormat = Of(d.Targets.Depth),
            stencilAttachmentFormat = d.Targets.Depth.HasStencil() ? Of(d.Targets.Depth) : VkFormat.Undefined,
        };
        VertexLayoutDesc layout = default;
        if (libs == null && !d.Layout.IsNull) _layouts.TryGet(d.Layout.Value, out layout);
        int na = layout.Attribs?.Length ?? 0;
        VkVertexInputAttributeDescription* attrs = stackalloc VkVertexInputAttributeDescription[Math.Max(na, 1)];
        for (int i = 0; i < na; i++)
        {
            VertexAttrib a = layout.Attribs![i];
            attrs[i] = new VkVertexInputAttributeDescription { location = a.Location, binding = 0, format = AttribFormat(a), offset = (uint)a.Offset };
        }
        var stream = new VkVertexInputBindingDescription { binding = 0, stride = (uint)layout.Stride, inputRate = VkVertexInputRate.Vertex };
        var vin = new VkPipelineVertexInputStateCreateInfo
        {
            vertexBindingDescriptionCount = na > 0 ? 1u : 0u, pVertexBindingDescriptions = &stream,
            vertexAttributeDescriptionCount = (uint)na, pVertexAttributeDescriptions = attrs,
        };
        var ia = new VkPipelineInputAssemblyStateCreateInfo
        {
            topology = d.Topo == Topology.TriangleStrip ? VkPrimitiveTopology.TriangleStrip : VkPrimitiveTopology.TriangleList,
        };
        var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
        var rs = new VkPipelineRasterizationStateCreateInfo
        {
            depthClampEnable = d.Raster.DepthClamp, polygonMode = VkPolygonMode.Fill,
            cullMode = Cull(d.Raster.Cull), frontFace = Winding(d.Raster.FrontCw), depthBiasEnable = d.Raster.DepthBias, lineWidth = 1,
        };
        var ms = new VkPipelineMultisampleStateCreateInfo
        {
            rasterizationSamples = (VkSampleCountFlags)Math.Max(d.Targets.Samples, 1), alphaToCoverageEnable = d.AlphaToCoverage,
        };
        var ds = new VkPipelineDepthStencilStateCreateInfo
        {
            depthTestEnable = d.Depth.Test, depthWriteEnable = d.Depth.Write, depthCompareOp = Compare(d.Depth.Compare),
        };
        var cb = new VkPipelineColorBlendStateCreateInfo { attachmentCount = (uint)colors, pAttachments = blend };
        VkDynamicState* dynamic = stackalloc VkDynamicState[MaxDynamicStates];
        uint nd = libs == null ? DynamicStatesOf(parts, dyn && colors > 0, dynamic) : 0;
        var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = nd, pDynamicStates = dynamic };

        var lib = new VkGraphicsPipelineLibraryCreateInfoEXT { pNext = &rendering, flags = LibraryFlags(parts) };
        var link = new VkPipelineLibraryCreateInfoKHR { pNext = &rendering, libraryCount = (uint)nlibs, pLibraries = libs };
        VkPipelineCreateFlags flags = VkPipelineCreateFlags.None;
        void* next = &rendering;
        if (libs != null)
        {
            next = &link;
            if (parts == Part.Whole) flags |= VkPipelineCreateFlags.LinkTimeOptimizationEXT;
        }
        else if (parts != Part.Whole)
        {
            next = &lib;
            flags |= VkPipelineCreateFlags.LibraryKHR | VkPipelineCreateFlags.RetainLinkTimeOptimizationInfoEXT;
        }
        var gci = new VkGraphicsPipelineCreateInfo
        {
            pNext = next, flags = flags, stageCount = (uint)ns, pStages = ns > 0 ? stages : null,
            pVertexInputState = &vin, pInputAssemblyState = &ia, pViewportState = &vp, pRasterizationState = &rs,
            pMultisampleState = &ms, pDepthStencilState = &ds, pColorBlendState = &cb, pDynamicState = nd > 0 ? &dy : null,
            layout = _pipeLayout,
        };
        // a library gets only its part's state: NVIDIA bakes a pre-raster library's vertex input, which others share
        Part own = libs != null ? Part.None : parts;
        if ((own & Part.VertexInput) == 0 || !d.Ms.IsNull) { gci.pVertexInputState = null; gci.pInputAssemblyState = null; }
        if ((own & Part.PreRaster) == 0) { gci.pViewportState = null; gci.pRasterizationState = null; }
        if ((own & Part.Fragment) == 0) gci.pDepthStencilState = null;
        if ((own & (Part.Fragment | Part.Output)) == 0) gci.pMultisampleState = null;
        if ((own & Part.Output) == 0) gci.pColorBlendState = null;
        VkPipeline pipe;
        if (_vkd.vkCreateGraphicsPipelines(_pipeCache, 1, &gci, &pipe) == VkResult.Success) return pipe;
        string why = $"{what}: vkCreateGraphicsPipelines refused ({(libs == null ? parts.ToString() : parts == Part.Whole ? "the optimised link" : "the link")})";
        if (libs != null && parts == Part.Whole) _log.Always($"VkBackend: REFUSED {why} -- the linked pipeline stays");
        else Fault(why);
        return default;
    }

    /// <summary>The library-part bits <paramref name="parts"/> names.</summary>
    private static VkGraphicsPipelineLibraryFlagsEXT LibraryFlags(Part parts)
    {
        VkGraphicsPipelineLibraryFlagsEXT f = 0;
        if ((parts & Part.VertexInput) != 0) f |= VkGraphicsPipelineLibraryFlagsEXT.VertexInputInterface;
        if ((parts & Part.PreRaster) != 0) f |= VkGraphicsPipelineLibraryFlagsEXT.PreRasterizationShaders;
        if ((parts & Part.Fragment) != 0) f |= VkGraphicsPipelineLibraryFlagsEXT.FragmentShader;
        if ((parts & Part.Output) != 0) f |= VkGraphicsPipelineLibraryFlagsEXT.FragmentOutputInterface;
        return f;
    }

    /// <summary>The states <paramref name="parts"/> leaves dynamic, each in the part its state belongs to: viewport,
    /// scissor, bias, cull and winding with the vertex stages, depth with the fragment stage, blend with the
    /// output. Written into <paramref name="into"/>; the count returned.</summary>
    private static uint DynamicStatesOf(Part parts, bool dyn, VkDynamicState* into)
    {
        uint n = 0;
        if ((parts & Part.PreRaster) != 0)
        {
            into[n++] = VkDynamicState.Viewport; into[n++] = VkDynamicState.Scissor; into[n++] = VkDynamicState.DepthBias;
            into[n++] = VkDynamicState.CullMode; into[n++] = VkDynamicState.FrontFace; into[n++] = VkDynamicState.DepthBiasEnable;
        }
        if ((parts & Part.Fragment) != 0)
        {
            into[n++] = VkDynamicState.DepthTestEnable; into[n++] = VkDynamicState.DepthWriteEnable; into[n++] = VkDynamicState.DepthCompareOp;
        }
        if (dyn && (parts & Part.Output) != 0)
        {
            into[n++] = VkDynamicState.ColorBlendEnableEXT; into[n++] = VkDynamicState.ColorBlendEquationEXT;
            into[n++] = VkDynamicState.ColorWriteMaskEXT;
        }
        return n;
    }

    /// <summary>Room for every state <see cref="DynamicStatesOf"/> can name at once.</summary>
    private const int MaxDynamicStates = 13;

    /// <summary>A stage the caller has already proven live.</summary>
    private VkShader ShaderOf(ShaderId id) { _shaders.TryGet(id.Value, out VkShader s); return s; }

    private static VkPipelineShaderStageCreateInfo Stage(VkShader s, VkSpecializationInfo* spec)
    {
        var st = new VkPipelineShaderStageCreateInfo
        {
            stage = s.Stage switch
            {
                ShaderStage.Vertex => VkShaderStageFlags.Vertex,
                ShaderStage.Geometry => VkShaderStageFlags.Geometry,
                ShaderStage.Compute => VkShaderStageFlags.Compute,
                ShaderStage.Task => VkShaderStageFlags.TaskEXT,
                ShaderStage.Mesh => VkShaderStageFlags.MeshEXT,
                _ => VkShaderStageFlags.Fragment,
            },
            module = s.Module, pSpecializationInfo = spec,
        };
        fixed (byte* main = "main\0"u8) st.pName = main;   // a u8 literal lives in the image: the pointer outlives the call
        return st;
    }

    internal static VkBlendFactor Factor(BlendFactor f) => f switch
    {
        BlendFactor.Zero => VkBlendFactor.Zero,
        BlendFactor.One => VkBlendFactor.One,
        BlendFactor.SrcAlpha => VkBlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => VkBlendFactor.OneMinusSrcAlpha,
        BlendFactor.SrcColor => VkBlendFactor.SrcColor,
        BlendFactor.OneMinusSrcColor => VkBlendFactor.OneMinusSrcColor,
        BlendFactor.DstColor => VkBlendFactor.DstColor,
        BlendFactor.OneMinusDstAlpha => VkBlendFactor.OneMinusDstAlpha,
        _ => VkBlendFactor.Zero,
    };

    private static VkBlendOp Equation(BlendOp o) => o == BlendOp.Min ? VkBlendOp.Min : VkBlendOp.Add;

    /// <summary>The channels <paramref name="a"/> writes: every one it does not mask.</summary>
    private static VkColorComponentFlags Mask(AttachmentBlend a) => (VkColorComponentFlags)(~(int)a.Masked & (int)ColorMask.All);

    /// <summary>A vertex attribute's Vulkan format; Undefined for a shape the seam does not carry.</summary>
    internal static VkFormat AttribFormat(VertexAttrib a) => (a.Type, a.Components) switch
    {
        (VertexType.Float, 1) => VkFormat.R32Sfloat,
        (VertexType.Float, 2) => VkFormat.R32G32Sfloat,
        (VertexType.Float, 3) => VkFormat.R32G32B32Sfloat,
        (VertexType.Float, 4) => VkFormat.R32G32B32A32Sfloat,
        (VertexType.U8, 4) => VkFormat.R8G8B8A8Uscaled,
        (VertexType.U8Norm, 4) => VkFormat.R8G8B8A8Unorm,
        (VertexType.Packed1010102Norm, 4) => VkFormat.A2B10G10R10SnormPack32,
        _ => VkFormat.Undefined,
    };

    private bool VertexFormatOk(VkFormat f)
    {
        VkFormatProperties p;
        _vki.vkGetPhysicalDeviceFormatProperties(_phys, f, &p);
        return (p.bufferFeatures & VkFormatFeatureFlags.VertexBuffer) != 0;
    }

    /// <summary>The RHI's GL winding as Vulkan's: the clip-to-framebuffer map is the same (positive viewport height), but
    /// Vulkan's area formula carries a minus sign GL's lacks (Vulkan 28.2.1, GL 4.6 14.6.1), so GL's CCW front is Vulkan's CW.</summary>
    internal static VkFrontFace Winding(bool glCw) => glCw ? VkFrontFace.CounterClockwise : VkFrontFace.Clockwise;

    private static VkCullModeFlags Cull(CullMode c)
        => c switch { CullMode.Back => VkCullModeFlags.Back, CullMode.Front => VkCullModeFlags.Front, _ => VkCullModeFlags.None };
}
