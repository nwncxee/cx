using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Which of a content pair's five programs a draw binds (<see cref="ContentPrograms"/>).</summary>
internal enum ContentVariant : byte { Forward, Deferred, Depth, Caster, CasterLayer }

/// <summary>One pipeline a content pair's parts draw through: the program (the pair's feature id and which
/// variant), its vertex input, and the blend, depth and raster state the draw sets, in the render seam's own
/// terms. GL rebinds these per batch; a pipeline-per-state backend needs one object per distinct key.</summary>
internal readonly record struct PipelineKey(int Feat, ContentVariant Variant, MeshArena.Fmt Format,
                                            DepthState Depth, RasterState Raster, AttachmentBlend Blend,
                                            bool Coverage);

/// <summary>The pipeline keys a content pair's parts reach, derived from the recipe that names the pair and the
/// facts its build read off its translation, and enumerated over the area's live recipes.</summary>
internal static class ContentPipelines
{
    /// <summary>Keys one recipe can name: forward, its blended twin, the deferred twin, the two depth-twin
    /// states and the two caster twins.</summary>
    internal const int MaxPerRecipe = 7;

    /// <summary>The depth states a content draw runs in: the colour and caster passes write under LEQUAL, the
    /// prepass under LESS, and the outline mask reads depth already written.</summary>
    private static readonly DepthState LeqWrite = new(Test: true, Write: true, CompareOp.LessEqual),
        LessWrite = new(Test: true, Write: true, CompareOp.Less),
        LeqRead = new(Test: true, Write: false, CompareOp.LessEqual),
        EqualRead = new(Test: true, Write: false, CompareOp.Equal);

    /// <summary>The material's own blend pair, which <c>glBlendFunc</c> applies to colour and alpha alike:
    /// (SRC_ALPHA, ONE_MINUS_SRC_ALPHA), or (SRC_ALPHA, ONE) for an additive material.</summary>
    private static AttachmentBlend Pair(bool additive) => new(Enable: true, BlendFactor.SrcAlpha,
        additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha,
        additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha);

    /// <summary>The keys a recipe's parts draw through, into <paramref name="into"/>; the count. Pure over the
    /// recipe's own state and the pair's built variants, so the table is checkable off-device.</summary>
    internal static int Keys(int feat, AlloyClass cls, RecipeFlag flags, bool additive, MeshArena.Fmt fmt,
                             bool deferred, bool ownDepth, bool caster, Span<PipelineKey> into)
    {
        int n = 0;
        var raster = new RasterState(flags.HasFlag(RecipeFlag.TwoSided) ? CullMode.None : CullMode.Back,
                                     DepthBias: flags.HasFlag(RecipeFlag.Wash));
        bool coverage = flags.HasFlag(RecipeFlag.Coverage);
        bool blends = AlloyClasses.Blends((int)cls);
        AttachmentBlend blend = blends ? Pair(additive) : default;
        // The forward program every content part draws through. A blended content part sits the OIT
        // accumulation out and draws in the sweep after the resolve, which writes depth like the opaque
        // pass -- so one depth state covers both.
        into[n++] = new PipelineKey(feat, ContentVariant.Forward, fmt, LeqWrite, raster, blend, coverage);
        // ...and a wash the slot class turns blended draws that same program with the material's pair on.
        if (!blends && flags.HasFlag(RecipeFlag.Wash)
            && (flags.HasFlag(RecipeFlag.HintBlended) || cls == AlloyClass.Cutout))
            into[n++] = new PipelineKey(feat, ContentVariant.Forward, fmt, LeqWrite, raster, Pair(additive), coverage);
        // The deferred twin takes the opaque and cutout batches only, and the coverage layer is forward-only.
        if (deferred && !blends)
            into[n++] = new PipelineKey(feat, ContentVariant.Deferred, fmt, LeqWrite, raster, default, false);
        if (ownDepth)
        {
            // Neither twin raster state is the forward one: alpha-to-coverage is the coverage layer's own, and
            // the wash offset is the colour pass's. The prepass writes the pair's own depth under LESS...
            RasterState twin = raster with { DepthBias = false };
            into[n++] = new PipelineKey(feat, ContentVariant.Depth, fmt, LessWrite, twin, default, false);
            // ...and the outline mask tests against depth already written: EQUAL for a cutout, else LEQUAL
            // pulled forward by the mask's own offset.
            into[n++] = cls == AlloyClass.Cutout
                ? new PipelineKey(feat, ContentVariant.Depth, fmt, EqualRead, twin, default, false)
                : new PipelineKey(feat, ContentVariant.Depth, fmt, LeqRead, twin with { DepthBias = true },
                                  default, false);
        }
        if (caster)
        {
            // both caster targets draw with face culling off for every batch and no offset
            var cast = new RasterState(CullMode.None);
            into[n++] = new PipelineKey(feat, ContentVariant.Caster, fmt, LeqWrite, cast, default, false);
            into[n++] = new PipelineKey(feat, ContentVariant.CasterLayer, fmt, LeqWrite, cast, default, false);
        }
        return n;
    }

    /// <summary>Every key the live recipe table's content materials name, appended to <paramref name="into"/> as
    /// <paramref name="seen"/> takes it; pairs whose build has not happened are counted in
    /// <paramref name="unsettled"/> and asked for, since their twins are not knowable yet.</summary>
    internal static int Enumerate(HashSet<PipelineKey> seen, List<PipelineKey> into, out int unsettled)
    {
        Span<PipelineKey> keys = stackalloc PipelineKey[MaxPerRecipe];
        int pairs = 0;
        unsettled = 0;
        for (int r = 0; r < DrawRecipe.High; r++)
        {
            int feat = DrawRecipe.Feat(r);
            if (!ShaderFeatures.IsContent(feat)) continue;
            MeshDef? mesh = DrawRecipe.Mesh(r);
            if (mesh == null) continue;
            if (!ContentPrograms.Settled(feat))
            {
                ContentPrograms.Want(feat);
                unsettled++;
                continue;
            }
            pairs++;
            RecipeFlag flags = DrawRecipe.Flags(r);
            MeshArena.Fmt fmt = MeshArena.FormatFor(flags.HasFlag(RecipeFlag.Skinned), MeshArena.HasUvSets(mesh));
            int n = Keys(feat, (AlloyClass)DrawRecipe.Cls(r), flags, DrawRecipe.Dst(r) == DrawRecipe.BlendOne, fmt,
                         ContentPrograms.HasDeferred(feat), ContentPrograms.OwnDepth(feat),
                         ContentPrograms.CastProgram(feat, false) != 0, keys);
            for (int k = 0; k < n; k++) if (seen.Add(keys[k])) into.Add(keys[k]);
        }
        return pairs;
    }

    /// <summary>Off-device. Oracle: the key terms hand-written from the state each pass sets around a content
    /// draw -- the issue loop's blend and depth per class, the prepass's LESS, the outline mask's EQUAL and the
    /// caster targets' culling -- plus hand-written tuples for the record struct's equality.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var cull = new RasterState(CullMode.Back);
        var a = new PipelineKey(5, ContentVariant.Forward, MeshArena.Fmt.Skinned, LeqWrite, cull, Pair(false), true);
        var b = new PipelineKey(5, ContentVariant.Forward, MeshArena.Fmt.Skinned, LeqWrite, cull, Pair(false), true);
        check.Expect(a == b && a.GetHashCode() == b.GetHashCode(),
                     "two keys built from the same seven terms are not equal, or do not hash alike");
        check.Expect(a != (b with { Feat = 6 }) && a != (b with { Variant = ContentVariant.Deferred })
                     && a != (b with { Format = MeshArena.Fmt.SkinnedUv })
                     && a != (b with { Blend = Pair(true) }) && a != (b with { Blend = default })
                     && a != (b with { Depth = LessWrite }) && a != (b with { Depth = LeqRead })
                     && a != (b with { Raster = new RasterState(CullMode.None) })
                     && a != (b with { Raster = cull with { DepthBias = true } }) && a != (b with { Coverage = false }),
                     "a key with one term changed still equals the original, so one pipeline would stand for two");
        Span<PipelineKey> keys = stackalloc PipelineKey[MaxPerRecipe];
        int feat = (int)ShaderFeature.Content0;
        int n = Keys(feat, AlloyClass.Opaque, RecipeFlag.Lit, false, MeshArena.Fmt.Static,
                     deferred: false, ownDepth: false, caster: false, keys);
        check.Expect(n == 1 && keys[0] == new PipelineKey(feat, ContentVariant.Forward, MeshArena.Fmt.Static,
                                                          new DepthState(true, true, CompareOp.LessEqual),
                                                          new RasterState(CullMode.Back), default, false),
                     "a plain opaque content recipe does not name exactly one key: its forward program, blend off, "
                     + "back faces culled, depth written under LEQUAL");
        n = Keys(feat, AlloyClass.Additive, RecipeFlag.TwoSided, true, MeshArena.Fmt.Static,
                 deferred: true, ownDepth: false, caster: false, keys);
        check.Expect(n == 1 && keys[0].Blend == new AttachmentBlend(true, BlendFactor.SrcAlpha, BlendFactor.One,
                                                                   BlendFactor.SrcAlpha, BlendFactor.One)
                     && keys[0].Raster.Cull == CullMode.None,
                     "an additive two-sided recipe does not draw (SRC_ALPHA, ONE) with culling off, or takes a "
                     + "deferred twin the G-buffer pass never draws it through");
        n = Keys(feat, AlloyClass.Cutout, RecipeFlag.Lit | RecipeFlag.Coverage, false, MeshArena.Fmt.StaticUv,
                 deferred: true, ownDepth: true, caster: true, keys);
        check.Expect(n == 6, $"a cutout content recipe with coverage, own depth and casters names {n} keys, not the "
                           + "six it draws through (forward, deferred, prepass, outline mask, two casters)");
        check.Expect(keys[0].Coverage && !keys[1].Coverage && keys[1].Variant == ContentVariant.Deferred,
                     "alpha-to-coverage does not reach the forward key, or it reaches the deferred twin, where the "
                     + "coverage layer never draws");
        check.Expect(keys[2].Depth.Compare == CompareOp.Less && keys[2].Depth.Write
                     && keys[3].Depth.Compare == CompareOp.Equal && !keys[3].Depth.Write,
                     "the depth twin's two states are not the prepass's LESS write and the outline mask's EQUAL read");
        check.Expect(keys[4].Raster.Cull == CullMode.None && keys[5].Variant == ContentVariant.CasterLayer,
                     "a caster twin draws with face culling on, or the layered twin is missing");
        n = Keys(feat, AlloyClass.Cutout, RecipeFlag.Wash | RecipeFlag.HintBlended, false, MeshArena.Fmt.Static,
                 deferred: false, ownDepth: false, caster: false, keys);
        check.Expect(n == 2 && !keys[0].Blend.Enable && keys[1].Blend.Enable && keys[0].Raster.DepthBias,
                     "a hinted cutout wash does not name both its states -- the cutout one and the blended one its "
                     + "slot class takes -- each with the wash depth bias");
        log.Info(check.Ok ? "ContentPipelines.SelfCheck: passed" : "ContentPipelines.SelfCheck: FAILED");
        return check.Ok;
    }
}
