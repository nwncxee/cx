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
                log.Always($"StageResources: [snap] pass {p.Ref} ({p.Prog}):{Audit(p.Prog, p.Frame, p.Binds, p.Samplers, false)}");
            Dump(dir, seq, s, log);
        }
        foreach (Rec r in g.Recs)
            log.Always($"StageResources: [snap] program {r.Prog} ({At(r.A)?.Ref ?? "-"}, {At(r.B)?.Ref ?? "-"}):"
                     + Audit(r.Prog, r.Frame, r.Binds, r.Samplers, true));
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
