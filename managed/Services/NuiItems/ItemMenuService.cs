using System;
using CX.Framework;
using CX.Services.Nui;
using CX.Ui;

namespace CX.Services.NuiItems;

/// <summary>
/// The extender's item menu: the rows a server declares (<see cref="ItemMenuPolicy"/>) drawn as the radial
/// replacement's <see cref="CascadeMenu"/> at the point the server names. A pick is sent back as
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
        public ExtenderLog? Log;
        public int ShownGen = -1;
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

    /// <summary>Per frame: open, re-open or close the cascade to match what the server declared, then draw it.</summary>
    private static void Draw()
    {
        ItemMenuModel m = ItemMenuPolicy.Menu;
        if (TextRig.Atlas is not GlyphAtlas atlas) return;
        if (m.Gen != _s.ShownGen)
        {
            _s.ShownGen = m.Gen;
            Sync(m, atlas);
        }
        if (!Menu.Open) return;
        if (!On) { Dismiss(); return; }
        VertexBatch b = _s.Batch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        b.Reset(atlas.SolidU, atlas.SolidV);
        _s.Style = CascadeStyle.Current;
        Menu.Draw(b, atlas, atlas.Ascent, in _s.Style);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
        foreach (IconQuad q in Menu.Icons)
            if (q.Resref.Length != 0) TextRig.DrawIcon(q.Resref, q.X, q.Y, q.Size, q.Size, q.Alpha);
    }

    /// <summary>Show what the server declared: its rows at its point, or nothing at all.</summary>
    private static void Sync(ItemMenuModel m, GlyphAtlas atlas)
    {
        if (!m.Open || m.RowCount == 0) { Menu.Close(); return; }
        ItemMenuRow[] rows = m.Rows();
        var items = new CascadeItem[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            items[i] = new CascadeItem(rows[i].Label, rows[i].Icon, disabled: !rows[i].Enabled, tag: rows[i].Id);
        Menu.OpenAt(items, m.X, m.Y, atlas, TextRig.ViewportW, TextRig.ViewportH);
        _log?.Info($"[itemmenu] open at {m.X},{m.Y} with {rows.Length} row(s)");
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
                Menu.Close();
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
        Menu.Close();
        ItemMenuPolicy.Menu.Close();
        _s.ShownGen = ItemMenuPolicy.Menu.Gen;
        ServerPolicy.Emit(MenuAction);
        _log?.Info("[itemmenu] closed with no pick");
    }
}
