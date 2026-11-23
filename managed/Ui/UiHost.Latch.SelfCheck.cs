using System;
using System.Diagnostics;
using CX.Framework;

namespace CX.Ui;

/// <summary>A window's band holds for a whole frame's draw and a whole event's walk, though its popup or tooltip
/// changes partway. Oracle: a hand-driven tooltip clock and a counted draw or offer.</summary>
internal static class UiHostLatchSelfCheck
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UiHost.Latch.SelfCheck");
        Func<long> clock = Tooltip.Clock;
        var timing = Tooltip.Timing;
        long t = 1_000_000, f = Stopwatch.Frequency;
        Tooltip.Clock = () => t;
        Tooltip.Timing = () => (0.5f, 0.2f, 0.4f);
        try
        {
            // Given the settings panel's tooltip fading out
            // When the fade ends while NUI flushes, between the band under NUI and the popup band
            // Then the panel draws once that frame
            UiHost.Alone(() =>
            {
                var p = new KnobPanel();
                Window w = KnobPanelService.NewWindow(p);
                int drawn = 0;
                w.DrawContent = () => drawn++;
                p.Tip.Hover("row", "Help", 50f, 50f, 70f);
                t += f * 5;
                p.Tip.Leave();
                t += f / 5;
                UiHost.DrawFrame(UiLayer.UnderGui);
                UiHost.DrawFrame(UiLayer.BelowNui);
                t += f / 4;
                UiHost.DrawFrame(UiLayer.AboveNui);
                UiHost.DrawFrame(UiLayer.Popup);
                check.Expect(drawn == 1, $"the settings panel draws once in the frame its tooltip finishes fading: drew {drawn}");
            });

            // Given the pointer resting on a settings row
            // When the tooltip's delay ends while NUI flushes
            // Then the panel draws once that frame
            UiHost.Alone(() =>
            {
                var p = new KnobPanel();
                Window w = KnobPanelService.NewWindow(p);
                int drawn = 0;
                w.DrawContent = () => drawn++;
                p.Tip.Hover("row", "Help", 50f, 50f, 70f);
                t += f / 2 - 10;
                UiHost.DrawFrame(UiLayer.UnderGui);
                UiHost.DrawFrame(UiLayer.BelowNui);
                t += f / 10;
                UiHost.DrawFrame(UiLayer.AboveNui);
                UiHost.DrawFrame(UiLayer.Popup);
                check.Expect(drawn == 1, $"the settings panel draws once in the frame its tooltip's delay ends: drew {drawn}");
            });
        }
        finally { Tooltip.Clock = clock; Tooltip.Timing = timing; }

        // Given a window with a popup open
        // When a press closes the popup and the window declines the press
        // Then the press is offered to that window once
        UiHost.Alone(() =>
        {
            bool open = true;
            int offered = 0;
            _ = new Window(0, 0, 100, 100)
            {
                PopupOpen = () => open,
                OnMouseButton = (down, b, x, y) => { offered++; open = false; return false; },
            };
            UiHost.DispatchMouseButton(true, 1, 10, 10);
            check.Expect(offered == 1, $"one press is offered to a window once: {offered}");
        });

        // Given a window under NUI whose popup opens after a frame's draw
        // When the next frame draws
        // Then the window draws in the popup band and not under NUI
        UiHost.Alone(() =>
        {
            bool open = false;
            int drawn = 0;
            var w = new Window(0, 0, 100, 100) { PopupOpen = () => open };
            w.DrawContent = () => drawn++;
            UiHost.DrawFrame();
            open = true;
            UiHost.DrawFrame(UiLayer.UnderGui);
            UiHost.DrawFrame(UiLayer.BelowNui);
            int under = drawn;
            UiHost.DrawFrame(UiLayer.AboveNui);
            UiHost.DrawFrame(UiLayer.Popup);
            check.Expect(under == 1 && drawn == 2, $"a popup opened between frames moves its window at the next frame: under NUI {under}, in all {drawn}");
        });

        // Given a window that opens another window under the pointer when pressed, and declines the press
        // When the player presses there
        // Then the window it opened is not offered that press
        UiHost.Alone(() =>
        {
            int late = 0;
            _ = new Window(0, 0, 100, 100)
            {
                OnMouseButton = (down, b, x, y) =>
                {
                    _ = new Window(0, 0, 100, 100) { OnMouseButton = (d2, b2, x2, y2) => { late++; return false; } };
                    return false;
                },
            };
            UiHost.DispatchMouseButton(true, 1, 10, 10);
            check.Expect(late == 0, $"a window opened by a press is not offered that press: {late}");
        });

        // Given two windows under the pointer, the upper one bringing the lower one to the front when pressed, and both declining
        // When the player presses there
        // Then each window is offered the press once
        UiHost.Alone(() =>
        {
            int upper = 0, lower = 0;
            var low = new Window(0, 0, 100, 100) { OnMouseButton = (down, b, x, y) => { lower++; return false; } };
            _ = new Window(0, 0, 100, 100) { OnMouseButton = (down, b, x, y) => { upper++; UiHost.Focus(low); return false; } };
            UiHost.DispatchMouseButton(true, 1, 10, 10);
            check.Expect(upper == 1 && lower == 1, $"a window raised during a press's walk leaves each window one offer: upper {upper}, lower {lower}");
        });

        if (check.Ok) log.Info("UiHost.Latch.SelfCheck: passed");
    }
}
