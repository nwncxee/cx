using System;

namespace CX.Graphics;

/// <summary>What GL tells Vulkan about the GPU it runs on: the device UUIDs <c>EXT_memory_object</c> and
/// <c>EXT_semaphore</c> both answer, which the Vulkan device is matched by. Nothing is shared between the APIs.</summary>
internal static unsafe class GlInterop
{
    public const uint NUM_DEVICE_UUIDS_EXT = 0x9596, DEVICE_UUID_EXT = 0x9597;
    public const int UUID_SIZE_EXT = 16;

    private static delegate* unmanaged<uint, uint, byte*, void> _getUnsignedBytei;

    /// <summary>Resolve the UUID query in the CURRENT context. Null when it answers, else why not as a constant string.</summary>
    internal static string? Refusal()
    {
        if (!Gl.HasExt("GL_EXT_memory_object"u8) && !Gl.HasExt("GL_EXT_semaphore"u8))
            return "the GL driver lacks GL_EXT_memory_object and GL_EXT_semaphore, so it cannot name its device";
        _getUnsignedBytei = (delegate* unmanaged<uint, uint, byte*, void>)Gl.P("glGetUnsignedBytei_vEXT");
        return _getUnsignedBytei == null ? "the GL driver advertises its device UUID but glGetUnsignedBytei_vEXT did not resolve" : null;
    }

    /// <summary>Is <paramref name="uuid"/> one of the devices the current context runs on? Only after
    /// <see cref="Refusal"/> answered null.</summary>
    internal static bool OnDevice(ReadOnlySpan<byte> uuid)
    {
        int n = 0;
        Gl.GetIntegerv(NUM_DEVICE_UUIDS_EXT, &n);
        byte* got = stackalloc byte[UUID_SIZE_EXT];
        for (uint i = 0; i < (uint)n; i++)
        {
            _getUnsignedBytei(DEVICE_UUID_EXT, i, got);
            if (new ReadOnlySpan<byte>(got, UUID_SIZE_EXT).SequenceEqual(uuid)) return true;
        }
        return false;
    }
}
