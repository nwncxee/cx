using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using NuiModelDoll = CX.Services.Nui.NuiModelDoll;

namespace CX.Alloy;

/// <summary>
/// The GUI path on Vulkan, under <c>alloy.backend vk</c>: the layers <see cref="GuiCapture"/> listed and CX's own
/// windows either side of NUI are recorded into the <see cref="CX.Rhi"/> command list and drawn into the composite
/// Vulkan presents, over the same depth stacking GL Alloy's <see cref="GuiPass"/> uses. One flush is one submit.
/// </summary>
internal static unsafe partial class GuiPassVk
{
    /// <summary>One model view's targets on Vulkan: the colour image standing in for the view's GL texture (both the
    /// draws' target and what a later panel samples), its depth, and the solve's two scratch images.</summary>
    private sealed class View
    {
        public uint Tex;
        public int W, H;
        public ImageId Colour, Depth, Black, White;
        /// <summary>A pass of this flush has written the colour image -- and, with <see cref="Solved"/>, the solve's
        /// two scratches -- so a later pass that may sample them is handed them first.</summary>
        public bool Drawn, Solved;
    }

    /// <summary>Everything the device owns, in one object: a new device replaces it whole.</summary>
    private sealed class GuiVkState
    {
        public VkBackend? Dev;
        public bool Failed, Live, OffThreadSaid, PrimSaid, BlendSaid, SolveSaid, DropSaid;
        /// <summary>Alloy's own GUI program and the two loose terms it reads per flush.</summary>
        public GuiVkProg? Prog;
        public int BoneStart = -1, Gamma = -1;
        /// <summary>The model-view solve's program and its background term.</summary>
        public GuiVkProg? Solve;
        public int SolveBg = -1;
        /// <summary>Content programs by <see cref="GuiContent.Key"/>; null = no program of its own, said once.</summary>
        public readonly Dictionary<int, GuiContent.VkPair?> Content = new();
        /// <summary>The records-and-bones storage buffer, the stream vertex and index buffers, and the loose arena.</summary>
        public BufferId Recs, Vtx, Idx, Loose;
        public long RecCap, VtxCap, IdxCap;
        public byte[] Arena = new byte[LooseCap];
        public int ArenaUsed;
        /// <summary>Alloy's GUI program's block in this flush's arena, -1 until a draw through it stages it.</summary>
        public int GuiBlockAt = -1;
        /// <summary>Where the open render pass's arena bytes start, and the attachment treatment it begins with.</summary>
        public int Arena0;
        public bool ClearOwed, ClearDepth, ClearColour;
        public System.Numerics.Vector4 Clear;
        /// <summary>Where this flush's screen layers land (the null id = the composite), and whether its first pass
        /// clears that image to opaque black: the A/B's terms, not a frame's.</summary>
        public ImageId Into;
        public bool Black;
        /// <summary>The GUI's own depth, over the whole composite, and the size it was made at.</summary>
        public ImageId Depth;
        public int DepthW, DepthH;
        /// <summary>The master palette's Vulkan copy and the send count it came from.</summary>
        public ImageId Palette;
        public long PaletteSends = -1;
        /// <summary>One transparent-black texel, for a draw whose texture has no Vulkan image.</summary>
        public ImageId StandIn;
        public SamplerId Linear, Nearest;
        public readonly VertexLayoutId[] ArenaLayout = new VertexLayoutId[MeshArena.Formats];
        /// <summary>Stream layouts by (format, stride); a format the table cannot lay out has none.</summary>
        public readonly Dictionary<long, VertexLayoutId> Stream = new(8);
        public readonly View[] Views = new View[NuiModelDoll.MaxViews];
        public CommandList? Cmds;
        public CommandList? Side;
        public long Drawn, Skipped, NoLayout, NoTexture, Submits, Dropped;
        /// <summary>CX's own windows' programs and buffers.</summary>
        public readonly WinState Win = new();

        public GuiVkState() { for (int i = 0; i < Views.Length; i++) Views[i] = new View(); }
    }

    private static GuiVkState _s = new();

    /// <summary>The loose arena: every bind's block in one flush. Past it the flush refuses (logged) rather than
    /// reallocating under a list that names the old buffer.</summary>
    private const int LooseCap = 1 << 20;

    /// <summary>The GUI's depth format on Vulkan. GL packs stencil beside it because its captured layers clear
    /// stencil; nothing in the pass tests it, so Vulkan carries depth alone.</summary>
    private const Format DepthFormat = Format.D32F;

    /// <summary>The composite's format, which <c>VkBackend.Resize</c> exports the pair in.</summary>
    private const Format CompositeFormat = Format.Rgba8;

    /// <summary>A model view's colour format: the RGBA8 its GL texture is, so a panel samples the same texels.</summary>
    private const Format ViewFormat = Format.Rgba8;

    /// <summary>What the GUI path cost this session, for the status line.</summary>
    internal static string Stats()
        => _s.Dev == null ? "" : $"gui vk: {_s.Submits} submits, {_s.Drawn} draws"
         + (_s.Dropped == 0 ? "" : $", {_s.Dropped} submit(s) REFUSED (those frames' GUI NOT DRAWN)")
         + (_s.Skipped == 0 ? "" : $", {_s.Skipped} skipped")
         + (_s.NoLayout == 0 ? "" : $", {_s.NoLayout} with no vertex layout (NOT DRAWN)")
         + (_s.NoTexture == 0 ? "" : $", {_s.NoTexture} on the stand-in texture");

    /// <summary>The flush: layers <paramref name="from"/>..<paramref name="mid"/>, CX's below-NUI windows, layers
    /// <paramref name="mid"/>..<paramref name="to"/>, the above-NUI windows, in one submit. <paramref name="into"/> is
    /// null for the composite Vulkan presents; <paramref name="clearBlack"/> clears it first, which is the A/B's.</summary>
    internal static bool Flush(int from, int mid, int to, bool windows, ExtenderLog log, ImageId into = default, bool clearBlack = false)
    {
        if (AlloyDevice.Vulkan(log) is not VkBackend vk) return false;
        if (!vk.OnOwnThread)
        {
            if (!_s.OffThreadSaid)
            {
                _s.OffThreadSaid = true;
                log.Always("GuiPassVk: this flush is not on the thread the Vulkan device belongs to -- the GUI is "
                         + "NOT DRAWN on such flushes; alloy.renderthread splits the two");
            }
            return false;
        }
        if (!Ensure(vk, log)) return false;
        GuiVkState s = _s;
        s.ArenaUsed = 0;
        s.GuiBlockAt = -1;
        foreach (View v in s.Views) v.Drawn = v.Solved = false;
        s.Into = into;
        s.Black = clearBlack;
        // the latch is taken whichever way it falls: on the A/B the GL half must not clear again, over black
        s.ClearOwed = GuiPass.TakeFrameClear() || clearBlack;
        if (!Stage(vk, log)) return false;
        CommandList cl = s.Cmds!;
        cl.Reset();
        Uploads(cl);
        bool ok = Record(vk, cl, from, mid, log)
                  && (!windows || Windows(vk, cl, CX.Ui.UiLayer.BelowNui, log))
                  && Record(vk, cl, mid, to, log)
                  && (!windows || Windows(vk, cl, CX.Ui.UiLayer.AboveNui, log))
                  && Submit(vk, cl, log);
        if (ok && !s.Live)
        {
            s.Live = true;
            log.Always($"GuiPassVk: LIVE -- Vulkan draws the GUI and CX's windows into the composite it presents "
                     + $"({GuiCapture.LayerN} scene(s), {GuiCapture.DrawN} draw(s) this flush)");
        }
        return ok;
    }

    /// <summary>The flush's one submit; a refusal is said once and counted after.</summary>
    private static bool Submit(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        GuiVkState s = _s;
        if (!vk.Submit(cl))
        {
            if (!s.DropSaid)
            {
                s.DropSaid = true;
                log.Always("GuiPassVk: the GUI submit was REFUSED (logged above) -- that frame's GUI is NOT DRAWN, "
                         + "and further refusals are only counted (/alloy)");
            }
            s.Dropped++;
            return false;
        }
        s.Submits++;
        return true;
    }

    /// <summary>The device's programs, buffers, layouts and stand-ins, made once per device, and the depth and
    /// palette that follow the frame. False = the GUI cannot be drawn at all, which fails Alloy loudly.</summary>
    private static bool Ensure(VkBackend vk, ExtenderLog log)
    {
        if (_s.Dev != vk) _s = new GuiVkState { Dev = vk };
        GuiVkState s = _s;
        if (s.Failed) return false;
        if (s.Prog == null && !Make(vk, s, log)) { s.Failed = true; return false; }
        int w = PresentTarget.Width, h = PresentTarget.Height;
        if (w <= 0 || h <= 0) return false;
        if (s.Depth.IsNull || s.DepthW != w || s.DepthH != h)
        {
            vk.Destroy(s.Depth);
            s.Depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, DepthFormat, w, h, 1, 1, 1, 1, ImageUse.DepthTarget, "vk gui depth"));
            s.DepthW = w; s.DepthH = h;
            if (s.Depth.IsNull)
            {
                Fail($"the Vulkan GUI depth at {w}x{h} was refused", log);
                return false;
            }
            log.Info($"GuiPassVk: drawing into the present image, {w}x{h} colour with a depth of Alloy's own ({DepthFormat})");
        }
        if (Db.PltPalettes.Sends != s.PaletteSends && !Db.PltPalettes.Sent.IsEmpty)
        {
            vk.Destroy(s.Palette);
            s.Palette = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, Db.PltPalettes.Width, Db.PltPalettes.Height,
                                                     1, 1, 1, 1, ImageUse.Sampled | ImageUse.CopyDst, "vk gui plt palette"));
            if (!s.Palette.IsNull) vk.Upload(s.Palette, 0, 0, Db.PltPalettes.Sent);
            s.PaletteSends = Db.PltPalettes.Sends;
        }
        return true;
    }

    /// <summary>Everything that does not follow the frame: the two programs, the arena layouts, the samplers, the
    /// stand-in texel, the buffers and the two command lists.</summary>
    private static bool Make(VkBackend vk, GuiVkState s, ExtenderLog log)
    {
        var none = new Dictionary<string, uint>(StringComparer.Ordinal);
        s.Prog = GuiVkProg.Build(vk, Text("cx_gui_v.glsl", log), Text("cx_gui_f.glsl", log), none, "Alloy GUI",
                                 p => { s.BoneStart = p.Loc("uBoneStart"); s.Gamma = p.Loc("uGamma"); }, log);
        if (s.Prog == null) { Fail("the Vulkan GUI program did not build (logged above)", log); return false; }
        s.Solve = GuiVkProg.Build(vk, Text("cx_fullscreen_v.glsl", log), Text("cx_mvsolve_f.glsl", log), none,
                                  "Alloy model view solve", p => s.SolveBg = p.Loc("uBg"), log);
        if (s.Solve == null) log.Always("GuiPassVk: the Vulkan model-view solve did not build (logged above) -- a view "
                                      + "with additive draws keeps the single pass, so its background shows through them");
        for (int f = 0; f < MeshArena.Formats; f++)
        {
            s.ArenaLayout[f] = vk.CreateVertexLayout(ArenaLayout((MeshArena.Fmt)f));
            if (s.ArenaLayout[f].IsNull) { Fail($"the Vulkan device refused GUI arena layout {(MeshArena.Fmt)f}", log); return false; }
        }
        s.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        s.Nearest = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Clamp));
        s.StandIn = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 1, 1, 1, 1, 1, 1,
                                                 ImageUse.Sampled | ImageUse.CopyDst, "vk gui stand-in"));
        s.Loose = vk.CreateBuffer(new BufferDesc(LooseCap, BufferUse.Uniform, MemoryKind.Device, "vk gui loose arena"));
        if (s.Linear.IsNull || s.Nearest.IsNull || s.StandIn.IsNull || s.Loose.IsNull)
        {
            Fail("the Vulkan GUI pass's constant resources were refused (logged above)", log);
            return false;
        }
        Span<byte> texel = stackalloc byte[4];
        texel.Clear();
        vk.Upload(s.StandIn, 0, 0, texel);
        s.Cmds = new CommandList("vulkan gui", 1 << 18, log);
        s.Side = new CommandList("vulkan gui draws", 1 << 19, log);
        log.Info($"GuiPassVk: programs built -- the GUI is Vulkan's from this flush ({MeshArena.Formats} arena layouts)");
        return true;
    }

    /// <summary>A GUI stage's text as GL compiles it: the library overrides, then the vanilla splice and the sizing
    /// tokens -- the same two steps <see cref="GlProgram.Graphics"/> and <see cref="VkPrograms"/> take.</summary>
    private static string Text(string resref, ExtenderLog log)
    {
        string what = $"GuiPassVk {resref}";
        return ShaderResource.Tokens(VanillaShader.Splice(GlProgram.Lib(ShaderSource.Get(resref), log, what), log, what, true, true));
    }

    /// <summary>Nothing draws the game's panels after this -- the scene database has no engine traversal underneath --
    /// so it is a hard failure naming the way out, as GL's is.</summary>
    private static void Fail(string why, ExtenderLog log)
    {
        _s.Failed = true;
        log.Always($"GuiPassVk: {why} -- the game's GUI panels are NOT DRAWN for the rest of the session");
        AlloyGate.Fail($"the interface could not be drawn on Vulkan ({why})");
    }

    /// <summary>The mesh arena's layout for the GUI path, which also feeds the one location a content stage may
    /// declare that no Alloy stage does -- the engine's <c>vCustomColor</c> -- from the vertex-paint word, because
    /// Vulkan has no attribute for an input nothing feeds. <see cref="GuiPass.LocColor"/> is the lightmap chart's
    /// location here and stays it, as GL's own arena vertex array leaves it.</summary>
    private static VertexLayoutDesc ArenaLayout(MeshArena.Fmt f)
    {
        VertexLayoutDesc d = MeshArena.VkLayout(f);
        var attribs = new List<VertexAttrib>(d.Attribs);
        VertexAttrib paint = attribs.Find(a => a.Location == AlloyRenderer.LocPaint);
        attribs.Add(paint with { Location = GuiContent.UnusedLoc });
        return d with { Attribs = attribs.ToArray(), Label = $"gui arena {f}" };
    }

    /// <summary>GL names and the Vulkan device both die with the context: the state goes whole.</summary>
    internal static void ContextLost() => _s = new GuiVkState();
}
