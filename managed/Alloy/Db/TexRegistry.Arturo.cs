using System;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The <c>.txi</c> procedural ripple's words. <c>proceduretype arturo</c> gives a texture an
/// <c>ArturoTextureController</c>, whose <c>Control</c> @0x5d83d0 warps the image by a wave field; the warp itself is
/// not ported to Vulkan, so such a texture draws its source image and the count below says how many do.
/// The words are still read: a declared controller is what sends the texture down the uncompressed path.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>The arturo words off one <c>.txi</c>. Defaults are the two constructors':
    /// <c>ArturoTextureController</c> @0x5d8960 writes the 15x15 grid, <c>TextureController</c> @0x5d70f0
    /// writes <c>distortangle</c> 1, <c>distortionamplitude</c> 5 and <c>speed</c> 1.</summary>
    internal struct ArturoTxi
    {
        public bool On;
        public int GridW, GridH, Distort, Angle;
        public float Amplitude, Speed;
    }

    /// <summary>The grid edge both <c>arturowidth</c> and <c>arturoheight</c> default to.</summary>
    private const int ArturoGrid = 15;

    /// <summary>How many textures asked for a ripple and draw unwarped, and how many asked flat.</summary>
    private sealed class ArturoState { public int Unwarped, Flat; }

    private static readonly ArturoState _artState = new();

    /// <summary>The arturo words, line by line as <c>CAurTextureBasic::ParseField</c> @0x5881e0 feeds them:
    /// a <c>proceduretype</c> line replaces the controller, so the words before it are lost and a later
    /// <c>proceduretype</c> of another kind takes the ripple away.</summary>
    internal static void ParseArturo(string? txi, out ArturoTxi a)
    {
        a = Defaults();
        if (txi == null) return;
        foreach (string raw in txi.Split('\n'))
        {
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            int sp = line.IndexOfAny(' ', '\t');
            if (sp < 0) continue;
            ReadOnlySpan<char> key = line[..sp], val = line[(sp + 1)..].Trim();
            int vsp = val.IndexOfAny(' ', '\t');
            if (vsp >= 0) val = val[..vsp];
            if (key.Equals("proceduretype", StringComparison.OrdinalIgnoreCase))
            { a = Defaults(); a.On = val.Equals("arturo", StringComparison.Ordinal); }
            else if (!a.On) continue;
            else if (key.Equals("arturowidth", StringComparison.OrdinalIgnoreCase)) Int(val, ref a.GridW);
            else if (key.Equals("arturoheight", StringComparison.OrdinalIgnoreCase)) Int(val, ref a.GridH);
            else if (key.Equals("distort", StringComparison.OrdinalIgnoreCase)) Int(val, ref a.Distort);
            else if (key.Equals("distortangle", StringComparison.OrdinalIgnoreCase)) Int(val, ref a.Angle);
            else if (key.Equals("distortionamplitude", StringComparison.OrdinalIgnoreCase)) Flt(val, ref a.Amplitude);
            else if (key.Equals("speed", StringComparison.OrdinalIgnoreCase)) Flt(val, ref a.Speed);
        }

        static ArturoTxi Defaults() => new() { GridW = ArturoGrid, GridH = ArturoGrid, Angle = 1, Amplitude = 5f, Speed = 1f };
        static void Int(ReadOnlySpan<char> v, ref int f) { if (int.TryParse(v, out int n)) f = n; }
        static void Flt(ReadOnlySpan<char> v, ref float f)
        {
            if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n)) f = n;
        }
    }

    /// <summary>Does this <c>.txi</c> put a controller on the texture? <c>AurGetImageWrapper</c> @0x584cc0
    /// reaches for the compressed container only when the texture has neither a controller nor a grid, so
    /// a declared one is what makes the engine load the uncompressed image instead.</summary>
    private static bool TxiIsProcedural(in ArturoTxi a, int gridX, int gridY) => a.On || gridX * gridY > 1;

    /// <summary>BRACKET, after the upload: count the ripple this texture does not get.</summary>
    private static void FileArturo(Job j)
    {
        if (!j.Arturo.On || j.Tail) return;
        // Control returns on all three sub-steps before the height rows are written when there is no
        // `distort`, so vanilla has no ripple here either.
        if (j.Arturo.Distort == 0) _artState.Flat++;
        else _artState.Unwarped++;
    }

    /// <summary>Off-device. Oracles: the shipped <c>tcn01_water01.txi</c> text and the two constructors'
    /// defaults, as hand-written literals.</summary>
    internal static bool ArturoSelfCheck(Checker check)
    {
        // Verbatim from data/xp2_tiles.bif, with no `distortangle` line -- so the default must survive it.
        ParseArturo("bumpyshinytexture ttr01__env\nbumpmaptexture shinywater\n\nproceduretype arturo\n"
                  + "distort 1\narturowidth 32\narturoheight 32\ndistortionamplitude 6\nspeed 20\n"
                  + "defaultheight 64\ndefaultwidth 64\n\nalphamean 0.999\n", out ArturoTxi a);
        check.Expect(a.On && a.GridW == 32 && a.GridH == 32 && a.Distort == 1 && a.Angle == 1
                     && a.Amplitude == 6f && a.Speed == 20f,
                     "the shipped water .txi reads as a 32x32 angled ripple at amplitude 6, speed 20");
        ParseArturo("proceduretype arturo\n", out a);
        check.Expect(a.On && a.GridW == 15 && a.GridH == 15 && a.Distort == 0 && a.Angle == 1
                     && a.Amplitude == 5f && a.Speed == 1f, "a bare arturo line takes the controllers' own defaults");
        ParseArturo("speed 9\nproceduretype arturo\narturowidth 4\nproceduretype cycle\narturoheight 7\n", out a);
        check.Expect(!a.On && a.GridH == 15, "a later proceduretype takes the ripple away");
        ParseArturo("arturowidth 4\nproceduretype arturo\n", out a);
        check.Expect(a.GridW == 15, "a word before the proceduretype line reaches the controller");

        // The term that sends a texture down the uncompressed path. Every stock arturo water ships a BCn DDS
        // beside its TGA, which is what the engine loads instead once no controller is declared.
        ParseArturo("proceduretype arturo\ndistort 1\n", out a);
        check.Expect(TxiIsProcedural(a, 1, 1), "a declared ripple takes the uncompressed container");
        ParseArturo("envmaptexture tm_coinbase01\n", out a);
        check.Expect(!TxiIsProcedural(a, 1, 1) && TxiIsProcedural(a, 4, 4),
                     "a plain texture races for its container, a flipbook grid does not");
        return check.Ok;
    }

    /// <summary>The ripple clause of the registry's note.</summary>
    private static string ArturoNote()
        => (_artState.Unwarped > 0 ? $", {_artState.Unwarped} ARTURO TEXTURE(S) NOT ANIMATED (the ripple warp is not ported to Vulkan)" : "")
         + (_artState.Flat > 0 ? $", {_artState.Flat} arturo without distort (vanilla draws those flat too)" : "");
}
