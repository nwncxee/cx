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
            });
        }
        finally { GameGate.InGame = inGame; IsTyping = typing; UseLayout(was); _settings.Reset(); }
        if (check.Ok) log.Info("ChatService.GearList.SelfCheck: passed");
    }
}
