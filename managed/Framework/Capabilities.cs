using System.Globalization;

namespace CX.Framework;

/// <summary>
/// What the extender tells a server at login beyond <c>cx_enabled</c>: <c>cx_version</c> and one device property per
/// live capability, <c>cx_cap_&lt;name&gt;</c>, valued by that capability's version. A feature registers its entry
/// once; the entry is advertised only while its predicate says its hooks resolved and its knob is on.
/// </summary>
public static class Capabilities
{
    /// <summary>The release as one integer: major * 1,000,000 + minor * 1,000 + patch. 0 when the build carries no version.</summary>
    public const string VersionProperty = "cx_version";
    /// <summary>Every capability property starts with this; the rest is its name.</summary>
    public const string Prefix = "cx_cap_";

    private sealed class State
    {
        public readonly List<(string Name, int Version, Func<bool> Live)> Entries = [];
        public Action<string, int>? Send;
        public bool LoggedIn;
    }

    private static State _s = new();

    /// <summary>Adds one capability. <paramref name="live"/> is read at every advertise.</summary>
    public static void Register(string name, int version, Func<bool> live) => _s.Entries.Add((name, version, live));

    /// <summary>Packs <c>v0.7.0-12-gabc</c> or <c>0.7.0</c> into <c>7000</c>; anything unparsable is 0.</summary>
    public static int PackVersion(string v)
    {
        string[] parts = v.TrimStart('v', 'V').Split('-')[0].Split('.');
        int total = 0, mul = 1_000_000;
        for (int i = 0; i < 3; i++, mul /= 1000)
        {
            if (i >= parts.Length) break;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > 999) return 0;
            total += n * mul;
        }
        return total;
    }

    /// <summary>The properties a login sends: the version, then each live capability. A stood-down one is absent.</summary>
    public static List<(string Name, int Value)> Collect(string version) => Build(version, false);

    private static List<(string Name, int Value)> Build(string version, bool withdrawn)
    {
        var l = new List<(string, int)> { (VersionProperty, PackVersion(version)) };
        foreach (var e in _s.Entries)
        {
            if (e.Live()) l.Add((Prefix + e.Name, e.Version));
            else if (withdrawn) l.Add((Prefix + e.Name, 0));
        }
        return l;
    }

    /// <summary>Sends the login set through <paramref name="send"/> and keeps it for <see cref="Refresh"/>.</summary>
    public static void Advertise(Action<string, int> send)
    {
        _s.Send = send;
        _s.LoggedIn = true;
        foreach (var (n, v) in Collect(Product.Version)) send(n, v);
    }

    /// <summary>A knob or hook changed what is live: sends the whole set again, a stood-down capability as 0.</summary>
    public static void Refresh()
    {
        if (!_s.LoggedIn || _s.Send == null) return;
        foreach (var (n, v) in Build(Product.Version, true)) _s.Send(n, v);
    }

    /// <summary>Oracle: hand-written literals for version packing and the expected property lists.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(PackVersion("v0.7.0-12-gabc1234") == 7000 && PackVersion("1.12.3") == 1_012_003, "versions pack major/minor/patch");
        check.Expect(PackVersion("unknown") == 0 && PackVersion("") == 0 && PackVersion("2.1") == 2_001_000, "no version is 0, a short one pads");
        var saved = _s;
        _s = new();
        bool hookOk = true, knobOn = true;
        Register("itemicon", 1, () => hookOk && knobOn);
        Register("other", 3, () => true);
        var sent = new List<string>();
        void Send(string n, int v) => sent.Add(n + "=" + v);
        string Joined(List<(string Name, int Value)> l) => string.Join(",", l.Select(p => p.Name + "=" + p.Value));
        check.Expect(Joined(Collect("0.7.0")) == "cx_version=7000,cx_cap_itemicon=1,cx_cap_other=3", "hook on, knob on: both listed");
        knobOn = false;
        check.Expect(Joined(Collect("0.7.0")) == "cx_version=7000,cx_cap_other=3", "knob off: the capability is left out");
        knobOn = true; hookOk = false;
        check.Expect(Joined(Collect("0.7.0")) == "cx_version=7000,cx_cap_other=3", "hook stood down: the capability is left out");
        Refresh();
        check.Expect(sent.Count == 0, "no refresh before a login");
        Advertise(Send);
        check.Expect(string.Join(",", sent) == $"cx_version={PackVersion(Product.Version)},cx_cap_other=3", "login sends only what is live");
        sent.Clear(); hookOk = true; Refresh();
        check.Expect(string.Join(",", sent) == $"cx_version={PackVersion(Product.Version)},cx_cap_itemicon=1,cx_cap_other=3", "refresh after a hook recovers");
        sent.Clear(); knobOn = false; Refresh();
        check.Expect(string.Join(",", sent) == $"cx_version={PackVersion(Product.Version)},cx_cap_itemicon=0,cx_cap_other=3", "refresh withdraws a capability as 0");
        _s = saved;
        log.Info(check.Ok ? "Capabilities.SelfCheck: passed" : "Capabilities.SelfCheck: FAILED");
    }
}
