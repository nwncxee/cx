using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Graphics;

/// <summary>The GL half of GL/Vulkan interop (<c>EXT_memory_object</c>, <c>EXT_semaphore</c> and their
/// <c>_fd</c>/<c>_win32</c> variants): importing an exported Vulkan allocation as a texture, the shared semaphore
/// pair, and the device UUID the Vulkan device is matched by.</summary>
internal static unsafe class GlInterop
{
    public const uint TEXTURE_TILING_EXT = 0x9580, DEDICATED_MEMORY_OBJECT_EXT = 0x9581, OPTIMAL_TILING_EXT = 0x9584;
    public const uint HANDLE_TYPE_OPAQUE_FD_EXT = 0x9586, HANDLE_TYPE_OPAQUE_WIN32_EXT = 0x9587;
    public const uint NUM_DEVICE_UUIDS_EXT = 0x9596, DEVICE_UUID_EXT = 0x9597, DRIVER_UUID_EXT = 0x9598;
    /// <summary>The one layout the composite changes hands in, both directions.</summary>
    public const uint LAYOUT_SHADER_READ_ONLY_EXT = 0x9591;
    public const int UUID_SIZE_EXT = 16;

    private static readonly bool Win = OperatingSystem.IsWindows();

    private static delegate* unmanaged<int, uint*, void> _createMemoryObjects, _deleteMemoryObjects, _genSemaphores, _deleteSemaphores;
    private static delegate* unmanaged<uint, uint, int*, void> _memoryObjectParameteriv;
    private static delegate* unmanaged<uint, ulong, uint, int, void> _importMemoryFd;
    private static delegate* unmanaged<uint, ulong, uint, IntPtr, void> _importMemoryWin32;
    private static delegate* unmanaged<uint, int, uint, int, int, uint, ulong, void> _textureStorageMem2D;
    private static delegate* unmanaged<uint, uint, int, void> _importSemaphoreFd;
    private static delegate* unmanaged<uint, uint, IntPtr, void> _importSemaphoreWin32;
    private static delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void> _signal, _wait;
    private static delegate* unmanaged<uint, uint, byte*, void> _getUnsignedBytei;

    [DllImport("kernel32.dll")] private static extern int CloseHandle(IntPtr h);

    /// <summary>Resolve the entry points and ask the CURRENT context for the extensions. Null when interop is
    /// available, else the missing piece as a constant string.</summary>
    internal static string? Refusal()
    {
        if (!(Win ? Gl.HasExt("GL_EXT_memory_object_win32"u8) && Gl.HasExt("GL_EXT_semaphore_win32"u8)
                  : Gl.HasExt("GL_EXT_memory_object_fd"u8) && Gl.HasExt("GL_EXT_semaphore_fd"u8))
            || !Gl.HasExt("GL_EXT_memory_object"u8) || !Gl.HasExt("GL_EXT_semaphore"u8))
            return Win ? "the GL driver lacks GL_EXT_memory_object_win32/GL_EXT_semaphore_win32"
                       : "the GL driver lacks GL_EXT_memory_object_fd/GL_EXT_semaphore_fd";
        _createMemoryObjects = (delegate* unmanaged<int, uint*, void>)Gl.P("glCreateMemoryObjectsEXT");
        _deleteMemoryObjects = (delegate* unmanaged<int, uint*, void>)Gl.P("glDeleteMemoryObjectsEXT");
        _memoryObjectParameteriv = (delegate* unmanaged<uint, uint, int*, void>)Gl.P("glMemoryObjectParameterivEXT");
        _importMemoryFd = (delegate* unmanaged<uint, ulong, uint, int, void>)Gl.P("glImportMemoryFdEXT");
        _importMemoryWin32 = (delegate* unmanaged<uint, ulong, uint, IntPtr, void>)Gl.P("glImportMemoryWin32HandleEXT");
        _textureStorageMem2D = (delegate* unmanaged<uint, int, uint, int, int, uint, ulong, void>)Gl.P("glTextureStorageMem2DEXT");
        _genSemaphores = (delegate* unmanaged<int, uint*, void>)Gl.P("glGenSemaphoresEXT");
        _deleteSemaphores = (delegate* unmanaged<int, uint*, void>)Gl.P("glDeleteSemaphoresEXT");
        _importSemaphoreFd = (delegate* unmanaged<uint, uint, int, void>)Gl.P("glImportSemaphoreFdEXT");
        _importSemaphoreWin32 = (delegate* unmanaged<uint, uint, IntPtr, void>)Gl.P("glImportSemaphoreWin32HandleEXT");
        _signal = (delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void>)Gl.P("glSignalSemaphoreEXT");
        _wait = (delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void>)Gl.P("glWaitSemaphoreEXT");
        _getUnsignedBytei = (delegate* unmanaged<uint, uint, byte*, void>)Gl.P("glGetUnsignedBytei_vEXT");
        bool import = Win ? _importMemoryWin32 != null && _importSemaphoreWin32 != null
                          : _importMemoryFd != null && _importSemaphoreFd != null;
        if (!import || _createMemoryObjects == null || _deleteMemoryObjects == null || _memoryObjectParameteriv == null
            || _textureStorageMem2D == null || _genSemaphores == null || _deleteSemaphores == null || _signal == null
            || _wait == null || _getUnsignedBytei == null || Gl.TextureParameteri == null || Gl.CreateTextures == null)
            return "the GL driver advertises the interop extensions but an entry point did not resolve";
        return null;
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

    /// <summary>A texture over an exported allocation of <paramref name="size"/> bytes, dedicated and optimally
    /// tiled as the Vulkan image is; <paramref name="internalFormat"/> must be the Vulkan format's own (GL refuses
    /// every use of an aliased one). The handle's fate differs per OS: GL owns an fd once imported, never a
    /// Windows handle, which is closed here. 0 texture when GL refused it.</summary>
    internal static uint ImportTexture(IntPtr handle, ulong size, uint internalFormat, int w, int h, out uint memory)
    {
        uint mem, tex;
        _createMemoryObjects(1, &mem);
        int yes = 1;
        _memoryObjectParameteriv(mem, DEDICATED_MEMORY_OBJECT_EXT, &yes);
        if (Win) { _importMemoryWin32(mem, size, HANDLE_TYPE_OPAQUE_WIN32_EXT, handle); CloseHandle(handle); }
        else _importMemoryFd(mem, size, HANDLE_TYPE_OPAQUE_FD_EXT, (int)handle);
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &tex);
        Gl.TextureParameteri(tex, TEXTURE_TILING_EXT, (int)OPTIMAL_TILING_EXT);
        _textureStorageMem2D(tex, 1, internalFormat, w, h, mem, 0);
        int got = 0;
        Gl.GetTextureLevelParameteriv(tex, 0, Gl.TEXTURE_WIDTH, &got);
        if (got != w)
        {
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, tex);
            _deleteMemoryObjects(1, &mem);
            memory = 0;
            return 0;
        }
        RenderTarget.Clamp(tex, Gl.NEAREST);
        memory = mem;
        return tex;
    }

    /// <summary>Drop a memory object whose texture is gone (retired frames ago).</summary>
    internal static void DeleteMemory(uint memory) { if (memory != 0) _deleteMemoryObjects(1, &memory); }

    /// <summary>A GL semaphore over an exported Vulkan one; ownership of the handle as in <see cref="ImportTexture"/>.</summary>
    internal static uint ImportSemaphore(IntPtr handle)
    {
        uint s;
        _genSemaphores(1, &s);
        if (Win) { _importSemaphoreWin32(s, HANDLE_TYPE_OPAQUE_WIN32_EXT, handle); CloseHandle(handle); }
        else _importSemaphoreFd(s, HANDLE_TYPE_OPAQUE_FD_EXT, (int)handle);
        return s;
    }

    internal static void DeleteSemaphore(uint s) { if (s != 0) _deleteSemaphores(1, &s); }

    /// <summary>GL hands <paramref name="textures"/> over in <see cref="LAYOUT_SHADER_READ_ONLY_EXT"/>; the signal
    /// flushes GL's command stream itself, so no glFlush follows.</summary>
    internal static void Signal(uint sem, ReadOnlySpan<uint> textures, ReadOnlySpan<uint> layouts)
    {
        fixed (uint* t = textures) fixed (uint* l = layouts) _signal(sem, 0, null, (uint)textures.Length, t, l);
    }

    /// <summary>GL takes <paramref name="textures"/> back in the layout Vulkan released them in.</summary>
    internal static void Wait(uint sem, ReadOnlySpan<uint> textures, ReadOnlySpan<uint> layouts)
    {
        fixed (uint* t = textures) fixed (uint* l = layouts) _wait(sem, 0, null, (uint)textures.Length, t, l);
    }
}
