using System;

namespace CX.Alloy;

/// <summary>One part offered to the Alloy world pass at enrollment. Everything the Alloy draw needs
/// that is stable for the lifetime of the ownership -- the per-frame values (texture id, index range, model
/// matrix) are re-read live in the draw loop, never cached here.</summary>
internal struct AlloyCandidate
{
    /// <summary>The PartTriMesh*, and the identity pair the draw loop revalidates against.</summary>
    public IntPtr Tri, Node, Mat;
    public IntPtr Vdo;
    /// <summary>Batch sort key: the diffuse CAurTexture* seen at capture. Used ONLY as an opaque grouping
    /// hint -- never dereferenced (borrowed pointer) and never trusted as the live texture, which the draw
    /// loop re-fetches. Material* would be useless here: each part owns its own Material instance, so pointer
    /// identity shatters every group to size 1.</summary>
    public IntPtr Tex0;
    public int Item, Prim;
    /// <summary>The engine would draw this part LIT. Ghidra <c>PartTriMesh::MatStatic</c> @0x450560 and
    /// <c>RenderFlat</c> @0x450eb0 agree on the test: lighting cap 5 on (a per-PASS state, checked at draw
    /// time, not here) and <c>mat+0x98</c> clear -- either one false and the engine sends flat white, which
    /// is the fullbright variant. False here means the material itself opted out.</summary>
    public bool Lit;
    /// <summary>The material declares an env map (<c>Material::HasEnvMap</c>, mat+0x40). No texture is
    /// carried with it -- the per-area IBL probe replaces what it held -- but the flag itself is what the
    /// engine's own shaders read as "this is shiny" on 2002 content, and it drives the same specularity and
    /// roughness synthesis here.</summary>
    public bool Env;
    /// <summary>The <see cref="ShaderFeature"/> this part's SharedMaterial asked for by name, 0 for
    /// none. Stable for the material's lifetime -- the .mtr is parsed once at load.</summary>
    public int Feat;
    /// <summary>The <see cref="AlloyClass"/> this part draws in -- which Alloy pass, with what depth
    /// and blend state. Set even when the candidate is then refused, so the refusal can be counted against
    /// the class it would have joined (the blended class is all-owned-or-none).</summary>
    public int Cls;
    /// <summary>The draw bucket this part was captured in. Kept for the one-owner AUDIT, not for the draw:
    /// bucket membership is recomputed every frame, so it is a diagnostic hint rather than a fact -- but
    /// "which class of geometry is going unsuppressed" is not answerable without it.</summary>
    public int Bucket;
    /// <summary>The <c>Gob</c> this part instances off (part+0x50), or zero for static geometry.
    /// Published even when the candidate is REFUSED, and by every gate from the part load onward: creature
    /// colour is all-owned-or-none per instance, so enrollment has to be able to name the instance a refusal
    /// belongs to and drop the rest of it with the refused part.</summary>
    public IntPtr Gob;
    /// <summary>The material's blend pair as <c>Aurora::AuroraBlendModes</c> (mat+0x90/+0x94), which
    /// the blended and additive passes translate to <c>glBlendFunc</c>. Part of the batch key: one multidraw
    /// carries one blend state.</summary>
    public int Src, Dst;
    /// <summary>The SharedMaterial's <c>twosided</c> keyword -- this part draws with face culling OFF.
    /// False (cull back faces) is the engine's default and what a part with no material gets. Part of the
    /// batch key: culling is GL state, and one multidraw cannot vary it.</summary>
    public bool TwoSided;
}
