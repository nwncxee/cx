using System;
using System.IO;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The GUI's half of the whole-frame A/B (<c>/alloy vkab</c>, which arms this beside <see cref="VkParity"/>): one flush
/// drawn by both renderers over the same cleared background -- Vulkan into a scratch image of the composite's shape, GL
/// into its own present texture -- then the two read back and compared texel for texel. The frame it runs on shows the GUI
/// over black, which is the price of comparing the GUI's own output rather than the world's underneath it.
/// </summary>
internal static unsafe class GuiParity
{
    /// <summary>One A/B: armed by the button, and Vulkan's texels until GL's arrive.</summary>
    private sealed class ParityState
    {
        public bool Armed;
        public string Seq = "";
        public ExtenderLog? Log;
        public VkBackend? Dev;
        public ImageId Scratch;
        public int W, H;
        public uint[]? Vk;
    }

    private static ParityState _s = new();

    /// <summary>A channel matches within this many byte steps absolute, plus <see cref="Rel"/> of the larger of the
    /// two: one step is the floor of an 8-bit composite, and a blend rounded the other way differs by exactly that.</summary>
    private const float Abs = 1.5f, Rel = 0.02f;

    /// <summary>Channels per texel compared, and words per texel on each side: GL reads one packed RGBA8 word, the
    /// Vulkan copy stage two half pairs.</summary>
    private const int Channels = 4, GlWords = 1, VkWords = 2;

    /// <summary>Is an A/B waiting, or running on this flush? The GL half reads this to leave CX's windows out.</summary>
    internal static bool Active => _s.Armed;

    /// <summary>Is this the flush the A/B runs on? The frame's closing flush, the one that carries the windows:
    /// it holds the panels, NUI and the overlay views, where an earlier flush would compare a corner.</summary>
    internal static bool Takes(bool windows) => _s.Armed && windows;

    /// <summary>The next GUI flush is an A/B; <paramref name="seq"/> names the set it writes beside
    /// <see cref="FrameTap"/>'s.</summary>
    internal static void Arm(ExtenderLog? log, string seq)
    {
        _s.Log = log;
        _s.Seq = seq;
        _s.Armed = Renderer.OnVulkan;
        _s.Vk = null;
        if (Renderer.OnVulkan)
            log?.Always("[vkabgui] armed -- the next GUI flush draws on both renderers over a cleared background; "
                      + "CX's own windows sit out of both halves");
    }

    /// <summary>Vulkan's half, from inside the 4.6 bracket before GL's flush: the GUI into a scratch image over
    /// opaque black, read back, and GL's present texture cleared to the same black so GL's half starts where Vulkan's did.
    /// A refusal disarms (logged): GL then draws the flush alone and nothing is reported.</summary>
    internal static void DrawVulkan(int from, int mid, int to, ExtenderLog? log)
    {
        ParityState s = _s;
        if (!s.Armed || log == null) return;
        s.Log = log;
        int w = PresentTarget.Width, h = PresentTarget.Height;
        if (AlloyDevice.Vulkan(log) is not VkBackend vk || !vk.OnOwnThread || w <= 0 || h <= 0)
        {
            Refuse("there is no Vulkan device on this thread, or no composite image");
            return;
        }
        if (s.Dev != vk) { _s = s = new ParityState { Log = log, Seq = s.Seq, Armed = s.Armed, Dev = vk }; }
        if (s.Scratch.IsNull || s.W != w || s.H != h)
        {
            vk.Destroy(s.Scratch);
            s.Scratch = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, w, h, 1, 1, 1, 1,
                                                     ImageUse.ColorTarget | ImageUse.Sampled, "vk gui a/b scratch"));
            s.W = w; s.H = h;
        }
        if (s.Scratch.IsNull) { Refuse("the scratch image was refused"); return; }
        if (!GuiPassVk.Flush(from, mid, to, false, log, s.Scratch, true)) { Refuse("the Vulkan GUI flush did not record"); return; }
        if (!VkParity.ReadColour(vk, s.Scratch, w, h, out uint[] words)) { Refuse("the Vulkan scratch did not read back"); return; }
        s.Vk = words;
        PresentTarget.ClearBlack();
    }

    /// <summary>GL's half after its flush, from inside the 4.6 bracket: its present texture read back, the two compared and
    /// the line logged. A no-op on every flush but the A/B's.</summary>
    internal static void ReadGlAndReport(ExtenderLog? log)
    {
        ParityState s = _s;
        if (!s.Armed || s.Vk == null) return;   // a flush the A/B did not take leaves it armed for the closing one
        s.Armed = false;
        if (log == null) return;
        int w = s.W, h = s.H;
        uint[]? gl = GlParity.Read(PresentTarget.Tex, Gl.RGBA, Gl.UNSIGNED_BYTE, 0, 0, w, h, GlWords);
        if (gl == null || PresentTarget.Width != w || PresentTarget.Height != h)
        {
            log.Always($"[vkabgui] REFUSED -- the composite did not read back at {w}x{h}");
            s.Vk = null;
            return;
        }
        Counts n = Compare(gl, s.Vk, w, h);
        string set = $"{s.Seq}_vkabgui";
        try
        {
            Directory.CreateDirectory(FrameTap.Dir);
            Write(Path.Combine(FrameTap.Dir, $"{set}_gl.ppm"), gl, w, h, GlWords);
            Write(Path.Combine(FrameTap.Dir, $"{set}_vk.ppm"), s.Vk, w, h, VkWords);
        }
        catch (IOException e) { log.Always($"[vkabgui] the images were not written -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { log.Always($"[vkabgui] the images were not written -- {e.Message}"); }
        log.Always($"[vkabgui] {set} {w}x{h}: {Report(n)}; images {FrameTap.Dir}/{set}_{{gl,vk}}.ppm; {GuiPassVk.Stats()}");
        s.Vk = null;
    }

    private static void Refuse(string why)
    {
        _s.Armed = false;
        _s.Vk = null;
        _s.Log?.Always($"[vkabgui] REFUSED -- {why}");
    }

    /// <summary>What differed, in texels: covered on one side only (one drew and the other left the background), and
    /// off past the tolerance where both drew.</summary>
    internal readonly record struct Counts(long Texels, long GlOnly, long VkOnly, long Off, long Worst);

    /// <summary>The counts as the log line says them, each as a share of the texels.</summary>
    internal static string Report(Counts n)
    {
        string Pct(long k) => n.Texels == 0 ? "0" : (100.0 * k / n.Texels).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";
        return $"GUI on GL only {Pct(n.GlOnly)}, on Vulkan only {Pct(n.VkOnly)}, colour off {Pct(n.Off)}, "
             + $"worst channel {n.Worst} of 255";
    }

    /// <summary>Compare GL's packed RGBA8 against Vulkan's half pairs over <paramref name="w"/> x
    /// <paramref name="h"/> texels. Both sides drew over opaque black, so a texel one side left black and the other
    /// painted counts as covered on that side alone.</summary>
    internal static Counts Compare(uint[] gl, uint[] vk, int w, int h)
    {
        long texels = (long)w * h, glOnly = 0, vkOnly = 0, off = 0, worst = 0;
        for (long i = 0; i < texels; i++)
        {
            int ga = 0, va = 0, gap = 0;
            bool apart = false;
            for (int k = 0; k < Channels; k++)
            {
                int a = (int)(gl[i * GlWords] >> (k * 8)) & 0xFF;
                int b = Byte(vk, i, k);
                if (k < 3) { ga += a; va += b; }
                int d = Math.Abs(a - b);
                if (d > gap) gap = d;
                apart |= d > Abs + Rel * Math.Max(a, b);
            }
            bool drewGl = ga != 0, drewVk = va != 0;
            if (drewGl != drewVk) { if (drewGl) glOnly++; else vkOnly++; continue; }
            if (apart) off++;
            if (gap > worst) worst = gap;
        }
        return new Counts(texels, glOnly, vkOnly, off, worst);
    }

    /// <summary>Channel <paramref name="k"/> of Vulkan texel <paramref name="i"/> as a byte: the copy stage packed it
    /// as a half of the 0..1 texel the sampler read.</summary>
    private static int Byte(uint[] vk, long i, int k)
    {
        uint word = vk[i * VkWords + (k >> 1)];
        float v = (float)BitConverter.UInt16BitsToHalf((ushort)(word >> ((k & 1) * 16)));
        return (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
    }

    /// <summary>One side as an 8-bit <c>.ppm</c>, top row first, so it reads the right way up.</summary>
    private static void Write(string path, uint[] side, int w, int h, int words)
    {
        using var f = File.Create(path);
        f.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"));
        var row = new byte[w * 3];
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x;
                for (int k = 0; k < 3; k++)
                    row[x * 3 + k] = (byte)(words == GlWords ? (int)(side[i] >> (k * 8)) & 0xFF : Byte(side, i, k));
            }
            f.Write(row);
        }
    }

    /// <summary>The comparator over a 2x2 frame. Oracle: hand-built sides with one known difference per texel and the
    /// counts worked out by hand -- a texel GL alone painted, one off by a quarter, and one off by a single step,
    /// which is inside the tolerance.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GuiParity.SelfCheck");
        static uint Rgba(int r, int g, int b, int a) => (uint)(r | g << 8 | b << 16 | a << 24);
        static void Put(uint[] v, long i, int r, int g, int b, int a)
        {
            v[i * VkWords] = BitConverter.HalfToUInt16Bits((Half)(r / 255f)) | (uint)BitConverter.HalfToUInt16Bits((Half)(g / 255f)) << 16;
            v[i * VkWords + 1] = BitConverter.HalfToUInt16Bits((Half)(b / 255f)) | (uint)BitConverter.HalfToUInt16Bits((Half)(a / 255f)) << 16;
        }
        uint[] gl = [Rgba(40, 40, 40, 255), Rgba(200, 100, 50, 255), Rgba(80, 80, 80, 255), Rgba(0, 0, 0, 255)];
        var vk = new uint[4 * VkWords];
        Put(vk, 0, 0, 0, 0, 255);           // texel 0: Vulkan left the background, GL painted it
        Put(vk, 1, 200, 36, 50, 255);       // texel 1: green 100 against 36, far past a step and 2%
        Put(vk, 2, 81, 80, 80, 255);        // texel 2: red off by one step, inside the tolerance
        Put(vk, 3, 0, 0, 0, 255);           // texel 3: both left the background
        Counts n = Compare(gl, vk, 2, 2);
        check.Expect(n.Texels == 4 && n.GlOnly == 1 && n.VkOnly == 0,
            $"one texel GL painted alone, none Vulkan's alone (got {n})");
        check.Expect(n.Off == 1, $"one texel off past the tolerance, the single step inside it (got {n})");
        check.Expect(n.Worst == 64, $"the worst channel gap is green's 100 against 36 (got {n.Worst})");
        log.Info(check.Ok ? "GuiParity.SelfCheck: passed" : "GuiParity.SelfCheck: FAILED");
    }
}
