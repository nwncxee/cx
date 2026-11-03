using System;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// Task and mesh stages, where the device offers <c>VK_EXT_mesh_shader</c>: the extension and both stages are enabled as
/// the device opens, mesh pipelines are built whole, and every barrier that names the shader stages names these two too.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    private static ReadOnlySpan<byte> MeshExt => "VK_EXT_mesh_shader\0"u8;

    /// <summary>The device runs task and mesh stages.</summary>
    private bool _mesh;
    /// <summary>Why it does not, for the line that says so; null when it does.</summary>
    private string? _meshWhy;
    /// <summary>The task and mesh stage bits, or none: a barrier may name them only on a device that enabled them.</summary>
    private VkPipelineStageFlags2 _meshStages;

    /// <summary>Mesh pipelines and <see cref="CommandList.DrawMeshTasks"/> work on this device.</summary>
    internal bool MeshShaders => _mesh;

    /// <summary>Why <see cref="MeshShaders"/> is false, naming the extension; null when it is true.</summary>
    internal string? MeshRefusal => _meshWhy;

    /// <summary><paramref name="s"/> with the task and mesh stages added wherever it names the vertex stage.</summary>
    private (VkPipelineStageFlags2 Stages, VkAccessFlags2 Access) MeshScope((VkPipelineStageFlags2 Stages, VkAccessFlags2 Access) s)
        => (s.Stages & VkPipelineStageFlags2.VertexShader) != 0 ? (s.Stages | _meshStages, s.Access) : s;

    private bool DrawMesh(DispatchCmd c)
    {
        if (!_mesh) return Fault("a mesh draw on a device without mesh shaders");
        if (!EnsureRendering()) return false;
        _vkd.vkCmdDrawMeshTasksEXT(_cmd, (uint)c.X, (uint)c.Y, (uint)c.Z);
        _drawN++;
        return true;
    }
}
