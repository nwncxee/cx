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
        bool inFirst = false;
        foreach (string raw in skin.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (inFirst) break;
                inFirst = line == "[[fonts]]";
                continue;
            }
            if (!inFirst) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            if (key == "resref") resref = value.Trim('"').ToLowerInvariant();
            else if (key == "font_size" && !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out px)) return false;
        }
        return resref.Length > 0 && px > 0f;
    }
}
