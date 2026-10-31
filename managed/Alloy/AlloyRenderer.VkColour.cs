using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// The colour bracket on Vulkan: the depth prepass, the G-buffer and the opaque, cutout and blended batches, recorded
/// from the same staged tables GL draws from. Every per-bind uniform the stages read -- the pass block, each program's
/// loose block -- is packed into one per-frame arena uploaded ahead of the render pass that reads it, and every per-batch
/// value is a push constant. A resource whose pass is not on Vulkan yet is a stand-in that reads as "nothing here".
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>One translated program: its modules, its loose block's layout, the locations GL's setters resolve
    /// against it, and its pipelines by state key.</summary>
    private sealed class VkProg
    {
        public VkPrograms.Id Id;
        /// <summary>Reads the lit stage's terms (<see cref="L"/>): the lit programs and every content program.</summary>
        public bool Lit;
        /// <summary>Writes the G-buffer rather than the scene colour: the deferred lit program and every deferred twin.</summary>
        public bool Deferred;
        /// <summary>Writes the reflection record (<c>oReflN</c>/<c>oReflW</c>); every other program leaves the pair alone.</summary>
        public bool Refl;
        /// <summary>A content pair's program: its feature id and the pair's build generation it came from; 0 otherwise.</summary>
        public int Feat, Epoch;
        public string Label = "";
        public ShaderId Vs, Fs;
        public LooseBlock.Member[] Members = [];
        public Dictionary<string, int> Names = new();
        public byte[] Block = [];
        public VkOpaque[] Opaques = [];
        /// <summary>Declares the <c>CxEngine</c> block, so a bind needs the frame's copy staged.</summary>
        public bool ReadsEngine;
        public LitLoc L;
        public FeatLoc F;
        public int Vp = -1, Tex = -1, Viz = -1, AlphaMip = -1, Tint = -1, SkyFog = -1, Oit = -1, OitDim = -1;
        /// <summary>The sky wrapper's two: the engine's inverse view (its translation column alone) and the
        /// constant colour a content stage reads instead of the record's alpha. -1 on every other program.</summary>
        public int ViewInv = -1, Global = -1;
        public readonly Dictionary<long, PipelineId> Pipes = new();
        /// <summary>The native stages it is built from (-1: none), its declarations that are their images, and where
        /// their frame uniforms sit (<see cref="VkStageAttach"/>).</summary>
        public int StageA = -1, StageB = -1;
        public StageResources.VkBind[] StageBinds = [];
        public FrameLoc StageFrame;
    }

    /// <summary>The colour bracket's resources on one device, replaced whole with the device.</summary>
    private sealed class VkColourState
    {
        public VkBackend? Dev;
        public bool Failed, Live;
        public readonly VkProg?[] Progs = new VkProg?[VkPrograms.All.Length];
        /// <summary>Content programs by <see cref="VkContentKey"/>; null for one that did not build (logged once).</summary>
        public readonly Dictionary<int, VkProg?> Content = new();
        /// <summary>The pair build generation a content program last failed at: it is tried again when GL rebuilds.</summary>
        public readonly Dictionary<int, int> ContentFailed = new();
        /// <summary>The content table's Vulkan copy, its handles as bindless indices, and the bytes last sent.</summary>
        public BufferId ContentRows;
        public byte[] ContentSent = [], ContentStage = [];
        /// <summary>The bytes of <see cref="ContentSent"/> the buffer holds, and the table and mirror versions they came from.</summary>
        public int ContentSentN;
        public long ContentVersion = -1, ContentTexGen = -1;
        /// <summary>This frame's <c>CxEngine</c> block in the arena, -1 until a program that reads it binds.</summary>
        public int EngineAt = -1;
        public readonly VertexLayoutId[] Layouts = new VertexLayoutId[MeshArena.Formats];
        public ImageId Hdr, Depth, G0, G1, G2;
        /// <summary>The prepass depth as a SAMPLED copy: the colour pass's draws cannot sample their own depth
        /// attachment, and the lit stage tests every AO fetch against the depth the estimate was computed from.</summary>
        public ImageId DepthRead;
        public int W, H;
        public BufferId Records, Slots, Bones, TexH, Arena, ZeroUbo, ZeroSsbo, Plt;
        public long RecCap, SlotCap, BoneCap, TexHCap, PltCap;
        /// <summary>The PLT rows' and master palette's send counts last mirrored; -1 = never.</summary>
        public long PltSends = -1, PaletteSends = -1;
        public ImageId Palette;
        /// <summary>Records the GL upload named since the last Vulkan one; <see cref="DirtyAll"/> when it overflowed.</summary>
        public int[] Dirty = new int[1024];
        public int DirtyN;
        public bool DirtyAll = true;
        public uint[] TexHTable = [];
        public byte[] ArenaBytes = new byte[ArenaCap];
        public int ArenaUsed;
        public ImageId Tex2D, TexU2D, Tex2DArray, Tex3D, TexCube, TexCubeArray, Depth2DArray, DepthCubeArray, DecalLayer, DecalSurf;
        public SamplerId Linear, Shadow;
        /// <summary>The blocker search's sampler: no compare and NEAREST, so a raw view reads one stored depth
        /// rather than a blend of four, which is what GL's own sampler object strips the arrays down to.</summary>
        public SamplerId Raw;
        public CommandList? Side;
        /// <summary>The frame's async queue work: the froxel build and the fog volume, beside the prepass through the G-buffer pass.</summary>
        public CommandList? Async;
        public uint WhiteIndex, NullIndex;
        public long Drawn, Skipped;
        /// <summary>This frame's colour pass wrote the G-buffer.</summary>
        public bool GbufWritten;
        /// <summary>The shading-rate image the G-buffer pass being issued reads, or null: its pipelines are made for it.</summary>
        public ImageId Rate;
        /// <summary>The blended range is being issued and this frame's deferred walk wrote the light record it reads.</summary>
        public bool LightRec;
        /// <summary>The OIT accumulation is being issued: the lit and unlit stages write the weighted pair (<c>uOit</c> 1).</summary>
        public bool Oit;
        /// <summary>The blended batches this frame's accumulation stepped over, for the pass after the resolve.</summary>
        public int OitOut;
        /// <summary>The shadow terms a lit bind was handed have been said once.</summary>
        public bool ShadowSaid;
        /// <summary>A CPU multi-draw's index-buffer byte offsets as element indices, per call.</summary>
        public int[] Firsts = new int[4096];
    }

    private static VkColourState _vkc = new();

    /// <summary>The Vulkan frame's scene depth, which CX's windows test their world anchors against; null id before one.</summary>
    internal static ImageId VkSceneDepth => _vkc.Depth;

    /// <summary>The per-frame uniform arena: every bind's pass block and loose block in a frame, at the device's offset
    /// alignment. Past it a frame refuses (logged) rather than reallocating under a list that names the old buffer.</summary>
    private const int ArenaCap = 4 << 20;
    /// <summary>The stand-in blocks' size: past every block a colour stage declares.</summary>
    private const int ZeroUboBytes = 16384, ZeroSsboBytes = 1 << 20;
    /// <summary>The draw buffer the G-buffer's first lane sits on, as GBuffer attaches it on GL.</summary>
    private const int GbufFirst = 4;
    /// <summary>Attachments the colour pass names: scene colour, three slots routed nowhere, the G-buffer's three.</summary>
    private const int ColourTargets = 7;

    /// <summary>Which attachment set a pipeline draws into.</summary>
    private enum VkTargets : byte { Prepass, Colour, Forward, Decal, Mask, Velocity, ColourRefl, Probe, Oit, ForwardDepth, Cover }

    /// <summary>The seven per-batch values, in <c>VkGlsl.Push</c>'s order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VkBatchPush
    {
        public int DrawBase;
        public float AlphaCut;
        public int SsaoOn;
        public float AoDirect;
        public int Decal, Solid, OitGob;
    }

    /// <summary>The GL upload's dirty records, handed over before it clears them: the Vulkan record table follows the
    /// same moves a frame later in the same frame. <paramref name="whole"/> = GL sent the table whole.</summary>
    internal static void VkNoteDirty(int[] dirty, int n, bool whole)
    {
        VkColourState s = _vkc;
        if (s.DirtyAll) return;
        if (whole || s.DirtyN + n > s.Dirty.Length) { s.DirtyAll = true; s.DirtyN = 0; return; }
        Array.Copy(dirty, 0, s.Dirty, s.DirtyN, n);
        s.DirtyN += n;
    }

    /// <summary>The device's colour resources and programs, made once per device; false (Alloy failed, loudly) when one
    /// refused. The targets follow the render size.</summary>
    private static bool VkEnsure(VkBackend vk, int w, int h, ExtenderLog log)
    {
        if (_vkc.Dev != vk) _vkc = new VkColourState { Dev = vk };
        VkColourState s = _vkc;
        if (s.Failed) return false;
        if (s.ZeroUbo.IsNull && !VkMakeStatic(vk, s, log)) { s.Failed = true; return false; }
        if (s.W != w || s.H != h)
        {
            vk.Destroy(s.Hdr); vk.Destroy(s.Depth); vk.Destroy(s.G0); vk.Destroy(s.G1); vk.Destroy(s.G2);
            vk.Destroy(s.DepthRead);
            // the deferred dispatch loads and stores the scene colour as an image, so it is a storage image too
            s.Hdr = VkTarget(vk, Format.Rgba16F, w, h, "vk scene colour", ImageUse.Storage);
            s.Depth = VkTarget(vk, Format.D32F, w, h, "vk scene depth");
            s.G0 = VkTarget(vk, Format.Rgba32Ui, w, h, "vk g-buffer 0", alias: AliasLanes.Wide);
            s.G1 = VkTarget(vk, Format.Rgba32Ui, w, h, "vk g-buffer 1", alias: AliasLanes.Second);
            s.G2 = VkTarget(vk, Format.R32Ui, w, h, "vk g-buffer 2", alias: AliasLanes.Narrow);
            s.DepthRead = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, w, h, 1, 1, 1, 1,
                                                       ImageUse.Sampled | ImageUse.CopyDst, "vk scene depth copy"));
            s.W = w; s.H = h;
            if (s.Hdr.IsNull || s.Depth.IsNull || s.G0.IsNull || s.G1.IsNull || s.G2.IsNull || s.DepthRead.IsNull)
            {
                s.Failed = true;
                log.Always($"AlloyRenderer: the Vulkan scene targets at {w}x{h} were REFUSED (logged above)");
                return false;
            }
        }
        return true;
    }

    /// <summary>A scene target: sampled by the passes after it, and a copy source -- the depth's readable copy is
    /// taken from the attachment itself.</summary>
    private static ImageId VkTarget(VkBackend vk, Format f, int w, int h, string label, ImageUse extra = ImageUse.None, string? alias = null)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1,
                                        (f.IsDepth() ? ImageUse.DepthTarget : ImageUse.ColorTarget)
                                        | ImageUse.Sampled | ImageUse.CopySrc | extra, label, alias));

    /// <summary>Everything that does not follow the render size: the programs, the layouts, the stand-ins, the arena.</summary>
    private static bool VkMakeStatic(VkBackend vk, VkColourState s, ExtenderLog log)
    {
        for (int f = 0; f < MeshArena.Formats; f++)
        {
            s.Layouts[f] = vk.CreateVertexLayout(MeshArena.VkLayout((MeshArena.Fmt)f));
            if (s.Layouts[f].IsNull) { log.Always($"AlloyRenderer: the Vulkan device REFUSED arena layout {(MeshArena.Fmt)f}"); return false; }
        }
        foreach (VkPrograms.Id id in VkPrograms.All)
        {
            VkProg? p = VkBuild(vk, id, log);
            if (p == null)
            {
                log.Always($"AlloyRenderer: the Vulkan {id} program did not build (logged above)");
                return false;
            }
            s.Progs[(int)id] = p;
        }
        s.ZeroUbo = vk.CreateBuffer(new BufferDesc(ZeroUboBytes, BufferUse.Uniform, MemoryKind.Device, "vk zero block"));
        s.ZeroSsbo = vk.CreateBuffer(new BufferDesc(ZeroSsboBytes, BufferUse.Storage, MemoryKind.Device, "vk zero storage"));
        s.Arena = vk.CreateBuffer(new BufferDesc(ArenaCap, BufferUse.Uniform, MemoryKind.Upload, "vk uniform arena"));
        s.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        s.Shadow = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp, CompareOp.LessEqual));
        s.Raw = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Clamp));
        s.Tex2D = VkStandIn(vk, ImageKind.Tex2D, Format.Rgba8, 1, "2D");
        s.TexU2D = VkStandIn(vk, ImageKind.Tex2D, Format.Rgba32Ui, 1, "uint 2D");
        s.Tex2DArray = VkStandIn(vk, ImageKind.Tex2DArray, Format.Rgba8, 1, "2D array");
        s.Tex3D = VkStandIn(vk, ImageKind.Tex3D, Format.Rgba8, 1, "3D");
        s.TexCube = VkStandIn(vk, ImageKind.Cube, Format.Rgba8, 6, "cube");
        s.TexCubeArray = VkStandIn(vk, ImageKind.CubeArray, Format.Rgba8, 6, "cube array");
        s.Depth2DArray = VkStandIn(vk, ImageKind.Tex2DArray, Format.D32F, 1, "shadow array");
        s.DepthCubeArray = VkStandIn(vk, ImageKind.CubeArray, Format.D32F, 6, "shadow cube array");
        s.DecalLayer = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, 1, 1, 1, 1, 1, 1, ImageUse.Storage, "vk decal layer stand-in"));
        s.DecalSurf = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg32F, 1, 1, 1, 1, 1, 1, ImageUse.Storage, "vk decal surface stand-in"));
        s.Side = new CommandList("vulkan colour pass", 1 << 20, log);
        s.Async = new CommandList("vulkan async", 64 << 10, log) { Async = true };
        if (s.ZeroUbo.IsNull || s.ZeroSsbo.IsNull || s.Arena.IsNull || s.Tex2D.IsNull || s.TexU2D.IsNull || s.Tex2DArray.IsNull
            || s.Tex3D.IsNull || s.TexCube.IsNull || s.TexCubeArray.IsNull || s.Depth2DArray.IsNull || s.DepthCubeArray.IsNull
            || s.DecalLayer.IsNull || s.DecalSurf.IsNull || s.Linear.IsNull || s.Shadow.IsNull || s.Raw.IsNull)
        {
            log.Always("AlloyRenderer: the Vulkan colour pass's constant resources were REFUSED (logged above)");
            return false;
        }
        log.Info($"AlloyRenderer: the Vulkan colour programs are built -- {VkPrograms.All.Length} programs, {MeshArena.Formats} arena layouts");
        return true;
    }

    /// <summary>The stand-ins handed over for sampling in the submit the async list waits on: either queue binds them, so
    /// neither may move one while the other runs. The caster maps were handed over by their own flush.</summary>
    private static void VkHandOverStandIns(CommandList cl)
    {
        VkColourState s = _vkc;
        ReadOnlySpan<ImageId> stand = [s.Tex2D, s.TexU2D, s.Tex2DArray, s.Tex3D, s.TexCube, s.TexCubeArray, s.Depth2DArray, s.DepthCubeArray];
        cl.ReadImages(stand);
    }

    /// <summary>A stand-in of one texel per layer: transparent black, or a depth of 1.0 that every shadow compare passes.</summary>
    private static ImageId VkStandIn(VkBackend vk, ImageKind kind, Format f, int faces, string label)
    {
        int layers = kind is ImageKind.Cube or ImageKind.CubeArray ? 1 : 1;
        ImageId img = vk.CreateImage(new ImageDesc(kind, f, 1, 1, 1, 1, layers, 1, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.Async,
                                                   $"vk stand-in {label}"));
        if (img.IsNull) return img;
        Span<byte> texel = stackalloc byte[16];
        texel.Clear();
        if (f == Format.D32F) MemoryMarshal.Write(texel, 1f);
        int bytes = (int)VkBackend.LevelBytes(new ImageDesc(kind, f, 1, 1, 1, 1, layers, 1, ImageUse.Sampled, ""), 0);
        for (int layer = 0; layer < faces; layer++) vk.Upload(img, 0, layer, texel[..bytes]);
        return img;
    }

    /// <summary>Named program <paramref name="id"/>, built by <see cref="VkBuild(VkBackend, VkProgramText, string, bool, ExtenderLog)"/>.</summary>
    private static VkProg? VkBuild(VkBackend vk, VkPrograms.Id id, ExtenderLog log)
    {
        bool lit = id is VkPrograms.Id.Lit or VkPrograms.Id.LitDeferred or VkPrograms.Id.Decal;
        VkProg? p = VkBuild(vk, VkPrograms.Text(id, log), $"{id}", lit, log);
        if (p != null) { p.Id = id; p.Deferred = id == VkPrograms.Id.LitDeferred; }
        return p;
    }

    /// <summary>A content program's cache key: its feature id and variant.</summary>
    private static int VkContentKey(int feat, ContentVariant v) => feat * 8 + (int)v;

    /// <summary>Content pair <paramref name="feat"/>'s <paramref name="v"/> program on this device, built the first time a
    /// batch asks and rebuilt when GL rebuilds the pair; null when GL has no such program or Vulkan refused it (logged
    /// once per build: its parts then draw the plain lit or depth program, as GL's do when a pair is not run).</summary>
    private static VkProg? VkContent(int feat, ContentVariant v)
    {
        VkColourState s = _vkc;
        int key = VkContentKey(feat, v), epoch = ContentPrograms.Epoch(feat);
        if (s.Content.TryGetValue(key, out VkProg? p) && (p == null ? s.ContentFailed.GetValueOrDefault(key) == epoch : p.Epoch == epoch))
            return p;
        if (_log == null || s.Dev == null) return null;
        string label = $"content {feat} {v}";
        (int na, int nb) = ContentPrograms.NativeStages(feat);
        if (VkPrograms.ContentText(feat, v, na, nb, _log) is not { } t) p = null;
        else
        {
            // every variant of a pair is the lit stage's text, so every one takes the lit table
            p = VkBuild(s.Dev, t, label, true, _log);
            if (p == null) _log.Always($"AlloyRenderer: Vulkan {label} NOT RUN (logged above) -- its parts draw the plain program");
            else if (na >= 0 || nb >= 0) VkStageAttach(p, na, nb);
        }
        if (p != null)
        {
            p.Feat = feat; p.Epoch = epoch; p.Deferred = v == ContentVariant.Deferred;
            _log.Always($"AlloyRenderer: Vulkan {label} built -- its parts draw the content's own stages");
        }
        else s.ContentFailed[key] = epoch;
        s.Content[key] = p;
        return p;
    }

    /// <summary>One program: translated, compiled once to SPIR-V for both its module and its reflection, its loose
    /// block laid out, and its location tables resolved against that layout -- the lit table when
    /// <paramref name="lit"/>.</summary>
    private static VkProg? VkBuild(VkBackend vk, VkProgramText t, string label, bool lit, ExtenderLog log)
    {
        if (t.Error != null) { log.Always($"AlloyRenderer: the Vulkan {label} program did not translate -- {t.Error}"); return null; }
        byte[]? vs = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"vk {label} vs", ShaderKind.VertexShader, out string ve);
        byte[]? fs = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"vk {label} fs", ShaderKind.FragmentShader, out string fe);
        if (vs == null || fs == null) { log.Always($"AlloyRenderer: the Vulkan {label} program did not compile -- {(vs == null ? ve : fe)}"); return null; }
        var p = new VkProg { Lit = lit, Label = label, Opaques = t.Opaques.ToArray(), Refl = t.Fs.Contains("oReflN", StringComparison.Ordinal) };
        p.ReadsEngine = Array.Exists(p.Opaques, o => o.Set == 0 && o.Kind == VkBindKind.UniformBlock && o.Binding == UboSlots.Engine);
        p.Vs = vk.CreateShader(ShaderStage.Vertex, vs, $"vk {label} vs");
        p.Fs = vk.CreateShader(ShaderStage.Fragment, fs, $"vk {label} fs");
        if (p.Vs.IsNull || p.Fs.IsNull) return null;
        if (LooseBlock.Layout(t, vs, fs) is { } l) { p.Members = l.Members; p.Block = LooseBlock.Block(l.Bytes); }
        p.Names = LooseBlock.Index(p.Members);
        LooseBlock.Resolve(p.Names, () =>
        {
            if (lit) p.L = Programs.LitLocs();
            else
            {
                p.Vp = LooseBlock.Loc("uVP"); p.Tex = LooseBlock.Loc("uTex"); p.Viz = LooseBlock.Loc("uViz");
                p.AlphaMip = LooseBlock.Loc("uAlphaMip"); p.Tint = LooseBlock.Loc("uTint");
                p.SkyFog = LooseBlock.Loc("uSkyFog"); p.Oit = LooseBlock.Loc("uOit"); p.OitDim = LooseBlock.Loc("uOitDim");
                p.ViewInv = LooseBlock.Loc("uViewInv"); p.Global = LooseBlock.Loc("globalColor");
                p.F = Programs.FeatLocs();
            }
        });
        return p;
    }

    /// <summary>Capture one program's bind into its loose block -- its terms in the order the issue loop writes
    /// them -- then the pass block, both appended to the arena, then the bind set. False (logged) when the arena is full.</summary>
    private static bool VkBindProgram(CommandList cl, VkProg p, in Matrix4x4 vp, int* viewport, float near, float far,
                                      BufferId slots, int tex)
    {
        VkColourState s = _vkc;
        Array.Clear(p.Block);
        int viz = _capturing ? 0 : _lightViz;
        bool lit = p.Lit;
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            fixed (Matrix4x4* m = &vp)
            {
                if (p.Id == VkPrograms.Id.Water) VkWaterTerms(p, in vp, viewport, near, far);
                else if (lit)
                {
                    ref LitLoc L = ref p.L;
                    LooseBlock.Set(L.Vp, new ReadOnlySpan<float>(m, 16));
                    LooseBlock.Set(L.Viz, viz);
                    LooseBlock.Set(L.AoCutout, Ao.Cutout.V);
                    LooseBlock.Set(L.SsaoHalf, SsaoPassVk.Half ? 1 : 0);
                    LooseBlock.Set(L.AlphaMip, Renderer.CutMip.V);
                    LooseBlock.Set(L.MirrorZ, _mirrorZ);
                    LooseBlock.Set(L.LightRecOn, s.LightRec ? 1 : 0);
                    LooseBlock.Set(L.Oit, s.Oit ? 1 : 0);
                    LooseBlock.Set(L.Capture, _capturing ? _planar ? 2 : 1 : 0);
                    UploadFeatureTerms(L.Feat, viewport);
                    BindPass(L.CubeCount, viewport, near, far);
                    LooseBlock.Set(L.DecalZ, [DecalLayer.Z0, DecalLayer.Z1]);
                    LooseBlock.Set(L.LightOcc, [Lighting.HiddenLights.V ? 1f : 0f, GlossyRough]);
                    UploadMaterialModel(L.Material);
                    bool wxOff = _capturing;
                    Weather.UploadLit(L.Weather, L.WeatherLook, L.CoverVp, L.CoverLayer, L.SnowLook, L.SnowLight, L.SnowPom, L.WxEdge, L.WxRun, L.WxCache, wxOff);
                    Weather.BindEsm(L.CoverEsm, s.Oit);
                    Weather.UploadMap(L.WxMapXf, L.WxMapP, wxOff);
                    SkyView.UploadLitVk(L.SkyVp, L.SkyTerm, VkSkyMap, _capturing && !_planar);
                    LooseBlock.Set(L.LmPages, LightmapBake.VkLivePages);
                    LooseBlock.Set(L.LmMovers, LightmapBake.MoverMode);
                }
                else
                {
                    LooseBlock.Set(p.Vp, new ReadOnlySpan<float>(m, 16));
                    LooseBlock.Set(p.AlphaMip, Renderer.CutMip.V);
                    LooseBlock.Set(p.Viz, viz);
                    LooseBlock.Set(p.Tint, [1f, 1f, 1f]);
                    LooseBlock.Set(p.Oit, s.Oit ? 1 : 0);
                    UploadFeatureTerms(p.F, viewport);
                }
            }
            VkStageFrame(p);
        }
        finally { LooseBlock.EndCapture(); }
        // the terms whose resources are not on Vulkan yet read as absent: no cube, no lightmap, no sky map, no weather,
        // no fog volume -- every one gates on the value it is zeroed to
        VkShadowSay(p);
        int loose = VkArena(p.Block);
        // the water stage is not the lit table's, but it reads the pass block's sun, ambient and fog as the lit stage does
        bool passed = lit || p.Id == VkPrograms.Id.Water;
        int pass = passed ? VkArenaPass() : -1;
        if (loose < 0 || (passed && pass < 0) || (p.ReadsEngine && VkEngine() < 0)) return false;
        VkBindSet(cl, p, s, loose, pass, slots, tex);
        return true;
    }

    /// <summary>This frame's <c>CxEngine</c> block in the arena, staged at the first program that reads it, its noise
    /// handle as a bindless index; negative (logged once) when the arena is full.</summary>
    private static int VkEngine()
    {
        VkColourState s = _vkc;
        if (s.EngineAt != -1) return s.EngineAt;
        EngineBlock b = EngineBlockStaged();
        ulong h = (ulong)b.Noise.Y << 32 | b.Noise.X;
        b.Noise = new UInt2(h != 0 ? VkTextures.IndexOfHandle(h) : 0u, 0u);
        if (b.Noise.X == 0) b.Noise.X = s.NullIndex;
        int at = VkArena(MemoryMarshal.AsBytes(new ReadOnlySpan<EngineBlock>(in b)));
        // a full arena is remembered for the frame (-2), so no later batch stages the block again
        s.EngineAt = at >= 0 ? at : -2;
        return at;
    }

    /// <summary>The content table into its Vulkan copy, its palette handles as bindless indices (an empty slot the
    /// transparent-black stand-in, as GL's null-handle rule has it), sent when the bytes moved. False when refused.</summary>
    private static bool VkContentRows(VkBackend vk, CommandList cl)
    {
        VkColourState s = _vkc;
        long version = Db.ContentTable.Version, gen = VkTextures.Generation;
        if (!s.ContentRows.IsNull && version == s.ContentVersion && gen == s.ContentTexGen) return true;
        int n = Db.ContentTable.Snapshot(ref s.ContentStage);
        Span<byte> rows = s.ContentStage.AsSpan(0, n);
        int rb = Db.ContentTable.RowBytes;
        for (int at = 0; at < n; at += rb)
            for (int k = 0; k < Db.ContentTable.PalSlots; k++)
            {
                Span<byte> slot = rows.Slice(at + k * sizeof(ulong), sizeof(ulong));
                ulong h = MemoryMarshal.Read<ulong>(slot);
                uint i = h != 0 ? VkTextures.IndexOfHandle(h) : 0;
                MemoryMarshal.Write(slot, (ulong)(i != 0 ? i : s.NullIndex));
            }
        if (!s.ContentRows.IsNull && rows.SequenceEqual(s.ContentSent.AsSpan(0, s.ContentSentN)))
        {
            s.ContentVersion = version; s.ContentTexGen = gen;
            return true;
        }
        if (s.ContentRows.IsNull || n > s.ContentSent.Length)
        {
            s.ContentSent = new byte[n];
            vk.Destroy(s.ContentRows);
            s.ContentRows = vk.CreateBuffer(new BufferDesc(n, BufferUse.Storage, MemoryKind.Device, "vk content table"));
            if (s.ContentRows.IsNull) return false;
        }
        cl.UpdateBuffer(s.ContentRows, 0, rows);
        rows.CopyTo(s.ContentSent);
        s.ContentSentN = n;
        s.ContentVersion = version; s.ContentTexGen = gen;
        return true;
    }

    /// <summary>Once per session, from the first lit bind that stages a cube count: what the shadow receiver was
    /// handed -- the count it will loop over and whether each map behind it is the bake's or a stand-in. A shadow
    /// that never appears is one of these three reading as nothing.</summary>
    private static void VkShadowSay(VkProg p)
    {
        // not before the bake has made a cube array: the first lit bind of a session runs ahead of it, and the
        // line is about the steady state, not about the frame the world was still loading in
        if (_vkc.ShadowSaid || !p.Lit || _log == null || CasterPass.VkCubeMap.IsNull
            || !p.Names.TryGetValue("uLocalCubes", out int i)) return;
        _vkc.ShadowSaid = true;
        int cubes = MemoryMarshal.Read<int>(p.Block.AsSpan(p.Members[i].Offset));
        _log.Always($"AlloyRenderer: the Vulkan lit stage's shadow terms -- uLocalCubes {cubes} "
                  + $"(CubesFresh {CasterPass.CubesFresh}, CubeCount {CasterPass.CubeCount}), cube map "
                  + $"{(CasterPass.VkCubeMap.IsNull ? "STAND-IN" : "the bake's")}, mover cubes "
                  + $"{(CasterPass.VkCubeDynMap.IsNull ? "STAND-IN" : "the bake's")}, sun cascades "
                  + $"{(CasterPass.VkCascadeMap.IsNull ? "STAND-IN" : "the bake's")}");
    }

    /// <summary>Zero loose member <paramref name="name"/>, when the program declares it.</summary>
    private static void VkZero(VkProg p, string name)
    {
        if (p.Names.TryGetValue(name, out int i))
            p.Block.AsSpan(p.Members[i].Offset, Math.Min(p.Members[i].Bytes, p.Block.Length - p.Members[i].Offset)).Clear();
    }

    /// <summary>Append <paramref name="bytes"/> to the frame's arena at the device's alignment; its offset, or -1 (logged) when full.</summary>
    private static int VkArena(ReadOnlySpan<byte> bytes)
    {
        VkColourState s = _vkc;
        int align = Math.Max(s.Dev!.UniformAlign, 16);
        int at = (s.ArenaUsed + align - 1) & ~(align - 1);
        if (at + bytes.Length > ArenaCap)
        {
            _log?.Always($"AlloyRenderer: the Vulkan uniform arena is full at {ArenaCap} bytes -- the frame refuses");
            return -1;
        }
        bytes.CopyTo(s.ArenaBytes.AsSpan(at));
        s.ArenaUsed = at + bytes.Length;
        return at;
    }

    /// <summary>The pass block <see cref="BindPass"/> just staged, into the arena with the probe as Vulkan has it.</summary>
    private static int VkArenaPass()
    {
        if (_passStage == null) return -1;
        int at = VkArena(new ReadOnlySpan<byte>(_passStage, PassBytes));
        // the probe as Vulkan holds it, 0 mips for none, in place of GL's cube
        if (at >= 0) MemoryMarshal.Write(_vkc.ArenaBytes.AsSpan(at + PbProbeMips, sizeof(float)), VkProbeMips);
        return at;
    }

    /// <summary>A snap frame's Vulkan half, after the frame's submit: the finished scene colour beside the set.</summary>
    internal static void VkFrameTap(VkBackend vk)
    {
        VkColourState s = _vkc;
        FrameTap.TakeVk(vk, s.Hdr, s.W, s.H);
    }

    /// <summary>The program's set 0: each declaration it makes, bound to its real resource where that is on Vulkan and to
    /// the stand-in of its kind otherwise. <paramref name="tex"/> is the batch's bound diffuse (GL name), 0 for none;
    /// <paramref name="storage"/> and <paramref name="sampledOwn"/> are the caller's buffers and images by binding.</summary>
    private static void VkBindSet(CommandList cl, VkProg p, VkColourState s, int loose, int pass, BufferId slots, int tex,
                                  ReadOnlySpan<int> extra = default, ReadOnlySpan<BufferBinding> storage = default,
                                  ReadOnlySpan<ImageBinding> sampledOwn = default)
    {
        Span<BufferBinding> ubo = stackalloc BufferBinding[12];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[16];
        Span<ImageBinding> sampled = stackalloc ImageBinding[48];
        Span<ImageBinding> images = stackalloc ImageBinding[8];
        int nu = 0, ns = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    int engine = o.Binding == UboSlots.Engine ? VkEngine() : -1;
                    ubo[nu++] = VkProbeSh(o.Binding) is { } sh ? sh
                              : o.Binding == UboSlots.Loose && loose >= 0 ? new BufferBinding(o.Binding, s.Arena, loose, p.Block.Length)
                              : o.Binding == UboSlots.Pass && pass >= 0 ? new BufferBinding(o.Binding, s.Arena, pass, PassBytes)
                              : engine >= 0 ? new BufferBinding(o.Binding, s.Arena, engine, sizeof(EngineBlock))
                              : new BufferBinding(o.Binding, s.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock when Bound(storage, o.Binding) is { } own:
                    ssbo[ns++] = own;
                    break;
                case VkBindKind.StorageBlock when o.Binding == Db.ContentTable.Binding && !s.ContentRows.IsNull:
                    ssbo[ns++] = new BufferBinding(o.Binding, s.ContentRows, 0, s.ContentSentN);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.Draw => s.Records, SsboSlots.DrawIdx => slots, SsboSlots.Bones => s.Bones, SsboSlots.TexH => s.TexH,
                        SsboSlots.Plt when !s.Plt.IsNull => s.Plt,
                        SsboSlots.Chart when !LightmapBake.VkChart.IsNull => LightmapBake.VkChart,
                        _ => s.ZeroSsbo,
                    });
                    break;
                case VkBindKind.StorageImage:
                    images[ni++] = StageResources.VkImageFor(p.StageBinds, o.Binding, true)
                                   ?? new ImageBinding(o.Binding, VkDecalImage(o.Name == "uDecalSurf", s), default, 0);
                    break;
                default:
                    sampled[nt++] = Own(sampledOwn, o.Binding) ?? StageResources.VkImageFor(p.StageBinds, o.Binding, false)
                                    ?? VkSampled(o, s, tex, extra);
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], images[..ni]));
    }

    /// <summary>The caller's own image for sampled binding <paramref name="slot"/>, or null.</summary>
    private static ImageBinding? Own(ReadOnlySpan<ImageBinding> own, uint slot)
    {
        foreach (ImageBinding b in own) if (b.Slot == slot) return b;
        return null;
    }

    /// <summary>The caller's own buffer for storage binding <paramref name="slot"/>, or null.</summary>
    private static BufferBinding? Bound(ReadOnlySpan<BufferBinding> storage, uint slot)
    {
        foreach (BufferBinding b in storage) if (b.Slot == slot) return b;
        return null;
    }

    /// <summary>A sampler declaration's binding: the batch's diffuse for a bound <c>uTex</c>, an engine unit out of
    /// <paramref name="extra"/> (unit 1 first) for a <c>texUnit</c><i>n</i> a hosted stage declares, else the stand-in
    /// of its type.</summary>
    private static ImageBinding VkSampled(in VkOpaque o, VkColourState s, int tex, ReadOnlySpan<int> extra = default)
    {
        if (o.Name.Length == "texUnit1".Length && o.Name.StartsWith("texUnit", StringComparison.Ordinal)
            && o.Name[7] - '1' is var u && (uint)u < (uint)extra.Length && extra[u] != 0 && !VkTextures.Image(extra[u]).IsNull)
            return new ImageBinding(o.Binding, VkTextures.Image(extra[u]), VkTextures.SamplerId(extra[u]));
        if (o.Name == "uTex" && tex != 0 && !VkTextures.Image(tex).IsNull)
            return new ImageBinding(o.Binding, VkTextures.Image(tex), VkTextures.SamplerId(tex));
        if (o.Name == "uPltPal" && !s.Palette.IsNull) return new ImageBinding(o.Binding, s.Palette);
        if (VkProbeImage(o) is { } probe) return probe;
        if (VkShadowMap(o.Name) is { IsNull: false } map)
            return new ImageBinding(o.Binding, map,
                o.Type.EndsWith("Shadow", StringComparison.Ordinal) ? s.Shadow : s.Raw);
        // the visibility estimate, fetched by texel in the lit stage
        if (o.Name == "uLightRec" && s.LightRec) return new ImageBinding(o.Binding, _vkdef.LightRec);
        if (o.Name == "uSsao" && SsaoPassVk.Live) return new ImageBinding(o.Binding, SsaoPassVk.Visibility);
        // ...and the depth it was computed from: the attachment itself for the decal writer, whose pass is outside
        // the colour one, and the sampled copy for every stage inside it, which may not read its own attachment
        if (o.Name == "uSceneDepth" && !VkDecalDepth.IsNull) return new ImageBinding(o.Binding, VkDecalDepth);
        if (o.Name == "uVFogVol" && !VkFogVolume.IsNull) return new ImageBinding(o.Binding, VkFogVolume, s.Linear);
        if (o.Name == "uSkyMap" && !VkSkyMap.IsNull) return new ImageBinding(o.Binding, VkSkyMap, s.Linear);
        if (LightmapBake.VkPage(o.Name, out SamplerId lms) is { IsNull: false } lm) return new ImageBinding(o.Binding, lm, lms);
        if (VkWxSampled(o.Name, out ImageId wx, out SamplerId wxs)) return new ImageBinding(o.Binding, wx, wxs);
        if (VkIsWaterTex(o.Name) && VkWaterTex(o.Name) is { IsNull: false } wat)
            return new ImageBinding(o.Binding, wat, VkWaterSampler(o.Name));
        if (o.Name == "uSceneDepth" && !s.DepthRead.IsNull) return new ImageBinding(o.Binding, s.DepthRead);
        return VkStandInOf(in o, s);
    }

    /// <summary>The stand-in of a sampler declaration's type, which reads as zero.</summary>
    private static ImageBinding VkStandInOf(in VkOpaque o, VkColourState s)
    {
        return o.Type switch
        {
            "usampler2D" => new ImageBinding(o.Binding, s.TexU2D),
            "sampler2DArray" => new ImageBinding(o.Binding, s.Tex2DArray, s.Linear),
            "sampler2DArrayShadow" => new ImageBinding(o.Binding, s.Depth2DArray, s.Shadow),
            "samplerCube" => new ImageBinding(o.Binding, s.TexCube, s.Linear),
            "samplerCubeArray" => new ImageBinding(o.Binding, s.DepthCubeArray, s.Linear),
            "samplerCubeArrayShadow" => new ImageBinding(o.Binding, s.DepthCubeArray, s.Shadow),
            "sampler3D" => new ImageBinding(o.Binding, s.Tex3D, s.Linear),
            _ => new ImageBinding(o.Binding, s.Tex2D, s.Linear),
        };
    }

    /// <summary>The pipeline for program <paramref name="p"/> drawing arena format <paramref name="fmt"/> into
    /// <paramref name="targets"/> under blend <paramref name="blend"/> (0 = off, else 1 + src * 8 + dst over the engine's
    /// blend-mode indices), made on first ask. Null id when the device refused it.</summary>
    private static PipelineId VkPipeline(VkProg p, int fmt, VkTargets targets, int blend)
    {
        int samples = targets == VkTargets.Cover ? _vkCover.Samples : 1;
        bool rate = targets is VkTargets.Colour or VkTargets.ColourRefl && !_vkc.Rate.IsNull;
        long key = fmt | (long)targets << 4 | (long)blend << 8 | (long)samples << 24 | (rate ? 1L << 32 : 0);
        if (p.Pipes.TryGetValue(key, out PipelineId id)) return id;
        VkColourState s = _vkc;
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Layout = s.Layouts[fmt], Topo = Topology.Triangles,
            Raster = new RasterState(CullMode.Back), Depth = new DepthState(true, true, WorldDepth.NearerOrEqual),
            Label = $"vk {p.Label} {(MeshArena.Fmt)fmt} {targets} blend {blend}{(rate ? " rated" : "")}", ShadingRate = rate,
        };
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = samples;
        if (targets == VkTargets.Prepass) { d.Targets.ColorCount = PrepassTargets; d.Targets.Colors[1] = Format.R32Ui; }
        else if (targets == VkTargets.Decal) { d.Targets.Depth = Format.Unknown; VkDecalBlend(ref d); }
        else if (targets == VkTargets.Forward) { d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rgba16F; }
        else if (targets == VkTargets.Cover) { d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rgba16F; d.AlphaToCoverage = true; }
        // the depth the blended class lays after the OIT resolve, inside the forward pass: its colour masked off
        else if (targets == VkTargets.ForwardDepth)
        {
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Blend[0] = new AttachmentBlend(Masked: ColorMask.All);
        }
        // OitPass's pair, as its Begin sets the per-buffer blend: accumulation adds, revealage multiplies by 1 - a
        else if (targets == VkTargets.Oit)
        {
            d.Targets.ColorCount = 2;
            d.Targets.Colors[0] = Format.Rgba32F;
            d.Targets.Colors[1] = Format.R16F;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
            d.Blend[1] = new AttachmentBlend(true, BlendFactor.Zero, BlendFactor.OneMinusSrcColor, BlendFactor.Zero, BlendFactor.OneMinusSrcColor);
        }
        // a probe face: alpha is the lit stage's 1 / distance, which every other stage leaves at the clear's "far"
        else if (targets == VkTargets.Probe)
        {
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Blend[0] = new AttachmentBlend(Masked: p.Id == VkPrograms.Id.Lit ? ColorMask.None : ColorMask.A);
        }
        else if (targets == VkTargets.Mask) { d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rgba8; }
        else if (targets == VkTargets.Velocity) { d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rg16F; }
        else if (targets is VkTargets.Colour or VkTargets.ColourRefl)
        {
            d.Targets.ColorCount = ColourTargets;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Colors[GbufFirst] = Format.Rgba32Ui;
            d.Targets.Colors[GbufFirst + 1] = Format.Rgba32Ui;
            d.Targets.Colors[GbufFirst + 2] = Format.R32Ui;
            bool twin = p.Deferred;
            for (int i = GbufFirst; i < ColourTargets; i++) d.Blend[i] = new AttachmentBlend(Masked: twin ? ColorMask.None : ColorMask.All);
            if (targets == VkTargets.ColourRefl)
            {
                d.Targets.Colors[SsrRecFirst] = Format.Rgba16F;
                d.Targets.Colors[SsrRecFirst + 1] = Format.R11G11B10F;
                for (int i = SsrRecFirst; i < SsrRecFirst + 2; i++) d.Blend[i] = new AttachmentBlend(Masked: p.Refl ? ColorMask.None : ColorMask.All);
            }
        }
        if (blend != 0 && targets != VkTargets.Prepass)
        {
            BlendFactor src = VkFactor((blend - 1) >> 3), dst = VkFactor((blend - 1) & 7);
            d.Blend[0] = new AttachmentBlend(true, src, dst, src, dst);
        }
        id = s.Dev!.CreatePipeline(in d);
        p.Pipes[key] = id;
        return id;
    }

    /// <summary>An <c>Aurora::AuroraBlendModes</c> index as the seam's factor -- <see cref="Issue.BlendGl"/>'s table.</summary>
    private static BlendFactor VkFactor(int aurora) => aurora switch
    {
        0 => BlendFactor.SrcAlpha, 1 => BlendFactor.OneMinusSrcAlpha, 2 => BlendFactor.One, 3 => BlendFactor.Zero,
        4 => BlendFactor.DstColor, 5 => BlendFactor.SrcColor, 6 => BlendFactor.OneMinusDstAlpha, _ => BlendFactor.SrcAlpha,
    };
}
