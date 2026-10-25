using System;
using System.IO;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The device's pipeline cache, every pipeline made through it, seeded from the file a past session saved
    /// for this driver; and whether a pipeline has been made since the last save.</summary>
    private VkPipelineCache _pipeCache;
    private bool _pipeCacheDirty;
    private string _pipeCachePath = "";

    /// <summary>Open the pipeline cache from <c>cx-shaders/</c>, under a name carrying the driver's own cache identity
    /// so another GPU or driver never hands this one its data. An unreadable or refused file starts it empty.</summary>
    private void OpenPipelineCache(in VkPhysicalDeviceProperties props)
    {
        fixed (byte* u = props.pipelineCacheUUID)
            _pipeCachePath = Path.Combine(ProgramBinary.CacheDir,
                $"vk-pipelines-{props.vendorID:x}-{props.deviceID:x}-{Convert.ToHexString(new ReadOnlySpan<byte>(u, 16))}.bin");
        byte[] seed = [];
        try { if (File.Exists(_pipeCachePath)) seed = File.ReadAllBytes(_pipeCachePath); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        VkPipelineCache cache;
        fixed (byte* p = seed)
        {
            var ci = new VkPipelineCacheCreateInfo { initialDataSize = (nuint)seed.Length, pInitialData = p };
            if (_vkd.vkCreatePipelineCache(&ci, null, &cache) == VkResult.Success)
            {
                _pipeCache = cache;
                _log.Info($"VkBackend: pipeline cache {(seed.Length > 0 ? $"seeded with {seed.Length} bytes from" : "empty, saved to")} {_pipeCachePath}");
                return;
            }
        }
        var empty = new VkPipelineCacheCreateInfo();
        if (_vkd.vkCreatePipelineCache(&empty, null, &cache) == VkResult.Success) _pipeCache = cache;
        _log.Info($"VkBackend: the driver refused the saved pipeline cache -- starting empty");
    }

    /// <summary>Write the pipeline cache back when a pipeline was made since the last write; cheap otherwise. Called on
    /// a cadence by the frame and at teardown.</summary>
    internal void SavePipelineCache()
    {
        if (!_pipeCacheDirty || _pipeCache.IsNull) return;
        nuint n = 0;
        if (_vkd.vkGetPipelineCacheData(_pipeCache, &n, null) != VkResult.Success || n == 0) return;
        var data = new byte[(int)n];
        fixed (byte* p = data) if (_vkd.vkGetPipelineCacheData(_pipeCache, &n, p) != VkResult.Success) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pipeCachePath)!);
            string tmp = _pipeCachePath + ".tmp";
            File.WriteAllBytes(tmp, data.AsSpan(0, (int)n).ToArray());
            File.Move(tmp, _pipeCachePath, overwrite: true);
            _pipeCacheDirty = false;
        }
        catch (IOException e) { _log.Info($"VkBackend: the pipeline cache was not saved -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { _log.Info($"VkBackend: the pipeline cache was not saved -- {e.Message}"); }
    }
}
