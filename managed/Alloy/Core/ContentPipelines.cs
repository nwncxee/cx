using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Which of a content pair's five programs a draw binds (<see cref="ContentPrograms"/>).</summary>
internal enum ContentVariant : byte { Forward, Deferred, Depth, Caster, CasterLayer }

/// <summary>The blend state a content draw runs in: off, or the material's own pair -- (SRC_ALPHA,
/// ONE_MINUS_SRC_ALPHA) or (SRC_ALPHA, ONE), the only two a recipe names.</summary>
internal enum PipelineBlend : byte { Off, AlphaOver, AlphaAdd }

/// <summary>The depth state a content draw runs in: the test's function, and whether the draw writes depth.</summary>
internal enum PipelineDepth : byte { LeqWrite, LessWrite, LeqNoWrite, EqualNoWrite }

/// <summary>The raster state a content draw varies: face culling off (a <c>twosided</c> material or a caster
/// pass), the wash polygon offset, and alpha-to-coverage.</summary>
[Flags]
internal enum PipelineRaster : byte { None = 0, NoCull = 1, Offset = 2, Coverage = 4 }

/// <summary>One pipeline a content pair's parts draw through: the program (the pair's feature id and which
/// variant), its vertex input, and the blend, depth and raster state the draw sets. GL rebinds these per batch;
/// a Vulkan backend needs one pipeline object per distinct key, which is what makes the set worth enumerating.</summary>
internal readonly record struct PipelineKey(int Feat, ContentVariant Variant, MeshArena.Fmt Format,
                                            PipelineBlend Blend, PipelineDepth Depth, PipelineRaster Raster);

/// <summary>The pipeline keys a content pair's parts reach, derived from the recipe that names the pair and the
/// facts its build read off its translation, and enumerated over the area's live recipes.</summary>
internal static class ContentPipelines
{
    /// <summary>Keys one recipe can name: forward, its blended twin, the deferred twin, the two depth-twin
    /// states and the two caster twins.</summary>
    internal const int MaxPerRecipe = 7;

    /// <summary>The keys a recipe's parts draw through, into <paramref name="into"/>; the count. Pure over the
    /// recipe's own state and the pair's built variants, so the table is checkable off-device.</summary>
    internal static int Keys(int feat, AlloyClass cls, RecipeFlag flags, bool additive, MeshArena.Fmt fmt,
                             bool deferred, bool ownDepth, bool caster, Span<PipelineKey> into)
    {
        int n = 0;
        PipelineRaster raster = (flags.HasFlag(RecipeFlag.TwoSided) ? PipelineRaster.NoCull : PipelineRaster.None)
                              | (flags.HasFlag(RecipeFlag.Wash) ? PipelineRaster.Offset : PipelineRaster.None)
                              | (flags.HasFlag(RecipeFlag.Coverage) ? PipelineRaster.Coverage : PipelineRaster.None);
        PipelineBlend own = additive ? PipelineBlend.AlphaAdd : PipelineBlend.AlphaOver;
        PipelineBlend blend = AlloyClasses.Blends((int)cls) ? own : PipelineBlend.Off;
        // The forward program every content part draws through. A blended content part sits the OIT
        // accumulation out and draws in the sweep after the resolve, which writes depth like the opaque
        // pass -- so one depth state covers both.
        into[n++] = new PipelineKey(feat, ContentVariant.Forward, fmt, blend, PipelineDepth.LeqWrite, raster);
        // ...and a wash the slot class turns blended draws that same program with the material's pair on.
        if (blend == PipelineBlend.Off && flags.HasFlag(RecipeFlag.Wash)
            && (flags.HasFlag(RecipeFlag.HintBlended) || cls == AlloyClass.Cutout))
            into[n++] = new PipelineKey(feat, ContentVariant.Forward, fmt, own, PipelineDepth.LeqWrite, raster);
        // The deferred twin takes the opaque and cutout batches only, and the coverage layer is forward-only.
        if (deferred && !AlloyClasses.Blends((int)cls))
            into[n++] = new PipelineKey(feat, ContentVariant.Deferred, fmt, PipelineBlend.Off,
                                        PipelineDepth.LeqWrite, raster & ~PipelineRaster.Coverage);
        if (ownDepth)
        {
            // Neither twin raster state is the forward one: alpha-to-coverage is the coverage layer's own, and
            // the wash offset is the colour pass's. The prepass writes the pair's own depth under LESS...
            PipelineRaster twin = raster & ~(PipelineRaster.Offset | PipelineRaster.Coverage);
            into[n++] = new PipelineKey(feat, ContentVariant.Depth, fmt, PipelineBlend.Off,
                                        PipelineDepth.LessWrite, twin);
            // ...and the outline mask tests against depth already written: EQUAL for a cutout, else LEQUAL
            // pulled forward by the mask's own offset.
            into[n++] = cls == AlloyClass.Cutout
                ? new PipelineKey(feat, ContentVariant.Depth, fmt, PipelineBlend.Off, PipelineDepth.EqualNoWrite, twin)
                : new PipelineKey(feat, ContentVariant.Depth, fmt, PipelineBlend.Off, PipelineDepth.LeqNoWrite,
                                  twin | PipelineRaster.Offset);
        }
        if (caster)
        {
            // both caster targets draw with face culling off for every batch and no offset
            into[n++] = new PipelineKey(feat, ContentVariant.Caster, fmt, PipelineBlend.Off,
                                        PipelineDepth.LeqWrite, PipelineRaster.NoCull);
            into[n++] = new PipelineKey(feat, ContentVariant.CasterLayer, fmt, PipelineBlend.Off,
                                        PipelineDepth.LeqWrite, PipelineRaster.NoCull);
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
        var a = new PipelineKey(5, ContentVariant.Forward, MeshArena.Fmt.Skinned, PipelineBlend.AlphaOver,
                                PipelineDepth.LeqWrite, PipelineRaster.NoCull | PipelineRaster.Offset);
        var b = new PipelineKey(5, ContentVariant.Forward, MeshArena.Fmt.Skinned, PipelineBlend.AlphaOver,
                                PipelineDepth.LeqWrite, PipelineRaster.NoCull | PipelineRaster.Offset);
        check.Expect(a == b && a.GetHashCode() == b.GetHashCode(),
                     "two keys built from the same six terms are not equal, or do not hash alike");
        check.Expect(a != (b with { Feat = 6 }) && a != (b with { Variant = ContentVariant.Deferred })
                     && a != (b with { Format = MeshArena.Fmt.SkinnedUv })
                     && a != (b with { Blend = PipelineBlend.AlphaAdd })
                     && a != (b with { Depth = PipelineDepth.LessWrite })
                     && a != (b with { Raster = PipelineRaster.NoCull }),
                     "a key with one term changed still equals the original, so one pipeline would stand for two");
        Span<PipelineKey> keys = stackalloc PipelineKey[MaxPerRecipe];
        int feat = (int)ShaderFeature.Content0;
        int n = Keys(feat, AlloyClass.Opaque, RecipeFlag.Lit, false, MeshArena.Fmt.Static,
                     deferred: false, ownDepth: false, caster: false, keys);
        check.Expect(n == 1 && keys[0] == new PipelineKey(feat, ContentVariant.Forward, MeshArena.Fmt.Static,
                                                          PipelineBlend.Off, PipelineDepth.LeqWrite, PipelineRaster.None),
                     "a plain opaque content recipe does not name exactly one key: its forward program, blend off, "
                     + "depth written under LEQUAL");
        n = Keys(feat, AlloyClass.Additive, RecipeFlag.TwoSided, true, MeshArena.Fmt.Static,
                 deferred: true, ownDepth: false, caster: false, keys);
        check.Expect(n == 1 && keys[0].Blend == PipelineBlend.AlphaAdd && keys[0].Raster == PipelineRaster.NoCull,
                     "an additive two-sided recipe does not draw (SRC_ALPHA, ONE) with culling off, or takes a "
                     + "deferred twin the G-buffer pass never draws it through");
        n = Keys(feat, AlloyClass.Cutout, RecipeFlag.Lit | RecipeFlag.Coverage, false, MeshArena.Fmt.StaticUv,
                 deferred: true, ownDepth: true, caster: true, keys);
        check.Expect(n == 6, $"a cutout content recipe with coverage, own depth and casters names {n} keys, not the "
                           + "six it draws through (forward, deferred, prepass, outline mask, two casters)");
        check.Expect(keys[1].Variant == ContentVariant.Deferred && keys[1].Raster == PipelineRaster.None,
                     "the deferred twin carries the coverage flag, where the coverage layer draws forward only");
        check.Expect(keys[2].Depth == PipelineDepth.LessWrite && keys[3].Depth == PipelineDepth.EqualNoWrite,
                     "the depth twin's two states are not the prepass's LESS write and the outline mask's EQUAL read");
        check.Expect(keys[4].Raster == PipelineRaster.NoCull && keys[5].Variant == ContentVariant.CasterLayer,
                     "a caster twin draws with face culling on, or the layered twin is missing");
        n = Keys(feat, AlloyClass.Cutout, RecipeFlag.Wash | RecipeFlag.HintBlended, false, MeshArena.Fmt.Static,
                 deferred: false, ownDepth: false, caster: false, keys);
        check.Expect(n == 2 && keys[0].Blend == PipelineBlend.Off && keys[1].Blend == PipelineBlend.AlphaOver
                     && keys[0].Raster == PipelineRaster.Offset,
                     "a hinted cutout wash does not name both its states -- the cutout one and the blended one its "
                     + "slot class takes -- each with the wash offset");
        log.Info(check.Ok ? "ContentPipelines.SelfCheck: passed" : "ContentPipelines.SelfCheck: FAILED");
        return check.Ok;
    }
}
