using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b>The GL half of <see cref="StageResources"/>:</b> the declared images as real textures on units of their
/// own, the declared passes as compute programs, a host's program resolved against its stages' images by name, and the
/// frame's head -- remakes, history swaps, and every pass of the stages drawn -- before the first draw pass.</summary>
internal static unsafe partial class StageResources
{
    /// <summary>The entry points a native stage's images and passes need. Named one at a time, because "it fell
    /// back" with no reason is the failure this project refuses.</summary>
    private static bool EntryPoints(out string missing)
    {
        missing = Gl.CreateTextures == null ? "glCreateTextures"
                : Gl.TextureStorage2D == null ? "glTextureStorage2D"
                : Gl.TextureStorage3D == null ? "glTextureStorage3D"
                : Gl.TextureParameteri == null ? "glTextureParameteri"
                : Gl.GenerateTextureMipmap == null ? "glGenerateTextureMipmap"
                : Gl.BindTextureUnit == null ? "glBindTextureUnit"
                : Gl.BindImageTexture == null ? "glBindImageTexture"
                : Gl.DispatchCompute == null ? "glDispatchCompute"
                : Gl.MemoryBarrier == null ? "glMemoryBarrier"
                : Gl.GetActiveUniform == null ? "glGetActiveUniform"
                : Gl.GetUniformiv == null ? "glGetUniformiv"
                : Gl.GetProgramiv == null ? "glGetProgramiv"
                : Gl.GetIntegerv == null ? "glGetIntegerv"
                : Gl.DeleteTextures == null ? "glDeleteTextures" : "";
        return missing.Length == 0;
    }

    /// <summary>Compile every declared pass as a compute program, read its work-group size off the program, and
    /// resolve its images. A pass that is not in the chain, will not build, or links from the wrapper's STUB refuses
    /// the whole stage: a pass that writes nothing leaves an empty buffer in the picture, which is a silent fallback.</summary>
    private static bool BuildPasses(int id, ExtenderLog? log)
    {
        if (At(id) is not { } s || s.Failed) return false;
        string embedded = ShaderStages.Source(ShdPassCs);
        string over = ShaderResource.Override(ShdPassCs, log) ?? embedded;
        foreach (Pass p in s.Passes)
        {
            if (!VanillaShader.InChain(p.Ref))
            { Fail(id, $"pass {p.Ref}.shd is not in the resource chain (or alloy.vanillabridge=0)", log); return false; }
            string what = $"Alloy pass program ({s.Ref}: {p.Ref})";
            uint prog = Compute(over, p.Ref, log, what);
            if (prog == 0 && !ReferenceEquals(over, embedded))
            {
                log?.Info($"{what}: the {ShdPassCs} override did not build -- taking the embedded wrapper");
                prog = Compute(embedded, p.Ref, log, what);
            }
            if (prog == 0)
            { Fail(id, $"pass {p.Ref}.shd did not build as a compute program (see the compile log above)", log); return false; }
            p.Prog = prog;
            int* gs = stackalloc int[3] { 1, 1, 1 };
            Gl.GetProgramiv(prog, Gl.COMPUTE_WORK_GROUP_SIZE, gs);
            p.Gx = Math.Max(gs[0], 1); p.Gy = Math.Max(gs[1], 1); p.Gz = Math.Max(gs[2], 1);
            p.Env = StageEnvLoc.Locate(prog);
            p.Frame = FrameLoc.Locate(prog);
            if (!Resolve(prog, id, -1, PassImgUnits, out p.Binds, out p.Samplers, out string why)
                || !Point(prog, p.Samplers, p.Binds, out why))
            { Fail(id, $"pass {p.Ref}: {why}", log); return false; }
            log?.Info($"{what}: built, {p.Gx}x{p.Gy}x{p.Gz} work group, {p.Binds.Length} image unit(s), "
                    + $"{p.Samplers.Length} sampler(s)");
        }
        if (!MarkSwaps(s, out string refused) || !OverlayWritten(s, out refused)) { Fail(id, refused, log); return false; }
        return true;
    }

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

    /// <summary>One pass wrapper with the region's resref written in, built and refused if it fell back to the
    /// wrapper's stub. The override lookup is the CALLER's (as in <see cref="SkyStage"/>): the resref has to
    /// be in the text before the splice reads the marker.</summary>
    private static uint Compute(string wrapper, string resref, ExtenderLog? log, string what)
    {
        GlProgram.LastStubbed = false;
        uint prog = GlProgram.Compute(wrapper.Replace(CsToken, resref, StringComparison.Ordinal), log, what);
        if (prog == 0 || !GlProgram.LastStubbed) return prog;
        log?.Info($"{what}: the spliced pass fell back to the wrapper's stub, which dispatches nothing -- refused");
        Gl.DeleteProgram(prog);
        return 0;
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

    /// <summary>The frame's head, main view only, before the first draw pass: every live stage's images made at the
    /// sizes this view asks for (its <c>once</c> passes run over a remake), then -- for each stage a host drew last
    /// frame or staged this one -- its history swapped and its frame passes run in declaration order. Vulkan's is <see cref="VkRun"/>.</summary>
    internal static void RunFrame(int* viewport, ExtenderLog? log)
    {
        var g = _gl;
        int f = AlloyRenderer.FrameSeq;
        if (g.Count == 0 || g.HeadSeq == f) return;
        g.HeadSeq = f;
        // a Vulkan frame's head is VkRun, recorded into the frame's own list
        if (Renderer.OnVulkan) return;
        bool any = false;
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Stages[i] is not { Failed: false } s || !Ensure(i, s, viewport, log)) continue;
            if (s.LastUse >= f - 1)
            {
                if (Gap(s.RanFrame, f)) s.FreshFrame = f;
                s.RanFrame = f;
                foreach (Img im in s.Images)
                    if (im.Swap) (im.Tex, im.Prev) = (im.Prev, im.Tex);
                any = true;
            }
            // ponytail: every frame, two calls per image -- nothing else binds these units; bind only on a make or a
            // swap if a profile ever shows the calls
            BindUnits(s);
        }
        if (!any) return;
        // A draw that stored into an image last frame (what of a sun it showed) is read back by these passes.
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Stages[i] is not { Failed: false } s || s.RanFrame != f) continue;
            if (s.FreshFrame == f) s.FreshRuns++;
            RunPasses(i, s, viewport, once: false);
            if (g.FogStage == i) ReadFog(s, log);
        }
    }

    /// <summary>A stage's images, made at the sizes the declarations and the main view ask for, and its <c>once</c>
    /// passes run over them. Idempotent: a frame that changes nothing costs the extent compare below.</summary>
    private static bool Ensure(int id, Stage s, int* viewport, ExtenderLog? log)
    {
        var g = _gl;
        int vw = g.HaveView ? g.ViewW : Math.Max(viewport[2], 1);
        int vh = g.HaveView ? g.ViewH : Math.Max(viewport[3], 1);
        if (s.Made && !Stale(s, vw, vh)) return true;
        if (!Remake(s, vw, vh, out string why)) { Fail(id, why, log); return false; }
        s.Made = true; s.MadeW = vw; s.MadeH = vh;
        s.FreshFrame = AlloyRenderer.FrameSeq;
        if (s.Images.Length > 0)
            log?.Info($"StageResources: {s.Ref} made {s.Images.Length} image(s), {s.Bytes / (1024.0 * 1024.0):F1} MiB "
                    + $"(main view {vw}x{vh})");
        // The once passes read through the units, so the new textures go onto them first. Every once pass re-runs
        // whenever ANY image was remade: sorting out which of them wrote a view-sized one has nothing to gain.
        BindUnits(s);
        RunPasses(id, s, viewport, once: true);
        return true;
    }

    private static bool Stale(Stage s, int vw, int vh)
    {
        foreach (Img im in s.Images)
        {
            Extent(im, vw, vh, out int w, out int h, out int d);
            if (im.Tex == 0 || im.MadeW != w || im.MadeH != h || im.MadeD != d) return true;
            if (im.History && im.Prev == 0) return true;
        }
        return false;
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

    private static bool Remake(Stage s, int vw, int vh, out string why)
    {
        if (!Budget(s, vw, vh, out long bytes, out why)) return false;
        s.Bytes = bytes;
        foreach (Img im in s.Images)
        {
            Extent(im, vw, vh, out int w, out int h, out int d);
            if (im.Tex != 0 && im.MadeW == w && im.MadeH == h && im.MadeD == d
                && (!im.History || im.Prev != 0)) continue;
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, im.Tex);
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, im.Prev);
            im.Tex = Texture(im, w, h, d);
            im.Prev = im.History ? Texture(im, w, h, d) : 0u;
            if (im.Tex == 0 || (im.History && im.Prev == 0))
            { im.MadeW = im.MadeH = im.MadeD = 0; why = $"image `{im.Name}` ({w}x{h}x{d}) could not be made"; return false; }
            im.MadeW = w; im.MadeH = h; im.MadeD = d;
        }
        return true;
    }

    /// <summary>Levels and bytes of one image at an extent -- the mip chain summed rather than approximated,
    /// since it is what the budget is taken against.</summary>
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

    private static uint Texture(Img im, int w, int h, int d)
    {
        bool vol = im.Shape == Shape.Tex3d;
        uint tex;
        Gl.CreateTextures(vol ? Gl.TEXTURE_3D : Gl.TEXTURE_2D, 1, &tex);
        if (tex == 0) return 0;
        int levels = Levels(im, w, h, d);
        if (vol) Gl.TextureStorage3D(tex, levels, im.Format, w, h, d);
        else Gl.TextureStorage2D(tex, levels, im.Format, w, h);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, im.Mips ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR);
        int wrap = im.Clamp || im.Shape == Shape.View ? Gl.CLAMP_TO_EDGE : Gl.REPEAT;
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_S, wrap);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_T, wrap);
        if (vol) Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_R, wrap);
        // Zeroed, so a frame between the make and the first pass that writes it reads a known value rather
        // than whatever the allocation held; cxFresh is how a stage is told its history is that zero.
        if (Gl.ClearTexImage != null)
            for (int l = 0; l < levels; l++) Gl.ClearTexImage(tex, l, Gl.RGBA_, Gl.FLOAT, null);
        return tex;
    }

    /// <summary>A stage's images onto their own units, current half and last frame's.</summary>
    private static void BindUnits(Stage s)
    {
        foreach (Img im in s.Images)
        {
            Gl.BindTextureUnit(im.Unit, im.Tex);
            if (im.History) Gl.BindTextureUnit(im.UnitPrev, im.Prev);
        }
    }

    /// <summary>One RGBA float texel: what a <c>cx_fog</c> readback copies per frame.</summary>
    private const int FogBytes = 16;

    /// <summary>Read the <c>cx_fog</c> texel back without a stall: this frame's copy goes into one slot, and the slot
    /// written two frames ago -- its fence long passed -- becomes the fog's colour. A colour meant for the fog changes
    /// over minutes, so two frames late is the colour. Hot: no allocation, no wait.</summary>
    private static void ReadFog(Stage s, ExtenderLog? log)
    {
        var g = _gl;
        if (s.FogImage < 0 || g.FogMiss.Length > 0) return;
        Img im = s.Images[s.FogImage];
        if (im.Tex == 0) return;
        if (!GpuReadback.EntryPoints || Gl.GetTextureSubImage == null || !g.FogRead.Ensure(FogBytes))
        {
            g.FogMiss = "the driver gave no mapped readback buffer";
            log?.Always($"StageResources: {s.Ref}'s fog colour cannot be read back ({g.FogMiss}) -- the fog keeps the "
                      + "area's own colour");
            return;
        }
        int w = g.FogSeq % GpuReadback.Slots, r = (g.FogSeq + 1) % GpuReadback.Slots;
        float* got = (float*)g.FogRead.Ready(r);
        if (got != null && float.IsFinite(got[0]) && float.IsFinite(got[1]) && float.IsFinite(got[2]))
        {
            g.Fog = new Vector3(MathF.Max(got[0], 0f), MathF.Max(got[1], 0f), MathF.Max(got[2], 0f));
            g.HaveFog = true;
        }
        Gl.MemoryBarrier(Gl.TEXTURE_UPDATE_BARRIER_BIT | Gl.PIXEL_BUFFER_BARRIER_BIT);
        g.FogRead.ReadTexture(im.Tex, s.FogX, s.FogY, 1, 1, Gl.RGBA, Gl.FLOAT, w, FogBytes);
        g.FogRead.Fence(w);
        g.FogSeq++;
    }

    /// <summary>Dispatch every pass of one kind, then hand back the image units and the program that was current.
    /// Symmetric on its own, so the frame's draw passes find nothing of ours bound. Hot: no allocation.</summary>
    private static void RunPasses(int id, Stage s, int* viewport, bool once)
    {
        bool any = false;
        foreach (Pass p in s.Passes) if (p.Once == once && p.Prog != 0) { any = true; break; }
        if (!any) return;
        int prev = 0;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        foreach (Pass p in s.Passes) if (p.Once == once && p.Prog != 0) Dispatch(id, s, p, viewport);
        for (uint u = 0; u < PassImgUnits; u++) Gl.BindImageTexture(u, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA8);
        Gl.UseProgram((uint)prev);
    }

    /// <summary>Work groups over one extent: the division rounded UP, so the texels of a last partial group
    /// still get an invocation (and the stage's own bounds test is what keeps it inside the image).</summary>
    private static uint Groups(int extent, int group) => (uint)((extent + group - 1) / Math.Max(group, 1));

    private static void Dispatch(int id, Stage s, Pass p, int* viewport)
    {
        Img over = s.Images[p.Image];
        Gl.UseProgram(p.Prog);
        p.Env.Upload(viewport);
        if (p.Frame.Any) p.Frame.Upload(FrameFor(id, -1), main: true, OverlayTex() != 0);
        BindImages(p.Binds);
        Gl.DispatchCompute(Groups(over.MadeW, p.Gx), Groups(over.MadeH, p.Gy), Groups(over.MadeD, p.Gz));
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
        foreach (ImageBind b in p.Binds)
        {
            // The CURRENT half only: a `_prev` bound as an image is last frame's picture, and regenerating
            // its chain would filter a texture this pass did not write.
            Img im = s.Images[b.Image];
            if (!b.Prev && im.Mips && im.Tex != 0) Gl.GenerateTextureMipmap(im.Tex);
        }
        s.Runs++;
    }
}
