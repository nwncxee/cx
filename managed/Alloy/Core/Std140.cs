using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>std140's <c>ivec2</c>, for a block's staging struct (<c>System.Numerics</c> has no integer
/// vectors).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Int2(int x, int y)
{
    public int X = x, Y = y;
}

/// <summary>std140's <c>ivec4</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Int4(int x, int y, int z, int w)
{
    public int X = x, Y = y, Z = z, W = w;
}

/// <summary>std140 arithmetic over a uniform block as the GLSL declares it: the member list, each member's
/// byte offset, and the pin that holds a C# staging struct to it.</summary>
internal static class Std140
{
    /// <summary>The named block's declaration in <paramref name="src"/> -- the <c>layout</c> line through the
    /// member before the closing brace, comments and whitespace dropped; null when there is no such block.</summary>
    internal static string? Members(string src, string block)
    {
        int at = -1;
        for (int from = 0; ; )
        {
            at = src.IndexOf("uniform " + block, from, StringComparison.Ordinal);
            if (at < 0) return null;
            int after = at + 8 + block.Length;
            if (after >= src.Length || !(char.IsLetterOrDigit(src[after]) || src[after] == '_')) break;
            from = at + 1;
        }
        int start = src.LastIndexOf('\n', at) + 1, end = src.IndexOf("};", at, StringComparison.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (string line in src[start..(end < 0 ? src.Length : end)].Split('\n'))
        {
            int c = line.IndexOf("//", StringComparison.Ordinal);
            foreach (char ch in c < 0 ? line : line[..c]) if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Every member name in a <see cref="Members"/> body, in declaration order: what a stage that
    /// reads the block spelled as loose uniforms before the block existed.</summary>
    internal static List<string> Names(string members)
    {
        var names = new List<string>();
        foreach (string m in members[(members.IndexOf('{') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int u = 0;
            while (u < m.Length - 1 && !(m[u] == 'u' && char.IsUpper(m[u + 1]))) u++;   // the name: its first u<Upper>
            if (u >= m.Length - 1) continue;
            int lb = m.IndexOf('[');
            names.Add(m[u..(lb < 0 ? m.Length : lb)]);
        }
        return names;
    }

    /// <summary>std140 byte offset of <paramref name="name"/> in a <see cref="Members"/> body, and its size;
    /// -1 when absent, -2 when a member's type is one this calculator does not know.</summary>
    internal static int Offset(string members, string name, out int size)
    {
        int off = 0;
        foreach (string m in members[(members.IndexOf('{') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int n = m.IndexOf(name, StringComparison.Ordinal);
            int u = 0;
            while (u < m.Length - 1 && !(m[u] == 'u' && char.IsUpper(m[u + 1]))) u++;   // the name: its first u<Upper>
            string type = m[..u];
            (int align, int bytes) = type switch
            {
                "float" or "int" or "uint" => (4, 4),
                "vec2" or "ivec2" or "uvec2" => (8, 8),
                "vec3" or "ivec3" or "uvec3" => (16, 12),
                "vec4" or "ivec4" or "uvec4" => (16, 16),
                "mat4" => (16, 64),
                _ => (-1, 0),
            };
            if (align < 0) { size = 0; return -2; }   // a type this calculator does not know fails the pin, never passes it
            int lb = m.IndexOf('[');
            if (lb >= 0) { int count = int.Parse(m[(lb + 1)..m.IndexOf(']')]); align = 16; bytes = ((bytes + 15) & ~15) * count; }
            off = (off + align - 1) & ~(align - 1);
            if (n >= 0 && (m.Length == n + name.Length || m[n + name.Length] == '[')) { size = bytes; return off; }
            off += bytes;
        }
        size = 0;
        return -1;
    }

    /// <summary>The byte offset of one staged member inside its block, from the compiler rather than from a
    /// hand-written constant -- the C# half of <see cref="Pin"/>'s oracle.</summary>
    internal static int At<TBlock, TMember>(ref TBlock block, ref TMember member)
        => (int)Unsafe.ByteOffset(ref Unsafe.As<TBlock, byte>(ref block), ref Unsafe.As<TMember, byte>(ref member));

    /// <summary>Hold one block's GLSL to the struct that stages it: the binding point, every member's offset
    /// against the offset the C# compiler gave its field, and the staged size. Oracle: the GLSL text on one
    /// side, the compiler's own field offsets on the other, so a member moved in either file is named here.</summary>
    internal static void Pin(Checker check, string src, string block, uint binding, int bytes, (string Name, int Offset)[] members)
    {
        string? body = Members(src, block);
        check.Expect(body != null, $"{block}: the source declares no such block");
        if (body == null) return;
        check.Expect(body.StartsWith($"layout(std140,binding={binding})uniform{block}{{", StringComparison.Ordinal),
          $"{block}: not declared std140 on point {binding}");
        int end = 0;
        foreach ((string name, int off) in members)
        {
            int at = Offset(body, name, out int size);
            check.Expect(at == off, $"{block}.{name} at std140 {at}, C# stages {off}");
            end = Math.Max(end, at + size);
        }
        check.Expect(end == bytes, $"{block}'s members reach {end}B, the struct staging it is {bytes}B");
        check.Expect(body.Split(';').Length - 1 == members.Length, $"{block} has a member C# does not stage");
    }
}
