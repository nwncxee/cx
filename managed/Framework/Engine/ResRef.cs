using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>An engine <c>CResRef</c> as it sits in memory: 16 name bytes and a terminator, NUL-padded. A call taking
/// <c>CResRef const&amp;</c> copies all 17 bytes (Win64 <c>GetOverride</c> reads byte 16), so a shorter buffer is
/// read past its end; every such <c>[Fn]</c> takes <c>ResRef*</c> for that reason.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ResRef
{
    /// <summary>The name's widest form.</summary>
    public const int Chars = 16;
    /// <summary>The engine's footprint: the name and the terminator byte after it.</summary>
    public const int Bytes = Chars + 1;

    /// <summary>The 17 bytes, bounds-checked.</summary>
    [InlineArray(Bytes)]
    private struct Raw
    {
        private byte _e;
    }

    private Raw _b;

    /// <summary>The NUL-terminated name at <paramref name="s"/>, cut at <see cref="Chars"/>; never reads past
    /// the terminator, so an engine string of any length is safe to copy.</summary>
    public static ResRef Of(byte* s, bool lower = false)
    {
        ResRef r = default;
        for (int i = 0; s != null && i < Chars && s[i] != 0; i++)
            r._b[i] = lower ? Lower(s[i]) : s[i];
        return r;
    }

    /// <summary><paramref name="s"/> as ASCII bytes, cut at <see cref="Chars"/> or its first NUL.</summary>
    public static ResRef Of(ReadOnlySpan<char> s, bool lower = false)
    {
        ResRef r = default;
        for (int i = 0; i < Chars && i < s.Length && s[i] != '\0'; i++)
            r._b[i] = lower ? Lower((byte)s[i]) : (byte)s[i];
        return r;
    }

    /// <summary>Byte <paramref name="i"/> of the 17, the terminator included.</summary>
    public readonly byte this[int i] => _b[i];

    /// <summary>The name's length: the bytes before the first NUL.</summary>
    public readonly int Length
    {
        get
        {
            int n = 0;
            while (n < Chars && _b[n] != 0) n++;
            return n;
        }
    }

    private static byte Lower(byte c) => (uint)(c - 'A') < 26 ? (byte)(c + 32) : c;
}

/// <summary>A <c>CResRef</c> crossing BY VALUE: SysV copies the 17 bytes onto the stack, Win64 passes a pointer to a
/// caller-made copy. One C# type name, so one <c>[Fn]</c> signature names the parameter on both platforms.</summary>
public unsafe struct ResRefArg
{
#if WINDOWS
    /// <summary>Win64: the caller-made copy, which must outlive the call.</summary>
    public ResRef* P;
#else
    /// <summary>SysV: the resref itself.</summary>
    public ResRef V;
#endif

    /// <summary>The argument for a resref the caller holds; on Win64 <paramref name="p"/> must outlive the call.</summary>
    public static ResRefArg Of(ResRef* p) =>
#if WINDOWS
        new() { P = p };
#else
        new() { V = *p };
#endif
}
