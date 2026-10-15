using System;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>A buffer of ours whose payload follows a header of type <typeparamref name="T"/> in the allocation:
/// the headroom is <c>sizeof(T)</c>, and the payload address is what travels.</summary>
internal static unsafe class Headroom<T> where T : unmanaged
{
    /// <summary>Bytes from the allocation start to the payload.</summary>
    internal static int Size => sizeof(T);

    /// <summary>The payload inside <paramref name="allocation"/>.</summary>
    internal static byte* PayloadOf(void* allocation) => (byte*)((T*)allocation + 1);

    /// <summary>The allocation a payload lives in.</summary>
    internal static void* AllocationOf(void* payload) => (T*)payload - 1;

    /// <summary><see cref="AllocationOf(void*)"/> for a payload held as an address.</summary>
    internal static void* AllocationOf(IntPtr payload) => (T*)payload - 1;
}

/// <summary>The engine's release tag ahead of a DDS header: a short type, then the owner pointer.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DdsTagSlot
{
    public short Type;
    public IntPtr Owner;
}

/// <summary>What precedes level 0 of a legacy-header DDS: the tag, then the 20-byte header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct DdsLegacyPrefix
{
    public DdsTagSlot Tag;
    public fixed byte Header[20];
}

/// <summary>What precedes level 0 of a Microsoft-header DDS: the tag, then the 128-byte header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct DdsMsPrefix
{
    public DdsTagSlot Tag;
    public fixed byte Header[128];
}

/// <summary>The release tag in front of a DDS level 0, found by the container's prefix type.</summary>
internal readonly unsafe struct DdsTag
{
    private readonly DdsTagSlot* _slot;

    /// <summary>The tag of the buffer whose level 0 is <paramref name="level0"/>.</summary>
    internal DdsTag(byte* level0, bool microsoft)
        => _slot = microsoft ? &((DdsMsPrefix*)level0 - 1)->Tag : &((DdsLegacyPrefix*)level0 - 1)->Tag;

    /// <summary>The tag's type word.</summary>
    internal short Type { get => _slot->Type; set => _slot->Type = value; }

    /// <summary>The owner pointer that follows the type word.</summary>
    internal IntPtr Owner { get => _slot->Owner; set => _slot->Owner = value; }
}
