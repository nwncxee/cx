using System;
using CX.Graphics;

namespace CX.Framework;

/// <summary>An engine pool's host shadow, which mirrors the GL buffer byte for byte.</summary>
internal readonly unsafe struct HostShadow
{
    private readonly byte* _host;

    /// <summary>The shadow whose first byte is <paramref name="host"/>.</summary>
    internal HostShadow(byte* host) => _host = host;

    /// <summary>Upload the shadow's bytes [<paramref name="offset"/>, +<paramref name="size"/>) to the same range of
    /// <paramref name="buffer"/>: a range the engine itself named.</summary>
    internal void UploadBytes(uint buffer, long offset, long size)
        => Gl.NamedBufferSubData(buffer, (IntPtr)offset, (IntPtr)size, _host + offset);
}
