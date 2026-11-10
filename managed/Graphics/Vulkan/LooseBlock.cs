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
    /// <summary>One member of a loose block: its name, byte offset, size, (for an array) element stride, and whether
    /// GLSL declares it of an integer kind (<c>int</c>, <c>uint</c>, <c>bool</c> and their vectors).</summary>
    internal readonly record struct Member(string Name, int Offset, int Bytes, int Stride, bool Ints = false);

    /// <summary>The block being resolved or filled, and whether either is open.</summary>
    private sealed class LooseState
    {
        public Member[] Members = [];
        public Dictionary<string, int>? Names;
        public byte[]? Block;
        public bool Open;
        public long Dropped;
        /// <summary>Writes whose kind (int or float) disagreed with the member's, converted; and the names said so far.</summary>
        public long Mismatched;
        public readonly HashSet<string> Said = new();
        public ExtenderLog? Log;
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
        // the module names a member as the block spells it, which is not the declared name for every member
        foreach (VkLoose u in t.Loose) names.Add(VkGlsl.Spelling(u.Name));
        uint at = VulkanToolchain.UniformBase + VkGlsl.LooseBinding;
        if ((VkSpirv.Block(fs, 0, at, names) ?? VkSpirv.Block(vs, 0, at, names)) is not { } l) return null;
        return (l.Members, l.Bytes, Members(t.Loose, l.Members));
    }

    /// <summary>The capture table of <paramref name="loose"/> as <paramref name="reflected"/> lays it out, in the
    /// translator's order; a member the module dropped is left out, and an array's stride is its size per element.</summary>
    internal static Member[] Members(IReadOnlyList<VkLoose> loose, Dictionary<string, (int Offset, int Size)> reflected)
    {
        var members = new List<Member>(reflected.Count);
        foreach (VkLoose u in loose)
        {
            if (!reflected.TryGetValue(VkGlsl.Spelling(u.Name), out var m)) continue;
            int count = u.Count;
            members.Add(new Member(u.Name, m.Offset, m.Size, count > 1 ? m.Size / count : 0, IntKind(u.Type)));
        }
        return members.ToArray();
    }

    /// <summary>Is GLSL type <paramref name="type"/> stored as integers: <c>int</c>, <c>uint</c>, <c>bool</c> or a vector of one.</summary>
    internal static bool IntKind(string type)
        => type is "int" or "uint" or "bool" || type.StartsWith("ivec", StringComparison.Ordinal)
           || type.StartsWith("uvec", StringComparison.Ordinal) || type.StartsWith("bvec", StringComparison.Ordinal);

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
    internal static void Set(int loc, int v) => Ints(loc, 1, 1, new ReadOnlySpan<int>(in v));

    /// <summary>A float member of the open block.</summary>
    internal static void Set(int loc, float v) => Floats(loc, 1, 1, new ReadOnlySpan<float>(in v));

    /// <summary>A vec2 member, lane by lane.</summary>
    internal static void Set(int loc, float a, float b) => Floats(loc, 1, 2, [a, b]);

    /// <summary>A vec3 member, lane by lane.</summary>
    internal static void Set(int loc, float a, float b, float c) => Floats(loc, 1, 3, [a, b, c]);

    /// <summary>A vec4 member, lane by lane.</summary>
    internal static void Set(int loc, float a, float b, float c, float d) => Floats(loc, 1, 4, [a, b, c, d]);

    /// <summary>An ivec2 member, lane by lane.</summary>
    internal static void Set(int loc, int a, int b) => Ints(loc, 1, 2, [a, b]);

    /// <summary>An ivec3 member, lane by lane.</summary>
    internal static void Set(int loc, int a, int b, int c) => Ints(loc, 1, 3, [a, b, c]);

    /// <summary>A matrix member, its sixteen lanes in the order the struct stores them. Its size comes from the
    /// type, so no caller counts lanes.</summary>
    internal static void Set(int loc, in Matrix4x4 m) => Floats(loc, 1, 16, Lanes(in m));

    /// <summary>A vec2 member from the struct that names its lanes; the count comes from the type.</summary>
    internal static void Set(int loc, in Vector2 v) => Floats(loc, 1, 2, Lanes(in v));

    /// <summary>A vec3 member from the struct that names its lanes; the count comes from the type.</summary>
    internal static void Set(int loc, in Vector3 v) => Floats(loc, 1, 3, Lanes(in v));

    /// <summary>A vec4 member from the struct that names its lanes; the count comes from the type.</summary>
    internal static void Set(int loc, in Vector4 v) => Floats(loc, 1, 4, Lanes(in v));

    /// <summary>One value's floats, their count taken from its type rather than from a caller.</summary>
    private static ReadOnlySpan<float> Lanes<T>(in T v) where T : unmanaged
        => MemoryMarshal.Cast<T, float>(new ReadOnlySpan<T>(in v));

    /// <summary>Lanes of one element (a vec2, a vec3, a mat4) into the open block.</summary>
    internal static void Set(int loc, ReadOnlySpan<float> lanes) => Floats(loc, 1, lanes.Length, lanes);

    /// <summary>Integer lanes of one element (an ivec2, an ivec4) into the open block.</summary>
    internal static void Set(int loc, ReadOnlySpan<int> lanes) => Ints(loc, 1, lanes.Length, lanes);

    /// <summary>One float per ELEMENT of an array member, at that member's own stride: std140 pads every element of
    /// a float array to 16 bytes, so a contiguous copy would put all but the first in the padding.</summary>
    internal static void SetEach(int loc, ReadOnlySpan<float> elements) => Floats(loc, elements.Length, 1, elements);

    /// <summary>One int per ELEMENT of an array member, at that member's own stride (16 bytes under std140).</summary>
    internal static void SetEach(int loc, ReadOnlySpan<int> elements) => Ints(loc, elements.Length, 1, elements);

    /// <summary>One ELEMENT per <paramref name="lanes"/> floats of <paramref name="elements"/> -- a vec4 array is
    /// four -- each at the array member's own stride.</summary>
    internal static void SetEach(int loc, int lanes, ReadOnlySpan<float> elements)
    {
        if (lanes <= 0) return;
        Floats(loc, elements.Length / lanes, lanes, elements);
    }

    /// <summary>Writes that named a location the block does not have or overran it, since boot.</summary>
    internal static long Dropped => _s.Dropped;

    /// <summary>Writes of an int into a float member, or a float into an int one, since boot: each converted, and
    /// said once per member name.</summary>
    internal static long Mismatched => _s.Mismatched;

    /// <summary>Where a mismatched write is said. Cold: once at boot.</summary>
    internal static void Attach(ExtenderLog log) => _s.Log = log;

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

    /// <summary><paramref name="count"/> elements of <paramref name="lanes"/> ints each, at the member's stride; into
    /// a float member each lane lands as the float it names, said once.</summary>
    private static void Ints(int loc, int count, int lanes, ReadOnlySpan<int> src)
    {
        for (int e = 0; e < count; e++)
        {
            Span<byte> p = At(loc, e, lanes * sizeof(int));
            if (p.IsEmpty) return;
            ReadOnlySpan<int> one = src.Slice(e * lanes, lanes);
            if (_s.Members[loc].Ints) { MemoryMarshal.AsBytes(one).CopyTo(p); continue; }
            Mismatch(loc, "an int");
            Span<float> f = MemoryMarshal.Cast<byte, float>(p);
            for (int k = 0; k < lanes; k++) f[k] = one[k];
        }
    }

    /// <summary><paramref name="count"/> elements of <paramref name="lanes"/> floats each, at the member's stride; into
    /// an int member each lane lands truncated to the int it names, said once.</summary>
    private static void Floats(int loc, int count, int lanes, ReadOnlySpan<float> src)
    {
        for (int e = 0; e < count; e++)
        {
            Span<byte> p = At(loc, e, lanes * sizeof(float));
            if (p.IsEmpty) return;
            ReadOnlySpan<float> one = src.Slice(e * lanes, lanes);
            if (!_s.Members[loc].Ints) { MemoryMarshal.AsBytes(one).CopyTo(p); continue; }
            Mismatch(loc, "a float");
            Span<int> n = MemoryMarshal.Cast<byte, int>(p);
            for (int k = 0; k < lanes; k++) n[k] = (int)one[k];
        }
    }

    /// <summary>Count a write whose kind is not its member's, and say the member the first time.</summary>
    private static void Mismatch(int loc, string kind)
    {
        LooseState s = _s;
        s.Mismatched++;
        string name = s.Members[loc].Name;
        if (s.Log != null && s.Said.Add(name))
            s.Log.Always($"LooseBlock: {kind} was written into {name}, which the shader declares the other kind -- converted, but the "
                       + "call site names the wrong overload and must be fixed");
    }

    /// <summary>A two-member block: a name resolves to its member index, a write lands at that member's offset, an
    /// array element at its stride, and an element past the array is counted. Oracle: the hand-laid std140 offsets
    /// below and the literal values written.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LooseBlock.SelfCheck");
        Member[] members = [new("uA", 0, 4, 0, Ints: true), new("uV", 16, 32, 16)];
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
        Member[] vecs = [new("uV2", 0, 8, 0), new("uV3", 16, 12, 0), new("uV4", 32, 16, 0)];
        var vb = new byte[48];
        Capture(vecs, vb);
        Set(0, new Vector2(11f, 12f));
        Set(1, new Vector3(21f, 22f, 23f));
        Set(2, new Vector4(31f, 32f, 33f, 34f));
        EndCapture();
        ReadOnlySpan<float> vf = MemoryMarshal.Cast<byte, float>(vb);
        check.Expect(vf[0] == 11f && vf[1] == 12f, $"a Vector2 lands as its two lanes ({vf[0]}, {vf[1]})");
        check.Expect(vf[4] == 21f && vf[6] == 23f, $"a Vector3 lands as three lanes at its member's offset ({vf[4]}, {vf[6]})");
        check.Expect(vf[8] == 31f && vf[11] == 34f, $"a Vector4 lands as four lanes at its member's offset ({vf[8]}, {vf[11]})");
        check.Expect(vf[3] == 0f && vf[7] == 0f, $"neither vector write spills into the padding after it ({vf[3]}, {vf[7]})");
        Member[] typed = [new("uSize", 0, 8, 0), new("uFlag", 8, 4, 0, Ints: true)];
        var tb = new byte[16];
        long mismatched = Mismatched;
        ExtenderLog? said = _s.Log;
        _s.Log = null;   // the examples' mismatches are on purpose, and must not use up a real member's one line
        try
        {
            Capture(typed, tb);
            Set(0, 1781, 965);
            Set(1, 2f);
            EndCapture();
        }
        finally { _s.Log = said; }
        ReadOnlySpan<float> tf = MemoryMarshal.Cast<byte, float>(tb);
        check.Expect(tf[0] == 1781f && tf[1] == 965f, $"ints written into a vec2 land as the floats they name, not their bit patterns ({tf[0]}, {tf[1]})");
        check.Expect(MemoryMarshal.Read<int>(tb.AsSpan(8)) == 2, "a float written into an int member lands as the int it names");
        check.Expect(Mismatched - mismatched == 2, $"both mismatched writes are counted ({Mismatched - mismatched})");
        log.Info(check.Ok ? "LooseBlock.SelfCheck: passed" : "LooseBlock.SelfCheck: FAILED");
    }
}
