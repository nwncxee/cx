using CX.Alloy;   // AlloyGate: a refused render target is a hard Alloy failure

namespace CX.Graphics;

/// <summary>
/// Allocating the textures an Alloy pass renders to or copies into. Three passes wrote this out
/// longhand, and the depth half -- the internal-format-to-(format, type) table -- was byte-identical in two
/// of them.
/// </summary>
public static unsafe class RenderTarget
{
    /// <summary>Does this depth internal format carry stencil? Decides both the upload format and which
    /// attachment point the texture goes on; getting it wrong makes the framebuffer incomplete.</summary>
    public static bool Packed(uint depthFormat)
        => depthFormat == Gl.DEPTH24_STENCIL8 || depthFormat == Gl.DEPTH32F_STENCIL8;

    /// <summary>Clamp-to-edge on <paramref name="tex"/>. Wrapping a screen-sized copy only invents data at the
    /// edges. The filter is the caller's: a copy read 1:1 wants NEAREST, one read at an offset (refraction, a
    /// screen-space march) wants LINEAR.</summary>
    public static void Clamp(uint tex, int filter = Gl.NEAREST)
    {
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, filter);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAG_FILTER, filter);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
    }

    /// <summary>A fresh immutable TEXTURE_2D in <paramref name="tex"/>, replacing the old name: immutable storage
    /// cannot be respecified, so a resize is always a new name and the caller re-attaches it. Storage the driver
    /// refuses (video memory full) keeps the old texture and fails Alloy loudly.</summary>
    public static void Create(ref uint tex, uint internalFormat, int w, int h, int filter)
    {
        uint t, old = tex;
        // Create before delete: GL may hand a freed name straight back, and a caller that caches the name to
        // detect a re-create (Highlight's depth attachment) would miss the change.
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.TextureStorage2D(t, 1, internalFormat, w, h);
        if (!HasStorage(t))
        {
            Gl.DeleteTextures(1, &t);
            AlloyGate.Fail($"the driver refused a {w}x{h} render target (format 0x{internalFormat:x}) -- video memory "
                         + "is full; close other GPU programs");
            return;
        }
        if (old != 0) Gl.DeleteTextures(1, &old);
        Clamp(t, filter);
        tex = t;
    }

    /// <summary>Did <paramref name="tex"/>'s storage call take? A refused one (OUT_OF_MEMORY) leaves level 0 at
    /// width 0, and a target with no storage behind it is a GPU page fault at address 0 the moment it is drawn.
    /// Asked of the texture rather than <c>glGetError</c>, which would eat errors the 4.6 counter reports.</summary>
    private static bool HasStorage(uint tex)
    {
        if (Gl.GetTextureLevelParameteriv == null) return true;
        int w = 0;
        Gl.GetTextureLevelParameteriv(tex, 0, Gl.TEXTURE_WIDTH, &w);
        return w > 0;
    }

    /// <summary>(Re)create <paramref name="tex"/> as a depth target. Also clears TEXTURE_COMPARE_MODE: these
    /// copies are SAMPLED, never compared, and a driver that defaults a depth format to COMPARE_REF_TO_TEXTURE
    /// hands back 0 or 1 instead of the stored depth.</summary>
    public static void AllocDepth(ref uint tex, uint depthFormat, int w, int h, int filter = Gl.NEAREST)
    {
        Create(ref tex, depthFormat, w, h, filter);
        Gl.TextureParameteri(tex, Gl.TEXTURE_COMPARE_MODE, (int)Gl.NONE);
    }

    /// <summary>(Re)create <paramref name="tex"/> as a colour target. <paramref name="hdr"/> picks RGBA16F over
    /// RGBA8, which must track what the copy SOURCE holds: an 8-bit copy of a linear HDR frame clips every
    /// highlight, and those are exactly the pixels a reflection is made of.</summary>
    public static void AllocColor(ref uint tex, bool hdr, int w, int h, int filter = Gl.NEAREST)
        => Create(ref tex, hdr ? Gl.RGBA16F : Gl.RGBA8, w, h, filter);

    /// <summary>One parameter of the bound FRAMEBUFFER's attachment, or 0 when nothing is attached there: any
    /// pname but OBJECT_TYPE/NAME on a NONE attachment raises INVALID_OPERATION (GL 4.6 9.2.3), which the old
    /// probe drained -- along with whatever unrelated error was queued ahead of it.</summary>
    public static int AttachmentParam(uint attachment, uint pname)
    {
        int type = 0, v = 0;
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, attachment, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &type);
        if (type != 0) Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, attachment, pname, &v);
        return v;
    }

    /// <summary>The depth internal format the bound FRAMEBUFFER's depth attachment holds, 0 when it has none.
    /// Asks the OBJECT for its format rather than the attachment for its bit widths: an owned framebuffer
    /// attaches depth at DEPTH_ATTACHMENT alone, so a packed D24S8 would read as a plain 24-bit one and the
    /// blit mismatches. The default framebuffer (<paramref name="fbo"/> 0) has no object, so it falls back.</summary>
    public static uint ProbeDepthFormat(uint fbo)
    {
        int type = 0, name = 0, fmt = 0;
        int bits = AttachmentParam(fbo == 0 ? Gl.DEPTH : Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE);
        int stencil = AttachmentParam(fbo == 0 ? Gl.STENCIL : Gl.STENCIL_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE);
        if (fbo != 0)
        {
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT,
                Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &type);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT,
                Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &name);
            if (name != 0 && type == (int)Gl.RENDERBUFFER)
                Gl.GetNamedRenderbufferParameteriv((uint)name, Gl.RENDERBUFFER_INTERNAL_FORMAT, &fmt);
            else if (name != 0)
                Gl.GetTextureLevelParameteriv((uint)name, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        }
        if (bits <= 0 && fmt == 0) return 0;
        return (uint)fmt switch
        {
            Gl.DEPTH_COMPONENT16 or Gl.DEPTH_COMPONENT24 or Gl.DEPTH_COMPONENT32
                or Gl.DEPTH_COMPONENT32F or Gl.DEPTH24_STENCIL8 or Gl.DEPTH32F_STENCIL8 => (uint)fmt,
            _ => stencil > 0 ? Gl.DEPTH24_STENCIL8
               : bits >= 32 ? Gl.DEPTH_COMPONENT32F
               : bits >= 24 ? Gl.DEPTH_COMPONENT24 : Gl.DEPTH_COMPONENT16,
        };
    }
}
