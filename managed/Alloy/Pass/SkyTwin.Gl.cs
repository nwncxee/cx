using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b>The GL half of <see cref="SkyTwin"/>:</b> the declared images as real textures, the declared
/// passes as compute programs, and the per-frame bind and dispatch. Every image is resolved BY NAME against
/// each program's active uniforms once at build -- location, type and unit -- so a frame only binds.</summary>
internal static unsafe partial class SkyTwin
{
    /// <summary>The entry points a twin's images and passes need. Named one at a time, because "the sky fell
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
                : Gl.GetProgramiv == null ? "glGetProgramiv"
                : Gl.GetIntegerv == null ? "glGetIntegerv"
                : Gl.DeleteTextures == null ? "glDeleteTextures" : "";
        return missing.Length == 0;
    }

    /// <summary>Compile every declared pass as a compute program, read its work-group size off the program,
    /// and resolve its image bindings. A pass that is not in the chain, will not build, or links from the
    /// wrapper's STUB refuses the whole twin: a pass that writes nothing leaves an empty buffer in the sky,
    /// which is a silent fallback.</summary>
    internal static bool BuildPasses(int id, ExtenderLog? log)
    {
        if (At(id) is not { } t || t.Failed) return false;
        if (!EntryPoints(out string missing)) { Fail(id, $"the driver exposes no {missing}", log); return false; }
        string embedded = ShaderStages.Source(ShdPassCs);
        string over = ShaderResource.Override(ShdPassCs, log) ?? embedded;
        foreach (Pass p in t.Passes)
        {
            if (!VanillaShader.InChain(p.Ref))
            { Fail(id, $"pass {p.Ref}.shd is not in the resource chain (or alloy.vanillabridge=0)", log); return false; }
            string what = $"Alloy sky pass program ({t.Ref}: {p.Ref})";
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
            p.Env = SkyEnvLoc.Locate(prog);
            if (!Resolve(t, prog, out p.Binds, out string why)) { Fail(id, $"pass {p.Ref}: {why}", log); return false; }
            log?.Info($"{what}: built, {p.Gx}x{p.Gy}x{p.Gz} work group, {p.Binds.Length} image binding(s)");
        }
        if (!MarkSwaps(t, out string refused) || !LensWritten(t, out refused)) { Fail(id, refused, log); return false; }
        return true;
    }

    /// <summary>A <c>cx_sky_lens</c> image has to be one a FRAME pass writes as an image: the resolve adds it to
    /// every frame, and one nothing rewrites is the same flare whichever way the camera turns.</summary>
    private static bool LensWritten(Twin t, out string why)
    {
        why = "";
        if (t.LensImage < 0) return true;
        foreach (Pass p in t.Passes)
            if (!p.Once)
                foreach (Bind b in p.Binds)
                    if (b.IsImage && !b.Prev && b.Image == t.LensImage) return true;
        why = $"the lens image `{t.LensRef}` is not written as an image by any frame pass";
        return false;
    }

    /// <summary>Which images swap halves each frame, once every pass's bindings are known: a <c>history</c>
    /// image a FRAME pass writes. A <c>history</c> image nothing rewrites each frame has no history to keep,
    /// and is refused with <paramref name="why"/>.</summary>
    private static bool MarkSwaps(Twin t, out string why)
    {
        why = "";
        foreach (Pass p in t.Passes)
            if (!p.Once)
                foreach (Bind b in p.Binds)
                    if (b.IsImage && !b.Prev && t.Images[b.Image].History) t.Images[b.Image].Swap = true;
        foreach (Img im in t.Images)
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

    /// <summary>Resolve the twin's declared images against one program's ACTIVE uniforms: by name, and by the
    /// type the program declared them with -- a sampler takes a texture unit, an image an image unit, and the
    /// shape has to agree with the declaration. Build time; the per-frame path only walks the result.</summary>
    internal static bool Locate(int id, uint prog, ExtenderLog? log)
    {
        if (At(id) is not { } t || t.Failed) return false;
        if (Resolve(t, prog, out t.Draw, out string why)) return true;
        Fail(id, $"the sky program: {why}", log);
        return false;
    }

    private static bool Resolve(Twin t, uint prog, out Bind[] binds, out string why)
    {
        binds = Array.Empty<Bind>();
        why = "";
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
        var list = new List<Bind>();
        uint tex = 0, img = 0;
        for (int i = 0; i < t.Images.Length; i++)
        {
            Img im = t.Images[i];
            for (int half = 0; half < 2; half++)
            {
                bool prev = half == 1;
                string name = prev ? im.Name + "_prev" : im.Name;
                if (!active.TryGetValue(name, out uint type)) continue;
                if (prev && !im.History)
                { why = $"the program reads `{name}`, but `{im.Name}` is not declared `history`"; return false; }
                bool isImage = type is Gl.IMAGE_2D or Gl.IMAGE_3D;
                bool wants3d = type is Gl.SAMPLER_3D or Gl.IMAGE_3D;
                if (!isImage && type is not (Gl.SAMPLER_2D or Gl.SAMPLER_3D))
                { why = $"`{name}` is declared as uniform type 0x{type:x}, not a sampler2D/sampler3D/image2D/image3D"; return false; }
                if (wants3d != (im.Shape == Shape.Tex3d))
                { why = $"`{name}` is declared {(wants3d ? "3D" : "2D")} but its cx_sky_image is {(im.Shape == Shape.Tex3d ? "3d" : "2d")}"; return false; }
                // The location first: an enumerated uniform with none is nothing to bind, and taking a unit
                // for it would spend the budget on a name no frame ever writes.
                int loc = GlProgram.Loc(prog, name);
                if (loc < 0) continue;
                uint unit;
                if (isImage)
                {
                    if (img >= ImgUnitCount) { why = $"more than {ImgUnitCount} image units are needed"; return false; }
                    unit = img++;
                }
                else
                {
                    if (tex >= TexUnitCount) { why = $"more than {TexUnitCount} texture units are needed"; return false; }
                    unit = TexUnit0 + tex++;
                }
                list.Add(new Bind { Loc = loc, Image = i, Prev = prev, IsImage = isImage, Unit = unit });
            }
        }
        binds = list.ToArray();
        return true;
    }

    /// <summary>The twin's images, made at the sizes the declarations and THIS view ask for, and its
    /// <c>once</c> passes run over them -- at the first point in a frame the twin is about to be used, so no
    /// draw and no frame pass can reach an unmade image. Idempotent: a frame that changes nothing costs the
    /// extent compare below.</summary>
    internal static bool Ensure(int id, int* viewport, ExtenderLog? log)
    {
        if (At(id) is not { } t || t.Failed) return false;
        var g = _gl;
        // Before the first main-view frame of a session, a probe face is the first thing to want the images;
        // its own viewport sizes them, and the main view's NoteView remakes them if it differs.
        int vw = g.HaveView ? g.ViewW : Math.Max(viewport[2], 1);
        int vh = g.HaveView ? g.ViewH : Math.Max(viewport[3], 1);
        if (t.Made && !Stale(t, vw, vh)) return true;
        // No MarkStale here: the latch has to outlive the rebuild it would trigger, or a deterministic
        // refusal (an extent past the byte budget, a texture the driver will not make) re-parses, recompiles
        // and says so again every single frame. SkyStage replants this one entry on the plain stage instead.
        if (!Remake(t, vw, vh, out string why)) { Fail(id, why, log); return false; }
        t.Made = true; t.MadeW = vw; t.MadeH = vh;
        t.FreshFrame = AlloyRenderer.FrameSeq;
        log?.Info($"SkyTwin: {t.Ref} made {t.Images.Length} image(s), {t.Bytes / (1024.0 * 1024.0):F1} MiB "
                + $"(main view {vw}x{vh})");
        // Every once-pass re-runs whenever ANY image was remade: a once-pass that writes a view-sized image
        // has to, and sorting out which of them do is a bookkeeping job with nothing to gain from it.
        RunPasses(t, viewport, once: true, main: !AlloyRenderer.Capturing);
        return true;
    }

    private static bool Stale(Twin t, int vw, int vh)
    {
        foreach (Img im in t.Images)
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

    /// <summary>What the twin's images come to at this view, against <see cref="MaxImageBytes"/>. A view
    /// extent of 0 means "no main view measured yet", and the view-sized images are left out of the sum --
    /// the declaration's own fixed-size images are still a build-time fact and are taken as one.</summary>
    private static bool Budget(Twin t, int vw, int vh, out long bytes, out string why)
    {
        why = "";
        bytes = 0;
        foreach (Img im in t.Images)
        {
            if (im.Shape == Shape.View && (vw <= 0 || vh <= 0)) continue;
            Extent(im, vw, vh, out int w, out int h, out int d);
            bytes += Bytes(im, w, h, d) * (im.History ? 2 : 1);
        }
        if (bytes <= MaxImageBytes) return true;
        why = $"its images come to {bytes / (1024.0 * 1024.0):F1} MiB, past the budget of "
            + $"{MaxImageBytes / (1024 * 1024)} MiB";
        return false;
    }

    private static bool Remake(Twin t, int vw, int vh, out string why)
    {
        if (!Budget(t, vw, vh, out long bytes, out why)) return false;
        t.Bytes = bytes;
        foreach (Img im in t.Images)
        {
            Extent(im, vw, vh, out int w, out int h, out int d);
            if (im.Tex != 0 && im.MadeW == w && im.MadeH == h && im.MadeD == d
                && (!im.History || im.Prev != 0)) continue;
            uint a = im.Tex, b = im.Prev;
            if (a != 0) Gl.DeleteTextures(1, &a);
            if (b != 0) Gl.DeleteTextures(1, &b);
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
        // than whatever the allocation held; cxSkyFresh is how a stage is told its history is that zero.
        if (Gl.ClearTexImage != null)
            for (int l = 0; l < levels; l++) Gl.ClearTexImage(tex, l, Gl.RGBA_, Gl.FLOAT, null);
        return tex;
    }

    /// <summary>The twin's FRAME passes, once per presented frame and for the main view only: the history
    /// images swap, then each pass runs in declaration order. Called before the sky's own GPU term opens --
    /// TIME_ELAPSED cannot nest.</summary>
    internal static void RunFrame(int id, int* viewport, ExtenderLog? log)
    {
        if (At(id) is not { } t || t.Failed) return;
        if (t.RanFrame == AlloyRenderer.FrameSeq) return;
        // Once per presented frame, before any twin's passes: the camera pair the whole frame reads.
        Roll();
        if (!Ensure(id, viewport, log)) return;
        t.RanFrame = AlloyRenderer.FrameSeq;
        foreach (Img im in t.Images)
            if (im.Swap) (im.Tex, im.Prev) = (im.Prev, im.Tex);
        // A dome that stores into an image (what of the sun it showed) is read back by this frame's passes.
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        RunPasses(t, viewport, once: false, main: true);
        ReadFog(t, log);
    }

    /// <summary>One RGBA float texel: what a <c>cx_sky_fog</c> readback copies per frame.</summary>
    private const int FogBytes = 16;

    /// <summary>Read the <c>cx_sky_fog</c> texel back without a stall: this frame's copy goes into one slot, and
    /// the slot written two frames ago -- its fence long passed -- becomes the fog's sky colour. A sky changes
    /// over minutes, so two frames late is the colour. Hot: no allocation, no wait.</summary>
    private static void ReadFog(Twin t, ExtenderLog? log)
    {
        if (t.FogImage < 0 || t.FogMiss.Length > 0) return;
        Img im = t.Images[t.FogImage];
        if (im.Tex == 0) return;
        if (!GpuReadback.EntryPoints || Gl.GetTextureSubImage == null || !t.FogRead.Ensure(FogBytes))
        {
            t.FogMiss = "the driver gave no mapped readback buffer";
            log?.Always($"SkyTwin: {t.Ref}'s fog colour cannot be read back ({t.FogMiss}) -- the fog keeps the "
                      + "area's own colour");
            return;
        }
        int w = t.FogSeq % GpuReadback.Slots, r = (t.FogSeq + 1) % GpuReadback.Slots;
        float* got = (float*)t.FogRead.Ready(r);
        if (got != null && float.IsFinite(got[0]) && float.IsFinite(got[1]) && float.IsFinite(got[2]))
        {
            t.Fog = new Vector3(MathF.Max(got[0], 0f), MathF.Max(got[1], 0f), MathF.Max(got[2], 0f));
            t.HaveFog = true;
        }
        Gl.MemoryBarrier(Gl.TEXTURE_UPDATE_BARRIER_BIT | Gl.PIXEL_BUFFER_BARRIER_BIT);
        t.FogRead.ReadTexture(im.Tex, t.FogX, t.FogY, 1, 1, Gl.RGBA, Gl.FLOAT, w, FogBytes);
        t.FogRead.Fence(w);
        t.FogSeq++;
    }

    /// <summary>Dispatch every pass of one kind, then hand back the image block, the texture units these
    /// passes themselves bound and the program that was current. Symmetric on its own, so a caller that
    /// returns before the sky draws anything leaves nothing of ours bound. Hot: no allocation.</summary>
    private static void RunPasses(Twin t, int* viewport, bool once, bool main)
    {
        bool any = false;
        foreach (Pass p in t.Passes) if (p.Once == once && p.Prog != 0) { any = true; break; }
        if (!any) return;
        var g = _gl;
        uint held = g.TexMask;
        int prev = 0;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        foreach (Pass p in t.Passes) if (p.Once == once && p.Prog != 0) Dispatch(t, p, viewport, main);
        UnbindImages();
        UnbindTex(g.TexMask & ~held);
        g.TexMask = held;
        Gl.UseProgram((uint)prev);
    }

    /// <summary>Work groups over one extent: the division rounded UP, so the texels of a last partial group
    /// still get an invocation (and the stage's own bounds test is what keeps it inside the image).</summary>
    private static uint Groups(int extent, int group) => (uint)((extent + group - 1) / Math.Max(group, 1));

    private static void Dispatch(Twin t, Pass p, int* viewport, bool main)
    {
        Img over = t.Images[p.Image];
        Gl.UseProgram(p.Prog);
        p.Env.Upload(viewport, main, FrameFor(t));
        BindUnits(t, p.Binds);
        Gl.DispatchCompute(Groups(over.MadeW, p.Gx), Groups(over.MadeH, p.Gy), Groups(over.MadeD, p.Gz));
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
        foreach (Bind b in p.Binds)
        {
            // The CURRENT half only: a `_prev` bound as an image is last frame's picture, and regenerating
            // its chain would filter a texture this pass did not write.
            Img im = t.Images[b.Image];
            if (b.IsImage && !b.Prev && im.Mips && im.Tex != 0) Gl.GenerateTextureMipmap(im.Tex);
        }
        t.Runs++;
    }

    /// <summary>The dome's own fragment program: its declared images onto the units resolved at build. Hot --
    /// once per sky program per pass, right after <c>glUseProgram</c>.</summary>
    internal static void BindDraw(int id)
    {
        if (At(id) is not { } t || t.Failed) return;
        BindUnits(t, t.Draw);
    }

    private static void BindUnits(Twin t, Bind[] binds)
    {
        var g = _gl;
        foreach (Bind b in binds)
        {
            Img im = t.Images[b.Image];
            uint tex = b.Prev ? im.Prev : im.Tex;
            if (b.IsImage)
            {
                Gl.BindImageTexture(b.Unit, tex, 0, im.Shape == Shape.Tex3d ? (byte)1 : (byte)0, 0,
                                    Gl.READ_WRITE, im.Format);
                g.ImgMask |= 1u << (int)b.Unit;
            }
            else
            {
                Gl.BindTextureUnit(b.Unit, tex);
                g.TexMask |= 1u << (int)b.Unit;
            }
            Gl.Uniform1i(b.Loc, (int)b.Unit);
        }
    }

    private static void UnbindImages()
    {
        var g = _gl;
        if (g.ImgMask == 0) return;
        for (uint u = 0; u < ImgUnitCount; u++)
            if ((g.ImgMask & (1u << (int)u)) != 0) Gl.BindImageTexture(u, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA8);
        g.ImgMask = 0;
    }

    /// <summary>Hand every unit the sky's programs took back, at the end of the sky pass. The texture units
    /// are all rebound by the pass that reads them before its next draw, but they go back to texture 0
    /// anyway -- except 10 and 11, which carry the dynamic shadow ARRAYS: a shadow sampler validated against
    /// an EMPTY unit is undefined even where the stage never takes the fetch, so those two are republished.</summary>
    internal static void Unbind()
    {
        var g = _gl;
        UnbindImages();
        UnbindTex(g.TexMask);
        g.TexMask = 0;
    }

    private static void UnbindTex(uint mask)
    {
        if (mask == 0) return;
        bool shadow = (mask & ((1u << (int)TextureSlots.SunShadowDyn)
                            | (1u << (int)TextureSlots.LocalShadowDyn))) != 0;
        for (uint u = TexUnit0; u < TexUnit0 + TexUnitCount; u++)
            if ((mask & (1u << (int)u)) != 0) Gl.BindTextureUnit(u, 0);
        if (shadow) { AlloyRenderer.BindSunMaps(); AlloyRenderer.BindCubeMaps(); }
        Gl.ActiveTexture(Gl.TEXTURE0);   // the unit every pass leaves current
    }
}
