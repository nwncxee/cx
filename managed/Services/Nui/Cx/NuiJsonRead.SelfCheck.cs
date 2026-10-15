using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
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
                check.Expect(Dump(Str(mem, msvc, "line\nbreak \u00e9"), msvc) == "\"line\\nbreak \u00e9\"", shape + ": a control character is escaped and other text is kept");
                byte* yaw = new NuiJsonValue((byte*)Str(mem, msvc, "yaw")).Pointer, longer = new NuiJsonValue((byte*)Str(mem, msvc, "a name past fifteen bytes")).Pointer;
                check.Expect(StringIs(yaw, msvc, "yaw"u8) && !StringIs(yaw, msvc, "yaws"u8) && !StringIs(yaw, msvc, "yax"u8)
                             && StringIs(longer, msvc, "a name past fifteen bytes"u8), shape + ": a string matches its own bytes only, inline or not");
                var sent = new List<IntPtr>();
                try
                {
                    string[] items = { "refused", "model", "model.k.zoom", "a reason that is longer than fifteen bytes" };
                    check.Expect(Dump((IntPtr)CxEvent.NewArray(sent, msvc, items), msvc) == "[\"refused\",\"model\",\"model.k.zoom\",\"a reason that is longer than fifteen bytes\"]",
                                 shape + ": the event payload CX builds reads back as the array it was given");
                }
                finally { foreach (IntPtr p in sent) NativeMemory.Free((void*)p); }
            }
            foreach (bool msvc in new[] { false, true })
            {
                IntPtr leaf = Obj(mem, msvc, ("bind", Str(mem, msvc, "yaw")));
                IntPtr tree = Obj(mem, msvc, ("a", Num(mem, 5, 1)), ("kids", Arr(mem, Obj(mem, msvc, ("v", leaf)), Num(mem, 5, 2))));
                IntPtr found = FindBind(tree, "yaw", msvc);
                check.Expect(found != IntPtr.Zero && new NuiJsonValue((byte*)found).Pointer == new NuiJsonValue((byte*)leaf).Pointer, (msvc ? "MSVC" : "libstdc++") + ": a bind leaf nested in an object and an array is found, as a value whose map is the leaf's");
                check.Expect(FindBind(tree, "pitch", msvc) == IntPtr.Zero, (msvc ? "MSVC" : "libstdc++") + ": a name no leaf has finds nothing");
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
        var j = new NuiJsonValue(A(mem, NuiJsonValue.Bytes));
        j.Type = type;
        if (type == 7) j.Float = v; else j.Int = (long)v;
        return (IntPtr)j.Ptr;
    }

    private static IntPtr Bool(List<IntPtr> mem, bool v)
    {
        var j = new NuiJsonValue(A(mem, NuiJsonValue.Bytes));
        j.Type = 4;
        j.Bool = v ? (byte)1 : (byte)0;
        return (IntPtr)j.Ptr;
    }

    private static void SetString(List<IntPtr> mem, NuiStdString s, string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        byte* chars = b.Length > NuiStdString.InlineMax ? A(mem, b.Length + 1) : s.Inline;
        for (int i = 0; i < b.Length; i++) chars[i] = b[i];
        s.Set(chars, (ulong)b.Length);
    }

    private static IntPtr Str(List<IntPtr> mem, bool msvc, string text)
    {
        var own = new NuiStdString(A(mem, NuiStdString.Bytes), msvc);
        var j = new NuiJsonValue(A(mem, NuiJsonValue.Bytes));
        SetString(mem, own, text);
        j.Type = 3;
        j.Pointer = own.Ptr;
        return (IntPtr)j.Ptr;
    }

    private static IntPtr Arr(List<IntPtr> mem, params IntPtr[] items)
    {
        var vec = new NuiJsonVector(A(mem, NuiJsonVector.Bytes));
        var elems = new NuiJsonValue(A(mem, NuiJsonValue.Bytes * items.Length));
        var j = new NuiJsonValue(A(mem, NuiJsonValue.Bytes));
        for (int i = 0; i < items.Length; i++) elems.At(i).CopyFrom(new NuiJsonValue((byte*)items[i]));
        vec.Begin = elems.Ptr;
        vec.End = elems.At(items.Length).Ptr;
        j.Type = 2;
        j.Pointer = vec.Ptr;
        return (IntPtr)j.Ptr;
    }

    /// <summary>An object whose map is a balanced tree of the members sorted by key, in the compiler's node shape.</summary>
    private static IntPtr Obj(List<IntPtr> mem, bool msvc, params (string Key, IntPtr Value)[] members)
    {
        var sorted = new List<(string Key, IntPtr Value)>(members);
        sorted.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        var head = new NuiJsonMapNode(A(mem, NuiJsonMapNode.Bytes));
        if (msvc) head.MarkHead();
        var root = Build(mem, msvc, sorted, 0, sorted.Count - 1, default, head);
        var map = new NuiJsonMap(A(mem, NuiJsonMap.Bytes));
        var j = new NuiJsonValue(A(mem, NuiJsonValue.Bytes));
        map.SetRoot(msvc, root, head);
        j.Type = 1;
        j.Pointer = map.Ptr;
        return (IntPtr)j.Ptr;
    }

    private static NuiJsonMapNode Build(List<IntPtr> mem, bool msvc, List<(string Key, IntPtr Value)> m, int lo, int hi, NuiJsonMapNode parent, NuiJsonMapNode head)
    {
        if (lo > hi) return default;
        int mid = (lo + hi) / 2;
        var n = new NuiJsonMapNode(A(mem, NuiJsonMapNode.Bytes));
        var left = Build(mem, msvc, m, lo, mid - 1, n, head);
        var right = Build(mem, msvc, m, mid + 1, hi, n, head);
        SetString(mem, n.Key(msvc), m[mid].Key);
        n.Value.CopyFrom(new NuiJsonValue((byte*)m[mid].Value));
        n.Link(msvc, left, parent, right, head);
        return n;
    }
}
