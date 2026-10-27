using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>UNIFORM UPLOAD. The per-pass scene terms every Alloy stage shares: the feature and fog terms
/// pushed to whichever program's locations the caller names, and the area light, sun cascades, local cubes,
/// probe and light curve staged once into the <c>CxPass</c> block.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The engine's world clock as the shaders see it, for the census. Everything time-varying in
    /// an owned stage derives from this int, so a frozen value is a frozen world.</summary>
    internal static int TimeOfDay => _runtime == null ? 0 : Rt.TimeOfDay;

    internal static Vector2 AreaWind()
        => _runtime == null ? default : new Vector2(Rt.AreaWind.X, Rt.AreaWind.Y);

    /// <summary>The scene's fog colour, linearised. The block stores it display-space -- the stock shaders
    /// lerp toward it AFTER RevertColorSpace -- and the Alloy frame is linear end to end, so the curve comes
    /// off here rather than in every stage that fogs.</summary>
    private static void UploadFogColour(int loc)
    {
        Vector3 src = FrameInputs.FogColor;
        float* c = stackalloc float[4];
        c[0] = MathF.Pow(MathF.Max(src.X, 0f), AlloyLights.SrgbGamma);
        c[1] = MathF.Pow(MathF.Max(src.Y, 0f), AlloyLights.SrgbGamma);
        c[2] = MathF.Pow(MathF.Max(src.Z, 0f), AlloyLights.SrgbGamma);
        c[3] = 1f;
        Gl.Uniform4fv(loc, 1, c);
    }

    /// <summary>This frame's fog, packed the way every Alloy stage reads it -- enable, start, end and
    /// 1/(end-start). Read out of <see cref="FrameInputs"/> (straight off <c>Scene+0x140</c>) rather than
    /// <c>GLRender::m_fog</c>, so a frame where the engine drew nothing still fogs correctly.</summary>
    private static void PackFog(float* px)
    {
        px[0] = FrameInputs.FogOn ? 1f : 0f;
        px[1] = FrameInputs.FogStart; px[2] = FrameInputs.FogEnd;
        float span = px[2] - px[1];
        px[3] = span != 0f ? 1f / span : 0f;
    }

    /// <summary>Just the scene fog -- the ramp's terms, an eye position and the volume's two (<see cref="FogVolume"/>) --
    /// for a program that wants it and none of the rest of <see cref="UploadFeatureTerms"/>. Every stage that fogs
    /// takes all of them from here, or two stages fog the same fragment differently.</summary>
    internal static void UploadFog(in FeatLoc loc)
    {
        float* px = stackalloc float[4];
        PackFog(px);
        Gl.Uniform4fv(loc.Fog, 1, px);
        UploadFogColour(loc.FogColor);
        fixed (Vector3* eye = &_eyePos) Gl.Uniform3fv(loc.Eye, 1, (float*)eye);
        Vector3 origin = SceneFog.Origin(out bool radial);
        Gl.Uniform4f(loc.FogOrigin, origin.X, origin.Y, origin.Z, radial ? 1f : 0f);
        // The height layer is anchored to the player's feet: an area carries no ground plane to read, and
        // the runtime block is where the engine keeps the one position that tracks them.
        float playerZ = _runtime == null ? 0f : Rt.PlayerPos.Z;
        SceneFog.Upload(loc, playerZ, _capturing);
        FogVolume.Upload(loc.VFog, loc.VFogVp, _capturing);
    }

    /// <summary>Feed one program the engine's own live world state -- the same five values its own shaders
    /// read under these names. Once per program per pass; nothing here is per part.</summary>
    internal static void UploadFeatureTerms(in FeatLoc loc, int* viewport)
    {
        // pixelParams is the ONE term not read out of an engine global -- SetUniforms builds it from the
        // window size, and this pass may be rendering into the probe cube instead, so it comes from the
        // viewport actually bound. Everything else below is the engine's own live value.
        float w = viewport[2] > 0 ? viewport[2] : 1f, h = viewport[3] > 0 ? viewport[3] : 1f;
        float* px = stackalloc float[4];
        px[0] = 1f / w; px[1] = 1f / h; px[2] = w / h;
        Gl.Uniform3fv(loc.Pixel, 1, px);
        Gl.Uniform1f(loc.LodBias, PostChain.LodBias);   // -1 where the stage declares none; GL ignores it
        Gl.Uniform1f(loc.Now, TexStream.Clock);
        UploadFog(loc);
        if (_shaderConstF != null)
        {
            float a = _shaderConstF[KeyholeDiaA], b = _shaderConstF[KeyholeDiaB];
            px[0] = MathF.Min(a, b); px[1] = MathF.Max(a, b); px[2] = _shaderConstF[KeyholeMinZ];
            Gl.Uniform3fv(loc.KhDia, 1, px);
        }
        UploadTargetShape(loc);
        UploadVanillaEnv(loc);
        if (_runtime == null) return;
        ShaderExposedRuntimeData rt = Rt;
        Gl.Uniform1i(loc.Time, rt.TimeOfDay);

        Gl.Uniform3fv(loc.Wind, 1, (float*)Unsafe.AsPointer(ref rt.AreaWind));
        Gl.Uniform1i(loc.Flags, rt.AreaFlags);
        Gl.Uniform3fv(loc.Cam, 1, (float*)Unsafe.AsPointer(ref rt.CamPos));
        Gl.Uniform3fv(loc.CamFocus, 1, (float*)Unsafe.AsPointer(ref rt.CamFocus));
        Gl.Uniform3fv(loc.Player, 1, (float*)Unsafe.AsPointer(ref rt.PlayerPos));
        px[0] = rt.CamDist; px[1] = rt.CamPitch;
        Gl.Uniform2fv(loc.KhCam, 1, px);
    }

    /// <summary>The spell-targeting shape's terms, straight out of the engine's global-constant tables, under
    /// the ENGINE's own uniform names (which <see cref="TargetFs"/> keeps) so a content override of that
    /// library reads the same contract. Only what the shape reads goes up, and nothing is converted: the
    /// colour is the engine's display-space vec4, mixed inside the same encode bracket the fog uses.</summary>
    private static void UploadTargetShape(in FeatLoc loc)
    {
        // The shape's own "nothing to draw" answer is targetShape 0 AND range 0; the object type is the
        // other half of it, and 0 there is what the engine writes when the cursor is over nothing.
        if (_shaderConstI == null || _shaderConstV == null || _shaderConstV4 == null || _shaderConstF == null)
        { Gl.Uniform1i(loc.TgtObjL, 0); Gl.Uniform1i(loc.TgtShapeL, 0); return; }
        Gl.Uniform1i(loc.TgtShapeL, _shaderConstI[TgtShape]);
        Gl.Uniform1i(loc.TgtSelfL, _shaderConstI[TgtOnSelf]);
        Gl.Uniform1i(loc.TgtObjL, _shaderConstI[TgtObjType]);
        Gl.Uniform1f(loc.TgtSizeXL, _shaderConstF[TgtSizeX]);
        Gl.Uniform1f(loc.TgtSizeYL, _shaderConstF[TgtSizeY]);
        Gl.Uniform1f(loc.TgtRangeL, _shaderConstF[TgtRange]);
        Gl.Uniform4fv(loc.TgtColL, 1, &_shaderConstV4[TgtColour].X);
        Gl.Uniform3fv(loc.TgtWorldL, 1, _shaderConstV);
    }

    /// <summary>The rest of the shared vanilla environment -- the <c>CxEngine</c> block (the projection, the
    /// calendar, the weather, the wind) and the scriptable entries the spliced stock files gate on, under the
    /// engine's own names as <see cref="UploadTargetShape"/>. The noir and vision gates are script-set integers,
    /// and zero is "off", which an unresolved constant table leaves them at.</summary>
    internal static void UploadVanillaEnv(in FeatLoc loc)
    {
        if (loc.Engine) BindEngineBlock();
        if (_shaderConstI == null || _shaderConstF == null || _shaderConstV4 == null) return;
        // The whole block, not a selection: a content shader that begins reading a slot nobody thought to
        // wire would otherwise compile, link, and read zero -- working under the engine and silently doing
        // nothing under Alloy. Only the live slots, so a program that declares none makes no calls at all.
        fixed (FeatLoc* l = &loc)
        {
            for (int m = l->SIntLive; m != 0; m &= m - 1)
            { int i = BitOperations.TrailingZeroCount(m); Gl.Uniform1i(l->SInt[i], _shaderConstI[SIntBase + i]); }
            for (int m = l->SFloatLive; m != 0; m &= m - 1)
            { int i = BitOperations.TrailingZeroCount(m); Gl.Uniform1f(l->SFloat[i], _shaderConstF[SFloatBase + i]); }
            for (int m = l->SVecLive; m != 0; m &= m - 1)
            { int i = BitOperations.TrailingZeroCount(m); Gl.Uniform4fv(l->SVec[i], 1, &_shaderConstV4[SVecBase + i].X); }
        }
    }

    /// <summary>ADR 0026: the lit stage's <c>uMaterial</c> -- (modern model, directional ambient, specular AA, no
    /// grazing rise on legacy materials). The two refinements ride on the model; the grazing switch does not.</summary>
    internal static void UploadMaterialModel(int loc)
    {
        bool modern = Lighting.Brdf.V;
        Gl.Uniform4f(loc, modern ? 1f : 0f, modern ? Lighting.AmbientDir.V : 0f, modern && Lighting.SpecAa.V ? 1f : 0f,
                     Lighting.Grazing.V ? 0f : 1f);
    }

    /// <summary>The <c>CxPass</c> block's binding point, pinned by <see cref="UboSlots.Pass"/> against
    /// <c>cx_pass.glsl</c>'s own <c>layout(std140, binding = 0)</c>.</summary>
    internal const uint PassBinding = UboSlots.Pass;
    /// <summary>The block's std140 size, 3620 by glslang reflection, rounded to a vec4.</summary>
    internal const int PassBytes = 3632;
    /// <summary>Member byte offsets in <c>CxPass</c>, read off glslang's std140 reflection of
    /// <c>cx_pass.glsl</c>. The declaration is pinned by <see cref="ShaderContract.SelfCheck"/>; reorder a
    /// member and these move with it. The two float arrays are std140's 16-byte stride.</summary>
    internal const int PbLight = 0, PbDynPage = 1024, PbFade = 2048, PbSunVp = 3072, PbSunTexel = 3328,
                       PbSunBias = 3344, PbSunDyn = 3360, PbSunRadius = 3376, PbLocalSoft = 3392, PbLocalPcss = 3408,
                       PbSunFocus = 3424, PbSunCascades = 3436, PbAmbient = 3440, PbLocalFloor = 3452, PbDiffuse = 3456,
                       PbProbeMips = 3468, PbDir = 3472, PbProbeGain = 3484, PbLocalPlane = 3488, PbSceneEnv = 3496,
                       PbTile = 3504, PbGrid = 3520, PbReach = 3532, PbFalloff = 3536, PbWrap = 3548, PbZ = 3552,
                       PbAtten = 3560, PbHot = 3568, PbSss = 3576, PbWrapN = 3584, PbDayDim = 3588, PbRelevance = 3592,
                       PbBounds = 3600, PbShBudget = 3604, PbLightFloor = 3608, PbFullModel = 3612,
                       PbSunPenum = 3616, PbSearchTaps = 3620;
    /// <summary>The sun terms the last staged pass block carried -- the cascade count, the fourth column of
    /// <c>uSunVp[0]</c> and the live focus. The receiver's own witness, for the <c>[sunrx]</c> clause on
    /// <c>CasterPass</c>'s period line.</summary>
    private static int _rxSunN;
    private static Vector4 _rxSunVp3;
    private static Vector3 _rxSunFocus, _rxAreaDif, _rxAreaDir;

    /// <summary>The sun half of the last pass block, for the period line. Cold.</summary>
    internal static string SunRxNote()
        => $"rx: uSunCascades={_rxSunN} uSunVp[0].col3=({_rxSunVp3.X:F3},{_rxSunVp3.Y:F3},{_rxSunVp3.Z:F3},{_rxSunVp3.W:F3}) " +
           $"uSunFocus=({_rxSunFocus.X:F1},{_rxSunFocus.Y:F1},{_rxSunFocus.Z:F1}) " +
           $"uAreaDiffuse=({_rxAreaDif.X:F3},{_rxAreaDif.Y:F3},{_rxAreaDif.Z:F3}) " +
           $"uAreaDir=({_rxAreaDir.X:F2},{_rxAreaDir.Y:F2},{_rxAreaDir.Z:F2}) binds={PassBinds} sends={PassUploads}";

    /// <summary>The block as staged and as last sent. Native, allocated once, so a refill allocates nothing.</summary>
    private static byte* _passStage, _passSent;
    private static uint _passBuf;
    /// <summary>This period's <see cref="BindPass"/> calls and the block sends (theirs and the lightmap bake's):
    /// a pass costs one send, a pass whose terms did not change none, and every other bind is a fill and a compare.</summary>
    internal static int PassBinds, PassUploads;
    /// <summary>This period's binds that drew through another view's cluster table, walking it whole (the planar
    /// mirror). The screen's own passes must not be among them.</summary>
    internal static int PassBindsBorrowed;

    /// <summary>This pass's shared scene terms into the <c>CxPass</c> block, then the shadow maps onto their
    /// units and the program's own <c>uLocalCubes</c>. Called at each program switch; the block goes up only
    /// when the refill differs from what is bound, so every switch after a pass's first costs a fill and a
    /// compare and no GL uniform traffic. On Vulkan only the staging fill runs.</summary>
    internal static void BindPass(int locCubes, int* viewport, float near, float far)
    {
        PassBinds++;
        byte* b = PassBegin();
        FillPassBlock((PassBlock*)b);
        if (!AlloyLights.FillDraw(b, viewport, near, far)) PassBindsBorrowed++;
        // a Vulkan pass reads the staged block and binds its own shadow maps
        Gl.Uniform1i(locCubes, CasterPass.CubesFresh ? CasterPass.CubeCount : 0);
    }

    /// <summary>The staging block, zeroed, for a caller that fills it itself (the lightmap bake's one light).</summary>
    internal static byte* PassBegin()
    {
        if (_passStage == null)
        {
            _passStage = (byte*)NativeMemory.AllocZeroed(PassBytes);
            _passSent = (byte*)NativeMemory.AllocZeroed(PassBytes);
        }
        new Span<byte>(_passStage, PassBytes).Clear();
        return _passStage;
    }

    /// <summary>Send the staged block if it differs from the one bound. Orphan-and-fill in one call, so a
    /// block the GPU is still reading is never written under it. Rebound with each send (about one per pass),
    /// so nothing that took binding 0 in between is ever read as the pass terms.</summary>
    internal static void FlushPassBlock()
    {
        if (_passBuf != 0 && new ReadOnlySpan<byte>(_passStage, PassBytes).SequenceEqual(new ReadOnlySpan<byte>(_passSent, PassBytes)))
            return;
        if (_passBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _passBuf = b; }
        Gl.NamedBufferData(_passBuf, (IntPtr)PassBytes, _passStage, Gl.DYNAMIC_DRAW);
        Gl.BindBufferBase(Gl.UNIFORM_BUFFER, PassBinding, _passBuf);
        Buffer.MemoryCopy(_passStage, _passSent, PassBytes, PassBytes);
        PassUploads++;
    }

    /// <summary>The block's name died with the context; the next send mints a new one.</summary>
    internal static void PassContextLost() => _passBuf = 0;

    /// <summary>Every <c>CxPass</c> member from this pass's state: the engine's area terms, the cascade and
    /// cube receivers as <see cref="CasterPass"/> published them, and the probe. A stale cascade or cube set
    /// leaves its half zero, which the receivers read as nothing to sample.</summary>
    private static void FillPassBlock(PassBlock* blk)
    {
        if (FrameInputs.HasAreaAmbient)
            // AlloyLights.AmbientScale / SunScale are the live /light dials -- applied here, so a retune shows
            // on the very next pass.
            blk->Ambient = FrameInputs.AreaAmbient * AlloyLights.AmbientScale;
        else
        {
            blk->Ambient = new Vector3(DefaultAmbient);
            if (!Diag._ambientFallbackLogged)
            {
                Diag._ambientFallbackLogged = true;
                _log?.Info("AlloyRenderer: no area ambient captured yet -- Alloy lit parts on the grey stand-in " +
                           "(if this persists, SetLightGL's index -1 path never fired)");
            }
        }
        blk->Diffuse = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse * AlloyLights.SunScale : default;
        blk->Dir = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuseDir : new Vector3(0f, 0f, 1f);

        if (CasterPass.CascadesFresh)
        {
            int n = CasterPass.CascadeCount;
            for (int i = 0; i < n; i++)
            {
                blk->SunTexel[i] = CasterPass.CascadeTexel[i];
                float range = CasterPass.CascadeRange[i];
                blk->SunBias[i] = range > 0f ? _sunWorldBias / range : 0f;
                blk->SunRadius[i] = CasterPass.CascadeFitRadius[i];
            }
            new ReadOnlySpan<float>(CasterPass.CascadeVp, 0, n * 16).CopyTo(MemoryMarshal.Cast<Matrix4x4, float>((Span<Matrix4x4>)blk->SunVp));
            new ReadOnlySpan<float>(CasterPass.SunDynMask, 0, 4).CopyTo((Span<float>)blk->SunDyn);
            blk->SunFocus = CasterPass.SunFocusLive;
            blk->SunCascades = n;
        }
        // Cold (read once per period by CasterPass's [sunrx] clause): what this pass block ACTUALLY carries
        // for the sun, taken off the staged bytes rather than off the publisher, so the witness cannot
        // disagree with what the receiver samples.
        _rxSunN = blk->SunCascades;
        _rxSunVp3 = new Vector4(blk->SunVp[0].M41, blk->SunVp[0].M42, blk->SunVp[0].M43, blk->SunVp[0].M44);
        _rxSunFocus = blk->SunFocus;
        _rxAreaDif = blk->Diffuse;
        _rxAreaDir = blk->Dir;

        blk->LocalFloor = AlloyLights.ShadowFloor;
        // Source radius (0 = no raw view, no search), temporal phase (golden-ratio turns per frame, 0
        // with no taa to integrate it), disc taps; w = the view depth past which no cube is read (0 = none)
        blk->LocalPcss = new Vector4(RawSearch ? AlloyLights.ShadowSource : 0f,
                PostStack.TaaLive ? (_frameSeq * 0.618034f) % 1f : 0f, Shadows.Taps.V, AlloyLights.ShadowDist);
        // CX-49: the sun's own source size, as penumbra texels per unit of stored cascade depth -- the same
        // "no raw view, no search" gate the cubes take, and the same temporal phase and tap count out of
        // uLocalPcss above. A caller that fills the block itself (the lightmap bake) leaves it zero, which
        // the receiver reads as the fixed four-tap cross.
        blk->SunPenum = RawSearch ? CasterPass.SunPenumbraScale : 0f;
        blk->SearchTaps = Shadows.Search.V;
        // Pixels -> world radius per unit view depth, so the shader compares its disc against the
        // screen without knowing the projection. 0 (no perspective this pass) = the disc everywhere.
        float discZ = _lodScale > 0f ? AlloyLights.ShadowDiscPx / _lodScale : 0f;
        blk->LocalSoft = new Vector4(AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld, discZ, AlloyLights.ShadowCap);
        // shadow.perpixel, the per-pixel SHADOW budget every walk and the deferred record share.
        // A caller that fills the block itself (the lightmap bake) leaves it zero, which every reader takes as
        // "no budget" -- the pre-budget path.
        blk->ShBudget = AlloyLights.ShadowPerPixel;
        // light.floor, the brightness a light's REACH ends at. Zero in a block a caller filled
        // itself (the lightmap bake, which wants every light's authored radius), which reads as no cut.
        blk->LightFloor = AlloyLights.LightFloor;
        // light.fullmodel, the lighting LOD's cut. Zero in a self-filled block (the bake), which
        // reads as the full model for every light -- the bake has exactly one light and it is the hero.
        blk->FullModel = AlloyLights.FullModel;
        if (CasterPass.CubesFresh)
        {
            int n = CasterPass.CubeCount;
            new ReadOnlySpan<float>(CasterPass.CubeLight, 0, n * 4).CopyTo(MemoryMarshal.Cast<Vector4, float>((Span<Vector4>)blk->Light));
            for (int i = 0; i < n; i++) { blk->DynPage[i].Value = CasterPass.CubeDynPage[i]; blk->Fade[i].Value = CasterPass.CubeFade[i]; }
            blk->LocalPlane = new Vector2(CasterPass.CubeNearPlane, 1f / CasterPass.CubeFaceSize);
        }

        blk->ProbeMips = ProbeLive ? EnvProbe.Mips : 0f;
        blk->ProbeGain = EnvProbe.Gain;
        blk->SceneEnv = _sceneEnv ? 1 : 0;
    }

    /// <summary>May this pass sample the IBL probe? Never during a capture: sampling an image that is an
    /// attachment of the target being written is undefined, and the capture draws through the lit stage.</summary>
    private static bool ProbeLive => (!_capturing || _planar) && EnvProbe.Ready;

    /// <summary>The blocker search has its raw views: Vulkan's bake binds its own.</summary>
    private static bool RawSearch => true;
}
