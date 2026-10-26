using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Graphics.Rhi;

/// <summary>
/// GL Alloy's uniform setters aimed at a Vulkan block: in <see cref="Resolve"/> a location is a loose member's index,
/// in <see cref="Capture"/> each <c>glUniform*</c> writes at that member's offset -- the same values GL's draws read.
/// ponytail: the bridge while GL Alloy lives; once it is deleted the setters write the block themselves.
/// </summary>
internal static unsafe class VkUniformShim
{
    /// <summary>One member of a loose block: its name, byte offset, size and (for an array) element stride.</summary>
    internal readonly record struct Member(string Name, int Offset, int Bytes, int Stride);

    /// <summary>The block being resolved or filled, and the GL entry points set aside while it is.</summary>
    private sealed class ShimState
    {
        public Member[] Members = [];
        public Dictionary<string, int>? Names;
        public byte[]? Block;
        public bool Active;
        public long Dropped;
        public delegate* unmanaged<uint, byte*, int> GetLoc;
        public delegate* unmanaged<uint, byte*, uint> GetBlock;
        public delegate* unmanaged<uint, void> Use;
        public delegate* unmanaged<int, int, void> U1i;
        public delegate* unmanaged<int, float, void> U1f;
        public delegate* unmanaged<int, float, float, void> U2f;
        public delegate* unmanaged<int, int, int, void> U2i;
        public delegate* unmanaged<int, uint, uint, void> U2ui;
        public delegate* unmanaged<int, float, float, float, void> U3f;
        public delegate* unmanaged<int, int, int, int, void> U3i;
        public delegate* unmanaged<int, float, float, float, float, void> U4f;
        public delegate* unmanaged<int, int, float*, void> U1fv, U2fv, U3fv, U4fv;
        public delegate* unmanaged<int, int, int*, void> U1iv, U2iv, U4iv;
        public delegate* unmanaged<int, int, byte, float*, void> M4fv;
    }

    private static readonly ShimState _s = new();

    /// <summary>Program names the shim answers for: far above any name GL hands out, so no real program collides.</summary>
    internal const uint ProgramBase = 0xC0000000;   // not-an-offset: a reserved name range

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

    /// <summary>Run <paramref name="locate"/> with locations answered from <paramref name="names"/>: a member's index,
    /// -1 for any other name. GL's own uniform writes and program binds inside it go nowhere. Cold: once per program.</summary>
    internal static void Resolve(Dictionary<string, int> names, Action locate)
    {
        ShimState s = _s;
        if (s.Active) throw new InvalidOperationException("VkUniformShim: Resolve inside a resolve or capture");
        s.Names = names; s.Block = null;
        Swap(true);
        try { locate(); }
        finally { Swap(false); s.Names = null; }
    }

    /// <summary>Open a capture into <paramref name="block"/>, laid out as <paramref name="members"/>; every
    /// <c>glUniform*</c> until <see cref="EndCapture"/> lands there. Hot: a program bind on the batch path.</summary>
    internal static void Capture(Member[] members, byte[] block)
    {
        ShimState s = _s;
        if (s.Active) throw new InvalidOperationException("VkUniformShim: Capture inside a resolve or capture");
        s.Members = members; s.Block = block;
        Swap(true);
    }

    /// <summary>Close the capture <see cref="Capture"/> opened; GL's entry points are back.</summary>
    internal static void EndCapture()
    {
        if (!_s.Active) return;
        Swap(false);
        _s.Block = null;
    }

    /// <summary>Write into the open capture directly, for a caller with no GL setter to borrow.</summary>
    internal static void Set(int loc, int v) => Put(loc, 0, v);

    /// <inheritdoc cref="Set(int, int)"/>
    internal static void Set(int loc, float v) => Put(loc, 0, v);

    /// <summary>A matrix into the open capture, its sixteen lanes in the order the struct stores them -- which is
    /// the order <c>glUniformMatrix4fv</c> takes them in. Its size comes from the type, so no caller counts lanes.</summary>
    internal static void Set(int loc, in Matrix4x4 m) => Put(loc, 0, m);

    /// <summary>Lanes of one element (a vec2, a vec3, a mat4) into the open capture.</summary>
    internal static void Set(int loc, ReadOnlySpan<float> lanes)
    {
        Span<byte> p = At(loc, 0, lanes.Length * sizeof(float));
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

    /// <summary>Integer lanes of one element (an ivec2, an ivec4) into the open capture.</summary>
    internal static void Set(int loc, ReadOnlySpan<int> lanes)
    {
        Span<byte> p = At(loc, 0, lanes.Length * sizeof(int));
        if (!p.IsEmpty) MemoryMarshal.AsBytes(lanes).CopyTo(p);
    }

    /// <summary>Writes that named a location the block does not have or overran it, since boot.</summary>
    internal static long Dropped => _s.Dropped;

    private static void Swap(bool on)
    {
        ShimState s = _s;
        if (on)
        {
            s.GetLoc = Gl.GetUniformLocation; s.GetBlock = Gl.GetUniformBlockIndex; s.Use = Gl.UseProgram;
            s.U1i = Gl.Uniform1i; s.U1f = Gl.Uniform1f; s.U2f = Gl.Uniform2f; s.U2i = Gl.Uniform2i; s.U2ui = Gl.Uniform2ui;
            s.U3f = Gl.Uniform3f; s.U3i = Gl.Uniform3i; s.U4f = Gl.Uniform4f;
            s.U1fv = Gl.Uniform1fv; s.U2fv = Gl.Uniform2fv; s.U3fv = Gl.Uniform3fv; s.U4fv = Gl.Uniform4fv;
            s.U1iv = Gl.Uniform1iv; s.U2iv = Gl.Uniform2iv; s.U4iv = Gl.Uniform4iv; s.M4fv = Gl.UniformMatrix4fv;
            Gl.GetUniformLocation = &GetLoc; Gl.GetUniformBlockIndex = &GetBlock; Gl.UseProgram = &Use;
            Gl.Uniform1i = &U1i; Gl.Uniform1f = &U1f; Gl.Uniform2f = &U2f; Gl.Uniform2i = &U2i; Gl.Uniform2ui = &U2ui;
            Gl.Uniform3f = &U3f; Gl.Uniform3i = &U3i; Gl.Uniform4f = &U4f;
            Gl.Uniform1fv = &U1fv; Gl.Uniform2fv = &U2fv; Gl.Uniform3fv = &U3fv; Gl.Uniform4fv = &U4fv;
            Gl.Uniform1iv = &U1iv; Gl.Uniform2iv = &U2iv; Gl.Uniform4iv = &U4iv; Gl.UniformMatrix4fv = &M4fv;
            s.Active = true;
            return;
        }
        Gl.GetUniformLocation = s.GetLoc; Gl.GetUniformBlockIndex = s.GetBlock; Gl.UseProgram = s.Use;
        Gl.Uniform1i = s.U1i; Gl.Uniform1f = s.U1f; Gl.Uniform2f = s.U2f; Gl.Uniform2i = s.U2i; Gl.Uniform2ui = s.U2ui;
        Gl.Uniform3f = s.U3f; Gl.Uniform3i = s.U3i; Gl.Uniform4f = s.U4f;
        Gl.Uniform1fv = s.U1fv; Gl.Uniform2fv = s.U2fv; Gl.Uniform3fv = s.U3fv; Gl.Uniform4fv = s.U4fv;
        Gl.Uniform1iv = s.U1iv; Gl.Uniform2iv = s.U2iv; Gl.Uniform4iv = s.U4iv; Gl.UniformMatrix4fv = s.M4fv;
        s.Active = false;
    }

    /// <summary>Where a write of <paramref name="bytes"/> into member <paramref name="loc"/>'s element
    /// <paramref name="elem"/> lands, or an empty span (counted) for a location or element the block does not have.</summary>
    private static Span<byte> At(int loc, int elem, int bytes)
    {
        ShimState s = _s;
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

    [UnmanagedCallersOnly]
    private static int GetLoc(uint prog, byte* name)
    {
        Dictionary<string, int>? names = _s.Names;
        if (names == null) return -1;
        string n = Marshal.PtrToStringAnsi((IntPtr)name) ?? "";
        return names.TryGetValue(n, out int i) ? i : -1;
    }

    /// <summary>A translated program declares the blocks its host binds whether GL would call them active or not.</summary>
    [UnmanagedCallersOnly] private static uint GetBlock(uint prog, byte* name) => 0;
    [UnmanagedCallersOnly] private static void Use(uint prog) { }
    [UnmanagedCallersOnly] private static void U1i(int l, int a) => Put(l, 0, a);
    [UnmanagedCallersOnly] private static void U1f(int l, float a) => Put(l, 0, a);
    [UnmanagedCallersOnly] private static void U2f(int l, float a, float b) { float* v = stackalloc float[2] { a, b }; Puts(l, 1, 2, v); }
    [UnmanagedCallersOnly] private static void U2i(int l, int a, int b) { int* v = stackalloc int[2] { a, b }; Puts(l, 1, 2, v); }
    [UnmanagedCallersOnly] private static void U2ui(int l, uint a, uint b) { uint* v = stackalloc uint[2] { a, b }; Puts(l, 1, 2, v); }
    [UnmanagedCallersOnly] private static void U3f(int l, float a, float b, float c) { float* v = stackalloc float[3] { a, b, c }; Puts(l, 1, 3, v); }
    [UnmanagedCallersOnly] private static void U3i(int l, int a, int b, int c) { int* v = stackalloc int[3] { a, b, c }; Puts(l, 1, 3, v); }
    [UnmanagedCallersOnly] private static void U4f(int l, float a, float b, float c, float d) { float* v = stackalloc float[4] { a, b, c, d }; Puts(l, 1, 4, v); }
    [UnmanagedCallersOnly] private static void U1fv(int l, int n, float* v) => Puts(l, n, 1, v);
    [UnmanagedCallersOnly] private static void U2fv(int l, int n, float* v) => Puts(l, n, 2, v);
    [UnmanagedCallersOnly] private static void U3fv(int l, int n, float* v) => Puts(l, n, 3, v);
    [UnmanagedCallersOnly] private static void U4fv(int l, int n, float* v) => Puts(l, n, 4, v);
    [UnmanagedCallersOnly] private static void U1iv(int l, int n, int* v) => Puts(l, n, 1, v);
    [UnmanagedCallersOnly] private static void U2iv(int l, int n, int* v) => Puts(l, n, 2, v);
    [UnmanagedCallersOnly] private static void U4iv(int l, int n, int* v) => Puts(l, n, 4, v);
    [UnmanagedCallersOnly] private static void M4fv(int l, int n, byte transpose, float* v) => Puts(l, n, 16, v);

    /// <summary>A two-member block captured through the shim: a resolved name lands at its offset, a vec4 array's
    /// second element at its stride, and a name or element the block lacks is dropped. Oracle: hand-laid std140
    /// offsets and the literal values written.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkUniformShim.SelfCheck");
        Member[] members = [new("uA", 0, 4, 0), new("uV", 16, 32, 16)];
        Dictionary<string, int> names = Index(members);
        int a = -2, v = -2, none = -2;
        Resolve(names, () =>
        {
            a = GlProgram.Loc(ProgramBase, "uA");
            v = GlProgram.Loc(ProgramBase, "uV");
            none = GlProgram.Loc(ProgramBase, "uMissing");
        });
        check.Expect(a == 0 && v == 1 && none == -1, $"locations are member indices, -1 for a name the block lacks ({a}, {v}, {none})");
        var block = new byte[48];
        long dropped = Dropped;
        Capture(members, block);
        // through the entry-point slots themselves: the swap is what is under test, not a loose uniform write
        delegate* unmanaged<int, int, void> u1i = Gl.Uniform1i;
        delegate* unmanaged<int, float, void> u1f = Gl.Uniform1f;
        delegate* unmanaged<int, int, float*, void> u4fv = Gl.Uniform4fv;
        u1i(a, 7);
        float* two = stackalloc float[8] { 1, 2, 3, 4, 5, 6, 7, 8 };
        u4fv(v, 2, two);
        u1f(none, 9f);
        u4fv(v, 3, two);
        EndCapture();
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(block);
        check.Expect(MemoryMarshal.Read<int>(block) == 7, "Uniform1i lands at offset 0");
        check.Expect(f[4] == 1f && f[8] == 5f && f[11] == 8f, "a vec4 array's two elements land at 16 and 32");
        check.Expect(Dropped - dropped == 1, $"the third element past the array is dropped, the unknown location ignored ({Dropped - dropped})");
        check.Expect(Gl.Uniform1i != (delegate* unmanaged<int, int, void>)&U1i, "the capture hands GL its entry points back");
        log.Info(check.Ok ? "VkUniformShim.SelfCheck: passed" : "VkUniformShim.SelfCheck: FAILED");
    }
}
