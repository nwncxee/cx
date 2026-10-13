using System;
using System.Collections.Generic;
using System.Globalization;
using CX.Framework;
using System.Text;
using System.Text.Json;

namespace CX.Services.Panels;

/// <summary>
/// Reads one policy value, a JSON card or array of cards (<c>docs/ui-policy-contract.md</c>), into <see cref="PanelDecl"/>s.
/// A field it does not know is ignored, so a newer server can send more than an older client reads; a field it does know
/// with the wrong type, a bad colour or a value over a <see cref="PanelLimits"/> entry refuses the whole declaration.
/// Cold: it allocates.
/// </summary>
internal static class PanelParser
{
    private sealed class Refusal(string why) : Exception(why);

    /// <summary>The cards in <paramref name="json"/> (none for an empty array, which declares that the key has no card), or false with the reason the declaration was refused.</summary>
    public static bool TryParse(string json, out PanelDecl[] cards, out string error, PanelStyle? style = null)
    {
        style ??= new PanelStyle();
        cards = [];
        error = "";
        if (Encoding.UTF8.GetByteCount(json) > PanelLimits.MaxJsonBytes) { error = $"over {PanelLimits.MaxJsonBytes} bytes"; return false; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = doc.RootElement;
            var list = new List<PanelDecl>();
            if (root.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in root.EnumerateArray()) list.Add(Panel(e, style));
            else list.Add(Panel(root, style));
            if (list.Count > PanelLimits.MaxPanels || (list.Count == 0 && root.ValueKind != JsonValueKind.Array)) throw new Refusal($"{list.Count} cards (1-{PanelLimits.MaxPanels} allowed)");
            cards = list.ToArray();
            return true;
        }
        catch (Refusal r) { error = r.Message; }
        catch (JsonException j) { error = "not JSON: " + j.Message; }
        return false;
    }

    /// <summary>The style in <paramref name="json"/> (one object of the card style fields) laid over the defaults, or false with the reason it was refused.</summary>
    public static bool TryParseStyle(string json, out PanelStyle style, out string error)
    {
        style = new PanelStyle();
        error = "";
        if (Encoding.UTF8.GetByteCount(json) > PanelLimits.MaxJsonBytes) { error = $"over {PanelLimits.MaxJsonBytes} bytes"; return false; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new Refusal("the style is a JSON object");
            ReadStyle(doc.RootElement, style);
            return true;
        }
        catch (Refusal r) { error = r.Message; }
        catch (JsonException j) { error = "not JSON: " + j.Message; }
        style = new PanelStyle();
        return false;
    }

    private static void Len(JsonElement e, string name, ref float field, float lo = 0f, float hi = PanelLimits.MaxLen)
        => field = Math.Clamp(Num(e, name, field), lo, hi);

    private static void Rgba(JsonElement e, string name, ref uint field)
    {
        if (e.TryGetProperty(name, out JsonElement v)) field = ColorOf(v, name);
    }

    /// <summary>Reads every style field <paramref name="e"/> names over <paramref name="st"/>; a field it does not name keeps its value.</summary>
    private static void ReadStyle(JsonElement e, PanelStyle st)
    {
        if (e.TryGetProperty("sizes", out JsonElement sz))
        {
            if (sz.ValueKind != JsonValueKind.Array || sz.GetArrayLength() is 0 or > PanelLimits.MaxSizes) throw new Refusal($"sizes is an array of 1-{PanelLimits.MaxSizes} numbers");
            var l = new List<float>();
            foreach (JsonElement n in sz.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Number || !n.TryGetSingle(out float f) || !float.IsFinite(f)) throw new Refusal("sizes is an array of numbers");
                l.Add(Math.Clamp(f, PanelLimits.MinSize, PanelLimits.MaxSize));
            }
            l.Sort();
            st.Sizes = l.ToArray();
        }
        Len(e, "minwidth", ref st.MinWidth, PanelLimits.AbsMinWidth, PanelLimits.AbsMaxWidth);
        Len(e, "maxwidth", ref st.MaxWidth, PanelLimits.AbsMinWidth, PanelLimits.AbsMaxWidth);
        if (st.MaxWidth < st.MinWidth) st.MaxWidth = st.MinWidth;
        st.Width = Math.Clamp(Num(e, "width", st.Width), st.MinWidth, st.MaxWidth);
        Len(e, "pad", ref st.Pad); Len(e, "gap", ref st.Gap); Len(e, "textgap", ref st.TextGap); Len(e, "statusgap", ref st.StatusGap);
        Len(e, "icongap", ref st.IconGap); Len(e, "frameinset", ref st.FrameInset);
        Len(e, "dividermargin", ref st.DividerMargin); Len(e, "dividerthick", ref st.DividerThick); Len(e, "sectionpad", ref st.SectionPad);
        Len(e, "footerpad", ref st.FooterPad); Len(e, "footersplit", ref st.FooterSplit, 0.05f, 0.95f);
        Len(e, "statuspx", ref st.StatusPx, PanelLimits.MinIconPx, PanelLimits.MaxIconPx);
        Len(e, "iconspx", ref st.IconsPx, PanelLimits.MinIconPx, PanelLimits.MaxIconPx);
        Len(e, "imagesize", ref st.ImageSize, 1f, PanelLimits.MaxImagePx);
        Len(e, "leading", ref st.Leading, 0.5f, 4f); Len(e, "slant", ref st.Slant, -1f, 1f);
        Len(e, "tabx", ref st.TabX); Len(e, "tabpad", ref st.TabPad); Len(e, "tabextra", ref st.TabExtra); Len(e, "tabdrop", ref st.TabDrop);
        Rgba(e, "color", ref st.Text); Rgba(e, "border", ref st.Border); Rgba(e, "divider", ref st.Divider); Rgba(e, "footerband", ref st.FooterBand);
        Rgba(e, "framefill", ref st.FrameFill); Rgba(e, "frameline", ref st.FrameLine); Rgba(e, "tabcolor", ref st.TabBg); Rgba(e, "tabtext", ref st.TabText);
        Rgba(e, "dimcolor", ref st.DimColor); Rgba(e, "shadow", ref st.Shadow); Rgba(e, "border2", ref st.Border2); Rgba(e, "glow", ref st.Glow); Rgba(e, "corners", ref st.Corners);
        if (e.TryGetProperty("dimalpha", out _)) { float da = 0f; Len(e, "dimalpha", ref da, 0f, 1f); st.DimAlpha = (byte)MathF.Round(da * 255f); }
        Len(e, "borderw", ref st.BorderW); Len(e, "framew", ref st.FrameW); Len(e, "border2w", ref st.Border2W); Len(e, "border2inset", ref st.Border2Inset);
        Len(e, "shadowdx", ref st.ShadowDx, -16f, 16f); Len(e, "shadowdy", ref st.ShadowDy, -16f, 16f);
        Len(e, "glowspread", ref st.GlowSpread, 0f, 64f); Len(e, "glowalpha", ref st.GlowAlpha, 0f, 1f); Len(e, "cornerlen", ref st.CornerLen); Len(e, "cornerw", ref st.CornerW);
        Len(e, "pulsemin", ref st.PulseMin, 0f, 1f); Len(e, "pulseperiod", ref st.PulsePeriodMs, 100f, 60_000f);
        string sides = Str(e, "sides", st.Sides);
        foreach (char ch in sides) if ("trbl".IndexOf(ch) < 0) throw new Refusal("sides uses only t, r, b and l");
        st.Sides = sides;
        st.Side = Str(e, "side", "") switch { "" => st.Side, "right" => Side.Right, "left" => Side.Left, "above" => Side.Above, "below" => Side.Below, var x => throw new Refusal($"side '{x}' is right, left, above or below") };
        st.Align = Str(e, "align", "") switch { "" => st.Align, "start" => Align.Start, "center" => Align.Center, "end" => Align.End, var x => throw new Refusal($"align '{x}' is start, center or end") };
        st.Flip = Bool(e, "flip", st.Flip);
        Len(e, "offset", ref st.Offset, -PanelLimits.MaxLen, PanelLimits.MaxLen); Len(e, "cardgap", ref st.CardGap, -PanelLimits.MaxLen, PanelLimits.MaxLen);
        Len(e, "dx", ref st.Dx, -1000f, 1000f); Len(e, "dy", ref st.Dy, -1000f, 1000f);
        Len(e, "delay", ref st.Delay, -1f, 30f); Len(e, "fadein", ref st.FadeIn, -1f, 10f); Len(e, "fadeout", ref st.FadeOut, -1f, 10f);
        if (e.TryGetProperty("bg", out JsonElement bg))
        {
            if (bg.ValueKind == JsonValueKind.String) { st.BgFrom = ColorOf(bg, "bg"); st.BgTo = null; }
            else if (bg.ValueKind == JsonValueKind.Object)
            {
                st.BgFrom = Col(bg, "from", st.BgFrom);
                st.BgTo = bg.TryGetProperty("to", out JsonElement to) ? ColorOf(to, "to") : null;
                st.Dir = Str(bg, "dir", "v") switch { "h" => BgDir.Horizontal, "d" => BgDir.Diagonal, _ => BgDir.Vertical };
            }
            else throw new Refusal("bg is a colour or {from, to, dir}");
        }
    }

    private static PanelDecl Panel(JsonElement e, PanelStyle global)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new Refusal("a card is a JSON object");
        var c = new PanelDecl { St = global.Clone() };
        ReadStyle(e, c.St);
        c.Tab = Str(e, "tab", "");
        c.Item = Str(e, "item", "");
        int count = 0;
        c.Blocks = Blocks(e, "blocks", 0, ref count, c.St);
        if (e.TryGetProperty("images", out JsonElement imgs))
        {
            if (imgs.ValueKind != JsonValueKind.Array || imgs.GetArrayLength() > PanelLimits.MaxImages) throw new Refusal($"images is an array of at most {PanelLimits.MaxImages}");
            var l = new List<PanelImage>();
            foreach (JsonElement i in imgs.EnumerateArray())
            {
                string src = Str(i, "src", "");
                if (src.Length is 0 or > PanelLimits.MaxIconName) throw new Refusal("an image needs a src");
                l.Add(new PanelImage
                {
                    Src = src,
                    W = Math.Clamp(Num(i, "w", c.St.ImageSize), 1f, PanelLimits.MaxImagePx), H = Math.Clamp(Num(i, "h", c.St.ImageSize), 1f, PanelLimits.MaxImagePx),
                    Dx = Math.Clamp(Num(i, "dx", 0f), -PanelLimits.MaxImagePx, PanelLimits.MaxImagePx),
                    Dy = Math.Clamp(Num(i, "dy", 0f), -PanelLimits.MaxImagePx, PanelLimits.MaxImagePx),
                    Alpha = Math.Clamp(Num(i, "alpha", 1f), 0f, 1f),
                    At = Str(i, "at", "tr") switch
                    {
                        "tl" => Anchor.TopLeft, "bl" => Anchor.BottomLeft, "br" => Anchor.BottomRight, "tr" => Anchor.TopRight,
                        "t" => Anchor.Top, "b" => Anchor.Bottom, "l" => Anchor.Left, "r" => Anchor.Right, "c" => Anchor.Center,
                        var a => throw new Refusal($"image anchor '{a}' is tl, tr, bl, br, t, b, l, r or c"),
                    },
                });
            }
            c.Images = l.ToArray();
        }
        return c;
    }

    private static Block[] Blocks(JsonElement parent, string name, int depth, ref int count, PanelStyle st)
    {
        if (!parent.TryGetProperty(name, out JsonElement arr)) return [];
        if (arr.ValueKind != JsonValueKind.Array) throw new Refusal($"{name} is an array");
        var l = new List<Block>();
        foreach (JsonElement b in arr.EnumerateArray())
        {
            if (++count > PanelLimits.MaxBlocks) throw new Refusal($"more than {PanelLimits.MaxBlocks} blocks");
            l.Add(Block(b, depth, ref count, st));
        }
        return l.ToArray();
    }

    private static Block Block(JsonElement e, int depth, ref int count, PanelStyle st)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new Refusal("a block is a JSON object");
        string type = Str(e, "type", "text");
        var b = new Block { Kind = type switch
        {
            "status" => BlockKind.Status, "divider" => BlockKind.Divider, "icons" => BlockKind.Icons, "section" => BlockKind.Section,
            "footer" => BlockKind.Footer, _ => BlockKind.Text,
        } };
        b.Size = st.Snap(Num(e, "size", 1f));
        b.Color = Col(e, "color", 0);
        if (Bool(e, "dim", false)) b.Color = (b.Color != 0 ? b.Color & 0xFFFFFF00 : st.DimColor & 0xFFFFFF00) | st.DimAlpha;
        b.Runs = Runs(e, "text", Bool(e, "italic", false), st);
        b.Icon = Str(e, "icon", "");
        if (b.Icon.Length > PanelLimits.MaxIconName) throw new Refusal("an icon name is too long");
        b.Frame = Bool(e, "frame", false);
        b.Pulse = Bool(e, "pulse", type == "loading");
        b.Tint = Col(e, "tint", 0);
        b.FrameFill = Col(e, "framefill", 0); b.FrameLine = Col(e, "frameline", 0);
        b.Align = Str(e, "align", "left") switch { "left" => 0, "right" => 1, "center" => 2, var a => throw new Refusal($"align '{a}' is left, center or right") };
        b.After = Opt(e, "after", -PanelLimits.MaxLen); b.Pad = Opt(e, "pad", 0f); b.Margin = Opt(e, "margin", 0f);
        b.Thick = Opt(e, "thick", 0f); b.Spacing = Opt(e, "spacing", 0f); b.Inset = Opt(e, "inset", 0f);
        switch (b.Kind)
        {
            case BlockKind.Icons:
                if (!e.TryGetProperty("icons", out JsonElement ics) || ics.ValueKind != JsonValueKind.Array || ics.GetArrayLength() > PanelLimits.MaxIcons)
                    throw new Refusal($"icons is an array of at most {PanelLimits.MaxIcons} names");
                var names = new List<string>();
                foreach (JsonElement n in ics.EnumerateArray())
                {
                    string s = n.ValueKind == JsonValueKind.String ? n.GetString()! : throw new Refusal("an icon name is a string");
                    if (s.Length > PanelLimits.MaxIconName) throw new Refusal("an icon name is too long");
                    names.Add(s);
                }
                b.Icons = names.ToArray();
                b.IconPx = Math.Clamp(Num(e, "px", st.IconsPx), PanelLimits.MinIconPx, PanelLimits.MaxIconPx);
                break;
            case BlockKind.Section:
                if (depth >= PanelLimits.MaxDepth) throw new Refusal($"sections nest at most {PanelLimits.MaxDepth} deep");
                b.Kids = Blocks(e, "blocks", depth + 1, ref count, st);
                break;
            case BlockKind.Footer:
                b.Right = Runs(e, "right", false, st);
                b.Runs = Runs(e, "left", false, st);
                break;
            case BlockKind.Status:
                b.IconPx = Math.Clamp(Num(e, "px", st.StatusPx), PanelLimits.MinIconPx, PanelLimits.MaxIconPx);
                break;
        }
        return b;
    }

    private static float? Opt(JsonElement e, string name, float lo)
        => e.TryGetProperty(name, out _) ? Math.Clamp(Num(e, name, 0f), lo, PanelLimits.MaxLen) : null;

    private static Run[] Runs(JsonElement e, string name, bool italic, PanelStyle st)
    {
        string s = Str(e, name, "");
        if (!PanelMarkup.TryParse(s, out Run[] runs, out string why, st)) throw new Refusal($"{name}: {why}");
        if (italic) for (int i = 0; i < runs.Length; i++) runs[i] = new Run(runs[i].Text, runs[i].Icon, runs[i].Rgba, runs[i].Size, true, runs[i].Break);
        return runs;
    }

    private static string Str(JsonElement e, string name, string def)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return def;
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new Refusal($"{name} is a string");
    }

    private static float Num(JsonElement e, string name, float def)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return def;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out float f) || !float.IsFinite(f)) throw new Refusal($"{name} is a number");
        return f;
    }

    private static bool Bool(JsonElement e, string name, bool def)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return def;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new Refusal($"{name} is true or false");
    }

    private static uint Col(JsonElement e, string name, uint def) => e.TryGetProperty(name, out JsonElement v) ? ColorOf(v, name) : def;

    private static uint ColorOf(JsonElement v, string name)
    {
        if (v.ValueKind != JsonValueKind.String || !TryColor(v.GetString()!, out uint c)) throw new Refusal($"{name} is a colour like #RRGGBB or #RRGGBBAA");
        return c;
    }

    /// <summary><c>#RRGGBB</c> (opaque) or <c>#RRGGBBAA</c> as 0xRRGGBBAA.</summary>
    public static bool TryColor(string s, out uint rgba) => HexColour.TryParse(s, out rgba);
}
