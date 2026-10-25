using CX.Framework;
using CX.Rhi;

namespace CX.Graphics.Rhi;

/// <summary>The seam's <see cref="Format"/> as GL names it: the internal format storage is made in, and the
/// (format, type) pair an upload of it is described by.</summary>
internal static class GlFormats
{
    /// <summary>One format's GL triple; <see cref="Internal"/> 0 is a format GL has no row for.</summary>
    internal readonly record struct Row(uint Internal, uint Upload, uint Type);

    internal static Row Of(Format f) => f switch
    {
        Format.R8 => new(Gl.R8, Gl.RED, Gl.UNSIGNED_BYTE),
        Format.Rg8 => new(Gl.RG8, Gl.RG, Gl.UNSIGNED_BYTE),
        Format.Rgba8 => new(Gl.RGBA8, Gl.RGBA, Gl.UNSIGNED_BYTE),
        Format.Rgba8Srgb => new(Gl.SRGB8_ALPHA8, Gl.RGBA, Gl.UNSIGNED_BYTE),
        Format.R16F => new(Gl.R16F, Gl.RED, Gl.HALF_FLOAT),
        Format.Rg16F => new(Gl.RG16F, Gl.RG, Gl.HALF_FLOAT),
        Format.Rgba16F => new(Gl.RGBA16F, Gl.RGBA, Gl.HALF_FLOAT),
        Format.R32F => new(Gl.R32F, Gl.RED, Gl.FLOAT),
        Format.Rg32F => new(Gl.RG32F, Gl.RG, Gl.FLOAT),
        Format.Rgba32F => new(Gl.RGBA32F, Gl.RGBA, Gl.FLOAT),
        Format.R32Ui => new(Gl.R32UI, Gl.RED_INTEGER, Gl.UNSIGNED_INT),
        Format.Rgba32Ui => new(Gl.RGBA32UI, Gl.RGBA_INTEGER, Gl.UNSIGNED_INT),
        Format.R11G11B10F => new(Gl.R11F_G11F_B10F, Gl.RGB, Gl.UNSIGNED_INT_10F_11F_11F_REV),
        Format.D16 => new(Gl.DEPTH_COMPONENT16, Gl.DEPTH_COMPONENT, Gl.UNSIGNED_SHORT),
        Format.D24 => new(Gl.DEPTH_COMPONENT24, Gl.DEPTH_COMPONENT, Gl.UNSIGNED_INT),
        Format.D24S8 => new(Gl.DEPTH24_STENCIL8, Gl.DEPTH_STENCIL, Gl.UNSIGNED_INT_24_8),
        Format.D32F => new(Gl.DEPTH_COMPONENT32F, Gl.DEPTH_COMPONENT, Gl.FLOAT),
        Format.D32FS8 => new(Gl.DEPTH32F_STENCIL8, Gl.DEPTH_STENCIL, Gl.FLOAT_32_UNSIGNED_INT_24_8_REV),
        _ => default,
    };

    /// <summary>The seam format whose GL internal format is <paramref name="internalFormat"/>, or Unknown.</summary>
    internal static Format FromInternal(uint internalFormat)
    {
        for (Format f = Format.Unknown + 1; f <= Format.D32FS8; f++)
            if (Of(f).Internal == internalFormat) return f;
        return Format.Unknown;
    }

    /// <summary>Oracle: the token values glcorearb.h assigns, transcribed by hand -- independent of the constants
    /// <see cref="Gl"/> declares, so a mistyped constant or a row pointing at the wrong one both fail.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        (Format F, uint I, uint U, uint T)[] spec =
        [
            (Format.R8, 0x8229, 0x1903, 0x1401), (Format.Rg8, 0x822B, 0x8227, 0x1401),
            (Format.Rgba8, 0x8058, 0x1908, 0x1401), (Format.Rgba8Srgb, 0x8C43, 0x1908, 0x1401),
            (Format.R16F, 0x822D, 0x1903, 0x140B), (Format.Rg16F, 0x822F, 0x8227, 0x140B),
            (Format.Rgba16F, 0x881A, 0x1908, 0x140B), (Format.R32F, 0x822E, 0x1903, 0x1406),
            (Format.Rg32F, 0x8230, 0x8227, 0x1406), (Format.Rgba32F, 0x8814, 0x1908, 0x1406),
            (Format.R32Ui, 0x8236, 0x8D94, 0x1405), (Format.Rgba32Ui, 0x8D70, 0x8D99, 0x1405),
            (Format.R11G11B10F, 0x8C3A, 0x1907, 0x8C3B),
            (Format.D16, 0x81A5, 0x1902, 0x1403), (Format.D24, 0x81A6, 0x1902, 0x1405),
            (Format.D24S8, 0x88F0, 0x84F9, 0x84FA), (Format.D32F, 0x8CAC, 0x1902, 0x1406),
            (Format.D32FS8, 0x8CAD, 0x84F9, 0x8DAD),
        ];
        foreach ((Format f, uint i, uint u, uint t) in spec)
        {
            Row r = Of(f);
            check.Expect(r == new Row(i, u, t),
                $"{f} is (0x{r.Internal:x}, 0x{r.Upload:x}, 0x{r.Type:x}), the GL headers say (0x{i:x}, 0x{u:x}, 0x{t:x})");
        }
        check.Expect(spec.Length == (int)Format.D32FS8, $"{spec.Length} rows checked, the seam has {(int)Format.D32FS8} formats");
        check.Expect(Of(Format.Unknown).Internal == 0, "Unknown has no GL row");
        log.Info(check.Ok ? "GlFormats.SelfCheck: passed" : "GlFormats.SelfCheck: FAILED");
    }
}
