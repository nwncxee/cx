using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.PartTable;
using static CX.Alloy.TexProbe;
namespace CX.Alloy;

/// <summary>The material rules the frame still asks, and their constants: which draw buckets and late keywords a
/// material lands in, which vertex stage it names, and a texture's name for diagnostics. The ownership census
/// that gave the class its name went with the engine-scene path.</summary>
internal static unsafe partial class AlloyCensus
{
    /// <summary>The engine's per-item-alpha draw bucket.</summary>
    internal const int AlphaBucket = 8;

    /// <summary><c>dst == 2</c> is <c>GL_ONE</c>: the engine's own additive test on the material's
    /// destination blend factor, and the one pair it turns the depth mask off for.</summary>
    internal const int BlendOne = 2;

    /// <summary>The dynamic gob pair: everything instanced lands in one of these two -- placeables, doors, and the
    /// tileset's own static objects. Ownable only as a PAIR, since the engine walks 2 then 3 and owning one without
    /// the other splits a single instance's geometry across two renderers.</summary>
    internal const int GobOpaqueBucket = 2, GobBlendBucket = 3;

    /// <summary><c>dumptex</c> (cx.cfg): log only parts whose diffuse texture name contains this, one frame's worth at a
    /// time, for as long as it takes the camera to find them.</summary>
    internal static readonly string? _dumpTex = Renderer.DumpTex.Boot.Length == 0 ? null : Renderer.DumpTex.Boot.ToLowerInvariant();

    /// <summary><c>sample_framebuffer</c>'s two values: 1 routes to draw bucket 5 (the late class), 2 to
    /// bucket 6 (water). Different classes, not degrees of one.</summary>
    internal const int SampleFbWater = 2;

    /// <summary><c>sample_framebuffer 1</c> -- draw bucket 5. A draw-ORDER keyword only ("renders near the end of
    /// the frame"); the refraction is the material's own fragment shader's business, and the copy it would read is
    /// bound on texture units 11/12 as <c>texFBColor</c>/<c>texFBDepth</c>
    /// (<c>Camera::BindRenderSubframeTextures</c> @0x4a6ab0, from <c>CopyRenderSubframe</c> @0x4a6b90).</summary>
    internal const int SampleFbLate = 1;

    /// <summary>The engine's late draw bucket: <c>sample_framebuffer 1</c> parts.</summary>
    internal const int LateBucket = 5;

    /// <summary>Distinct unrecognised custom-shader names the diag names. A handful is a report; the whole
    /// set would be a wall, and past a handful the answer is "port more features", not "list them all".</summary>
    internal const int MaxUnknown = 6;

    internal static readonly string[] _unknown = new string[MaxUnknown];

    /// <summary>Does a material's pair of late-draw keywords refuse the part outright? <c>volumetric</c> always
    /// does -- the engine draws it twice per pass, back then front faces, and files it in buckets 3 AND 5 -- and so
    /// does any <c>sample_framebuffer</c> that is neither water nor <see cref="SampleFbLate"/>. <paramref name="water"/>
    /// is <see cref="IsWater"/>'s verdict, not <c>sampleFb == 2</c>: bumpy-shiny reaches bucket 6 keyword-less.</summary>
    internal static bool RefusedByLateKeywords(int sampleFb, bool volumetric, bool water)
        => volumetric || (sampleFb != 0 && !water && sampleFb != SampleFbLate);

    /// <summary>Draw buckets the Alloy pass takes parts from: the static tile pair 0/1, the residual static
    /// classes 4 additive, 7 the tilefade cap, 8 the fade buckets, the dynamic gob pair 2/3, and 5 the late class.
    /// Membership here and suppression in <c>AlloyRenderer.IterSight</c> are ONE list: suppressing a bucket we do
    /// not draw loses geometry, and drawing one we do not suppress double-draws it.</summary>
    internal static bool OwnableBucket(int bucket)
        => bucket == 0 || bucket == 1 || bucket == GobOpaqueBucket || bucket == GobBlendBucket
           || bucket == 4 || bucket == 7 || bucket == AlphaBucket || bucket == LateBucket
           || (bucket == AlloyRenderer.WaterBucket && FrameSnapshot.Usable);

    internal static string TexName(IntPtr tex)
    {
        if (tex == IntPtr.Zero) return "-";
        IntPtr real = new CAurTexture(tex).GetReferencedTexture().Ptr;
        if (real == IntPtr.Zero) real = tex;
        CAurTextureBasic basic = new CAurTexture(real).AsCAurTextureBasic();
        return basic.IsNull ? "?" : Marshal.PtrToStringAnsi((IntPtr)Unsafe.AsPointer(ref basic.Name)) ?? "?";
    }
}
