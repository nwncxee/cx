using System.Runtime.CompilerServices;

namespace CX.Framework;

/// <summary>The one self-check assert: a failed expectation flips <see cref="Ok"/> and logs
/// "<c>{prefix} FAIL: {what}</c>". The prefix defaults to the calling file's stem up to its first dot plus
/// ".SelfCheck" (so a partial-class file names its class); a suite that prints another name passes it.</summary>
public sealed class Checker
{
    public bool Ok = true;
    private readonly ExtenderLog _log;
    private readonly string _prefix;

    public Checker(ExtenderLog log, string? prefix = null, [CallerFilePath] string file = "")
    {
        _log = log;
        if (prefix == null)
        {
            int start = file.LastIndexOfAny(['/', '\\']) + 1;
            int end = file.IndexOf('.', start);
            prefix = (end < 0 ? file[start..] : file[start..end]) + ".SelfCheck";
        }
        _prefix = prefix;
    }

    public void Expect(bool cond, string what)
    {
        if (cond) return;
        Ok = false;
        _log.Info($"{_prefix} FAIL: {what}");
    }

    /// <summary>Waits for cx.log to catch up before a check reads it, and fails naming the flush when it never does,
    /// so a slow writer is not read as a missing line.</summary>
    public bool ExpectFlushed()
    {
        bool ok = ExtenderLog.Flush();
        Expect(ok, $"the log flush gave up with {ExtenderLog.Unwritten} line(s) not yet written");
        return ok;
    }

    /// <summary>Suites that stood down instead of running, over the whole session: a skip that is not counted
    /// reads exactly like coverage that exists.</summary>
    public static int Skipped;

    /// <summary>This suite cannot run on this host: log <c>SKIPPED</c>, count it, and leave <see cref="Ok"/>
    /// alone. Returns true so a suite may <c>return check.Skip(...)</c> where its result means "do not stand
    /// the feature down" -- a skip is never a pass, so the caller must not go on to log "passed".</summary>
    public bool Skip(string why)
    {
        Skipped++;
        _log.Always($"{_prefix} SKIPPED: {why}");
        return true;
    }

}
