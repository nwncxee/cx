using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Services.Nui;

internal static unsafe partial class NuiJsonRead
{
    /// <summary>Builds the two compilers' trees by hand and reads them back. Oracle: the literal JSON text each tree was built to
    /// mean. The libstdc++ shape is proven against the live client by the harness run; the MSVC shape is the layout the Windows
    /// decompile shows and is proven only by the Proton run.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var mem = new List<IntPtr>();
        try
        {
            foreach (bool msvc in new[] { false, true })
            {
                string shape = msvc ? "MSVC" : "libstdc++";
                IntPtr doc = Obj(mem, msvc, ("name", Str(mem, msvc, "a \"quoted\" word that is longer than fifteen bytes")), ("n", Num(mem, 5, 42)),
                    ("list", Arr(mem, Num(mem, 5, 1), Str(mem, msvc, "x"), Bool(mem, true))), ("zeta", Num(mem, 0, 0)),
                    ("inner", Obj(mem, msvc, ("z", Num(mem, 0, 0)), ("a", Num(mem, 7, 2.5)))));
                check.Expect(Dump(doc, msvc) == "{\"inner\":{\"a\":2.5,\"z\":null},\"list\":[1,\"x\",true],\"n\":42,\"name\":\"a \\\"quoted\\\" word that is longer than fifteen bytes\",\"zeta\":null}",
                             shape + ": a tree reads back as sorted compact JSON");
                check.Expect(Dump(Obj(mem, msvc), msvc) == "{}", shape + ": an empty object is {}");
                var sent = new List<IntPtr>();
                try
                {
                    string[] items = { "refused", "model", "model.k.zoom", "a reason that is longer than fifteen bytes" };
                    check.Expect(Dump((IntPtr)CxEvent.NewArray(sent, msvc, items), msvc) == "[\"refused\",\"model\",\"model.k.zoom\",\"a reason that is longer than fifteen bytes\"]",
                                 shape + ": the event payload CX builds reads back as the array it was given");
                }
                finally { foreach (IntPtr p in sent) NativeMemory.Free((void*)p); }
            }
            check.Expect(Dump(Num(mem, 9, 0), false) == null, "a value of an unknown type ends the dump");
        }
        finally { foreach (IntPtr p in mem) NativeMemory.Free((void*)p); }
        log.Info(check.Ok ? "NuiJsonRead.SelfCheck: passed" : "NuiJsonRead.SelfCheck: FAILED");
    }

    private static byte* A(List<IntPtr> mem, int n)
    {
        byte* p = (byte*)NativeMemory.AllocZeroed((nuint)n);
        mem.Add((IntPtr)p);
        return p;
    }

    private static IntPtr Num(List<IntPtr> mem, byte type, double v)
    {
        byte* j = A(mem, 16);
        j[0] = type;
        if (type == 7) *(double*)(j + 8) = v; else *(long*)(j + 8) = (long)v;
        return (IntPtr)j;
    }

    private static IntPtr Bool(List<IntPtr> mem, bool v)
    {
        byte* j = A(mem, 16);
        j[0] = 4;
        j[8] = v ? (byte)1 : (byte)0;
        return (IntPtr)j;
    }

    private static void SetString(List<IntPtr> mem, byte* s, bool msvc, string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        bool heap = b.Length > 15;
        byte* chars = heap ? A(mem, b.Length + 1) : msvc ? s : s + 16;
        for (int i = 0; i < b.Length; i++) chars[i] = b[i];
        if (msvc)
        {
            if (heap) *(byte**)s = chars;
            *(ulong*)(s + 16) = (ulong)b.Length;
            *(ulong*)(s + 24) = heap ? (ulong)b.Length : 15UL;
        }
        else
        {
            *(byte**)s = chars;
            *(ulong*)(s + 8) = (ulong)b.Length;
        }
    }

    private static IntPtr Str(List<IntPtr> mem, bool msvc, string text)
    {
        byte* own = A(mem, 32), j = A(mem, 16);
        SetString(mem, own, msvc, text);
        j[0] = 3;
        *(byte**)(j + 8) = own;
        return (IntPtr)j;
    }

    private static IntPtr Arr(List<IntPtr> mem, params IntPtr[] items)
    {
        byte* vec = A(mem, 24), elems = A(mem, 16 * items.Length), j = A(mem, 16);
        for (int i = 0; i < items.Length; i++) Buffer.MemoryCopy((void*)items[i], elems + 16 * i, 16, 16);
        ((byte**)vec)[0] = elems;
        ((byte**)vec)[1] = elems + 16 * items.Length;
        j[0] = 2;
        *(byte**)(j + 8) = vec;
        return (IntPtr)j;
    }

    /// <summary>An object whose map is a balanced tree of the members sorted by key, in the compiler's node shape.</summary>
    private static IntPtr Obj(List<IntPtr> mem, bool msvc, params (string Key, IntPtr Value)[] members)
    {
        var sorted = new List<(string Key, IntPtr Value)>(members);
        sorted.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        byte* head = A(mem, 80);
        if (msvc) head[25] = 1;
        byte* root = Build(mem, msvc, sorted, 0, sorted.Count - 1, null, head);
        byte* map = A(mem, 48), j = A(mem, 16);
        if (msvc) { *(byte**)map = head; *(byte**)(head + 8) = root == null ? head : root; }
        else *(byte**)(map + 16) = root;
        j[0] = 1;
        *(byte**)(j + 8) = map;
        return (IntPtr)j;
    }

    private static byte* Build(List<IntPtr> mem, bool msvc, List<(string Key, IntPtr Value)> m, int lo, int hi, byte* parent, byte* head)
    {
        if (lo > hi) return null;
        int mid = (lo + hi) / 2;
        byte* n = A(mem, 80);
        byte* left = Build(mem, msvc, m, lo, mid - 1, n, head), right = Build(mem, msvc, m, mid + 1, hi, n, head);
        SetString(mem, n + 32, msvc, m[mid].Key);
        Buffer.MemoryCopy((void*)m[mid].Value, n + 64, 16, 16);
        if (msvc)
        {
            *(byte**)n = left == null ? head : left;
            *(byte**)(n + 8) = parent == null ? head : parent;
            *(byte**)(n + 16) = right == null ? head : right;
        }
        else
        {
            *(byte**)(n + 8) = parent;
            *(byte**)(n + 16) = left;
            *(byte**)(n + 24) = right;
        }
        return n;
    }
}
