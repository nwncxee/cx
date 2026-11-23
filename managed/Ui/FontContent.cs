using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Services.NuiItems;

namespace CX.Ui;

/// <summary>The engine half of the fonts a hak, the module or NWSync supply: every <c>.ttf</c> resource of that content,
/// read again whenever it changes and handed to <see cref="FontRegistry.UseContent"/>. Main thread, outside any resman hook.</summary>
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
            if (NuiSkinFontLoad.Read(resref, NuiSkinFontLoad.TtfResType, out byte[]? b, out _, serverOnly: true)) found.Add((resref, FontSource.Server, b!));
            else left++;
        FontCatalog content = FontCatalog.FromContent(found);
        FontRegistry.UseContent(content);
        KnobGroup.Log?.Info($"FontContent: {found.Count} .ttf from a hak, the module or NWSync ({left} other left out); families: {string.Join(", ", content.Families)}");
        UiHostService.ApplyTypeface();
        TextRig.Refont();
    }
}
