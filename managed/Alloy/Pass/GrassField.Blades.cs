using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Which atlas cells draw as procedural blades. Servers and builders swap grass textures at will
/// (<c>AddGrassOverride</c>, per-area tilesets) and NWN does not say which is grass and which is a flower, so
/// each live atlas is classified from its own pixels (<see cref="GrassAtlas"/>) once per upload. ponytail: no
/// hak 2DA override of the verdict -- that needs a <c>C2DA</c> read on both platforms.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>One material slot's verdict, keyed by the texture it was read from: the GL name and a hash of
    /// the resref. Either changing (an override, an area transition re-uploading under a recycled name)
    /// re-classifies; <c>Done</c> with no blade bits is also the failure latch, so an unreadable atlas is asked
    /// once, not every frame (the <see cref="HasMips"/> per-frame-rebuild trap).</summary>
    private struct Cls { public int Tid; public uint Name; public int Kinds; public bool Done; }
    private static readonly Cls[] _cls = new Cls[MaxGrassMaterials];
    /// <summary>Root/mid/tip per cell per material, display space.</summary>
    private static readonly Vector3[] _ramp = new Vector3[MaxGrassMaterials * 12];
    /// <summary>Per cell per material: the art's mean blade length over the card height (<see cref="GrassAtlas.Cell.Height"/>).</summary>
    private static readonly float[] _bladeLen = new float[MaxGrassMaterials * 4];

    /// <summary>Every live material's BLADE cell bits this frame: none under the texture style, or without
    /// the blade program. <paramref name="cards"/> takes the complement -- does the material draw any CARD --
    /// off the same walk, because it is a property of the material and the visible-patch loop was re-deriving
    /// it per patch for an answer that cannot differ between two patches of one material.</summary>
    private static void ClassifyAll(int* tids, bool* live, int* bits, bool* cards, bool bladeProg)
    {
        bool blades = Grass.Style.V == Grass.StyleBlades && bladeProg;
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            CAurGrassInfo info = live[m] ? GrassInfo(m) : default;
            bits[m] = blades && !info.IsNull ? Classified(m, tids[m], info) : 0;
            cards[m] = !info.IsNull && CardCells(info, bits[m]);
        }
    }

    /// <summary>The material's BLADE cell bits, classifying its atlas when it is new. Cold on a miss: one
    /// level-0 read per atlas upload (GL's readback, or the engine's own copy on Vulkan), which allocates.</summary>
    private static int Classified(int m, int tid, CAurGrassInfo info)
    {
        byte* resref = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref info.Name);
        uint name = 2166136261u;
        for (int i = 0; i < 32 && resref[i] != 0; i++) name = (name ^ resref[i]) * 16777619u;
        ref Cls c = ref _cls[m];
        if (c.Done && c.Tid == tid && c.Name == name) return c.Kinds;
        if (tid == 0) return 0;   // not uploaded yet: a card this frame (which skips it too), asked again next
        int w = 0, h = 0;
        byte[]? px = null;
        IntPtr tex = IntPtr.Zero;
        string? why = null;
        // the engine's own decoded copy, else the Vulkan image read back (one in flight)
        if (_gr.M < 0) px = TexScan.TopRgba(tex = _aurGet(resref, IntPtr.Zero, null, null, 0, 0), out w, out h);
        if (px == null)
        {
            int r = VkRead(m, tid, out px, out w, out h, out why);
            if (r == ReadPending) { c = new Cls { Tid = tid, Name = name }; return 0; }
        }
        c = new Cls { Tid = tid, Name = name, Done = true };
        if (w < 8 || h < 8 || (long)w * h > 4L << 20 || px == null)
        {
            _log?.Info($"[grass] material {m} tid {tid}: atlas unreadable ({w}x{h}"
                     + $", the engine's copy: {TexScan.Describe(tex)}, the Vulkan image: {why}"
                     + ") -- drawn as cards");
            return 0;
        }
        px ??= new byte[w * h * 4];
        Span<GrassAtlas.Cell> cells = stackalloc GrassAtlas.Cell[4];
        fixed (byte* p = px)
        {
            GrassAtlas.Classify(p, w, h, cells);
        }
        for (int k = 0; k < 4; k++)
        {
            if (cells[k].Blade) c.Kinds |= 1 << k;
            _ramp[m * 12 + k * 3] = cells[k].Root;
            _ramp[m * 12 + k * 3 + 1] = cells[k].Mid;
            _ramp[m * 12 + k * 3 + 2] = cells[k].Tip;
            _bladeLen[m * 4 + k] = cells[k].Height;
        }
        string v = "";
        for (int k = 0; k < 4; k++) v += (c.Kinds >> k & 1) != 0 ? $" BLADE({cells[k].Height:0.00}h)" : " CARD";
        _log?.Info($"[grass] material {m} tid {tid} {w}x{h} classified:{v}");
        return c.Kinds;
    }

    /// <summary>The Vulkan read of an atlas the engine kept no copy of: one level-0 copy into a host buffer at a time,
    /// collected once its submit has finished -- polled, never waited on.</summary>
    private sealed class GrassReadState
    {
        public VkBackend? Dev;
        public CommandList? Cl;
        public BufferId Buf;
        public long Cap, Want, At;
        public int M = -1, Tid, Polls;
        public ImageDesc Desc;
        /// <summary>A copy is in flight for material <paramref name="m"/>'s texture <paramref name="tid"/>.</summary>
        public bool Busy(int m, int tid) => M == m && Tid == tid;
    }

    private static GrassReadState _gr = new();

    /// <summary>Asks of one in-flight readback before it is reported stalled: seconds of frames, far past any real copy.</summary>
    private const int StallPolls = 600;

    /// <summary><see cref="VkRead"/>'s answers: the pixels are in hand, the copy is in flight, or it cannot be read.</summary>
    private const int ReadDone = 0, ReadPending = 1, ReadRefused = 2;

    /// <summary>Material <paramref name="m"/>'s atlas read off its Vulkan image as RGBA8, rows in upload order: the copy
    /// submitted alone on the first ask, the pixels on the first ask after THAT submit finished, whatever came after it.
    /// One copy in flight; another material's ask waits its turn as a card.</summary>
    private static int VkRead(int m, int tid, out byte[]? px, out int w, out int h, out string? why)
    {
        px = null; w = h = 0; why = null;
        if (VkTextures.Device(_log) is not VkBackend vk) { why = "no Vulkan device"; return ReadRefused; }
        GrassReadState s = _gr;
        if (s.Dev != vk) _gr = s = new GrassReadState { Dev = vk };
        if (s.M >= 0)
        {
            if (!s.Busy(m, tid)) return ReadPending;
            if (!vk.Finished(s.At))
            {
                if (++s.Polls == StallPolls)
                    _log?.Always($"[grass] material {m} tid {tid}: the atlas readback (submit {s.At}) has not finished after "
                               + $"{StallPolls} asks -- every grass atlas stays a card until it does");
                return ReadPending;
            }
            s.M = -1;
            var raw = new byte[s.Want];
            if (!vk.ReadMapped(s.Buf, 0, raw)) { why = "the readback was refused"; return ReadRefused; }
            w = s.Desc.W; h = s.Desc.H;
            px = Rgba(raw, s.Desc, out why);
            return px == null ? ReadRefused : ReadDone;
        }
        ImageId img = VkTextures.Image(tid);
        if (img.IsNull || !vk.DescOf(img, out ImageDesc d)) { why = "none under that name"; return ReadRefused; }
        if (d.W < 8 || d.H < 8 || (long)d.W * d.H > 4L << 20) { why = $"{d.W}x{d.H}"; return ReadRefused; }
        if (!Decodable(d.Fmt)) { why = $"{d.Fmt} has no CPU decoder here"; return ReadRefused; }
        long bytes = VkBackend.LevelBytes(d, 0);
        if (s.Buf.IsNull || s.Cap < bytes)
        {
            vk.Destroy(s.Buf);
            s.Buf = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.CopyDst, MemoryKind.Readback, "vk grass atlas readback"));
            s.Cap = s.Buf.IsNull ? 0 : bytes;
            if (s.Buf.IsNull) { why = "the readback buffer was refused"; return ReadRefused; }
        }
        CommandList cl = s.Cl ??= new CommandList("vulkan grass readback", 1 << 10, _log!);
        cl.Reset();
        cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(0, 0, 0, 0), d.W, d.H, s.Buf, 0));
        cl.ReadImages([img]);
        if (!vk.Submit(cl)) { why = "the copy's submit was refused"; return ReadRefused; }
        s.At = vk.Submitted - 1; s.M = m; s.Tid = tid; s.Desc = d; s.Want = bytes; s.Polls = 0;
        return ReadPending;
    }

    /// <summary>Can <see cref="Rgba"/> turn level 0 of format <paramref name="f"/> into RGBA8?</summary>
    private static bool Decodable(Format f) => f is Format.Rgba8 or Format.Rgba8Srgb || BcnDecode.Decodes(Bcn(f));

    /// <summary><paramref name="f"/> as the CPU decoder names it; <c>None</c> for a format it has no decoder for.</summary>
    private static DdsReader.Fmt Bcn(Format f) => f switch
    {
        Format.Bc1 or Format.Bc1A => DdsReader.Fmt.Bc1,
        Format.Bc2 => DdsReader.Fmt.Bc2,
        Format.Bc3 => DdsReader.Fmt.Bc3,
        Format.Bc7 => DdsReader.Fmt.Bc7,
        _ => DdsReader.Fmt.None,
    };

    /// <summary>Level 0 read back from an image of description <paramref name="d"/>, as RGBA8.</summary>
    private static byte[]? Rgba(byte[] raw, in ImageDesc d, out string? why)
    {
        why = null;
        if (d.Fmt is Format.Rgba8 or Format.Rgba8Srgb) return raw;
        DdsReader.Fmt f = Bcn(d.Fmt);
        if (!BcnDecode.Decodes(f)) { why = $"{d.Fmt} has no CPU decoder here"; return null; }
        int n = BcnDecode.PixelBytes(f);
        var dec = new byte[d.W * d.H * n];
        BcnDecode.Unpack(raw, d.W, d.H, f, dec);
        var o = new byte[d.W * d.H * 4];
        TexScan.Widen(dec, n, o);
        return o;
    }
}
