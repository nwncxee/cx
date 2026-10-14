using System;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy;

/// <summary>The session's <see cref="IModelSource"/> -- the engine half of the model cache's read, so that
/// <c>Alloy/Mdl/</c> stays buildable with no client. <see cref="ResLocate"/> turns the engine's own
/// <c>GetKeyEntry</c> lookup into a value record on the MAIN thread (its precedence IS the engine's) and a worker
/// reads the bytes. A RESIDENT entry is the one refusal: its pointer is not one a worker may keep.</summary>
internal static unsafe class ModelFeed
{
    /// <summary>The model restype, as <c>CResHelper&lt;CResMDL, 2002&gt;</c> declares it.</summary>
    private const ushort MdlResType = 2002;
    /// <summary>A tile's walkmesh is a SEPARATE resource under the same resref: restype 2016 (<c>wok</c>,
    /// <c>0x7e0</c> in the engine's own extension table @0x4157b0). It is asked for as
    /// <c>&lt;resref&gt;.wok</c> so the model cache keys, refcounts and evicts it exactly like a model --
    /// one entry per distinct walkmesh, shared by every placement of that tile.</summary>
    internal const string WokSuffix = ".wok";
    private const ushort WokResType = 2016;

    /// <summary>Split a cache key into the resref the containers know and the restype to ask for.</summary>
    private static ushort TypeOf(string resref, out string name)
    {
        if (resref.EndsWith(WokSuffix, StringComparison.OrdinalIgnoreCase))
        { name = resref[..^WokSuffix.Length]; return WokResType; }
        name = resref;
        return MdlResType;
    }
    /// <summary>A resref is at most 16 characters.</summary>
    private const int ResRefChars = 16;

    private static ExtenderLog? _log;
    private static Source? _source;
    private static long _resident, _nwsyncOff, _refused, _bytes, _ascii, _moved;

    /// <summary>Install the feed if the engine lookup can answer at all. A client where it cannot keeps the
    /// cache with NO source, which refuses every parse loudly rather than inventing one.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        // The cache takes the log FIRST and unconditionally: a session where the feed cannot attach is exactly
        // the session whose refusals have to be readable, and the cache's own log would be null below.
        ModelCache.Attach(log);
        if (!ResLocate.Attach(out string? why))
        {
            log.Always($"ModelFeed: stood down -- {why}; the model cache has no source this session, and every "
                     + "acquire is a counted refusal (nosource on the [models] line)");
            return;
        }
        _source ??= new Source();
        ModelCache.SetSource(_source);
    }

    /// <summary>What <see cref="Source.Locate"/> hands the worker: a value record by construction, exactly as
    /// <see cref="TexLocation"/> is.</summary>
    private sealed class Token
    {
        /// <summary>The restype this token was located under: 2002 for a model, 2016 for a tile walkmesh.</summary>
        public ushort Type;
        public string Name = "";
        public TexLocation Loc;
        /// <summary>The <c>RESID</c> the location was taken from. <see cref="Source.StillValid"/> re-proves it
        /// on the main thread before the parsed model is filed, so a resref aliased or a key table rebuilt
        /// mid-read costs a counted miss and not stale bytes under the right name.</summary>
        public uint R0, R1, R2, R3;
        /// <summary>The resref as a NUL-terminated byte buffer, for the re-prove.</summary>
        public byte[] Name8 = Array.Empty<byte>();
    }

    private sealed class Source : IModelSource
    {
        /// <summary>Main thread: the engine's own lookup, snapshotted.</summary>
        public object? Locate(string resref, out string? why)
        {
            ushort type = TypeOf(resref, out resref);
            why = null;
            byte* name = stackalloc byte[ResRefChars + 1];
            int n = 0;
            for (; n < ResRefChars && n < resref.Length; n++) name[n] = (byte)resref[n];
            name[n] = 0;
            if (!ResLocate.Find(name, type, out IntPtr table, out IntPtr entry))
            { why = "no key-table entry"; return null; }
            uint* resId = stackalloc uint[ResLocate.ResIdWords];
            if (!ResLocate.Locate(table, entry, resId, out TexLocation loc, out string? w, out ResLocate.Source src))
            {
                if (src == ResLocate.Source.Resident)
                {
                    _resident++;
                    why = "the engine holds this resource resident; a worker cannot read it";
                }
                else if (src == ResLocate.Source.NwSync) { _nwsyncOff++; why = w ?? "NWSync reader unavailable"; }
                else { _refused++; why = w ?? "cannot locate"; }
                return null;
            }
            var t = new Token { Name = resref, Type = type, Loc = loc, R0 = resId[0], R1 = resId[1], R2 = resId[2], R3 = resId[3] };
            t.Name8 = new byte[n + 1];
            for (int i = 0; i < n; i++) t.Name8[i] = name[i];
            return t;
        }

        /// <summary>Game thread, at the publish: ask the engine's lookup again and compare the <c>RESID</c>.
        /// <see cref="ResLocate.StillAt"/> is that comparison, and it is what turns a resref aliased under us
        /// (or a key table rebuilt) into a miss rather than the wrong model under the right name.</summary>
        public bool StillValid(object token, out string? why)
        {
            var t = (Token)token;
            fixed (byte* nm = t.Name8)
                if (ResLocate.StillAt(nm, t.Type, t.R0, t.R1, t.R2, t.R3)) { why = null; return true; }
            _moved++;
            why = "the resource lookup no longer lands on the RESID it was read from";
            return false;
        }

        /// <summary>Worker: read, parse. Touches no engine memory
        /// -- the location was copied for it -- and no GL.</summary>
        public ModelDef? Parse(object token, out string? why)
        {
            why = null;
            var t = (Token)token;
            ReadOnlySpan<byte> raw;
            if (t.Loc.Kind == KindNwSync)
            {
                Span<byte> nm = stackalloc byte[t.Loc.Path.Length];
                System.Text.Encoding.ASCII.GetBytes(t.Loc.Path, nm);
                if (!NwSyncReader.TryRead(nm, t.Type, out byte* data, out int size))
                { why = "NWSync: no row"; return null; }
                try { return Build(t, new ReadOnlySpan<byte>(data, size), out why); }
                finally { NativeMemory.Free(data); }
            }
            if (!(_handles ??= new Handles()).Read(t.Loc, out raw, out why)) return null;
            return Build(t, raw, out why);
        }

        private ModelDef? Build(Token t, ReadOnlySpan<byte> raw, out string? why)
        {
            why = null;
            if (raw.Length == 0) { why = "no bytes"; return null; }
            System.Threading.Interlocked.Add(ref _bytes, raw.Length);
            if (Ascii(raw)) System.Threading.Interlocked.Increment(ref _ascii);
            // The name is NOT set here: the cache files the model under the resref it was asked for and
            // counts a file whose internal name disagrees (its Publish), so overwriting it here would hide
            // exactly the case the engine logs as "Model Names Differ".
            return MdlParser.Parse(raw, t.Name, out why);
        }

        private static bool Ascii(ReadOnlySpan<byte> d)
            => d.Length < 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d) != 0;
    }

    /// <summary>One set of open containers and one read buffer per reader thread: <see cref="Handles"/> is
    /// worker-thread-only state. The cache runs ONE parse thread, so this is its handle set.</summary>
    [ThreadStatic] private static Handles? _handles;

    /// <summary>Why a model could not be handed to the worker, for the period line. Each class is also logged
    /// once, by the cache, with the model's name.</summary>
    internal static string Note()
    {
        string s = $"feed {_bytes >> 20} MiB read";
        if (_ascii != 0) s += $", {_ascii} ASCII sources parsed directly";
        if (_resident != 0) s += $", {_resident} resident (engine-held)";
        if (_nwsyncOff != 0) s += $", {_nwsyncOff} nwsync-off";
        if (_refused != 0) s += $", {_refused} unlocatable";
        if (_moved != 0) s += $", {_moved} moved under the read";
        return s;
    }

    // ---- CX-112: the same two halves, reachable by name ----

    /// <summary>Game thread: where the model named <paramref name="resref"/> lives, or null with
    /// <paramref name="why"/> set. The live parse check (<c>MdlParseCheck</c>) needs the seam without going
    /// through the cache, so it does not disturb the cache's own accounting.</summary>
    internal static object? Locate(string resref, out string? why)
    {
        Source? src = Current(out why);
        return src == null ? null : ((IModelSource)src).Locate(resref, out why);
    }

    /// <summary>Game thread: does the engine's own lookup hold an entry for this model? The question the seam asks
    /// before it refuses, because "no such model" is an answer every caller null-checks while "CX could not read
    /// it" is CX's own failure and must not look like the first. The engine's own test, asked with
    /// <c>logMissing: false</c>; a lookup that cannot answer at all returns true, never a claimed absence.</summary>
    internal static bool Present(string resref)
    {
        if (!ResLocate.Ready && !ResLocate.Attach(out _)) return true;
        ushort type = TypeOf(resref, out string name);
        byte* nm = stackalloc byte[ResRefChars + 1];
        int n = 0;
        for (; n < ResRefChars && n < name.Length; n++) nm[n] = (byte)name[n];
        nm[n] = 0;
        return ResLocate.Find(nm, type, out _, out _);
    }

    /// <summary>The one source, created on the GAME thread behind the same gate <see cref="Attach"/> uses --
    /// a lazy <c>??=</c> here raced the worker's own use of it, and skipped the gate that decides whether the
    /// engine's lookup can answer at all.</summary>
    private static Source? Current(out string? why)
    {
        why = null;
        if (_source != null) return _source;
        if (!ResLocate.Attach(out why)) return null;
        lock (Gate) return _source ??= new Source();
    }

    private static readonly object Gate = new();

    /// <summary>Worker thread: read and parse what <see cref="Locate"/> found.</summary>
    internal static ModelDef? ParseToken(object token, out string? why)
    {
        Source? src = _source;
        if (src == null) { why = "the model source is not attached"; return null; }
        return ((IModelSource)src).Parse(token, out why);
    }
}
