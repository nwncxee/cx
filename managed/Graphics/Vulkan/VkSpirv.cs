using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CX.Graphics.Rhi;

/// <summary>The layout a compiled SPIR-V module gives one block: each member's byte offset and size, read from the
/// module's own offset decorations by the variable's set and binding (or its push-constant storage), so C# fills
/// the block where the compiler put each member rather than where a hand-kept table says it is. Debug names are not
/// needed: members keep their declaration order, which the caller names. Cold: once per program.</summary>
internal static class VkSpirv
{
    /// <summary>The SPIR-V opcodes, decorations and storage classes read here, from the specification's tables.</summary>
    private const int OpTypeBool = 20, OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23, OpTypeMatrix = 24,
                      OpTypeArray = 28, OpTypeStruct = 30, OpTypePointer = 32, OpConstant = 43, OpVariable = 59,
                      OpDecorate = 71, OpMemberDecorate = 72;
    private const int DecorationArrayStride = 6, DecorationMatrixStride = 7, DecorationBinding = 33,
                      DecorationDescriptorSet = 34, DecorationOffset = 35;
    private const int StorageUniform = 2, StoragePushConstant = 9;
    /// <summary>The words ahead of the first instruction: magic, version, generator, id bound, schema.</summary>
    private const int HeaderWords = 5;

    /// <summary>The uniform block on (<paramref name="set"/>, <paramref name="binding"/>): its members (offset, byte
    /// size) under <paramref name="names"/> in declaration order, and its size rounded to 16; null when the module
    /// binds no block there or it has a different member count.</summary>
    internal static (Dictionary<string, (int Offset, int Size)> Members, int Bytes)? Block(byte[] spirv, uint set, uint binding,
                                                                                         IReadOnlyList<string> names)
        => Find(spirv, StorageUniform, set, binding, names);

    /// <summary>The push-constant block, as <see cref="Block"/> reads a uniform one.</summary>
    internal static (Dictionary<string, (int Offset, int Size)> Members, int Bytes)? Push(byte[] spirv, IReadOnlyList<string> names)
        => Find(spirv, StoragePushConstant, 0, 0, names);

    private static (Dictionary<string, (int Offset, int Size)> Members, int Bytes)? Find(byte[] spirv, int storage, uint set,
                                                                                       uint binding, IReadOnlyList<string> names)
    {
        ReadOnlySpan<uint> w = MemoryMarshal.Cast<byte, uint>(spirv);
        var offsets = new Dictionary<(uint, uint), int>();
        var arrayStride = new Dictionary<uint, int>();
        var matStride = new Dictionary<(uint, uint), int>();
        var bindings = new Dictionary<uint, uint>();
        var sets = new Dictionary<uint, uint>();
        var scalar = new Dictionary<uint, int>();
        var vector = new Dictionary<uint, (uint Elem, int N)>();
        var matrix = new Dictionary<uint, (uint Col, int N)>();
        var array = new Dictionary<uint, (uint Elem, uint Len)>();
        var structs = new Dictionary<uint, uint[]>();
        var pointers = new Dictionary<uint, uint>();
        var consts = new Dictionary<uint, uint>();
        var vars = new List<(uint Ptr, uint Id, int Storage)>();
        for (int i = HeaderWords; i < w.Length;)
        {
            int op = (int)(w[i] & 0xFFFF), n = (int)(w[i] >> 16);
            if (n == 0 || i + n > w.Length) break;
            ReadOnlySpan<uint> a = w.Slice(i + 1, n - 1);
            switch (op)
            {
                case OpTypeBool: scalar[a[0]] = 4; break;
                case OpTypeInt or OpTypeFloat: scalar[a[0]] = (int)a[1] / 8; break;
                case OpTypeVector: vector[a[0]] = (a[1], (int)a[2]); break;
                case OpTypeMatrix: matrix[a[0]] = (a[1], (int)a[2]); break;
                case OpTypeArray: array[a[0]] = (a[1], a[2]); break;
                case OpTypeStruct: structs[a[0]] = a[1..].ToArray(); break;
                case OpTypePointer: pointers[a[0]] = a[2]; break;
                case OpConstant when a.Length >= 3: consts[a[1]] = a[2]; break;
                case OpVariable: vars.Add((a[0], a[1], (int)a[2])); break;
                case OpDecorate when a.Length >= 3 && a[1] == DecorationArrayStride: arrayStride[a[0]] = (int)a[2]; break;
                case OpDecorate when a.Length >= 3 && a[1] == DecorationBinding: bindings[a[0]] = a[2]; break;
                case OpDecorate when a.Length >= 3 && a[1] == DecorationDescriptorSet: sets[a[0]] = a[2]; break;
                case OpMemberDecorate when a.Length >= 4 && a[2] == DecorationOffset: offsets[(a[0], a[1])] = (int)a[3]; break;
                case OpMemberDecorate when a.Length >= 4 && a[2] == DecorationMatrixStride: matStride[(a[0], a[1])] = (int)a[3]; break;
            }
            i += n;
        }
        foreach (var (ptr, id, sc) in vars)
        {
            if (sc != storage || !pointers.TryGetValue(ptr, out uint type) || !structs.TryGetValue(type, out uint[]? members)) continue;
            if (storage == StorageUniform && (!bindings.TryGetValue(id, out uint b) || b != binding
                                              || (sets.TryGetValue(id, out uint s) ? s : 0) != set)) continue;
            if (members.Length != names.Count) return null;
            var outMembers = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            int end = 0;
            for (uint m = 0; m < members.Length; m++)
            {
                if (!offsets.TryGetValue((type, m), out int off)) return null;
                int size = Size(members[m], matStride.TryGetValue((type, m), out int ms) ? ms : 0);
                outMembers[names[(int)m]] = (off, size);
                end = Math.Max(end, off + size);
            }
            return (outMembers, (end + 15) & ~15);
        }
        return null;

        int Size(uint t, int mstride)
        {
            if (scalar.TryGetValue(t, out int s)) return s;
            if (vector.TryGetValue(t, out var v)) return v.N * Size(v.Elem, 0);
            if (matrix.TryGetValue(t, out var mx)) return mx.N * (mstride > 0 ? mstride : Size(mx.Col, 0));
            if (array.TryGetValue(t, out var ar))
                return (consts.TryGetValue(ar.Len, out uint len) ? (int)len : 0)
                     * (arrayStride.TryGetValue(t, out int st) ? st : Size(ar.Elem, mstride));
            return 0;
        }
    }
}
