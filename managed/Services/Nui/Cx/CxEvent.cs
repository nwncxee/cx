using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// Sends the server a NUI event on the reserved element <see cref="Element"/>: the event name, the element id and a JSON array
/// payload, built in memory this class owns and frees after the call. The engine's push helper copies all three, so nothing it
/// receives outlives the call. The string and array shapes follow the running compiler's (<see cref="Layout"/>).
/// </summary>
internal static unsafe class CxEvent
{
    /// <summary>The element id every CX event carries.</summary>
    internal const string Element = "cx";
    /// <summary>The event name of a setting CX refused; the payload is <c>[kind, key, value, reason]</c>.</summary>
    internal const string Refused = "refused";
    private const int StringBytes = 32, JsonBytes = 16, VectorBytes = 24, InlineMax = 15, MaxText = 200;
    private const byte JsonString = 3, JsonArray = 2;

    /// <summary>Queue a refusal event for <paramref name="window"/>. False when the push helper did not resolve.</summary>
    internal static bool PushRefusal(IntPtr window, PolicyRefusal r)
    {
        var push = EngineFn.NuiEventPush;
        if (push == null) return false;
        var mem = new List<IntPtr>(8);
        try
        {
            byte* name = NewString(Refused, mem), element = NewString(Element, mem);
            byte* payload = NewArray(mem, r.Kind.ToString().ToLowerInvariant(), r.Key, r.Value, r.Reason);
            IntPtr target = OperatingSystem.IsWindows() ? window : (IntPtr)Unsafe.AsPointer(ref new Nui__JSON__JsonWindow(window).WindowId);
            push(target, (IntPtr)name, (IntPtr)element, -1, (IntPtr)payload);
            return true;
        }
        finally { foreach (IntPtr p in mem) NativeMemory.Free((void*)p); }
    }

    private static byte* Alloc(List<IntPtr> mem, int bytes)
    {
        byte* p = (byte*)NativeMemory.AllocZeroed((nuint)bytes);
        mem.Add((IntPtr)p);
        return p;
    }

    /// <summary>A 32-byte <c>std::string</c> holding <paramref name="text"/> (UTF-8, cut at <see cref="MaxText"/> bytes).</summary>
    internal static byte* NewString(string text, List<IntPtr> mem, bool? msvc = null)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        int n = Math.Min(b.Length, MaxText);
        bool win = msvc ?? OperatingSystem.IsWindows();
        byte* s = Alloc(mem, StringBytes);
        byte* chars;
        if (n <= InlineMax) chars = win ? s : s + 16;
        else chars = Alloc(mem, n + 1);
        for (int i = 0; i < n; i++) chars[i] = b[i];
        if (win)
        {
            if (n > InlineMax) *(byte**)s = chars;
            *(ulong*)(s + 16) = (ulong)n;
            *(ulong*)(s + 24) = n > InlineMax ? (ulong)n : InlineMax;
        }
        else
        {
            *(byte**)s = chars;
            *(ulong*)(s + 8) = (ulong)n;
            if (n > InlineMax) *(ulong*)(s + 16) = (ulong)n;
        }
        return s;
    }

    /// <summary>A JSON array of strings: the value, its vector, and a string value for each item.</summary>
    internal static byte* NewArray(List<IntPtr> mem, params string[] items) => NewArray(mem, null, items);

    /// <summary>As <see cref="NewArray(List{IntPtr}, string[])"/>, with the container shapes picked by <paramref name="msvc"/>.</summary>
    internal static byte* NewArray(List<IntPtr> mem, bool? msvc, string[] items)
    {
        byte* vec = Alloc(mem, VectorBytes);
        byte* elems = Alloc(mem, JsonBytes * items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            elems[i * JsonBytes] = JsonString;
            *(byte**)(elems + i * JsonBytes + 8) = NewString(items[i], mem, msvc);
        }
        ((byte**)vec)[0] = elems;
        ((byte**)vec)[1] = elems + JsonBytes * items.Length;
        ((byte**)vec)[2] = elems + JsonBytes * items.Length;
        byte* top = Alloc(mem, JsonBytes);
        top[0] = JsonArray;
        *(byte**)(top + 8) = vec;
        return top;
    }
}
