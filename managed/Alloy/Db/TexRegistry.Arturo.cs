using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>The <c>.txi</c> procedural ripple. <c>proceduretype arturo</c> gives a texture an
/// <c>ArturoTextureController</c>, whose <c>Control</c> @0x5d83d0 warps the image by a wave field every third
/// tick of a 0.03 s clock and re-uploads level 0; here the wave field is the CPU's and the warp is
/// <c>cx_arturo_c.glsl</c>'s, writing the slot's own GL texture where it stands.</summary>
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
    /// <summary>The axis-aligned warp's height scale: a byte's distance from the midpoint over 128. Declared
    /// here and transcribed into the shader, which the self-check holds against it.</summary>
    private const float ArturoHeightScale = 0.0078125f;
    /// <summary>Height values there are: a byte, so the turn table is exact at 256 entries.</summary>
    private const int ArturoBytes = 256;
    /// <summary>Image units the warp binds to. The shader's own <c>binding</c> lines are checked
    /// against these.</summary>
    private const uint ArturoUnitOut = 0, ArturoUnitSrc = 1, ArturoUnitWave = 2, ArturoUnitTurn = 3;
    /// <summary>The square of texels one work group of the warp covers, which the dispatch counts in.</summary>
    private const int ArturoGroup = 8;
    /// <summary>Knuth's multiplicative step, spreading slot numbers into unrelated generator seeds.</summary>
    private const uint RngSeedStep = 2654435761u;
    /// <summary>The widest wave grid a <c>.txi</c> may ask for. The stock extreme is 128, and the grid is a
    /// square of floats, so this is the line past which content would be sizing the bracket's own memory.
    /// <c>ponytail:</c> clamped and counted; a real ceiling would size it against the texture instead.</summary>
    private const int ArturoGridMax = 512;

    /// <summary>One texture's running ripple: the controller's two wave grids, the source image the warp
    /// fetches through them, and the GL textures both reach the shader as. Every array is allocated once, at
    /// the file, so a step allocates nothing.</summary>
    private sealed class Arturo
    {
        public int W, H, Aw, Ah, Angle, Sub;
        public uint Gen;
        public float Amp, Speed, Phase, Acc;
        public uint PixFmt, PixType, Rng;
        public bool Mip;
        public float[] Cur = [], Prev = [], Next = [];
        /// <summary>The two grids as one cell per entry -- X this step's, Y the last -- which is the RG32F
        /// wave texture byte for byte.</summary>
        public Vector2[] Pair = [];
        public byte[] Src = [];
        /// <summary>The source image and the (Cur, Prev) pair as this ripple's own GL textures; 0 until the
        /// bracket made them, and 0 again once a context loss took them.</summary>
        public uint SrcTex, WaveTex;

        /// <summary>The controller's own generator, standing in for the engine's <c>rand()</c>: xorshift32,
        /// seeded off the slot so two textures do not ripple in lockstep.</summary>
        public int Roll(int n)
        {
            Rng ^= Rng << 13; Rng ^= Rng >> 17; Rng ^= Rng << 5;
            return n <= 0 ? 0 : (int)(Rng >> 1) % n;
        }

        /// <summary>The starting wave and the two steps <c>ArturoTextureController::Init</c> @0x5d6a70 runs
        /// over it. The height map is the warp's own, so there is none to seed.</summary>
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

        /// <summary>One step of the wave: each cell becomes half the sum of its four wrapped neighbours less
        /// its own previous value, one random cell takes a drop, and the result is clamped and rotated in --
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

        /// <summary>The two grids gathered into <see cref="Pair"/>, which is the one float texture the
        /// shader reads them as. Allocated once, so this allocates nothing.</summary>
        public void PairUp()
        {
            for (int i = 0; i < Cur.Length; i++) Pair[i] = new Vector2(Cur[i], Prev[i]);
        }
    }

    /// <summary>Every ripple there is, and what they cost and refuse: the slot table the bracket walks, the
    /// clock it reads and the counters the registry's note prints.</summary>
    private sealed class ArturoState
    {
        public long Last, Warps;
        public int Unbuilt, Flat, Huge, Vk;
        /// <summary>The warp program and the turn table, both per context; <see cref="Why"/> is set and said
        /// once when the device would not give us either, and every ripple then draws unwarped.</summary>
        public uint Prog, Turn;
        public string? Why;
        public int LocSize, LocWave, LocPhase, LocAmp, LocAngled;
        /// <summary>The slot's ripple, filed at the upload; null for every texture that does not ripple.</summary>
        public readonly Arturo?[] Slots = new Arturo?[MaxSlots];
        /// <summary>Slots that may hold a ripple, walked by the bracket; a slot whose ripple is gone drops off.</summary>
        public readonly List<int> Running = new();
    }

    /// <summary>The angled warp's direction per height byte -- X sine, Y cosine, which is the RG32F turn
    /// table byte for byte. The engine's own <c>sineLUT</c>/<c>cosineLUT</c>: a byte is the only input, so it
    /// is exact.</summary>
    private static readonly Vector2[] ArturoTurn = BuildTurn();

    /// <inheritdoc cref="ArturoTurn"/>
    private static Vector2[] BuildTurn()
    {
        var t = new Vector2[ArturoBytes];
        for (int i = 0; i < ArturoBytes; i++)
        {
            float a = i * ArturoByteToRadians;
            t[i] = new Vector2(MathF.Sin(a), MathF.Cos(a));
        }
        return t;
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

    /// <summary>WORKER: a rippling texture keeps its image and the buffers the step runs in, so the bracket
    /// allocates nothing. <c>ponytail:</c> a ripple on a BCn texture stands down, counted -- vanilla never
    /// loads one, because a controller is what sends it down the uncompressed path.</summary>
    private static void KeepArturo(Job j)
    {
        if (!j.Arturo.On || j.Arturo.Distort == 0 || j.Pixels != null || j.Compressed || j.Data == null) return;
        if (j.Channels < 1 || !Pow2(j.W) || !Pow2(j.H)) return;
        int aw = Math.Clamp(j.Arturo.GridW, 1, ArturoGridMax), ah = Math.Clamp(j.Arturo.GridH, 1, ArturoGridMax);
        if (aw != j.Arturo.GridW || ah != j.Arturo.GridH) System.Threading.Interlocked.Increment(ref _artState.Huge);
        j.Pixels = KeepWholeImage(j);
        // RGB8 and R8 are not image formats, and the warp writes the slot's own texture through an image unit.
        // The upload still goes through the TGA's own (format, type), which GL expands; a one-channel slot
        // keeps its G/B swizzle, so nothing samples differently for it.
        j.GlFmt = Gl.RGBA8;
        // What the slot costs on the device, for ADR 0023's eviction budget: the forced RGBA8 level, the
        // ripple's own copy of it that every warp fetches through, and the two-float wave grid.
        j.Bytes = (long)j.W * j.H * 8 + (long)aw * ah * 8;
        j.Ripple = new Arturo
        {
            Gen = j.Gen, W = j.W, H = j.H, Aw = aw, Ah = ah,
            Angle = j.Arturo.Angle, Amp = j.Arturo.Amplitude, Speed = j.Arturo.Speed,
            PixFmt = j.PixFmt, PixType = j.PixType, Mip = j.GenMips,
            Rng = (uint)j.Slot * RngSeedStep | 1u,
            Cur = new float[aw * ah], Prev = new float[aw * ah], Next = new float[aw * ah],
            Pair = new Vector2[aw * ah], Src = j.Pixels,
        };
        j.Ripple.Seed();
    }

    /// <summary>Is <paramref name="n"/> a power of two? The warp wraps with a mask, so a texture that is not
    /// cannot be fetched through.</summary>
    private static bool Pow2(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>BRACKET, after the upload: file the slot's ripple, or count why it stands out.</summary>
    private static void FileArturo(Job j)
    {
        if (!j.Arturo.On || j.Tail) return;
        // Control returns on all three sub-steps before the height rows are written when there is no
        // `distort`, so vanilla has no ripple here either.
        if (j.Arturo.Distort == 0) { _artState.Flat++; return; }
        if (j.Ripple == null) { _artState.Unbuilt++; return; }
        // ponytail: the warp is a GL compute pass; on Vulkan the texture draws its source image, said in the note
        if (Renderer.OnVulkan) { _artState.Vk++; return; }
        lock (_lock)
        {
            if ((_gen[j.Slot] & 0xFFF) != j.Gen) return;
            BuryArturo(_artState.Slots[j.Slot]);
            _artState.Slots[j.Slot] = j.Ripple;
            if (!_artState.Running.Contains(j.Slot)) _artState.Running.Add(j.Slot);
        }
    }

    /// <summary>The slot's ripple, dropped where the slot itself is reset; its GL goes with it. Caller holds
    /// <see cref="_lock"/>.</summary>
    private static void ClearArturo(int i)
    {
        BuryArturo(_artState.Slots[i]);
        _artState.Slots[i] = null;
    }

    /// <summary>A ripple's own two textures into the registry's graveyard, which the bracket deletes -- they
    /// are never resident, so there is no handle to retire first. Caller holds <see cref="_lock"/>.</summary>
    private static void BuryArturo(Arturo? a)
    {
        if (a == null) return;
        if (a.SrcTex != 0) { _graveyard.Enqueue(a.SrcTex); a.SrcTex = 0; }
        if (a.WaveTex != 0) { _graveyard.Enqueue(a.WaveTex); a.WaveTex = 0; }
    }

    /// <summary>TERMINAL per session (<c>docs/owned-resource-lifetimes.md</c>): the program, the turn table and
    /// every ripple's textures went with the context, so they are FORGOTTEN here rather than deleted and the
    /// next warp makes them again. Runs before the graveyard is cleared. Caller holds <see cref="_lock"/>.</summary>
    private static void ArturoContextLost()
    {
        ArturoState s = _artState;
        s.Prog = 0; s.Turn = 0; s.Why = null;
        for (int i = 0; i < s.Slots.Length; i++)
        {
            Arturo? a = s.Slots[i];
            if (a != null) { a.SrcTex = 0; a.WaveTex = 0; }
        }
    }

    /// <summary>BRACKET, once a frame: step every resident ripple on the controller's own clock -- one
    /// sub-step per 0.03 s, the phase advanced on the first of three and the warp dispatched on the third.
    /// ponytail: one clock per texture, and at most one sub-step a frame, so under 33 fps the ripple slows
    /// with the frame; the engine shares one clock across all of them and steps it from every bind instead.</summary>
    private static void StepArturo()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _artState.Last == 0 ? 0f : (now - _artState.Last) / (float)System.Diagnostics.Stopwatch.Frequency;
        _artState.Last = now;
        // Stood down: the wave grids would step for ever for a warp that will never run. After the clock, so
        // a context loss that lifts the stand-down does not hand the phase the whole interval at once.
        if (_artState.Why != null) return;
        List<int> running = _artState.Running;
        if (running.Count == 0) return;
        for (int k = running.Count - 1; k >= 0; k--)
        {
            int i = running[k];
            Arturo? a; uint name; uint gen;
            lock (_lock) { a = _artState.Slots[i]; name = _preview[i] ? 0u : _name[i]; gen = _gen[i] & 0xFFF; }
            if (a == null || a.Gen != gen)
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_artState.Slots[i], a)) { BuryArturo(a); _artState.Slots[i] = null; }
                    running.RemoveAt(k);
                }
                continue;
            }
            if (name == 0) continue;
            a.Acc += dt;
            if (a.Acc <= ArturoTick) continue;
            a.Acc -= MathF.Floor(a.Acc / ArturoTick) * ArturoTick;
            a.Sub = (a.Sub + 1) % 3;
            // The controller's three sub-steps: the phase and the wave on the first, half the height rows on
            // each of the first two, the warp on the third. The shader works the height out per texel, so the
            // middle sub-step has nothing left to do -- and the cadence must keep it, or the ripple runs half
            // again as fast as the engine's.
            if (a.Sub == 0)
            {
                a.Phase += a.Speed * dt * 1.5f;
                while (a.Phase > 1f) { a.Wave(); a.Phase -= 1f; }
            }
            else if (a.Sub == 2) WarpArturo(name, a);
        }
    }

    /// <summary>BRACKET: one ripple's height map and fetch on the GPU, into level 0 of the slot's own texture
    /// -- the one materials already sample, so no handle and no record changes -- and its mips after.</summary>
    private static void WarpArturo(uint name, Arturo a)
    {
        ArturoState s = _artState;
        if (!ArturoPrograms() || !ArturoBuffers(a)) return;
        // The first warp of the context is the one checked below, so it starts from a clean slate: an earlier
        // stage of this same bracket leaving an error would otherwise stand the feature down for it.
        bool first = s.Warps == 0;
        if (first) Gl.DrainErrors(8);
        a.PairUp();
        fixed (Vector2* p = a.Pair) Gl.TextureSubImage2D(a.WaveTex, 0, 0, 0, a.Aw, a.Ah, Gl.RG, Gl.FLOAT, p);
        Gl.UseProgram(s.Prog);
        Gl.BindImageTexture(ArturoUnitOut, name, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA8);
        Gl.BindImageTexture(ArturoUnitSrc, a.SrcTex, 0, 0, 0, Gl.READ_ONLY, Gl.RGBA8);
        Gl.BindImageTexture(ArturoUnitWave, a.WaveTex, 0, 0, 0, Gl.READ_ONLY, Gl.RG32F);
        Gl.BindImageTexture(ArturoUnitTurn, s.Turn, 0, 0, 0, Gl.READ_ONLY, Gl.RG32F);
        Gl.Uniform2i(s.LocSize, a.W, a.H);
        Gl.Uniform2i(s.LocWave, a.Aw, a.Ah);
        Gl.Uniform1f(s.LocPhase, a.Phase);
        Gl.Uniform1f(s.LocAmp, a.Amp);
        Gl.Uniform1i(s.LocAngled, a.Angle != 0 ? 1 : 0);
        Gl.DispatchCompute((uint)((a.W + ArturoGroup - 1) / ArturoGroup), (uint)((a.H + ArturoGroup - 1) / ArturoGroup), 1);
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.TEXTURE_UPDATE_BARRIER_BIT);
        if (a.Mip && Gl.GenerateTextureMipmap != null) Gl.GenerateTextureMipmap(name);
        Gl.UseProgram(0);
        // A driver that refused the dispatch would otherwise leave every rippling texture frozen with
        // nothing said; once per context is enough, and a per-frame glGetError is not free.
        if (first && Gl.GetError != null)
        {
            uint err = Gl.GetError();
            if (err != 0) { ArturoStandDown($"the driver refused the warp dispatch (0x{err:x})"); return; }
        }
        s.Warps++;
    }

    /// <summary>The warp program and the turn table, once per context.</summary>
    private static bool ArturoPrograms()
    {
        ArturoState s = _artState;
        if (s.Prog != 0) return true;
        if (s.Why != null) return false;
        if (Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.MemoryBarrier == null
            || Gl.CreateTextures == null || Gl.TextureStorage2D == null || Gl.TextureSubImage2D == null)
            return ArturoStandDown("the device has no compute entry points");
        s.Prog = GlProgram.Compute(ShaderSource.Get("cx_arturo_c.glsl"), _log, "arturo ripple warp");
        if (s.Prog == 0) return ArturoStandDown("the warp program did not build");
        uint turn;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &turn);
        if (turn == 0) return ArturoStandDown("glCreateTextures gave no turn table");
        s.Turn = turn;
        Gl.TextureStorage2D(turn, 1, Gl.RG32F, ArturoBytes, 1);
        fixed (Vector2* p = ArturoTurn) Gl.TextureSubImage2D(turn, 0, 0, 0, ArturoBytes, 1, Gl.RG, Gl.FLOAT, p);
        s.LocSize = GlProgram.Loc(s.Prog, "uSize");
        s.LocWave = GlProgram.Loc(s.Prog, "uWaveSize");
        s.LocPhase = GlProgram.Loc(s.Prog, "uPhase");
        s.LocAmp = GlProgram.Loc(s.Prog, "uAmp");
        s.LocAngled = GlProgram.Loc(s.Prog, "uAngled");
        return true;
    }

    /// <summary>This ripple's source image and wave-pair textures, once per context. The image goes up here
    /// and never again: it is what every warp fetches through.</summary>
    private static bool ArturoBuffers(Arturo a)
    {
        if (a.SrcTex == 0)
        {
            uint t;
            Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
            if (t == 0) return ArturoStandDown("glCreateTextures gave no source image");
            Gl.TextureStorage2D(t, 1, Gl.RGBA8, a.W, a.H);
            int svAlign;
            Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &svAlign);
            if (svAlign != 1) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
            fixed (byte* p = a.Src) Gl.TextureSubImage2D(t, 0, 0, 0, a.W, a.H, a.PixFmt, a.PixType, p);
            if (svAlign != 1) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, svAlign);
            a.SrcTex = t;
        }
        if (a.WaveTex == 0)
        {
            uint t;
            Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
            if (t == 0) return ArturoStandDown("glCreateTextures gave no wave grid");
            Gl.TextureStorage2D(t, 1, Gl.RG32F, a.Aw, a.Ah);
            a.WaveTex = t;
        }
        return true;
    }

    /// <summary>No warp on this device or in this context: said once, loudly, and on the period line, with
    /// everything it held given back -- the program, the turn table and every ripple's own two textures,
    /// which nothing will read again. A rippling texture then draws its source image, which is the vanilla
    /// look with the ripple missing. BRACKET, as every caller is.</summary>
    private static bool ArturoStandDown(string why)
    {
        ArturoState s = _artState;
        s.Why = why;
        if (s.Prog != 0 && Gl.DeleteProgram != null) Gl.DeleteProgram(s.Prog);
        GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, s.Turn);
        s.Prog = 0; s.Turn = 0;
        lock (_lock) foreach (int i in s.Running) BuryArturo(s.Slots[i]);
        _log?.Always($"TexRegistry: ARTURO RIPPLES STAND DOWN -- {why}. Every \"proceduretype arturo\" texture draws unwarped");
        return false;
    }

    /// <summary>Off-device. Oracles: the shipped <c>tcn01_water01.txi</c> text and the two constructors'
    /// defaults as hand-written literals; the turn table against two angles evaluated by hand; the starting
    /// wave against three cells evaluated by hand; the warp's own GLSL text against these declarations.</summary>
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
        // beside its TGA, and a ripple cannot be written into a compressed level, so this decides the feature.
        ParseArturo("proceduretype arturo\ndistort 1\n", out a);
        check.Expect(TxiIsProcedural(a, 1, 1), "a declared ripple takes the uncompressed container");
        ParseArturo("envmaptexture tm_coinbase01\n", out a);
        check.Expect(!TxiIsProcedural(a, 1, 1) && TxiIsProcedural(a, 4, 4),
                     "a plain texture races for its container, a flipbook grid does not");

        // A byte read as a full turn: 64 * 0.024543693 = 1.5708 rad is a quarter of one, 128 a half.
        check.Expect(MathF.Abs(ArturoTurn[64].X - 1f) < 0.0001f && MathF.Abs(ArturoTurn[64].Y) < 0.0001f,
                     "the turn table reads height byte 64 as a quarter turn");
        check.Expect(MathF.Abs(ArturoTurn[128].X) < 0.0001f && MathF.Abs(ArturoTurn[128].Y + 1f) < 0.0001f,
                     "the turn table reads height byte 128 as a half turn");
        check.Expect(ArturoTurn[0].X == 0f && ArturoTurn[0].Y == 1f, "the turn table starts at no turn at all");

        // The warp itself is the shader's, so what can go wrong here is the transcription: a unit the bracket
        // binds that the shader does not read, a group the dispatch does not match, a constant that drifted.
        string cs = ShaderSource.Get("cx_arturo_c.glsl");
        check.Expect(cs.Contains($"layout(local_size_x = {ArturoGroup}, local_size_y = {ArturoGroup}) in;"),
                     "the warp shader is cut into the group the dispatch counts");
        check.Expect(cs.Contains($"binding = {ArturoUnitOut}) writeonly uniform image2D uWarped;")
                     && cs.Contains($"binding = {ArturoUnitSrc}) readonly uniform image2D uSource;")
                     && cs.Contains($"binding = {ArturoUnitWave}) readonly uniform image2D uWave;")
                     && cs.Contains($"binding = {ArturoUnitTurn}) readonly uniform image2D uTurn;"),
                     "the warp shader's four images sit on the units the bracket binds them to");
        check.Expect(cs.Contains("layout(rgba8, binding = 0) writeonly") && cs.Contains("layout(rgba8, binding = 1) readonly"),
                     "the warped image and its source are the rgba8 the slot's own texture is forced to");
        check.Expect(cs.Contains($"const float CX_ARTURO_HEIGHT = {ArturoHeightScale.ToString(System.Globalization.CultureInfo.InvariantCulture)};"),
                     "the shader's axis-aligned warp scale is the controller's own");
        check.Expect(cs.Contains("imageLoad(uTurn, ivec2(b, 0))") && cs.Contains("+ 127.0) & 0xFF"),
                     "the shader indexes the turn table by the wrapped height byte");

        var r = new Arturo { Aw = 4, Ah = 4, Rng = 1u, Cur = new float[16], Prev = new float[16], Next = new float[16] };
        r.Lay();
        // (1,2) of a 4x4 grid sits at radius 0.5, where sin(pi/2)*127 is the peak; (2,2) at the centre is
        // sin(pi), and (0,0) at radius sqrt(2) is sin(-0.41421*pi)*127.
        check.Expect(MathF.Abs(r.Cur[2 * 4 + 1] - 127f) < 0.01f, "the starting wave peaks at half the grid radius");
        check.Expect(MathF.Abs(r.Cur[2 * 4 + 2]) < 0.001f, "the starting wave is zero at the grid centre");
        check.Expect(MathF.Abs(r.Cur[0] + 122.40f) < 0.05f, "the starting wave dips to -122.4 at the corner");
        r.Wave();
        bool bounded = true;
        for (int i = 0; i < 16; i++) if (r.Cur[i] < -127f || r.Cur[i] > 128f) bounded = false;
        check.Expect(bounded, "a wave step clamps every cell into the byte range the height map is written from");
        return check.Ok;
    }

    /// <summary>The ripple clause of the registry's note.</summary>
    private static string ArturoNote()
    {
        int live = 0;
        lock (_lock) foreach (int i in _artState.Running) if (_artState.Slots[i] != null) live++;
        return $", {live} rippling ({_artState.Warps} GPU warps)"
             + (_artState.Why != null ? $", ARTURO RIPPLES STAND DOWN ({_artState.Why})" : "")
             + (_artState.Unbuilt > 0 ? $", {_artState.Unbuilt} ARTURO TEXTURE(S) NOT ANIMATED (compressed, or not a power of two)" : "")
             + (_artState.Vk > 0 ? $", {_artState.Vk} ARTURO TEXTURE(S) NOT ANIMATED ON VULKAN (the warp is not ported)" : "")
             + (_artState.Huge > 0 ? $", {_artState.Huge} ARTURO GRID(S) CLAMPED to {ArturoGridMax}" : "")
             + (_artState.Flat > 0 ? $", {_artState.Flat} arturo without distort (vanilla draws those flat too)" : "");
    }
}
