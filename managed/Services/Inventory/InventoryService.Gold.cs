using System;
using System.Globalization;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Inventory;

/// <summary>
/// The gold button and the world drops. The button opens the CX <see cref="GoldPrompt"/>; its Drop puts the amount "on
/// the cursor" (the stock <c>CGuiPlayerGoldSource</c> drag, drawn with the same <c>ir_buy</c> icon) and the next left
/// click in the world sends it where it lands, by the stock drop's rules (<see cref="InventoryNative.GoldTarget"/>).
/// Items dragged out land the same way; both run the <c>inventory.gold</c> / <c>.drop</c> / <c>.barter</c> / <c>.use</c> routes.
/// </summary>
public sealed unsafe partial class InventoryService
{
    private static readonly GoldPrompt _gold = new();
    private static Window? _goldWin;
    private static VertexBatch? _goldBatch;
    /// <summary>Gold on the cursor, 0 when none.</summary>
    private static int _carry;
    private static string _carryText = "";

    private static void InitGold()
    {
        _gold.Confirm = n => { _carry = n; _carryText = n.ToString("N0", CultureInfo.CurrentCulture) + " gp"; SyncGoldModal(); };
        _goldWin = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            CapturesPointer = true,
            DrawContent = DrawGold,
            OnMouseButton = GoldMouseButton,
            OnMouseMotion = (x, y) => _gold.Open ? _gold.MouseMove(x, y) : false,
            OnMouseWheel = dy => _gold.Open && _gold.MouseWheel(dy),
            OnKey = GoldKey,
            HitTestOverride = (px, py) => _gold.Contains(px, py),
        };
    }

    /// <summary>The gold window withholds chords (and wins presses ahead of the engine GUI) exactly while the prompt is up
    /// or gold is on the cursor.</summary>
    private static void SyncGoldModal()
    {
        if (_goldWin != null) _goldWin.SuppressChords = _gold.Open || _carry > 0;
    }

    private static void EndCarry() { _carry = 0; SyncGoldModal(); }

    /// <summary>The gold button: refused by the policy route, or an empty purse; else the prompt, centred on the window.</summary>
    private static void OpenGold()
    {
        if (ServerPolicy.Route("inventory.gold") == PolicyRoute.Deny) { InventoryNative.Feedback("The server does not allow that here."); return; }
        int purse = InventoryNative.Gold();
        if (purse <= 0) { InventoryNative.Feedback("You have no gold to drop."); return; }
        EndCarry();
        _gold.Show(purse, _inv.S, _inv.X + _inv.W / 2f, _inv.Y + _inv.H / 2f, TextRig.ViewportW, TextRig.ViewportH);
        SyncGoldModal();
    }

    private static bool GoldMouseButton(bool down, int button, int x, int y)
    {
        if (_gold.Open) { bool r = _gold.MouseButton(down, button, x, y, InventoryNative.Mods()); SyncGoldModal(); return r; }
        if (_carry <= 0) return false;
        if (!down) return true;
        int amount = _carry;
        EndCarry();
        if (button == 1) DropGold(amount, x, y);
        return true;
    }

    private static bool GoldKey(uint key, int mods)
    {
        if (_gold.Open) { _gold.Key(key, mods); SyncGoldModal(); return true; }
        if (_carry > 0 && key == 0x1B) { EndCarry(); return true; }   // not-an-offset: SDLK_ESCAPE
        return false;
    }

    /// <summary>Carried gold released at (<paramref name="x"/>, <paramref name="y"/>): over a CX window nothing happens (as
    /// the stock drag does over a panel); in the world it goes where <see cref="InventoryNative.GoldTarget"/> says.</summary>
    private static void DropGold(int amount, int x, int y)
    {
        if (UiHost.HitTest(x, y) != null) return;
        WorldHit w = InventoryNative.PickWorld(x, y);
        if (!InventoryNative.GoldTarget(w, out uint target)) return;
        if (ExtenderLog.Enabled) _log?.Info($"[inv] gold {amount} -> 0x{target:x} at {w.Point}");
        if (!Routed("inventory.gold", target, 0, amount, w)) return;
        InventoryNative.SendGold(target, amount, w);
    }

    /// <summary>An item dragged out of the window, released at the action's screen point: over another CX window, or the CX
    /// bar away from its buttons, nothing; over a quickbar button it is assigned there (route <c>inventory.quickbar</c>, the slot 0-35 in the amount field; not
    /// from a container, whose items the stock quickbar refuses); in the world the stock drop rules
    /// (<see cref="InventoryNative.DropVerb"/>) pick drop / use / barter.</summary>
    private static void DropInWorld(InvAction a)
    {
        Window? over = UiHost.HitTest(a.X, a.Y);
        if (over != null && !Quickbar.QuickbarService.IsBar(over)) return;
        int slot = -1;
        IntPtr button = a.Other == 0 ? InventoryNative.QuickButtonAt(a.X, a.Y, out slot) : IntPtr.Zero;
        if (button != IntPtr.Zero)
        {
            if (Routed("inventory.quickbar", a.Oid, a.Mask, slot, default)) InventoryNative.AssignQuickButton(button, a.Oid);
            return;
        }
        if (over != null) return;   // the CX bar but no button under the point (its grip): not a drop on the world
        if (!InventoryKnobs.DragDrop.V) return;
        WorldHit w = InventoryNative.PickWorld(a.X, a.Y);
        string? verb = InventoryNative.DropVerb(a.Oid, a.Mask != 0 || a.Other != 0, w);
        if (verb == null) return;
        if (!Routed("inventory." + verb, a.Oid, a.Mask, 0, w)) return;
        InventoryNative.DropOn(verb, a.Oid, w);
        if (verb == "drop") _inv.MarkPending(a);
    }

    /// <summary>The policy route of a world action: true = send the engine's message. <c>server</c> emits
    /// <c>action;oid;mask;amount;0;target;x;y;z</c> (the world point last, invariant floats).</summary>
    private static bool Routed(string action, uint oid, uint mask, int amount, in WorldHit w)
    {
        switch (ServerPolicy.Route(action))
        {
            case PolicyRoute.Deny:
                InventoryNative.Feedback("The server does not allow that here.");
                return false;
            case PolicyRoute.Server:
                uint target = w.Kind == WorldKind.Object ? w.Oid : InventoryModel.Invalid;
                ServerPolicy.Emit(action, Hex(oid), Hex(mask), amount.ToString(CultureInfo.InvariantCulture), "0", Hex(target),
                                  F(w.Point.X), F(w.Point.Y), F(w.Point.Z));
                return false;
        }
        return true;
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- the frame ----

    private static void DrawGold()
    {
        if (!_gold.Open && _carry <= 0) return;
        if (!GameGate.InGame) { _gold.Hide(); EndCarry(); return; }
        if (_gold.Open) _gold.SetMax(InventoryNative.Gold());
        if (!Faces()) return;
        _goldBatch ??= new VertexBatch(_text!.SolidU, _text.SolidV);
        _goldBatch.Reset(_text!.SolidU, _text.SolidV);
        _goldBatch.Outline = Halo;
        Skin? skin = null;
        if (Theme.IsNative && _skin.Arm(_log, ""))
        {
            _skin.Begin();
            skin = _skin;
        }
        _gold.Emit(_goldBatch, skin, _text, _small!);
        if (_carry > 0 && EngineFn.SdlGetMouseState != null)
        {
            int mx = 0, my = 0;
            EngineFn.SdlGetMouseState(&mx, &my);
            float s = _inv.S, size = 32f * s, ix = mx + 10f * s, iy = my + 10f * s;
            if (skin != null) skin.Draw(InventoryWindow.KGoldCursor, ix, iy, ix + size, iy + size, s);
            UiText.Draw(_goldBatch, _small!, _carryText, ix + size + 4f * s, iy + size - _small!.LineHeight + _small.Ascent, PanelChrome.Warn, float.MaxValue);
        }
        skin?.Flush();
        TextRig.DrawBatch(_goldBatch.AsSpan(), _goldBatch.VertexCount);
    }
}
