using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>The engine's release tag ahead of a DDS header: a short type, then the owner pointer.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DdsTagSlot
{
    public short Type;
    public IntPtr Owner;
}

/// <summary>The 20-byte legacy DDS header, opaque to us.</summary>
[InlineArray(CX.Alloy.DdsReader.LegacyHeader)]
internal struct DdsLegacyHeader { private byte _e; }

/// <summary>The 128-byte Microsoft DDS header, opaque to us.</summary>
[InlineArray(CX.Alloy.DdsReader.MsHeader)]
internal struct DdsMsHeader { private byte _e; }

/// <summary>What precedes level 0 of a legacy-header DDS: the tag, then the 20-byte header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DdsLegacyPrefix
{
    public DdsTagSlot Tag;
    public DdsLegacyHeader Header;
}

/// <summary>What precedes level 0 of a Microsoft-header DDS: the tag, then the 128-byte header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DdsMsPrefix
{
    public DdsTagSlot Tag;
    public DdsMsHeader Header;
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
