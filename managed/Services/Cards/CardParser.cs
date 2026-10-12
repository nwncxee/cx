using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CX.Services.Cards;

/// <summary>
/// Reads one policy value, a JSON card or array of cards (<c>docs/ui-policy-contract.md</c>), into <see cref="CardDecl"/>s.
/// A field it does not know is ignored, so a newer server can send more than an older client reads; a field it does know
/// with the wrong type, a bad colour or a value over a <see cref="CardLimits"/> entry refuses the whole declaration.
/// Cold: it allocates.
/// </summary>
internal static class CardParser
{
    private sealed class Refusal(string why) : Exception(why);

    /// <summary>The cards in <paramref name="json"/> (none for an empty array, which declares that the key has no card), or false with the reason the declaration was refused.</summary>
    public static bool TryParse(string json, out CardDecl[] cards, out string error)
    {
        cards = [];
        error = "";
        if (Encoding.UTF8.GetByteCount(json) > CardLimits.MaxJsonBytes) { error = $"over {CardLimits.MaxJsonBytes} bytes"; return false; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = doc.RootElement;
            var list = new List<CardDecl>();
            if (root.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in root.EnumerateArray()) list.Add(Card(e));
            else list.Add(Card(root));
            if (list.Count > CardLimits.MaxCards || (list.Count == 0 && root.ValueKind != JsonValueKind.Array)) throw new Refusal($"{list.Count} cards (1-{CardLimits.MaxCards} allowed)");
            cards = list.ToArray();
            return true;
        }
        catch (Refusal r) { error = r.Message; }
        catch (JsonException j) { error = "not JSON: " + j.Message; }
        return false;
    }

    private static CardDecl Card(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new Refusal("a card is a JSON object");
        var c = new CardDecl();
        c.Width = Math.Clamp(Num(e, "width", c.Width), CardLimits.MinWidth, CardLimits.MaxWidth);
        c.Border = Col(e, "border", 0);
        c.Tab = Str(e, "tab", "");
        c.TabRgba = Col(e, "tabcolor", 0x000000B0);
        c.Item = Str(e, "item", "");
        if (e.TryGetProperty("bg", out JsonElement bg))
        {
            if (bg.ValueKind == JsonValueKind.String) c.BgFrom = c.BgTo = ColorOf(bg, "bg");
            else if (bg.ValueKind == JsonValueKind.Object)
            {
                c.BgFrom = Col(bg, "from", 0);
                c.BgTo = Col(bg, "to", c.BgFrom);
                c.Horizontal = Str(bg, "dir", "v") == "h";
            }
            else throw new Refusal("bg is a colour or {from, to, dir}");
        }
        int count = 0;
        c.Blocks = Blocks(e, "blocks", 0, ref count);
        if (e.TryGetProperty("images", out JsonElement imgs))
        {
            if (imgs.ValueKind != JsonValueKind.Array || imgs.GetArrayLength() > CardLimits.MaxImages) throw new Refusal($"images is an array of at most {CardLimits.MaxImages}");
            var l = new List<CardImage>();
            foreach (JsonElement i in imgs.EnumerateArray())
            {
                string src = Str(i, "src", "");
                if (src.Length is 0 or > CardLimits.MaxIconName) throw new Refusal("an image needs a src");
                l.Add(new CardImage
                {
                    Src = src,
                    W = Math.Clamp(Num(i, "w", 64f), 1f, CardLimits.MaxImagePx), H = Math.Clamp(Num(i, "h", 64f), 1f, CardLimits.MaxImagePx),
                    Dx = Math.Clamp(Num(i, "dx", 0f), -CardLimits.MaxImagePx, CardLimits.MaxImagePx),
                    Dy = Math.Clamp(Num(i, "dy", 0f), -CardLimits.MaxImagePx, CardLimits.MaxImagePx),
                    At = Str(i, "at", "tr") switch { "tl" => Anchor.TopLeft, "bl" => Anchor.BottomLeft, "br" => Anchor.BottomRight, "tr" => Anchor.TopRight, var a => throw new Refusal($"image anchor '{a}' is tl, tr, bl or br") },
                });
            }
            c.Images = l.ToArray();
        }
        return c;
    }

    private static Block[] Blocks(JsonElement parent, string name, int depth, ref int count)
    {
        if (!parent.TryGetProperty(name, out JsonElement arr)) return [];
        if (arr.ValueKind != JsonValueKind.Array) throw new Refusal($"{name} is an array");
        var l = new List<Block>();
        foreach (JsonElement b in arr.EnumerateArray())
        {
            if (++count > CardLimits.MaxBlocks) throw new Refusal($"more than {CardLimits.MaxBlocks} blocks");
            l.Add(Block(b, depth, ref count));
        }
        return l.ToArray();
    }

    /// <summary>The opacity <c>dim</c> gives a colour: 60%.</summary>
    private const uint DimAlpha = 0x99;   // not-an-offset: an alpha byte

    private static Block Block(JsonElement e, int depth, ref int count)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new Refusal("a block is a JSON object");
        string type = Str(e, "type", "text");
        var b = new Block { Kind = type switch
        {
            "status" => BlockKind.Status, "divider" => BlockKind.Divider, "icons" => BlockKind.Icons, "section" => BlockKind.Section,
            "footer" => BlockKind.Footer, _ => BlockKind.Text,
        } };
        b.Size = CardLimits.Snap(Num(e, "size", 1f));
        b.Color = Col(e, "color", 0);
        if (Bool(e, "dim", false)) b.Color = (b.Color != 0 ? b.Color & 0xFFFFFF00 : 0xFFFFFF00) | DimAlpha;
        b.Runs = Runs(e, "text", Bool(e, "italic", false));
        b.Icon = Str(e, "icon", "");
        if (b.Icon.Length > CardLimits.MaxIconName) throw new Refusal("an icon name is too long");
        b.Frame = Bool(e, "frame", false);
        b.Pulse = Bool(e, "pulse", type == "loading");
        b.Tint = Col(e, "tint", 0);
        switch (b.Kind)
        {
            case BlockKind.Icons:
                if (!e.TryGetProperty("icons", out JsonElement ics) || ics.ValueKind != JsonValueKind.Array || ics.GetArrayLength() > CardLimits.MaxIcons)
                    throw new Refusal($"icons is an array of at most {CardLimits.MaxIcons} names");
                var names = new List<string>();
                foreach (JsonElement n in ics.EnumerateArray())
                {
                    string s = n.ValueKind == JsonValueKind.String ? n.GetString()! : throw new Refusal("an icon name is a string");
                    if (s.Length > CardLimits.MaxIconName) throw new Refusal("an icon name is too long");
                    names.Add(s);
                }
                b.Icons = names.ToArray();
                b.IconPx = Math.Clamp(Num(e, "px", 28f), CardLimits.MinIconPx, CardLimits.MaxIconPx);
                break;
            case BlockKind.Section:
                if (depth >= CardLimits.MaxDepth) throw new Refusal($"sections nest at most {CardLimits.MaxDepth} deep");
                b.Kids = Blocks(e, "blocks", depth + 1, ref count);
                break;
            case BlockKind.Footer:
                b.Right = Runs(e, "right", false);
                b.Runs = Runs(e, "left", false);
                break;
            case BlockKind.Status:
                b.IconPx = Math.Clamp(Num(e, "px", 22f), CardLimits.MinIconPx, CardLimits.MaxIconPx);
                break;
        }
        return b;
    }

    private static Run[] Runs(JsonElement e, string name, bool italic)
    {
        string s = Str(e, name, "");
        if (!CardMarkup.TryParse(s, out Run[] runs, out string why)) throw new Refusal($"{name}: {why}");
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
    public static bool TryColor(string s, out uint rgba)
    {
        rgba = 0;
        if (s.Length is not (7 or 9) || s[0] != '#') return false;
        if (!uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
        rgba = s.Length == 7 ? v << 8 | 0xFF : v;
        return true;
    }
}
