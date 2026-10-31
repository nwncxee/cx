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

    /// <summary>The material's BLADE cell bits, classifying its atlas when it is new. Cold on a miss: the engine's own
    /// decoded copy classified now, else the Vulkan image's copy queued, classified when its submit finishes.</summary>
    private static int Classified(int m, int tid, CAurGrassInfo info)
    {
        byte* resref = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref info.Name);
        uint name = 2166136261u;
        for (int i = 0; i < 32 && resref[i] != 0; i++) name = (name ^ resref[i]) * 16777619u;
        ref Cls c = ref _cls[m];
        if (c.Done && c.Tid == tid && c.Name == name) return c.Kinds;
        if (tid == 0 || _gr.M >= 0) return 0;   // not uploaded yet, or another atlas's read in flight: a card this frame
        IntPtr tex = _aurGet(resref, IntPtr.Zero, null, null, 0, 0);
        byte[]? px = TexScan.TopRgba(tex, out int w, out int h);
        if (px != null) return Classify(m, tid, name, px, w, h, null);
        if (VkRead(m, tid, name, out string? why)) { c = new Cls { Tid = tid, Name = name }; return 0; }
        if (c.Done && c.Tid == tid && c.Name == name) return c.Kinds;   // a refused submit already told its work
        return Classify(m, tid, name, null, 0, 0, $"the engine's copy: {TexScan.Describe(tex)}, the Vulkan image: {why}");
    }

    /// <summary>Material <paramref name="m"/>'s verdict from its atlas pixels into the tables, logged; an unreadable
    /// atlas (<paramref name="px"/> null, or out of range) latches as all cards, <paramref name="why"/> saying why.</summary>
    private static int Classify(int m, int tid, uint name, byte[]? px, int w, int h, string? why)
    {
        ref Cls c = ref _cls[m];
        c = new Cls { Tid = tid, Name = name, Done = true };
        if (w < 8 || h < 8 || (long)w * h > 4L << 20 || px == null)
        {
            _log?.Info($"[grass] material {m} tid {tid}: atlas unreadable ({w}x{h}, {why ?? "out of range"}) -- drawn as cards");
            return 0;
        }
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
    /// classified by <see cref="OnAtlasRead"/> when the backend says its submit finished.</summary>
    private sealed class GrassReadState
    {
        public VkBackend? Dev;
        public CommandList? Cl;
        public BufferId Buf;
        public long Cap, Want;
        public int M = -1, Tid, Gen;
        public uint Name;
        public ImageDesc Desc;
    }

    private static GrassReadState _gr = new();

    /// <summary><see cref="OnAtlasRead"/>, made once: queuing a read allocates no delegate.</summary>
    private static readonly Action<int, bool> AtlasRead = OnAtlasRead;

    /// <summary>Queue material <paramref name="m"/>'s atlas copy off its Vulkan image; true when it is in flight, false
    /// with <paramref name="why"/> when it cannot be read.</summary>
    private static bool VkRead(int m, int tid, uint name, out string? why)
    {
        why = null;
        if (VkTextures.Device(_log) is not VkBackend vk) { why = "no Vulkan device"; return false; }
        GrassReadState s = _gr;
        if (s.Dev != vk) _gr = s = new GrassReadState { Dev = vk, Gen = s.Gen };
        ImageId img = VkTextures.Image(tid);
        if (img.IsNull || !vk.DescOf(img, out ImageDesc d)) { why = "none under that name"; return false; }
        if (d.W < 8 || d.H < 8 || (long)d.W * d.H > 4L << 20) { why = $"{d.W}x{d.H}"; return false; }
        if (!Decodable(d.Fmt)) { why = $"{d.Fmt} has no CPU decoder here"; return false; }
        long bytes = VkBackend.LevelBytes(d, 0);
        if (s.Buf.IsNull || s.Cap < bytes)
        {
            vk.Destroy(s.Buf);
            s.Buf = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.CopyDst, MemoryKind.Readback, "vk grass atlas readback"));
            s.Cap = s.Buf.IsNull ? 0 : bytes;
            if (s.Buf.IsNull) { why = "the readback buffer was refused"; return false; }
        }
        CommandList cl = s.Cl ??= new CommandList("vulkan grass readback", 1 << 10, _log!);
        cl.Reset();
        cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(0, 0, 0, 0), d.W, d.H, s.Buf, 0));
        cl.ReadImages([img]);
        s.M = m; s.Tid = tid; s.Name = name; s.Desc = d; s.Want = bytes;
        cl.WhenDone(AtlasRead, ++s.Gen);
        if (!vk.Submit(cl)) { s.M = -1; why = "the copy's submit was refused"; return false; }
        return true;
    }

    /// <summary>The atlas copy's submit finished (or never ran): its pixels classified into the material's verdict.
    /// A read a newer one replaced, or a device that changed, is dropped.</summary>
    private static void OnAtlasRead(int gen, bool ran)
    {
        GrassReadState s = _gr;
        if (gen != s.Gen || s.M < 0) return;
        int m = s.M;
        s.M = -1;
        if (!ran) { Classify(m, s.Tid, s.Name, null, 0, 0, "the copy's submit never ran"); return; }
        var raw = new byte[s.Want];
        if (!s.Dev!.ReadMapped(s.Buf, 0, raw)) { Classify(m, s.Tid, s.Name, null, 0, 0, "the readback was refused"); return; }
        byte[]? px = Rgba(raw, s.Desc, out string? why);
        Classify(m, s.Tid, s.Name, px, s.Desc.W, s.Desc.H, why);
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
