using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Chat;

public sealed unsafe partial class ChatService
{
    /// <summary>Esc on the chat gear panel backs out one step: an open dropdown first, then the panel.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void GearListSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ChatService.GearList.SelfCheck");
        bool inGame = GameGate.InGame, typing = IsTyping;
        ChatWindow win = ChatExamplesKit.Window(100, 100);
        ChatLayout? was = UseLayout(new ChatLayout(win));
        try
        {
            GameGate.InGame = true;
            IsTyping = false;
            UiHost.Alone(() =>
            {
                // Given the chat gear panel open with one of its dropdowns open
                // When the player presses Esc
                // Then the dropdown closes and the gear panel stays open
                _ = NewSurfaces();
                bool gear = UiHost.DispatchMouseButton(true, 1, (int)(win.X + win.W - 2), (int)(win.Y + 2));
                _settings.Panel.Drop.OpenAt(new[] { "a", "b" }, 0, null, 100f, 100f, 120f, 100f, 20f);
                bool list = _settings.Panel.Drop.Open;
                bool taken = EscOrderApprovedExamples.Press();
                check.Expect(gear && list && taken && !_settings.Panel.Drop.Open && _settings.Open,
                    $"Esc on the gear panel's open list closes the list and keeps the panel: gear={gear} list={list} taken={taken} listAfter={_settings.Panel.Drop.Open} panelOpen={_settings.Open}");
                bool second = EscOrderApprovedExamples.Press();
                check.Expect(second && !_settings.Open, $"a second Esc closes the gear panel: taken={second} panelOpen={_settings.Open}");

                // Given the chat gear panel open under NUI
                // When one of its rows shows a tooltip
                // Then the chat surface under NUI draws in the popup band, so the tooltip draws over NUI
                Window below = NewSurfaces().Below;
                _ = UiHost.DispatchMouseButton(true, 1, (int)(win.X + win.W - 2), (int)(win.Y + 2));
                System.Func<long> clock = Tooltip.Clock;
                long t = 1_000_000;
                Tooltip.Clock = () => t;
                try
                {
                    UiLayer quiet = below.Band;
                    _settings.Panel.Tip.Hover("row", "Help", 120f, 120f, 140f);
                    t += System.Diagnostics.Stopwatch.Frequency * 5;
                    check.Expect(_settings.Open && quiet == UiLayer.BelowNui && below.Band == UiLayer.Popup,
                        $"a gear tooltip lifts chat's under-NUI surface into the popup band: open={_settings.Open} before {quiet}, shown {below.Band}");
                }
                finally { _settings.Panel.Tip.Cut(); Tooltip.Clock = clock; }

                // Given the chat gear panel open under NUI with a row's tooltip showing
                // When the pointer rests on the panel
                // Then chat's surface over NUI stays in its own band and leaves the panel's pointer to the surface under NUI
                (Window under, Window over) = NewSurfaces();
                _settings.Reset();
                _ = UiHost.DispatchMouseButton(true, 1, (int)(win.X + win.W - 2), (int)(win.Y + 2));
                Tooltip.Clock = () => t;
                try
                {
                    _settings.Panel.Tip.Hover("row", "Help", 120f, 120f, 140f);
                    t += System.Diagnostics.Stopwatch.Frequency * 5;
                    _settings.Panel.X = 600f; _settings.Panel.Y = 600f; _settings.Panel.W = 300f; _settings.Panel.H = 200f;
                    float px = _settings.X + 2f, py = _settings.Y + 2f;
                    bool chatThere = win.HitTestFull((int)px, (int)py);
                    check.Expect(_settings.Open && !chatThere && over.Band == UiLayer.AboveNui && under.Hit(px, py) && !over.Hit(px, py),
                        $"a gear panel under NUI lifts and hits only chat's under-NUI surface: open={_settings.Open} chat window there {chatThere}, over band {over.Band}, under hit {under.Hit(px, py)}, over hit {over.Hit(px, py)}");
                }
                finally { _settings.Panel.Tip.Cut(); Tooltip.Clock = clock; }
            });
        }
        finally { GameGate.InGame = inGame; IsTyping = typing; UseLayout(was); _settings.Reset(); }
        if (check.Ok) log.Info("ChatService.GearList.SelfCheck: passed");
    }
}
