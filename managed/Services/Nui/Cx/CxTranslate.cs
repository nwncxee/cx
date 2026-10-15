using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>One policy key a window's <c>"cx"</c> members set. <see cref="Bind"/> names the NUI bind that supplies the value
/// (<see cref="Value"/> is then empty until the server sets it).</summary>
internal sealed record CxSet(string Key, string Value, string? Bind);

/// <summary>What the translation of one window's JSON found: the keys to set and the members it could not use.</summary>
internal sealed class CxPlan
{
    public readonly List<CxSet> Sets = new();
    public readonly List<PolicyRefusal> Refused = new();
}

/// <summary>
/// Turns the <c>"cx"</c> members of a NUI window's JSON into the policy keys the cards, model views, held images and window
/// render properties already read (<c>docs/ui-policy-contract.md</c>), so none of those readers knows where a key came from.
/// Pure: text in, a plan out.
/// </summary>
internal static class CxTranslate
{
    /// <summary>The member that carries the settings, on the window object or on any element.</summary>
    internal const string Member = "cx";

    /// <summary>The plan for window <paramref name="windowId"/> whose JSON is <paramref name="json"/>.</summary>
    internal static CxPlan Translate(string windowId, string json)
    {
        var plan = new CxPlan();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return plan;
            if (root.TryGetProperty(Member, out JsonElement wcx)) Window(plan, windowId, wcx);
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name != Member) Walk(plan, p.Value);
        }
        catch (JsonException) { Refuse(plan, "cx", json.Length > 64 ? json[..64] : json, "the window JSON could not be read"); }
        return plan;
    }

    private static void Walk(CxPlan plan, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in e.EnumerateArray()) Walk(plan, c);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty p in e.EnumerateObject())
            if (p.Name == Member) Element(plan, e, p.Value);
            else Walk(plan, p.Value);
    }

    private static void Window(CxPlan plan, string windowId, JsonElement cx)
    {
        if (cx.ValueKind != JsonValueKind.Object) { Refuse(plan, "cx", cx.GetRawText(), "the window's cx member must be an object"); return; }
        foreach (JsonProperty p in cx.EnumerateObject())
        {
            switch (p.Name)
            {
                case "cardstyle":
                    if (p.Value.ValueKind == JsonValueKind.Object) plan.Sets.Add(new CxSet("cardstyle", p.Value.GetRawText(), null));
                    else Refuse(plan, "cardstyle", p.Value.GetRawText(), "cardstyle must be an object");
                    break;
                case "held":
                    if (p.Value.ValueKind != JsonValueKind.Object) { Refuse(plan, "held", p.Value.GetRawText(), "held must be an object"); break; }
                    foreach (JsonProperty h in p.Value.EnumerateObject()) Scalar(plan, "held." + h.Name, h.Value);
                    break;
                default:
                    Scalar(plan, CX.Alloy.NuiWindows.Key(windowId, p.Name), p.Value);
                    break;
            }
        }
    }

    private static void Element(CxPlan plan, JsonElement owner, JsonElement cx)
    {
        if (cx.ValueKind != JsonValueKind.Object) { Refuse(plan, "cx", cx.GetRawText(), "an element's cx member must be an object"); return; }
        string? id = owner.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String ? idE.GetString() : null;
        if (string.IsNullOrEmpty(id)) id = null;
        foreach (JsonProperty p in cx.EnumerateObject())
        {
            switch (p.Name)
            {
                case "model": Model(plan, owner, p.Value); break;
                case "card":
                    if (id == null) Refuse(plan, "card", "", "a card needs the element to have an id");
                    else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) plan.Sets.Add(new CxSet("card." + id, p.Value.GetRawText(), null));
                    else Refuse(plan, "card." + id, p.Value.GetRawText(), "card must be a card object or an array of them");
                    break;
                case "pick":
                    if (id == null) Refuse(plan, "pick", "", "a pick needs the element to have an id");
                    else Scalar(plan, "held.pick." + id, p.Value);
                    break;
                default: Refuse(plan, "cx." + p.Name, p.Value.GetRawText(), "not a cx member an element reads"); break;
            }
        }
    }

    private static void Model(CxPlan plan, JsonElement owner, JsonElement settings)
    {
        string? key = ModelKey(owner);
        if (key == null) { Refuse(plan, "model", "", "the element names no @model:<key> image"); return; }
        if (settings.ValueKind != JsonValueKind.Object) { Refuse(plan, "model." + key, settings.GetRawText(), "model must be an object"); return; }
        foreach (JsonProperty p in settings.EnumerateObject()) Scalar(plan, "model." + key + "." + p.Name, p.Value);
    }

    /// <summary>The key of the first <c>@model:</c> image the element names, in its own members or one object down.</summary>
    internal static string? ModelKey(JsonElement owner)
    {
        foreach (JsonProperty p in owner.EnumerateObject())
        {
            if (p.Name == Member) continue;
            if (p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty q in p.Value.EnumerateObject())
                    if (Key(q.Value) is { } k) return k;
            }
            else if (Key(p.Value) is { } k) return k;
        }
        return null;
    }

    private static string? Key(JsonElement v) => v.ValueKind == JsonValueKind.String ? ImageNames.ModelKey(v.GetString() ?? "") : null;

    /// <summary>One setting: a number, text, a switch, a list of numbers (joined with commas) or a NUI bind.</summary>
    private static void Scalar(CxPlan plan, string key, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                plan.Sets.Add(new CxSet(key, BoundText(v), null));
                return;
            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (JsonElement c in v.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Number) { Refuse(plan, key, v.GetRawText(), "a list holds numbers only"); return; }
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(c.GetRawText());
                }
                plan.Sets.Add(new CxSet(key, sb.ToString(), null));
                return;
            }
            case JsonValueKind.Object:
                if (v.TryGetProperty("bind", out JsonElement b) && b.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(b.GetString()))
                {
                    plan.Sets.Add(new CxSet(key, "", b.GetString()));
                    return;
                }
                break;
        }
        Refuse(plan, key, v.GetRawText(), "a setting is a number, text, a list of numbers or {\"bind\": name}");
    }

    /// <summary>A bound value as the policy line text: numbers as written, switches as 1 and 0, text as it is, null as released.</summary>
    internal static string BoundText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => "",
    };

    private static void Refuse(CxPlan plan, string key, string value, string reason)
        => plan.Refused.Add(new PolicyRefusal(0, PolicyRefusals.KindOf(key), key, value, reason));

    /// <summary>Oracle: hand-written window JSON in the shape NWScript's <c>NuiWindow</c> builds, and the keys the contract documents for it.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        CxPlan p = Translate("w1", """
            {"type":"window","root":{"type":"col","children":[
              {"type":"image","value":"@model:doll","cx":{"model":{"target":"player","drag":1,"yaw":{"bind":"doll_yaw"},"rot":[0,90,0],"anim":true}}},
              {"type":"label","id":"tip","cx":{"card":[{"width":300}],"pick":"@item:1f"}}]},
              "cx":{"background":0,"fadein":150,"cardstyle":{"pad":4},"held":{"size":40}}}
            """);
        string Get(string k) => p.Sets.Find(s => s.Key == k)?.Value ?? "<none>";
        check.Expect(p.Refused.Count == 0, "a valid window refuses nothing: " + string.Join("|", p.Refused.ConvertAll(r => r.Key + ":" + r.Reason)));
        check.Expect(Get("nui" + ".w1.background") == "0" && Get("nui" + ".w1.fadein") == "150", "window members become nui.<id>.<member>");
        check.Expect(Get("cardstyle") == "{\"pad\":4}" && Get("held.size") == "40", "cardstyle and held members map to their documented keys");
        check.Expect(Get("model.doll.target") == "player" && Get("model.doll.drag") == "1" && Get("model.doll.rot") == "0,90,0" && Get("model.doll.anim") == "1",
                     "model members become model.<key>.<member> with the key from the image resref");
        check.Expect(p.Sets.Exists(s => s.Key == "model.doll.yaw" && s.Bind == "doll_yaw"), "a {bind} value is a bound setting");
        check.Expect(Get("card.tip") == "[{\"width\":300}]" && Get("held.pick.tip") == "@item:1f", "card and pick key by the element id");

        CxPlan bad = Translate("w2", """
            {"root":{"type":"label","cx":{"card":{},"model":{"yaw":1},"bogus":1,"pick":"x"}},"cx":{"opacity":{"x":1}}}
            """);
        check.Expect(bad.Sets.Count == 0 && bad.Refused.Count == 5, "no id, no image, an unknown member and an unusable value are all refused, none set: " + bad.Refused.Count);
        check.Expect(bad.Refused.Exists(r => r.Key == "nui" + ".w2.opacity" && r.Kind == RefusalKind.Window), "a refused window member is a window refusal");
        check.Expect(Translate("w3", "{not json").Refused.Count == 1, "unreadable JSON is one refusal");
        check.Expect(Translate("w4", "{\"root\":{\"type\":\"spacer\"}}").Sets.Count == 0, "a window with no cx member sets nothing");
        log.Info(check.Ok ? "CxTranslate.SelfCheck: passed" : "CxTranslate.SelfCheck: FAILED");
    }
}
