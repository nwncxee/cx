using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The upload half of CX-owned fonts -- a baked TTF atlas becomes a CX-owned <c>RGBA8</c> image,
/// sampled NEAREST with no mips, which is the engine's own font sampling. A BITMAP font's atlas is an
/// ordinary resref-keyed texture and has no image of its own here: counted and said, never drawn blank.</summary>
internal static partial class TextAtlas
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

    /// <summary>Is there anything for <see cref="Pump"/> to do? Two list counts, so its caller can skip the
    /// drain on the frames -- nearly all of them -- with no atlas to build or free.</summary>
    internal static bool Due => TextFontBake.Pending.Count > 0 || TextFonts.Orphans.Count > 0;

    /// <summary>Drain the bake queue. Cold: it runs on the frames right after a font is first named, and does
    /// nothing at all on every other frame. It touches only Vulkan images.</summary>
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

    /// <summary>Delete the atlases nothing refers to any more. Cold, and the one place a font's image dies.</summary>
    private static void Sweep()
    {
        var orphans = TextFonts.Orphans;
        if (orphans.Count == 0) return;
        for (int i = 0; i < orphans.Count; i++)
        {
            if (orphans[i] == 0) continue;
            Forget(orphans[i]);
            Deleted++;
        }
        orphans.Clear();
    }

    private static bool Upload(TextFontBake.PendingAtlas p)
    {
        // Asked before the device is up: the entry stays pending with its pixels and the upload lands as soon as
        // the device exists, so nothing is minted per retry. Only a wait that never ends is said.
        if (!MirrorReady())
        {
            if (++p.Waited == GiveUpFrames)
                _log?.Always("TextAtlas: the Vulkan device never came up on this thread -- CX fonts do NOT DRAW");
            return false;
        }
        uint t = VkTextures.NewName();
        if (t == 0) return false;
        p.Font.Atlas = t;
        MirrorVk(t, p);
        Marshal.FreeHGlobal(p.Pixels);
        p.Pixels = IntPtr.Zero;
        Uploaded++;
        _log?.Info($"TextAtlas: \"{p.Font.Name}\" uploaded, {p.Width}x{p.Height} RGBA8 nearest (texture {t})");
        return true;
    }

    /// <summary>The device went: every atlas image is gone with it, so every font is re-baked on its next ask
    /// (the epoch moves, which is what invalidates every cached quad list).</summary>
    internal static void ContextLost()
    {
        foreach (TextFontBake.PendingAtlas p in TextFontBake.Pending)
            if (p.Pixels != IntPtr.Zero) { Marshal.FreeHGlobal(p.Pixels); p.Pixels = IntPtr.Zero; }
        TextFontBake.Pending.Clear();
        TextFonts.Clear(orphanAtlases: false);   // the names died with the context; deleting them is a dead use
        TextFonts.Orphans.Clear();
        _vk = new VkAtlasState();   // the Vulkan device went with the context, and every mirror with it
    }
}
