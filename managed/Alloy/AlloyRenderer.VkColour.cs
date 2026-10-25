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
        public ShaderId Vs, Fs;
        public VkUniformShim.Member[] Members = [];
        public Dictionary<string, int> Names = new();
        public byte[] Block = [];
        public VkOpaque[] Opaques = [];
        public LitLoc L;
        public FeatLoc F;
        public int Vp = -1, Tex = -1, Viz = -1, AlphaMip = -1, Tint = -1, SkyFog = -1, Oit = -1, OitDim = -1;
        public readonly Dictionary<long, PipelineId> Pipes = new();
    }

    /// <summary>The colour bracket's resources on one device, replaced whole with the device.</summary>
    private sealed class VkColourState
    {
        public VkBackend? Dev;
        public bool Failed, Live;
        public readonly VkProg?[] Progs = new VkProg?[VkPrograms.All.Length];
        public readonly VertexLayoutId[] Layouts = new VertexLayoutId[MeshArena.Formats];
        public ImageId Hdr, Depth, G0, G1, G2;
        public int W, H;
        public BufferId Records, Slots, Bones, TexH, Arena, ZeroUbo, ZeroSsbo;
        public long RecCap, SlotCap, BoneCap, TexHCap;
        /// <summary>Records the GL upload named since the last Vulkan one; <see cref="DirtyAll"/> when it overflowed.</summary>
        public int[] Dirty = new int[1024];
        public int DirtyN;
        public bool DirtyAll = true;
        public uint[] TexHTable = [];
        public byte[] ArenaBytes = new byte[ArenaCap];
        public int ArenaUsed;
        public ImageId Tex2D, TexU2D, Tex2DArray, Tex3D, TexCube, TexCubeArray, Depth2DArray, DepthCubeArray, DecalLayer, DecalSurf;
        public SamplerId Linear, Shadow;
        public CommandList? Side;
        public PipelineId Encode;
        public ShaderId EncodeVs, EncodeFs;
        public uint WhiteIndex, NullIndex;
        public long Drawn, Skipped;
        /// <summary>A CPU multi-draw's index-buffer byte offsets as element indices, per call.</summary>
        public int[] Firsts = new int[4096];
    }

    private static VkColourState _vkc = new();

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
    private enum VkTargets : byte { Prepass, Colour, Forward }

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
        if (!Renderer.OnVulkan || s.DirtyAll) return;
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
            s.Hdr = VkTarget(vk, Format.Rgba16F, w, h, "vk scene colour");
            s.Depth = VkTarget(vk, Format.D32F, w, h, "vk scene depth");
            s.G0 = VkTarget(vk, Format.Rgba32Ui, w, h, "vk g-buffer 0");
            s.G1 = VkTarget(vk, Format.Rgba32Ui, w, h, "vk g-buffer 1");
            s.G2 = VkTarget(vk, Format.R32Ui, w, h, "vk g-buffer 2");
            s.W = w; s.H = h;
            if (s.Hdr.IsNull || s.Depth.IsNull || s.G0.IsNull || s.G1.IsNull || s.G2.IsNull)
            {
                s.Failed = true;
                log.Always($"AlloyRenderer: the Vulkan scene targets at {w}x{h} were REFUSED (logged above)");
                return false;
            }
        }
        return true;
    }

    private static ImageId VkTarget(VkBackend vk, Format f, int w, int h, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1,
                                        (f.IsDepth() ? ImageUse.DepthTarget : ImageUse.ColorTarget) | ImageUse.Sampled, label));

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
            if (p == null) return false;
            s.Progs[(int)id] = p;
        }
        s.ZeroUbo = vk.CreateBuffer(new BufferDesc(ZeroUboBytes, BufferUse.Uniform, MemoryKind.Device, "vk zero block"));
        s.ZeroSsbo = vk.CreateBuffer(new BufferDesc(ZeroSsboBytes, BufferUse.Storage, MemoryKind.Device, "vk zero storage"));
        s.Arena = vk.CreateBuffer(new BufferDesc(ArenaCap, BufferUse.Uniform, MemoryKind.Upload, "vk uniform arena"));
        s.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        s.Shadow = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp, CompareOp.LessEqual));
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
        if (s.ZeroUbo.IsNull || s.ZeroSsbo.IsNull || s.Arena.IsNull || s.Tex2D.IsNull || s.TexU2D.IsNull || s.Tex2DArray.IsNull
            || s.Tex3D.IsNull || s.TexCube.IsNull || s.TexCubeArray.IsNull || s.Depth2DArray.IsNull || s.DepthCubeArray.IsNull
            || s.DecalLayer.IsNull || s.DecalSurf.IsNull || s.Linear.IsNull || s.Shadow.IsNull)
        {
            log.Always("AlloyRenderer: the Vulkan colour pass's constant resources were REFUSED (logged above)");
            return false;
        }
        if (!VkMakeEncode(vk, s, log)) return false;
        log.Info($"AlloyRenderer: the Vulkan colour programs are built -- {VkPrograms.All.Length} programs, {MeshArena.Formats} arena layouts");
        return true;
    }

    /// <summary>A stand-in of one texel per layer: transparent black, or a depth of 1.0 that every shadow compare passes.</summary>
    private static ImageId VkStandIn(VkBackend vk, ImageKind kind, Format f, int faces, string label)
    {
        int layers = kind is ImageKind.Cube or ImageKind.CubeArray ? 1 : 1;
        ImageId img = vk.CreateImage(new ImageDesc(kind, f, 1, 1, 1, 1, layers, 1, ImageUse.Sampled | ImageUse.CopyDst, $"vk stand-in {label}"));
        if (img.IsNull) return img;
        Span<byte> texel = stackalloc byte[16];
        texel.Clear();
        if (f == Format.D32F) MemoryMarshal.Write(texel, 1f);
        int bytes = (int)VkBackend.LevelBytes(new ImageDesc(kind, f, 1, 1, 1, 1, layers, 1, ImageUse.Sampled, ""), 0);
        for (int layer = 0; layer < faces; layer++) vk.Upload(img, 0, layer, texel[..bytes]);
        return img;
    }

    /// <summary>One program: translated, compiled once to SPIR-V for both its module and its reflection, its loose
    /// block laid out, and GL's location tables resolved against that layout through <see cref="VkUniformShim"/>.</summary>
    private static VkProg? VkBuild(VkBackend vk, VkPrograms.Id id, ExtenderLog log)
    {
        VkProgramText t = VkPrograms.Text(id, log);
        if (t.Error != null) { log.Always($"AlloyRenderer: the Vulkan {id} program did not translate -- {t.Error}"); return null; }
        byte[]? vs = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"vk {id} vs", ShaderKind.VertexShader, out string ve);
        byte[]? fs = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"vk {id} fs", ShaderKind.FragmentShader, out string fe);
        if (vs == null || fs == null) { log.Always($"AlloyRenderer: the Vulkan {id} program did not compile -- {(vs == null ? ve : fe)}"); return null; }
        var p = new VkProg { Id = id, Opaques = t.Opaques.ToArray() };
        p.Vs = vk.CreateShader(ShaderStage.Vertex, vs, $"vk {id} vs");
        p.Fs = vk.CreateShader(ShaderStage.Fragment, fs, $"vk {id} fs");
        if (p.Vs.IsNull || p.Fs.IsNull) return null;
        var names = new List<string>(t.Loose.Count);
        foreach (VkLoose u in t.Loose) names.Add(u.Name);
        uint at = VulkanToolchain.UniformBase + VkGlsl.LooseBinding;
        var layout = VkSpirv.Block(fs, 0, at, names) ?? VkSpirv.Block(vs, 0, at, names);
        if (layout is { } l)
        {
            var members = new List<VkUniformShim.Member>(l.Members.Count);
            foreach (VkLoose u in t.Loose)
            {
                if (!l.Members.TryGetValue(u.Name, out var m)) continue;
                int count = u.Array.Length == 0 ? 1 : VkGlsl.ResolveInt(u.Array, t.Fs + t.Vs) ?? 1;
                members.Add(new VkUniformShim.Member(u.Name, m.Offset, m.Size, count > 1 ? m.Size / count : 0));
            }
            p.Members = members.ToArray();
            p.Block = new byte[(l.Bytes + 15) & ~15];
        }
        p.Names = VkUniformShim.Index(p.Members);
        uint name = VkUniformShim.ProgramBase + (uint)id;
        VkUniformShim.Resolve(p.Names, () =>
        {
            switch (id)
            {
                case VkPrograms.Id.Lit or VkPrograms.Id.LitDeferred:
                    p.L = Programs.LitLocs(name);
                    break;
                default:
                    p.Vp = GlProgram.Loc(name, "uVP"); p.Tex = GlProgram.Loc(name, "uTex"); p.Viz = GlProgram.Loc(name, "uViz");
                    p.AlphaMip = GlProgram.Loc(name, "uAlphaMip"); p.Tint = GlProgram.Loc(name, "uTint");
                    p.SkyFog = GlProgram.Loc(name, "uSkyFog"); p.Oit = GlProgram.Loc(name, "uOit"); p.OitDim = GlProgram.Loc(name, "uOitDim");
                    p.F = Programs.FeatLocs(name);
                    break;
            }
        });
        return p;
    }

    /// <summary>Capture one program's bind into its loose block -- GL's own setters, in the order the issue loop calls
    /// them -- then the pass block, both appended to the arena, then the bind set. False (logged) when the arena is full.</summary>
    private static bool VkBindProgram(CommandList cl, VkProg p, in Matrix4x4 vp, int* viewport, float near, float far,
                                      BufferId slots, int tex)
    {
        VkColourState s = _vkc;
        Array.Clear(p.Block);
        int viz = _capturing ? 0 : _lightViz;
        bool lit = p.Id is VkPrograms.Id.Lit or VkPrograms.Id.LitDeferred;
        VkUniformShim.Capture(p.Members, p.Block);
        try
        {
            fixed (Matrix4x4* m = &vp)
            {
                if (lit)
                {
                    ref LitLoc L = ref p.L;
                    VkUniformShim.Set(L.Vp, new ReadOnlySpan<float>(m, 16));
                    VkUniformShim.Set(L.Viz, viz);
                    VkUniformShim.Set(L.AoCutout, Ao.Cutout.V);
                    VkUniformShim.Set(L.AlphaMip, Renderer.CutMip.V);
                    VkUniformShim.Set(L.MirrorZ, _mirrorZ);
                    UploadFeatureTerms(L.Feat, viewport);
                    BindPass(L.CubeCount, viewport, near, far);
                    VkUniformShim.Set(L.DecalZ, [DecalLayer.Z0, DecalLayer.Z1]);
                    VkUniformShim.Set(L.LightOcc, [Lighting.HiddenLights.V ? 1f : 0f, GlossyRough]);
                    UploadMaterialModel(L.Material);
                    Weather.UploadLit(L.Weather, L.WeatherLook, L.CoverVp, L.CoverLayer, L.SnowLook, L.SnowLight, L.SnowPom, L.WxEdge, L.WxRun, L.WxCache, false);
                }
                else
                {
                    VkUniformShim.Set(p.Vp, new ReadOnlySpan<float>(m, 16));
                    VkUniformShim.Set(p.AlphaMip, Renderer.CutMip.V);
                    VkUniformShim.Set(p.Viz, viz);
                    VkUniformShim.Set(p.Tint, [1f, 1f, 1f]);
                    UploadFeatureTerms(p.F, viewport);
                }
            }
        }
        finally { VkUniformShim.EndCapture(); }
        // the terms whose resources are not on Vulkan yet read as absent: no cube, no lightmap, no sky map, no weather,
        // no fog volume -- every one gates on the value it is zeroed to
        VkZero(p, "uLocalCubes"); VkZero(p, "uLmPages"); VkZero(p, "uSkyTerm"); VkZero(p, "uWeather"); VkZero(p, "uVFog");
        VkZero(p, "uSsaoHalf"); VkZero(p, "uLightRecOn"); VkZero(p, "uCapture"); VkZero(p, "uOit");
        int loose = VkArena(p.Block);
        int pass = lit ? VkArenaPass() : -1;
        if (loose < 0 || (lit && pass < 0)) return false;
        VkBindSet(cl, p, s, loose, pass, slots, tex);
        return true;
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

    /// <summary>The pass block <see cref="BindPass"/> just staged, into the arena with the receivers Vulkan has no maps
    /// for yet switched off: no sun cascade, no probe.</summary>
    private static int VkArenaPass()
    {
        if (_passStage == null) return -1;
        int at = VkArena(new ReadOnlySpan<byte>(_passStage, PassBytes));
        if (at < 0) return at;
        Span<byte> b = _vkc.ArenaBytes.AsSpan(at, PassBytes);
        MemoryMarshal.Write(b[PbSunCascades..], 0);
        MemoryMarshal.Write(b[PbProbeMips..], 0f);
        return at;
    }

    /// <summary>The program's set 0: each declaration it makes, bound to its real resource where that is on Vulkan and to
    /// the stand-in of its kind otherwise. <paramref name="tex"/> is the batch's bound diffuse (GL name), 0 for none.</summary>
    private static void VkBindSet(CommandList cl, VkProg p, VkColourState s, int loose, int pass, BufferId slots, int tex)
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
                    ubo[nu++] = o.Binding == UboSlots.Loose && loose >= 0 ? new BufferBinding(o.Binding, s.Arena, loose, p.Block.Length)
                              : o.Binding == UboSlots.Pass && pass >= 0 ? new BufferBinding(o.Binding, s.Arena, pass, PassBytes)
                              : new BufferBinding(o.Binding, s.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.Draw => s.Records, SsboSlots.DrawIdx => slots, SsboSlots.Bones => s.Bones, SsboSlots.TexH => s.TexH,
                        _ => s.ZeroSsbo,
                    });
                    break;
                case VkBindKind.StorageImage:
                    images[ni++] = new ImageBinding(o.Binding, o.Name == "uDecalSurf" ? s.DecalSurf : s.DecalLayer, default, 0);
                    break;
                default:
                    sampled[nt++] = VkSampled(o, s, tex);
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], images[..ni]));
    }

    /// <summary>A sampler declaration's binding: the batch's diffuse for a bound <c>uTex</c>, else the stand-in of its type.</summary>
    private static ImageBinding VkSampled(in VkOpaque o, VkColourState s, int tex)
    {
        if (o.Name == "uTex" && tex != 0 && !VkTextures.Image(tex).IsNull)
            return new ImageBinding(o.Binding, VkTextures.Image(tex), VkTextures.SamplerId(tex));
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
        long key = fmt | (long)targets << 4 | (long)blend << 8;
        if (p.Pipes.TryGetValue(key, out PipelineId id)) return id;
        VkColourState s = _vkc;
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Layout = s.Layouts[fmt], Topo = Topology.Triangles,
            Raster = new RasterState(CullMode.Back), Depth = new DepthState(true, true, CompareOp.LessEqual),
            Label = $"vk {p.Id} {(MeshArena.Fmt)fmt} {targets} blend {blend}",
        };
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = 1;
        if (targets == VkTargets.Forward) { d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rgba16F; }
        else if (targets == VkTargets.Colour)
        {
            d.Targets.ColorCount = ColourTargets;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Colors[GbufFirst] = Format.Rgba32Ui;
            d.Targets.Colors[GbufFirst + 1] = Format.Rgba32Ui;
            d.Targets.Colors[GbufFirst + 2] = Format.R32Ui;
            bool twin = p.Id == VkPrograms.Id.LitDeferred;
            for (int i = GbufFirst; i < ColourTargets; i++) d.Blend[i] = new AttachmentBlend(Masked: twin ? ColorMask.None : ColorMask.All);
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
