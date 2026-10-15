using System;
using System.Buffers.Binary;

namespace CX.Alloy;

/// <summary>The pure half of <see cref="DdsReader"/>: both headers parsed and the chain measured from the resource
/// bytes alone. No engine reads, so <c>tools/rastercheck</c> compiles this file alone.</summary>
internal static partial class DdsReader
{
    /// <summary>The engine's <c>Aurora::AuroraContainerFormats</c> for the two DDS headers.</summary>
    internal const int ContainerLegacy = 1, ContainerMs = 2;
    internal const int LegacyHeader = 20, MsHeader = 128;
    private const int Dx10Header = MsHeader + 20;
    /// <summary>Microsoft header fields read (from the file start, magic included): dwHeight, dwWidth, dwDepth,
    /// dwMipMapCount, ddspf.dwFourCC, dwCaps2; then the DX10 extension's dxgiFormat, resourceDimension, miscFlag, arraySize.</summary>
    private const int MsHeight = 12, MsWidth = 16, MsMips = 28, MsFourCC = 84, MsCaps2 = 112,
                      Dx10Format = 128, Dx10Dim = 132, Dx10Misc = 136, Dx10Array = 140;
    private const uint MagicDds = 0x20534444;   // not-an-offset: 'DDS '
    private const uint FourDxt1 = 0x31545844, FourDxt5 = 0x35545844, FourAti1 = 0x31495441, FourAti2 = 0x32495441, FourDxt3 = 0x33545844,   // not-an-offset: FourCCs
                       FourBc4U = 0x55344342, FourBc4S = 0x53344342, FourBc5U = 0x55354342, FourBc5S = 0x53354342, FourDx10 = 0x30315844;   // not-an-offset: FourCCs
    /// <summary>dwCaps2: a cube map, all six faces present, a volume.</summary>
    private const uint Caps2Cube = 0x200, Caps2AllFaces = 0xFC00, Caps2Volume = 0x200000;   // not-an-offset: DDSCAPS2 flags
    /// <summary>DX10 extension: <c>D3D10_RESOURCE_DIMENSION_TEXTURE2D</c> and <c>D3D10_RESOURCE_MISC_TEXTURECUBE</c>.</summary>
    private const uint Dim2D = 3, MiscCube = 0x4;   // not-an-offset: DX10 enum values

    /// <summary>Every block format either header can name. The engine's table uploads <see cref="Bc1"/>,
    /// <see cref="Bc3"/>, <see cref="Bc4"/> and <see cref="Bc5"/>; the rest only Alloy uploads.</summary>
    internal enum Fmt : byte { None, Bc1, Bc2, Bc3, Bc4, Bc4S, Bc5, Bc5S, Bc6U, Bc6S, Bc7 }

    /// <summary>The engine's own table covers this format: its <c>CreateTexture</c> uploads it correctly.</summary>
    internal static bool EngineUploads(Fmt f) => f is Fmt.Bc1 or Fmt.Bc3 or Fmt.Bc4 or Fmt.Bc5;

    /// <summary>The channel count reported to the engine: its native meaning for the four formats it knows, and
    /// otherwise the one whose block size matches (8 bytes = 3 or 1, 16 = 4 or 2), so every size the engine
    /// computes from it is right.</summary>
    internal static int ChannelsOf(Fmt f) => f switch
    {
        Fmt.Bc1 => 3, Fmt.Bc4 or Fmt.Bc4S => 1, Fmt.Bc5 or Fmt.Bc5S => 2, Fmt.None => 0, _ => 4,
    };

    /// <summary>The engine's alpha-mean rule (0.5 for a DXT5, 1.0 otherwise), extended: 0.5 for any format that carries alpha.</summary>
    private static float AlphaMeanOf(Fmt f) => f is Fmt.Bc2 or Fmt.Bc3 or Fmt.Bc7 ? 0.5f : 1.0f;

    /// <summary>The GL internal format Alloy uploads <paramref name="f"/> as. The UNORM one for an sRGB DXGI
    /// format too: every other texture reaches the shaders undecoded, and moving sRGB decode into the sampler
    /// is its own change.</summary>
    internal static uint GlFormat(Fmt f) => f switch
    {
        Fmt.Bc1 => 0x83F0, Fmt.Bc2 => 0x83F2, Fmt.Bc3 => 0x83F3,   // not-an-offset: COMPRESSED_RGB_S3TC_DXT1 / RGBA_DXT3 / RGBA_DXT5
        Fmt.Bc4 => 0x8DBB, Fmt.Bc4S => 0x8DBC, Fmt.Bc5 => 0x8DBD, Fmt.Bc5S => 0x8DBE,   // not-an-offset: COMPRESSED_(SIGNED_)RED/RG_RGTC1/2
        Fmt.Bc6S => 0x8E8E, Fmt.Bc6U => 0x8E8F, Fmt.Bc7 => 0x8E8C,   // not-an-offset: COMPRESSED_RGB_BPTC_(UN)SIGNED_FLOAT / RGBA_BPTC_UNORM
        _ => 0,
    };

    private static Fmt FromFourCC(uint four) => four switch
    {
        FourDxt1 => Fmt.Bc1, FourDxt3 => Fmt.Bc2, FourDxt5 => Fmt.Bc3, FourAti1 or FourBc4U => Fmt.Bc4, FourBc4S => Fmt.Bc4S,
        FourAti2 or FourBc5U => Fmt.Bc5, FourBc5S => Fmt.Bc5S, _ => Fmt.None,
    };

    /// <summary>DXGI_FORMAT codes: the BC1-BC7 UNORM, SNORM, UF16/SF16 and UNORM_SRGB members. TYPELESS ones are refused.</summary>
    private static Fmt FromDxgi(uint dxgi, out bool srgb)
    {
        srgb = dxgi is 72 or 75 or 78 or 99;
        return dxgi switch
        {
            71 or 72 => Fmt.Bc1, 74 or 75 => Fmt.Bc2, 77 or 78 => Fmt.Bc3, 80 => Fmt.Bc4, 81 => Fmt.Bc4S,
            83 => Fmt.Bc5, 84 => Fmt.Bc5S, 95 => Fmt.Bc6U, 96 => Fmt.Bc6S, 98 or 99 => Fmt.Bc7, _ => Fmt.None,
        };
    }

    internal struct Info
    {
        public int Container, W, H, Channels;
        /// <summary>Level 0's byte size as the engine reports it: stored (legacy) or computed (Microsoft). The
        /// chain walk below always computes.</summary>
        public int Level0;
        public float AlphaMean;
        /// <summary>Where level 0 starts in the resource.</summary>
        public int DataOffset;
        /// <summary>Levels of the geometric chain to 1x1 -- what the engine's upload walks, bound or not.</summary>
        public int Levels;
        /// <summary>Of those, how many the resource holds in full; less than <see cref="Levels"/> is a
        /// partial chain the engine reads past the end of.</summary>
        public int Present;
        /// <summary>Byte size of the full geometric chain.</summary>
        public long ChainBytes;
        public Fmt Format;
        /// <summary>The engine's own reader answers this file correctly: a legacy header, or a Microsoft one with
        /// DXT1/DXT5/ATI1/ATI2 (<c>CResDDS::GetChannelCount</c> @0x1135520). Set before any refusal.</summary>
        public bool EngineReads;
        /// <summary>The DXGI format was an sRGB one; uploaded as its UNORM twin (see <see cref="GlFormat"/>).</summary>
        public bool Srgb;
        /// <summary>6 for a cube map (faces +X, -X, +Y, -Y, +Z, -Z, each a whole chain), else 1.</summary>
        public int Faces;
        /// <summary>Bytes from one face's level 0 to the next: the levels the file stores, not the geometric count.</summary>
        public long FaceStride;
    }

    /// <summary>BCn block bytes for an engine channel count: 8 for BC1/BC4, 16 for BC3/BC5.</summary>
    internal static int BlockBytes(int channels) => channels == 3 || channels == 1 ? 8 : 16;

    /// <summary><c>CResDDS::GetCompressedSize</c>: ceil-to-block grid times the block size.</summary>
    internal static int LevelSize(int w, int h, int channels)
        => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes(channels);

    /// <summary>Parse the header and measure the chain. False names why in <paramref name="why"/>: an
    /// unknown or typeless format, an array, a volume, a partial cube, a zero or absurd size, or a resource
    /// too short for level 0 (of every face).</summary>
    internal static bool Parse(ReadOnlySpan<byte> raw, out Info info, out string? why)
    {
        info = default; why = null;
        info.Faces = 1;
        if (raw.Length < LegacyHeader) { why = "shorter than any header"; return false; }
        int stored = int.MaxValue;
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) == MagicDds)
        {
            info.Container = ContainerMs;
            if (raw.Length < MsHeader) { why = "truncated Microsoft header"; return false; }
            info.H = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[MsHeight..]);
            info.W = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[MsWidth..]);
            uint four = BinaryPrimitives.ReadUInt32LittleEndian(raw[MsFourCC..]);
            info.EngineReads = four is FourDxt1 or FourDxt5 or FourAti1 or FourAti2;
            uint caps2 = BinaryPrimitives.ReadUInt32LittleEndian(raw[MsCaps2..]);
            // DirectXTex's rule: a zero count is one level, the DDSD_MIPMAPCOUNT flag is not consulted.
            stored = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[MsMips..]));
            if ((caps2 & Caps2Volume) != 0) { why = "a volume texture"; return false; }
            if ((caps2 & Caps2Cube) != 0)
            {
                if ((caps2 & Caps2AllFaces) != Caps2AllFaces) { why = $"a cube map with only some faces (caps2 0x{caps2:x})"; return false; }
                info.Faces = 6;
            }
            info.DataOffset = MsHeader;
            if (four == FourDx10)
            {
                if (raw.Length < Dx10Header) { why = "truncated DX10 header"; return false; }
                uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(raw[Dx10Format..]);
                uint dim = BinaryPrimitives.ReadUInt32LittleEndian(raw[Dx10Dim..]);
                bool cube = (BinaryPrimitives.ReadUInt32LittleEndian(raw[Dx10Misc..]) & MiscCube) != 0;
                uint array = Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(raw[Dx10Array..]));
                info.Format = FromDxgi(dxgi, out info.Srgb);
                if (info.Format == Fmt.None) { why = $"DXGI format {dxgi} is not a BC1-BC7 UNORM/SNORM/SRGB/UF16/SF16 one"; return false; }
                if (dim != Dim2D) { why = $"resource dimension {dim} (2D only)"; return false; }
                if (array != 1) { why = $"an array of {array}: nothing in Alloy or the engine samples a texture array"; return false; }
                info.Faces = cube ? 6 : 1;
                info.DataOffset = Dx10Header;
            }
            else
            {
                info.Format = FromFourCC(four);
                if (info.Format == Fmt.None) { why = $"FourCC 0x{four:x8} is not DXT1/DXT3/DXT5/ATI1/ATI2/BC4U/BC4S/BC5U/BC5S/DX10"; return false; }
            }
            info.Channels = ChannelsOf(info.Format);
            info.AlphaMean = AlphaMeanOf(info.Format);
        }
        else
        {
            info.Container = ContainerLegacy;
            info.EngineReads = true;
            info.W = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw);
            info.H = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
            info.Channels = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
            if (info.Channels != 3 && info.Channels != 4) { why = $"legacy channel count {info.Channels} (3 or 4 only)"; return false; }
            info.Format = info.Channels == 4 ? Fmt.Bc3 : Fmt.Bc1;
            info.DataOffset = LegacyHeader;
            info.Level0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);   // stored, and the engine reports it as stored
            info.AlphaMean = BinaryPrimitives.ReadSingleLittleEndian(raw[16..]);
        }
        if (info.W <= 0 || info.H <= 0 || info.W > MaxSide || info.H > MaxSide) { why = $"size {info.W}x{info.H}"; return false; }
        if (info.Faces == 6 && info.W != info.H) { why = $"a cube map with {info.W}x{info.H} faces"; return false; }
        if (info.Container == ContainerMs) info.Level0 = LevelSize(info.W, info.H, info.Channels);
        long avail = raw.Length - info.DataOffset, off = 0;
        for (int w = info.W, h = info.H; ; w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
        {
            long sz = LevelSize(w, h, info.Channels);
            if (info.Levels < stored) info.FaceStride += sz;
            // A cube's face 0 is followed by face 1, so only the stored count bounds it; a 2D chain is measured by fit, as the engine walks it.
            if (off + sz <= avail && (info.Faces == 1 || info.Levels < stored)) info.Present++;
            off += sz; info.Levels++;
            if (w == 1 && h == 1) break;
        }
        info.ChainBytes = off;
        if (info.Present == 0) { why = $"{avail} bytes after the header, level 0 needs {LevelSize(info.W, info.H, info.Channels)}"; return false; }
        if (info.Faces == 6 && avail < 5 * info.FaceStride + info.Level0) { why = $"{avail} bytes after the header, six faces need {5 * info.FaceStride + info.Level0}"; return false; }
        return true;
    }

    /// <summary>Largest side accepted: GL 4.6's minimum MAX_TEXTURE_SIZE is 16384, and nothing shipped is near it.</summary>
    private const int MaxSide = 16384;

    /// <summary>Copy the chain into <paramref name="dst"/> (<see cref="Info.ChainBytes"/> long). A partial
    /// chain's missing levels are filled with the last present level's first block, so the engine's unbounded
    /// geometric walk reads memory we own and a far mip reads roughly the texture's colour.</summary>
    internal static void CopyChain(ReadOnlySpan<byte> raw, in Info info, Span<byte> dst)
    {
        int bb = BlockBytes(info.Channels);
        long off = 0, lastBlock = 0;
        int lv = 0;
        for (int w = info.W, h = info.H; lv < info.Levels; lv++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
        {
            int sz = LevelSize(w, h, info.Channels);
            if (lv < info.Present)
            {
                raw.Slice(info.DataOffset + (int)off, sz).CopyTo(dst.Slice((int)off, sz));
                lastBlock = off;
            }
            else
                for (int b = 0; b < sz; b += bb) dst.Slice((int)lastBlock, bb).CopyTo(dst.Slice((int)off + b, bb));
            off += sz;
        }
    }
}
