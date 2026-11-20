using System.Collections.Concurrent;

namespace CX.Framework;

/// <summary>Injected logger, writing stderr plus the <see cref="LogParts"/>. The files land in $HOME (/tmp only if
/// $HOME is unset) because the Steam Linux Runtime gives the game a container-private /tmp the host cannot see. Never
/// blocks the caller: <see cref="Info(string)"/> enqueues and a background thread writes and rolls, so a line queued
/// microseconds before a crash can be lost -- unless <see cref="Synchronous"/> or <see cref="AlwaysNow"/> wrote it.</summary>
public sealed class ExtenderLog
{
    private static readonly LogParts _parts = new(UserFolders.Logs);

    /// <summary>Every line is on disk before its call returns: set by <c>alloy.gpudebug=1</c>, so the log of a crash
    /// ends at the crash. Costs a file write per line on the caller's thread, and the roll when one falls due.</summary>
    public static bool Synchronous;

    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly AutoResetEvent _wake = new(false);
    private static readonly object _fileLock = new();
    private static Thread? _writer;

    /// <summary>A self-check's ear: an instance with a tap sends its lines here instead of to the file, so a check
    /// gives its own instance one and no other line is diverted.</summary>
    internal Action<string>? Tap;

    /// <summary>One line to cx.log; the same write as <see cref="Always"/>.</summary>
    public void Info(string message) => Always(message);

    /// <summary>One line to cx.log, queued for the writer thread (or the tap).</summary>
    public void Always(string message)
    {
        if (Tap != null) { Tap(message); return; }
        _queue.Enqueue($"{DateTime.Now:O} [cx] {message}");
        if (_writer == null)
        {
            _writer = new Thread(Drain) { IsBackground = true, Name = "cx-log" };
            _writer.Start();
        }
        if (Synchronous) WriteQueued();
        else _wake.Set();
    }

    /// <summary><see cref="Always"/>, on disk before it returns: for the line a crash may follow at once.</summary>
    public void AlwaysNow(string message)
    {
        Always(message);
        WriteQueued();
    }

    private static void Drain()
    {
        Affinity.ClearThis();
        while (true)
        {
            _wake.WaitOne();
            WriteQueued();
        }
    }

    /// <summary>Write and flush everything queued, on the calling thread: the writer thread's loop, and the
    /// synchronous paths.</summary>
    private static void WriteQueued()
    {
        lock (_fileLock)
        {
            while (_queue.TryDequeue(out var l))
            {
                Console.Error.WriteLine(l);
                _parts.Write(l);
            }
            _parts.Flush();
        }
    }

    /// <summary>The folder cx.log is written to now.</summary>
    internal static string Dir { get { lock (_fileLock) return _parts.Dir; } }

    /// <summary>Carry cx.log, with every line this launch wrote so far, into <paramref name="dir"/> and go on there.</summary>
    internal static void MoveTo(string dir)
    {
        WriteQueued();
        lock (_fileLock) _parts.MoveTo(dir);
    }

    /// <summary>Block until everything queued so far is on disk. Off-device only (the self-checks).</summary>
    public static void Flush()
    {
        _wake.Set();
        for (int i = 0; i < 200 && !_queue.IsEmpty; i++) BlockingWait.Sleep(1, "self-checks only, never a frame hook");
    }

    /// <summary>Off-device: a tapped log hears its lines and the file does not; an <see cref="AlwaysNow"/> line is
    /// in the file before it returns. Oracle: the file's own bytes and the literals written here.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var heard = new List<string>();
        var ear = new ExtenderLog { Tap = heard.Add };
        string quiet = $"ExtenderLog.SelfCheck: tapped, never in the file {Environment.TickCount64}";
        ear.Info("plain");
        ear.Info(quiet);
        Flush();
        bool tapped = heard.Count == 2 && heard[0] == "plain" && heard[1] == quiet && !TailHas(quiet);
        string mark = $"ExtenderLog.SelfCheck: on disk before return {Environment.TickCount64}";
        log.AlwaysNow(mark);
        bool now = TailHas(mark);
        log.Info(now && tapped ? "ExtenderLog.SelfCheck: passed"
                 : !tapped ? $"ExtenderLog.SelfCheck: FAILED -- a tapped log heard [{string.Join("|", heard)}]"
                 : "ExtenderLog.SelfCheck: FAILED -- an AlwaysNow line was not in the file when the call returned");
    }

    /// <summary>Does the log file's last few KiB hold <paramref name="text"/>? Off-device only.</summary>
    internal static bool TailHas(string text)
    {
        try
        {
            using var f = new FileStream(_parts.PathOf(0), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(Math.Max(0, f.Length - 8192), SeekOrigin.Begin);
            return new StreamReader(f).ReadToEnd().Contains(text, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }
}
