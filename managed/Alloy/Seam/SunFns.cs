using System;
using System.Buffers.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The area light the game sets by console text. <c>CNWCArea::UpdateDayNightCycle</c> @0x806a20 blends
/// the day/night colours (and whatever the module's script set) and hands them to <c>controlpart sun
/// gidy_sun_amb|gidy_sun_diff color r g b</c>; the engine's <c>controlpart</c> looks the sun up in a table DB
/// objects are never in, so every colour was dropped and DB lit the area from the ARE file's load-time pair.</summary>
public sealed unsafe class SunFns : ISeamProvider
{
    public void Register(SeamRegistry r)
        => r.ReplaceEngine(SymbolMap.ControlPart, (void*)(delegate* unmanaged<byte*, byte*>)&ControlPart);

    /// <summary><c>controlpart(char*)</c>: "objectname partname field values". Only the sun's two colour
    /// parts mean anything in DB mode; anything else answers what the engine would have, since no gob of
    /// that name exists. Runs per day/night update, so it parses in place and allocates nothing.</summary>
    [UnmanagedCallersOnly]
    private static byte* ControlPart(byte* text)
    {
        if (text == null) return Say("usage:  setcolor objectname partname r g b\0"u8);
        switch (Parse(Native.Chars(text), out bool amb, out Vector3 c))
        {
            case Verdict.Usage: return Say("usage:  setcolor objectname partname r g b\0"u8);
            case Verdict.NoObject: return Say("no object by that name\0"u8);
            case Verdict.NoPart: return Say("no part by that name\0"u8);
            case Verdict.Colour: DbLights.SunColour(amb, c); break;
        }
        return Say("OK\0"u8);
    }

    /// <summary>What a <c>controlpart</c> line asked for, in the engine's own answer vocabulary.</summary>
    internal enum Verdict { Usage, NoObject, NoPart, Other, Colour }

    /// <summary>The parse, apart from the hook so it can be checked off-device.</summary>
    internal static Verdict Parse(ReadOnlySpan<byte> s, out bool amb, out Vector3 c)
    {
        amb = false; c = default;
        if (!Next(ref s, out var obj) || !Next(ref s, out var part)) return Verdict.Usage;
        if (!Ascii.EqualsIgnoreCase(obj, "sun"u8)) return Verdict.NoObject;
        amb = Ascii.EqualsIgnoreCase(part, "gidy_sun_amb"u8);
        if (!amb && !Ascii.EqualsIgnoreCase(part, "gidy_sun_diff"u8)) return Verdict.NoPart;
        if (!Next(ref s, out var field) || !Ascii.EqualsIgnoreCase(field, "color"u8)
            || !Float(ref s, out float r) || !Float(ref s, out float g) || !Float(ref s, out float b))
            return Verdict.Other;
        c = new Vector3(r, g, b);
        return Verdict.Colour;
    }

    private static byte* Say(ReadOnlySpan<byte> z) => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(z));

    /// <summary>The next whitespace-separated token of <paramref name="s"/>, consuming it.</summary>
    private static bool Next(ref ReadOnlySpan<byte> s, out ReadOnlySpan<byte> tok)
    {
        int i = 0;
        while (i < s.Length && s[i] <= (byte)' ') i++;
        int j = i;
        while (j < s.Length && s[j] > (byte)' ') j++;
        tok = s[i..j];
        s = s[j..];
        return j > i;
    }

    private static bool Float(ref ReadOnlySpan<byte> s, out float v)
    {
        v = 0f;
        return Next(ref s, out var tok) && Utf8Parser.TryParse(tok, out v, out int used) && used == tok.Length;
    }
}
