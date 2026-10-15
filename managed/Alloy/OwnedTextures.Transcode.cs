using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The BCn transcode and its census: the funnel's decoded cells are encoded on the cx-bcn thread and
/// uploaded a few per frame, and the per-area tally of the uncompressed share says what the transcode is
/// worth.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>The TRANSCODE, fed straight from the funnel: the upload funnel's scan copies the decoded cell
    /// into a rented buffer sized for its whole mip chain and hands it to the encoder THREAD (<c>cx-bcn</c>),
    /// which builds the mips in place and encodes BC1/BC3. The main thread's only share is the upload,
    /// <c>bcndrip</c> per frame; a job whose resource moved on is dropped and the raw upload stands.</summary>
    private sealed class TransJob
    {
        public ulong Key; public int Etid; public uint Src; public long Bytes; public int Ctx;
        public int Levels; public readonly int[] Lw = new int[MaxLevels], Lh = new int[MaxLevels];
        public readonly long[] Off = new long[MaxLevels];
        /// <summary>Rented: the funnel's RGBA level 0, then the chain the encoder builds in place.</summary>
        public byte[] Pixels = Array.Empty<byte>();
        /// <summary>Rented by the encoder; the encoded levels, level i at <see cref="Off"/>[i].</summary>
        public byte[] Blocks = Array.Empty<byte>();
        public bool Bc3; public long Compressed;
    }
    /// <summary>RGBA held by jobs between the funnel and the upload; past this the texture stays raw rather than
    /// holding the area's whole decoded set in memory at load.</summary>
    private const long TransPixelCap = 256L << 20;
    private static long _transPixelBytes;
    /// <summary>The caster pass's cutout threshold, the alpha under which a texel is a hole.</summary>
    internal const float CoverageAlpha = 0.2f;
    /// <summary>Jobs in flight at most (funnel to upload); past this the texture stays raw.</summary>
    private const int TransQueue = 1024;
    /// <summary>Funnel -> encoder, and encoder -> upload. Main thread counts <see cref="_transN"/> alone.</summary>
    private static readonly ConcurrentQueue<TransJob> _encodeQ = new(), _encodedQ = new();
    private static int _transN;
    /// <summary>Bumped by <see cref="ContextLost"/>: a job minted before it names a texture of the dead context.</summary>
    private static int _transCtx;
    private static long _transDone, _transDropped, _transStale, _transFailed;
    /// <summary>The session's transcode cost in Stopwatch ticks -- the main-thread upload and the encoder
    /// thread (mips + BCn), which a disk cache of the blocks would save.</summary>
    private static long _transMainTicks, _transEncodeTicks;
    /// <summary><c>bcndrip</c>: finished transcodes uploaded per frame.</summary>
    private static int TranscodeBudget = 1;
    private static Thread? _encoder;
    private static readonly SemaphoreSlim _encodeWake = new(0, int.MaxValue);

    /// <summary>The funnel's RGBA, straight to the encoder. Takes ownership of <paramref name="pixels"/> (rented,
    /// sized by <see cref="TexScan.ChainBytes"/>) whether or not it is queued.</summary>
    private static void EnqueueTranscode(ulong key, int etid, uint src, long bytes, byte[] pixels, int w, int h, bool mips)
    {
        if (w < TranscodeFloor || h < TranscodeFloor) { ArrayPool<byte>.Shared.Return(pixels); return; }
        if (_transN >= TransQueue || _transPixelBytes + pixels.Length > TransPixelCap) { _transDropped++; ArrayPool<byte>.Shared.Return(pixels); return; }   // stays raw, as bcn=0 would leave it
        var job = new TransJob { Key = key, Etid = etid, Src = src, Bytes = bytes, Ctx = _transCtx, Pixels = pixels };
        fixed (int* lw = job.Lw, lh = job.Lh) fixed (long* off = job.Off)
            job.Levels = TexScan.Chain(w, h, mips, MaxLevels, lw, lh, off, out _);
        _transN++;
        _transPixelBytes += pixels.Length;
        _inFlight.Add(key);
        if (_encoder == null)
        {
            _encoder = new Thread(EncodeLoop) { IsBackground = true, Name = "cx-bcn", Priority = ThreadPriority.BelowNormal };
            _encoder.Start();
        }
        _encodeQ.Enqueue(job);
        _encodeWake.Release();
    }

    /// <summary>One frame's uploads of finished jobs. Engine context, on the <see cref="DripRebuilds"/> anchor;
    /// every edit is by name, so the engine's bind cache is never touched, and its unpack-buffer binding is restored.</summary>
    private static void DripTranscodes()
    {
        if (!GlContextService.Available || _transN == 0) return;
        long t0 = FrameZones.Now;
        for (int spent = 0; spent < TranscodeBudget && _encodedQ.TryDequeue(out TransJob? job); spent++)
        {
            long ts = FrameZones.Now;
            UploadJob(job);
            FrameZones.Add(FrameZones.Zone.BcnUp, ts);
        }
        FrameZones.Add(FrameZones.Zone.Bcn, t0);
        _transMainTicks += FrameZones.Now - t0;
    }

    private static void EncodeLoop()
    {
        Affinity.ClearThis();
        while (true)
        {
            _encodeWake.Wait();
            while (_encodeQ.TryDequeue(out TransJob? job))
            {
                long t0 = FrameZones.Now;
                long blk = 0;
                for (int i = 0; i < job.Levels; i++) blk += BcnEncode.LevelSize(job.Lw[i], job.Lh[i], true);
                job.Blocks = ArrayPool<byte>.Shared.Rent((int)blk);
                fixed (byte* px = job.Pixels)
                fixed (byte* blocks = job.Blocks)
                fixed (int* lw = job.Lw, lh = job.Lh) fixed (long* offs = job.Off)
                {
                    TexScan.BuildMips(px, job.Levels, lw, lh, offs);
                    bool bc3 = false;
                    for (long i = 3, end = (long)job.Lw[0] * job.Lh[0] * 4; i < end; i += 4)
                        if (px[i] != 255) { bc3 = true; break; }
                    long off = 0;
                    for (int i = 0; i < job.Levels; i++)
                    {
                        BcnEncode.EncodeLevel(&px[job.Off[i]], job.Lw[i], job.Lh[i], bc3, &blocks[off]);
                        job.Off[i] = off;   // re-used: the level's offset in the BLOCK buffer from here on
                        off += BcnEncode.LevelSize(job.Lw[i], job.Lh[i], bc3);
                    }
                    job.Bc3 = bc3; job.Compressed = off;
                }
                Interlocked.Add(ref _transEncodeTicks, FrameZones.Now - t0);
                _encodedQ.Enqueue(job);
            }
        }
    }

    /// <summary>A new name, the source's sampler state, the compressed levels, and the swap; the job's buffers go
    /// back to the pool whatever happens.</summary>
    private static void UploadJob(TransJob job)
    {
        _transN--;
        _transPixelBytes -= job.Pixels.Length;
        _inFlight.Remove(job.Key);
        try { Upload(job); }
        finally
        {
            ArrayPool<byte>.Shared.Return(job.Pixels);
            ArrayPool<byte>.Shared.Return(job.Blocks);
        }
    }

    private static void Upload(TransJob job)
    {
        if (job.Ctx != _transCtx || !_res.TryGetValue(job.Key, out Entry e) || e.Owned != job.Src) { _transStale++; return; }
        uint dst; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) { _transFailed++; return; }
        // DSA uploads still read through PIXEL_UNPACK_BUFFER, so the engine's (if any) steps aside.
        int svUnpackBuf; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpackBuf);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        Gl.DrainErrors(8);
        int maxLevel = CopySampler(job.Src, dst);
        uint fmt = job.Bc3 ? Gl.COMPRESSED_RGBA_S3TC_DXT5 : Gl.COMPRESSED_RGB_S3TC_DXT1;
        // Immutable storage for the whole chain, then one sub-image per level -- never respecify: defining
        // mutable levels one by one read as BcnUp 2,5-4,7ms per texture, and a mip read after respecification
        // corrupted every level but the base on AMD/Mesa.
        Gl.TextureStorage2D(dst, job.Levels, fmt, job.Lw[0], job.Lh[0]);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_LEVEL, Math.Min(maxLevel, job.Levels - 1));
        fixed (byte* blocks = job.Blocks)
            for (int i = 0; i < job.Levels; i++)
                Gl.CompressedTextureSubImage2D(dst, i, 0, 0, job.Lw[i], job.Lh[i], fmt,
                                               BcnEncode.LevelSize(job.Lw[i], job.Lh[i], job.Bc3), &blocks[job.Off[i]]);
        uint err = Gl.GetError();
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpackBuf);
        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            _transFailed++;
            if (++_bcnFails >= BcnFailCap)
            {
                _bcn = false;
                _log?.Info($"OwnedTextures: BCn transcode DISABLED -- {_bcnFails} driver refusals (last 0x{err:x}); uploads stay raw until the next context");
            }
            return;
        }
        Replace(job.Key, job.Etid, dst, job.Compressed);
        FrameZones.Count(FrameZones.Ev.Transcode);
        _transDone++;
        _censusTrans++; _censusSaved += job.Bytes > job.Compressed ? job.Bytes - job.Compressed : 0;
    }

    internal static string TranscodeNote()
        => $"transcode: {_transDone} done, {_transN} in flight, {_transStale} stale, {_transFailed} failed, {_transDropped} dropped past the queue, {TranscodeBudget} upload(s)/frame, session {_transMainTicks * 1000.0 / Stopwatch.Frequency:F0} ms main + {Interlocked.Read(ref _transEncodeTicks) * 1000.0 / Stopwatch.Frequency:F0} ms encoder";

    /// <summary>Smallest side worth transcoding. A BCn block IS 4x4 with two endpoint colours for its sixteen
    /// texels, so a 4x4 image is ONE block: the client's tiny GUI swatch sheets (<c>gui_rgbc</c>, 4x4) came
    /// out of BC1 a uniform grey. 16 rather than 8 because the saving stops mattering first -- a 16x16 RGBA
    /// level is 1 KiB -- while every texel in such a sheet was chosen on purpose.</summary>
    private const int TranscodeFloor = 16;

    private static bool _bcn, _bcnWanted;
    private static int _bcnFails;
    /// <summary>A driver that refuses one S3TC upload refuses them all; three strikes covers a transient.</summary>
    private const int BcnFailCap = 3;

    /// <summary>Per area, how many textures (and bytes) come down the engine's UNCOMPRESSED upload path --
    /// <c>basic+0x70</c> == 0, the <c>GetCompressedSize</c> @0x484b00 verdict that picks
    /// <c>glTexImage2D</c> over <c>glCompressedTexImage2D</c> -- versus the already-BCn path. The
    /// uncompressed share is the whole BCn-transcode prize.</summary>
    private static int _censusRaw, _censusBcn, _censusTrans;
    private static long _censusRawBytes, _censusBcnBytes, _censusSaved;
    /// <summary>Per area, how many textures are <c>numX*numY &gt; 1</c> grids the engine de-atlased into one
    /// GL texture per cell -- the array-texture prize -- how many cells they expand into, and their bytes.</summary>
    private static int _censusGrid, _censusGridCells;
    private static long _censusGridBytes;
    private static int _censusLastUpload;
    private static bool _censusReported;
    /// <summary>Frames of upload silence that count as "the area settled" -- ~5s at 60fps, same reasoning as
    /// <see cref="RebuildRearm"/>.</summary>
    private const int CensusSettle = 300;

    private static void Census(IntPtr tex, long bytes, long transSaved, int gridN, int cell)
        => CensusTally(new CAurTextureBasic(tex).CompressedSize > 0, bytes, transSaved, gridN, cell);

    /// <summary>The tally half, split from the field read so <see cref="SelfCheck"/> can exercise the window:
    /// report-then-upload resets, silence settles, one line per window. A texture is charged to the PATH it
    /// came down (<c>+0x70</c>), with its transcode savings alongside, so the raw share reads as the
    /// post-transcode residual and the saved term says what the transcode took off.</summary>
    private static void CensusTally(bool bcn, long bytes, long transSaved, int gridN = 1, int cell = 0)
    {
        if (_censusReported)   // first upload after a settled report opens the next area's window
        {
            _censusRaw = _censusBcn = _censusTrans = 0;
            _censusRawBytes = _censusBcnBytes = _censusSaved = 0;
            _censusGrid = _censusGridCells = 0; _censusGridBytes = 0;
            _censusReported = false;
        }
        if (bcn) { _censusBcn++; _censusBcnBytes += bytes; }
        else { _censusRaw++; _censusRawBytes += bytes; }
        if (transSaved > 0) { _censusTrans++; _censusSaved += transSaved; }
        if (gridN > 1)
        {
            // Cell 0 counts the texture; every cell counts toward the object/byte total the de-atlas costs.
            if (cell == 0) { _censusGrid++; _censusGridCells += gridN; }
            _censusGridBytes += bytes;
        }
        _censusLastUpload = _dripFrame;
    }

    /// <summary>Emit the settle line once per upload window. Rides the <see cref="DripRebuilds"/> anchor:
    /// once a frame, engine context, no GL.</summary>
    private static void CensusSettleCheck()
    {
        if (_censusReported || _censusRaw + _censusBcn == 0 || _dripFrame - _censusLastUpload < CensusSettle)
            return;
        _censusReported = true;
        // #284 ledger keyed on raw CAurTexture*: the area edge recycles addresses, so a fresh texture
        // inherited a dead one's Never/spent verdict and was never re-asked for the session (no-gl-id 31/frame).
        _rebuildTried.Clear();
        int total = _censusRaw + _censusBcn;
        long totalBytes = _censusRawBytes + _censusBcnBytes;
        _log?.Info($"OwnedTextures: [census] area settled -- {_censusRaw}/{total} textures "
                 + $"({100 * _censusRaw / total}%) came down the UNCOMPRESSED path (basic+0x70==0), "
                 + $"{_censusRawBytes / OneMiB}MiB of {totalBytes / OneMiB}MiB resident "
                 + $"({(totalBytes == 0 ? 0 : 100 * _censusRawBytes / totalBytes)}% of VRAM)"
                 + (_censusTrans == 0 ? " -- #265: this share is the BCn-transcode prize"
                    : $"; #265 transcoded {_censusTrans} of them to BCn, saving {_censusSaved / OneMiB}MiB"));
        _log?.Info($"OwnedTextures: [census] #266 grids -- {_censusGrid} textures are numX*numY>1, "
                 + $"de-atlased into {_censusGridCells} GL textures ({_censusGridBytes / OneMiB}MiB)"
                 + (_censusGrid == 0 ? "; the array-texture path buys nothing here" : ""));
    }

    /// <summary>The residency line, every MiB rather than every texture -- the count climbs on every insert,
    /// so testing it per texture fired hundreds of lines while an area loaded. The resource count runs ABOVE
    /// "textures in the area": an animated texture cycles one resource per animation frame and a grid is one
    /// texture per cell. Correct and bounded, not a leak.</summary>
    private static void HighWater()
    {
        if (_bytes <= _peakBytes + OneMiB) return;
        _peakBytes = _bytes;
        _log?.Info($"OwnedTextures: [vram] high-water {_peakBytes / OneMiB}MiB across {_peakCount} " +
                   $"owned textures (uploads {_uploads}, re-uploads {_reuploads}, borrowed {_borrowed.Count}" +
                   (_pltOnGpu == null ? _pltWanted ? ", GPU-PLT NOT FORCED OFF (symbol unresolved)" : ", alloy.pltcpu=0"
                                      : _pltForced == 0 ? "" : $", GPU-PLT re-cleared {_pltForced}x")
                   // #360: the copy path is never silent -- a session that adopted nothing and a session
                   // that has no GPU composites to adopt must not read the same.
                   + (_pltCopied + _pltCopyRefused + _pltCopyDied == 0 ? ""
                      : $", GPU-PLT copied {_pltCopied} (refused {_pltCopyRefused}, source died {_pltCopyDied})")
                   + ")");
    }
}
