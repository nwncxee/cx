using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The GL half of CX-owned fonts -- a baked TTF atlas becomes a CX-owned texture, in immutable
/// <c>RGBA8</c> storage with NEAREST and no mips, which is the engine's own sampling with a sized format.
/// Lives on the submission side because <c>Alloy/Scene</c> and <c>Alloy/Db</c> name no GL. A BITMAP font's
/// atlas is an ordinary resref-keyed texture and has no handle here yet: counted and said, never drawn blank.</summary>
internal static unsafe class TextAtlas
{
    private static ExtenderLog? _log;
    /// <summary>One registry reference per bitmap-font texture, held for the session like the font itself.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, TexId> _tex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Atlases uploaded, and bitmap fonts whose texture never arrived. <see cref="NoTexture"/> counts
    /// FONTS, not frames: the registry is re-asked every frame by design, so a per-frame tally would grow at
    /// frame rate and measure nothing.</summary>
    internal static long Uploaded, NoTexture, Deleted;

    /// <summary>Frames a bitmap font may wait for its texture before the wait is called a failure. A registry
    /// load is a worker read, so this is generous on purpose -- roughly ten seconds at 60 fps.</summary>
    private const int GiveUpFrames = 600;

    internal static void Install(ExtenderLog log) => _log = log;

    /// <summary>Is there anything for <see cref="Pump"/> to do? Two list counts, so its caller can skip
    /// ENTERING Alloy's context on the frames -- nearly all of them -- with no atlas to build or free.</summary>
    internal static bool Due => TextFontBake.Pending.Count > 0 || TextFonts.Orphans.Count > 0;

    /// <summary>Drain the bake queue. Cold: it runs on the frames right after a font is first named, and does
    /// nothing at all on every other frame. Must be called inside Alloy's bracket: the uploads are 4.5 DSA
    /// and the sweep deletes GL names, so the engine's 3.3 context is the wrong one for both.</summary>
    internal static void Pump()
    {
        Sweep();
        var pending = TextFontBake.Pending;
        if (pending.Count == 0) return;
        for (int i = 0; i < pending.Count; i++)
        {
            TextFontBake.PendingAtlas p = pending[i];
            if (p.Resref != null)
            {
                if (!_tex.TryGetValue(p.Resref, out TexId id))
                    _tex[p.Resref] = id = TexRegistry.Acquire(p.Resref);
                if (!TexRegistry.TryResident(id, out uint h, out _))
                {
                    if (++p.Waited == GiveUpFrames)
                    {
                        NoTexture++;
                        _log?.Always($"TextAtlas: the bitmap font \"{p.Font.Name}\" still has no atlas after "
                                   + $"{GiveUpFrames} frames -- its texture \"{p.Resref}\" never became resident; "
                                   + "it lays out but does NOT DRAW (totals on /alloy)");
                    }
                    continue;
                }
                p.Font.Atlas = h;
            }
            else if (!Upload(p)) continue;
            if (p.Replaces != null) TextFonts.Swap(p.Replaces, p.Font);
            pending.RemoveAt(i--);
        }
    }

    /// <summary>Delete the atlases nothing refers to any more. Cold, and the one place a font's GL name dies.</summary>
    private static void Sweep()
    {
        var orphans = TextFonts.Orphans;
        if (orphans.Count == 0) return;
        for (int i = 0; i < orphans.Count; i++)
        {
            if (orphans[i] == 0) continue;
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, orphans[i]);
            Deleted++;
        }
        orphans.Clear();
    }

    private static bool Upload(TextFontBake.PendingAtlas p)
    {
        // Not an error on the frames before the 4.6 context is up: the pending list is re-pumped every
        // frame and the upload lands as soon as the entry points resolve. Only a wait that never ends is said.
        if (Gl.CreateTextures == null || Gl.TextureStorage2D == null || Gl.TextureSubImage2D == null
            || Gl.TextureParameteri == null)
        {
            if (++p.Waited == GiveUpFrames)
                _log?.Always("TextAtlas: the DSA texture entry points never resolved -- CX fonts do NOT DRAW");
            return false;
        }
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        if (t == 0) return false;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, p.Width, p.Height);
        Gl.TextureSubImage2D(t, 0, 0, 0, p.Width, p.Height, Gl.RGBA, Gl.UNSIGNED_BYTE, (void*)p.Pixels);
        // The engine's own font sampling: nearest on both filters, no mips. Anything else moves glyph pixels.
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, (int)Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, (int)Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
        p.Font.Atlas = t;
        Marshal.FreeHGlobal(p.Pixels);
        p.Pixels = IntPtr.Zero;
        Uploaded++;
        _log?.Info($"TextAtlas: \"{p.Font.Name}\" uploaded, {p.Width}x{p.Height} RGBA8 nearest (texture {t})");
        return true;
    }

    /// <summary>The context went: every atlas name is gone with it, so every font is re-baked on its next ask
    /// (the epoch moves, which is what invalidates every cached quad list).</summary>
    internal static void ContextLost()
    {
        foreach (TextFontBake.PendingAtlas p in TextFontBake.Pending)
            if (p.Pixels != IntPtr.Zero) { Marshal.FreeHGlobal(p.Pixels); p.Pixels = IntPtr.Zero; }
        TextFontBake.Pending.Clear();
        TextFonts.Clear(orphanAtlases: false);   // the names died with the context; deleting them is a dead use
        TextFonts.Orphans.Clear();
    }
}
