using System.Numerics;
using CX.Engine;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>The frame a native stage and its passes see, as one record: the MAIN view's camera and size whichever view
/// a program is drawn into, and whether the stage reading it may trust its history.</summary>
internal readonly struct StageFrame
{
    internal readonly Matrix4x4 InvVp, PrevVp;
    internal readonly Vector3 Eye;
    internal readonly int W, H, Seq;
    /// <summary>Is this frame's history garbage -- images just (re)made, or the first frame after a gap?</summary>
    internal readonly bool Fresh;

    internal StageFrame(Matrix4x4 invVp, Matrix4x4 prevVp, Vector3 eye, int w, int h, int seq, bool fresh)
    {
        InvVp = invVp; PrevVp = prevVp; Eye = eye; W = w; H = h; Seq = seq; Fresh = fresh;
    }
}

/// <summary>The <c>cx*</c> frame uniforms, located in one program by name: no environment declares them, so a stage
/// declares the ones it reads and gets them set -- a native one or a stage the engine also compiles, which leaves
/// them 0. Every host uploads the same set (<c>docs/shader-overrides.md</c>, "The frame").</summary>
internal unsafe struct FrameLoc
{
    internal int Frame, Main, Fresh, InvVp, PrevVp, Eye, View, Overlay;

    /// <summary>Does the program declare any of them? A program that declares none costs its host nothing per bind.</summary>
    internal readonly bool Any => Frame >= 0 || Main >= 0 || Fresh >= 0 || InvVp >= 0 || PrevVp >= 0 || Eye >= 0
                                || View >= 0 || Overlay >= 0;

    internal static FrameLoc Locate() => new()
    {
        Frame = LooseBlock.Loc("cxFrame"), Main = LooseBlock.Loc("cxMain"),
        Fresh = LooseBlock.Loc("cxFresh"), InvVp = LooseBlock.Loc("cxInvVp"),
        PrevVp = LooseBlock.Loc("cxPrevVp"), Eye = LooseBlock.Loc("cxEye"),
        View = LooseBlock.Loc("cxView"), Overlay = LooseBlock.Loc("cxOverlay"),
    };

    /// <summary>Upload the set into the open loose block. <paramref name="main"/> is <c>cxMain</c>: 1 for the
    /// on-screen view, 0 for a probe face or a reflection. Hot, and it allocates nothing.</summary>
    internal readonly void Upload(in StageFrame f, bool main, bool overlay)
    {
        LooseBlock.Set(Frame, f.Seq);
        LooseBlock.Set(Main, main ? 1 : 0);
        LooseBlock.Set(Fresh, f.Fresh ? 1 : 0);
        // raw row-major bytes, read column-major by GLSL: the transpose, so `M * v` in the stage is exact
        // (the convention MotionVectors' uInvVP/uPrevVP upload takes).
        LooseBlock.Set(InvVp, f.InvVp);
        LooseBlock.Set(PrevVp, f.PrevVp);
        Vector3 eye = f.Eye;
        LooseBlock.Set(Eye, new ReadOnlySpan<float>((float*)&eye, 3));
        LooseBlock.Set(View, f.W, f.H);
        LooseBlock.Set(Overlay, overlay ? 1 : 0);
    }
}

/// <summary>The engine terms a WHOLE stage takes -- the sky's fragment stage and a compute pass, which run under the
/// engine's own names (<c>cx_stageenv.glsl</c>): the scriptable, fog and camera block every Alloy stage gets, and the
/// calendar and weather scalars from <c>m_shaderExposedRuntimeData</c>, which ride the <c>CxEngine</c> block the
/// feature upload binds. One upload feeds both.</summary>
internal unsafe struct StageEnvLoc
{
    internal AlloyRenderer.FeatLoc Feat;

    /// <summary>Resolve every name in one program. A location the program does not declare is -1, which GL
    /// ignores, so the same set is uploaded to a stock sky stage and to a pass alike.</summary>
    internal static StageEnvLoc Locate() => new() { Feat = Programs.FeatLocs() };

    /// <summary>Upload the whole block into the program currently bound. Hot -- one call per program per pass, and it
    /// allocates nothing.</summary>
    internal readonly void Upload(int* viewport) => AlloyRenderer.UploadFeatureTerms(Feat, viewport);
}
