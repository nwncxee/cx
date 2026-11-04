using System;
using System.IO;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// Depth of field's approved examples that need no device (approved-spec). Oracles: focus distances worked by hand from
/// the stock <c>inc_postpr_dof</c> rules, vanilla's defaults and ranges as read from the client's config bindings, and
/// cx.log as written to disk.
/// </summary>
internal static partial class DepthOfField
{
    static partial void Examples(Checker check, ExtenderLog log)
    {
        MouseExample(check);
        RefusedExample(check, log);
        DefaultsExample(check);
        ChoicesExample(check);
    }

    private static void MouseExample(Checker check)
    {
        // Given the depth of field focus type is set to Mouse
        // When the mouse moves from the character to a distant wall
        // Then the focus moves to the wall smoothly, with no jump
        (float amount, float dead, int focus, bool never) = (Dof.Amount.V, Dof.DeadZone.V, Dof.Focus.V, Dof.NeverBlur.V);
        try
        {
            Dof.Amount.V = 1f; Dof.DeadZone.V = 3f; Dof.Focus.V = 3; Dof.NeverBlur.V = false;
            var onCharacter = new Scene(8f, 45f, false, false, 8f, 0f, 0f, 0f);
            var onWall = onCharacter with { MouseDepth = 30f };
            Band now = Focus(in onCharacter);
            // the rules offset a mouse focus by a metre and the dead zone adds half its 3 m: 8 + 1 + 1.5, and 30 + 1 + 1.5
            check.Expect(MathF.Abs(now.Far - 10.5f) < 0.01f, $"Focus choices: on the character the sharp band ends at 10.5 m (got {now.Far:F2})");
            Band wall = Focus(in onWall);
            check.Expect(MathF.Abs(wall.Far - 32.5f) < 0.01f, $"Focus choices: on the wall it ends at 32.5 m (got {wall.Far:F2})");
            float largest = 0f, was = now.Far;
            for (int frame = 0; frame < 120; frame++)
            {
                now = Ease(in now, in wall, 1f / 60f);
                largest = MathF.Max(largest, now.Far - was);
                was = now.Far;
            }
            check.Expect(largest > 0f && largest < 22f * 0.15f, $"Focus choices: no frame takes more than 15% of the move (largest step {largest:F2} m)");
            check.Expect(MathF.Abs(now.Far - 32.5f) < 0.25f, $"Focus choices: two seconds later the focus has reached the wall (at {now.Far:F2})");
        }
        finally { Dof.Amount.V = amount; Dof.DeadZone.V = dead; Dof.Focus.V = focus; Dof.NeverBlur.V = never; }
    }

    private static void RefusedExample(Checker check, ExtenderLog log)
    {
        // Given a graphics device that cannot run the depth of field effect
        // When the game starts with the setting on
        // Then the frame draws with no blur and cx.log says why
        string? noWaves = VkBackend.DofRefusalOf(waves: false, linearDepth: true);
        string? noFilter = VkBackend.DofRefusalOf(waves: true, linearDepth: false);
        check.Expect(noWaves != null && noFilter != null, "A device that refuses: a device missing either feature is refused");
        check.Expect(VkBackend.DofRefusalOf(waves: true, linearDepth: true) == null, "A device that refuses: one with both is not");
        Forget();
        bool enabled = Dof.Enabled.V;
        Dof.Enabled.V = true;
        check.Expect(Wanted, "A device that refuses: the setting on wants the effect, before any refusal");
        string why = $"the test GPU refused the effect ({Environment.TickCount64})";
        Refuse(why, log);
        Refuse(why, log);
        try
        {
            check.Expect(!Wanted, "A device that refuses: the frame draws with no blur");
            ExtenderLog.Flush();
            string path = Path.Combine(HostEnv.Home, "cx.log");
            if (!OperatingSystem.IsLinux() || !File.Exists(path)) { check.Skip("the cx.log half: no cx.log under $HOME to read"); return; }
            int said = 0;
            foreach (string line in File.ReadLines(path)) if (line.Contains(why, StringComparison.Ordinal)) said++;
            check.Expect(said == 1, $"A device that refuses: cx.log says why once (said {said} times)");
        }
        finally { Forget(); Dof.Enabled.V = enabled; }
    }

    private static void DefaultsExample(Checker check)
    {
        // Given a client that has never saved CX's depth of field settings
        // When the settings panel opens
        // Then depth of field is off, Amount is 1, Dead zone is 3, Focus type is Camera Pitch and Never blur the player is on
        check.Expect(!Dof.Enabled.Default, "Vanilla's defaults: depth of field is off");
        check.Expect(Dof.Amount.Default == 1f, $"Vanilla's defaults: Amount is 1 (got {Dof.Amount.Default})");
        check.Expect(Dof.DeadZone.Default == 3f, $"Vanilla's defaults: Dead zone is 3 (got {Dof.DeadZone.Default})");
        check.Expect(Dof.Focus.FormatDefault() == "Camera Pitch", $"Vanilla's defaults: Focus type is Camera Pitch (got {Dof.Focus.FormatDefault()})");
        check.Expect(Dof.NeverBlur.Default, "Vanilla's defaults: Never blur the player is on");
    }

    private static void ChoicesExample(Checker check)
    {
        // Given the settings panel is open
        // When the depth of field rows are used
        // Then Amount runs from 0 to 5, Dead zone from 0 to 10, and Focus type offers Player Character, Camera Pitch, Dynamic and Mouse
        check.Expect(Dof.Amount.Min == 0f && Dof.Amount.Max == 5f, $"Vanilla's choices: Amount runs 0 to 5 (got {Dof.Amount.Min} to {Dof.Amount.Max})");
        check.Expect(Dof.DeadZone.Min == 0f && Dof.DeadZone.Max == 10f, $"Vanilla's choices: Dead zone runs 0 to 10 (got {Dof.DeadZone.Min} to {Dof.DeadZone.Max})");
        string offered = string.Join(", ", Dof.Focus.Choices);
        check.Expect(offered == "Player Character, Camera Pitch, Dynamic, Mouse", $"Vanilla's choices: Focus type offers the four (got {offered})");
    }
}
