using System;
using System.Buffers.Binary;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>A data texture skips the coverage scan and the chain rewrite in both containers, beside the same bytes
/// as colour, which are scanned and rewritten. Oracle: hand-written texels and BC3 blocks, and the file's own bytes.</summary>
internal static unsafe partial class TexRegistry
{
    private const int DdsSide = 8, DdsLevels = 4, Bc3Block = 16;
    /// <summary>An 8x8 BC3 chain is 4 + 1 + 1 + 1 blocks.</summary>
    private const int DdsChainBytes = 7 * Bc3Block;
    /// <summary>Alpha indices 001 for all 16 texels: every texel takes a1, which is 0.</summary>
    private static ReadOnlySpan<byte> AllHoleIndices => [73, 146, 36, 73, 146, 36];

    internal static bool DataTextureSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.DataTexture.SelfCheck");
        DataTga(check);
        DataDds(check);
        log.Info(check.Ok ? "TexRegistry.DataTexture.SelfCheck: passed" : "TexRegistry.DataTexture.SelfCheck: FAILED");
        return check.Ok;
    }

    private static void DataTga(Checker check)
    {
        byte[] tga = Tga(Profile(), ProfileW, ProfileH);
        Job colour = SkyDecode(null, tga), data = SkyDecode(DataTxi, tga);
        try
        {
            check.Expect(colour.Cov is TexCoverage.Cutout or TexCoverage.Blended && colour.Chained,
                         $"TGA: the profile's texels as colour should scan as cutting and rebuild their chain ({colour.Cov}, chained {colour.Chained})");
            check.Expect(data.Cov == TexCoverage.Opaque && !data.Chained,
                         $"TGA: a data texture read {data.Cov}, chained {data.Chained}; it should answer Opaque unscanned and keep its bytes");
        }
        finally { FreeJob(colour); FreeJob(data); }
    }

    private static void DataDds(Checker check)
    {
        byte[] dds = HolesDds();
        ReadOnlySpan<byte> stored = dds.AsSpan(DdsReader.LegacyHeader, DdsChainBytes);
        Job colour = DdsDecode(null, dds), data = DdsDecode(DataTxi, dds);
        try
        {
            check.Expect(colour.Why == null && data.Why == null, $"DDS: the fixture did not decode ({colour.Why ?? data.Why})");
            if (colour.Data == null || data.Data == null) return;
            check.Expect(colour.Cov == TexCoverage.Cutout && !Level(colour, 0, DdsChainBytes).SequenceEqual(stored),
                         $"DDS: the blocks as colour should scan Cutout and have their mips rewritten ({colour.Cov})");
            check.Expect(data.Cov == TexCoverage.Opaque, $"DDS: a data texture read {data.Cov}; it should answer Opaque unscanned");
            check.Expect(Level(data, 0, DdsChainBytes).SequenceEqual(stored),
                         $"DDS: a data texture's chain differs from the file at byte {FirstDiff(Level(data, 0, DdsChainBytes), stored)}");
        }
        finally { FreeDds(colour); FreeDds(data); }
    }

    /// <summary>A legacy 8x8 BC3 file, laid out by hand: level 0's left blocks opaque blue, its right blocks all holes in
    /// red, and three grey opaque mips that a rewrite from level 0 cannot reproduce.</summary>
    private static byte[] HolesDds()
    {
        const int ChannelsAt = 8, Level0At = 12, AlphaMeanAt = 16, ColourAt = 8;
        const ushort Blue = 31, Red = 63488, Grey = 33808;
        var file = new byte[DdsReader.LegacyHeader + DdsChainBytes];
        BinaryPrimitives.WriteInt32LittleEndian(file, DdsSide);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), DdsSide);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(ChannelsAt), 4);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(Level0At), 4 * Bc3Block);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(AlphaMeanAt), 0.5f);
        for (int b = 0; b < DdsChainBytes / Bc3Block; b++)
        {
            Span<byte> block = file.AsSpan(DdsReader.LegacyHeader + b * Bc3Block, Bc3Block);
            bool hole = b is 1 or 3;
            block[0] = 255;
            block[1] = hole ? (byte)0 : (byte)255;
            if (hole) AllHoleIndices.CopyTo(block[2..]);
            ushort c = b >= 4 ? Grey : hole ? Red : Blue;
            BinaryPrimitives.WriteUInt16LittleEndian(block[ColourAt..], c);
            BinaryPrimitives.WriteUInt16LittleEndian(block[(ColourAt + 2)..], c);
        }
        return file;
    }

    /// <summary>The file decoded as the worker decodes a DDS a cutting material asks for, after its <c>.txi</c> words.</summary>
    private static Job DdsDecode(string? txi, byte[] dds)
    {
        var j = new Job { Kind = TexKind.Plain, Cuts = true, Loc = new TexLocation(KindLoose, 0, "", 0, -1, dds.Length, false, DdsResType) };
        byte blend = _txiBlend[0];
        bool decal = _txiDecal[0];
        try { TxiMaterial(0, txi, j); }
        finally { _txiBlend[0] = blend; _txiDecal[0] = decal; }
        Decode(j, dds);
        return j;
    }

    private static void FreeDds(Job j)
    {
        if (j.Data == null) return;
        DdsReader.FreePrepared(j.Data);
        j.Data = null;
    }
}
