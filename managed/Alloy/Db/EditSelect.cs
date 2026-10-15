using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Seam;
using CX.Ui;

namespace CX.Alloy.Db;

/// <summary>Wires <see cref="EditSelect"/>'s hooks: the selection lives on CX's own text document and the
/// highlight is drawn by <see cref="TextQuads"/>.</summary>
public sealed class EditSelectService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks) => EditSelect.Install(log, hooks);
}

/// <summary>Modern editing in the engine's own edit boxes (<c>CGuiEdit</c> and subclasses), <c>ui.textedit</c>: selection, Ctrl+C/X, typing over
/// it, word-wise Ctrl+arrow/Backspace/Delete. The selection is CX state on the edit's <see cref="TextDoc"/>, the caret the engine's; text is only
/// ever REMOVED, through the focused object's key handler one backspace at a time, so a subclass's length cap and formatting still run.
/// ponytail: no undo (vanilla has none either); add an EditBuffer-style snapshot ring if players ask.</summary>
internal static unsafe class EditSelect
{
    /// <summary><c>CGuiEdit::HandleKeyPress</c> @0x592c60's backspace: caret left one, then delete at the caret.</summary>
    private const int EngineBackspace = 0x87;  // not-an-offset: engine key code

    private const int KmodShift = 0x0003, KmodCtrl = 0x00C0;  // not-an-offset: SDL key/event code
    private const uint KeyReturn = 0x0D, KeyEsc = 0x1B, KeyBackspace = 0x08, KeyDelete = 0x7F, KeyTab = 0x09;  // not-an-offset: SDL key/event code
    private const uint KeyA = 0x61, KeyC = 0x63, KeyV = 0x76, KeyX = 0x78;  // not-an-offset: SDL key/event code
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;  // not-an-offset: SDL key/event code
    private const uint KeyHome = 0x4000004A, KeyEnd = 0x4000004D;  // not-an-offset: SDL key/event code

    private static nint _guiMan;
    private static GuiEditHandleLMouseDownFn* _down;
    private static GuiEditSetFocusFn* _focus;
    private static GuiManHandleMouseMoveFn* _move;
    /// <summary>The edit box last clicked or focused, and its vtable then: live exactly while it still holds the
    /// keyboard (<c>CGuiMan+0x50</c>) under the same vtable -- a freed edit releases the keyboard in its destructor.</summary>
    private static nint _edit, _vtbl;
    private static bool _dragging;
    private static byte[] _joined = new byte[256];

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!Renderer.On) { log.Info("EditSelect: game text boxes keep vanilla editing -- ui.textedit needs the Alloy renderer"); return; }
        _guiMan = Native.Sym(SymbolMap.GuiMan);
        _down = hooks.ChainGuiEditHandleLMouseDown(&OnDown, 0);
        _focus = hooks.ChainGuiEditSetFocus(&OnFocus, 0);
        _move = hooks.ChainGuiManHandleMouseMove(&OnMove, 0);
        bool ok = _guiMan != 0 && _down != null && _focus != null && _move != null && EngineFn.GuiManHandleKeyPress != null;
        log.Info(ok ? "EditSelect: game text boxes take selection, clipboard and word editing (ui.textedit)"
                    : "EditSelect: a hook or symbol did not resolve -- game text boxes keep vanilla editing");
        if (!ok) _guiMan = 0;
    }

    private static nint GuiMan() => _guiMan == 0 ? 0 : *(nint*)_guiMan;
    private static int Mods() => EngineFn.SdlGetModState != null ? EngineFn.SdlGetModState() : 0;

    /// <summary>The focused, writable edit box and its document, or false.</summary>
    private static bool Active(out DbId id, out TextDoc d)
    {
        id = default;
        d = null!;
        nint gm = GuiMan();
        if (gm == 0 || _edit == 0 || !UiKnobs.TextEdit.V) return false;
        if (new CGuiMan(gm).KeyboardFocus != _edit || *(nint*)_edit != _vtbl) return false;
        var edit = new CGuiEdit(_edit);
        if (edit.ReadOnly != 0) return false;
        id = Facade.StringId(edit.String.Ptr);
        TextDoc? doc = id.IsNull ? null : TextObjects.Of(id);
        if (doc == null) return false;
        d = doc;
        return true;
    }

    // ---- the engine's hands ----

    /// <summary>Chained <c>CGuiEdit::HandleLMouseDown</c>: the engine places the caret at the hit, then the click
    /// becomes a word (double-click), an extension (shift), or the start of a drag.</summary>
    [UnmanagedCallersOnly]
    private static void OnDown(nint self)
    {
        int oldCaret = -1, oldAnchor = -1;
        if (self == _edit && Active(out _, out TextDoc was)) { oldCaret = CaretAt(was); oldAnchor = was.SelAnchor; }
        if (_down != null && *_down != null) (*_down)(self);
        _edit = self;
        _vtbl = *(nint*)self;
        _dragging = false;
        if (!Active(out DbId id, out TextDoc d)) return;
        int caret = CaretAt(d);
        if (new CGuiMan(GuiMan()).DoubleClick != 0)
        {
            var (a, b) = WordAt(Joined(d), caret);
            SetCaret(id, d, b);
            Anchor(id, d, a);
            return;
        }
        if (oldCaret >= 0 && (Mods() & KmodShift) != 0) { Anchor(id, d, oldAnchor >= 0 ? oldAnchor : oldCaret); return; }
        Anchor(id, d, caret);
        _dragging = true;
    }

    /// <summary>Chained <c>CGuiEdit::SetFocus</c>: Tab or a panel moved the keyboard here; any old selection goes.</summary>
    [UnmanagedCallersOnly]
    private static void OnFocus(nint self)
    {
        if (_focus != null && *_focus != null) (*_focus)(self);
        _edit = self;
        _vtbl = *(nint*)self;
        _dragging = false;
        if (Active(out DbId id, out TextDoc d)) Anchor(id, d, -1);
    }

    /// <summary>Chained <c>CGuiMan::HandleMouseMove</c>: while a drag is held the caret follows the engine's own hit
    /// point, through the same <c>MoveCaretTo</c> a click uses. Over no GUI object at all the caret stays put.</summary>
    [UnmanagedCallersOnly]
    private static void OnMove(nint gm, int x, int y)
    {
        if (_move != null && *_move != null) (*_move)(gm, x, y);
        if (!_dragging) return;
        if (!Active(out DbId id, out TextDoc d)) { _dragging = false; return; }
        var g = new CGuiMan(gm);
        if (g.HitObject == 0) return;
        Span<float> p = g.HitPoint;
        TextSlots.MoveCaretTo(id, new Vector3(p[0], p[1], p[2]));
    }

    /// <summary>The button came up: the drag is over. From the SDL pump.</summary>
    internal static void MouseUp() => _dragging = false;

    /// <summary>A key the CX windows did not take, before the engine sees it. True when it was consumed here.</summary>
    internal static bool OnKey(uint key, int mods)
    {
        if (!Active(out DbId id, out TextDoc d)) return false;
        bool shift = (mods & KmodShift) != 0, ctrl = (mods & KmodCtrl) != 0;
        int caret = CaretAt(d);
        int anchor = Math.Min(d.SelAnchor, Total(d));
        bool has = anchor >= 0 && anchor != caret;
        int from = Math.Min(anchor, caret), to = Math.Max(anchor, caret);
        if (ctrl)
        {
            switch (key)
            {
                case KeyA: SetCaret(id, d, Total(d)); Anchor(id, d, 0); return true;
                case KeyC: if (has) Copy(d, from, to); return true;
                case KeyX: if (has) { Copy(d, from, to); Delete(id, d, from, to); } return true;
                case KeyV: if (has) Delete(id, d, from, to); return false;   // the engine pastes
                case KeyBackspace: if (has) Delete(id, d, from, to); else Delete(id, d, WordLeft(Joined(d), caret), caret); return true;
                case KeyDelete: if (has) Delete(id, d, from, to); else Delete(id, d, caret, WordRight(Joined(d), caret)); return true;
                case KeyLeft:
                case KeyRight:
                    int keep = shift ? (anchor >= 0 ? anchor : caret) : -1;
                    SetCaret(id, d, key == KeyLeft ? WordLeft(Joined(d), caret) : WordRight(Joined(d), caret));
                    Anchor(id, d, keep);
                    return true;
            }
        }
        switch (key)
        {
            case KeyBackspace:
            case KeyDelete:
                if (!has) return false;
                Delete(id, d, from, to);
                return true;
            case KeyLeft: case KeyRight: case KeyHome: case KeyEnd: case KeyUp: case KeyDown:
                if (shift) { if (anchor < 0) Anchor(id, d, caret); return false; }   // the engine moves the caret
                Anchor(id, d, -1);
                if (!has || key is not (KeyLeft or KeyRight)) return false;
                SetCaret(id, d, key == KeyLeft ? from : to);
                return true;
            case KeyReturn: case KeyTab: case KeyEsc:
                Anchor(id, d, -1);
                return false;
        }
        return false;
    }

    /// <summary>Finished text is about to reach the engine: it replaces the selection, so the selection goes first.</summary>
    internal static void BeforeText()
    {
        if (!Active(out DbId id, out TextDoc d)) return;
        int caret = CaretAt(d), anchor = Math.Min(d.SelAnchor, Total(d));
        if (anchor >= 0 && anchor != caret) Delete(id, d, Math.Min(anchor, caret), Math.Max(anchor, caret));
        else if (anchor >= 0) Anchor(id, d, -1);
    }

    private static void Anchor(DbId id, TextDoc d, int at)
    {
        d.SelAnchor = at;
        TextObjects.MarkText(id, d);
    }

    /// <summary>Remove <c>[from, to)</c> as that many backspaces from <c>to</c>, through the focused object's handler.</summary>
    private static void Delete(DbId id, TextDoc d, int from, int to)
    {
        d.SelAnchor = -1;
        if (to <= from) { TextObjects.MarkText(id, d); return; }
        SetCaret(id, d, to);
        nint gm = GuiMan();
        for (int i = from; i < to; i++) EngineFn.GuiManHandleKeyPress(gm, EngineBackspace);
        TextObjects.MarkText(id, d);
    }

    private static void SetCaret(DbId id, TextDoc d, int pos)
    {
        var (line, col) = Locate(d, pos);
        TextSlots.SetCaretLinePos(id, line);
        if (line < d.LineCount) TextSlots.SetCaretColumnPos(id, col);
    }

    /// <summary>The selection's bytes onto the clipboard as UTF-8, each codepage byte through the engine's own
    /// mapping (locale and a module's <c>encoding.2da</c>).</summary>
    private static void Copy(TextDoc d, int from, int to)
    {
        if (EngineFn.SdlSetClipboardText == null) return;
        ReadOnlySpan<byte> all = Joined(d);
        var sb = new StringBuilder(to - from);
        for (int i = from; i < to && i < all.Length; i++)
        {
            int cp = EngineFn.EncodingSingleCharToUtf8 == null ? all[i] : EngineFn.EncodingSingleCharToUtf8(all[i], 0);
            sb.Append(cp is > 0 and < 0x110000 and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(cp) : "?");
        }
        byte[] u = Encoding.UTF8.GetBytes(sb.Append('\0').ToString());
        fixed (byte* p = u) EngineFn.SdlSetClipboardText(p);
    }

    // ---- positions: a byte offset into the joined lines (CGuiEdit::GetText's concatenation) ----

    /// <summary>The caret as an offset into the joined lines.</summary>
    internal static int CaretAt(TextDoc d)
    {
        int at = 0, line = Math.Min(d.Caret.Line, d.LineCount);
        for (int i = 0; i < line; i++) at += d.Lines[i].Length;
        return line < d.LineCount ? at + Math.Clamp(d.Caret.Column, 0, d.Lines[line].Length) : at;
    }

    internal static int Total(TextDoc d)
    {
        int n = 0;
        for (int i = 0; i < d.LineCount; i++) n += d.Lines[i].Length;
        return n;
    }

    /// <summary>Where offset <paramref name="pos"/> sits: a wrapped line's end is the next line's start, and the end
    /// after a final newline is the line one past the last, as the engine's own caret has it.</summary>
    internal static (int Line, int Col) Locate(TextDoc d, int pos)
    {
        int at = 0;
        for (int i = 0; i < d.LineCount; i++)
        {
            int len = d.Lines[i].Length;
            if (pos < at + len) return (i, Math.Max(pos - at, 0));
            at += len;
        }
        if (d.LineCount == 0) return (0, 0);
        TextLine last = d.Lines[d.LineCount - 1];
        return last.Length > 0 && last.Bytes[last.Length - 1] == (byte)'\n' ? (d.LineCount, 0) : (d.LineCount - 1, last.Length);
    }

    /// <summary>The document's lines joined, in a scratch buffer valid until the next call.</summary>
    internal static ReadOnlySpan<byte> Joined(TextDoc d)
    {
        int n = Total(d);
        if (_joined.Length < n) _joined = new byte[Math.Max(n, _joined.Length * 2)];
        int at = 0;
        for (int i = 0; i < d.LineCount; i++) { d.Lines[i].Span.CopyTo(_joined.AsSpan(at)); at += d.Lines[i].Length; }
        return _joined.AsSpan(0, n);
    }

    private static bool Space(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    /// <summary>Whitespace-delimited words, as the CX chat line has them.</summary>
    internal static int WordLeft(ReadOnlySpan<byte> s, int p)
    {
        while (p > 0 && Space(s[p - 1])) p--;
        while (p > 0 && !Space(s[p - 1])) p--;
        return p;
    }

    internal static int WordRight(ReadOnlySpan<byte> s, int p)
    {
        while (p < s.Length && Space(s[p])) p++;
        while (p < s.Length && !Space(s[p])) p++;
        return p;
    }

    /// <summary>The word, or the whitespace run, under offset <paramref name="p"/> (a double-click).</summary>
    internal static (int From, int To) WordAt(ReadOnlySpan<byte> s, int p)
    {
        if (s.Length == 0) return (0, 0);
        int i = Math.Clamp(p, 0, s.Length - 1);
        bool ws = Space(s[i]);
        int a = i, b = i + 1;
        while (a > 0 && Space(s[a - 1]) == ws) a--;
        while (b < s.Length && Space(s[b]) == ws) b++;
        return (a, b);
    }
}
