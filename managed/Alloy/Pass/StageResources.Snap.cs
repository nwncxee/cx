using System;
using System.IO;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class StageResources
{
    /// <summary>Diagnostic, on the frame <c>/alloy snap</c> takes, right after the main view's sky: every live stage's
    /// images beside the snap as <c>{seq}_native_{image}.cxi</c> (a <c>CXI w h d</c> line, then level 0 as float RGBA,
    /// rows bottom-up), and a line per program on what its frame uniforms, samplers and images hold NOW against what
    /// they should. Cold: it stalls the frame it runs on.</summary>
    internal static void Snap(string dir, string seq, ExtenderLog? log)
    {
        var g = _gl;
        if (g.Count == 0 || log == null || Gl.GetIntegeri_v == null || Gl.GetTextureImage == null) return;
        log.Always($"StageResources: [snap] frame {AlloyRenderer.FrameSeq}, head {g.HeadSeq}, view {g.ViewW}x{g.ViewH} "
                 + $"noted at {g.ViewSeq}, overlay texture {OverlayTex()}");
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Stages[i] is not { } s) continue;
            log.Always($"StageResources: [snap] {s.Ref}: failed {s.Failed}, made {s.Made}, last use {s.LastUse}, "
                     + $"ran {s.RanFrame}, fresh {s.FreshFrame}");
            if (s.Failed || !s.Made) continue;
            foreach (Pass p in s.Passes)
            {
                log.Always($"StageResources: [snap] pass {p.Ref} ({p.Prog}):{Audit(p.Prog, p.Frame, p.Binds, p.Samplers, false)}");
                Uniforms(dir, $"{seq}_native_{p.Ref}.uni", p.Prog, log);
            }
            Dump(dir, seq, s, log);
        }
        foreach (Rec r in g.Recs)
        {
            log.Always($"StageResources: [snap] program {r.Prog} ({At(r.A)?.Ref ?? "-"}, {At(r.B)?.Ref ?? "-"}):"
                     + Audit(r.Prog, r.Frame, r.Binds, r.Samplers, true));
            if (At(r.A) is { } a) Uniforms(dir, $"{seq}_native_{a.Ref}_{r.Prog}.uni", r.Prog, log);
        }
    }

    /// <summary>Every plain-value uniform of one program as GL holds it now, a line each: <c>name f|i v...</c>, a
    /// matrix as its 16 (or 9) floats in GL's column order. <c>tools/sky/preview.py --uniforms</c> replays them.</summary>
    private static void Uniforms(string dir, string file, uint prog, ExtenderLog log)
    {
        if (Gl.GetActiveUniform == null || Gl.GetUniformfv == null) return;
        int count = 0;
        Gl.GetProgramiv(prog, Gl.ACTIVE_UNIFORMS, &count);
        var sb = new System.Text.StringBuilder();
        byte* name = stackalloc byte[256];
        float* f = stackalloc float[16];
        int* n = stackalloc int[4];
        for (uint u = 0; u < (uint)count; u++)
        {
            int len, size; uint type;
            Gl.GetActiveUniform(prog, u, 256, &len, &size, &type, name);
            (int comps, bool isInt) = type switch
            {
                Gl.FLOAT => (1, false), Gl.FLOAT_VEC2 => (2, false), Gl.FLOAT_VEC3 => (3, false), Gl.FLOAT_VEC4 => (4, false),
                Gl.FLOAT_MAT3 => (9, false), Gl.FLOAT_MAT4 => (16, false),
                Gl.INT => (1, true), Gl.INT_VEC2 => (2, true), Gl.INT_VEC3 => (3, true), Gl.INT_VEC4 => (4, true),
                _ => (0, false),
            };
            if (comps == 0) continue;
            string nm = System.Text.Encoding.ASCII.GetString(name, len);
            if (nm.EndsWith("[0]", StringComparison.Ordinal)) nm = nm[..^3];
            for (int e = 0; e < size; e++)
            {
                string el = size > 1 ? $"{nm}[{e}]" : nm;
                int loc = GlProgram.Loc(prog, el);
                if (loc < 0) continue;
                sb.Append(el).Append(isInt ? " i" : " f");
                if (isInt) { Gl.GetUniformiv(prog, loc, n); for (int c = 0; c < comps; c++) sb.Append(' ').Append(n[c]); }
                else
                {
                    Gl.GetUniformfv(prog, loc, f);
                    for (int c = 0; c < comps; c++) sb.Append(' ').Append(f[c].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }
        }
        try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, file), sb.ToString()); }
        catch (IOException e) { log.Always($"StageResources: [snap] {file} not written: {e.Message}"); }
    }

    /// <summary>One program's state as GL reads it back. A pass hands its image units back after it dispatches, so
    /// only a drawing program's (<paramref name="units"/>) are read: whatever bound them last is what a draw sees.</summary>
    private static string Audit(uint prog, FrameLoc fr, ImageBind[] images, ImageBind[] samplers, bool units)
    {
        var sb = new System.Text.StringBuilder();
        int* v = stackalloc int[4];
        if (fr.Frame >= 0) { Gl.GetUniformiv(prog, fr.Frame, v); sb.Append($" cxFrame={v[0]}"); }
        if (fr.Main >= 0) { Gl.GetUniformiv(prog, fr.Main, v); sb.Append($" cxMain={v[0]}"); }
        if (fr.Fresh >= 0) { Gl.GetUniformiv(prog, fr.Fresh, v); sb.Append($" cxFresh={v[0]}"); }
        if (fr.View >= 0) { Gl.GetUniformiv(prog, fr.View, v); sb.Append($" cxView={v[0]}x{v[1]}"); }
        if (fr.Overlay >= 0) { Gl.GetUniformiv(prog, fr.Overlay, v); sb.Append($" cxOverlay={v[0]}"); }
        foreach (ImageBind b in images)
        {
            Gl.GetUniformiv(prog, b.Loc, v);
            int held = -1;
            if (units) Gl.GetIntegeri_v(Gl.IMAGE_BINDING_NAME, b.Unit, &held);
            sb.Append($" | image {Named(b)}: unit {b.Unit}, uniform {v[0]}, holds {held}, wants {Want(b)}");
        }
        int active = 0;
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &active);
        foreach (ImageBind b in samplers)
        {
            Gl.GetUniformiv(prog, b.Loc, v);
            int held = -1;
            Gl.ActiveTexture(Gl.TEXTURE0 + b.Unit);
            bool vol = At(b.Stage) is { } s && s.Images[b.Image].Shape == Shape.Tex3d;
            Gl.GetIntegerv(vol ? Gl.TEXTURE_BINDING_3D : Gl.TEXTURE_BINDING_2D, &held);
            sb.Append($" | sampler {Named(b)}: unit {b.Unit}, uniform {v[0]}, holds {held}, wants {Want(b)}");
        }
        Gl.ActiveTexture((uint)active);
        return sb.ToString();
    }

    private static string Named(ImageBind b) => At(b.Stage) is { } s ? s.Images[b.Image].Name + (b.Prev ? "_prev" : "") : "?";

    private static uint Want(ImageBind b) => At(b.Stage) is { } s ? (b.Prev ? s.Images[b.Image].Prev : s.Images[b.Image].Tex) : 0;

    /// <summary>Every image half of one stage, level 0, to its own file.</summary>
    private static void Dump(string dir, string seq, Stage s, ExtenderLog log)
    {
        int pack = 0;
        Gl.GetIntegerv(Gl.PIXEL_PACK_BUFFER_BINDING, &pack);
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        try
        {
            Directory.CreateDirectory(dir);
            foreach (Img im in s.Images)
            {
                Write(dir, $"{seq}_native_{im.Name}.cxi", im, im.Tex);
                if (im.History) Write(dir, $"{seq}_native_{im.Name}_prev.cxi", im, im.Prev);
            }
            log.Always($"StageResources: [snap] {s.Ref}'s {s.Images.Length} image(s) written to {dir}");
        }
        catch (IOException e) { log.Always($"StageResources: [snap] {s.Ref}'s images not written: {e.Message}"); }
        finally { Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, (uint)pack); }
    }

    private static void Write(string dir, string file, Img im, uint tex)
    {
        int w = im.MadeW, h = im.MadeH, d = im.MadeD;
        var px = new byte[(long)w * h * d * 16];
        fixed (byte* p = px) Gl.GetTextureImage(tex, 0, Gl.RGBA, Gl.FLOAT, px.Length, p);
        using var f = File.Create(Path.Combine(dir, file));
        var hdr = System.Text.Encoding.ASCII.GetBytes($"CXI {w} {h} {d}\n");
        f.Write(hdr, 0, hdr.Length);
        f.Write(px, 0, px.Length);
    }
}
