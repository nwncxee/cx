using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b>The host-side half of <see cref="StageResources"/>:</b> a host's program resolved against its stages'
/// declared images by name, the image units and frame uniforms that resolve puts on it, and the extent and budget
/// arithmetic the backend sizes those images by.</summary>
internal static unsafe partial class StageResources
{

    /// <summary>A <c>cx_overlay</c> image has to be one a FRAME pass writes as an image: the resolve adds it to every
    /// frame, and one nothing rewrites is the same picture whichever way the camera turns.</summary>
    private static bool OverlayWritten(Stage s, out string why)
    {
        why = "";
        if (s.OverlayImage < 0) return true;
        foreach (Pass p in s.Passes)
            if (!p.Once)
                foreach (ImageBind b in p.Binds)
                    if (!b.Prev && b.Image == s.OverlayImage) return true;
        why = $"the overlay image `{s.OverlayRef}` is not written as an image by any frame pass";
        return false;
    }

    /// <summary>Which images swap halves each frame, once every pass's image units are known: a <c>history</c> image
    /// a FRAME pass writes. A <c>history</c> image nothing rewrites each frame has no history to keep, and is
    /// refused with <paramref name="why"/>.</summary>
    private static bool MarkSwaps(Stage s, out string why)
    {
        why = "";
        foreach (Pass p in s.Passes)
            if (!p.Once)
                foreach (ImageBind b in p.Binds)
                    if (!b.Prev && s.Images[b.Image].History) s.Images[b.Image].Swap = true;
        foreach (Img im in s.Images)
            if (im.History && !im.Swap)
            {
                why = $"image `{im.Name}` is declared `history`, but no frame pass writes it as an image "
                    + "-- there is no history to keep";
                return false;
            }
        return true;
    }

    /// <summary>A program a host built and is about to draw with, from native stages <paramref name="a"/> and
    /// <paramref name="b"/> (-1: none) or from none: its samplers and images pointed at their units for good, and what
    /// its every bind has to do. -1 = nothing to do at a bind; -2 = refused, and the native stages failed with it.</summary>
    internal static int Attach(uint prog, int a, int b, ExtenderLog? log)
    {
        if (prog == 0) return -1;
        var frame = FrameLoc.Locate(prog);
        if (!Resolve(prog, a, b, DrawImgUnits, out ImageBind[] binds, out ImageBind[] samplers, out string why)
            || !Point(prog, samplers, binds, out why))
        {
            Fail(a, why, log);
            Fail(b, why, log);
            return -2;
        }
        if (binds.Length == 0 && !frame.Any) return -1;
        var g = _gl;
        g.Recs.Add(new Rec { Prog = prog, A = a, B = b, Binds = binds, Samplers = samplers, Frame = frame });
        return g.Recs.Count - 1;
    }

    /// <summary>Samplers and images are program state: set once, a sampler reads its image's texture unit and an image
    /// its image unit from every pass that draws the program. Each is read back, because one left at its default of 0
    /// reads whatever unit 0 holds with no error at all. Cold -- at a program's build; hands back the current program.</summary>
    private static bool Point(uint prog, ImageBind[] samplers, ImageBind[] images, out string why)
    {
        why = "";
        if (samplers.Length == 0 && images.Length == 0) return true;
        int prev = 0;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        Gl.UseProgram(prog);
        foreach (ImageBind b in samplers) Gl.Uniform1i(b.Loc, (int)b.Unit);
        foreach (ImageBind b in images) Gl.Uniform1i(b.Loc, (int)b.Unit);
        Gl.UseProgram((uint)prev);
        foreach (ImageBind b in samplers)
            if (!Pointed(prog, b.Loc, b.Unit, out why)) return false;
        foreach (ImageBind b in images)
            if (!Pointed(prog, b.Loc, b.Unit, out why)) return false;
        return true;
    }

    private static bool Pointed(uint prog, int loc, uint unit, out string why)
    {
        int got = -1;
        Gl.GetUniformiv(prog, loc, &got);
        why = got == (int)unit ? "" : $"the uniform at location {loc} reads unit {got} after it was pointed at {unit}";
        return why.Length == 0;
    }

    /// <summary>Resolve stages <paramref name="a"/> and <paramref name="b"/>'s images against one program's ACTIVE
    /// uniforms: by name, and by the type the program declared them with -- a sampler reads the image's own texture
    /// unit, an image takes an image unit below <paramref name="imgUnits"/>, and the shape has to agree. Build time.</summary>
    private static bool Resolve(uint prog, int a, int b, uint imgUnits, out ImageBind[] binds,
                                out ImageBind[] samplers, out string why)
    {
        binds = [];
        samplers = [];
        why = "";
        if (!Live(a) && !Live(b)) return true;
        int n = 0, maxLen = 0;
        Gl.GetProgramiv(prog, Gl.ACTIVE_UNIFORMS, &n);
        Gl.GetProgramiv(prog, Gl.ACTIVE_UNIFORM_MAX_LENGTH, &maxLen);
        if (n <= 0) return true;
        var active = new Dictionary<string, uint>(n, StringComparer.Ordinal);
        byte[] buf = new byte[Math.Max(maxLen, 2) + 1];
        for (uint i = 0; i < (uint)n; i++)
        {
            int len = 0, size = 0; uint type = 0;
            fixed (byte* p = buf) Gl.GetActiveUniform(prog, i, buf.Length, &len, &size, &type, p);
            if (len <= 0) continue;
            string name = System.Text.Encoding.ASCII.GetString(buf, 0, len);
            int bracket = name.IndexOf('[');          // an array uniform comes back as `name[0]`
            if (bracket > 0) name = name[..bracket];
            active[name] = type;
        }
        var list = new List<ImageBind>();
        var read = new List<ImageBind>();
        var from = new Dictionary<string, string>(StringComparer.Ordinal);
        uint img = 0;
        foreach (int id in new[] { a, b })
        {
            if (At(id) is not { Failed: false } s) continue;
            for (int i = 0; i < s.Images.Length; i++)
            {
                Img im = s.Images[i];
                for (int half = 0; half < 2; half++)
                {
                    bool prev = half == 1;
                    string name = prev ? im.Name + "_prev" : im.Name;
                    if (!active.TryGetValue(name, out uint type)) continue;
                    if (from.TryGetValue(name, out string? first))
                    { why = $"`{name}` is declared by both {first} and {s.Ref}: declare it in one of them"; return false; }
                    from[name] = s.Ref;
                    if (prev && !im.History)
                    { why = $"the program reads `{name}`, but `{im.Name}` is not declared `history`"; return false; }
                    bool isImage = type is Gl.IMAGE_2D or Gl.IMAGE_3D;
                    bool wants3d = type is Gl.SAMPLER_3D or Gl.IMAGE_3D;
                    if (!isImage && type is not (Gl.SAMPLER_2D or Gl.SAMPLER_3D))
                    { why = $"`{name}` is declared as uniform type 0x{type:x}, not a sampler2D/sampler3D/image2D/image3D"; return false; }
                    if (wants3d != (im.Shape == Shape.Tex3d))
                    { why = $"`{name}` is declared {(wants3d ? "3D" : "2D")} but its cx_image is {(im.Shape == Shape.Tex3d ? "3d" : "2d")}"; return false; }
                    // The location first: an enumerated uniform with none is nothing to bind, and taking a unit
                    // for it would spend the budget on a name no frame ever writes.
                    int loc = GlProgram.Loc(prog, name);
                    if (loc < 0) continue;
                    if (!isImage)
                    { read.Add(new ImageBind { Loc = loc, Stage = id, Image = i, Prev = prev, Unit = prev ? im.UnitPrev : im.Unit }); continue; }
                    if (img >= imgUnits) { why = $"more than {imgUnits} image units are needed"; return false; }
                    list.Add(new ImageBind { Loc = loc, Stage = id, Image = i, Prev = prev, Unit = img++ });
                }
            }
        }
        binds = list.ToArray();
        samplers = read.ToArray();
        return true;
    }

    /// <summary>A host bound an attached program: its stages are drawn this frame, and its image units and the frame
    /// uniforms go onto the program now current. An image unit a draw program takes is left bound -- every Alloy compute
    /// pass binds its own before it dispatches. A bind ahead of the frame's head (the shadow bracket) reads the frame
    /// before's camera and images. Hot: once per program per pass, no allocation.</summary>
    internal static void Bind(int rec)
    {
        var g = _gl;
        if ((uint)rec >= (uint)g.Recs.Count) return;
        Rec r = g.Recs[rec];
        Use(r.A);
        Use(r.B);
        BindImages(r.Binds);
        if (r.Frame.Any) r.Frame.Upload(FrameFor(r.A, r.B), !AlloyRenderer.Capturing, OverlayTex() != 0);
    }

    private static void BindImages(ImageBind[] binds)
    {
        foreach (ImageBind b in binds)
        {
            if (At(b.Stage) is not { Failed: false } s)
            {
                Gl.BindImageTexture(b.Unit, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA8);
                continue;
            }
            Img im = s.Images[b.Image];
            Gl.BindImageTexture(b.Unit, b.Prev ? im.Prev : im.Tex, 0, im.Shape == Shape.Tex3d ? (byte)1 : (byte)0, 0,
                                Gl.READ_WRITE, im.Format);
        }
    }

    /// <summary>A declared image's extent in this view: its own for <c>2d</c>/<c>3d</c>, the main view's
    /// render size over the divisor (rounded up) for <c>view</c>.</summary>
    private static void Extent(Img im, int vw, int vh, out int w, out int h, out int d)
    {
        if (im.Shape == Shape.View)
        { w = (vw + im.Div - 1) / im.Div; h = (vh + im.Div - 1) / im.Div; d = 1; }
        else { w = im.W; h = im.H; d = im.D; }
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (d < 1) d = 1;
    }

    /// <summary>One stage's images at this view, mips and history halves included. A view extent of 0 means "no main
    /// view measured yet", and the view-sized images are left out.</summary>
    private static long StageBytes(Stage s, int vw, int vh)
    {
        long bytes = 0;
        foreach (Img im in s.Images)
        {
            if (im.Shape == Shape.View && (vw <= 0 || vh <= 0)) continue;
            Extent(im, vw, vh, out int w, out int h, out int d);
            bytes += Bytes(im, w, h, d) * (im.History ? 2 : 1);
        }
        return bytes;
    }

    /// <summary>What <paramref name="s"/>'s images come to at this view, and whether every live stage's together stay
    /// inside <see cref="MaxImageBytes"/>. Past it, <paramref name="s"/> is the one refused.</summary>
    private static bool Budget(Stage s, int vw, int vh, out long bytes, out string why)
    {
        var g = _gl;
        why = "";
        bytes = StageBytes(s, vw, vh);
        long total = bytes;
        for (int i = 0; i < g.Count; i++)
            if (g.Stages[i] is { Failed: false } t && !ReferenceEquals(t, s)) total += StageBytes(t, vw, vh);
        if (total <= MaxImageBytes) return true;
        why = $"its images come to {bytes / (1024.0 * 1024.0):F1} MiB, and every native stage's together "
            + $"{total / (1024.0 * 1024.0):F1} MiB, past the budget of {MaxImageBytes / (1024 * 1024)} MiB";
        return false;
    }

    /// <summary>Levels of one image at an extent -- the mip chain counted rather than approximated, since the
    /// budget is taken against it.</summary>
    private static int Levels(Img im, int w, int h, int d)
    {
        if (!im.Mips) return 1;
        int side = Math.Max(w, Math.Max(h, im.Shape == Shape.Tex3d ? d : 1)), n = 1;
        while (side > 1) { side >>= 1; n++; }
        return n;
    }

    private static long Bytes(Img im, int w, int h, int d)
    {
        long total = 0;
        int levels = Levels(im, w, h, d);
        for (int l = 0; l < levels; l++)
        {
            total += (long)Math.Max(w >> l, 1) * Math.Max(h >> l, 1)
                   * (im.Shape == Shape.Tex3d ? Math.Max(d >> l, 1) : 1) * im.Bpp;
        }
        return total;
    }

    /// <summary>Work groups over one extent: the division rounded UP, so the texels of a last partial group
    /// still get an invocation (and the stage's own bounds test is what keeps it inside the image).</summary>
    private static uint Groups(int extent, int group) => (uint)((extent + group - 1) / Math.Max(group, 1));
}
