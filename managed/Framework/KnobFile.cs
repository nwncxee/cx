using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CX.Framework;

/// <summary>
/// The one <c>cx.cfg</c> writer. Every set arms a trailing debounce; when it fires, a pool thread walks the
/// registry, writes <c>cx.cfg.tmp</c> beside <see cref="Settings.Path"/> and renames it over the file. Content:
/// the fixed header, then off-default declared keys, every <see cref="KnobBag"/> pair and any key no declaration
/// claims, one alphabetical list. Last writer wins; the render thread never comes here.
/// </summary>
public static class KnobFile
{
    /// <summary>Trailing debounce: a slider drag is one write, after it stops.</summary>
    public const int DebounceMs = 1000;
    /// <summary>The header. No <c>=</c> in it: <see cref="ConfigFile.ForEachPair"/> would read one as a key.</summary>
    public const string Header = "# cx.cfg -- written by CX; comments are not preserved; edit with the client closed";

    private static readonly Timer _timer = new(_ => Write(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly BootWriteHold _hold = new();
    private static int _writes;
    private static int _fires;

    /// <summary>Writes so far; the "count it" for a slider drag.</summary>
    public static int Writes => _writes;

    /// <summary>Debounces that fired, counted before the write itself: an arm that reached the timer shows here
    /// even where the write then stands down.</summary>
    public static int Fires => _fires;

    /// <summary>True until <see cref="BootEnded"/>.</summary>
    public static bool Holding => _hold.Holding;

    /// <summary>Arms the boot hold dropped: a boot set is nobody's choice, so the file keeps what it was read
    /// from.</summary>
    public static int HeldArms => _hold.HeldArms;

    /// <summary>Restart the debounce. Cold: called once per set on the setter's thread. Held during boot.</summary>
    public static void Arm()
    {
        if (_hold.Arm()) _timer.Change(DebounceMs, Timeout.Infinite);
    }

    /// <summary>A first run has no text to keep: the one arm that outlives the boot hold.</summary>
    public static void ArmFirstRun() => _hold.ArmFirstRun();

    /// <summary>Boot is over: name what the hold dropped, then honour a first run's missing file. Idempotent.</summary>
    public static void BootEnded(ExtenderLog? log)
    {
        if (!_hold.Holding) return;
        bool firstRun = _hold.End();
        log?.Info($"settings: boot held {_hold.HeldArms} write arm(s) -- {Settings.Path} left as read");
        if (firstRun) Arm();
    }

    /// <summary>The file's text from the live registry and the loaded unknown keys.</summary>
    public static string Compose() => Compose(out _);

    private static string Compose(out int keys)
    {
        var lines = new List<string>();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bags = new List<KnobBag>();
        foreach (KnobGroup g in KnobGroup.All)
        {
            foreach (Knob k in g.Knobs)
            {
                if (!k.Persisted) continue;
                declared.Add(k.Key);
                if (!k.StoredIsDefault) lines.Add(k.Key + "=" + k.FormatStored());
            }
            foreach (KnobBag b in g.Bags)
            {
                bags.Add(b);
                foreach (var kv in b.Pairs) lines.Add(g.Name + "." + kv.Key + "=" + kv.Value);
            }
        }
        foreach (var kv in Settings.Pairs)
            if (!declared.Contains(kv.Key) && !kv.Key.StartsWith('#') && !Bagged(bags, kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
        lines.Sort(string.CompareOrdinal);
        keys = lines.Count;
        var sb = new StringBuilder(Header).Append('\n');
        foreach (string l in lines) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    private static bool Bagged(List<KnobBag> bags, string key)
    {
        for (int i = 0; i < bags.Count; i++) if (bags[i].Owns(key)) return true;
        return false;
    }

    /// <summary>The timer's callback, on a pool thread: tmp + rename. An IO failure is logged and swallowed;
    /// a config write must never take the client down.</summary>
    private static void Write()
    {
        Interlocked.Increment(ref _fires);
        string path = Settings.Path;
        if (path.Length == 0) { KnobGroup.Log?.Info("settings: no file path latched -- write skipped"); return; }
        if (Native.Sym(SymbolMap.AppManager) == IntPtr.Zero)
        { KnobGroup.Log?.Info($"settings: off-device -- {path} is the real user's, write skipped"); return; }
        try
        {
            string text = Compose(out int keys);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            int n = Interlocked.Increment(ref _writes);
            KnobGroup.Log?.Info($"settings: wrote {path} (write #{n}, {keys} key(s))");
        }
        catch (Exception e) { KnobGroup.Log?.Info($"settings: write of {path} failed: {e.Message}"); }
    }

    /// <summary>Off-device: the composed text carries the header, no declared-at-default key, and every
    /// unknown loaded key verbatim. Never writes: the demo's file is the real user's.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        string text = Compose();
        check.Expect(text.StartsWith(Header + "\n"), "the header comes first");
        check.Expect(!Header.Contains('='), "the header reads back as no key");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bags = new List<KnobBag>();
        foreach (KnobGroup g in KnobGroup.All)
        {
            foreach (Knob k in g.Knobs)
            {
                if (k.Persisted) declared.Add(k.Key);
                check.Expect(k.StoredIsDefault == !text.Contains("\n" + k.Key + "=") || !k.Persisted, $"{k.Key}: present iff off-default");
            }
            foreach (KnobBag b in g.Bags)
            {
                bags.Add(b);
                foreach (var kv in b.Pairs)
                    check.Expect(text.Contains("\n" + g.Name + "." + kv.Key + "=" + kv.Value + "\n"), $"bag pair {kv.Key} written verbatim");
            }
        }
        foreach (var kv in Settings.Pairs)
            if (!declared.Contains(kv.Key) && !kv.Key.StartsWith('#') && !Bagged(bags, kv.Key))
                check.Expect(text.Contains("\n" + kv.Key + "=" + kv.Value + "\n"), $"unknown key {kv.Key} preserved verbatim");
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 2; i < lines.Length; i++)
            check.Expect(string.CompareOrdinal(lines[i - 1], lines[i]) <= 0, "keys are alphabetical");
        check.Expect(Writes == 0, "nothing wrote during boot");
        check.Expect(Fires == 0, "...and no debounce even fired");
        check.Expect(Holding, "the writer still holds at the end of boot");

        log.Info(check.Ok ? "KnobFile.SelfCheck: passed" : "KnobFile.SelfCheck: FAILED");
    }
}
