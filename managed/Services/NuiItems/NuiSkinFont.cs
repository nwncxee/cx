using System;
using System.Globalization;

namespace CX.Services.NuiItems;

/// <summary>The NUI skin's default font: what the server's own NUI menu draws its labels in.</summary>
internal static class NuiSkinFont
{
    /// <summary>
    /// The first <c>[[fonts]]</c> entry of a <c>nui_skin.tml</c>, which NUI takes as its default: its resref, lowercase as
    /// the resource tables hold it, and its <c>font_size</c> in px at UI scale 1. False when there is none or either is unreadable.
    /// </summary>
    internal static bool Parse(string skin, out string resref, out float px)
    {
        resref = ""; px = 0f;
        bool sized = true;
        foreach ((string key, string value) in First(skin))
        {
            if (key == "resref") resref = value.Trim('"').ToLowerInvariant();
            else if (key == "font_size") sized = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out px);
        }
        return sized && resref.Length > 0 && px > 0f;
    }

    /// <summary>The key and value lines of the skin's first <c>[[fonts]]</c> entry, in order.</summary>
    private static System.Collections.Generic.IEnumerable<(string Key, string Value)> First(string skin)
    {
        bool inFirst = false;
        foreach (string raw in skin.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (inFirst) yield break;
                inFirst = line == "[[fonts]]";
                continue;
            }
            int eq = line.IndexOf('=');
            if (!inFirst || eq < 0) continue;
            yield return (line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
    }

    /// <summary>How the default font's text is laid down: its <c>spacing_h</c> in px and its <c>pixel_snap</c>.</summary>
    internal static bool Text(string skin, out float spacing, out bool snap)
    {
        spacing = 0f; snap = false;
        bool any = false;
        foreach ((string key, string value) in First(skin))
        {
            any = true;
            if (key == "spacing_h") float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out spacing);
            else if (key == "pixel_snap") snap = value == "true";
        }
        return any;
    }
}
