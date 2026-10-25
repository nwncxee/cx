using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>One std140 uniform block: a staging <typeparamref name="T"/> the caller writes field by field,
/// uploaded and bound by <see cref="Bind"/>. Replaces a pass's loose <c>glUniform*</c> traffic with one
/// orphan-and-fill, and allocates nothing after construction, so a per-frame refill is GC-free.</summary>
/// <typeparam name="T">The block's staging struct, pinned to the GLSL by <see cref="Std140.Pin"/>.</typeparam>
internal sealed unsafe class UniformBlock<T> where T : unmanaged
{
    private T _stage, _sent;
    private uint _buf;
    private bool _everSent;

    /// <summary>The staged block, written in place: <c>blk.Stage.Radius = ...</c>.</summary>
    internal ref T Stage => ref _stage;

    /// <summary>std140 rounds a block up to a whole vec4, so the buffer is that size and the staging struct
    /// -- which covers every member and nothing more -- fills its front.</summary>
    private static int Bytes => (Unsafe.SizeOf<T>() + 15) & ~15;

    /// <summary>Upload the staged block if it differs from what this buffer holds, then bind it on
    /// <paramref name="point"/>. The bind is unconditional: a shared point may have changed hands since the
    /// last send, so no reader ever trusts a bind another pass made.</summary>
    internal void Bind(uint point)
    {
        if (_buf == 0) { uint b; Gl.CreateBuffers(1, &b); _buf = b; _everSent = false; }
        Span<byte> stage = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _stage, 1));
        if (!_everSent || !stage.SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _sent, 1))))
        {
            // Orphan and fill: the block the GPU may still be reading is never written under it.
            Gl.NamedBufferData(_buf, (IntPtr)Bytes, null, Gl.DYNAMIC_DRAW);
            fixed (byte* p = stage) Gl.NamedBufferSubData(_buf, IntPtr.Zero, (IntPtr)stage.Length, p);
            _sent = _stage;
            _everSent = true;
        }
        Gl.BindBufferBase(Gl.UNIFORM_BUFFER, point, _buf);
    }
}
