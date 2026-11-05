using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy.Db;

/// <summary>The <c>.txi</c> procedural ripple's words. <c>proceduretype arturo</c> gives a texture an
/// <c>ArturoTextureController</c>, whose <c>Control</c> @0x5d83d0 warps the image by a wave field every third tick of
/// a 0.03 s clock and re-uploads level 0. Here too the warp is the CPU's: the stock images are 256 square at most,
/// and level 0 goes up through the path a flipbook cell takes, so a record's bindless handle never moves.</summary>
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

    /// <summary>The controller's clock: <c>Control</c> runs one sub-step per 0.03 s and warps on every third.</summary>
    private const float ArturoTick = 0.03f;
    /// <summary>A height byte read as a full turn, which is how the angled warp turns it into a direction.</summary>
    private const float ArturoByteToRadians = 0.024543693f;
    /// <summary>The axis-aligned warp's height scale: a byte's distance from the midpoint over 128.</summary>
    private const float ArturoHeightScale = 0.0078125f;
    /// <summary>Height values there are: a byte, so the offset tables are exact at 256 entries.</summary>
    private const int ArturoBytes = 256;
    /// <summary>Knuth's multiplicative step, spreading slot numbers into unrelated generator seeds.</summary>
    private const uint RngSeedStep = 2654435761u;
    /// <summary>The widest wave grid a <c>.txi</c> may ask for. The stock extreme is 128, and the grid is a square of
    /// floats, so this is the line past which content would be sizing the bracket's own memory.</summary>
    private const int ArturoGridMax = 512;

    /// <summary>One texture's running ripple: the controller's wave grids, the source image the warp fetches through
    /// and the level it writes. Every array is allocated once, at the file, so a step allocates nothing.</summary>
    private sealed class Arturo
    {
        public int W, H, Aw, Ah, Px, Sub;
        public float Speed, Phase, Acc;
        public uint Rng;
        public bool Mip;
        public float[] Cur = [], Prev = [], Next = [];
        /// <summary>The source texel offset per height byte, worked out once from the amplitude and the angle word.</summary>
        public readonly int[] Dx = new int[ArturoBytes], Dy = new int[ArturoBytes];
        /// <summary>The image as the Vulkan level holds it, and the level this warp writes.</summary>
        public byte[] Src = [], Out = [];

        /// <summary>The controller's own generator, standing in for the engine's <c>rand()</c>: xorshift32, seeded
        /// off the slot so two textures do not ripple in lockstep.</summary>
        public int Roll(int n)
        {
            Rng ^= Rng << 13; Rng ^= Rng >> 17; Rng ^= Rng << 5;
            return n <= 0 ? 0 : (int)(Rng >> 1) % n;
        }

        /// <summary><c>TextureController::Distort</c> @0x5d61d0's offset for every height byte: <c>distortangle</c>
        /// reads the byte as a direction and steps the amplitude along it, otherwise the byte's distance from the
        /// midpoint offsets both axes alike. Both truncate, as the controller's cast does.</summary>
        public void Offsets(bool angled, float amp)
        {
            for (int b = 0; b < ArturoBytes; b++)
            {
                float turn = b * ArturoByteToRadians, flat = (b - 127) * ArturoHeightScale * amp - 0.5f;
                Dx[b] = angled ? (int)(amp * MathF.Sin(turn) - 0.5f) : (int)flat;
                Dy[b] = angled ? (int)(amp * MathF.Cos(turn) - 0.5f) : (int)flat;
            }
        }

        /// <summary>The starting wave and the two steps <c>ArturoTextureController::Init</c> @0x5d6a70 runs over it.</summary>
        public void Seed() { Lay(); Wave(); Wave(); }

        /// <summary>The starting wave: a radial sine bump over the grid, with the previous frame flat.</summary>
        public void Lay()
        {
            for (int y = 0; y < Ah; y++)
                for (int x = 0; x < Aw; x++)
                {
                    float nx = (float)x / Aw * 2f - 1f, ny = (float)y / Ah * 2f - 1f;
                    int i = y * Aw + x;
                    Prev[i] = 0f;
                    Cur[i] = MathF.Sin((1f - MathF.Sqrt(nx * nx + ny * ny)) * MathF.PI) * 127f;
                }
        }

        /// <summary>One step of the wave: each cell becomes half the sum of its four wrapped neighbours less its own
        /// previous value, one random cell takes a drop, and the result is clamped and rotated in --
        /// <c>ArturoTextureController::BuildArturoImage</c> @0x5d67d0.</summary>
        public void Wave()
        {
            int rx = Roll(Aw), ry = Roll(Ah);
            for (int y = 0; y < Ah; y++)
            {
                int row = y * Aw, up = (y - 1 + Ah) % Ah * Aw, down = (y + 1) % Ah * Aw;
                for (int x = 0; x < Aw; x++)
                {
                    float v = (Cur[row + (x - 1 + Aw) % Aw] + Cur[row + (x + 1) % Aw]
                             + Cur[up + x] + Cur[down + x]) * 0.5f - Prev[row + x];
                    if (x == rx && y == ry) v += Roll(256) - 128;
                    v += 127f;
                    Next[row + x] = v >= 255f ? 128f : v > 0f ? v - 127f : -127f;
                }
            }
            Array.Copy(Cur, Prev, Cur.Length);
            Array.Copy(Next, Cur, Next.Length);
        }

        /// <summary>The height byte at each texel, from the two grids sampled bilinearly with a wrap and mixed by the
        /// phase, then each texel fetched from <see cref="Src"/> through it with a wrapping mask.</summary>
        public void Warp()
        {
            float sx = (float)Aw / W, sy = (float)Ah / H;
            int mx = W - 1, my = H - 1, o = 0;
            for (int y = 0; y < H; y++)
            {
                float fy = y * sy;
                int iy = (int)(fy - 0.5f), row0 = iy * Aw, row1 = (iy + 1) % Ah * Aw;
                float ty = fy - iy;
                for (int x = 0; x < W; x++, o += Px)
                {
                    float fx = x * sx;
                    int ix = (int)(fx - 0.5f), ix1 = (ix + 1) % Aw;
                    float tx = fx - ix;
                    float cur = (Cur[row1 + ix1] * tx + Cur[row1 + ix] * (1f - tx)) * ty
                              + (Cur[row0 + ix1] * tx + Cur[row0 + ix] * (1f - tx)) * (1f - ty);
                    float prev = (Prev[row1 + ix1] * tx + Prev[row1 + ix] * (1f - tx)) * ty
                               + (Prev[row0 + ix1] * tx + Prev[row0 + ix] * (1f - tx)) * (1f - ty);
                    int b = (int)(Phase * cur + prev * (1f - Phase) + 127f) & 0xFF;
                    int at = (((y + Dy[b]) & my) * W + ((x + Dx[b]) & mx)) * Px;
                    for (int c = 0; c < Px; c++) Out[o + c] = Src[at + c];
                }
            }
        }
    }

    /// <summary>Every ripple there is and what they cost and refuse: the slot table the bracket walks, the clock it
    /// reads and the counters the registry's note prints.</summary>
    private sealed class ArturoState
    {
        public long Last, Warps;
        public int Unbuilt, Flat, Huge;
        /// <summary>The slot's ripple, filed at the upload; null for every texture that does not ripple.</summary>
        public readonly Arturo?[] Slots = new Arturo?[MaxSlots];
        /// <summary>Slots that may hold a ripple, walked by the bracket; a slot whose ripple is gone drops off.</summary>
        public readonly List<int> Running = new();
    }

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

    /// <summary>Is <paramref name="n"/> a power of two? The warp wraps with a mask, so an image that is not cannot
    /// be fetched through.</summary>
    private static bool Pow2(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>WORKER: a rippling texture keeps its image and the buffers the step runs in, so the bracket allocates
    /// nothing. A BCn or grid image stands out, counted at the file: a controller is what sends vanilla's down the
    /// uncompressed path, so only a texture with no TGA at all lands there.</summary>
    private static void HoldRipple(Job j)
    {
        if (!j.Arturo.On || j.Arturo.Distort == 0 || j.Pixels != null || j.Compressed || j.Data == null) return;
        if (j.Channels < 1 || !Pow2(j.W) || !Pow2(j.H)) return;
        int aw = Math.Clamp(j.Arturo.GridW, 1, ArturoGridMax), ah = Math.Clamp(j.Arturo.GridH, 1, ArturoGridMax);
        if (aw != j.Arturo.GridW || ah != j.Arturo.GridH) System.Threading.Interlocked.Increment(ref _artState.Huge);
        byte[] src = VkTextures.Pack(j.Data, j.W, 0, 0, j.W, j.H, j.Channels, nibbles: false, out _);
        var a = new Arturo
        {
            W = j.W, H = j.H, Aw = aw, Ah = ah, Px = src.Length / (j.W * j.H),
            Speed = j.Arturo.Speed, Mip = j.GenMips, Rng = (uint)j.Slot * RngSeedStep | 1u,
            // Three sub-steps apart by slot, so a whole tileset's water does not warp on one frame.
            Sub = j.Slot % 3,
            Cur = new float[aw * ah], Prev = new float[aw * ah], Next = new float[aw * ah],
            Src = src, Out = new byte[src.Length],
        };
        a.Offsets(j.Arturo.Angle != 0, j.Arturo.Amplitude);
        a.Seed();
        j.Ripple = a;
    }

    /// <summary>BRACKET, after the upload: file the slot's ripple, or count why it stands out.</summary>
    private static void FileArturo(Job j)
    {
        if (!j.Arturo.On || j.Tail) return;
        // Control returns on all three sub-steps before the height rows are written when there is no
        // `distort`, so vanilla has no ripple here either.
        if (j.Arturo.Distort == 0) { _artState.Flat++; return; }
        if (j.Ripple == null) { _artState.Unbuilt++; return; }
        lock (_lock)
        {
            if ((_gen[j.Slot] & 0xFFF) != j.Gen) return;
            _artState.Slots[j.Slot] = j.Ripple;
            if (!_artState.Running.Contains(j.Slot)) _artState.Running.Add(j.Slot);
        }
    }

    /// <summary>BRACKET, once a frame: step every resident ripple on the controller's own clock -- one sub-step per
    /// 0.03 s, the phase advanced on the first of three and the warp on the third.
    /// ponytail: one clock per texture and at most one sub-step a frame, so under 33 fps the ripple slows with the
    /// frame; the engine shares one clock across all of them and steps it from every bind instead.</summary>
    private static void StepRipples()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _artState.Last == 0 ? 0f : (now - _artState.Last) / (float)System.Diagnostics.Stopwatch.Frequency;
        _artState.Last = now;
        List<int> running = _artState.Running;
        for (int k = running.Count - 1; k >= 0; k--)
        {
            int i = running[k];
            Arturo? a; uint name;
            lock (_lock) { a = _artState.Slots[i]; name = _preview[i] ? 0u : _name[i]; }
            if (a == null) { lock (_lock) running.RemoveAt(k); continue; }
            if (name == 0) continue;
            a.Acc += dt;
            if (a.Acc <= ArturoTick) continue;
            a.Acc -= MathF.Floor(a.Acc / ArturoTick) * ArturoTick;
            a.Sub = (a.Sub + 1) % 3;
            if (a.Sub == 0)
            {
                a.Phase += a.Speed * dt * 1.5f;
                if (a.Phase > 1f) { a.Wave(); a.Phase -= MathF.Floor(a.Phase); }
            }
            else if (a.Sub == 2) UploadWarp(name, a);
        }
    }

    /// <summary>One warp into level 0 of the slot's own image, the one materials already sample, and its mips after.</summary>
    private static void UploadWarp(uint name, Arturo a)
    {
        ImageId img = VkTextures.Image((int)name);
        if (img.IsNull || VkTextures.Device(_log) is not VkBackend vk) return;
        a.Warp();
        if (vk.Upload(img, 0, 0, a.Out) && a.Mip) vk.GenerateMips(img);
        _artState.Warps++;
    }

    /// <summary>Off-device. Oracles: the shipped <c>tcn01_water01.txi</c> text and the two constructors' defaults, as
    /// hand-written literals; the starting wave and the warp's offsets, evaluated by hand.</summary>
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

        var r = new Arturo { Aw = 4, Ah = 4, Rng = 1u, Cur = new float[16], Prev = new float[16], Next = new float[16] };
        r.Lay();
        // (1,2) of a 4x4 grid sits at radius 0.5, where sin(pi/2)*127 is the peak; (2,2) at the centre is
        // sin(pi), and (0,0) at radius sqrt(2) is sin(-0.41421*pi)*127.
        check.Expect(MathF.Abs(r.Cur[9] - 127f) < 0.01f, "the starting wave peaks at half the grid radius");
        check.Expect(MathF.Abs(r.Cur[10]) < 0.001f, "the starting wave is zero at the grid centre");
        check.Expect(MathF.Abs(r.Cur[0] + 122.40f) < 0.05f, "the starting wave dips to -122.4 at the corner");
        r.Wave();
        bool bounded = true;
        for (int i = 0; i < 16; i++) if (r.Cur[i] < -127f || r.Cur[i] > 128f) bounded = false;
        check.Expect(bounded, "a wave step clamps every cell into the height byte's range");

        // A flat wave is height byte 127 everywhere. Straight, that byte is no distance from the midpoint and
        // -0.5 truncates to 0. Angled at amplitude 5, 127 is 3.117 rad: 5*sin-0.5 = -0.38 -> 0, 5*cos-0.5 = -5.50 -> -5.
        byte[] rows = new byte[16];
        for (int i = 0; i < 16; i++) rows[i] = (byte)(i / 4 * 10);
        var w = new Arturo { W = 4, H = 4, Aw = 2, Ah = 2, Px = 1, Cur = new float[4], Prev = new float[4], Src = rows, Out = new byte[16] };
        w.Offsets(false, 5f);
        w.Warp();
        check.Expect(w.Out[0] == 0 && w.Out[5] == 10 && w.Out[15] == 30, "a flat wave's straight warp leaves the image alone");
        w.Offsets(true, 5f);
        w.Warp();
        check.Expect(w.Out[0] == 30 && w.Out[4] == 0 && w.Out[8] == 10 && w.Out[12] == 20,
                     "a flat wave's angled warp at amplitude 5 fetches five rows up, wrapped over four");
        return check.Ok;
    }

    /// <summary>The ripple clause of the registry's note.</summary>
    private static string ArturoNote()
    {
        int live = 0;
        lock (_lock) foreach (int i in _artState.Running) if (_artState.Slots[i] != null) live++;
        return $", {live} rippling ({_artState.Warps} warps)"
             + (_artState.Unbuilt > 0 ? $", {_artState.Unbuilt} ARTURO TEXTURE(S) NOT ANIMATED (compressed, a grid, or not a power of two)" : "")
             + (_artState.Huge > 0 ? $", {_artState.Huge} ARTURO GRID(S) CLAMPED to {ArturoGridMax}" : "")
             + (_artState.Flat > 0 ? $", {_artState.Flat} arturo without distort (vanilla draws those flat too)" : "");
    }
}
