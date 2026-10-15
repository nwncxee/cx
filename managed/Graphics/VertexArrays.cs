using System;

namespace CX.Graphics;

/// <summary>
/// One vertex attribute's format and source buffer, as read off an engine VAO. Replaces the five parallel
/// arrays each owning pass used to carry per stream.
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
/// Reading an engine VAO's attribute formats and replaying them into an Alloy one -- the one copy of that
/// walk, shared by every pass that needs it.
/// </summary>
public static unsafe class VertexArrays
{
    /// <summary>Streams replayed since boot, and how many had an offset or stride off 4-byte alignment. The
    /// spec allows any byte offset; radeonsi on GFX6/GFX10+ open-codes the vertex fetch for such a stream and
    /// recompiles the vertex shader when a buffer's alignment class changes (si_state.c), so a non-zero count
    /// is a Mesa-only per-vertex cost the engine's pool layout is paying for.</summary>
    public static int Streams, Unaligned;

    /// <summary>Read one attribute slot of the CURRENTLY BOUND VAO. False (and <paramref name="s"/> zeroed)
    /// when the slot is disabled or unbacked, which is how a missing stream is reported upward.</summary>
    public static bool Read(uint loc, out VertexStream s)
    {
        s = default;
        int en; Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_ENABLED, &en);
        if (en == 0) return false;
        int sz, st, ty, bf, nm; void* ptr;
        Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_SIZE, &sz);
        Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_STRIDE, &st);
        Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_TYPE, &ty);
        Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING, &bf);
        Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_NORMALIZED, &nm);
        Gl.GetVertexAttribPointerv(loc, Gl.VERTEX_ATTRIB_ARRAY_POINTER, &ptr);
        if (ty == 0 || bf == 0) return false;
        s.Buf = (uint)bf; s.Size = sz; s.Type = (uint)ty; s.Stride = st; s.Off = (nint)ptr;
        s.Norm = (byte)(nm != 0 ? 1 : 0);
        return true;
    }

    /// <summary>Is this slot of the CURRENTLY BOUND VAO enabled at all? For the caller that only has to know
    /// a stream is there, never pulls it, and so does not need its format.</summary>
    public static bool Enabled(uint loc)
    { int en = 0; Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_ENABLED, &en); return en != 0; }

    /// <summary>Replay one stream into Alloy VAO <paramref name="vao"/> at <paramref name="loc"/>, binding point
    /// <paramref name="loc"/> too. Touches no global binding.</summary>
    public static void Bind(uint vao, uint loc, in VertexStream s)
    {
        Streams++;
        if ((s.Off & 3) != 0 || (s.Stride & 3) != 0) Unaligned++;
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
