using System;
using System.IO;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Alloy.Mdl;   // MdlParser.ContentHash: the one content hash

namespace CX.Graphics;

/// <summary>Linked programs kept as driver binaries in <c>cx-shaders/</c> beside cx.cfg, keyed by every
/// stage's exact text and <see cref="GlContextService.Driver"/>, so a launch after the first skips the link;
/// a blob the driver refuses is deleted and the program compiles as before. An NVIDIA blob is not final code
/// -- the first draw finishes it unless the driver's own disk cache, which cx-launch keeps private, holds it.</summary>
internal static unsafe class ProgramBinary
{
    /// <summary>Entries unused this long are swept at the session's first lookup: every CX release or shader
    /// edit orphans the previous set.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(30);
    private const ulong Golden = 0x9E3779B97F4A7C15UL;   // not-an-offset: golden-ratio hash multiplier

    private static string? _dir;
    private static ulong _seed;

    /// <summary>The shader cache folder beside cx.cfg, swept of stale entries at first use; Vulkan's SPIR-V and pipeline
    /// cache live here too.</summary>
    internal static string CacheDir => Dir;

    private static string Dir => _dir ??= Sweep(Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), "cx-shaders"));

    /// <summary>The driver identity every key starts from; 0 (cache off) until the 4.6 context is live, or when
    /// the driver has no program-binary entry points.</summary>
    internal static ulong Seed
    {
        get
        {
            if (_seed != 0 || GlContextService.Driver.Length == 0 || Gl.GetProgramBinary == null || Gl.ProgramBinaryLoad == null)
                return _seed;
            return _seed = SeedOf(GlContextService.Driver);
        }
    }

    /// <summary>The seed a driver identity (<c>GL_VENDOR</c>, <c>GL_RENDERER</c> and <c>GL_VERSION</c> as
    /// <see cref="GlContextService.Driver"/> joins them) folds to: never 0, which means the cache is off.</summary>
    internal static ulong SeedOf(string driver) => MdlParser.ContentHash(MemoryMarshal.AsBytes(driver.AsSpan())) | 1;

    /// <summary>Fold one stage -- its type and the exact text glShaderSource receives -- into a key. 0 stays 0.</summary>
    internal static ulong Key(ulong h, uint type, string text) =>
        h == 0 ? 0 : ((h ^ type) * Golden) ^ MdlParser.ContentHash(MemoryMarshal.AsBytes(text.AsSpan()));

    /// <summary>Is there a blob for <paramref name="key"/> to try? Not whether the driver will take it.</summary>
    internal static bool Has(ulong key) => key != 0 && File.Exists(PathOf(key));

    /// <summary>The cached program for <paramref name="key"/>, or 0 to compile.</summary>
    internal static uint Load(ulong key, ExtenderLog? log, string what)
    {
        if (key == 0) return 0;
        string path = PathOf(key);
        byte[] blob;
        try
        {
            if (!File.Exists(path)) return 0;
            blob = File.ReadAllBytes(path);
        }
        catch (Exception e) { log?.Info($"{what}: cached program binary unreadable ({e.Message}) -- compiling"); return 0; }
        if (blob.Length <= 4) { Drop(path); return 0; }
        uint prog = Gl.CreateProgram();
        if (GlContextService.Debug) log?.Always($"ProgramBinary: [gldebug] loading {what} from cx-shaders/");
        fixed (byte* p = blob.AsSpan(4)) Gl.ProgramBinaryLoad(prog, BitConverter.ToUInt32(blob, 0), p, blob.Length - 4);
        int ok; Gl.GetProgramiv(prog, Gl.LINK_STATUS, &ok);
        if (ok == 0)
        {
            Gl.DeleteProgram(prog);
            Gl.DrainErrors();   // an unknown format raises INVALID_ENUM; the refusal is this line, not a GL error
            Drop(path);
            log?.Info($"{what}: cached program binary refused by the driver -- compiling");
            return 0;
        }
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        log?.Info($"{what}: linked from cx-shaders/ (program binary cache)");
        return prog;
    }

    /// <summary>Write a freshly linked program under <paramref name="key"/>; false (logged unless the key is 0)
    /// costs the next launch a compile.</summary>
    internal static bool Save(ulong key, uint prog, ExtenderLog? log, string what)
    {
        if (key == 0 || prog == 0) return false;
        int len; Gl.GetProgramiv(prog, Gl.PROGRAM_BINARY_LENGTH, &len);
        if (len <= 0) { log?.Info($"{what}: driver returned no program binary -- the next launch compiles it again"); return false; }
        byte[] blob = new byte[len + 4];
        int got; uint fmt;
        fixed (byte* p = blob.AsSpan(4)) Gl.GetProgramBinary(prog, len, &got, &fmt, p);
        if (got <= 0) { log?.Info($"{what}: driver returned no program binary -- the next launch compiles it again"); return false; }
        BitConverter.TryWriteBytes(blob.AsSpan(0, 4), fmt);
        try
        {
            Directory.CreateDirectory(Dir);
            string path = PathOf(key), tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob.AsSpan(0, got + 4));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) { log?.Info($"{what}: program binary not saved ({e.Message}) -- the next launch compiles it again"); return false; }
    }

    private static string PathOf(ulong key) => Path.Combine(Dir, $"{key:x16}.bin");

    private static void Drop(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Off-device. Oracle: the keys below, worked out by an independent reimplementation of the
    /// documented fold (murmur-mixed content hash over the strings' UTF-16 bytes, driver seed odd, stage type
    /// folded in) and written here as literals -- never a second call to <see cref="Key"/>.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        const string nv = "NVIDIA|RTX 3070|4.6.0";
        const string mesa = "Mesa|AMD Radeon RX 9070 (radeonsi)|4.6 (Core Profile) Mesa 25.2.0";
        const string vs = "void main(){}\n";
        check.Expect(SeedOf(nv) == 0x364e03d3530f899fUL,   // not-an-offset: the hand-worked seed of nv
                     "the driver identity does not fold to the seed the cache's own key was worked out from, so every "
                     + "blob this release writes is keyed differently from every blob it reads");
        check.Expect(Key(SeedOf(nv), Gl.VERTEX_SHADER, vs) == 0x8e4da4bd4da507b4UL   // not-an-offset: hand-worked key
                     && Key(SeedOf(nv), Gl.FRAGMENT_SHADER, vs) == 0x280622f4cd6b7ba9UL   // not-an-offset: ...and of the fragment stage
                     && Key(Key(SeedOf(nv), Gl.VERTEX_SHADER, vs), Gl.FRAGMENT_SHADER, vs) == 0x1f975d50c8baf126UL,   // not-an-offset: ...and of the pair
                     "a stage key is not the hand-worked fold of the driver seed, the stage type and the text -- a "
                     + "launch after this one looks its blobs up under other names");
        check.Expect(Key(0, Gl.VERTEX_SHADER, vs) == 0,
                     "a stage folded onto the cache-off seed yields a key, so a launch with no driver identity would "
                     + "read and write blobs under one");
        check.Expect(SeedOf(mesa) != 0 && SeedOf(mesa) != SeedOf(nv),
                     "two different driver identities fold to the same seed, or to 0 -- which is the cache-off value, "
                     + "so one driver's blobs would be handed to another");
        check.Expect(Key(SeedOf(nv), Gl.VERTEX_SHADER, vs) != Key(SeedOf(nv), Gl.VERTEX_SHADER, vs + " "),
                     "a one-character edit to a stage's text does not move its key");
        log.Info(check.Ok ? "ProgramBinary.SelfCheck: passed" : "ProgramBinary.SelfCheck: FAILED");
        return check.Ok;
    }

    private static string Sweep(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                foreach (string f in Directory.EnumerateFiles(dir))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > Stale) File.Delete(f);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return dir;
    }
}
