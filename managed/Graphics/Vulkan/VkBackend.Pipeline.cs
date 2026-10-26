using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>A pipeline on the one shared layout. Graphics pipelines bake the target formats for dynamic rendering
    /// and leave viewport, scissor and depth bias dynamic, as the seam records them.</summary>
    public PipelineId CreatePipeline(in PipelineDesc d)
    {
        if (!OnThread("CreatePipeline")) return default;
        string what = d.Label ?? "pipeline";
        VkPipeline pipe;
        if (!d.Cs.IsNull)
        {
            if (!_shaders.TryGet(d.Cs.Value, out VkShader cs)) return Refused<PipelineId>($"{what}: stale compute stage");
            var ci = new VkComputePipelineCreateInfo { stage = Stage(cs), layout = _pipeLayout };
            _pipeCacheDirty = true;
            if (_vkd.vkCreateComputePipelines(_pipeCache, 1, &ci, &pipe) != VkResult.Success)
                return Refused<PipelineId>($"{what}: vkCreateComputePipelines refused");
            return new PipelineId(_pipelines.Add(new VkPipe(pipe, true, default, default)));
        }
        if (!_shaders.TryGet(d.Vs.Value, out VkShader vs) || !_shaders.TryGet(d.Fs.Value, out VkShader fs))
            return Refused<PipelineId>($"{what}: a graphics pipeline needs a live vertex and fragment stage");
        VertexLayoutDesc layout = default;
        if (!d.Layout.IsNull && !_layouts.TryGet(d.Layout.Value, out layout)) return Refused<PipelineId>($"{what}: stale vertex layout");
        if (d.Raster.DepthClamp && !_depthClamp) return Refused<PipelineId>($"{what}: this device has no depth clamp");

        VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[3];
        int ns = 0;
        stages[ns++] = Stage(vs);
        if (!d.Gs.IsNull)
        {
            if (!_shaders.TryGet(d.Gs.Value, out VkShader gs)) return Refused<PipelineId>($"{what}: stale geometry stage");
            stages[ns++] = Stage(gs);
        }
        stages[ns++] = Stage(fs);

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
                colorBlendOp = a.Op == BlendOp.Min ? VkBlendOp.Min : VkBlendOp.Add,
                alphaBlendOp = a.Op == BlendOp.Min ? VkBlendOp.Min : VkBlendOp.Add,
                colorWriteMask = (VkColorComponentFlags)(~(int)a.Masked & (int)ColorMask.All),
            };
        }
        var rendering = new VkPipelineRenderingCreateInfo
        {
            colorAttachmentCount = (uint)colors, pColorAttachmentFormats = fmts,
            depthAttachmentFormat = Of(d.Targets.Depth),
            stencilAttachmentFormat = d.Targets.Depth.HasStencil() ? Of(d.Targets.Depth) : VkFormat.Undefined,
        };
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
        // GL's clip space: depth runs -1..1 in every Alloy projection, and the window maps it to 0..1 as GL does
        var clip = new VkPipelineViewportDepthClipControlCreateInfoEXT { negativeOneToOne = true };
        var vp = new VkPipelineViewportStateCreateInfo { pNext = &clip, viewportCount = 1, scissorCount = 1 };
        var rs = new VkPipelineRasterizationStateCreateInfo
        {
            depthClampEnable = d.Raster.DepthClamp, polygonMode = VkPolygonMode.Fill,
            cullMode = Cull(d.Raster.Cull),
            frontFace = Winding(d.Raster.FrontCw),
            depthBiasEnable = d.Raster.DepthBias, lineWidth = 1,
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
        // depth and raster are dynamic too: a bind restates the desc's, and a batch may override them per draw
        VkDynamicState* dyn = stackalloc VkDynamicState[DynamicStates]
        {
            VkDynamicState.Viewport, VkDynamicState.Scissor, VkDynamicState.DepthBias, VkDynamicState.CullMode,
            VkDynamicState.FrontFace, VkDynamicState.DepthTestEnable, VkDynamicState.DepthWriteEnable,
            VkDynamicState.DepthCompareOp, VkDynamicState.DepthBiasEnable,
        };
        var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = DynamicStates, pDynamicStates = dyn };
        var gci = new VkGraphicsPipelineCreateInfo
        {
            pNext = &rendering, stageCount = (uint)ns, pStages = stages, pVertexInputState = &vin, pInputAssemblyState = &ia,
            pViewportState = &vp, pRasterizationState = &rs, pMultisampleState = &ms, pDepthStencilState = &ds,
            pColorBlendState = &cb, pDynamicState = &dy, layout = _pipeLayout,
        };
        _pipeCacheDirty = true;
        if (_vkd.vkCreateGraphicsPipelines(_pipeCache, 1, &gci, &pipe) != VkResult.Success)
            return Refused<PipelineId>($"{what}: vkCreateGraphicsPipelines refused");
        return new PipelineId(_pipelines.Add(new VkPipe(pipe, false, d.Depth, d.Raster)));
    }

    private static VkPipelineShaderStageCreateInfo Stage(VkShader s)
    {
        var st = new VkPipelineShaderStageCreateInfo
        {
            stage = s.Stage switch
            {
                ShaderStage.Vertex => VkShaderStageFlags.Vertex,
                ShaderStage.Geometry => VkShaderStageFlags.Geometry,
                ShaderStage.Compute => VkShaderStageFlags.Compute,
                _ => VkShaderStageFlags.Fragment,
            },
            module = s.Module,
        };
        fixed (byte* main = "main\0"u8) st.pName = main;   // a u8 literal lives in the image: the pointer outlives the call
        return st;
    }

    /// <summary>The states every graphics pipeline leaves dynamic.</summary>
    private const int DynamicStates = 9;

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
