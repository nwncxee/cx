using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

/// <summary>One piece of skin art: a texture resref and its nine-slice border in texels, drawn 1:1 at every UI scale as
/// NUI draws it (all zero = stretched whole). A non-zero <see cref="SrcW"/> draws only that window of the sheet (texels
/// from the image's top-left), stretched across and, with <see cref="TileY"/>, repeated down at its native height.</summary>
public readonly record struct SkinPart(string Name, string Resref, int L = 0, int T = 0, int R = 0, int B = 0,
                                       int SrcX = 0, int SrcY = 0, int SrcW = 0, int SrcH = 0, bool TileY = false);

/// <summary>
/// A window's skin over the game's own GUI art: the declared parts, one <see cref="VertexBatch"/> per part, one
/// <see cref="TextRig.DrawSkin"/> per part per frame. Each resref is resolved by name every frame and nothing
/// engine-side is cached across one -- the <c>CAurTexture*</c> is borrowed. A server may re-point a part with the
/// policy key <c>&lt;skin&gt;.skin.&lt;part&gt;=&lt;resref&gt;</c>.
/// </summary>
public sealed class Skin
{
    /// <summary>Texture restypes a part may resolve to: tga, dds, ktx, png.</summary>
    private static readonly ushort[] TexTypes = { 3, 2033, 2073, 2080 };

    public readonly string Name;
    private readonly SkinPart[] _parts;
    private readonly string[] _res;
    private readonly VertexBatch[] _b;
    private readonly int[] _tid, _w, _h;
    private int _armedEpoch = -1;
    private string _armedFor = "";

    /// <summary>Probed and whole this session/policy: the owner may draw with it.</summary>
    public bool Armed { get; private set; }
    /// <summary>The parts that failed the last probe, comma-joined; "" when whole.</summary>
    public string Missing { get; private set; } = "";

    public Skin(string name, params SkinPart[] parts)
    {
        Name = name; _parts = parts;
        _res = new string[parts.Length];
        _b = new VertexBatch[parts.Length];
        _tid = new int[parts.Length]; _w = new int[parts.Length]; _h = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) { _res[i] = parts[i].Resref; _b[i] = new VertexBatch(0f, 0f); }
    }

    /// <summary>The part's index by name, -1 when undeclared. Cold.</summary>
    public int IndexOf(string part)
    {
        for (int i = 0; i < _parts.Length; i++) if (_parts[i].Name == part) return i;
        return -1;
    }

    /// <summary>Re-resolve every part's resref against the server policy and probe them through the engine's pure
    /// key lookup, when the policy moved or the key <paramref name="context"/> (e.g. the module) changed since the
    /// last arm; otherwise a field compare per frame. Refusal is whole-skin: one missing resref logs every missing
    /// name, puts a notice on screen, and the owner draws its built-in look instead.</summary>
    public bool Arm(ExtenderLog? log, string context)
    {
        if (_armedEpoch == ServerPolicy.Epoch && _armedFor == context) return Armed;
        _armedEpoch = ServerPolicy.Epoch; _armedFor = context;
        var missing = new List<string>();
        for (int i = 0; i < _parts.Length; i++)
        {
            _res[i] = ServerPolicy.Str($"{Name}.skin.{_parts[i].Name}", _parts[i].Resref);
            if (!Exists(_res[i])) missing.Add($"{_parts[i].Name}={_res[i]}");
        }
        bool was = Armed;
        Armed = missing.Count == 0;
        Missing = string.Join(", ", missing);
        if (!Armed) Notice.Show(log, $"{Name} skin refused -- missing art: {Missing}; drawing the built-in look");
        else if (!was) log?.Info($"Skin {Name}: armed ({_parts.Length} part(s): {string.Join(", ", _res)})");
        return Armed;
    }

    private static unsafe bool Exists(string resref)
    {
        if (resref.Length == 0 || resref.Length > 16) return false;
        if (!CX.Alloy.ResLocate.Attach(out _)) return false;
        byte* name = stackalloc byte[17];
        for (int i = 0; i < resref.Length; i++) name[i] = (byte)char.ToLowerInvariant(resref[i]);
        name[resref.Length] = 0;
        foreach (ushort t in TexTypes)
            if (CX.Alloy.ResLocate.Find(name, t, out _, out _)) return true;
        return false;
    }

    /// <summary>Force the next <see cref="Arm"/> to probe again (a window opening: the module's haks may have changed).</summary>
    public void Rearm() => _armedEpoch = -1;

    /// <summary>Start a frame: resolve every part's live texture id and size, clear the batches. Render thread;
    /// allocates nothing.</summary>
    public void Begin()
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            _b[i].Clear();
            _tid[i] = AurTex.TidByName(_res[i]);
            TextRig.TexSize(_tid[i], out _w[i], out _h[i]);
        }
    }

    /// <summary>Part <paramref name="part"/> over the rect snapped to whole pixels, nine-sliced by its declared border
    /// at 1:1 (a borderless part stretches whole), tinted.</summary>
    public void Draw(int part, float x0, float y0, float x1, float y1, uint rgba = 0xffffffffu)  // not-an-offset: opaque white
    {
        if ((uint)part >= (uint)_parts.Length || _tid[part] == 0) return;
        x0 = MathF.Round(x0); y0 = MathF.Round(y0); x1 = MathF.Round(x1); y1 = MathF.Round(y1);
        SkinPart p = _parts[part];
        float r = ((rgba >> 24) & 0xFF) / 255f, g = ((rgba >> 16) & 0xFF) / 255f, b = ((rgba >> 8) & 0xFF) / 255f, a = (rgba & 0xFF) / 255f;
        if (p.SrcW > 0 && _w[part] > 0 && _h[part] > 0)
            Cut(_b[part], x0, y0, x1, y1, _w[part], _h[part], p, r, g, b, a);
        else if (p.L + p.T + p.R + p.B == 0) _b[part].Tex(x0, y0, x1, y1, 0f, 1f, 1f, 0f, r, g, b, a);
        else _b[part].NineSlice(x0, y0, x1, y1, _w[part], _h[part], p.L, p.T, p.R, p.B, 1f, r, g, b, a);
    }

    /// <summary>The sub-rect stretched across; with <see cref="SkinPart.TileY"/> repeated down every
    /// <see cref="SkinPart.SrcH"/> px from the top, the last repeat cut short with its UV.</summary>
    private static void Cut(VertexBatch b, float x0, float y0, float x1, float y1, float w, float h, in SkinPart p,
                            float r, float g, float bl, float a)
    {
        float u0 = p.SrcX / w, u1 = (p.SrcX + p.SrcW) / w, vTop = 1f - p.SrcY / h, vSpan = p.SrcH / h;
        if (!p.TileY) { b.Tex(x0, y0, x1, y1, u0, vTop, u1, vTop - vSpan, r, g, bl, a); return; }
        for (float y = y0; y < y1; y += p.SrcH)
        {
            float ye = MathF.Min(y + p.SrcH, y1);
            b.Tex(x0, y, x1, ye, u0, vTop, u1, vTop - vSpan * (ye - y) / p.SrcH, r, g, bl, a);
        }
    }

    /// <summary>The part's texture width over height this frame, 0 when unresolved.</summary>
    public float Aspect(int part) => (uint)part < (uint)_parts.Length && _h[part] > 0 ? (float)_w[part] / _h[part] : 0f;

    /// <summary>One draw per part holding geometry, in declaration order (declare backgrounds first); <paramref name="skip"/>
    /// and <paramref name="skip2"/> are left for <see cref="FlushClipped"/>.</summary>
    public void Flush(float alpha = 1f, int skip = -1, int skip2 = -1)
    {
        for (int i = 0; i < _parts.Length; i++)
            if (i != skip && i != skip2 && _b[i].VertexCount > 0) TextRig.DrawSkin(_tid[i], _b[i].AsSpan(), _b[i].VertexCount, alpha);
    }

    /// <summary>Draw one part's batch scissored to (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="cw"/>,
    /// <paramref name="ch"/>), y-down px.</summary>
    public void FlushClipped(int part, int cx, int cy, int cw, int ch, float alpha = 1f)
    {
        if ((uint)part < (uint)_parts.Length && _b[part].VertexCount > 0)
            TextRig.DrawSkin(_tid[part], _b[part].AsSpan(), _b[part].VertexCount, alpha, true, cx, cy, cw, ch);
    }

    /// <summary>Off-device: the nine-slice emitter's geometry and UVs, the proportional shrink, and a zero-alloc
    /// emit.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var b = new VertexBatch(0f, 0f);
        b.NineSlice(0f, 0f, 100f, 50f, 64, 32, 4, 4, 4, 4, 2f, 1f, 1f, 1f, 1f);
        check.Expect(b.VertexCount == 9 * 6, "nine quads");
        float[] v = b.ToArray();
        check.Expect(v[0] == 0f && v[1] == 0f && v[2] == 0f && v[3] == 1f, "top-left corner starts at uv (0,1): bottom-up texture");
        check.Expect(v[VertexBatch.Floats * 2] == 8f && v[VertexBatch.Floats * 2 + 1] == 8f, "corner is border × scale on screen");
        check.Expect(MathF.Abs(v[VertexBatch.Floats * 2 + 2] - 4f / 64f) < 1e-6f, "corner u ends at the border texel");
        b.Clear();
        b.NineSlice(0f, 0f, 10f, 10f, 64, 64, 8, 8, 8, 8, 1f, 1f, 1f, 1f, 1f);
        check.Expect(b.VertexCount == 4 * 6, "a rect narrower than its borders: corners only, shrunk to fit");
        b.Clear();
        b.NineSlice(0f, 0f, 0f, 10f, 64, 64, 8, 8, 8, 8, 1f, 1f, 1f, 1f, 1f);
        check.Expect(b.VertexCount == 0, "degenerate rect emits nothing");
        b.NineSlice(0f, 0f, 100f, 50f, 64, 32, 4, 4, 4, 4, 2f, 1f, 1f, 1f, 1f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); b.NineSlice(0f, 0f, 100f, 50f, 64, 32, 4, 4, 4, 4, 2f, 1f, 1f, 1f, 1f); }
        check.Expect(GC.GetAllocatedBytesForCurrentThread() - before == 0, "100 emits allocate nothing");
        b.Clear();
        Cut(b, 0f, 0f, 10f, 40f, 16f, 16f, new SkinPart("thumb", "x", SrcX: 3, SrcW: 10, SrcH: 16, TileY: true), 1f, 1f, 1f, 1f);
        v = b.ToArray();
        check.Expect(b.VertexCount == 3 * 6, "a 40 px thumb over a 16 px rib tiles three times");
        check.Expect(v[VertexBatch.Floats * 2 + 3] == 0f && v[VertexBatch.Floats * 12 + 1] == 32f && v[VertexBatch.Floats * 14 + 1] == 40f,
                     "full repeats span the rib's height; the last stops at the rect");
        check.Expect(MathF.Abs(v[VertexBatch.Floats * 14 + 3] - 0.5f) < 1e-6f, "the 8 px tail samples half the rib");
        check.Expect(MathF.Abs(v[2] - 3f / 16f) < 1e-6f && MathF.Abs(v[VertexBatch.Floats + 2] - 13f / 16f) < 1e-6f, "u spans columns 3..13");
        log.Info(check.Ok ? "Skin.SelfCheck: passed" : "Skin.SelfCheck: FAILED");
    }
}
