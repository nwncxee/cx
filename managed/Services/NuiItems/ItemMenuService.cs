using System;
using CX.Framework;
using CX.Services.Nui;
using CX.Services.Radial;
using CX.Ui;

namespace CX.Services.NuiItems;

/// <summary>
/// The extender's item menu: the rows a server declares (<see cref="ItemMenuPolicy"/>) drawn as the radial
/// replacement's <see cref="CascadeMenu"/>, in its look and above every NUI window, at the point the server names. A pick is sent back as
/// <c>menu;&lt;row id&gt;</c>, and a close -- Escape, or a click off the menu -- as <c>menu;</c> with no row.
/// </summary>
internal static class ItemMenuService
{
    /// <summary>The <see cref="ServerPolicy.EventMinor"/> action a pick and a close are sent as.</summary>
    internal const string MenuAction = "menu";

    /// <summary>The menu on screen, the window it draws in, its batch and style, and the generation it was built from.</summary>
    internal sealed class MenuState
    {
        public readonly CascadeMenu Menu = new() { DragEnabled = false };
        public Window? Window;
        public VertexBatch? Batch;
        public CascadeStyle Style;
        /// <summary>The radial menu's art for the native look.</summary>
        public readonly Skin Skin = NativeChrome.NewSkin();
        /// <summary>The face the menu was laid out in; a face that bakes later lays it out again where it stands.</summary>
        public GlyphAtlas? Atlas;
        public ExtenderLog? Log;
        public int ShownGen = -1, OpenGen = -1, OpenX, OpenY;
        /// <summary>The pointer's last pixel, where a menu the server opened at the pointer appears.</summary>
        public int PointerX, PointerY;
    }

    /// <summary>Every pointer move, from the input pump: a server that knows only the element pressed opens the menu
    /// here.</summary>
    internal static void OnPointer(int x, int y)
    {
        _s.PointerX = x;
        _s.PointerY = y;
    }

    private static readonly MenuState _s = new();
    private static CascadeMenu Menu => _s.Menu;
    private static ExtenderLog? _log => _s.Log;

    /// <summary>True while the player allows the extender's NUI work; off, the server's own NUI menu is what shows.</summary>
    private static bool On => NuiKnobs.Enabled.V;

    public static void Init(ExtenderLog log)
    {
        _s.Log = log;
        ItemMenuPolicy.Init();
        _s.Window = new Window(0, 0, 0, 0)
        {
            Layer = UiLayer.AboveNui,
            Focusable = false,
            CapturesPointer = true,
            DrawContent = Draw,
            OnMouseButton = OnMouseButton,
            OnMouseMotion = OnMouseMotion,
            OnMouseWheel = OnMouseWheel,
            OnKey = OnKey,
            HitTestOverride = (px, py) => Menu.Open && Menu.Contains(px, py),
        };
        Capabilities.Register("menu", 1, static () => On);
        NuiKnobs.Enabled.Changed += Capabilities.Refresh;
    }

    /// <summary>Per frame: open, re-open or close the cascade to match what the server declared, then draw it in the radial
    /// menu's font, opacity, animation and, under the native theme, its art.</summary>
    private static void Draw()
    {
        ItemMenuModel m = ItemMenuPolicy.Menu;
        if (RadialKnobs.Font.Face(TextRig.Atlas) is not GlyphAtlas atlas) return;
        if (m.Gen != _s.ShownGen || !ReferenceEquals(atlas, _s.Atlas))
        {
            _s.ShownGen = m.Gen;
            Show(m, atlas, TextRig.ViewportW, TextRig.ViewportH);
        }
        if (!Menu.Open) return;
        if (!On) { Dismiss(); return; }
        VertexBatch b = _s.Batch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        b.Reset(atlas.SolidU, atlas.SolidV);
        b.Outline = RadialKnobs.Font.HaloPx;
        _s.Style = Look(Environment.TickCount64);
        Skin? skin = DrawsArt && _s.Skin.Arm(_log, "") ? _s.Skin : null;
        skin?.Begin();
        Menu.Draw(b, atlas, atlas.Ascent, in _s.Style, skin);
        skin?.Flush();
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
        foreach (IconQuad q in Menu.Icons)
            if (q.Resref.Length != 0) TextRig.DrawIcon(q.Resref, q.X, q.Y, q.Size, q.Size, q.Alpha);
    }

    /// <summary>The menu's colours, opacity and animation at <paramref name="nowMs"/>.</summary>
    internal static CascadeStyle Look(long nowMs)
    {
        CascadeStyle look = CascadeStyle.Current;
        look.Alpha = RadialKnobs.Opacity.V;
        look.AnimMs = RadialKnobs.Anim.V ? RadialKnobs.AnimMs.V : 0;
        look.NowMs = nowMs;
        return look;
    }

    /// <summary>True when the menu draws the game's frame art.</summary>
    internal static bool DrawsArt => Theme.IsNative;

    /// <summary>True while the menu is on screen.</summary>
    internal static bool IsOpen => Menu.Open;

    /// <summary>Show what the server declared: its rows at its point, or nothing at all. A menu shown again in a new face keeps
    /// the point it first opened at, even when that was the pointer and the pointer has moved since.</summary>
    internal static void Show(ItemMenuModel m, GlyphAtlas atlas, int viewportW, int viewportH)
    {
        _s.Atlas = atlas;
        if (!m.Open || m.RowCount == 0) { Close(); return; }
        ItemMenuRow[] rows = m.Rows();
        var items = new CascadeItem[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            items[i] = new CascadeItem(rows[i].Label, rows[i].Icon, disabled: !rows[i].Enabled, tag: rows[i].Id);
        if (m.Gen != _s.OpenGen)
        {
            _s.OpenGen = m.Gen;
            (_s.OpenX, _s.OpenY) = m.At(_s.PointerX, _s.PointerY);
        }
        Menu.OpenAt(items, _s.OpenX, _s.OpenY, atlas, viewportW, viewportH);
        Modal(true);
        _log?.Info($"[itemmenu] open at {_s.OpenX},{_s.OpenY} with {rows.Length} row(s)");
    }

    /// <summary>The menu goes; the window stops taking every press and key.</summary>
    private static void Close()
    {
        Menu.Close();
        _s.OpenGen = -1;
        Modal(false);
    }

    /// <summary>While the menu is open its window takes every press and key ahead of NUI and the game, so a click away and
    /// Escape close it rather than reach the window under it.</summary>
    private static void Modal(bool open)
    {
        if (_s.Window != null) _s.Window.SuppressChords = open;
    }

    private static bool OnMouseButton(bool down, int button, int x, int y)
    {
        if (!Menu.Open) return false;
        if (down)
        {
            if (!Menu.Contains(x, y)) { Dismiss(); return true; }
            Act(Menu.MouseDown(x, y, button));
            return true;
        }
        Act(Menu.MouseUp());
        return true;
    }

    private static bool OnMouseMotion(int x, int y)
    {
        if (!Menu.Open) return false;
        Menu.MouseMove(x, y);
        return true;
    }

    private static bool OnMouseWheel(int dy)
    {
        if (!Menu.Open) return false;
        Menu.MouseWheel(dy);
        return true;
    }

    /// <summary>Every key is eaten while the menu is open, so Escape closes it instead of reaching the engine's own menu.</summary>
    private static bool OnKey(uint sdlKey, int mods)
    {
        if (!Menu.Open) return false;
        CascadeAction a = Menu.Key(sdlKey);
        if (a.Kind == CascadeActionKind.CloseAll) { Dismiss(); return true; }
        Act(a);
        return true;
    }

    private static void Act(CascadeAction a)
    {
        switch (a.Kind)
        {
            case CascadeActionKind.Activate:
                string id = Menu.Level(a.Level).Items[a.Row].Tag as string ?? "";
                Close();
                ItemMenuPolicy.Menu.Close();
                _s.ShownGen = ItemMenuPolicy.Menu.Gen;
                if (id.Length != 0) ServerPolicy.Emit(MenuAction, id);
                _log?.Info($"[itemmenu] picked {id}");
                break;
            case CascadeActionKind.CloseAll:
                Dismiss();
                break;
        }
    }

    /// <summary>Escape or a click away: the menu goes, the server is told it closed with no row picked, and nothing else moves.</summary>
    private static void Dismiss()
    {
        Close();
        ItemMenuPolicy.Menu.Close();
        _s.ShownGen = ItemMenuPolicy.Menu.Gen;
        ServerPolicy.Emit(MenuAction, "");
        _log?.Info("[itemmenu] closed with no pick");
    }
}
