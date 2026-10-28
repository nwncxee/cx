using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Graphics.Rhi;

/// <summary>The loose uniform block a translated program reads: <see cref="Resolve"/> hands a member's index out for
/// its name, and inside <see cref="Capture"/> a <see cref="Set"/> writes that member's bytes at its std140 offset.</summary>
internal static unsafe class LooseBlock
{
    /// <summary>One member of a loose block: its name, byte offset, size and (for an array) element stride.</summary>
    internal readonly record struct Member(string Name, int Offset, int Bytes, int Stride);

    /// <summary>The block being resolved or filled, and whether either is open.</summary>
    private sealed class LooseState
    {
        public Member[] Members = [];
        public Dictionary<string, int>? Names;
        public byte[]? Block;
        public bool Open;
        public long Dropped;
    }

    private static readonly LooseState _s = new();

    /// <summary>A program's loose block as its compiled modules lay it out: the reflected members by name and the
    /// block's size, beside the capture table in the translator's own order. Null when neither stage kept the block,
    /// which is what a program that reads no loose uniform compiles to. Cold: once per program.</summary>
    internal static (Dictionary<string, (int Offset, int Size)> Reflected, int Bytes, Member[] Members)?
        Layout(VkProgramText t, byte[] vs, byte[] fs)
    {
        if (t.Loose.Count == 0) return null;
        var names = new List<string>(t.Loose.Count);
        foreach (VkLoose u in t.Loose) names.Add(u.Name);
        uint at = VulkanToolchain.UniformBase + VkGlsl.LooseBinding;
        if ((VkSpirv.Block(fs, 0, at, names) ?? VkSpirv.Block(vs, 0, at, names)) is not { } l) return null;
        var members = new List<Member>(l.Members.Count);
        foreach (VkLoose u in t.Loose)
        {
            if (!l.Members.TryGetValue(u.Name, out var m)) continue;
            int count = u.Array.Length == 0 ? 1 : VkGlsl.ResolveInt(u.Array, t.Fs + t.Vs) ?? 1;
            members.Add(new Member(u.Name, m.Offset, m.Size, count > 1 ? m.Size / count : 0));
        }
        return (l.Members, l.Bytes, members.ToArray());
    }

    /// <summary>A block's bytes, rounded up to the alignment a uniform binding takes.</summary>
    internal static byte[] Block(int bytes) => new byte[(bytes + 15) & ~15];

    /// <summary>A block table to resolve or fill against; build once per translated program.</summary>
    internal static Dictionary<string, int> Index(Member[] members)
    {
        var d = new Dictionary<string, int>(members.Length);
        for (int i = 0; i < members.Length; i++) d[members[i].Name] = i;
        return d;
    }

    /// <summary>Run <paramref name="locate"/> with <paramref name="names"/> answering <see cref="Loc"/>.
    /// Cold: once per program.</summary>
    internal static void Resolve(Dictionary<string, int> names, Action locate)
    {
        LooseState s = _s;
        if (s.Open) throw new InvalidOperationException("LooseBlock: Resolve inside a resolve or capture");
        s.Names = names; s.Block = null; s.Open = true;
        try { locate(); }
        finally { s.Open = false; s.Names = null; }
    }

    /// <summary><paramref name="name"/>'s member index inside an open <see cref="Resolve"/>, -1 for a name the
    /// block lacks -- an opaque uniform, a sampler, a term the stage optimised out -- and -1 outside one.</summary>
    internal static int Loc(string name)
    {
        Dictionary<string, int>? names = _s.Names;
        return names != null && names.TryGetValue(name, out int i) ? i : -1;
    }

    /// <summary>Open a capture into <paramref name="block"/>, laid out as <paramref name="members"/>; every
    /// <see cref="Set"/> until <see cref="EndCapture"/> lands there. Hot: a program bind on the batch path.</summary>
    internal static void Capture(Member[] members, byte[] block)
    {
        LooseState s = _s;
        if (s.Open) throw new InvalidOperationException("LooseBlock: Capture inside a resolve or capture");
        s.Members = members; s.Block = block; s.Open = true;
    }

    /// <summary>Close the capture <see cref="Capture"/> opened; a later write lands nowhere.</summary>
    internal static void EndCapture()
    {
        if (!_s.Open) return;
        _s.Open = false;
        _s.Block = null;
    }

    /// <summary>An int member of the open block.</summary>
    internal static void Set(int loc, int v) => Put(loc, 0, v);

    /// <summary>A float member of the open block.</summary>
    internal static void Set(int loc, float v) => Put(loc, 0, v);

    /// <summary>A vec2 member, lane by lane.</summary>
    internal static void Set(int loc, float a, float b)
    {
        float* v = stackalloc float[2] { a, b };
        Puts(loc, 1, 2, v);
    }

    /// <summary>A vec3 member, lane by lane.</summary>
    internal static void Set(int loc, float a, float b, float c)
    {
        float* v = stackalloc float[3] { a, b, c };
        Puts(loc, 1, 3, v);
    }

    /// <summary>A vec4 member, lane by lane.</summary>
    internal static void Set(int loc, float a, float b, float c, float d)
    {
        float* v = stackalloc float[4] { a, b, c, d };
        Puts(loc, 1, 4, v);
    }

    /// <summary>An ivec2 member, lane by lane.</summary>
    internal static void Set(int loc, int a, int b)
    {
        int* v = stackalloc int[2] { a, b };
        Puts(loc, 1, 2, v);
    }

    /// <summary>An ivec3 member, lane by lane.</summary>
    internal static void Set(int loc, int a, int b, int c)
    {
        int* v = stackalloc int[3] { a, b, c };
        Puts(loc, 1, 3, v);
    }

    /// <summary>A matrix member, its sixteen lanes in the order the struct stores them. Its size comes from the
    /// type, so no caller counts lanes.</summary>
    internal static void Set(int loc, in Matrix4x4 m) => Put(loc, 0, m);

    /// <summary>Lanes of one element (a vec2, a vec3, a mat4) into the open block.</summary>
    internal static void Set(int loc, ReadOnlySpan<float> lanes)
    {
        Span<byte> p = At(loc, 0, lanes.Length * sizeof(float));
        if (!p.IsEmpty) MemoryMarshal.AsBytes(lanes).CopyTo(p);
    }

    /// <summary>Integer lanes of one element (an ivec2, an ivec4) into the open block.</summary>
    internal static void Set(int loc, ReadOnlySpan<int> lanes)
    {
        Span<byte> p = At(loc, 0, lanes.Length * sizeof(int));
        if (!p.IsEmpty) MemoryMarshal.AsBytes(lanes).CopyTo(p);
    }

    /// <summary>One float per ELEMENT of an array member, at that member's own stride: std140 pads every element of
    /// a float array to 16 bytes, so a contiguous copy would put all but the first in the padding.</summary>
    internal static void SetEach(int loc, ReadOnlySpan<float> elements)
    {
        for (int e = 0; e < elements.Length; e++)
        {
            Span<byte> p = At(loc, e, sizeof(float));
            if (p.IsEmpty) return;
            MemoryMarshal.Write(p, in elements[e]);
        }
    }

    /// <summary>One int per ELEMENT of an array member, at that member's own stride (16 bytes under std140).</summary>
    internal static void SetEach(int loc, ReadOnlySpan<int> elements)
    {
        fixed (int* v = elements) Puts(loc, elements.Length, 1, v);
    }

    /// <summary>One ELEMENT per <paramref name="lanes"/> floats of <paramref name="elements"/> -- a vec4 array is
    /// four -- each at the array member's own stride.</summary>
    internal static void SetEach(int loc, int lanes, ReadOnlySpan<float> elements)
    {
        if (lanes <= 0) return;
        fixed (float* v = elements) Puts(loc, elements.Length / lanes, lanes, v);
    }

    /// <summary>Writes that named a location the block does not have or overran it, since boot.</summary>
    internal static long Dropped => _s.Dropped;

    /// <summary>Where a write of <paramref name="bytes"/> into member <paramref name="loc"/>'s element
    /// <paramref name="elem"/> lands, or an empty span (counted) for a location or element the block does not have.</summary>
    private static Span<byte> At(int loc, int elem, int bytes)
    {
        LooseState s = _s;
        if (s.Block == null || (uint)loc >= (uint)s.Members.Length) { if (s.Block != null && loc >= 0) s.Dropped++; return default; }
        Member m = s.Members[loc];
        int off = m.Offset + elem * m.Stride;
        if (elem > 0 && m.Stride == 0 || off + bytes > m.Offset + Math.Max(m.Bytes, bytes) || off + bytes > s.Block.Length) { s.Dropped++; return default; }
        return s.Block.AsSpan(off, bytes);
    }

    private static void Put<T>(int loc, int elem, T v) where T : unmanaged
    {
        Span<byte> p = At(loc, elem, sizeof(T));
        if (!p.IsEmpty) MemoryMarshal.Write(p, in v);
    }

    /// <summary><paramref name="count"/> elements of <paramref name="lanes"/> words each, at the member's stride.</summary>
    private static void Puts<T>(int loc, int count, int lanes, T* v) where T : unmanaged
    {
        var src = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(v, Math.Max(count, 0) * lanes));
        int each = lanes * sizeof(T);
        for (int e = 0; e < count; e++)
        {
            Span<byte> p = At(loc, e, each);
            if (p.IsEmpty) return;
            src.Slice(e * each, each).CopyTo(p);
        }
    }

    /// <summary>A two-member block: a name resolves to its member index, a write lands at that member's offset, an
    /// array element at its stride, and an element past the array is counted. Oracle: the hand-laid std140 offsets
    /// below and the literal values written.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LooseBlock.SelfCheck");
        Member[] members = [new("uA", 0, 4, 0), new("uV", 16, 32, 16)];
        Dictionary<string, int> names = Index(members);
        int a = -2, v = -2, none = -2;
        Resolve(names, () => { a = Loc("uA"); v = Loc("uV"); none = Loc("uMissing"); });
        check.Expect(a == 0 && v == 1 && none == -1, $"a name resolves to its member index, -1 for one the block lacks ({a}, {v}, {none})");
        check.Expect(Loc("uA") == -1, "outside a resolve no name has a location");
        var block = new byte[48];
        long dropped = Dropped;
        Capture(members, block);
        Set(a, 7);
        SetEach(v, 4, [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f]);
        Set(none, 9f);
        SetEach(v, 4, [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f]);
        EndCapture();
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(block);
        check.Expect(MemoryMarshal.Read<int>(block) == 7, "an int lands at its member's offset 0");
        check.Expect(f[4] == 1f && f[8] == 5f && f[11] == 8f, "a vec4 array's two elements land at 16 and 32");
        check.Expect(Dropped - dropped == 1, $"a third element past the two-element array is counted, the location the block lacks ignored ({Dropped - dropped})");
        log.Info(check.Ok ? "LooseBlock.SelfCheck: passed" : "LooseBlock.SelfCheck: FAILED");
    }
}
