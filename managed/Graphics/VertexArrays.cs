using System;

namespace CX.Graphics;

/// <summary>
/// One vertex attribute's format and source buffer. Replaces the five parallel arrays each owning pass used
/// to carry per stream.
/// </summary>
public struct VertexStream
{
    public uint Buf;
    public int Size;
    public uint Type;
    public int Stride;
    public long Off;
    /// <summary>GL's NORMALIZED flag. Only the bone streams care, and getting it backwards scales every
    /// weight by 1/32767 (mesh collapsed to the origin) or by 32767 (one flung off-screen).</summary>
    public byte Norm;

    /// <summary>Backed by a real buffer with a real type -- the only shape safe to hand to
    /// <c>VertexAttribPointer</c>. An enabled-but-unset array crashes the driver's vertex pull.</summary>
    public readonly bool Present => Buf != 0 && Type != 0;
}

/// <summary>
/// Declaring a vertex attribute's format on an Alloy VAO -- the one copy of that walk, shared by every pass
/// that needs it.
/// </summary>
public static unsafe class VertexArrays
{
    /// <summary>Replay one stream into Alloy VAO <paramref name="vao"/> at <paramref name="loc"/>, binding point
    /// <paramref name="loc"/> too. Touches no global binding.</summary>
    public static void Bind(uint vao, uint loc, in VertexStream s)
    {
        Gl.VertexArrayVertexBuffer(vao, loc, s.Buf, (IntPtr)s.Off, s.Stride != 0 ? s.Stride : ElementBytes(s.Size, s.Type));
        Gl.VertexArrayAttribFormat(vao, loc, s.Size, s.Type, s.Norm, 0);
        Gl.VertexArrayAttribBinding(vao, loc, loc);
        Gl.EnableVertexArrayAttrib(vao, loc);
    }

    /// <summary>Bytes of one tightly packed element. A binding's stride 0 means "do not advance", not "tightly
    /// packed" as it does for <c>glVertexAttribPointer</c>, so a tight stream's stride is computed. The packed
    /// types hold the whole element in one 4-byte word whatever the component count.</summary>
    public static int ElementBytes(int size, uint type) => type switch
    {
        Gl.BYTE or Gl.UNSIGNED_BYTE => size,
        Gl.SHORT or Gl.UNSIGNED_SHORT or Gl.HALF_FLOAT => size * 2,
        0x8368 or 0x8D9F or 0x8C3B /* not-an-offset: the three packed 32-bit types */ => 4,
        _ => size * 4,
    };
}
