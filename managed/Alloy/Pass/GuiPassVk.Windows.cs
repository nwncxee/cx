using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;
using CX.Ui;

namespace CX.Alloy;

internal static unsafe partial class GuiPassVk
{
    /// <summary>One of CX's window programs on Vulkan: <see cref="TextRig"/>'s GL text for one draw kind, and the
    /// locations of the loose terms its replay sets per command.</summary>
    private sealed class WinProg
    {
        public GuiVkProg P = null!;
        public int Proj = -1, Alpha = -1, Offset = -1, Occl = -1, Anchor = -1, Crop = -1, Rows = -1, Vp = -1, DepthK = -1, View = -1, Scene = -1;
    }

    /// <summary>CX's window replay on the device: a program per kind (null until built, refused for the session when
    /// one did not), the UI vertex layout, and each layer's vertex buffer.</summary>
    private sealed class WinState
    {
        public readonly WinProg?[] Progs = new WinProg?[KindCount];
        public bool Tried, Ok;
        public VertexLayoutId Layout;
        public readonly BufferId[] Vtx = new BufferId[NowList + 1];
        public readonly long[] VtxCap = new long[NowList + 1];
    }

    private const int KindCount = (int)UiDrawKind.IconPlt + 1;

    /// <summary>The vertex buffer <see cref="ReplayNow"/>'s list takes, past the two layers'.</summary>
    private const int NowList = 2;

    /// <summary>A list drawn outside the GUI flush -- the loading screen's lines, the compile notice -- recorded over the
    /// composite and submitted at once, as GL's immediate replay draws it. False (said once) when the device is not on
    /// this thread or a flush is recording.</summary>
    internal static bool ReplayNow(UiDrawList l, ExtenderLog log)
    {
        if (AlloyDevice.Vulkan(log) is not VkBackend vk || !vk.OnOwnThread || _s.Busy || !Ensure(vk, log))
        {
            if (!_s.NowSaid)
            {
                _s.NowSaid = true;
                log.Always("GuiPassVk: an immediate UI draw (the loading screen's lines or the compile notice) came while the "
                         + "Vulkan GUI could not take it -- that draw is NOT SHOWN; further ones are not said");
            }
            return false;
        }
        GuiVkState s = _s;
        s.Into = default;
        s.ArenaUsed = 0;
        CommandList cl = s.Now ??= new CommandList("vulkan ui now", 1 << 14, log);
        cl.Reset();
        return Windows(vk, cl, l, NowList, log) && vk.Submit(cl);
    }

    /// <summary>The replay's blend, GL's <c>BlendFunc(SRC_ALPHA, ONE_MINUS_SRC_ALPHA)</c>, as a <see cref="Pipeline"/> key.</summary>
    private static readonly int WinBlend = 1 + (int)BlendFactor.SrcAlpha * 16 + (int)BlendFactor.OneMinusSrcAlpha;

    /// <summary>One command's loose terms, set as <see cref="TextRig"/>'s GL replay sets them.</summary>
    private readonly record struct WinFill(WinProg W, UiDrawList L, int Cmd, Matrix4x4 Ortho, bool Scene, Matrix4x4 Vp, Vector2 K) : IFill
    {
        public void Write()
        {
            ref UiCmd c = ref L.Cmds[Cmd];
            VkUniformShim.Set(W.Proj, Ortho);
            VkUniformShim.Set(W.Alpha, c.Alpha);
            VkUniformShim.Set(W.Offset, [c.OffX, c.OffY]);
            VkUniformShim.Set(W.Occl, [c.Wx, c.Wy, c.Wz, c.Occl ? c.Hidden : TextRig.NoSceneTest]);
            VkUniformShim.Set(W.Anchor, [c.Ax, c.Ay]);
            VkUniformShim.Set(W.Scene, Scene ? 1 : 0);
            if (c.Kind == UiDrawKind.IconPlt)
            {
                VkUniformShim.Set(W.Crop, [c.U1, c.V0]);
                VkUniformShim.SetEach(W.Rows, L.Rows.AsSpan(c.RowsAt, TextRig.PltRows));
            }
            if (!Scene) return;
            VkUniformShim.Set(W.Vp, Vp);
            VkUniformShim.Set(W.DepthK, [K.X, K.Y]);
            VkUniformShim.Set(W.View, [(float)L.ViewW, L.ViewH]);
        }
    }

    /// <summary>CX's windows of <paramref name="layer"/> over the whole composite, in a pass of their own after the
    /// layers before them: display space, depth off, one draw per recorded command with its scissor. False = the
    /// flush refuses (the arena is full); a layer with nothing recorded, or programs that did not build, draws nothing.</summary>
    private static bool Windows(VkBackend vk, CommandList cl, UiLayer layer, ExtenderLog log)
        => Windows(vk, cl, UiHostService.Windows(layer), (int)layer, log);

    /// <summary>List <paramref name="l"/> over the composite through vertex buffer <paramref name="li"/>.</summary>
    private static bool Windows(VkBackend vk, CommandList cl, UiDrawList l, int li, ExtenderLog log)
    {
        GuiVkState s = _s;
        ImageId screen = s.Into.IsNull ? vk.Composite : s.Into;
        if (l.CmdN == 0 || l.ViewW <= 0 || l.ViewH <= 0 || screen.IsNull || !WinEnsure(vk, s.Win, log)) return true;
        if (!Grow(vk, ref s.Win.Vtx[li], ref s.Win.VtxCap[li], Math.Max((long)l.VertN * sizeof(float), 64), BufferUse.Vertex,
                  $"vk ui windows {li}", log)) return false;
        cl.UpdateBuffer(s.Win.Vtx[li], 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(l.Verts.AsSpan(0, l.VertN)));
        ImageId atlas = TextRig.VkAtlas(vk, out SamplerId atlasSampler);
        bool scene = UiScene.GetVk(out Matrix4x4 vp, out Vector2 k, out ImageId depth);
        Matrix4x4 ortho = TextRig.Ortho(l.ViewW, l.ViewH);
        var pass = new Pass();
        var b = new Bound();
        Open(cl, ref pass, ref b, screen, s.Depth, PresentTarget.Width, PresentTarget.Height, false, false);
        CommandList side = s.Side!;
        side.SetViewport(0, 0, l.ViewW, l.ViewH);
        side.SetDepthRange(0f, 1f);
        side.SetDepthState(default);
        side.SetRasterState(default);
        side.BindVertexBuffer(s.Win.Vtx[li]);
        Span<BufferBinding> ubo = stackalloc BufferBinding[1];
        Span<ImageBinding> img = stackalloc ImageBinding[4];
        for (int i = 0; i < l.CmdN; i++)
        {
            ref UiCmd c = ref l.Cmds[i];
            WinProg w = s.Win.Progs[(int)c.Kind]!;
            PipelineId pipe = Pipeline(w.P, s.Win.Layout, WinBlend, Topology.Triangles, log);
            if (pipe.IsNull) { s.Skipped++; continue; }
            int loose = Block(w.P, new WinFill(w, l, i, ortho, scene, vp, k), log);
            if (loose < 0) return false;
            if (pipe != b.Pipe) { side.SetPipeline(pipe); b.Pipe = pipe; }
            if (c.Clip) side.SetScissor(c.Cx, l.ViewH - (c.Cy + c.Ch), Math.Max(c.Cw, 0), Math.Max(c.Ch, 0));
            else side.SetScissor(0, 0, l.ViewW, l.ViewH);
            int nt = 0;
            ubo[0] = new BufferBinding(UboSlots.Loose, s.Loose, loose, w.P.Block.Length);
            foreach (VkOpaque o in w.P.Opaques)
            {
                if (o.Set != 0 || o.Kind != VkBindKind.Sampled || nt == img.Length) continue;
                ImageBinding t = o.Name switch
                {
                    "uTex" when c.Kind == UiDrawKind.Text => atlas.IsNull ? Texture(s, 0) : new ImageBinding(0, atlas, atlasSampler),
                    "uTex" => c.Sampler ? Texture(s, (int)c.Tex) with { Sampler = s.Linear } : Texture(s, (int)c.Tex),
                    "uPal" when c.Pal == Db.PltPalettes.Texture && !s.Palette.IsNull => new ImageBinding(0, s.Palette, s.Nearest),
                    "uPal" => Texture(s, (int)c.Pal),
                    "uDepth" when scene => new ImageBinding(0, depth, s.Nearest),
                    _ => new ImageBinding(0, s.StandIn, s.Nearest),
                };
                img[nt++] = t with { Slot = o.Binding };
            }
            side.BindSet(0, new BindSet(ubo, default, img[..nt], default));
            side.Draw(c.Count, 1, c.First, 0);
            s.Drawn++;
        }
        Close(cl, ref pass);
        return true;
    }

    /// <summary>The window programs and the UI vertex layout (pos2, uv2, rgb3, alpha1), made on the first replay. A
    /// refusal is said once and CX's windows are NOT DRAWN on Vulkan for the session.</summary>
    private static bool WinEnsure(VkBackend vk, WinState w, ExtenderLog log)
    {
        if (w.Tried) return w.Ok;
        w.Tried = true;
        int stride = VertexBatch.Floats * sizeof(float);
        w.Layout = vk.CreateVertexLayout(new VertexLayoutDesc(
        [
            new(0, 2, VertexType.Float, 0), new(1, 2, VertexType.Float, 2 * sizeof(float)),
            new(2, 3, VertexType.Float, 4 * sizeof(float)), new(3, 1, VertexType.Float, 7 * sizeof(float)),
        ], stride, "ui windows"));
        var units = new System.Collections.Generic.Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["uTex"] = 0, ["uPal"] = 1, ["uDepth"] = TextureSlots.HiliteDepth,
        };
        string vs = Vk460(TextRig.VertexText);
        for (int k = 0; k < KindCount && !w.Layout.IsNull; k++)
        {
            var wp = new WinProg();
            wp.P = GuiVkProg.Build(vk, vs, Vk460(TextRig.FragmentText((UiDrawKind)k)), units, $"CX window {(UiDrawKind)k}", p =>
            {
                wp.Proj = p.Loc("uProj"); wp.Alpha = p.Loc("uAlpha"); wp.Offset = p.Loc("uOffset"); wp.Occl = p.Loc("uOccl");
                wp.Anchor = p.Loc("uAnchor"); wp.Crop = p.Loc("uCrop"); wp.Rows = p.Loc("uRows"); wp.Vp = p.Loc("uVp");
                wp.DepthK = p.Loc("uDepthK"); wp.View = p.Loc("uView"); wp.Scene = p.Loc("uScene");
            }, log)!;
            if (wp.P == null) break;
            w.Progs[k] = wp;
        }
        w.Ok = !w.Layout.IsNull && Array.TrueForAll(w.Progs, p => p != null);
        log.Always(w.Ok ? $"GuiPassVk: CX's windows draw on Vulkan ({KindCount} programs)"
                        : "GuiPassVk: a CX window program or the UI vertex layout was REFUSED (logged above) -- CX's own windows "
                          + "(chat, panels, nameplates) are NOT DRAWN on Vulkan for the session");
        return w.Ok;
    }

    /// <summary>The replay's GL 3.3 text at the version the Vulkan translation targets.</summary>
    private static string Vk460(string glsl) => glsl.Replace("#version 330 core", "#version 460 core", StringComparison.Ordinal);
}
