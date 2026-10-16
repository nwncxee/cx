using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>A texture's <c>.txi</c> beyond its sampler: the material words (<c>blending</c>, <c>decal</c>) and the
/// <c>numx</c>/<c>numy</c> grid with its <c>proceduretype cycle</c> flipbook. The engine cuts cell <c>k</c> at
/// <c>((k % numx) * w/numx, (k / numx) * h/numy)</c>; here one cell-sized texture per slot is re-uploaded as
/// <c>CycleTIDTextureController::Control</c> @0x5d6c90 steps the cell, so a record's bindless handle never moves.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary><c>blending</c> as <c>Material::ParseField</c> @0x5c5850 stores it: <c>additive</c> is the pair
    /// (SRC_ALPHA, ONE); <c>punchthrough</c> and <c>default</c> are not read here.</summary>
    private const byte TxiBlendAdditive = 1;

    /// <summary>One cycling grid: the whole decoded image kept on the CPU and the step state of the engine's controller.</summary>
    private sealed class Cycle
    {
        public int NumX, NumY, CellW, CellH, FullW, Px, Frame;
        public uint PixFmt, PixType;
        public float Fps, Acc;
        public bool Mip;
        public byte[] Pixels = [];
    }

    private static readonly byte[] _txiBlend = new byte[MaxSlots];
    private static readonly bool[] _txiDecal = new bool[MaxSlots];
    /// <summary>The slot's cycle, set by the bracket at the upload; null for every texture that does not cycle.</summary>
    private static readonly Cycle?[] _cycle = new Cycle?[MaxSlots];
    /// <summary>Slots that may hold a cycle, walked by the bracket; a slot whose cycle is gone drops off at the walk.</summary>
    private static readonly List<int> _cycling = new();
    private static long _cycleLast;
    private static long _cycleSteps;
    private static int _gridCompressed;

    /// <summary>Does this texture's <c>.txi</c> say <c>blending additive</c>? The engine's material blends it
    /// (SRC_ALPHA, ONE), which is the additive class: no depth write, no shadow.</summary>
    internal static bool TxiAdditive(TexId id) => Valid(id) && _txiBlend[id.Index] == TxiBlendAdditive;

    /// <summary>Does this texture's <c>.txi</c> say <c>decal 1</c>? The material's +0x98 flag, which makes the
    /// engine draw the part fullbright.</summary>
    internal static bool TxiDecal(TexId id) => Valid(id) && _txiDecal[id.Index];

    /// <summary>The <c>.txi</c> words this file owns, parsed line by line as <c>CAurTextureBasic::ParseField</c>
    /// @0x5881e0 and <c>Material::ParseField</c> @0x5c5850 do: the first word is the key, so a <c>#</c> line is nothing. <c>fps</c>
    /// is the cycle controller's own field and reaches it only once a <c>proceduretype cycle</c> line has created it,
    /// and a later <c>proceduretype</c> of another kind replaces it.</summary>
    internal static void ParseTxi(string? txi, out byte blend, out bool decal, out int numX, out int numY, out bool cycle, out float fps)
    {
        blend = 0; decal = false; numX = 1; numY = 1; cycle = false; fps = 1f;
        if (txi == null) return;
        foreach (string raw in txi.Split('\n'))
        {
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            int sp = line.IndexOfAny(' ', '\t');
            if (sp < 0) continue;
            ReadOnlySpan<char> key = line[..sp], val = line[(sp + 1)..].Trim();
            int vsp = val.IndexOfAny(' ', '\t');
            if (vsp >= 0) val = val[..vsp];
            if (key.Equals("blending", StringComparison.OrdinalIgnoreCase))
                blend = val.SequenceEqual("additive") ? TxiBlendAdditive : (byte)0;   // the value compare is byte-exact, unlike the key's
            else if (key.Equals("decal", StringComparison.OrdinalIgnoreCase)) decal = int.TryParse(val, out int d) && d != 0;
            else if (key.Equals("numx", StringComparison.OrdinalIgnoreCase)) { if (int.TryParse(val, out int n)) numX = n; }
            else if (key.Equals("numy", StringComparison.OrdinalIgnoreCase)) { if (int.TryParse(val, out int n)) numY = n; }
            else if (key.Equals("proceduretype", StringComparison.OrdinalIgnoreCase))
            { cycle = val.SequenceEqual("cycle"); fps = 1f; }
            else if (cycle && key.Equals("fps", StringComparison.OrdinalIgnoreCase)
                     && float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                fps = f;
        }
    }

    /// <summary>GAME thread, at the offer: file the material words on the slot and hand the grid to the job.</summary>
    private static void TxiMaterial(int i, string? txi, Job j)
    {
        ParseTxi(txi, out byte blend, out bool decal, out j.GridX, out j.GridY, out j.Cycle, out j.Fps);
        ParseArturo(txi, out j.Arturo);
        lock (_lock)
        {
            if (_txiBlend[i] == blend && _txiDecal[i] == decal) return;
            _txiBlend[i] = blend; _txiDecal[i] = decal;
            CoverageSerial++; TableSerial++;
        }
    }

    /// <summary>WORKER: a decoded raster that is a grid keeps its whole image and becomes one cell's upload.
    /// A BCn grid stays whole -- the engine decodes those uncompressed, which this path does not.</summary>
    private static void CutGrid(Job j)
    {
        if (j.GridX * j.GridY <= 1 || j.Kind != TexKind.Plain) return;
        if (j.Compressed) { System.Threading.Interlocked.Increment(ref _gridCompressed); return; }
        int cw = j.W / Math.Max(j.GridX, 1), ch = j.H / Math.Max(j.GridY, 1);
        if (cw < 1 || ch < 1) return;
        j.Pixels = KeepWholeImage(j);
        j.FullW = j.W; j.W = cw; j.H = ch; j.Levels = Levels(cw, ch);
        j.Bytes = (long)cw * ch * j.Channels;
    }

    /// <summary>WORKER: the job's decoded raster as a managed copy, which is what a texture the registry
    /// re-uploads part of every frame is built out of. The length is the decode's own, not the caller's.</summary>
    private static byte[] KeepWholeImage(Job j)
    {
        var px = new byte[(long)j.W * j.H * j.Channels];
        new ReadOnlySpan<byte>(j.Data, px.Length).CopyTo(px);
        return px;
    }

    /// <summary>BRACKET, after the upload: file the slot's cycle (a grid with no <c>cycle</c> shows cell 0, as the engine's does).</summary>
    private static void FileCycle(Job j)
    {
        if (j.Pixels == null || !j.Cycle) return;
        var c = new Cycle
        {
            NumX = j.GridX, NumY = j.GridY, CellW = j.W, CellH = j.H, FullW = j.FullW, Px = j.Channels,
            PixFmt = j.PixFmt, PixType = j.PixType, Fps = j.Fps, Mip = j.GenMips, Pixels = j.Pixels,
        };
        lock (_lock)
        {
            if ((_gen[j.Slot] & 0xFFF) != j.Gen) return;
            _cycle[j.Slot] = c;
            if (!_cycling.Contains(j.Slot)) _cycling.Add(j.Slot);
        }
    }

    /// <summary>BRACKET, once a frame: step every resident cycle the way <c>CycleTIDTextureController::Control</c> does --
    /// the frame's time accumulates and, once past <c>1/fps</c>, resets to 0 and the cell advances by one, wrapping at
    /// <c>numx*numy</c>.
    /// ponytail: one clock for all; the engine's steps only while the texture is bound, so an unseen flipbook pauses there.</summary>
    private static void StepCycles()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _cycleLast == 0 ? 0f : (now - _cycleLast) / (float)System.Diagnostics.Stopwatch.Frequency;
        _cycleLast = now;
        if (_cycling.Count == 0 || Gl.TextureSubImage2D == null) return;
        for (int k = _cycling.Count - 1; k >= 0; k--)
        {
            int i = _cycling[k];
            Cycle? c; uint name;
            lock (_lock) { c = _cycle[i]; name = _preview[i] ? 0u : _name[i]; }
            if (c == null) { _cycling.RemoveAt(k); continue; }
            if (name == 0) continue;
            c.Acc += dt;
            if (!(1f / c.Fps < c.Acc)) continue;
            c.Acc = 0f;
            c.Frame = (c.Frame + 1) % (c.NumX * c.NumY);
            UploadCell(name, c);
            _cycleSteps++;
        }
    }

    /// <summary>Cell <see cref="Cycle.Frame"/> out of the kept image into the slot's texture, mips regenerated.</summary>
    private static void UploadCell(uint name, Cycle c)
    {
        int x = c.Frame % c.NumX * c.CellW, y = c.Frame / c.NumX * c.CellH;
        int svAlign, svRow;
        Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &svAlign);
        Gl.GetIntegerv(Gl.UNPACK_ROW_LENGTH, &svRow);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, c.FullW);
        fixed (byte* p = c.Pixels.AsSpan(checked((int)(((long)y * c.FullW + x) * c.Px))))
            Gl.TextureSubImage2D(name, 0, 0, 0, c.CellW, c.CellH, c.PixFmt, c.PixType, p);
        Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, svRow);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, svAlign);
        if (c.Mip && Gl.GenerateTextureMipmap != null) Gl.GenerateTextureMipmap(name);
    }

    /// <summary>The cycle clause of the registry's note.</summary>
    private static string CycleNote()
    {
        int live = 0;
        lock (_lock) foreach (int i in _cycling) if (_cycle[i] != null) live++;
        return $"{live} flipbook(s) cycling ({_cycleSteps} steps)"
             + (_gridCompressed > 0 ? $", {_gridCompressed} BCn GRID(S) DRAWN WHOLE" : "");
    }

    /// <summary>Off-device: the <c>.txi</c> words read as the engine reads them -- a commented line is nothing, <c>fps</c>
    /// before <c>proceduretype cycle</c> is lost, a stock flame grid reads whole.</summary>
    internal static bool TxiSelfCheck(Checker check)
    {
        ParseTxi("proceduretype cycle\r\nnumx 4\r\nnumy 4\r\nfps 32\r\nblending additive\r\ndecal 1\r\n",
                 out byte b, out bool d, out int nx, out int ny, out bool cy, out float fps);
        check.Expect(b == TxiBlendAdditive && d && nx == 4 && ny == 4 && cy && fps == 32f, "fxpa_flame02.txi reads as a 4x4 additive decal cycling at 32");
        ParseTxi("fps 12\nproceduretype cycle\nnumy 8\n# blending additive\nblending default\n",
                 out b, out d, out nx, out ny, out cy, out fps);
        check.Expect(b == 0 && !d && nx == 1 && ny == 8 && cy && fps == 1f, "a # line and an fps before the controller exists are nothing");
        ParseTxi("proceduretype cycle\nfps 8\nproceduretype arturo\n", out _, out _, out _, out _, out cy, out _);
        check.Expect(!cy, "a later proceduretype replaces the cycle");
        return check.Ok;
    }
}
