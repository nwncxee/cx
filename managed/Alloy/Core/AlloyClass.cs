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
    /// declared <c>NWN_ALPHA_MODE 2</c>; what is left is non-opaque because the DIFFUSE carries alpha. Water
    /// is tested first, the engine's own precedence: later would file a sea as blended.</summary>
    internal static AlloyClass Of(bool water, bool additive, bool opaque, bool declaredTransparent,
                                  bool alphaBelowOne, bool coverage)
        => water ? AlloyClass.Water
         : additive ? AlloyClass.Additive
         // CX-224: a declared coverage material is in the cutout class whatever its diffuse MEASURES. Its
         // shader may read coverage from a map other than texture0, so an opaque texture0 is no evidence at
         // all -- and reading it as evidence filed such a part opaque, out of the layer's batch range and
         // into the diffuse-only depth prepass, where its solid silhouette would fill the holes the coverage
         // leaves open. Only a DECLARED mode 2 gets here: an unknown material is classified as before.
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

        check.Expect(Of(true, true, true, true, true, true) == AlloyClass.Water, "water beats every other term");
        check.Expect(Of(false, true, true, false, false, true) == AlloyClass.Additive, "additive beats opaque and coverage");
        check.Expect(Of(false, true, false, true, true, false) == AlloyClass.Additive, "additive beats the transparency terms");
        check.Expect(Of(false, false, true, true, true, false) == AlloyClass.Opaque, "an opaque part is opaque whatever it declares");
        check.Expect(Of(false, false, false, true, false, false) == AlloyClass.Blended, "declared transparency blends");
        check.Expect(Of(false, false, false, false, true, false) == AlloyClass.Blended, "a sub-1 part alpha blends");
        check.Expect(Of(false, false, false, false, false, false) == AlloyClass.Cutout, "texture alpha alone is a cutout");

        // CX-224's table, expected classes written out by hand: a declared NWN_ALPHA_MODE 2 reaches the
        // coverage layer off ANY diffuse, nothing else does, and a material that declares nothing is
        // classified exactly as it was before the term existed.
        AlloyClass modeTwoOpaque = Of(false, false, true, false, false, true);
        check.Expect(modeTwoOpaque == AlloyClass.Cutout && Covers((int)modeTwoOpaque, true),
          "mode 2 over an OPAQUE diffuse must reach the coverage layer, not the opaque pass");
        check.Expect(Of(false, false, true, false, false, false) == AlloyClass.Opaque
          && !Covers((int)AlloyClass.Opaque, false),
          "an opaque diffuse with no mode 2 is a plain opaque part");
        check.Expect(Of(false, false, false, false, false, false) == AlloyClass.Cutout
          && !Covers((int)AlloyClass.Cutout, false),
          "a graded diffuse with no mode 2 stays the plain 0.2 cutout -- an unknown material never becomes coverage");
        check.Expect(Of(false, false, false, true, false, true) == AlloyClass.Cutout,
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
