using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The mesh path golden (approved-spec, A1-A3): the weather pass's own path choice and programs on this device and on
/// one opened without mesh shaders, and the field each path draws. Oracle: the driver's own extension list, the
/// extension's name, and the compute path's coverage of a fixed field as the image the mesh path must match.
/// </summary>
internal static partial class GpuGolden
{
    private static void RainMeshApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (vk.Offers("VK_EXT_mesh_shader\0"u8))
        {
            // Given a GPU that offers mesh shaders and rain falling
            // When the frame draws
            // Then the rain draws through mesh shaders, and cx.log says so once
            Weather.WxPath path = AlloyRenderer.VkWxPath(vk, log, out string line, out bool again);
            log.Info($"GpuGolden: rain mesh, this device: {path} -- {line}");
            check.Expect(path == Weather.WxPath.Mesh && line.Contains("mesh shaders", StringComparison.Ordinal) && !again,
                $"rain mesh A1: this device offers VK_EXT_mesh_shader, so rain must draw through mesh shaders; the path is {path}, the line \"{line}\", said again {again}");
        }
        else check.Skip("rain mesh A1: this device offers no VK_EXT_mesh_shader");

        // Given a GPU that does not offer mesh shaders and rain falling
        // When the frame draws
        // Then rain, snow and roof drips still fall as they do today, and cx.log names the missing extension once
        VkBackend? plain = VkBackend.TryCreate(log, meshShaders: false);
        check.Expect(plain != null, "rain mesh A2: a device opened without mesh shaders was refused (logged above)");
        if (plain != null)
        {
            try
            {
                Weather.WxPath path = AlloyRenderer.VkWxPath(plain, log, out string line, out bool again);
                log.Info($"GpuGolden: rain mesh, a device without mesh shaders: {path} -- {line}");
                check.Expect(path == Weather.WxPath.Compute && line.Contains("VK_EXT_mesh_shader", StringComparison.Ordinal) && !again,
                    $"rain mesh A2: without mesh shaders rain must take the compute path and name the extension once; the path is {path}, the line \"{line}\", said again {again}");
                foreach ((int kind, string name) in WxKinds)
                {
                    float[]? img = AlloyRenderer.VkWxCoverage(plain, log, Weather.WxPath.Compute, kind);
                    check.Expect(img != null && Sum(img) > 0f, $"rain mesh A2: without mesh shaders the {name} must still fall");
                }
            }
            finally { plain.Dispose(); }
        }

        if (!vk.Offers("VK_EXT_mesh_shader\0"u8)) { check.Skip("rain mesh A3: this device offers no VK_EXT_mesh_shader"); return; }
        foreach ((int kind, string name) in WxKinds)
        {
            float[]? compute = AlloyRenderer.VkWxCoverage(vk, log, Weather.WxPath.Compute, kind);
            float[]? mesh = AlloyRenderer.VkWxCoverage(vk, log, Weather.WxPath.Mesh, kind);
            int covered = 0, apart = 0;
            float total = compute == null ? 0f : Sum(compute), off = 0f;
            if (compute != null && mesh != null && mesh.Length == compute.Length)
                for (int i = 0; i < compute.Length; i++)
                {
                    off += MathF.Abs(mesh[i] - compute[i]);
                    if (compute[i] > 0f) covered++;
                    if (compute[i] > 0f != mesh[i] > 0f) apart++;
                }
            else apart = int.MaxValue;
            log.Info(FormattableString.Invariant($"GpuGolden: rain mesh, {name}: compute covers {covered} px ({total:F3}), the paths differ in {apart} px by {off:F4}"));

            // Given the same rain, snow or roof drips, camera and cover map
            // When the mesh path and today's path each draw them
            // Then the same drops stand in the same places
            check.Expect(covered >= 20 && apart <= covered / 200 && off <= total * 0.005f,
                FormattableString.Invariant($"rain mesh A3 ({name}): of {covered} px the compute path covers (at least 20), {apart} differ (at most 0.5%), and the coverage differs by {off:F4} of {total:F4}"));
        }
    }

    /// <summary>Every texel of a coverage image added up.</summary>
    private static float Sum(float[] img)
    {
        float s = 0f;
        foreach (float v in img) s += v;
        return s;
    }
}
