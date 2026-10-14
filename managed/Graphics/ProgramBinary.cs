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

    private static string Dir => _dir ??= Sweep(Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), "cx-shaders"));

    /// <summary>The driver identity every key starts from; 0 (cache off) until the 4.6 context is live, or when
    /// the driver has no program-binary entry points.</summary>
    internal static ulong Seed
    {
        get
        {
            if (_seed != 0 || GlContextService.Driver.Length == 0 || Gl.GetProgramBinary == null || Gl.ProgramBinaryLoad == null)
                return _seed;
            return _seed = MdlParser.ContentHash(MemoryMarshal.AsBytes(GlContextService.Driver.AsSpan())) | 1;
        }
    }

    /// <summary>Fold one stage -- its type and the exact text glShaderSource receives -- into a key. 0 stays 0.</summary>
    internal static ulong Key(ulong h, uint type, string text) =>
        h == 0 ? 0 : ((h ^ type) * Golden) ^ MdlParser.ContentHash(MemoryMarshal.AsBytes(text.AsSpan()));

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
        fixed (byte* p = blob) Gl.ProgramBinaryLoad(prog, BitConverter.ToUInt32(blob, 0), p + 4, blob.Length - 4);
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
        fixed (byte* p = blob) Gl.GetProgramBinary(prog, len, &got, &fmt, p + 4);
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
