using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Which Alloy pass a part draws in. The engine sorts nothing inside a draw bucket and survives on a 0.2
/// alpha cutout, so the Alloy pass PICKS an order rather than reproducing one -- and the pick is this enum.
/// Declaration order IS draw order, and the staging arrays are sorted on it, so each pass owns a contiguous
/// batch range.
/// </summary>
internal enum AlloyClass
{
    /// <summary>Genuinely opaque, and the tilefade caps -- the engine draws bucket 7 with blending and the
    /// alpha discard both off, so a cap is an opaque draw whatever its material declares.</summary>
    Opaque = 0,
    /// <summary>The 0.2-discard majority: non-opaque only because the diffuse texture carries an alpha
    /// channel. Draws WITH the opaque set -- depth-writing, unsorted, alpha-tested -- which is what makes it
    /// order-independent by construction rather than by luck.</summary>
    Cutout = 1,
    /// <summary>Genuine alpha: the material declares <c>transparency</c>, or the part carries an alpha
    /// multiplier below 1 (which is also what a fading tile becomes for the frames it fades). Sorted
    /// back-to-front per part, depth writes OFF.</summary>
    Blended = 2,
    /// <summary>Water -- not a blend mode but a SHADING class, the one the engine routes on
    /// <c>Material::HasBumpyShinyMap</c> or the <c>.mtr</c>'s <c>sample_framebuffer 2</c> (value 1 is the late
    /// class, a different thing). Drawn between blended and additive because that is where its INPUT, a copy
    /// of the frame, is ready; depth writes stay ON so additive can sit in front of the surface.</summary>
    Water = 3,
    /// <summary>Destination blend <c>GL_ONE</c> -- the engine's own bucket-4 test. No depth write, and no
    /// sort needed: addition commutes.</summary>
    Additive = 4,
}

internal static class AlloyClasses
{
    internal const int Count = 5;

    /// <summary>Classify a part from the terms the engine's own predicates read -- the destination blend
    /// factor, <c>PartTriMesh::GetIsOpaque</c>, the <c>transparency</c> keyword and a sub-1 alpha -- plus a
    /// declared <c>NWN_ALPHA_MODE 2</c>, which beats both opacity terms because such a shader may read its
    /// coverage from a map other than texture0. Water first, the engine's own precedence.</summary>
    internal static AlloyClass Of(bool water, bool additive, bool opaque, bool declaredTransparent,
                                  bool alphaBelowOne, bool coverage)
        => water ? AlloyClass.Water
         : additive ? AlloyClass.Additive
         // ...so filing it opaque takes it out of the layer's range and INTO the diffuse-only prepass.
         : coverage ? AlloyClass.Cutout
         : opaque ? AlloyClass.Opaque
         : declaredTransparent || alphaBelowOne ? AlloyClass.Blended
         : AlloyClass.Cutout;

    /// <summary>Does this part draw through <see cref="CoverageLayer"/>? A declared coverage material, and
    /// only in the cutout class: water and additive keep their own passes, where the alpha is not coverage
    /// at all. The one answer the layer's batch range, the prepass exclusion and the caster all read.</summary>
    internal static bool Covers(int cls, bool coverage) => coverage && cls == (int)AlloyClass.Cutout;

    /// <summary>Does this class draw with <c>GL_BLEND</c> on, using the material's own blend pair? The three
    /// sorted classes, and NOT cutout: a cutout keeps its depth write, and a graded texel that both blends and
    /// stamps depth punches sky-coloured holes through foliage. ponytail: hard edges on graded foliage; the
    /// upgrade path is coverage (<see cref="CoverageLayer"/>, opt-in per material), not blending cutout.</summary>
    internal static bool Blends(int cls) => cls != (int)AlloyClass.Opaque && cls != (int)AlloyClass.Cutout;

    /// <summary>The engine's <c>fAlphaDiscardValue</c> for this class (<c>GLRender::SetUniforms</c> @0x4ccbf0:
    /// 0.2 under <c>Enable(1)</c>, -1.0 otherwise). Cutout and blended get the test -- a fading tile is a
    /// bucket-8 part, drawn under <c>Enable(1)</c> -- while opaque, additive and water get none: water's alpha
    /// is coverage the shader computes, so a 0.2 test on it would punch holes in shallows.</summary>
    internal static float Discard(int cls) => cls == (int)AlloyClass.Opaque || cls == (int)AlloyClass.Additive
        || cls == (int)AlloyClass.Water ? -1f : 0.2f;

    /// <summary>Off-device: the classifier's precedence, the one thing about it not readable off a single
    /// line. Additive beats opacity (the engine tests the blend factor first), opacity beats both transparency
    /// terms, and a texture-alpha part with nothing declared is a cutout -- the majority of NWN content, and
    /// the reason the missing sort survives at all.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyClasses.SelfCheck");
        // Each row names only the terms it sets; the rest are false. Six adjacent bools spelled positionally
        // swap silently, and a swapped pair here would read as the classifier agreeing with the table.
        static AlloyClass Row(bool water = false, bool additive = false, bool opaque = false,
                              bool declaredTransparent = false, bool alphaBelowOne = false, bool coverage = false)
            => Of(water, additive, opaque, declaredTransparent, alphaBelowOne, coverage);

        check.Expect(Row(water: true, additive: true, opaque: true, declaredTransparent: true,
                         alphaBelowOne: true, coverage: true) == AlloyClass.Water, "water beats every other term");
        check.Expect(Row(additive: true, opaque: true, coverage: true) == AlloyClass.Additive, "additive beats opaque and coverage");
        check.Expect(Row(additive: true, declaredTransparent: true, alphaBelowOne: true) == AlloyClass.Additive,
          "additive beats the transparency terms");
        check.Expect(Row(opaque: true, declaredTransparent: true, alphaBelowOne: true) == AlloyClass.Opaque,
          "an opaque part is opaque whatever it declares");
        check.Expect(Row(declaredTransparent: true) == AlloyClass.Blended, "declared transparency blends");
        check.Expect(Row(alphaBelowOne: true) == AlloyClass.Blended, "a sub-1 part alpha blends");
        check.Expect(Row() == AlloyClass.Cutout, "texture alpha alone is a cutout");

        // The coverage table, expected classes written out by hand: a declared NWN_ALPHA_MODE 2 reaches the
        // layer off ANY diffuse, nothing else does, and a material that declares nothing is unchanged.
        AlloyClass modeTwoOpaque = Row(opaque: true, coverage: true);
        check.Expect(modeTwoOpaque == AlloyClass.Cutout && Covers((int)modeTwoOpaque, true),
          "mode 2 over an OPAQUE diffuse must reach the coverage layer, not the opaque pass");
        check.Expect(Row(opaque: true) == AlloyClass.Opaque && !Covers((int)AlloyClass.Opaque, false),
          "an opaque diffuse with no mode 2 is a plain opaque part");
        check.Expect(Row() == AlloyClass.Cutout && !Covers((int)AlloyClass.Cutout, false),
          "a graded diffuse with no mode 2 stays the plain 0.2 cutout -- an unknown material never becomes coverage");
        check.Expect(Row(declaredTransparent: true, coverage: true) == AlloyClass.Cutout,
          "mode 2 beats a declared transparency: the keyword is the vanilla fallback for clients without the layer");
        check.Expect(!Covers((int)AlloyClass.Water, true) && !Covers((int)AlloyClass.Additive, true)
          && !Covers((int)AlloyClass.Blended, true),
          "only the cutout class draws through the layer");

        check.Expect(!Blends((int)AlloyClass.Opaque), "plain opaque does not blend");
        check.Expect(!Blends((int)AlloyClass.Cutout), "#357: nor does a cutout -- it writes depth, so it must be opaque");
        check.Expect(Blends((int)AlloyClass.Blended) && Blends((int)AlloyClass.Additive), "blended and additive do");
        check.Expect(Blends((int)AlloyClass.Water), "and so does water -- the engine draws bucket 6 under Enable(0)");
        check.Expect(Discard((int)AlloyClass.Cutout) == 0.2f && Discard((int)AlloyClass.Blended) == 0.2f,
          "the engine's 0.2 cutout, on the two classes it draws under Enable(1)");
        check.Expect(Discard((int)AlloyClass.Opaque) < 0f && Discard((int)AlloyClass.Additive) < 0f
          && Discard((int)AlloyClass.Water) < 0f,
          "and no discard at all on the three it does not");
        // Draw order is the enum's own order, and the staging sort is what depends on it: opaque and cutout
        // share a pass, so they must be adjacent AND first.
        check.Expect((int)AlloyClass.Opaque == 0 && (int)AlloyClass.Cutout == 1 && (int)AlloyClass.Blended == 2
          && (int)AlloyClass.Water == 3 && (int)AlloyClass.Additive == 4, "declaration order IS draw order");
        // #129: and water sits BETWEEN them, which is the whole of its scheduling. The framebuffer copy it
        // reads is taken at this boundary: everything below it is in the copy, everything above is not.
        check.Expect((int)AlloyClass.Water > (int)AlloyClass.Blended && (int)AlloyClass.Water < (int)AlloyClass.Additive,
          "water draws after the blended class (fade is in its copy) and before additive (glow is not)");
        check.Expect(Count == System.Enum.GetValues<AlloyClass>().Length, "Count sizes the per-class arrays");

        log.Info(check.Ok ? "AlloyClasses.SelfCheck: passed" : "AlloyClasses.SelfCheck: FAILED");
    }
}
