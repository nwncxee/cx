using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Services.NuiItems;

namespace CX.Ui;

/// <summary>The engine half of the fonts the game's own data, a hak, the module or NWSync supply: every <c>.ttf</c>
/// resource, read again whenever the content changes and handed to <see cref="FontRegistry.UseContent"/>. Main thread,
/// outside any resman hook.</summary>
internal static class FontContent
{
    private sealed class ScanState { public long Key = long.MinValue; }

    private static readonly ScanState _scan = new();

    /// <summary>Re-reads the content's fonts once the module or the NWSync content moves; a frame with no move costs a compare.</summary>
    internal static void Tick()
    {
        long key = CX.Alloy.NwSyncReader.ContentEdge * 2 + (GameGate.InModule ? 1 : 0);
        if (key == _scan.Key) return;
        _scan.Key = key;
        var found = new List<(string, FontSource, byte[])>();
        int left = 0;
        foreach (string resref in CX.Alloy.ResLocate.OfType(NuiSkinFontLoad.TtfResType))
            if (NuiSkinFontLoad.Read(resref, NuiSkinFontLoad.TtfResType, out byte[]? b, out _, out FontSource source)) found.Add((resref, source, b!));
            else left++;
        FontCatalog content = FontCatalog.FromContent(found);
        FontRegistry.UseContent(content);
        KnobGroup.Log?.Info($"FontContent: {found.Count} .ttf read ({left} unreadable); game's: {string.Join(", ", content.GameFamilies)}; families: {string.Join(", ", content.Families)}");
        UiHostService.ApplyTypeface();
        TextRig.Refont();
    }
}
