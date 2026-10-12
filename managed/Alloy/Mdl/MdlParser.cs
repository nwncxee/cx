using System;
using System.Buffers.Binary;

namespace CX.Alloy.Mdl;

/// <summary>Parses one model file -- binary or ASCII, told apart by the first dword as the engine's
/// <c>ReadSync</c> does -- and runs the post-process, returning an immutable <see cref="ModelDef"/>. Pure C#,
/// thread-agnostic and engine-free, which is what lets <c>tools/mdlcheck</c> run the whole parse off-device;
/// the model cache calls it on its worker.</summary>
public static class MdlParser
{
    /// <summary>Scores a material's render hint by name (<c>materialname</c>, else <c>texture0</c>) as the
    /// engine's <c>SharedMaterial::GetSharedMaterial</c> does, so a mesh with no <c>renderhint</c> of its own
    /// still gets a tangent frame. A resource lookup, so the host wires it once at startup and the parse worker
    /// reads it; null means none. Engine score: <c>none</c> 1, <c>normalandspecmapped</c> 2, <c>normaltangents</c> 3, else 0.</summary>
    public static Func<string, int>? MaterialHint;

    /// <summary>Meshes that asked for a material's render hint and got no answer: no host wired
    /// <see cref="MaterialHint"/>, or the <c>.mtr</c> was not cached yet. Such a mesh leaves the parse with no
    /// tangent frame and no handedness split; the material feed revisits the second case once its read lands,
    /// and both are counted -- a refusal of our own mechanism is never a quiet hole.</summary>
    public static int MaterialHintMisses;

    /// <summary>Parse <paramref name="data"/> as the model <paramref name="name"/>; null with
    /// <paramref name="error"/> set when the file is not a model this parser accepts.</summary>
    public static ModelDef? Parse(ReadOnlySpan<byte> data, string name, out string? error)
    {
        error = null;
        if (data.Length < 12) { error = "shorter than a model header"; return null; }
        // The engine's discriminator: a binary MDL opens with a zero dword (the wrapper's first word), an
        // ASCII one with its first keyword. ModelLoad.cs documents the same test on ReadSync's side.
        bool binary = BinaryPrimitives.ReadUInt32LittleEndian(data) == 0;
        ModelDef? m;
        try
        {
            m = binary ? MdlBinary.Read(data, name, out error) : MdlAscii.Read(data, name, out error);
            if (m == null) return null;
            m.FromAscii = !binary;
            m.ContentHash = ContentHash(data);
            // Inside the try: the post-process is where a hostile file's arithmetic lands, and Parse's
            // contract is a refusal, never a throw out of the worker.
            MdlPost.Run(m);
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
        return m;
    }

    private const ulong Mix1 = 0xFF51AFD7ED558CCDUL;    // not-an-offset: murmur3's fmix64 multiplier
    private const ulong Mix2 = 0xC4CEB9FE1A85EC53UL;    // not-an-offset: murmur3's fmix64 multiplier
    private const ulong Golden = 0x9E3779B97F4A7C15UL;  // not-an-offset: the odd golden-ratio multiplier
    private const ulong Seed = 0x27D4EB2F165667C5UL;    // not-an-offset: an odd 64-bit seed

    private static ulong Fmix(ulong z)
    {
        z ^= z >> 33; z *= Mix1; z ^= z >> 33; z *= Mix2; z ^= z >> 33;
        return z;
    }

    /// <summary>The same content hash <c>MdlCache</c> keys its disk entries on, so a parsed model and a cached
    /// compile of the same source carry one identity.</summary>
    public static ulong ContentHash(ReadOnlySpan<byte> src)
    {
        ulong h = Seed ^ (ulong)(uint)src.Length;
        int i = 0;
        for (; i + 8 <= src.Length; i += 8)
            h = System.Numerics.BitOperations.RotateLeft(h ^ Fmix(BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(i, 8))), 27) * Golden + Seed;
        ulong tail = 0;
        for (; i < src.Length; i++) tail = (tail << 8) | src[i];
        return Fmix(h ^ tail);
    }
}
