using System;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>What a held-image value names.</summary>
internal enum HeldKind
{
    /// <summary>Nothing is held.</summary>
    None,
    /// <summary>A texture resref.</summary>
    Resref,
    /// <summary><c>@item:&lt;hex&gt;</c>, an item the client knows.</summary>
    Item,
    /// <summary><c>@look:&lt;hex&gt;</c>, an item described by its appearance.</summary>
    Look,
    /// <summary><c>@model:&lt;key&gt;</c>, a live model view.</summary>
    Model,
    /// <summary>Held, but a name this build cannot draw (a malformed name, a model view with no key): the cursor swaps, no image shows.</summary>
    Other,
}

/// <summary>
/// The show, hide and clear rules for the held image, with no engine in them. A value shows its image, an empty one
/// hides it, and a hold clears itself once NUI has drawn no widget for <see cref="GraceMs"/> or the window it began over is
/// gone (<see cref="Release"/>). A <see cref="Pick"/> shows pending until a <c>held.image</c> line answers or <c>held.confirm</c> ms pass.
/// </summary>
internal sealed class HeldImageModel
{
    /// <summary>How long NUI may draw nothing before the hold is dropped; 0 never drops it. The server's <c>held.grace</c>.</summary>
    internal int GraceMs = DefaultGraceMs;
    internal const int DefaultGraceMs = 500;
    private const int ResrefMax = 16, HexMax = 8;

    public HeldKind Kind { get; private set; }
    /// <summary>The value as sent.</summary>
    public string Name { get; private set; } = "";
    /// <summary>The item id for <see cref="HeldKind.Item"/>, as the server's <c>ObjectToString</c> reads.</summary>
    public uint Oid { get; private set; }
    /// <summary>The model view's key for <see cref="HeldKind.Model"/>, else empty.</summary>
    public string ModelKey { get; private set; } = "";
    public bool Active => Kind != HeldKind.None;
    /// <summary>True while the shown image is a pick the server has not answered.</summary>
    public bool Pending { get; private set; }

    private int _epoch = -1, _pickStamp;
    private bool _enabled = true;
    private long _drawn, _drawnAt, _pickAt, _confirmMs;

    /// <summary>True when the policy epoch or the knob moved since the last <see cref="Update"/> that read the value.</summary>
    public bool Stale(int epoch, bool enabled) => epoch != _epoch || enabled != _enabled;

    /// <summary>
    /// One frame. <paramref name="value"/> is the key's value when <see cref="Stale"/> said so, else null.
    /// <paramref name="drawn"/> is the NUI widget-draw counter and <paramref name="counting"/> says it is wired.
    /// True when what is held changed.
    /// </summary>
    public bool Update(int epoch, bool enabled, string? value, long drawn, bool counting, long nowMs, int stamp = 0)
    {
        if (value != null)
        {
            _epoch = epoch;
            if (Pending && enabled && _enabled && stamp == _pickStamp) return false;
            _enabled = enabled;
            Pending = false;
            bool changed = Set(enabled ? value : "");
            _drawn = drawn;
            _drawnAt = nowMs;
            return changed;
        }
        if (Pending && nowMs - _pickAt >= _confirmMs) { Pending = false; Set(""); return true; }
        if (!Active || !counting) return false;
        if (drawn != _drawn) { _drawn = drawn; _drawnAt = nowMs; return false; }
        if (GraceMs <= 0 || nowMs - _drawnAt < GraceMs) return false;
        Set("");
        return true;
    }

    /// <summary>
    /// The player pressed an element that declared <c>held.pick</c>: show <paramref name="image"/> now, pending. <paramref name="stamp"/> is
    /// <see cref="ServerPolicy.Stamp"/> of <c>held.image</c> at the press, so only a line for that key answers it. True when what is held changed.
    /// </summary>
    public bool Pick(string image, long nowMs, int confirmMs, int stamp, long drawn)
    {
        bool changed = Set(image);
        Pending = Active;
        _pickAt = nowMs; _confirmMs = confirmMs; _pickStamp = stamp;
        _drawn = drawn; _drawnAt = nowMs;
        return changed;
    }

    /// <summary>The window the hold came from is gone: drop it now. True when something was held.</summary>
    public bool Release()
    {
        bool was = Active;
        Pending = false;
        Set("");
        return was;
    }

    private bool Set(string value)
    {
        HeldKind kind = Classify(value, out uint oid);
        bool changed = kind != Kind || value != Name;
        Kind = kind;
        Name = kind == HeldKind.None ? "" : value;
        Oid = oid;
        ModelKey = kind == HeldKind.Model ? value[NuiModelView.Prefix.Length..] : "";
        return changed;
    }

    /// <summary>The kind of <paramref name="value"/>, and the item id when it is an <c>@item:</c>.</summary>
    internal static HeldKind Classify(string value, out uint oid)
    {
        oid = 0;
        if (value.Length == 0) return HeldKind.None;
        if (value.StartsWith("@item:", StringComparison.Ordinal))
        {
            int n = 0;
            for (int i = 6; i < value.Length && n < HexMax; i++, n++)
            {
                int d = Hex(value[i]);
                if (d < 0) break;
                oid = oid << 4 | (uint)d;
            }
            return n > 0 ? HeldKind.Item : HeldKind.Other;
        }
        if (value.StartsWith("@look:", StringComparison.Ordinal)) return HeldKind.Look;
        if (value.StartsWith(NuiModelView.Prefix, StringComparison.Ordinal)) return value.Length > NuiModelView.Prefix.Length ? HeldKind.Model : HeldKind.Other;
        if (value.Length > ResrefMax) return HeldKind.Other;
        foreach (char c in value)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return HeldKind.Other;
        return HeldKind.Resref;
    }

    private static int Hex(char c) => c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : -1;

    /// <summary>Oracle: hand-written names and values, and a hand-driven clock and draw counter.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var m = new HeldImageModel();
        check.Expect(m.Stale(0, true) && !m.Active, "a new model is stale and holds nothing");

        check.Expect(m.Update(1, true, "iit_belt", 10, true, 1000) && m.Kind == HeldKind.Resref && m.Name == "iit_belt", "a resref shows");
        check.Expect(!m.Stale(1, true) && m.Stale(2, true) && m.Stale(1, false), "an epoch or knob move makes it stale");
        check.Expect(!m.Update(1, true, null, 11, true, 1400) && m.Active, "NUI still drawing keeps the hold");
        check.Expect(!m.Update(1, true, null, 11, true, 1899) && m.Active, "a quiet NUI is forgiven inside the grace");
        check.Expect(m.Update(1, true, null, 11, true, 1900) && !m.Active, "a quiet NUI past the grace drops the hold");
        var keep = new HeldImageModel { GraceMs = 0 };
        keep.Update(1, true, "iit_belt", 10, true, 1000);
        keep.Update(1, true, null, 11, true, 1400);
        check.Expect(!keep.Update(1, true, null, 11, true, 61_400) && keep.Active, "grace 0 keeps a hold through a minute of quiet NUI");

        m.Update(2, true, "iit_belt", 11, true, 2000);
        check.Expect(m.Active, "the same value sent again shows again after a drop");
        m.Update(3, true, "", 11, true, 2100);
        check.Expect(!m.Active && m.Name == "", "an empty value hides");

        m.Update(4, true, "@item:7F00a1ff2", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Item && m.Oid == 0x7f00a1ff, "@item: reads up to eight hex digits");
        m.Update(5, true, "@item:zz", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Other, "@item: with no digits is held but not drawable");
        m.Update(6, true, "@look:0010", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Look, "@look: is a look");
        m.Update(7, true, "@model:doll", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Model && m.Active && m.ModelKey == "doll", "a model view name is a model view, keyed by what follows the prefix");
        m.Update(7, true, "@model:", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Other && m.Active, "a model view with no key is held but not drawable");
        check.Expect(m.ModelKey == "", "a model view with no key has none");
        m.Update(8, true, "has space", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Other, "a malformed resref is not drawn");
        m.Update(9, true, "abcdefghijklmnopq", 11, true, 2200);
        check.Expect(m.Kind == HeldKind.Other, "a resref over 16 characters is not drawn");

        m.Update(10, true, "iit_belt", 11, true, 3000);
        m.Update(10, false, "iit_belt", 11, true, 3010);
        check.Expect(!m.Active, "the knob off hides what the server sent");
        m.Update(11, true, "iit_belt", 11, false, 3020);
        check.Expect(!m.Update(11, true, null, 11, false, 99999) && m.Active, "with no NUI counter wired the hold never times out");
        m.Update(12, true, "iit_belt", 11, true, 4000);
        check.Expect(m.Release() && !m.Active && !m.Release(), "a release drops the hold once");
        m.Pick("iit_belt", 4100, 1000, 0, 11);
        check.Expect(m.Release() && !m.Pending && !m.Active, "a release also drops a pending pick");
        log.Info(check.Ok ? "HeldImageModel.SelfCheck: passed" : "HeldImageModel.SelfCheck: FAILED");
    }
}
