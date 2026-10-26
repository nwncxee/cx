using System;
using System.IO;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The whole-frame A/B under <c>alloy.backend vk</c> (<c>/alloy vkab</c>): one frame drawn by both renderers from the
/// same staged tables and unjittered camera -- Vulkan, and GL Alloy running only the passes Vulkan has, with every term
/// Vulkan stands in for stood down the same way -- then the depth, the G-buffer lanes and the scene colour read back from
/// each and compared texel for texel. A port shrinks <see cref="GlPass"/>'s refusals and <see cref="StoodDown"/>.
/// </summary>
internal static unsafe class VkParity
{
    /// <summary>One side's read-back lanes, cropped to the viewport, row 0 first on both (the two share GL's row order).
    /// Colour is two half pairs a texel, depth a float's bits, G0 and G1 four words, G2 one.</summary>
    internal sealed class Lanes
    {
        public uint[] Colour = [], Depth = [], G0 = [], G1 = [], G2 = [];
        /// <summary>The side wrote its G-buffer this frame; otherwise no G-buffer lane is compared.</summary>
        public bool Gbuf;
        /// <summary>The visibility estimate as one float a texel, and whether the side wrote one: the AO pass's own
        /// output, so the G-buffer's transfer weights are not the only place its difference can be read.</summary>
        public uint[] Ao = [];
        public bool HasAo;
    }

    /// <summary>What differed, in texels: geometry one side drew and the other did not, depth past <see cref="DepthTol"/>
    /// where both drew, any word of a G-buffer lane (G1 and G2 where a twin drew), and colour past the tolerance.</summary>
    internal readonly record struct Counts(long Texels, long GlOnly, long VkOnly, long DepthOff, long G0Off, long G1Off,
                                           long G2Off, long ColourOff, bool Gbuf);

    /// <summary>One A/B: armed by the button, active for the one frame it runs, and the Vulkan side's lanes until GL's.</summary>
    private sealed class ParityState
    {
        public bool Armed, Active;
        public string Seq = "";
        public ExtenderLog? Log;
        public VkBackend? Dev;
        public CommandList? Cmds;
        public PipelineId Float1, Float2, Uint1, Uint4;
        public BufferId Out;
        public long OutCap;
        public Lanes? Vk;
        public int W, H;
    }

    private static ParityState _s = new();

    private const int ColourWords = 2, DepthWords = 1, G01Words = 4, G2Words = 1, AoWords = 1;
    /// <summary>A visibility texel is an R8 UNORM, so a step is this much: a difference under one step is the two
    /// targets rounding the same estimate apart, and the mean is what says whether anything else is going on.</summary>
    private const float AoStep = 1f / 255f;
    /// <summary>The copy stage's local size, as <c>cx_parity_c.glsl</c> declares it.</summary>
    private const int Group = 8;
    /// <summary>Window depth two invariant clip expressions on one GPU may still differ by: two compilers contract
    /// differently. A thousandth of a percent of the range.</summary>
    internal const float DepthTol = 1e-5f;
    /// <summary>A colour channel matches within this much absolute, plus <see cref="ColourRel"/> of its size.</summary>
    internal const float ColourAbs = 2e-3f, ColourRel = 0.02f;
    /// <summary>The diff image's codes, worst first: geometry on one side only, depth, a G-buffer word, colour.</summary>
    internal const byte MaskSame = 0, MaskCoverage = 1, MaskDepth = 2, MaskGbuf = 3, MaskColour = 4;
    /// <summary>The colour each mask code is drawn in: black, red, orange, yellow, blue.</summary>
    private static readonly byte[] MaskRgb = [0, 0, 0, 255, 0, 0, 255, 128, 0, 255, 255, 0, 0, 96, 255];

    /// <summary>The colour-bracket passes GL runs in the A/B frame: the ones on Vulkan.</summary>
    internal static bool GlPass(PassId id)
        => id is PassId.DepthPrepass or PassId.AmbientOcclusion or PassId.FogVolume or PassId.OpaqueOwned or PassId.SkyOwned
               or PassId.CutoutOwned or PassId.DeferredLights or PassId.BlendedOwned or PassId.FbCopy
               or PassId.WaterOwned or PassId.AdditiveOwned or PassId.BeamsOwned
               or PassId.ParticlesOwned or PassId.HalosOwned
               or PassId.WeatherOwned;

    /// <summary>The shadow-bracket passes Vulkan bakes rather than GL -- <see cref="GlPass"/>'s counterpart for the
    /// bracket the A/B does not compare lane by lane.</summary>
    internal static bool VkShadow(PassId id) => id is PassId.SunCascades or PassId.LocalLightCubes;

    /// <summary>The lit stage's loose terms whose resources are not on Vulkan: Vulkan binds them at zero. GL's A/B frame
    /// reaches the same zero at each term's own source, keyed on <see cref="Active"/>.</summary>
    internal static readonly string[] StoodDown =
        ["uLmPages", "uSkyTerm", "uWeather", "uLightRecOn", "uCapture", "uOit", "uSplash"];

    /// <summary>The pass block's receivers with no map on Vulkan, switched off in a staged block: no probe. The sun
    /// cascades stand: the A/B frame bakes on both backends, so both sides read the same count over their own maps.</summary>
    internal static void StandDownPass(Span<byte> block)
    {
        MemoryMarshal.Write(block[AlloyRenderer.PbProbeMips..], 0f);
    }

    /// <summary>This frame is the A/B: GL draws its Vulkan passes beside Vulkan's, and the two are compared.</summary>
    internal static bool Active => _s.Active;

    /// <summary>Give this frame's A/B up before anything draws, saying why: a comparison one side cannot make is
    /// worth less than no comparison. The next <c>/alloy vkab</c> arms another.</summary>
    internal static void Refuse(string why)
    {
        if (!_s.Active) return;
        _s.Active = false;
        _s.Vk = null;
        _s.Log?.Always($"[vkab] REFUSED -- {why}");
    }

    internal static void Attach(ExtenderLog log) => _s.Log = log;

    /// <summary>The button: the next Vulkan frame is an A/B.</summary>
    internal static void Arm()
    {
        _s.Seq = DateTime.Now.ToString("HHmmss");
        _s.Armed = Renderer.OnVulkan;
        _s.Log?.Always(Renderer.OnVulkan ? $"[vkab] armed -- the next frame draws on both renderers; {FrameTap.Dir}/{_s.Seq}_vkab_*"
                                         : "[vkab] REFUSED -- the A/B compares the Vulkan frame, and alloy.backend is not vk");
        GuiParity.Arm(_s.Log, _s.Seq);
    }

    /// <summary>At the frame's head: an armed A/B becomes this frame's, once. The SHADOW bracket calls this
    /// first, ahead of the colour build, so the bake knows it is an A/B frame and runs on both backends; the
    /// colour build's own call is then the no-op that keeps a frame with no bracket working as before.</summary>
    internal static void BeginFrame(bool capturing)
    {
        var s = _s;
        if (s.Active) return;
        s.Active = s.Armed && Renderer.OnVulkan && !capturing;
        if (s.Active) s.Armed = false;
        s.Vk = null;
    }

    /// <summary>GL's A/B bind of a lit program: every shared block point a Vulkan bind leaves on its zero stand-in, pointed
    /// at zeros -- the light rig, the probe's SH, the shared scratch -- so GL reads the same "nothing here".</summary>
    internal static void GlStandDown()
    {
        if (_s.Active) GlParity.ZeroBlocks(SsboSlots.Shared, SsboSlots.Content, UboSlots.ProbeSh, UboSlots.Budget, UboSlots.Engine);
    }

    /// <summary>The Vulkan frame's targets, after its submit: each lane copied into words and read back. False (logged)
    /// when the device refused it: the frame stays stood down and reports nothing.</summary>
    internal static bool ReadVulkan(VkBackend vk, ImageId hdr, ImageId depth, ImageId g0, ImageId g1, ImageId g2, bool gbuf,
                                    ImageId ao, bool hasAo, int w, int h)
    {
        if (!_s.Active) return true;
        bool built = Ensure(vk);
        var s = _s;
        // a refusal leaves Active up: the GL half already stood its terms down for this frame, and ReadGlAndReport,
        // finding no Vulkan lanes, reports nothing
        if (!built || s.Log == null)
        {
            s.Log?.Always("[vkab] REFUSED -- the Vulkan copy stages did not build (logged above)");
            return false;
        }
        var l = new Lanes { Gbuf = gbuf, HasAo = hasAo && !ao.IsNull };
        bool ok = Copy(vk, hdr, s.Float2, ColourWords, w, h, out l.Colour) && Copy(vk, depth, s.Float1, DepthWords, w, h, out l.Depth)
                  && (!gbuf || Copy(vk, g0, s.Uint4, G01Words, w, h, out l.G0) && Copy(vk, g1, s.Uint4, G01Words, w, h, out l.G1)
                               && Copy(vk, g2, s.Uint1, G2Words, w, h, out l.G2))
                  && (!l.HasAo || Copy(vk, ao, s.Float1, AoWords, w, h, out l.Ao));
        if (!ok)
        {
            s.Log.Always("[vkab] REFUSED -- a Vulkan lane did not read back (logged above)");
            return false;
        }
        s.Vk = l; s.W = w; s.H = h;
        return true;
    }

    /// <summary>One colour image read back as two half pairs a texel, through this A/B's own copy stage -- the GUI's
    /// sibling A/B shares it rather than building a second. False (logged) when the device refused a step.</summary>
    internal static bool ReadColour(VkBackend vk, ImageId img, int w, int h, out uint[] words)
    {
        words = [];
        return Ensure(vk) && Copy(vk, img, _s.Float2, ColourWords, w, h, out words);
    }

    /// <summary>GL's targets after its A/B passes, then the comparison: logged, with GL's and Vulkan's colour and the diff
    /// mask written beside <see cref="FrameTap"/>'s sets.</summary>
    internal static void ReadGlAndReport(string issued, int* viewport, uint hdr, uint depth, uint g0, uint g1, uint g2, bool gbuf,
                                         uint ao, bool hasAo)
    {
        var s = _s;
        if (!s.Active) return;
        s.Active = false;
        if (s.Vk == null || s.Log == null) return;
        int w = s.W, h = s.H;
        if (viewport[2] != w || viewport[3] != h)
        {
            s.Log.Always($"[vkab] REFUSED -- GL drew {viewport[2]}x{viewport[3]} and Vulkan {w}x{h}");
            return;
        }
        int x = viewport[0], y = viewport[1];
        var gl = new Lanes { Gbuf = gbuf };
        uint[]? c = GlParity.Read(hdr, Gl.RGBA, Gl.HALF_FLOAT, x, y, w, h, ColourWords);
        uint[]? d = GlParity.Read(depth, Gl.DEPTH_COMPONENT, Gl.FLOAT, x, y, w, h, DepthWords);
        if (c == null || d == null) { s.Log.Always("[vkab] REFUSED -- GL's colour or depth did not read back"); return; }
        gl.Colour = c; gl.Depth = d;
        if (gbuf)
        {
            uint[]? a = GlParity.Read(g0, Gl.RGBA_INTEGER, Gl.UNSIGNED_INT, x, y, w, h, G01Words);
            uint[]? b = GlParity.Read(g1, Gl.RGBA_INTEGER, Gl.UNSIGNED_INT, x, y, w, h, G01Words);
            uint[]? e = GlParity.Read(g2, Gl.RED_INTEGER, Gl.UNSIGNED_INT, x, y, w, h, G2Words);
            if (a == null || b == null || e == null) gl.Gbuf = false;
            else { gl.G0 = a; gl.G1 = b; gl.G2 = e; }
        }
        if (hasAo && ao != 0 && s.Vk.HasAo && GlParity.Read(ao, Gl.RED, Gl.FLOAT, x, y, w, h, AoWords) is { } v)
        { gl.Ao = v; gl.HasAo = true; }
        var mask = new byte[(long)w * h];
        Counts n = Compare(gl, s.Vk, w, h, mask);
        issued += "; " + AoNote(gl, s.Vk);
        if (n.Gbuf) issued += $"; G0 {Words(gl.G0, s.Vk.G0, gl.G0, s.Vk.G0)}; G1 {Words(gl.G1, s.Vk.G1, gl.G0, s.Vk.G0)}";
        string set = $"{s.Seq}_vkab";
        try
        {
            Directory.CreateDirectory(FrameTap.Dir);
            WriteColour(Path.Combine(FrameTap.Dir, $"{set}_gl.pfm"), gl.Colour, w, h);
            WriteColour(Path.Combine(FrameTap.Dir, $"{set}_vk.pfm"), s.Vk.Colour, w, h);
            WriteMask(Path.Combine(FrameTap.Dir, $"{set}_diff.ppm"), mask, w, h);
        }
        catch (IOException e) { s.Log.Always($"[vkab] the images were not written -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { s.Log.Always($"[vkab] the images were not written -- {e.Message}"); }
        s.Log.Always($"[vkab] {set} {w}x{h}: {Report(n)}; images {FrameTap.Dir}/{set}_{{gl,vk}}.pfm and _diff.ppm "
                   + $"(red = drawn on one side only, orange = depth, yellow = G-buffer, blue = colour); {issued}");
        s.Vk = null;
    }

    /// <summary>The two visibility estimates held against each other: the share of texels that differ at all, the share
    /// past one R8 step, and the mean and worst absolute difference. The G-buffer's transfer weights carry this term
    /// through a shared-exponent pack, where any difference rewrites every bit, so this is where its SIZE is readable.</summary>
    internal static string AoNote(Lanes gl, Lanes vk)
    {
        if (!gl.HasAo || !vk.HasAo || gl.Ao.Length != vk.Ao.Length || gl.Ao.Length == 0) return "AO not compared (a side wrote none)";
        long any = 0, wide = 0;
        double sum = 0;
        float max = 0;
        for (long i = 0; i < gl.Ao.LongLength; i++)
        {
            float d = MathF.Abs(BitConverter.UInt32BitsToSingle(gl.Ao[i]) - BitConverter.UInt32BitsToSingle(vk.Ao[i]));
            if (d > 0f) any++;
            if (d > 1.5f * AoStep) wide++;
            sum += d;
            max = MathF.Max(max, d);
        }
        double n = gl.Ao.LongLength;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return $"AO off {(100.0 * any / n).ToString("0.##", c)}% of texels ({(100.0 * wide / n).ToString("0.##", c)}% past one "
             + $"R8 step), mean |d| {(sum / n).ToString("0.#####", c)}, worst {max.ToString("0.###", c)}";
    }

    /// <summary>The counts as the log line says them: each as a share of the texels, the gaps named.</summary>
    internal static string Report(Counts n)
    {
        string Pct(long k) => n.Texels == 0 ? "0" : (100.0 * k / n.Texels).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";
        return $"geometry on GL only {Pct(n.GlOnly)}, on Vulkan only {Pct(n.VkOnly)}, depth off {Pct(n.DepthOff)}, "
             + (n.Gbuf ? $"G0 off {Pct(n.G0Off)}, G1 off {Pct(n.G1Off)}, G2 off {Pct(n.G2Off)}, " : "G-buffer not compared (a side did not write it), ")
             + $"colour off {Pct(n.ColourOff)}";
    }

    /// <summary>Compare two sides' lanes over <paramref name="w"/> x <paramref name="h"/> texels, coding each into
    /// <paramref name="mask"/> (when it is long enough) by the worst difference it shows.</summary>
    internal static Counts Compare(Lanes gl, Lanes vk, int w, int h, Span<byte> mask)
    {
        long texels = (long)w * h, glOnly = 0, vkOnly = 0, depthOff = 0, g0 = 0, g1 = 0, g2 = 0, colourOff = 0;
        bool gbuf = gl.Gbuf && vk.Gbuf;
        bool marks = mask.Length >= texels;
        for (long i = 0; i < texels; i++)
        {
            float a = BitConverter.UInt32BitsToSingle(gl.Depth[i]), b = BitConverter.UInt32BitsToSingle(vk.Depth[i]);
            bool ga = a < 1f, gb = b < 1f;
            byte code = MaskSame;
            if (ga != gb) { if (ga) glOnly++; else vkOnly++; code = MaskCoverage; }
            else if (ga && MathF.Abs(a - b) > DepthTol) { depthOff++; code = MaskDepth; }
            if (gbuf)
            {
                // G1 and G2 are the twin's alone: where neither side's G0 says a twin drew, they hold nothing
                bool twin = gl.G0[i * G01Words] != 0 || vk.G0[i * G01Words] != 0;
                bool o0 = !Same(gl.G0, vk.G0, i, G01Words), o1 = twin && !Same(gl.G1, vk.G1, i, G01Words), o2 = twin && gl.G2[i] != vk.G2[i];
                if (o0) g0++;
                if (o1) g1++;
                if (o2) g2++;
                if ((o0 || o1 || o2) && code == MaskSame) code = MaskGbuf;
            }
            if (!ColourSame(gl.Colour, vk.Colour, i)) { colourOff++; if (code == MaskSame) code = MaskColour; }
            if (marks) mask[(int)i] = code;
        }
        return new Counts(texels, glOnly, vkOnly, depthOff, g0, g1, g2, colourOff, gbuf);
    }

    /// <summary>Each of a four-word lane's words where either side's G0 is non-zero: the texels it differs at, the bits
    /// that ever differ, and the texels where some byte differs by more than one step.</summary>
    internal static string Words(uint[] a, uint[] b, uint[] ga, uint[] gb)
    {
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < G01Words; k++)
        {
            long off = 0, wide = 0;
            uint bits = 0;
            for (long i = 0; i < ga.Length / G01Words; i++)
            {
                if (ga[i * G01Words] == 0 && gb[i * G01Words] == 0) continue;
                uint x = a[i * G01Words + k], y = b[i * G01Words + k];
                if (x == y) continue;
                off++; bits |= x ^ y;
                for (int sh = 0; sh < 32; sh += 8)
                    if (Math.Abs((int)((x >> sh) & 0xFF) - (int)((y >> sh) & 0xFF)) > 1) { wide++; break; }
            }
            sb.Append($"{"xyzw"[k]} {off} (bits {bits:x8}, wide {wide}) ");
        }
        return sb.ToString().TrimEnd();
    }

    private static bool Same(uint[] a, uint[] b, long i, int words)
    {
        for (long k = i * words, e = k + words; k < e; k++) if (a[k] != b[k]) return false;
        return true;
    }

    private static bool ColourSame(uint[] a, uint[] b, long i)
    {
        for (int k = 0; k < ColourWords * 2; k++)
        {
            int word = (int)(i * ColourWords) + (k >> 1), shift = (k & 1) * 16;
            float x = (float)BitConverter.UInt16BitsToHalf((ushort)(a[word] >> shift));
            float y = (float)BitConverter.UInt16BitsToHalf((ushort)(b[word] >> shift));
            if (float.IsNaN(x) != float.IsNaN(y)) return false;
            if (MathF.Abs(x - y) > ColourAbs + ColourRel * MathF.Max(MathF.Abs(x), MathF.Abs(y))) return false;
        }
        return true;
    }

    /// <summary>The device's copy stages and the read-back buffer, made once per device. A new device replaces
    /// <see cref="_s"/>, so a caller reads it after this.</summary>
    private static bool Ensure(VkBackend vk)
    {
        var s = _s;
        if (s.Dev != null && s.Dev != vk) _s = s = new ParityState { Log = s.Log, Seq = s.Seq, Active = s.Active };
        s.Dev = vk;
        if (!s.Float1.IsNull) return !(s.Float2.IsNull || s.Uint1.IsNull || s.Uint4.IsNull);
        s.Cmds = new CommandList("vulkan a/b", 4096, s.Log!);
        s.Float1 = Copier(vk, false, DepthWords);
        s.Float2 = Copier(vk, false, ColourWords);
        s.Uint1 = Copier(vk, true, G2Words);
        s.Uint4 = Copier(vk, true, G01Words);
        return !(s.Float1.IsNull || s.Float2.IsNull || s.Uint1.IsNull || s.Uint4.IsNull);
    }

    private static PipelineId Copier(VkBackend vk, bool uint_, int words)
    {
        const string Version = "#version 460 core\n";
        string src = ShaderSource.Get("cx_parity_c.glsl").Replace(Version,
            Version + (uint_ ? "#define CX_PARITY_UINT 1\n" : "") + $"#define CX_PARITY_WORDS {words}\n");
        string label = $"VkParity copy {(uint_ ? "uint" : "float")} x{words}";
        var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, label)), Label = label };
        return vk.CreatePipeline(in d);
    }

    /// <summary>One lane: <paramref name="img"/> through <paramref name="pipe"/> into the read-back buffer, one submit,
    /// then the words into <paramref name="words"/>.</summary>
    private static bool Copy(VkBackend vk, ImageId img, PipelineId pipe, int perTexel, int w, int h, out uint[] words)
    {
        var s = _s;
        words = new uint[(long)w * h * perTexel];
        long bytes = words.LongLength * sizeof(uint);
        if (bytes > s.OutCap)
        {
            vk.Destroy(s.Out);
            s.Out = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.Storage | BufferUse.CopySrc, MemoryKind.Device, "vk a/b read-back"));
            s.OutCap = s.Out.IsNull ? 0 : bytes;
            if (s.Out.IsNull) return false;
        }
        CommandList cl = s.Cmds!;
        cl.Reset();
        cl.SetPipeline(pipe);
        cl.BindSet(0, new BindSet(default, [new BufferBinding(SsboSlots.ParityOut, s.Out)], [new ImageBinding(0, img)], default));
        cl.Dispatch((w + Group - 1) / Group, (h + Group - 1) / Group);
        return vk.Submit(cl) && vk.ReadBack(s.Out, MemoryMarshal.AsBytes(words.AsSpan()));
    }

    /// <summary>One Vulkan image read back as <paramref name="words"/> words a texel through the copy stages, for a
    /// caller outside the A/B (the frame tap). <paramref name="fmt"/> is the image's own, and decides which stage
    /// reads it -- an integer lane never goes through the float path. Null (logged) when refused.</summary>
    internal static uint[]? Read(VkBackend vk, ImageId img, Format fmt, int words, int w, int h)
    {
        if (!Ensure(vk)) return null;
        PipelineId pipe = fmt.IsInteger() ? words == 1 ? _s.Uint1 : _s.Uint4 : words == 1 ? _s.Float1 : _s.Float2;
        return Copy(vk, img, pipe, words, w, h, out uint[] read) ? read : null;
    }

    /// <summary>Colour as a float RGB <c>.pfm</c>, whose rows run bottom-up as GL's and Vulkan's both do here.</summary>
    internal static void WriteColour(string path, uint[] colour, int w, int h)
    {
        using var f = File.Create(path);
        byte[] hdr = System.Text.Encoding.ASCII.GetBytes($"PF\n{w} {h}\n-1.0\n");
        f.Write(hdr);
        var row = new float[w * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                long i = ((long)y * w + x) * ColourWords;
                row[x * 3] = (float)BitConverter.UInt16BitsToHalf((ushort)colour[i]);
                row[x * 3 + 1] = (float)BitConverter.UInt16BitsToHalf((ushort)(colour[i] >> 16));
                row[x * 3 + 2] = (float)BitConverter.UInt16BitsToHalf((ushort)colour[i + 1]);
            }
            f.Write(MemoryMarshal.AsBytes(row.AsSpan()));
        }
    }

    /// <summary>The mask as an 8-bit <c>.ppm</c>, top row first, so it reads the right way up beside the frame.</summary>
    private static void WriteMask(string path, byte[] mask, int w, int h)
    {
        using var f = File.Create(path);
        f.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"));
        var row = new byte[w * 3];
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++) Array.Copy(MaskRgb, mask[(long)y * w + x] * 3, row, x * 3, 3);
            f.Write(row);
        }
    }

    /// <summary>The comparator over a 2x2 frame. Oracle: hand-built lanes with one known difference per texel and the
    /// counts and mask codes worked out by hand -- a hole, a depth past the tolerance, a G1 word, a colour channel --
    /// and a G2 word where no twin drew, which must not count.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkParity.SelfCheck");
        static uint F(float v) => BitConverter.SingleToUInt32Bits(v);
        static uint H(float a, float b) => BitConverter.HalfToUInt16Bits((Half)a) | (uint)BitConverter.HalfToUInt16Bits((Half)b) << 16;
        Lanes Side() => new()
        {
            Gbuf = true,
            Depth = [F(0.5f), F(0.5f), F(0.5f), F(0.5f)],
            Colour = [H(1f, 0.5f), H(0.25f, 1f), H(1f, 0.5f), H(0.25f, 1f), H(1f, 0.5f), H(0.25f, 1f), H(1f, 0.5f), H(0.25f, 1f)],
            G0 = new uint[16], G1 = new uint[16], G2 = new uint[4],
        };
        Lanes gl = Side(), vk = Side();
        vk.Depth[0] = F(1f);                         // texel 0: Vulkan drew nothing there
        vk.Depth[1] = F(0.5f + 1e-3f);               // texel 1: depth well past the tolerance
        vk.Depth[2] = F(0.5f + 1e-6f);               // ...and texel 2's inside it
        vk.G1[2 * 4 + 3] = 7;                        // texel 2: one G1 word, under a twin's G0 on both sides
        gl.G0[2 * 4] = vk.G0[2 * 4] = 1;
        gl.G2[1] = 9;                                // texel 1: a G2 word no twin covered, which is not compared
        vk.Colour[3 * 2 + 1] = H(0.5f, 1f);          // texel 3: blue 0.25 against 0.5, far past 2% + 0.002
        vk.Colour[2 * 2] = H(1.01f, 0.5f);           // texel 2: red 1.0 against 1.01, inside 2% + 0.002
        var mask = new byte[4];
        Counts n = Compare(gl, vk, 2, 2, mask);
        check.Expect(n.Texels == 4 && n.GlOnly == 1 && n.VkOnly == 0 && n.DepthOff == 1,
            $"one texel GL drew alone and one depth off, the near-equal one inside the tolerance (got {n})");
        check.Expect(n.G0Off == 0 && n.G1Off == 1 && n.G2Off == 0 && n.ColourOff == 1 && n.Gbuf,
            $"one G1 word and one colour channel off, the 1% red inside the tolerance (got {n})");
        check.Expect(mask[0] == MaskCoverage && mask[1] == MaskDepth && mask[2] == MaskGbuf && mask[3] == MaskColour,
            $"the mask codes each texel by its difference: coverage, depth, G-buffer, colour (got {string.Join(",", mask)})");
        vk.Gbuf = false;
        check.Expect(Compare(gl, vk, 2, 2, default) is { G1Off: 0, Gbuf: false },
            "a side that wrote no G-buffer has none compared");
        log.Info(check.Ok ? "VkParity.SelfCheck: passed" : "VkParity.SelfCheck: FAILED");
    }
}
