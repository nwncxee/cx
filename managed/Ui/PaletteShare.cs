using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Ui;

/// <summary>The custom palette's buttons and the palette scan: start from the previous palette, copy and paste
/// palette-file text through the clipboard, save to <c>override/</c>, and list every <c>cxpal_*</c> palette there.
/// Cold: a button press or a scan.</summary>
internal static unsafe class PaletteShare
{
    /// <summary>The palette file's extension and its restype: plain text, which override/, a hak (ERF) and NWSync all
    /// carry unfiltered, and which the engine never loads by itself under a <c>cxpal_</c> name.</summary>
    internal const string Ext = ".txt";
    internal const ushort ResTypeTxt = 10;

    private sealed class ScanState { public long Key = long.MinValue; }
    private static ScanState _scan = new();

    private static ExtenderLog? Log => KnobGroup.Log;

    internal static void StartFromPrevious()
    {
        SetCustom(Theme.RgbOf(Theme.Previous) ?? Theme.Palettes[0].Rgb);
        Notice.Show(Log, $"Custom palette: started from {Theme.Previous}");
    }

    internal static void Copy()
    {
        if (EngineFn.SdlSetClipboardText == null) return;
        string id = UiKnobs.Palette.V;
        byte[] u = Encoding.UTF8.GetBytes(PaletteText.Format(id == Theme.Custom ? UiKnobs.CustomName.V : id, Theme.RgbOf(id) ?? Theme.Palettes[0].Rgb) + "\0");
        fixed (byte* b = u) EngineFn.SdlSetClipboardText(b);
        Notice.Show(Log, "Palette copied to the clipboard");
    }

    internal static void Paste()
    {
        if (EngineFn.SdlGetClipboardText == null) return;
        IntPtr p = EngineFn.SdlGetClipboardText();
        if (p == IntPtr.Zero) return;
        string text = Marshal(p);
        if (!PaletteText.TryParse(text, Theme.RgbOf, out _, out uint[] rgb, out string error))
        { Notice.Show(Log, $"Clipboard is not a palette: {error}"); return; }
        SetCustom(rgb);
        Notice.Show(Log, "Custom palette: pasted from the clipboard");
    }

    internal static void Save()
    {
        string id = UiKnobs.CustomName.V.Trim().ToLowerInvariant();
        if (!Theme.ValidId(id)) { Notice.Show(Log, $"Palette file name '{id}' must be 1-10 of a-z, 0-9 and _"); return; }
        string? dir = OverrideDir();
        if (dir == null) { Notice.Show(Log, "Palette not saved: the game's user directory is not known yet"); return; }
        string path = Path.Combine(dir, Theme.FilePrefix + id + Ext);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, PaletteText.Format(id, Theme.CustomRgb()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Notice.Show(Log, $"Palette not saved to {path}: {e.Message}"); return; }
        Notice.Show(Log, $"Palette saved to {path}");
        Rescan();
    }

    /// <summary>Per frame: re-list when the content can have moved -- entering or leaving a module (its haks), a new
    /// NWSync manifest or session. A compare per frame; the scan itself is cold.</summary>
    internal static void Tick()
    {
        long key = CX.Alloy.NwSyncReader.ContentEdge * 2 + (GameGate.InModule ? 1 : 0);
        if (key == _scan.Key || Native.UserDirectory() == null) return;
        _scan.Key = key;
        Rescan();
    }

    /// <summary>Re-list every palette: the player's own from <c>override/</c> on disk (fresh after a save, which the
    /// engine's one startup scan of override/ would not be), then a hak's, the module's or NWSync's through the
    /// resource manager, which win on a shared id as they do in the engine. A file that does not parse is logged and left out.</summary>
    internal static void Rescan()
    {
        var found = new List<Palette>();
        string? dir = OverrideDir();
        if (dir != null && Directory.Exists(dir))
            foreach (string f in Directory.EnumerateFiles(dir, Theme.FilePrefix + "*" + Ext))
            {
                string id = Path.GetFileNameWithoutExtension(f)[Theme.FilePrefix.Length..].ToLowerInvariant();
                if (!Theme.ValidId(id) || found.Exists(x => x.Id == id)) continue;
                string text;
                try { text = File.ReadAllText(f); }
                catch (IOException) { continue; }
                if (PaletteText.TryParse(text, Theme.RgbOf, out string label, out uint[] rgb, out string error))
                    found.Add(new Palette(id, label.Length > 0 ? label : id, rgb));
                else Log?.Info($"Palette {Path.GetFileName(f)} refused: {error}");
            }
        int own = found.Count;
        FromContent(found);
        found.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        Log?.Info($"Palettes: {own} in override/, {found.Count - own} from the server's content" + (found.Count > 0 ? $" ({string.Join(", ", found.ConvertAll(p => p.Id))})" : ""));
        Theme.SetFound(found.ToArray());
    }

    /// <summary>The <c>cxpal_*</c> text resources a hak, the module or NWSync supply. Main thread, outside any resman hook.</summary>
    private static void FromContent(List<Palette> found)
    {
        if (EngineFn.ResManGetResOfType == null || EngineFn.ExoStringListCount == null || EngineFn.ExoStringListGet == null
            || EngineFn.ExoStringListDtor == null || EngineFn.OperatorDelete == null || !CX.Alloy.ResLocate.Attach(out _)
            || CX.Alloy.ResLocate.ResMan == IntPtr.Zero) return;
        IntPtr list = EngineFn.ResManGetResOfType(CX.Alloy.ResLocate.ResMan, ResTypeTxt, 0);
        if (list == IntPtr.Zero) return;
        var ids = new List<string>();
        int n = EngineFn.ExoStringListCount(list);
        for (int i = 0; i < n; i++)
        {
            IntPtr str = EngineFn.ExoStringListGet(list, i);
            byte* text = str == IntPtr.Zero ? null : *(byte**)str;
            if (text == null) continue;
            int len = 0;
            while (len < 16 && text[len] != 0) len++;
            string name = Encoding.ASCII.GetString(text, len).ToLowerInvariant();
            if (name.StartsWith(Theme.FilePrefix, StringComparison.Ordinal) && !ids.Contains(name)) ids.Add(name);
        }
        EngineFn.ExoStringListDtor(list);
        EngineFn.OperatorDelete(list);
        foreach (string resref in ids)
        {
            string id = resref[Theme.FilePrefix.Length..];
            if (!Theme.ValidId(id) || !FromServer(resref)) continue;
            byte[]? b = CX.Alloy.ShaderResource.Blob(resref, ResTypeTxt, Log);
            if (b == null) continue;
            if (!PaletteText.TryParse(Encoding.UTF8.GetString(b), Theme.RgbOf, out string label, out uint[] rgb, out string error))
            { Log?.Info($"Palette {resref}.txt (server content) refused: {error}"); continue; }
            found.RemoveAll(x => x.Id == id);
            found.Add(new Palette(id, label.Length > 0 ? label : id, rgb));
        }
    }

    /// <summary>The resref's winning copy is a hak's, the module's or NWSync's -- not a loose file, which the disk read
    /// already covered (and which may be older than the file a save just wrote).</summary>
    private static bool FromServer(string resref)
    {
        byte* name = stackalloc byte[17];
        for (int i = 0; i < 17; i++) name[i] = i < resref.Length ? (byte)resref[i] : (byte)0;
        if (!CX.Alloy.ResLocate.Find(name, ResTypeTxt, out _, out IntPtr entry)) return false;
        byte kind = (byte)new CKeyTableEntry(entry).ResId[0];
        return kind is CX.Alloy.TexLoadRead.KindErf or CX.Alloy.TexLoadRead.KindNwSync;
    }

    private static string? OverrideDir() => Native.UserDirectory() is string u ? Path.Combine(u, "override") : null;

    private static void SetCustom(uint[] rgb)
    {
        for (int i = 0; i < UiKnobs.CustomRgb.Length; i++) UiKnobs.CustomRgb[i].Set((rgb[i] << 8) | 0xff);
        UiKnobs.Palette.Set(Theme.Custom);
    }

    private static string Marshal(IntPtr p)
    {
        byte* s = (byte*)p;
        int n = 0;
        while (s[n] != 0) n++;
        string text = Encoding.UTF8.GetString(s, n);
        if (EngineFn.SdlFree != null) EngineFn.SdlFree(p);   // SDL_GetClipboardText returns SDL-malloc'd memory
        return text;
    }
}
