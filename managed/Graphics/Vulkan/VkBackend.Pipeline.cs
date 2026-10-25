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
            if (_vkd.vkCreateComputePipelines(default, 1, &ci, &pipe) != VkResult.Success)
                return Refused<PipelineId>($"{what}: vkCreateComputePipelines refused");
            return new PipelineId(_pipelines.Add(new VkPipe(pipe, true)));
        }
        if (!_shaders.TryGet(d.Vs.Value, out VkShader vs) || !_shaders.TryGet(d.Fs.Value, out VkShader fs))
            return Refused<PipelineId>($"{what}: a graphics pipeline needs a live vertex and fragment stage");
        if (!d.Layout.IsNull) return Refused<PipelineId>($"{what}: vertex layouts are not on the Vulkan backend yet");
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
            fmts[i] = Of(d.Targets.Colors[i]);
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
            depthAttachmentFormat = VkFormat.Undefined, stencilAttachmentFormat = VkFormat.Undefined,
        };
        var vin = new VkPipelineVertexInputStateCreateInfo();
        var ia = new VkPipelineInputAssemblyStateCreateInfo
        {
            topology = d.Topo == Topology.TriangleStrip ? VkPrimitiveTopology.TriangleStrip : VkPrimitiveTopology.TriangleList,
        };
        var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
        var rs = new VkPipelineRasterizationStateCreateInfo
        {
            depthClampEnable = d.Raster.DepthClamp, polygonMode = VkPolygonMode.Fill,
            cullMode = d.Raster.Cull switch { CullMode.Back => VkCullModeFlags.Back, CullMode.Front => VkCullModeFlags.Front, _ => VkCullModeFlags.None },
            // GL and Vulkan both take the winding in framebuffer coordinates with row 0 at y 0: no flip
            frontFace = d.Raster.FrontCw ? VkFrontFace.Clockwise : VkFrontFace.CounterClockwise,
            depthBiasEnable = d.Raster.DepthBias, lineWidth = 1,
        };
        var ms = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
        var ds = new VkPipelineDepthStencilStateCreateInfo
        {
            depthTestEnable = d.Depth.Test, depthWriteEnable = d.Depth.Write, depthCompareOp = Compare(d.Depth.Compare),
        };
        var cb = new VkPipelineColorBlendStateCreateInfo { attachmentCount = (uint)colors, pAttachments = blend };
        VkDynamicState* dyn = stackalloc VkDynamicState[3] { VkDynamicState.Viewport, VkDynamicState.Scissor, VkDynamicState.DepthBias };
        var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 3, pDynamicStates = dyn };
        var gci = new VkGraphicsPipelineCreateInfo
        {
            pNext = &rendering, stageCount = (uint)ns, pStages = stages, pVertexInputState = &vin, pInputAssemblyState = &ia,
            pViewportState = &vp, pRasterizationState = &rs, pMultisampleState = &ms, pDepthStencilState = &ds,
            pColorBlendState = &cb, pDynamicState = &dy, layout = _pipeLayout,
        };
        if (_vkd.vkCreateGraphicsPipelines(default, 1, &gci, &pipe) != VkResult.Success)
            return Refused<PipelineId>($"{what}: vkCreateGraphicsPipelines refused");
        return new PipelineId(_pipelines.Add(new VkPipe(pipe, false)));
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

    private static VkBlendFactor Factor(BlendFactor f) => f switch
    {
        BlendFactor.Zero => VkBlendFactor.Zero,
        BlendFactor.SrcAlpha => VkBlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => VkBlendFactor.OneMinusSrcAlpha,
        BlendFactor.OneMinusSrcColor => VkBlendFactor.OneMinusSrcColor,
        _ => VkBlendFactor.One,
    };
}
