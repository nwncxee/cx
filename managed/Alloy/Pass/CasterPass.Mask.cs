using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The cutout mask: which diffuse earns the alpha-tested depth stage (holes against graded alpha), the
/// ordered dither its discard steps on, and the bindless handle program that samples it.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Masked shadow depth (<c>/shadow mask</c>): 1 = alpha-tested, 0 = the solid program for masked
    /// batches (prices the fetch + discard), -1 = skip them (prices their raster), 2 = alpha-tested only where
    /// the texture's hole fraction reaches <see cref="_maskHoles"/> (a gate, a railing), solid elsewhere (a
    /// leaf card). Default 2.</summary>
    private static int _maskMode => Shadows.Mask.V;
    /// <summary><c>/shadow maskholes X</c>: the hole fraction (texels under the cutout) from which mode 2 masks.</summary>
    private static float _maskHoles => Shadows.MaskHoles.V;
    /// <summary><c>/shadow maskgraded X</c>: the graded-alpha fraction from which a diffuse counts as
    /// glass and takes the masked stage regardless of its holes. Default = the census's blend threshold, so a
    /// soft-edged leaf card stays on the hole gate.</summary>
    private static float _maskGraded => Shadows.MaskGraded.V;
    /// <summary><c>/shadow maskdither N</c>: the shadow-texel block the masked stage's ordered discard
    /// steps on (1 = per texel, the receiver's PCF averages it; 2 = 2x2 clumps read softer but blotchier);
    /// 0 = off. Never per-frame: the maps are cached, and a jittering pattern is a permanent mover.</summary>
    private static int _maskDither => Shadows.MaskDither.V;
    /// <summary>Past this the blocks read as a pattern, not a shadow.</summary>
    internal const int MaskDitherMax = 16;

    private static int _maskNoTex, _maskNoProg;

    private static bool _maskH => Shadows.MaskH.V;
    private static bool HandleMaskReady => _maskH && _gl.ProgMaskH != 0 && (_gl.ProgLayer == 0 || _gl.ProgLayerMaskH != 0);

    /// <summary>Build the handle-sampling pair once the renderer's bindless path is armed. Lazy because the
    /// bindless probe runs in the first COLOUR frame and this pass's programs are built in the shadow
    /// bracket before it; a pass built on that frame would otherwise never get the pair. Re-stages once
    /// when the pair becomes usable, so the standing key picks it up.</summary>
    private static void EnsureHandleMask()
    {
        if (_gl.MaskHFailed || _gl.ProgMask == 0 || !AlloyRenderer.Bindless || CastMaskHFs == CastMaskHFsSrc) return;
        if (_gl.ProgMaskH == 0)
        {
            _gl.ProgMaskH = GlProgram.Graphics(CastMaskHVs, CastMaskHFs, _log, "CasterPass handle-masked depth program");
            if (_gl.ProgMaskH == 0) { _gl.MaskHFailed = true; return; }
            _gl.LocMaskHVp = GlProgram.Loc(_gl.ProgMaskH, "uVP"); _gl.LocMaskHCut = GlProgram.Loc(_gl.ProgMaskH, "uAlphaCut"); _gl.LocMaskHDither = GlProgram.Loc(_gl.ProgMaskH, "uDither"); _gl.LocMaskHEnv = GlProgram.Loc(_gl.ProgMaskH, "uEnvAlpha"); _gl.LocMaskHMip = GlProgram.Loc(_gl.ProgMaskH, "uAlphaMip");
            _listVer++;
            _log?.Info("CasterPass: handle-sampled masks armed -- masked batches merge per VAO (/shadow maskh 0 is the A/B)");
        }
        if (_gl.ProgLayer != 0 && _gl.ProgLayerMaskH == 0)
        {
            _gl.ProgLayerMaskH = GlProgram.Graphics(CastLayerMaskHVs, CastMaskHFs, _log, "CasterPass layered handle-masked depth program");
            if (_gl.ProgLayerMaskH == 0) { _gl.MaskHFailed = true; return; }
            _gl.LocLayerMaskHCut = GlProgram.Loc(_gl.ProgLayerMaskH, "uAlphaCut"); _gl.LocLayerMaskHDither = GlProgram.Loc(_gl.ProgLayerMaskH, "uDither"); _gl.LocLayerMaskHEnv = GlProgram.Loc(_gl.ProgLayerMaskH, "uEnvAlpha"); _gl.LocLayerMaskHMip = GlProgram.Loc(_gl.ProgLayerMaskH, "uAlphaMip");
            _listVer++;
        }
    }

    /// <summary>The two engine thresholds. Alpha means COVERAGE on everything the engine ships, so the
    /// default is the engine's own 0.2 -- the value its colour pass uses, which is what makes a leaf cast a
    /// leaf. The exception is an env-mapped material, where the diffuse alpha is REFLECTIVITY
    /// (<c>fEnvMapLevel = 1 - alpha</c>): a polished floor at alpha 0.3 is solid and must keep its shadow.</summary>
    private const float CutoutDiscard = 0.2f, EnvAlphaDiscard = 0.02f;

    /// <summary>Which threshold a masked caster tests its mask at, as a KIND rather than the number. The
    /// staging key, the dither and the <c>uEnvAlpha</c> flag each need to know WHICH, and recovering that
    /// from a float equality goes wrong silently the day one of the three values moves or becomes a knob.</summary>
    internal enum CasterCut : byte
    {
        /// <summary>The engine's own <c>fAlphaDiscardValue</c> -- what makes a leaf cast a leaf.</summary>
        Cutout = 0,
        /// <summary>Env-mapped: the diffuse alpha is reflectivity, so only empty texels may punch through.</summary>
        EnvAlpha = 1,
        /// <summary>A declared coverage material: the layer's own cut, so the shadow drops exactly the texels
        /// the draw does rather than cutting a hard 0.2 hole out of geometry that draws nearly solid.</summary>
        Coverage = 2,
    }

    /// <summary>The threshold each kind tests at -- the one place a kind becomes a number.</summary>
    internal static float Threshold(CasterCut kind) => kind switch
    {
        CasterCut.Coverage => CoverageLayer.Cut,
        CasterCut.EnvAlpha => EnvAlphaDiscard,
        _ => CutoutDiscard,
    };

    /// <summary>One caster's threshold kind: a declared coverage material first (the author already said its
    /// alpha is coverage), then the env-alpha exception, then the engine's own.</summary>
    internal static CasterCut CutFor(bool coverage, bool envAlpha)
        => coverage ? CasterCut.Coverage : envAlpha ? CasterCut.EnvAlpha : CasterCut.Cutout;

    /// <summary>The ordered-dither block for a batch of this kind. A coverage batch always dithers: the
    /// dither IS how the shadow map carries fractional coverage, so at 0 its cut would stamp a near-solid
    /// silhouette -- strictly worse than the 0.2 one it replaced.</summary>
    internal static int DitherFor(CasterCut kind)
        => kind == CasterCut.Coverage ? Math.Max(_maskDither, 1) : _maskDither;

    /// <summary>Does this texture carry coverage? Answered once by a level-0 readback, because
    /// <c>Material::GetIsOpaque</c> @0x4c6190 short-circuits on an env map, the gob alpha and its own memo,
    /// and the internal format lies (a shipped ivy card is a DXT1 with ALPHA_SIZE 0 and 48% punchthrough
    /// blocks). The same question is put to the decoded image at the upload funnel, texel by texel.</summary>
    internal static bool TexHasAlpha(int tid) => TexHoles(tid) > 0f;
    /// <summary>Fraction of level-0 texels that are neither cutout nor opaque -- a glass-imitating
    /// diffuse at alpha ~0.5. At or above <see cref="_maskGraded"/> the caster takes the masked stage whatever
    /// its hole fraction, and the stage's stochastic discard turns that alpha into a partial shadow.</summary>
    internal static float TexGraded(int tid) { TexStat(tid, out float g); return g; }

    /// <summary>Fraction of level-0 texels the cutout drops; 0 = a slab. 1 for a texture the funnel never
    /// scanned: test rather than cast a possible slab. No readback: the answer was computed from the engine's
    /// decoded image at upload (<see cref="TexScan"/>), which is what keeps a radeonsi engine context's
    /// descriptors of the same texture honest.</summary>
    internal static float TexHoles(int tid) => TexStat(tid, out _);
    private static float TexStat(int tid, out float graded)
    {
        graded = 0f;
        if (tid <= 0) return 0f;
        int h = (int)((uint)tid * 2654435761u % TexCacheSize);
        for (int probe = 0; probe < 8; probe++)
        {
            int k = (h + probe) % TexCacheSize;
            if (_texKey[k] == tid) { graded = _texGraded[k]; return _texHoles[k]; }
            if (_texKey[k] != 0) continue;
            float holes = OwnedTextures.TryHoles(tid, out float f, out graded) ? f : 1f;
            _texKey[k] = tid; _texHoles[k] = holes; _texGraded[k] = graded;
            _texQueried++;
            if (holes > 0f) _texWithAlpha++;
            return holes;
        }
        return 1f;
    }

    private const int TexCacheSize = 2048;
    private static readonly int[] _texKey = new int[TexCacheSize];
    private static readonly float[] _texHoles = new float[TexCacheSize], _texGraded = new float[TexCacheSize];
    private static int _texQueried, _texWithAlpha;

    /// <summary>The caster's alpha-mask texture id, 0 if it does not resolve; cold relative to the draw (once
    /// per masked caster per collect) and never cached. Every caster draws out of the mesh arena, so the id
    /// comes off the draw recipe.</summary>
    private static int MaskTid(in AlloyRenderer.CasterFacts f) => AlloyRenderer.DbCasterTid(f.Slot);
}
