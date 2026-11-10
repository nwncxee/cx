using System;
using System.IO;
using System.Threading;
using CX.Framework;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The device's pipeline cache, every pipeline made through it, seeded from the file a past session saved
    /// for this driver; and whether a pipeline has been made since the last save.</summary>
    private VkPipelineCache _pipeCache;
    private volatile bool _pipeCacheDirty;
    private string _pipeCachePath = "";

    /// <summary>Open the pipeline cache from <c>cx-shaders/</c>, under a name carrying the driver's own cache identity
    /// so another GPU or driver never hands this one its data. An unreadable or refused file starts it empty.</summary>
    private void OpenPipelineCache(in VkPhysicalDeviceProperties props)
    {
        fixed (byte* u = props.pipelineCacheUUID)
            _pipeCachePath = Path.Combine(ShaderCache.Dir,
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

    /// <summary>The worker holding the last blob handed off, while it holds it: the blob is a hundred megabytes and the
    /// file write is not the frame's work. One write is in flight at a time; a save that finds it busy leaves the dirty
    /// flag standing and the next cadence hands off instead.</summary>
    private Thread? _pipeCacheWriter;

    /// <summary>Write the pipeline cache back when a pipeline was made since the last write; cheap otherwise. Called on
    /// a cadence by the frame and at teardown. <paramref name="wait"/> writes on the calling thread -- teardown, where
    /// no later cadence comes.</summary>
    internal void SavePipelineCache(bool wait = false)
    {
        if (wait && _pipeCacheWriter is { IsAlive: true } busy) busy.Join();
        if (!_pipeCacheDirty || _pipeCache.IsNull) return;
        if (!wait && _pipeCacheWriter is { IsAlive: true }) return;
        // vkGetPipelineCacheData reads the device's cache, so it takes the thread that owns the device
        nuint n = 0;
        if (_vkd.vkGetPipelineCacheData(_pipeCache, &n, null) != VkResult.Success || n == 0) return;
        var data = new byte[(int)n];
        fixed (byte* p = data) if (_vkd.vkGetPipelineCacheData(_pipeCache, &n, p) != VkResult.Success) return;
        if ((int)n < data.Length) data = data[..(int)n];
        _pipeCacheDirty = false;
        string path = _pipeCachePath;
        ExtenderLog log = _log;
        if (wait) { if (!WriteCacheFile(data, path, log)) _pipeCacheDirty = true; return; }
        var t = new Thread(() => { if (!WriteCacheFile(data, path, log)) _pipeCacheDirty = true; })
        { IsBackground = true, Name = "cx-pipecache", Priority = ThreadPriority.BelowNormal };
        _pipeCacheWriter = t;
        t.Start();
    }

    /// <summary>Put <paramref name="blob"/> at <paramref name="path"/> through a temporary beside it, so a half-written
    /// file is never the one a later session seeds the driver from. False, loudly, when it did not land.</summary>
    private static bool WriteCacheFile(byte[] blob, string path, ExtenderLog log)
    {
        Affinity.ClearThis();   // created by the (possibly pinned) frame thread; the write must not share its core
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            log.Always($"VkBackend: the pipeline cache was NOT saved to {path} -- {e.Message}; the next session rebuilds its pipelines");
            return false;
        }
    }
}
