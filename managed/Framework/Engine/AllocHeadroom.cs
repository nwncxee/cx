using System;

namespace CX.Framework;

/// <summary>A buffer of ours whose payload starts a fixed headroom into its allocation, the layout the engine's
/// release path expects: the allocation is one headroom before the payload, and the payload address is what travels.</summary>
internal static unsafe class Headroom
{
    /// <summary>The payload address <paramref name="bytes"/> into <paramref name="allocation"/>.</summary>
    internal static byte* Payload(byte* allocation, long bytes) => allocation + bytes;

    /// <summary>The allocation a payload sits <paramref name="headroom"/> bytes into.</summary>
    internal static byte* Allocation(byte* payload, int headroom) => payload - headroom;

    /// <summary><see cref="Allocation(byte*, int)"/> for a payload held as an address.</summary>
    internal static byte* Allocation(IntPtr payload, int headroom) => (byte*)payload - headroom;
}

/// <summary>The release tag the engine reads ahead of a DDS level 0: a short type, then the owner pointer.
/// <paramref name="tagOffset"/> is the tag's distance before level 0 for the buffer's container.</summary>
internal readonly unsafe struct DdsTag
{
    private readonly byte* _at;

    /// <summary>The tag of the buffer whose level 0 is <paramref name="level0"/>.</summary>
    internal DdsTag(byte* level0, int tagOffset) => _at = level0 - tagOffset;

    /// <summary>The tag's type word.</summary>
    internal short Type { get => *(short*)_at; set => *(short*)_at = value; }

    /// <summary>The owner pointer that follows the type word.</summary>
    internal IntPtr Owner { get => *(IntPtr*)(_at + 2); set => *(IntPtr*)(_at + 2) = value; }
}
