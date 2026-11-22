using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Every owned texture on the device: the bytes each of <see cref="OwnedTextures"/>' upload sites decodes go up
/// as a Vulkan image with the same levels and sampler, and the image takes a bindless index. Keyed by the owned
/// name the batch tables carry, whether an engine texture's name or one <see cref="NewName"/> hands out for an
/// image no engine texture stands behind.
/// </summary>
internal static class VkTextures
{
    /// <summary>One owned name's mirror: its image and index while it is on the device, and while it waits for room
    /// (evicted, or refused for want of room) the texels that bring it back.</summary>
    private sealed class Entry
    {
        public ImageId Image;
        public BindlessHandle Handle;
        public SamplerDesc Sampler;
        public SamplerId SamplerId;
        public long Bytes;
        public Format F;
        public int W, H, Mips;
        public bool Grey, GenMips;
        public string Label = "";
        /// <summary>The device frame a draw last asked for it; never drawn reads as the oldest.</summary>
        public long Drawn = long.MinValue;
        /// <summary>Each level's texels while it waits for room; null on the device.</summary>
        public byte[]?[]? Texels;
        public long RetryAt;
        /// <summary>A draw asked for it while it waited: the frame's end brings it back.</summary>
        public bool Wanted;
    }

    /// <summary>The next Vulkan-only name; outlives a device, so a name is never handed out twice in a session.</summary>
    private sealed class NameState { public int Next = (int)FirstName - 1; }

    private static readonly NameState _names = new();

    /// <summary>The first Vulkan-only name: far past any name GL hands out, so the two ranges never meet.</summary>
    internal const uint FirstName = 0x40000000;   // not-an-offset: a texture-name range

    /// <summary>One device's mirror, replaced whole with the device (context loss drops both).</summary>
    private sealed class VkTexState : VkBackend.IResidents
    {
        public VkBackend? Dev;
        /// <summary>Names waiting for room, evicted or refused, in the order they began waiting.</summary>
        public readonly List<Entry> Parked = new();
        /// <summary>Labels already named in cx.log as refused for room: a repeat refusal adds no line.</summary>
        public readonly HashSet<string> Named = new();
        public long Evictions;
        public bool Evict(long bytes) => EvictIdle(this, bytes);
        public void FrameEnd() => Restore(this);
        public readonly Dictionary<uint, Entry> ByName = new();
        public readonly Dictionary<SamplerDesc, SamplerId> Samplers = new();
        /// <summary>Names sharing each image: a sampler change is a new GL name over the same texels.</summary>
        public readonly Dictionary<uint, int> Refs = new();
        public long Mirrored, Refused;
        /// <summary>Bumped by every change to a name's mirror or a handle's name, and carried to a new device's state.</summary>
        public long Gen;
    }

    private static VkTexState _s = new();

    /// <summary>Distinct refusals logged before they are only counted.</summary>
    private const int RefusalLines = 8;

    /// <summary>Frames between two tries to bring back a texture that is waiting for room: the frame line's period.</summary>
    private const int RetryPeriod = 300;

    /// <summary>Readback offsets between evicted levels: a whole BC block and Vulkan's four-byte copy rule both divide it.</summary>
    private const int LevelAlign = 16;

    /// <summary>The device the mirror writes to, or null: not on Vulkan, Alloy not owning textures, or no device.
    /// Made here when it does not exist yet, which an area load reaches before the first Vulkan frame.</summary>
    internal static VkBackend? Device(ExtenderLog? log)
    {
        if (!Renderer.OnVulkan || log == null || !OwnedTextures.Armed || AlloyGate.Failed) return null;
        if (AlloyDevice.Vulkan(log) is not VkBackend vk) return null;
        if (_s.Dev != vk) Attach(vk);
        return vk;
    }

    /// <summary>Why <see cref="Device"/> answers null: the first of its conditions that is false, in its order.</summary>
    internal static string Absent(ExtenderLog? log)
        => !Renderer.OnVulkan ? "Alloy is not drawing on Vulkan"
         : log == null ? "the texture mirror has no log yet"
         : !OwnedTextures.Armed ? "Alloy does not own the textures"
         : AlloyGate.Failed ? "Alloy's gate failed"
         : AlloyDevice.Vulkan(log) is null ? "the Vulkan device does not exist"
         : "the device came back since it was asked for";

    /// <summary>A golden's device for the mirror, which it drops again with null: the session's gate never opens there.</summary>
    internal static void DeviceForCheck(VkBackend? vk) => Attach(vk);

    /// <summary>A fresh mirror for <paramref name="vk"/>, which lends it room and gives it a turn at each frame's end.</summary>
    private static void Attach(VkBackend? vk)
    {
        if (_s.Dev?.Residents == _s) _s.Dev.Residents = null;
        _s = new VkTexState { Dev = vk, Gen = _s.Gen + 1 };
        if (vk != null) vk.Residents = _s;
    }

    /// <summary>A new Vulkan image for owned name <paramref name="gl"/>, replacing whatever the name held; null id
    /// (counted, logged) when the device refuses it. The caller uploads its levels next.</summary>
    internal static ImageId Create(VkBackend vk, uint gl, Format f, int w, int h, int mips, bool grey, in SamplerDesc s, string label)
    {
        Forget(gl);
        VkTexState t = _s;
        var e = new Entry { Sampler = s, F = f, W = w, H = h, Mips = mips, Grey = grey, Label = label };
        for (int l = 0; l < mips; l++) e.Bytes += LevelBytes(f, Math.Max(1, w >> l), Math.Max(1, h >> l));
        if (!t.Samplers.TryGetValue(s, out e.SamplerId)) t.Samplers[s] = e.SamplerId = vk.CreateSampler(s);
        ImageId img = vk.CreateImage(DescOf(e));
        if (img.IsNull && vk.LastFault.Contains(VkBackend.NoRoomTag, StringComparison.Ordinal))
        {
            t.Refused++;
            e.Texels = new byte[mips][];
            e.RetryAt = vk.Frame + RetryPeriod;
            t.ByName[gl] = e;
            t.Parked.Add(e);
            t.Gen++;
            if (t.Named.Add(label))
                vk.Log.Always($"VkTextures: {label} needs {e.Bytes / (1024.0 * 1024.0):0.#} MiB of device memory and none is free "
                                    + "-- it draws as the stand-in until there is room");
            return default;
        }
        if (img.IsNull) { Refuse($"{label}: the device refused a {f} {w}x{h} image"); return default; }
        Seat(t, vk, e, img);
        t.ByName[gl] = e;
        t.Mirrored++;
        return img;
    }

    private static ImageDesc DescOf(Entry e)
        => new(ImageKind.Tex2D, e.F, e.W, e.H, 1, e.Mips, 1, 1,
               ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc | (e.Grey ? ImageUse.Grey : ImageUse.None), e.Label);

    /// <summary><paramref name="e"/> on the device as <paramref name="img"/>, with an index of its own.</summary>
    private static void Seat(VkTexState t, VkBackend vk, Entry e, ImageId img)
    {
        e.Image = img;
        e.Handle = vk.Bindless(img, e.SamplerId);
        t.Refs[img.Value] = 1;
        t.Gen++;
    }

    /// <summary>Level <paramref name="level"/> of <paramref name="gl"/>'s texels: onto its image, or kept for when it
    /// has room. False (logged) when the device refuses the upload or the name has no mirror.</summary>
    internal static bool Upload(VkBackend vk, uint gl, int level, ReadOnlySpan<byte> bytes)
    {
        if (!_s.ByName.TryGetValue(gl, out Entry? e) || (uint)level >= (uint)e.Mips) return false;
        if (!e.Image.IsNull) return vk.Upload(e.Image, level, 0, bytes);
        e.Texels![level] = bytes.ToArray();
        return true;
    }

    /// <summary>Fill <paramref name="gl"/>'s levels below the first from it, now or once it has room.</summary>
    internal static void GenerateMips(VkBackend vk, uint gl)
    {
        if (!_s.ByName.TryGetValue(gl, out Entry? e) || e.Mips < 2) return;
        if (!e.Image.IsNull) vk.GenerateMips(e.Image);
        else e.GenMips = true;
    }

    /// <summary>Free about <paramref name="bytes"/> of idle mirrors, least recently drawn first: each one's levels are
    /// read back to system RAM, and it waits there until a draw asks for it. False when nothing was idle.</summary>
    private static bool EvictIdle(VkTexState t, long bytes)
    {
        VkBackend? vk = t.Dev;
        if (vk == null) return false;
        long idleBefore = vk.Frame - 1, freed = 0, at = 0;
        var batch = new List<(Entry E, long At)>();
        while (freed < bytes)
        {
            Entry? pick = null;
            foreach (Entry e in t.ByName.Values)
                if (!e.Image.IsNull && e.Drawn < idleBefore && t.Refs.GetValueOrDefault(e.Image.Value) == 1   // ponytail: a cloned image never evicts; count its names to lift it
                    && !batch.Exists(b => b.E == e) && (pick == null || e.Drawn < pick.Drawn))
                    pick = e;
            if (pick == null) break;
            batch.Add((pick, at));
            for (int l = 0; l < pick.Mips; l++) at += Aligned(LevelBytes(pick.F, Math.Max(1, pick.W >> l), Math.Max(1, pick.H >> l)));
            freed += pick.Bytes;
        }
        if (batch.Count == 0) return false;
        BufferId rb = vk.CreateBuffer(new BufferDesc(at, BufferUse.CopyDst, MemoryKind.Readback, "evicted textures"));
        if (rb.IsNull) return false;
        var cl = new CommandList("evict textures", 1 << 12, vk.Log);
        foreach ((Entry e, long off) in batch)
            for (int l = 0, o = 0; l < e.Mips; l++)
            {
                int w = Math.Max(1, e.W >> l), h = Math.Max(1, e.H >> l);
                cl.CopyImageToBuffer(new CopyToBufferCmd(e.Image, new ImageRegion(l, 0, 0, 0), w, h, rb, off + o));
                o += (int)Aligned(LevelBytes(e.F, w, h));
            }
        bool read = vk.Submit(cl) && vk.WaitSubmitted();
        foreach ((Entry e, long off) in batch)
        {
            var texels = new byte[e.Mips][];
            for (int l = 0, o = 0; l < e.Mips; l++)
            {
                int w = Math.Max(1, e.W >> l), h = Math.Max(1, e.H >> l);
                texels[l] = new byte[LevelBytes(e.F, w, h)];
                if (read) vk.ReadMapped(rb, off + o, texels[l]);
                o += (int)Aligned(texels[l]!.Length);
            }
            t.Refs.Remove(e.Image.Value);
            vk.DestroyNow(e.Image, e.Handle);
            e.Image = default;
            e.Handle = default;
            e.Texels = texels;
            e.GenMips = false;
            e.RetryAt = vk.Frame;
            t.Parked.Add(e);
            t.Evictions++;
        }
        vk.Destroy(rb);
        t.Gen++;
        return true;
    }

    private static long Aligned(long n) => (n + LevelAlign - 1) / LevelAlign * LevelAlign;

    /// <summary>The frame's end: each waiting name a draw asked for, whose retry is due, goes back on the device with
    /// its texels; one refused again waits a period. Returns at once when nothing waits.</summary>
    private static void Restore(VkTexState t)
    {
        if (t.Parked.Count == 0 || t.Dev is not VkBackend vk) return;
        long frame = vk.Frame;
        for (int i = t.Parked.Count - 1; i >= 0; i--)
        {
            if (i >= t.Parked.Count) continue;
            Entry e = t.Parked[i];
            if (!e.Wanted || frame < e.RetryAt) continue;
            ImageId img = vk.CreateImage(DescOf(e));
            if (img.IsNull) { e.RetryAt = frame + RetryPeriod; continue; }
            t.Parked.Remove(e);
            Seat(t, vk, e, img);
            byte[]?[] texels = e.Texels!;
            for (int l = 0; l < texels.Length; l++) if (texels[l] is byte[] b) vk.Upload(img, l, 0, b);
            if (e.GenMips) vk.GenerateMips(img);
            e.Texels = null;
            e.Wanted = false;
            e.Drawn = frame;
        }
    }

    /// <summary><paramref name="gl"/> is a copy of <paramref name="src"/>'s texels under sampler <paramref name="s"/>:
    /// the same image, its own index. A no-op when the source has no mirror.</summary>
    internal static void Clone(uint src, uint gl, in SamplerDesc s)
    {
        VkTexState t = _s;
        // ponytail: a source waiting for room clones nothing; a clone that waits beside it lifts this
        if (t.Dev == null || !t.ByName.TryGetValue(src, out Entry? e) || e.Image.IsNull) return;
        Forget(gl);
        if (!t.Samplers.TryGetValue(s, out SamplerId sid)) t.Samplers[s] = sid = t.Dev.CreateSampler(s);
        t.ByName[gl] = new Entry { Image = e.Image, Handle = t.Dev.Bindless(e.Image, sid), Sampler = s, SamplerId = sid, Bytes = e.Bytes,
                                   F = e.F, W = e.W, H = e.H, Mips = e.Mips, Grey = e.Grey, Label = e.Label };
        t.Gen++;
        t.Refs[e.Image.Value] = t.Refs.GetValueOrDefault(e.Image.Value) + 1;
    }

    /// <summary>One cell of an uncompressed sheet, tightly packed for Vulkan: four channels as RGBA8, three widened
    /// with an opaque alpha, one as R8 (sampled grey), two as RG8; a four-bit-per-channel texel (<paramref name="nibbles"/>)
    /// widened to RGBA8. A null sheet packs zeros, as GL's zeroed storage holds.</summary>
    internal static unsafe byte[] Pack(byte* sheet, int rowPixels, int x0, int y0, int w, int h, int channels, bool nibbles, out Format f)
    {
        int outPx = channels switch { 1 => 1, 2 when !nibbles => 2, _ => 4 };
        f = outPx switch { 1 => Format.R8, 2 => Format.Rg8, _ => Format.Rgba8 };
        byte[] px = new byte[w * h * outPx];
        if (sheet == null) return px;
        var src = new ReadOnlySpan<byte>(sheet, checked(rowPixels * (y0 + h) * channels));
        for (int y = 0; y < h; y++)
        {
            int at = ((y0 + y) * rowPixels + x0) * channels, o = y * w * outPx;
            for (int x = 0; x < w; x++, o += outPx, at += channels)
                switch (channels)
                {
                    case 1: px[o] = src[at]; break;
                    case 2 when !nibbles: px[o] = src[at]; px[o + 1] = src[at + 1]; break;
                    case 3: px[o] = src[at]; px[o + 1] = src[at + 1]; px[o + 2] = src[at + 2]; px[o + 3] = 255; break;
                    case 4: px[o] = src[at]; px[o + 1] = src[at + 1]; px[o + 2] = src[at + 2]; px[o + 3] = src[at + 3]; break;
                    default:
                        // 4_4_4_4: one native-endian ushort, R in the high nibble down to A in the low
                        int v = src[at] | src[at + 1] << 8;
                        px[o] = (byte)((v >> 12) * 17); px[o + 1] = (byte)(((v >> 8) & 15) * 17);
                        px[o + 2] = (byte)(((v >> 4) & 15) * 17); px[o + 3] = (byte)((v & 15) * 17);
                        break;
                }
        }
        return px;
    }

    /// <summary>A name for a texture that lives on Vulkan only: no GL object is made for it, and GL calls on it are no-ops.</summary>
    internal static uint NewName() => (uint)System.Threading.Interlocked.Increment(ref _names.Next);

    /// <summary>Does <paramref name="gl"/> hold an image?</summary>
    internal static bool Has(uint gl) => _s.ByName.ContainsKey(gl);

    /// <summary>Owned name <paramref name="gl"/>'s image is on the device now, not waiting for room.</summary>
    internal static bool Resident(uint gl) => _s.ByName.TryGetValue(gl, out Entry? e) && !e.Image.IsNull;

    /// <summary>The bytes <paramref name="gl"/>'s image holds over every level; 0 when it has none.</summary>
    internal static long Bytes(uint gl) => _s.ByName.TryGetValue(gl, out Entry? e) ? e.Bytes : 0;

    private static long LevelBytes(Format f, int w, int h) => f.IsCompressed()
        ? (long)((w + 3) / 4) * ((h + 3) / 4) * f.BlockBytes()
        : (long)w * h * (f switch { Format.R8 => 1, Format.Rg8 => 2, _ => 4 });

    /// <summary>The sampler <paramref name="gl"/>'s mirror was made with, for a replacement that keeps it.</summary>
    internal static SamplerDesc? SamplerOf(uint gl) => _s.ByName.TryGetValue(gl, out Entry? e) ? e.Sampler : null;

    /// <summary><paramref name="gl"/>'s mirror, marked drawn this frame and, while it waits for room, wanted back.</summary>
    private static Entry? Drawn(int gl)
    {
        VkTexState t = _s;
        if (!t.ByName.TryGetValue((uint)gl, out Entry? e)) return null;
        e.Drawn = t.Dev!.Frame;
        if (e.Image.IsNull) e.Wanted = true;
        return e;
    }

    /// <summary>The bindless index of <paramref name="gl"/>'s mirror; 0 when it has none or it waits for room.</summary>
    internal static uint Index(int gl) => Drawn(gl) is Entry e ? (uint)e.Handle.Value : 0;

    /// <summary>The mirror's image, for a bound (not bindless) read; null id when it has none or it waits for room.</summary>
    internal static ImageId Image(int gl) => Drawn(gl) is Entry e ? e.Image : default;

    /// <summary>The sampler a bound read of <paramref name="gl"/>'s mirror goes through; null id when it has none.</summary>
    internal static SamplerId SamplerId(int gl) => _s.ByName.TryGetValue((uint)gl, out Entry? e) && !e.Image.IsNull ? e.SamplerId : default;

    /// <summary>The bit that marks a record-table handle as Vulkan's: the owned name in the low half, no GL handle behind it.</summary>
    private const ulong VkHandle = 1UL << 63;

    /// <summary>The handle the record tables carry for owned name <paramref name="gl"/> on Vulkan; nothing is minted.</summary>
    internal static ulong HandleOf(uint gl) => gl == 0 ? 0 : VkHandle | gl;

    /// <summary>The mirror's generation: unchanged means every handle resolves to the index it did.</summary>
    internal static long Generation => _s.Gen;

    /// <summary>The bindless index of the mirror behind <see cref="HandleOf"/>'s <paramref name="handle"/>; 0 when it has none.</summary>
    internal static uint IndexOfHandle(ulong handle) => (handle & VkHandle) != 0 ? Index((int)(uint)handle) : 0;

    /// <summary>The GL name died: its mirror and its index go after the frames in flight.</summary>
    internal static void Forget(uint gl)
    {
        VkTexState s = _s;
        if (s.Dev == null || !s.ByName.Remove(gl, out Entry? e)) return;
        s.Gen++;
        if (e.Image.IsNull) { s.Parked.Remove(e); return; }
        s.Dev.Unbind(e.Handle);
        int refs = s.Refs.GetValueOrDefault(e.Image.Value) - 1;
        if (refs > 0) { s.Refs[e.Image.Value] = refs; return; }
        s.Refs.Remove(e.Image.Value);
        s.Dev.Destroy(e.Image);
    }

    /// <summary>A texture whose mirror could not be made draws with the null index; logged a few times, then counted.</summary>
    internal static void Refuse(string why)
    {
        if (++_s.Refused <= RefusalLines)
            KnobGroup.Log?.Always($"VkTextures: REFUSED {why} -- the texture draws as the stand-in on Vulkan"
                                + (_s.Refused == RefusalLines ? "; further refusals are only counted" : ""));
    }

    /// <summary>The engine's sampler state (GL enums, <c>OwnedTextures.EngineSampler</c>) as the seam's, asking
    /// for <paramref name="aniso"/> samples (1 = none): the level is the caller's, not this translation's.</summary>
    internal static SamplerDesc Sampler(int min, int mag, int wrapS, int wrapT, float aniso)
    {
        Filter Of(int f) => f is (int)EngineTokens.LINEAR or (int)EngineTokens.LINEAR_MIPMAP_LINEAR or (int)EngineTokens.LINEAR_MIPMAP_NEAREST ? Filter.Linear : Filter.Nearest;
        MipMode mip = min switch
        {
            (int)EngineTokens.LINEAR_MIPMAP_LINEAR or (int)EngineTokens.NEAREST_MIPMAP_LINEAR => MipMode.Linear,
            (int)EngineTokens.NEAREST_MIPMAP_NEAREST or (int)EngineTokens.LINEAR_MIPMAP_NEAREST => MipMode.Nearest,
            _ => MipMode.None,
        };
        Wrap W(int w) => w == (int)EngineTokens.CLAMP_TO_EDGE ? Wrap.Clamp : Wrap.Repeat;
        return new SamplerDesc(Of(min), Of(mag), mip, W(wrapS), Aniso: aniso, WrapT: W(wrapT));
    }

    /// <summary>The seam's twin of a GL compressed internal format an owned texture is stored in; Unknown for
    /// one Vulkan's desktop devices do not sample (ETC2).</summary>
    internal static Format Compressed(uint gl) => gl switch
    {
        0x83F0 => Format.Bc1, 0x83F1 => Format.Bc1A, 0x83F2 => Format.Bc2, 0x83F3 => Format.Bc3,   // not-an-offset: S3TC DXT1 / DXT1A / DXT3 / DXT5
        0x8DBB => Format.Bc4, 0x8DBC => Format.Bc4S, 0x8DBD => Format.Bc5, 0x8DBE => Format.Bc5S,   // not-an-offset: RGTC1/2, unsigned and signed
        0x8E8F => Format.Bc6U, 0x8E8E => Format.Bc6S, 0x8E8C => Format.Bc7,                        // not-an-offset: BPTC float and unorm
        _ => Format.Unknown,
    };

    /// <summary>The textures refused for room that wait to be placed again.</summary>
    internal static int Waiting => _s.Parked.Count;

    /// <summary>One line for the period report.</summary>
    internal static string Line() => _s.Dev == null ? "" : $"VkTextures: {_s.ByName.Count} mirrored ({_s.Mirrored} made, {_s.Refused} refused, {_s.Evictions} evicted, {_s.Parked.Count} waiting for room)";

    /// <summary>Oracle: the GL tokens glcorearb.h assigns, hand-written, against the seam formats they are.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkTextures.SelfCheck");
        check.Expect(Compressed(0x83F0) == Format.Bc1 && Compressed(0x83F3) == Format.Bc3 && Compressed(0x8DBD) == Format.Bc5
                  && Compressed(0x8E8C) == Format.Bc7 && Compressed(0x9274) == Format.Unknown,
            "DXT1 0x83F0 is BC1, DXT5 0x83F3 BC3, RG_RGTC2 0x8DBD BC5, RGBA_BPTC 0x8E8C BC7; ETC2 0x9274 has no Vulkan twin");
        SamplerDesc s = Sampler(0x2703, 0x2601, 0x812F, 0x2901, aniso: 1f);
        check.Expect(s.Min == Filter.Linear && s.Mip == MipMode.Linear && s.Mag == Filter.Linear && s.Wrap == Wrap.Clamp && s.WrapT == Wrap.Repeat,
            "LINEAR_MIPMAP_LINEAR 0x2703 / LINEAR 0x2601 / CLAMP_TO_EDGE 0x812F on S / REPEAT 0x2901 on T read as trilinear, clamped on S only");
        check.Expect(Sampler(0x2700, 0x2600, 0x2901, 0x2901, 1f).Mip == MipMode.Nearest && Sampler(0x2600, 0x2600, 0x2901, 0x2901, 1f).Mip == MipMode.None,
            "NEAREST_MIPMAP_NEAREST 0x2700 picks the nearest mip; plain NEAREST 0x2600 reads the base level only");
        check.Expect(Post.Aniso.Default == 16 && string.Join(' ', Post.Aniso.Values) == "1 2 4 8 16",
            $"post.aniso offers 1, 2, 4, 8 and 16 and defaults to 16x, so today's image is unchanged (offers: {string.Join(' ', Post.Aniso.Values)}, default {Post.Aniso.Default}x)");
        check.Expect(Math.Abs(Sampler(0x2703, 0x2601, 0x2901, 0x2901, aniso: 4f).Aniso - 4f) < 1e-6f
                  && Math.Abs(s.Aniso - 1f) < 1e-6f,
            "the sampler asks for the level its caller named -- four samples for 4, and one where the caller takes no anisotropy");
        log.Info(check.Ok ? "VkTextures.SelfCheck: passed" : "VkTextures.SelfCheck: FAILED");
        return check.Ok;
    }
}
