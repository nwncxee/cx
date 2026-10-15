using System;
using CX.Graphics;

namespace CX.Framework;

/// <summary>One vertex attribute of an engine pool's host shadow: <see cref="VertexStream"/> says where the
/// stream starts in the pool and how far apart its vertices are.</summary>
internal readonly unsafe struct HostVertices
{
    private readonly byte* _at;
    private readonly int _stride;

    /// <summary>The stream <paramref name="stream"/> inside the pool shadow <paramref name="host"/>.</summary>
    internal HostVertices(byte* host, in VertexStream stream)
    {
        _at = host + stream.Off;
        _stride = stream.Stride;
    }

    /// <summary>The attribute's floats at <paramref name="vertex"/>.</summary>
    internal float* Floats(long vertex) => (float*)(_at + vertex * _stride);
}

/// <summary>A run of 16-bit indices in an engine pool's host shadow, starting at index <c>first</c>.</summary>
internal readonly unsafe struct HostIndices
{
    private readonly ushort* _at;

    /// <summary>The indices of <paramref name="host"/> from index <paramref name="first"/>.</summary>
    internal HostIndices(byte* host, long first) => _at = (ushort*)host + first;

    /// <summary>The <paramref name="i"/>th index of the run.</summary>
    internal ushort this[int i] => _at[i];
}

/// <summary>An engine pool's host shadow, which mirrors the GL buffer byte for byte.</summary>
internal readonly unsafe struct HostShadow
{
    private readonly byte* _host;

    /// <summary>The shadow whose first byte is <paramref name="host"/>.</summary>
    internal HostShadow(byte* host) => _host = host;

    /// <summary>Upload the shadow's bytes [<paramref name="offset"/>, +<paramref name="size"/>) to the same range of <paramref name="buffer"/>.</summary>
    internal void Upload(uint buffer, long offset, long size)
        => Gl.NamedBufferSubData(buffer, (IntPtr)offset, (IntPtr)size, _host + offset);
}
