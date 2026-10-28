using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// The deferred path's two compute dispatches on Vulkan (ADR 0029): <see cref="DeferredShadows"/>'s per-pixel
/// visibility record, then <see cref="DeferredLights"/>'s walk, which adds every point light's diffuse, specular
/// and film terms into the Vulkan scene colour as a storage image. The same stages GL compiles, translated by
/// <see cref="VkGlsl.TranslateCompute"/>; the light records are the rig's own fill, mirrored into a Vulkan buffer.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>One translated compute stage: its pipeline, the declarations the host binds, and its loose block
    /// with the locations the per-frame fill writes through.</summary>
    private sealed class VkDefStage
    {
        public ShaderId Cs;
        public PipelineId Pipe;
        public VkOpaque[] Opaques = [];
        public LooseBlock.Member[] Members = [];
        public Dictionary<string, int> Names = new();
        public byte[] Block = [];
        public VkDefLoc L;
    }

    /// <summary>Where each loose term this path's stages read sits in its own block, resolved once at build: the
    /// shared lighting libraries' four, the shadow record writer's five, and the froxel build's own.</summary>
    private struct VkDefLoc
    {
        public int Cubes, Material, LightOcc, LmMovers, Rect, InvVp, Proj, ShSize, ShRes;
        public int View, Grid, ProjXY, Z, LightCount, Reach, LightFloor, BinCap, Atten, Hot, ReflK, Rel, Pad, Tally, DynPage;
    }

    /// <summary>The deferred path's resources on one device, replaced whole with the device.</summary>
    private sealed class VkDeferredState
    {
        public VkBackend? Dev;
        public bool Failed, Live;
        /// <summary>This frame's walk wrote <see cref="LightRec"/>.</summary>
        public bool Wrote;
        /// <summary>The shipped walk, the film pass, the record writer, and the walk's in-loop-tap variant --
        /// built on the first frame an "every light" escape asks for it (<c>shadow.cap 0</c>).</summary>
        public VkDefStage? Walk, Film, Shadow, InLoop;
        public bool InLoopFailed;
        /// <summary>The light records this frame, the counters both dispatches add to, and <see cref="DeferredBlock"/>.</summary>
        public BufferId Lights, Stats, Block;
        public long LightCap;
        /// <summary>The light record the walk writes, and the visibility record's two lanes.</summary>
        public ImageId LightRec, ShVis, ShKey;
        public int W, H, ShW, ShH;
        public DeferredBlock Blk;
    }

    private static VkDeferredState _vkdef = new();

    /// <summary>This frame's light records on the device, for the froxel build that reads the same buffer.</summary>
    private static BufferId VkDeferredLights() => _vkdef.Lights;

    /// <summary>Invocations per group per axis, each stage's own <c>local_size</c>.</summary>
    private const int VkDefGroup = 16, VkShGroup = 8;

    /// <summary>The counters' words, as <c>cx_deferred_cs</c> declares the block.</summary>
    private const int VkDefStatsWords = 8;

    /// <summary>The two dispatches into <paramref name="cl"/>, between the colour pass and the blended one -- the
    /// frame-order place GL runs them. <paramref name="vp"/> is the view-projection the prepass rasterised with
    /// (every world position is reconstructed from its inverse) and <paramref name="slots"/> the draw order the
    /// colour pass drew from, which is how a G-buffer record index resolves to its draw.</summary>
    internal static void VkDeferred(VkBackend vk, CommandList cl, in Matrix4x4 vp, in Matrix4x4 view,
                                    in Matrix4x4 proj, int* viewport, float near, float far, BufferId slots,
                                    ExtenderLog log)
    {
        VkDeferredState d = _vkdef;
        if (d.Dev != vk) _vkdef = d = new VkDeferredState { Dev = vk };
        VkColourState s = _vkc;
        d.Wrote = false;
        int w = viewport[2], h = viewport[3];
        // GbufWritten is this frame's own verdict on light.deferred, the viz modes and the projection: with no
        // G-buffer there is nothing for the walk to shade
        if (d.Failed || !s.GbufWritten || w <= 0 || h <= 0) return;
        if (!Matrix4x4.Invert(vp, out Matrix4x4 invVp) || !Matrix4x4.Invert(view, out Matrix4x4 camToWorld)) return;
        Vector3 eye = camToWorld.Translation;
        int div = Math.Max(Shadows.DeferredRes.V, 1);
        int rw = (w + div - 1) / div, rh = (h + div - 1) / div;
        if (!VkDefEnsure(vk, cl, w, h, rw, rh, log)) return;
        bool film = Weather.ReflK > 0 && d.Film != null;
        VkDefStage walk = VkDefWalk(vk, log);

        ReadOnlySpan<byte> rig = MemoryMarshal.AsBytes(AlloyLights.Staged);
        if (!VkDefLights(vk, cl, rig, log)) return;

        int arena0 = s.ArenaUsed, pass = -1, looseFilm = 0;
        if (!VkDefBlocks(d.Shadow!, true, viewport, near, far, in invVp, in proj, rw, rh, div, out int looseSh, ref pass)
            || !VkDefBlocks(walk, false, viewport, near, far, in invVp, in proj, rw, rh, div, out int looseWalk, ref pass)
            || film && !VkDefBlocks(d.Film!, false, viewport, near, far, in invVp, in proj, rw, rh, div, out looseFilm, ref pass))
            return;

        ref DeferredBlock b = ref d.Blk;
        b.Rect = new Int4(viewport[0], viewport[1], w, h);
        b.InvVp = invVp;
        b.Proj = proj;
        b.View = view;
        b.EyePos = eye;
        // light.deferredtiles, as GL reads it: each pixel's own froxel list by default, now that the build that
        // writes them runs here too, and the per-tile cull when the knob asks -- the table says which it is
        b.Tiles = Lighting.DeferredTiles.V || VkClusterCounts.IsNull ? 1 : 0;
        b.LightCount = AlloyLights.UploadedLights;
        b.ReflK = Weather.ReflK;
        b.ShSize = new Int2(rw, rh);
        b.ShRes = div;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<DeferredBlock>()];
        blk.Clear();
        MemoryMarshal.Write(blk, in b);
        cl.UpdateBuffer(d.Block, 0, blk);
        int len = (s.ArenaUsed - arena0 + 3) & ~3;
        if (len > 0) cl.UpdateBuffer(s.Arena, arena0, s.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));

        cl.SetPipeline(d.Shadow!.Pipe);
        VkDefBindSet(cl, d.Shadow, d.ShVis, d.ShKey, looseSh, pass, slots);
        cl.Dispatch((rw + VkShGroup - 1) / VkShGroup, (rh + VkShGroup - 1) / VkShGroup);
        cl.SetPipeline(walk.Pipe);
        VkDefBindSet(cl, walk, s.Hdr, d.LightRec, looseWalk, pass, slots);
        cl.Dispatch((w + VkDefGroup - 1) / VkDefGroup, (h + VkDefGroup - 1) / VkDefGroup);
        d.Wrote = true;
        if (film)
        {
            // the film's highlights on top: a linear term of the same pixel, so a second read-modify-write is exact
            cl.SetPipeline(d.Film!.Pipe);
            VkDefBindSet(cl, d.Film, s.Hdr, d.LightRec, looseFilm, pass, slots);
            cl.Dispatch((w + VkDefGroup - 1) / VkDefGroup, (h + VkDefGroup - 1) / VkDefGroup);
        }
        if (!d.Live)
        {
            d.Live = true;
            int cubes = CasterPass.CubesFresh ? CasterPass.CubeCount : 0;
            log.Always($"AlloyRenderer: the Vulkan deferred path is LIVE -- {b.LightCount} point lights walked per tile "
                     + $"over the G-buffer, visibility record {rw}x{rh}" + (film ? ", film highlights on top" : "")
                     + (cubes > 0 ? $"; {cubes} shadow cube(s) on the record's lanes"
                                  : "; no cube map was baked for this frame, so every light here is unshadowed"));
        }
    }

    /// <summary>The walk this frame binds: the in-loop-tap variant while an "every light" escape is on
    /// (<c>shadow.cap 0</c>, <c>shadow.perpixel 0</c>), else the shipped one, whose loop has no tap.</summary>
    private static VkDefStage VkDefWalk(VkBackend vk, ExtenderLog log)
    {
        VkDeferredState d = _vkdef;
        if (AlloyLights.ShadowCap > 0f && AlloyLights.ShadowPerPixel > 0f) return d.Walk!;
        if (d.InLoop == null && !d.InLoopFailed)
        {
            d.InLoop = VkDefBuild(vk, DeferredLights.ShdCs, "CX_SH_INLOOP 1", "vk deferred in-loop taps", log);
            if (d.InLoop == null)
            {
                d.InLoopFailed = true;
                log.Always("AlloyRenderer: the Vulkan deferred in-loop-tap variant did not build (logged above) -- "
                         + "shadow.cap 0 / shadow.perpixel 0 keep the record's four-light budget instead of tapping every light");
            }
        }
        return d.InLoop ?? d.Walk!;
    }

    /// <summary>The device's stages, buffers and record images. False (the path stood down, loudly) when one was
    /// refused: a deferred frame with no walk would leave every twin-drawn surface unlit by its point lights.</summary>
    private static bool VkDefEnsure(VkBackend vk, CommandList cl, int w, int h, int rw, int rh, ExtenderLog log)
    {
        VkDeferredState d = _vkdef;
        if (d.Shadow == null)
        {
            d.Shadow = VkDefBuild(vk, DeferredShadows.ShdCs, null, "vk deferred shadow record", log);
            d.Walk = VkDefBuild(vk, DeferredLights.ShdCs, null, "vk deferred walk", log);
            d.Film = VkDefBuild(vk, DeferredLights.ShdCs, "CX_FILM_PASS 1", "vk deferred film", log);
            d.Stats = vk.CreateBuffer(new BufferDesc(VkDefStatsWords * sizeof(uint), BufferUse.Storage | BufferUse.CopySrc,
                                                     MemoryKind.Device, "vk deferred stats"));
            d.Block = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<DeferredBlock>(), BufferUse.Uniform,
                                                     MemoryKind.Upload, "vk deferred block"));
            if (d.Shadow == null || d.Walk == null || d.Film == null || d.Stats.IsNull || d.Block.IsNull)
            {
                d.Failed = true;
                log.Always("AlloyRenderer: the Vulkan deferred path did NOT build (logged above) -- the frame's twins "
                         + "keep their ambient and sun terms and no point light reaches them");
                return false;
            }
            cl.ZeroBuffer(d.Stats);
        }
        if (d.LightRec.IsNull || d.W != w || d.H != h)
        {
            vk.Destroy(d.LightRec);
            d.LightRec = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32Ui, w, h, 1, 1, 1, 1,
                                                      ImageUse.Storage | ImageUse.Sampled, "vk light record"));
            d.W = w; d.H = h;
        }
        if (d.ShVis.IsNull || d.ShW != rw || d.ShH != rh)
        {
            vk.Destroy(d.ShVis); vk.Destroy(d.ShKey);
            d.ShVis = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, rw, rh, 1, 1, 1, 1,
                                                   ImageUse.Storage | ImageUse.Sampled, "vk shadow record"));
            d.ShKey = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg32Ui, rw, rh, 1, 1, 1, 1,
                                                   ImageUse.Storage | ImageUse.Sampled, "vk shadow record key"));
            d.ShW = rw; d.ShH = rh;
            log.Info($"AlloyRenderer: the Vulkan visibility record is {rw}x{rh} (shadow.deferredres)");
        }
        if (d.LightRec.IsNull || d.ShVis.IsNull || d.ShKey.IsNull)
        {
            d.Failed = true;
            log.Always($"AlloyRenderer: a Vulkan deferred record image at {w}x{h} was REFUSED (logged above) -- "
                     + "the deferred path stands down on this device");
            return false;
        }
        return true;
    }

    /// <summary>This frame's light records into the device's copy, grown when a denser area arrives. The bytes are
    /// the rig's own staging, so the two devices walk identical records.</summary>
    private static bool VkDefLights(VkBackend vk, CommandList cl, ReadOnlySpan<byte> rig, ExtenderLog log)
    {
        VkDeferredState d = _vkdef;
        long want = Math.Max(rig.Length, 64);
        if (d.Lights.IsNull || want > d.LightCap)
        {
            vk.Destroy(d.Lights);
            d.LightCap = want;
            d.Lights = vk.CreateBuffer(new BufferDesc(want, BufferUse.Storage, MemoryKind.Upload, "vk light records"));
            if (d.Lights.IsNull)
            {
                d.Failed = true;
                d.LightCap = 0;
                log.Always($"AlloyRenderer: the Vulkan light record buffer of {want} bytes was REFUSED (logged above) -- "
                         + "the deferred path stands down on this device");
                return false;
            }
            cl.ZeroBuffer(d.Lights);
        }
        if (rig.Length > 0) cl.UpdateBuffer(d.Lights, 0, rig);
        return true;
    }

    /// <summary>Stage <paramref name="st"/>'s loose block and (once per frame) the <c>CxPass</c> block into the
    /// frame's arena -- the same writes a colour bind captures, with every term whose map is not on Vulkan left at
    /// zero. False (logged by the arena) when the arena is full.</summary>
    private static bool VkDefBlocks(VkDefStage st, bool shadow, int* viewport, float near, float far,
                                    in Matrix4x4 invVp, in Matrix4x4 proj, int rw, int rh, int div, out int loose, ref int pass)
    {
        Array.Clear(st.Block);
        LooseBlock.Capture(st.Members, st.Block);
        try
        {
            // the CxPass block is filled from the same frame state and viewport as the colour pass's binds, so the
            // walk reads the cluster lookup and the light curve that pass drew with
            BindPass(st.L.Cubes, viewport, near, far);
            UploadMaterialModel(st.L.Material);
            LooseBlock.Set(st.L.LightOcc, [Lighting.HiddenLights.V ? 1f : 0f, GlossyRough]);
            LooseBlock.Set(st.L.LmMovers, LightmapBake.MoverMode);
            if (shadow)
            {
                // the record writer's own five, which are loose there rather than a block of their own
                LooseBlock.Set(st.L.Rect, (ReadOnlySpan<int>)[viewport[0], viewport[1], viewport[2], viewport[3]]);
                LooseBlock.Set(st.L.InvVp, in invVp);
                LooseBlock.Set(st.L.Proj, in proj);
                LooseBlock.Set(st.L.ShSize, (ReadOnlySpan<int>)[rw, rh]);
                LooseBlock.Set(st.L.ShRes, div);
            }
        }
        finally { LooseBlock.EndCapture(); }
        loose = VkArena(st.Block);
        if (pass < 0) pass = VkArenaPass();
        return loose >= 0 && pass >= 0;
    }

    /// <summary>One compute stage's set 0: every declaration it makes, on its real resource where that is on
    /// Vulkan and on the stand-in of its kind otherwise. <paramref name="img0"/> and <paramref name="img1"/> are
    /// the two stores the stage writes, in its own declaration order.</summary>
    private static void VkDefBindSet(CommandList cl, VkDefStage st, ImageId img0, ImageId img1, int loose, int pass, BufferId slots,
                                     ImageId hist = default)
    {
        VkColourState s = _vkc;
        VkDeferredState d = _vkdef;
        Span<BufferBinding> ubo = stackalloc BufferBinding[12];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[16];
        Span<ImageBinding> sampled = stackalloc ImageBinding[48];
        Span<ImageBinding> images = stackalloc ImageBinding[8];
        int nu = 0, ns = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, s.Arena, loose, st.Block.Length)
                              : o.Binding == UboSlots.Pass ? new BufferBinding(o.Binding, s.Arena, pass, PassBytes)
                              : o.Binding == UboSlots.Deferred ? new BufferBinding(o.Binding, d.Block)
                              : new BufferBinding(o.Binding, s.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.Draw => s.Records,
                        SsboSlots.DrawIdx => slots,
                        SsboSlots.Lights => d.Lights,
                        SsboSlots.DeferredStats => d.Stats,
                        // the froxel table this frame's build wrote, and the spheres its hot partition read
                        SsboSlots.ClusterCount when !VkClusterCounts.IsNull => VkClusterCounts,
                        SsboSlots.ClusterIdx when !VkClusterIdx.IsNull => VkClusterIdx,
                        SsboSlots.Movers when !VkMovers.IsNull => VkMovers,
                        SsboSlots.Chart when !LightmapBake.VkChart.IsNull => LightmapBake.VkChart,
                        _ => s.ZeroSsbo,
                    });
                    break;
                case VkBindKind.StorageImage:
                    images[ni++] = new ImageBinding(o.Binding, o.Binding == 0 ? img0 : img1, default, 0);
                    break;
                default:
                    sampled[nt++] = o.Binding switch
                    {
                        TextureSlots.Gbuf0 => new ImageBinding(o.Binding, s.G0),
                        TextureSlots.Gbuf1 => new ImageBinding(o.Binding, s.G1),
                        TextureSlots.Gbuf2 => new ImageBinding(o.Binding, s.G2),
                        TextureSlots.GbufDepth => new ImageBinding(o.Binding, s.Depth),
                        TextureSlots.ShadowVis => new ImageBinding(o.Binding, d.ShVis),
                        TextureSlots.ShadowKey => new ImageBinding(o.Binding, d.ShKey),
                        _ when o.Name == "uVfHist" && !hist.IsNull => new ImageBinding(o.Binding, hist, s.Linear),
                        _ => VkSampled(o, s, 0),
                    };
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], images[..ni]));
    }

    /// <summary>Stage <paramref name="resref"/>'s text as GL would compile it -- its own override, then the library
    /// swaps inside whichever text won, back to the embedded stage for an override that moved a block's term out of
    /// it -- with <paramref name="define"/> on top, and that text translated for Vulkan. Null when the record
    /// contract refused it (logged).</summary>
    private static (VkProgramText Text, string Src)? VkDefText(string resref, string? define, string label, ExtenderLog log)
    {
        string embedded = ShaderStages.Source(resref);
        string src = ShaderSource.Content(ShaderResource.Override(resref, log) ?? embedded, log);
        if (!ShaderContract.BlocksSafe(embedded, src))
        {
            log.Always($"AlloyRenderer: {label}'s override reads a uniform block's terms outside the block -- building the embedded stage");
            src = embedded;
        }
        if (!ShaderContract.RecordSafe(src) || !ShaderContract.RecordSafe(src, "CxChart", LightmapBake.RecFloats))
        {
            log.Always($"AlloyRenderer: {label}'s CxDraw or CxChart record disagrees with the extender's (an override older than it)");
            return null;
        }
        if (define != null) src = ShaderStages.WithDefine(src, define);
        // the compile-time text GL's own compute build makes: the vanilla splice, then the sizing tokens
        src = ShaderResource.Tokens(VanillaShader.Splice(src, log, label, true, true));
        return (VkGlsl.TranslateCompute(src, VkPrograms.Units), src);
    }

    /// <summary>Stage <paramref name="resref"/> (with <paramref name="define"/>, as GL's variants are defined)
    /// translated, compiled and linked, its loose block reflected and its locations resolved. Null, logged, for a
    /// text the contract guard, the translator or the toolchain refused.</summary>
    private static VkDefStage? VkDefBuild(VkBackend vk, string resref, string? define, string label, ExtenderLog log)
        => VkDefText(resref, define, label, log) is ({ } t, { } src) ? VkDefCompile(vk, t, src, label, log) : null;

    /// <summary>A translated compute stage compiled and linked, its loose block reflected against
    /// <paramref name="src"/> (the text it came from) and its locations resolved. Null, logged, when refused.</summary>
    private static VkDefStage? VkDefCompile(VkBackend vk, VkProgramText t, string src, string label, ExtenderLog log)
    {
        if (t.Error != null) { log.Always($"AlloyRenderer: {label} did not translate -- {t.Error}"); return null; }
        byte[]? spirv = VulkanToolchain.Compile(VkBackend.Translate(t.Cs), label, ShaderKind.ComputeShader, out string e);
        if (spirv == null) { log.Always($"AlloyRenderer: {label} did not compile -- {e}"); return null; }
        var st = new VkDefStage { Opaques = t.Opaques.ToArray() };
        st.Cs = vk.CreateShader(ShaderStage.Compute, spirv, label);
        if (st.Cs.IsNull) return null;
        var pd = new PipelineDesc { Cs = st.Cs, Label = label };
        st.Pipe = vk.CreatePipeline(in pd);
        if (st.Pipe.IsNull) return null;
        var names = new List<string>(t.Loose.Count);
        foreach (VkLoose u in t.Loose) names.Add(u.Name);
        if (VkSpirv.Block(spirv, 0, VulkanToolchain.UniformBase + VkGlsl.LooseBinding, names) is { } l)
        {
            var members = new List<LooseBlock.Member>(l.Members.Count);
            foreach (VkLoose u in t.Loose)
            {
                if (!l.Members.TryGetValue(u.Name, out var m)) continue;
                int count = u.Array.Length == 0 ? 1 : VkGlsl.ResolveInt(u.Array, src) ?? 1;
                members.Add(new LooseBlock.Member(u.Name, m.Offset, m.Size, count > 1 ? m.Size / count : 0));
            }
            st.Members = members.ToArray();
            st.Block = new byte[(l.Bytes + 15) & ~15];
        }
        st.Names = LooseBlock.Index(st.Members);
        LooseBlock.Resolve(st.Names, () =>
        {
            st.L.Cubes = LooseBlock.Loc("uLocalCubes");
            st.L.Material = LooseBlock.Loc("uMaterial");
            st.L.LightOcc = LooseBlock.Loc("uLightOcc");
            st.L.LmMovers = LooseBlock.Loc("uLmMovers");
            st.L.Rect = LooseBlock.Loc("uRect");
            st.L.InvVp = LooseBlock.Loc("uInvVp");
            st.L.Proj = LooseBlock.Loc("uProj");
            st.L.ShSize = LooseBlock.Loc("uShSize");
            st.L.ShRes = LooseBlock.Loc("uShRes");
            st.L.View = LooseBlock.Loc("uView");
            st.L.Grid = LooseBlock.Loc("uGrid");
            st.L.ProjXY = LooseBlock.Loc("uProjXY");
            st.L.Z = LooseBlock.Loc("uZ");
            st.L.LightCount = LooseBlock.Loc("uLightCount");
            st.L.Reach = LooseBlock.Loc("uReach");
            st.L.LightFloor = LooseBlock.Loc("uLightFloor");
            st.L.BinCap = LooseBlock.Loc("uBinCap");
            st.L.Atten = LooseBlock.Loc("uAtten");
            st.L.Hot = LooseBlock.Loc("uHot");
            st.L.ReflK = LooseBlock.Loc("uReflK");
            st.L.Rel = LooseBlock.Loc("uRel");
            st.L.Pad = LooseBlock.Loc("uPad");
            st.L.Tally = LooseBlock.Loc("uTally");
            st.L.DynPage = LooseBlock.Loc("uDynPage");
        });
        log.Info($"AlloyRenderer: {label} is built -- {st.Members.Length} loose members, {st.Opaques.Length} declarations");
        return st;
    }
}
