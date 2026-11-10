using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>The message buffer answers for its own length, so a reader of a packet in flight takes one span
/// instead of a pointer and a count that have to agree.</summary>
public readonly unsafe partial struct CNWMessage
{
    /// <summary>The whole buffer as the bytes the message holds, empty when no buffer is set or the length row
    /// reads past what a span can address.</summary>
    public ReadOnlySpan<byte> Bytes
        => Buffer == IntPtr.Zero || Length == 0 || Length > int.MaxValue
           ? default : new ReadOnlySpan<byte>((byte*)Buffer, (int)Length);
}
