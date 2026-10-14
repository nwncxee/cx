using System;
using System.Globalization;
using CX.Framework;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Inventory;

/// <summary>
/// The "how much gold" prompt the inventory's gold button opens, in place of the stock <c>CGuiItemStackSplitPanel</c>
/// (TLK 63448, max = the player's gold, OK starts the gold drag): an amount box, a slider with − / +, preset chips and
/// Cancel / Drop, typed with digits, Backspace/Delete, Up/Down ±1 (Shift ×10, Ctrl ×100), PageUp/PageDown ±10 %,
/// Home/End, Enter, Esc. GL- and engine-free: the service draws <see cref="Emit"/>'s output and routes input here.
/// </summary>
internal sealed class GoldPrompt
{
    private const float WDesign = 300f, HDesign = 184f, TitleH = 24f, Pad = 10f, Row = 26f, Btn = 22f, ChipGap = 4f,
                        ActionW = 84f, CloseW = 20f, IconSize = 32f;
    private const int ButtonLeft = 1;
    private const int KmodCtrl = 0x00C0, KmodShift = 0x0003;                                                // not-an-offset: SDL KMOD_CTRL / KMOD_SHIFT
    private const uint KeyEscape = 0x1B, KeyReturn = 0x0D, KeyBackspace = 0x08, KeyDelete = 0x7F;          // not-an-offset: SDL keycodes
    private const uint KeyUp = 0x40000052, KeyDown = 0x40000051, KeyPageUp = 0x4000004B, KeyPageDown = 0x4000004E;   // not-an-offset: SDL keycodes
    private const uint KeyHome = 0x4000004A, KeyEnd = 0x4000004D, KeyKpEnter = 0x40000058;                          // not-an-offset: SDL keycodes
    private const uint KeyKp1 = 0x40000059, KeyKp0 = 0x40000062;                                                    // not-an-offset: SDL keycodes
    /// <summary>The amount box holds at most this many digits (the wire's gold is an int).</summary>
    private const int MaxDigits = 9;
    /// <summary>The window art's border, drawn 1:1: what a part inside the frame insets by.</summary>
    private const float Inset = InventoryWindow.FrameInset;
    /// <summary>The slider's bar in design px: the thin rail its cursor floats over, as NUI's own slider has.</summary>
    private const float BarH = 6f;

    /// <summary>The chips: a fixed amount, or (negative) half / all of <see cref="Max"/>.</summary>
    private static readonly int[] ChipValue = { 1, 10, 100, 1000, -2, -1 };
    private static readonly string[] ChipText = { "1", "10", "100", "1000", "Half", "All" };

    public bool Open { get; private set; }
    public int Max { get; private set; }
    public int Amount { get; private set; }
    public float X, Y, S = 1f;
    public float W => WDesign * S;
    public float H => HDesign * S;
    /// <summary>Drop pressed with <see cref="Amount"/> ≥ 1.</summary>
    public Action<int>? Confirm;

    /// <summary>The next typed digit replaces the amount (the box opens "selected").</summary>
    private bool _replace;
    private bool _sliding, _moving;
    private float _moveDx, _moveDy, _mx = -1f, _my = -1f;
    private string _amountText = "", _maxText = "";
    private int _amountFor = -1, _maxFor = -1;

    /// <summary>Open over <paramref name="max"/> gold with the whole purse preselected (the stock panel's default),
    /// centred on (<paramref name="cx"/>, <paramref name="cy"/>) and kept on the <paramref name="vw"/>×<paramref name="vh"/>
    /// screen.</summary>
    public void Show(int max, float scale, float cx, float cy, float vw, float vh)
    {
        Max = Math.Max(0, max); Amount = Max; S = scale;
        Open = true; _replace = true; _sliding = _moving = false;
        X = cx - W / 2f; Y = cy - H / 2f;
        Clamp(vw, vh);
    }

    public void Hide() { Open = false; _sliding = _moving = false; }

    /// <summary>The purse moved under an open prompt (gold arrived or left): keep the amount inside it.</summary>
    public void SetMax(int max)
    {
        if (max == Max) return;
        Max = Math.Max(0, max);
        Amount = Math.Min(Amount, Max);
    }

    public void Clamp(float vw, float vh)
    {
        if (vw > 0) X = Window.ClampAxis(X, W, vw);
        if (vh > 0) Y = Window.ClampAxis(Y, H, vh);
    }

    public bool Contains(float px, float py) => Open && px >= X && py >= Y && px < X + W && py < Y + H;

    private void Set(int v) { Amount = Math.Clamp(v, Max > 0 ? 1 : 0, Max); _replace = false; }

    // ---- geometry (design px from the prompt's top-left) ----

    private float Sx(float d) => X + d * S;
    private float Sy(float d) => Y + d * S;
    private bool In(float x0, float y0, float x1, float y1, float px, float py) => px >= Sx(x0) && px < Sx(x1) && py >= Sy(y0) && py < Sy(y1);

    private const float AmountY = TitleH + Pad, SliderY = AmountY + Row + 12f, ChipY = SliderY + Btn + 12f, ActionY = ChipY + Row + 14f;
    private const float BoxX0 = Pad + IconSize + 8f, BoxX1 = WDesign - Pad;
    private const float MinusX0 = Pad, MinusX1 = Pad + Btn, PlusX0 = WDesign - Pad - Btn, PlusX1 = WDesign - Pad;
    private const float TrackX0 = MinusX1 + 10f, TrackX1 = PlusX0 - 10f;
    private const float DropX1 = WDesign - Pad, DropX0 = DropX1 - ActionW, CancelX1 = DropX0 - 8f, CancelX0 = CancelX1 - ActionW;
    private static float ChipW => (WDesign - 2f * Pad - (ChipValue.Length - 1) * ChipGap) / ChipValue.Length;
    private static float ChipX0(int i) => Pad + i * (ChipW + ChipGap);

    private int ChipAmount(int i) => ChipValue[i] switch { -1 => Max, -2 => Math.Max(1, Max / 2), var v => v };

    private float Frac => Max <= 1 ? 1f : (Amount - 1f) / (Max - 1f);

    private void SlideTo(float px)
    {
        float t = Math.Clamp((px - Sx(TrackX0)) / ((TrackX1 - TrackX0) * S), 0f, 1f);
        Set(Max <= 1 ? Max : 1 + (int)MathF.Round(t * (Max - 1)));
    }

    private static int Step(int mods) => (mods & KmodCtrl) != 0 ? 100 : (mods & KmodShift) != 0 ? 10 : 1;

    // ---- input ----

    /// <summary>A press or release. Modal: every press is consumed while open (outside the prompt it does nothing).</summary>
    public bool MouseButton(bool down, int button, float px, float py, int mods)
    {
        if (!Open) return false;
        _mx = px; _my = py;
        if (!down) { _sliding = _moving = false; return true; }
        if (button != ButtonLeft || !Contains(px, py)) return true;
        if (In(WDesign - Pad - CloseW, 2f, WDesign - Pad, 2f + CloseW, px, py)) { Hide(); return true; }
        if (In(0f, 0f, WDesign, TitleH, px, py)) { _moving = true; _moveDx = px - X; _moveDy = py - Y; return true; }
        if (In(MinusX0, SliderY, MinusX1, SliderY + Btn, px, py)) { Set(Amount - Step(mods)); return true; }
        if (In(PlusX0, SliderY, PlusX1, SliderY + Btn, px, py)) { Set(Amount + Step(mods)); return true; }
        if (In(TrackX0 - 6f, SliderY, TrackX1 + 6f, SliderY + Btn, px, py) && Max > 0) { _sliding = true; SlideTo(px); return true; }
        for (int i = 0; i < ChipValue.Length; i++)
            if (In(ChipX0(i), ChipY, ChipX0(i) + ChipW, ChipY + Row, px, py)) { if (ChipAmount(i) <= Max) Set(ChipAmount(i)); return true; }
        if (In(CancelX0, ActionY, CancelX1, ActionY + Row, px, py)) { Hide(); return true; }
        if (In(DropX0, ActionY, DropX1, ActionY + Row, px, py)) { Commit(); return true; }
        return true;
    }

    public bool MouseMove(float px, float py)
    {
        if (!Open) return false;
        _mx = px; _my = py;
        if (_moving) { X = px - _moveDx; Y = py - _moveDy; return true; }
        if (_sliding) { SlideTo(px); return true; }
        return Contains(px, py);
    }

    /// <summary>A wheel notch over the prompt: ±1 % of the purse (at least 1). SDL sign: + = up = more.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open) return false;
        if (dy != 0 && Contains(_mx, _my)) Set(Amount + Math.Sign(dy) * Math.Max(1, Max / 100));
        return true;
    }

    /// <summary>Keys (see the class summary). Consumes every key while open: the prompt is modal.</summary>
    public bool Key(uint key, int mods)
    {
        if (!Open) return false;
        int digit = key >= '0' && key <= '9' ? (int)(key - '0') : key >= KeyKp1 && key < KeyKp0 ? (int)(key - KeyKp1) + 1 : key == KeyKp0 ? 0 : -1;
        if (digit >= 0)
        {
            long v = _replace ? digit : Amount * 10L + digit;
            _replace = false;
            if (v.ToString(CultureInfo.InvariantCulture).Length <= MaxDigits) Amount = (int)Math.Min(v, Max);
            return true;
        }
        switch (key)
        {
            case KeyEscape: Hide(); break;
            case KeyReturn or KeyKpEnter: Commit(); break;
            case KeyBackspace: Amount = _replace ? 0 : Amount / 10; _replace = false; break;
            case KeyDelete: Amount = 0; _replace = false; break;
            case KeyUp: Set(Amount + Step(mods)); break;
            case KeyDown: Set(Amount - Step(mods)); break;
            case KeyPageUp: Set(Amount + Math.Max(1, Max / 10)); break;
            case KeyPageDown: Set(Amount - Math.Max(1, Max / 10)); break;
            case KeyHome: Set(1); break;
            case KeyEnd: Set(Max); break;
        }
        return true;
    }

    private void Commit()
    {
        if (Amount < 1) return;
        int n = Amount;
        Hide();
        Confirm?.Invoke(n);
    }

    // ---- draw ----

    /// <summary>Emit one frame into <paramref name="under"/> (and the skin's parts when <paramref name="skin"/> is armed).
    /// Allocates nothing once warm: the two numbers are re-formatted only when they change.</summary>
    public void Emit(VertexBatch under, Skin? skin, GlyphAtlas text, GlyphAtlas small)
    {
        if (!Open) return;
        float s = S, lh = text.LineHeight, slh = small.LineHeight;
        if (_amountFor != Amount) { _amountFor = Amount; _amountText = Amount.ToString("N0", CultureInfo.CurrentCulture); }
        if (_maxFor != Max) { _maxFor = Max; _maxText = "of " + Max.ToString("N0", CultureInfo.CurrentCulture) + " gp"; }

        float x0 = X, y0 = Y, x1 = X + W, y1 = Y + H;
        if (skin != null) { skin.Draw(NativeChrome.Frame, x0, y0, x1, y1); skin.Draw(NativeChrome.Header, x0, y0, x1, Sy(TitleH)); }
        else { Fill(under, x0, y0, x1, y1, PanelBg, 0.98f); Outline(under, x0, y0, x1, y1, Border); Fill(under, x0 + 1f, y0 + 1f, x1 - 1f, Sy(TitleH), HeaderBg); }
        UiText.Draw(under, text, "Drop gold", Sx(Pad), y0 + (TitleH * s - lh) / 2f + text.Ascent, Tx, Sx(WDesign - Pad - CloseW));
        bool closeHot = In(WDesign - Pad - CloseW, 2f, WDesign - Pad, 2f + CloseW, _mx, _my);
        if (skin != null) skin.Draw(NativeChrome.Close, Sx(WDesign - Pad - CloseW) + Inset, Sy(2f) + Inset, Sx(WDesign - Pad) - Inset, Sy(2f + CloseW) - Inset,
                                    InventoryWindow.ArtTint(closeHot));
        else
        {
            if (closeHot) Fill(under, Sx(WDesign - Pad - CloseW), Sy(2f), Sx(WDesign - Pad), Sy(2f + CloseW), Accent);
            UiText.Draw(under, text, "x", Sx(WDesign - Pad - CloseW / 2f) - UiText.Measure(text, "x") / 2f, Sy(2f) + (CloseW * s - lh) / 2f + text.Ascent, Hi, float.MaxValue);
        }

        // amount: the coin icon, the box (right-aligned number), "of N gp" under it
        if (skin != null) skin.Draw(InventoryWindow.KGoldCursor, Sx(Pad), Sy(AmountY - 3f), Sx(Pad + IconSize), Sy(AmountY - 3f + IconSize));
        float bx0 = Sx(BoxX0), by0 = Sy(AmountY), bx1 = Sx(BoxX1), by1 = Sy(AmountY + Row);
        if (skin != null) skin.Draw(NativeChrome.Edit, bx0, by0, bx1, by1);
        else { Fill(under, bx0, by0, bx1, by1, Input); Outline(under, bx0, by0, bx1, by1, Accent2); }
        float aw = UiText.Measure(text, _amountText);
        float ax = bx1 - 8f * s - aw, abase = by0 + (by1 - by0 - lh) / 2f + text.Ascent;
        if (_replace && Amount > 0) Fill(under, ax - 2f * s, abase - text.Ascent, bx1 - 6f * s, abase - text.Ascent + lh, Selected, 0.7f);
        UiText.Draw(under, text, _amountText, ax, abase, Warn, float.MaxValue);
        if (!_replace) Fill(under, bx1 - 7f * s, abase - text.Ascent, bx1 - 6f * s, abase - text.Ascent + lh, Hi);
        UiText.Draw(under, small, _maxText, bx0 + 6f * s, abase - text.Ascent + (lh - slh) / 2f + small.Ascent, Tx3, ax - 6f * s);

        // slider: − bar +, the cursor floating on it as NUI's own slider draws it
        StepButton(under, skin, text, MinusX0, "-");
        StepButton(under, skin, text, PlusX0, "+");
        float tx0 = Sx(TrackX0), tx1 = Sx(TrackX1), tcy = Sy(SliderY + Btn / 2f), th = BarH * s;
        Fill(under, tx0, tcy - th / 2f, tx1, tcy + th / 2f, Track);
        float kx = tx0 + (tx1 - tx0) * Frac;
        Fill(under, tx0, tcy - th / 2f + 1f, kx, tcy + th / 2f - 1f, Accent, 0.8f);
        bool knobHot = _sliding || In(TrackX0 - 6f, SliderY, TrackX1 + 6f, SliderY + Btn, _mx, _my);
        float kh = Btn * s / 2f, aspect = skin?.Aspect(InventoryWindow.KSlider) ?? 0f;
        float kw = aspect > 0f ? kh * aspect : 7f * s;
        if (skin != null) skin.Draw(InventoryWindow.KSlider, kx - kw, tcy - kh, kx + kw, tcy + kh, InventoryWindow.ArtTint(knobHot));
        else Fill(under, kx - kw, tcy - kh, kx + kw, tcy + kh, knobHot ? Accent2 : Accent);

        // chips
        for (int i = 0; i < ChipValue.Length; i++)
        {
            int v = ChipAmount(i);
            bool on = Max > 0 && v == Amount, off = v > Max || Max == 0;
            TextButton(under, skin, small, ChipX0(i), ChipY, ChipX0(i) + ChipW, ChipY + Row, ChipText[i], on, off);
        }

        // actions
        TextButton(under, skin, text, CancelX0, ActionY, CancelX1, ActionY + Row, "Cancel", false, false);
        TextButton(under, skin, text, DropX0, ActionY, DropX1, ActionY + Row, "Drop", true, Amount < 1);
    }

    private void StepButton(VertexBatch under, Skin? skin, GlyphAtlas text, float dx0, string label)
        => TextButton(under, skin, text, dx0, SliderY, dx0 + Btn, SliderY + Btn, label, false, Max == 0);

    private void TextButton(VertexBatch under, Skin? skin, GlyphAtlas face, float dx0, float dy0, float dx1, float dy1, string label, bool on, bool off)
    {
        float x0 = Sx(dx0), y0 = Sy(dy0), x1 = Sx(dx1), y1 = Sy(dy1);
        bool hot = !off && _mx >= x0 && _mx < x1 && _my >= y0 && _my < y1;
        if (skin != null) skin.Draw(InventoryWindow.ButtonFace(on, hot), x0, y0, x1, y1, off ? 0x80808080u : 0xffffffffu);   // not-an-offset: rgba tints
        else { Fill(under, x0, y0, x1, y1, on ? NavOn : hot ? Frame2 : CatBg, off ? 0.5f : 1f); Outline(under, x0, y0, x1, y1, on ? Accent2 : ColBorder); }
        float w = UiText.Measure(face, label);
        UiText.Draw(under, face, label, (x0 + x1 - w) / 2f, y0 + (y1 - y0 - face.LineHeight) / 2f + face.Ascent, off ? Tx3 : on ? Hi : Tx, float.MaxValue);
    }

    // ---- self-check ----

    /// <summary>Off-device: the preselected purse, typing (replace, append, digit cap, clamp), Backspace, the step keys and
    /// modifiers, the chips (half, all, a chip beyond the purse), the slider ends, Drop and Esc.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var p = new GoldPrompt();
        int dropped = -1;
        p.Confirm = n => dropped = n;
        p.Show(1234, 1f, 500f, 400f, 1920f, 1080f);
        check.Expect(p.Open && p.Amount == 1234 && MathF.Abs(p.X + p.W / 2f - 500f) < 0.01f, "opens on the whole purse, centred on the anchor");
        p.Key('5', 0); p.Key('0', 0);
        check.Expect(p.Amount == 50, "the first digit replaces the preselected amount, the next appends");
        p.Key('9', 0); p.Key('9', 0);
        check.Expect(p.Amount == 1234, "typing past the purse clamps to it");
        p.Key(KeyBackspace, 0);
        check.Expect(p.Amount == 123, "Backspace drops the last digit");
        p.Key(KeyUp, 0); p.Key(KeyUp, KmodShift); p.Key(KeyDown, KmodCtrl);
        check.Expect(p.Amount == 34, "Up +1, Shift+Up +10, Ctrl+Down -100");
        p.Key(KeyEnd, 0);
        check.Expect(p.Amount == 1234, "End = all");
        p.Key(KeyHome, 0); p.Key(KeyDown, 0);
        check.Expect(p.Amount == 1, "Home = 1, and nothing goes below 1");

        float Cx(float d) => p.X + d * p.S;
        float Cy(float d) => p.Y + d * p.S;
        p.MouseButton(true, ButtonLeft, Cx(ChipX0(4) + 2f), Cy(ChipY + 2f), 0); p.MouseButton(false, ButtonLeft, 0, 0, 0);
        check.Expect(p.Amount == 617, "the Half chip");
        p.MouseButton(true, ButtonLeft, Cx(ChipX0(3) + 2f), Cy(ChipY + 2f), 0); p.MouseButton(false, ButtonLeft, 0, 0, 0);
        check.Expect(p.Amount == 1000, "the 1000 chip");
        p.MouseButton(true, ButtonLeft, Cx(TrackX1 + 3f), Cy(SliderY + Btn / 2f), 0);
        check.Expect(p.Amount == 1234, "the track's right end is the whole purse");
        p.MouseMove(Cx(TrackX0 - 50f), Cy(SliderY));
        check.Expect(p.Amount == 1, "sliding past the left end is 1");
        p.MouseButton(false, ButtonLeft, 0, 0, 0);
        p.MouseButton(true, ButtonLeft, Cx(PlusX0 + 2f), Cy(SliderY + 2f), KmodShift); p.MouseButton(false, ButtonLeft, 0, 0, 0);
        check.Expect(p.Amount == 11, "Shift+click on + steps 10");
        p.MouseButton(true, ButtonLeft, Cx(DropX0 + 2f), Cy(ActionY + 2f), 0);
        check.Expect(dropped == 11 && !p.Open, "Drop confirms the amount and closes");

        p.Show(40, 1f, 500f, 400f, 1920f, 1080f);
        p.MouseButton(true, ButtonLeft, Cx(ChipX0(3) + 2f), Cy(ChipY + 2f), 0); p.MouseButton(false, ButtonLeft, 0, 0, 0);
        check.Expect(p.Amount == 40, "a chip beyond the purse does nothing");
        dropped = -1;
        p.Key(KeyEscape, 0);
        check.Expect(!p.Open && dropped == -1, "Esc cancels without a drop");
        p.Show(0, 1f, 500f, 400f, 1920f, 1080f);
        p.Key(KeyReturn, 0);
        check.Expect(p.Open && dropped == -1, "an empty purse cannot drop");
        p.Hide();

        p.Show(5000, 2f, 10f, 10f, 800f, 600f);
        check.Expect(p.X >= 0f && p.Y >= 0f, "clamped on screen at 2x");
        var b = new VertexBatch(0f, 0f);
        var atlas = new GlyphAtlas { LineHeight = 14f, Ascent = 11f };
        p.Emit(b, null, atlas, atlas);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) { b.Clear(); p.Emit(b, null, atlas, atlas); }
        check.Expect(GC.GetAllocatedBytesForCurrentThread() - before == 0, "a warm emit allocates nothing");
        log.Info(check.Ok ? "GoldPrompt.SelfCheck: passed" : "GoldPrompt.SelfCheck: FAILED");
    }
}
