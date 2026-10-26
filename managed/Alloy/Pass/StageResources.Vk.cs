using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary><b>The Vulkan half of <see cref="StageResources"/>:</b> the declared images as Vulkan images, the declared
/// passes as compute pipelines translated from the text GL compiles, and the frame's head recorded into the frame's own
/// command list -- remakes, history swaps, the passes, their mips and the fog texel's readback -- before the first draw.
/// A host's program finds its stages' images by name through <see cref="VkUnits"/> and <see cref="VkBinds"/>.</summary>
internal static unsafe partial class StageResources
{
    /// <summary>One image or sampler a Vulkan program declares that names a stage image: its binding, the stage, the
    /// image, which half, and whether it is bound as a storage image.</summary>
    internal readonly record struct VkBind(uint Binding, int Stage, int Image, bool Prev, bool Storage);

    /// <summary>One pass as a compute pipeline, its loose block and the locations its uploads write.</summary>
    private sealed class VkPassProg
    {
        public ShaderId Cs;
        public PipelineId Pipe;
        public VkOpaque[] Opaques = [];
        public VkBind[] Binds = [];
        public VkUniformShim.Member[] Members = [];
        public Dictionary<string, int> Names = new();
        public byte[] Block = [];
        public StageEnvLoc Env;
        public FrameLoc Frame;
        public int Gx = 1, Gy = 1, Gz = 1;
    }

    /// <summary>One stage's Vulkan images (current and last frame's halves), the extents they were made at, which of
    /// them a draw program writes as a storage image, and its passes.</summary>
    private sealed class VkStageRes
    {
        public ImageId[] Tex = [], Prev = [];
        public int[] W = [], H = [], D = [];
        public VkPassProg?[]? Passes;
        public bool Made;
    }

    /// <summary>The Vulkan half on one device for one <see cref="ResourcesState"/>: a module change or a device change
    /// replaces it, its images and pipelines destroyed.</summary>
    private sealed class VkResState
    {
        public VkBackend? Dev;
        public ResourcesState? Owner;
        public readonly VkStageRes?[] Stages = new VkStageRes[MaxStages];
        public int HeadSeq = int.MinValue;
        /// <summary>Samplers by (mips, clamp).</summary>
        public readonly SamplerId[] Samplers = new SamplerId[4];
        /// <summary>The fog texel's readback ring and the submit each slot's copy completes with (-1: empty).</summary>
        public BufferId FogBuf;
        public readonly long[] FogAt = [-1, -1, -1];
        public int FogSlot;
        public bool SaidLive;
    }

    private static VkResState _vk = new();


    /// <summary>Fake GL program names a pass's loose block resolves under, past every other Vulkan shim range.</summary>
    private const uint VkShimBase = VkUniformShim.ProgramBase + (1u << 24);   // not-an-offset: a reserved name range

    /// <summary>Sampled bindings the stage images may take on Vulkan: from <see cref="TextureSlots.ResourceBase"/> to the
    /// device's set-0 sampled range.</summary>
    private const uint VkSampledTop = 48;

    /// <summary>One RGBA texel of the widest format a fog image can have, and the readback ring's slots.</summary>
    private const int VkFogBytes = 16, VkFogSlots = 3;

    /// <summary>A <c>sampler2D</c>/<c>sampler3D</c>/<c>image2D</c>/<c>image3D</c> declaration's type and name.</summary>
    private static readonly Regex OpaqueDecl = new(@"\b(sampler2D|sampler3D|image2D|image3D)\s+(\w+)\b", RegexOptions.CultureInvariant);

    /// <summary>A compute stage's work-group size, one axis per match.</summary>
    private static readonly Regex LocalSize = new(@"\blocal_size_([xyz])\s*=\s*(\w+)", RegexOptions.CultureInvariant);

    private static Format VkFormat(uint gl) => gl switch
    {
        Gl.RGBA8 => Format.Rgba8, Gl.RGBA16F => Format.Rgba16F, Gl.RGBA32F => Format.Rgba32F, Gl.RG16F => Format.Rg16F,
        Gl.RG8 => Format.Rg8, Gl.R16F => Format.R16F, Gl.R32F => Format.R32F, Gl.R8 => Format.R8,
        Gl.R11F_G11F_B10F => Format.R11G11B10F, _ => Format.Unknown,
    };

    /// <summary>Stage image <paramref name="name"/> (or its <c>_prev</c> half) among stages <paramref name="a"/> and
    /// <paramref name="b"/>'s images. False for any other name.</summary>
    private static bool FindImage(int a, int b, string name, out int stage, out int image, out bool prev)
    {
        foreach (int id in new[] { a, b })
        {
            if (At(id) is not { Failed: false } s) continue;
            for (int i = 0; i < s.Images.Length; i++)
            {
                string n = s.Images[i].Name;
                if (string.Equals(n, name, StringComparison.Ordinal)) { stage = id; image = i; prev = false; return true; }
                if (name.Length == n.Length + 5 && name.StartsWith(n, StringComparison.Ordinal) && name.EndsWith("_prev", StringComparison.Ordinal))
                { stage = id; image = i; prev = true; return true; }
            }
        }
        stage = image = -1;
        prev = false;
        return false;
    }

    /// <summary><paramref name="baseUnits"/> with every stage image <paramref name="text"/> declares given a binding of
    /// its own: a sampler from <see cref="TextureSlots.ResourceBase"/>, an image below <paramref name="imgUnits"/>. Null
    /// with <paramref name="why"/> when a declaration disagrees with its image or the bindings run out. Cold.</summary>
    internal static IReadOnlyDictionary<string, uint>? VkUnits(IReadOnlyDictionary<string, uint> baseUnits, string text, int a,
                                                              int b, uint imgUnits, out string why)
    {
        why = "";
        if (!Live(a) && !Live(b)) return baseUnits;
        var d = new Dictionary<string, uint>(baseUnits, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        uint tex = TextureSlots.ResourceBase, img = 0;
        foreach (Match m in OpaqueDecl.Matches(VkGlsl.Mask(text)))
        {
            string type = m.Groups[1].Value, name = m.Groups[2].Value;
            if (!FindImage(a, b, name, out int stage, out int image, out bool prev) || !taken.Add(name)) continue;
            Img im = _gl.Stages[stage]!.Images[image];
            if (prev && !im.History) { why = $"the program reads `{name}`, but `{im.Name}` is not declared `history`"; return null; }
            bool wants3d = type.EndsWith("3D", StringComparison.Ordinal);
            if (wants3d != (im.Shape == Shape.Tex3d))
            { why = $"`{name}` is declared {(wants3d ? "3D" : "2D")} but its cx_image is {(im.Shape == Shape.Tex3d ? "3d" : "2d")}"; return null; }
            if (type.StartsWith("image", StringComparison.Ordinal))
            {
                if (img >= imgUnits) { why = $"more than {imgUnits} image units are needed"; return null; }
                d[name] = img++;
            }
            else
            {
                if (tex >= VkSampledTop) { why = $"more than {VkSampledTop - TextureSlots.ResourceBase} stage samplers are needed on Vulkan"; return null; }
                d[name] = tex++;
            }
        }
        return d;
    }

    /// <summary>The declarations of a translated program that name stages <paramref name="a"/> and <paramref name="b"/>'s
    /// images, by binding. A storage image a DRAW program binds (<paramref name="draw"/>) is noted, so the frame's head
    /// hands it over writable. Cold: once per program.</summary>
    internal static VkBind[] VkBinds(IReadOnlyList<VkOpaque> opaques, int a, int b, bool draw)
    {
        var list = new List<VkBind>();
        foreach (VkOpaque o in opaques)
        {
            if (o.Set != 0 || o.Kind is not (VkBindKind.Sampled or VkBindKind.StorageImage)) continue;
            if (!FindImage(a, b, o.Name, out int stage, out int image, out bool prev)) continue;
            bool storage = o.Kind == VkBindKind.StorageImage;
            list.Add(new VkBind(o.Binding, stage, image, prev, storage));
            if (storage && draw) _gl.Stages[stage]!.Images[image].VkDrawStore = true;
        }
        return list.ToArray();
    }

    /// <summary>The image and sampler binding <paramref name="slot"/> takes among <paramref name="binds"/>, or null for a
    /// binding no stage image holds. A stage that stood down, or an image not made yet, binds the stand-in of its shape.
    /// Hot: no allocation.</summary>
    internal static ImageBinding? VkImageFor(ReadOnlySpan<VkBind> binds, uint slot, bool storage)
    {
        foreach (VkBind b in binds)
            if (b.Binding == slot && b.Storage == storage) return VkBinding(b);
        return null;
    }

    private static ImageBinding VkBinding(in VkBind b)
    {
        VkResState v = _vk;
        Img? im = At(b.Stage) is { Failed: false } s ? s.Images[b.Image] : null;
        ImageId i = Res(b.Stage) is { Made: true } r && im != null ? b.Prev ? r.Prev[b.Image] : r.Tex[b.Image] : default;
        bool vol = im?.Shape == Shape.Tex3d;
        if (i.IsNull) i = vol ? AlloyRenderer.VkStandIn3D : AlloyRenderer.VkStandIn2D;
        if (b.Storage) return new ImageBinding(b.Binding, i);
        bool clamp = im == null || im.Clamp || im.Shape == Shape.View;
        return new ImageBinding(b.Binding, i, v.Samplers[(im?.Mips == true ? 2 : 0) + (clamp ? 1 : 0)]);
    }

    /// <summary>The frame terms (<c>cxFrame</c>, <c>cxInvVp</c> ...) into the block being captured, for a program built
    /// from stages <paramref name="a"/> and <paramref name="b"/>. Hot.</summary>
    internal static void VkUploadFrame(in FrameLoc loc, int a, int b, bool main)
    {
        if (loc.Any) loc.Upload(FrameFor(a, b), main, !VkOverlay.IsNull);
    }

    /// <summary>The overlay this frame's passes drew, for the resolve to add over the frame; null when no live stage
    /// declares one or its passes did not run this frame. Hot.</summary>
    internal static ImageId VkOverlay
    {
        get
        {
            var g = _gl;
            if (At(g.OverlayStage) is not { Failed: false } s || s.RanFrame != AlloyRenderer.FrameSeq) return default;
            return Res(g.OverlayStage) is { Made: true } r ? r.Tex[s.OverlayImage] : default;
        }
    }

    /// <summary>Stage <paramref name="id"/>'s Vulkan half, made on first ask with the arrays its images need.</summary>
    private static VkStageRes? Res(int id)
    {
        if (At(id) is not { } s) return null;
        VkResState v = _vk;
        if (v.Owner != _gl) { if (v.Dev == null) return null; v = VkState(v.Dev); }
        return v.Stages[id] ??= new VkStageRes
        {
            Tex = new ImageId[s.Images.Length], Prev = new ImageId[s.Images.Length], W = new int[s.Images.Length],
            H = new int[s.Images.Length], D = new int[s.Images.Length],
        };
    }

    /// <summary>The state for this device and this module, the old one's objects destroyed when either changed.</summary>
    private static VkResState VkState(VkBackend vk)
    {
        VkResState v = _vk;
        if (v.Dev == vk && v.Owner == _gl) return v;
        if (v.Dev != null)
        {
            for (int i = 0; i < v.Stages.Length; i++) VkRelease(v.Dev, v.Stages[i]);
            if (v.Dev == vk) { v.Dev.Destroy(v.FogBuf); foreach (SamplerId s in v.Samplers) v.Dev.Destroy(s); }
        }
        _vk = v = new VkResState { Dev = vk, Owner = _gl };
        for (int k = 0; k < 4; k++)
            v.Samplers[k] = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, k >= 2 ? MipMode.Linear : MipMode.None,
                                                             (k & 1) != 0 ? Wrap.Clamp : Wrap.Repeat));
        return v;
    }

    private static void VkRelease(VkBackend vk, VkStageRes? r)
    {
        if (r == null) return;
        VkDropImages(vk, r);
        foreach (VkPassProg? p in r.Passes ?? [])
        {
            if (p == null) continue;
            vk.Destroy(p.Pipe);
            vk.Destroy(p.Cs);
        }
        r.Passes = null;
    }

    private static void VkDropImages(VkBackend vk, VkStageRes r)
    {
        for (int i = 0; i < r.Tex.Length; i++)
        {
            vk.Destroy(r.Tex[i]); vk.Destroy(r.Prev[i]);
            r.Tex[i] = r.Prev[i] = default;
            r.W[i] = r.H[i] = r.D[i] = 0;
        }
        r.Made = false;
    }

    /// <summary>The frame's head on Vulkan, main view only, recorded into <paramref name="cl"/> ahead of every draw:
    /// each live stage's passes built (once), its images made at the sizes this view asks for (its <c>once</c> passes run
    /// over a remake), then -- for each stage a host staged -- its history swapped and its frame passes run, the fog
    /// texel read back, and every image handed to the draws in the layout they bind it in.</summary>
    internal static void VkRun(VkBackend vk, CommandList cl, int* viewport, bool capturing, ExtenderLog? log)
    {
        var g = _gl;
        int f = AlloyRenderer.FrameSeq;
        if (g.Count == 0 || capturing) return;
        VkResState v = VkState(vk);
        if (v.HeadSeq == f) return;
        v.HeadSeq = f;
        int ran = 0;
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Stages[i] is not { } s) continue;
            if (s.Failed) { VkRelease(vk, v.Stages[i]); continue; }
            if (Res(i) is not { } r) continue;
            if (r.Passes == null && !VkBuildPasses(vk, i, s, r, log)) continue;
            if (!VkEnsure(vk, cl, i, s, r, viewport, log)) continue;
            if (s.LastUse < f - 1) continue;
            if (Gap(s.RanFrame, f)) s.FreshFrame = f;
            s.RanFrame = f;
            if (s.FreshFrame == f) s.FreshRuns++;
            for (int k = 0; k < s.Images.Length; k++)
                if (s.Images[k].Swap) (r.Tex[k], r.Prev[k]) = (r.Prev[k], r.Tex[k]);
            VkPasses(cl, i, s, r, viewport, once: false);
            if (g.FogStage == i) VkReadFog(vk, cl, s, r, log);
            ran++;
        }
        VkHandOver(cl);
        if (ran > 0 && !v.SaidLive)
        {
            v.SaidLive = true;
            log?.Always($"StageResources: the Vulkan native stages are LIVE -- {ran} stage(s) ran their passes this frame "
                      + "into Vulkan images, read by their hosts' Vulkan programs");
        }
    }

    /// <summary>Every made image into the layout its readers bind it in: writable where a draw program stores into it,
    /// sampled otherwise. A draw cannot place the barrier itself once its pass has begun.</summary>
    private static void VkHandOver(CommandList cl)
    {
        var g = _gl;
        Span<ImageId> read = stackalloc ImageId[MaxImages * 2], write = stackalloc ImageId[MaxImages];
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Stages[i] is not { Failed: false } s || Res(i) is not { Made: true } r) continue;
            int nr = 0, nw = 0;
            for (int k = 0; k < r.Tex.Length; k++)
            {
                if (s.Images[k].VkDrawStore) write[nw++] = r.Tex[k]; else read[nr++] = r.Tex[k];
                if (!r.Prev[k].IsNull) read[nr++] = r.Prev[k];
            }
            if (nr > 0) cl.ReadImages(read[..nr]);
            if (nw > 0) cl.ReadImages(write[..nw], Access.StorageWrite);
        }
    }

    /// <summary>A stage's images at the sizes the declarations and the main view ask for, zeroed, and its <c>once</c>
    /// passes run over them. Idempotent: a frame that changes nothing costs the extent compare.</summary>
    private static bool VkEnsure(VkBackend vk, CommandList cl, int id, Stage s, VkStageRes r, int* viewport, ExtenderLog? log)
    {
        var g = _gl;
        int vw = g.HaveView ? g.ViewW : Math.Max(viewport[2], 1);
        int vh = g.HaveView ? g.ViewH : Math.Max(viewport[3], 1);
        bool stale = !r.Made;
        for (int k = 0; k < s.Images.Length && !stale; k++)
        {
            Extent(s.Images[k], vw, vh, out int w, out int h, out int d);
            stale = r.W[k] != w || r.H[k] != h || r.D[k] != d;
        }
        if (!stale) return true;
        if (!Budget(s, vw, vh, out long bytes, out string why)) { Fail(id, why, log); return false; }
        s.Bytes = bytes;
        VkDropImages(vk, r);
        for (int k = 0; k < s.Images.Length; k++)
        {
            Img im = s.Images[k];
            Extent(im, vw, vh, out int w, out int h, out int d);
            r.Tex[k] = VkImage(vk, cl, s, im, w, h, d);
            r.Prev[k] = im.History ? VkImage(vk, cl, s, im, w, h, d) : default;
            if (r.Tex[k].IsNull || (im.History && r.Prev[k].IsNull))
            { Fail(id, $"image `{im.Name}` ({w}x{h}x{d}) could not be made on Vulkan", log); VkDropImages(vk, r); return false; }
            r.W[k] = w; r.H[k] = h; r.D[k] = d;
        }
        r.Made = s.Made = true;
        s.MadeW = vw; s.MadeH = vh;
        s.FreshFrame = AlloyRenderer.FrameSeq;
        if (s.Images.Length > 0)
            log?.Info($"StageResources: {s.Ref} made {s.Images.Length} Vulkan image(s), {s.Bytes / (1024.0 * 1024.0):F1} MiB "
                    + $"(main view {vw}x{vh})");
        VkPasses(cl, id, s, r, viewport, once: true);
        return true;
    }

    /// <summary>One declared image on Vulkan, zeroed so a frame before the first pass that writes it reads a known value.</summary>
    private static ImageId VkImage(VkBackend vk, CommandList cl, Stage s, Img im, int w, int h, int d)
    {
        bool vol = im.Shape == Shape.Tex3d;
        ImageId i = vk.CreateImage(new ImageDesc(vol ? ImageKind.Tex3D : ImageKind.Tex2D, VkFormat(im.Format), w, h, vol ? d : 1,
                                                 Levels(im, w, h, d), 1, 1,
                                                 ImageUse.Sampled | ImageUse.Storage | ImageUse.CopyDst | ImageUse.CopySrc,
                                                 $"vk stage {s.Ref} {im.Name}"));
        if (!i.IsNull) cl.ClearImage(i);
        return i;
    }

    /// <summary>Every pass of one kind in declaration order, each image a pass wrote as an image re-mipped after it.</summary>
    private static void VkPasses(CommandList cl, int id, Stage s, VkStageRes r, int* viewport, bool once)
    {
        if (r.Passes == null) return;
        for (int i = 0; i < s.Passes.Length; i++)
        {
            Pass p = s.Passes[i];
            if (p.Once != once || r.Passes[i] is not { } q) continue;
            if (!VkDispatch(cl, id, s, r, p, q, viewport)) return;
        }
    }

    private static bool VkDispatch(CommandList cl, int id, Stage s, VkStageRes r, Pass p, VkPassProg q, int* viewport)
    {
        Array.Clear(q.Block);
        VkUniformShim.Capture(q.Members, q.Block);
        try
        {
            q.Env.Upload(viewport);
            VkUploadFrame(q.Frame, id, -1, main: true);
        }
        finally { VkUniformShim.EndCapture(); }
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<ImageBinding> sampled = stackalloc ImageBinding[(int)VkSampledTop];
        Span<ImageBinding> images = stackalloc ImageBinding[(int)PassImgUnits];
        int nu = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in q.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                // a loose block the compiler dropped is in no module: nothing to bind
                case VkBindKind.UniformBlock when o.Binding != UboSlots.Engine && q.Block.Length == 0:
                    break;
                case VkBindKind.UniformBlock:
                    BufferId arena;
                    int len, at = o.Binding == UboSlots.Engine ? AlloyRenderer.VkStageEngine(cl, out arena, out len)
                                                               : AlloyRenderer.VkStageUniforms(cl, q.Block, out arena, out len);
                    if (at < 0) return false;
                    if (nu < ubo.Length) ubo[nu++] = new BufferBinding(o.Binding, arena, at, len);
                    break;
                case VkBindKind.StorageImage:
                    if (ni < images.Length && VkImageFor(q.Binds, o.Binding, true) is { } w) images[ni++] = w;
                    break;
                case VkBindKind.Sampled:
                    if (nt < sampled.Length)
                        sampled[nt++] = VkImageFor(q.Binds, o.Binding, false)
                                        ?? new ImageBinding(o.Binding, o.Type == "sampler3D" ? AlloyRenderer.VkStandIn3D : AlloyRenderer.VkStandIn2D,
                                                            _vk.Samplers[1]);
                    break;
            }
        }
        cl.SetPipeline(q.Pipe);
        cl.BindSet(0, new BindSet(ubo[..nu], default, sampled[..nt], images[..ni]));
        int x = r.W[p.Image], y = r.H[p.Image], z = r.D[p.Image];
        cl.Dispatch((x + q.Gx - 1) / q.Gx, (y + q.Gy - 1) / q.Gy, (z + q.Gz - 1) / q.Gz);
        foreach (VkBind b in q.Binds)
            if (b.Storage && !b.Prev && s.Images[b.Image].Mips) cl.GenerateMips(r.Tex[b.Image]);
        s.Runs++;
        return true;
    }

    /// <summary>Every declared pass as a Vulkan compute pipeline from the text GL compiles -- the <see cref="ShdPassCs"/>
    /// wrapper with the pass spliced in, the override first -- then the swap and overlay rules checked against what the
    /// passes write. A pass that will not build refuses the whole stage, as on GL.</summary>
    private static bool VkBuildPasses(VkBackend vk, int id, Stage s, VkStageRes r, ExtenderLog? log)
    {
        r.Passes = new VkPassProg?[s.Passes.Length];
        string embedded = ShaderStages.Source(ShdPassCs);
        string over = ShaderResource.Override(ShdPassCs, log) ?? embedded;
        for (int i = 0; i < s.Passes.Length; i++)
        {
            Pass p = s.Passes[i];
            if (!VanillaShader.InChain(p.Ref))
            { Fail(id, $"pass {p.Ref}.shd is not in the resource chain (or alloy.vanillabridge=0)", log); return false; }
            string what = $"Vulkan pass ({s.Ref}: {p.Ref})";
            uint name = VkShimBase + (uint)(id * MaxPasses + i);
            VkPassProg? q = VkPass(vk, over, p.Ref, id, name, what, log);
            if (q == null && !ReferenceEquals(over, embedded))
            {
                log?.Info($"{what}: the {ShdPassCs} override did not build -- taking the embedded wrapper");
                q = VkPass(vk, embedded, p.Ref, id, name, what, log);
            }
            if (q == null) { Fail(id, $"pass {p.Ref}.shd did not build as a Vulkan compute pipeline (logged above)", log); return false; }
            r.Passes[i] = q;
            var stores = new List<ImageBind>();
            var reads = new List<ImageBind>();
            foreach (VkBind b in q.Binds)
                (b.Storage ? stores : reads).Add(new ImageBind { Loc = -1, Stage = b.Stage, Image = b.Image, Prev = b.Prev, Unit = b.Binding });
            p.Binds = stores.ToArray();
            p.Samplers = reads.ToArray();
            p.Gx = q.Gx; p.Gy = q.Gy; p.Gz = q.Gz;
            log?.Info($"{what}: built, {q.Gx}x{q.Gy}x{q.Gz} work group, {p.Binds.Length} image(s), {p.Samplers.Length} sampler(s)");
        }
        if (!MarkSwaps(s, out string refused) || !OverlayWritten(s, out refused)) { Fail(id, refused, log); return false; }
        return true;
    }

    /// <summary>One pass wrapper with the region's resref written in, translated, compiled and linked, its loose block
    /// reflected and its locations resolved under fake program <paramref name="name"/>. Null, logged, when refused.</summary>
    private static VkPassProg? VkPass(VkBackend vk, string wrapper, string resref, int id, uint name, string what, ExtenderLog? log)
    {
        string text = ShaderResource.Tokens(VanillaShader.Splice(GlProgram.Lib(wrapper.Replace(CsToken, resref, StringComparison.Ordinal),
                                                                               log, what), log, what, true, true));
        if (VkUnits(VkPrograms.Units, text, id, -1, PassImgUnits, out string why) is not { } units)
        { log?.Always($"StageResources: {what} -- {why}"); return null; }
        VkProgramText t = VkGlsl.TranslateCompute(text, units);
        if (t.Error != null) { log?.Always($"StageResources: {what} did not translate -- {t.Error}"); return null; }
        foreach (VkOpaque o in t.Opaques)
            if (o.Set == 0 && (o.Kind == VkBindKind.StorageBlock
                               || o.Kind == VkBindKind.UniformBlock && o.Binding != UboSlots.Engine && o.Binding != VkGlsl.LooseBinding))
            { log?.Always($"StageResources: {what} declares block {o.Name}, which a pass has no buffer for on Vulkan"); return null; }
        byte[]? spirv = VulkanToolchain.Compile(VkBackend.Translate(t.Cs), what, ShaderKind.ComputeShader, out string e);
        if (spirv == null) { log?.Always($"StageResources: {what} did not compile -- {e}"); return null; }
        var q = new VkPassProg { Opaques = t.Opaques.ToArray() };
        q.Cs = vk.CreateShader(ShaderStage.Compute, spirv, what);
        if (q.Cs.IsNull) return null;
        var pd = new PipelineDesc { Cs = q.Cs, Label = what };
        q.Pipe = vk.CreatePipeline(in pd);
        if (q.Pipe.IsNull) { vk.Destroy(q.Cs); return null; }
        var names = new List<string>(t.Loose.Count);
        foreach (VkLoose u in t.Loose) names.Add(u.Name);
        if (VkSpirv.Block(spirv, 0, VulkanToolchain.UniformBase + VkGlsl.LooseBinding, names) is { } l)
        {
            var members = new List<VkUniformShim.Member>(l.Members.Count);
            foreach (VkLoose u in t.Loose)
            {
                if (!l.Members.TryGetValue(u.Name, out var m)) continue;
                int count = u.Array.Length == 0 ? 1 : VkGlsl.ResolveInt(u.Array, text) ?? 1;
                members.Add(new VkUniformShim.Member(u.Name, m.Offset, m.Size, count > 1 ? m.Size / count : 0));
            }
            q.Members = members.ToArray();
            q.Block = VkUniformShim.Block(l.Bytes);
        }
        q.Names = VkUniformShim.Index(q.Members);
        VkUniformShim.Resolve(q.Names, () => { q.Env = StageEnvLoc.Locate(name); q.Frame = FrameLoc.Locate(name); });
        foreach (Match m in LocalSize.Matches(VkGlsl.Mask(t.Cs)))
        {
            int n = Math.Max(VkGlsl.ResolveInt(m.Groups[2].Value, t.Cs) ?? 1, 1);
            switch (m.Groups[1].Value) { case "x": q.Gx = n; break; case "y": q.Gy = n; break; default: q.Gz = n; break; }
        }
        q.Binds = VkBinds(q.Opaques, id, -1, draw: false);
        return q;
    }

    /// <summary>The <c>cx_fog</c> texel read back without a stall: this frame's copy into one ring slot, and the newest
    /// slot whose submit has finished becomes the fog's colour. A colour meant for the fog changes over minutes.</summary>
    private static void VkReadFog(VkBackend vk, CommandList cl, Stage s, VkStageRes r, ExtenderLog? log)
    {
        var g = _gl;
        VkResState v = _vk;
        if (s.FogImage < 0 || g.FogMiss.Length > 0 || r.Tex[s.FogImage].IsNull) return;
        Format fmt = VkFormat(s.Images[s.FogImage].Format);
        if (fmt is not (Format.Rgba8 or Format.Rgba16F or Format.Rgba32F or Format.Rg16F or Format.R16F or Format.R32F))
        {
            g.FogMiss = $"its image's format ({fmt}) is not one the Vulkan readback decodes";
            log?.Always($"StageResources: {s.Ref}'s fog colour cannot be read back ({g.FogMiss}) -- the fog keeps the area's own colour");
            return;
        }
        if (v.FogBuf.IsNull)
            v.FogBuf = vk.CreateBuffer(new BufferDesc(VkFogBytes * VkFogSlots, BufferUse.CopyDst, MemoryKind.Readback, "vk stage fog readback"));
        if (v.FogBuf.IsNull) { g.FogMiss = "the readback buffer was refused"; return; }
        Span<byte> texel = stackalloc byte[VkFogBytes];
        long newest = -1;
        int pick = -1;
        for (int k = 0; k < VkFogSlots; k++)
            if (v.FogAt[k] > newest && vk.Finished(v.FogAt[k])) { newest = v.FogAt[k]; pick = k; }
        if (pick >= 0 && vk.ReadMapped(v.FogBuf, pick * VkFogBytes, texel) && Decode(fmt, texel, out System.Numerics.Vector3 c))
        {
            g.Fog = c;
            g.HaveFog = true;
            v.FogAt[pick] = -1;
        }
        int slot = v.FogSlot;
        v.FogSlot = (slot + 1) % VkFogSlots;
        cl.CopyImageToBuffer(new CopyToBufferCmd(r.Tex[s.FogImage], new ImageRegion(0, s.FogX, s.FogY, 0), 1, 1, v.FogBuf, slot * VkFogBytes));
        v.FogAt[slot] = vk.Submitted;
    }

    /// <summary>One texel of <paramref name="fmt"/> as a linear colour, negatives and non-finite values refused.</summary>
    private static bool Decode(Format fmt, ReadOnlySpan<byte> b, out System.Numerics.Vector3 c)
    {
        float r, gg = 0f, bb = 0f;
        switch (fmt)
        {
            case Format.Rgba8: r = b[0] / 255f; gg = b[1] / 255f; bb = b[2] / 255f; break;
            case Format.Rgba16F or Format.Rg16F or Format.R16F:
                r = (float)BitConverter.ToHalf(b);
                if (fmt != Format.R16F) gg = (float)BitConverter.ToHalf(b[2..]);
                if (fmt == Format.Rgba16F) bb = (float)BitConverter.ToHalf(b[4..]);
                break;
            default:
                r = BitConverter.ToSingle(b);
                if (fmt == Format.Rgba32F) { gg = BitConverter.ToSingle(b[4..]); bb = BitConverter.ToSingle(b[8..]); }
                break;
        }
        c = new System.Numerics.Vector3(MathF.Max(r, 0f), MathF.Max(gg, 0f), MathF.Max(bb, 0f));
        return float.IsFinite(r) && float.IsFinite(gg) && float.IsFinite(bb);
    }
}
