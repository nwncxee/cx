using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The per-frame streaming arena -- one <see cref="GpuRing"/> mapping in three fenced regions,
/// each sized for one push of every <see cref="Site"/>, so a slice is good only inside the frame that wrote it.
/// There is no fallback: a push that fails is our bug, so it is logged, painted and its pass sits out.</summary>
internal static unsafe class StreamRing
{
    /// <summary>The uploads that stream, one push each per frame. The value is the band <see cref="Paint"/> draws.</summary>
    internal enum Site { DrawSlots, CullSource, PrevBones }

    /// <summary>Why a push failed: the region's fence outwaited <see cref="GpuRing.TimeoutNs"/>, the frame pushed past
    /// the bounds the region was sized from, or a caller rebound a slice the ring had already left.</summary>
    internal enum Why { Timeout, Overflow, Stale }

    /// <summary>A ring slice as bound, for the same frame's rebinds.</summary>
    internal struct Range
    {
        internal long Off, Len;
        internal int Gen;
        internal readonly bool Valid => Len > 0;
        internal readonly bool Stale => Gen != _gen;
        internal readonly void Bind(uint binding)
            => Gl.BindBufferRange(Gl.SHADER_STORAGE_BUFFER, binding, _buf, (IntPtr)Off, (IntPtr)Len);
    }

    private const int Regions = 3;
    /// <summary>GL 4.6's ceiling on SHADER_STORAGE_BUFFER_OFFSET_ALIGNMENT, so any driver's value divides it.</summary>
    private const long Align = 256;
    /// <summary>Each site's worst case, one push per frame: every owned draw, and every bone slot.</summary>
    private const long SlotsMax = (long)AlloyRenderer.MaxOwned * sizeof(int),
                       CullMax = (long)AlloyRenderer.MaxOwned * CullPass.SrcUints * sizeof(uint),
                       BonesMax = (long)AlloyRenderer.MaxBoneSlots * AlloyRenderer.BoneFloats * sizeof(float);
    private const long Region = SlotsMax + CullMax + BonesMax + 3 * Align;

    private static ExtenderLog? _log;
    private static uint _buf;
    private static byte* _map;
    private static IntPtr[]? _fence;   // not static readonly: an initialiser would give the class a NativeAOT cctor
    private static long _used;
    private static int _at, _frame = -1, _gen, _logged, _paint;
    private static bool _open;
    private static long _failSlots, _failCull, _failBones;

    /// <summary>The ring could not be created: the renderer has failed (<see cref="AlloyGate.Fail"/>).</summary>
    internal static bool Refused { get; private set; }

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The entry points the ring needs; asked at frame start beside the multidraw check.</summary>
    internal static bool EntryPoints => Gl.NamedBufferStorage != null && Gl.MapNamedBufferRange != null
                                        && Gl.BindBufferRange != null && Gl.FenceSync != null && Gl.ClientWaitSync != null;

    /// <summary>Copy <paramref name="bytes"/> from <paramref name="src"/> into this frame's region and bind it on SSBO
    /// <paramref name="binding"/>. False = nothing was written or bound; the caller's pass sits the frame out.</summary>
    internal static bool Stream(Site site, uint binding, void* src, long bytes, out Range r)
    {
        r = default;
        long len = Math.Max(bytes, 4);
        if (_frame != AlloyRenderer.FrameSeq) Rotate();
        if (Refused) return false;
        if (!_open) return Failed(site, Why.Timeout, len);
        long at = Bump(ref _used, len);
        if (_used > Region) return Failed(site, Why.Overflow, len);
        long off = (long)_at * Region + at;
        if (bytes > 0) new ReadOnlySpan<byte>(src, checked((int)bytes)).CopyTo(new Span<byte>(_map, checked((int)(off + bytes))).Slice(checked((int)off)));
        r = new Range { Off = off, Len = len, Gen = _gen };
        r.Bind(binding);
        return true;
    }

    /// <summary>Rebind <paramref name="r"/>, or fail it loudly if the ring has left its region.</summary>
    internal static bool Rebind(Site site, uint binding, in Range r)
    {
        if (!r.Valid) return false;
        if (r.Stale) return Failed(site, Why.Stale, r.Len);
        r.Bind(binding);
        return true;
    }

    /// <summary>Place <paramref name="len"/> bytes after <paramref name="used"/> at the SSBO alignment; returns
    /// the offset and advances <paramref name="used"/> past it.</summary>
    private static long Bump(ref long used, long len)
    {
        long at = (used + Align - 1) & ~(Align - 1);
        used = at + len;
        return at;
    }

    /// <summary>Count, log the first failure of each (site, reason) in full, and flag the site's band.</summary>
    private static bool Failed(Site site, Why why, long len)
    {
        long n = site switch { Site.DrawSlots => ++_failSlots, Site.CullSource => ++_failCull, _ => ++_failBones };
        _paint |= 1 << (int)site;
        int bit = 1 << ((int)site * 3 + (int)why);
        if ((_logged & bit) == 0)
        {
            _logged |= bit;
            _log?.Always($"[stream] UPLOAD FAILED -- {site}: {why}, {len} bytes at {_used - len}/{Region} of region {_at}, " +
                         $"frame {AlloyRenderer.FrameSeq}; its pass sits out and paints a red band (failure #{n} for this site; " +
                         "later ones of this kind are counted on the [alloy] line)");
        }
        return false;
    }

    /// <summary>Fence the region just left behind every command issued out of it, and take the next.</summary>
    private static void Rotate()
    {
        if (_open) GpuRing.Signal(_fence!, _at);
        _frame = AlloyRenderer.FrameSeq;
        _gen++;
        _used = 0;
        _open = false;
        if (Refused || (_map == null && !Create())) return;
        _at = (_at + 1) % Regions;
        long t0 = FrameZones.Now;
        _open = GpuRing.Acquire(_fence!, _at);
        FrameZones.Add(FrameZones.Zone.Wait, t0);
    }

    private static bool Create()
    {
        _fence = new IntPtr[Regions];
        _map = GpuRing.Map(Gl.SHADER_STORAGE_BUFFER, Region * Regions, out _buf);
        if (_map != null) return true;
        uint d = _buf; Gl.DeleteBuffers(1, &d); _buf = 0;
        Refused = true;
        AlloyGate.Fail($"the {Region * Regions >> 20} MB persistent-mapped stream ring was refused by the driver");
        return false;
    }

    /// <summary>The bands this frame's failures asked for, cleared as they are taken: bit <c>1 &lt;&lt; Site</c>.</summary>
    internal static int TakePaint() { int p = _paint; _paint = 0; return p; }

    /// <summary>The names died with the context: drop them, delete nothing, and let the next push remap.</summary>
    internal static void ContextLost()
    {
        _buf = 0; _map = null; _fence = null; _used = 0;
        _open = false; _frame = -1; _gen++;
    }

    internal static string Diag()
        => _failSlots + _failCull + _failBones == 0
            ? $"stream ring {Region * Regions >> 20}MB, no failed uploads"
            : $"stream ring {Region * Regions >> 20}MB, FAILED uploads (session): draw slots {_failSlots}, " +
              $"cull source {_failCull}, prev bones {_failBones}";

    /// <summary>A wrong bump hands two uploads one slice, or a driver a misaligned bind, so this joins the disarm
    /// chain: offsets land on the alignment and the region holds one worst-case push of every site.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        long used = 0;
        long a = Bump(ref used, 4), b = Bump(ref used, 300), c = Bump(ref used, 1);
        bool ok = a == 0 && b == Align && c == 3 * Align && used == 3 * Align + 1;
        used = 0;
        Bump(ref used, SlotsMax); Bump(ref used, CullMax); Bump(ref used, BonesMax);
        ok &= used <= Region;
        if (!ok) log.Info("StreamRing.SelfCheck: FAILED -- ring slices overlap, misalign or outgrow the region");
        return ok;
    }
}
