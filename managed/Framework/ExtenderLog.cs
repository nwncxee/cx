using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CX.Framework;

/// <summary>Injected logger, writing stderr plus a log file. The file lands in $HOME (/tmp only if $HOME is unset)
/// because the Steam Linux Runtime gives the game a container-private /tmp the host cannot see. Never blocks the
/// caller: <see cref="Info(string)"/> enqueues and a background thread writes, so a line queued microseconds
/// before a crash can be lost -- unless <see cref="Synchronous"/> or <see cref="AlwaysNow"/> wrote it.</summary>
public sealed class ExtenderLog
{
    private static readonly string LogPath = OperatingSystem.IsWindows()
        ? Path.Combine(Native.DefaultUserDirectory(), "cx.log")
        : Path.Combine(HostEnv.Home, "cx.log");

    /// <summary>The one gate every line passes: <c>general.logs=1</c> keeps it open, the default closes it once
    /// <see cref="Settings.Load"/> has read cx.cfg, and <c>General.Logs</c> flips it live. Off means off at the CALL
    /// SITE too -- an interpolated argument goes through <see cref="Line"/>, whose holes the compiler never
    /// evaluates when this is false, so a census behind a closed gate is never walked at all.</summary>
    public static bool Enabled = true;

    /// <summary>Every line is on disk before its call returns: set by <c>alloy.gpudebug=1</c>, so the log of a crash
    /// ends at the crash. Costs a file write per line on the caller's thread.</summary>
    public static bool Synchronous;

    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly AutoResetEvent _wake = new(false);
    private static readonly object _fileLock = new();
    private static Thread? _writer;
    private static StreamWriter? _file;

    public void Info(string message)
    {
        if (!Enabled) return;
        Always(message);
    }

    /// <summary>A boot line that bypasses the <c>logs</c> gate: a feature standing down is the line a stock
    /// install's bug report needs, and a stock install has the gate closed.</summary>
    public void Always(string message)
    {
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

    /// <summary>The interpolated-string overload the compiler prefers for <c>$"..."</c> arguments.</summary>
    public void Info([InterpolatedStringHandlerArgument] ref Line line)
    {
        if (!Enabled) return;
        Info(line.ToStringAndClear());
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
            try { _file ??= new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { NewLine = "\n" }; }
            catch { /* log is best-effort */ }
            while (_queue.TryDequeue(out var l))
            {
                Console.Error.WriteLine(l);
                try { _file?.WriteLine(l); }
                catch { _file = null; }
            }
            try { _file?.Flush(); }
            catch { _file = null; }
        }
    }

    /// <summary>Block until everything queued so far is on disk. Off-device only (the self-checks).</summary>
    public static void Flush()
    {
        _wake.Set();
        for (int i = 0; i < 200 && !_queue.IsEmpty; i++) BlockingWait.Sleep(1, "self-checks only, never a frame hook");
    }

    /// <summary>Interpolation handler: when <see cref="Enabled"/> is false, <c>shouldAppend</c> tells the
    /// compiler to skip every hole, so the expressions inside <c>$"..."</c> never run.</summary>
    [InterpolatedStringHandler]
    public ref struct Line
    {
        private DefaultInterpolatedStringHandler _h;
        private readonly bool _on;
        public Line(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _on = Enabled;
            shouldAppend = _on;
            if (_on) _h = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
        }
        public void AppendLiteral(string s) => _h.AppendLiteral(s);
        public void AppendFormatted<T>(T v) => _h.AppendFormatted(v);
        public void AppendFormatted<T>(T v, string? format) => _h.AppendFormatted(v, format);
        public void AppendFormatted<T>(T v, int alignment) => _h.AppendFormatted(v, alignment);
        public void AppendFormatted<T>(T v, int alignment, string? format) => _h.AppendFormatted(v, alignment, format);
        public void AppendFormatted(ReadOnlySpan<char> v) => _h.AppendFormatted(v);
        public void AppendFormatted(string? v) => _h.AppendFormatted(v);
        public void AppendFormatted(object? v, int alignment = 0, string? format = null) => _h.AppendFormatted(v, alignment, format);
        public string ToStringAndClear() => _on ? _h.ToStringAndClear() : "";
    }

    /// <summary>Off-device: a disabled log must not evaluate its holes, an enabled one must; the queue must reach
    /// the file; and an <see cref="AlwaysNow"/> line is in the file before it returns. Oracle: the file's own bytes.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        int evaluated = 0;
        int Hole() { evaluated++; return evaluated; }
        bool saved = Enabled;
        Enabled = false;
        log.Info($"ExtenderLog.SelfCheck: must not appear {Hole()}");
        bool ok = evaluated == 0;
        Enabled = true;
        log.Info($"ExtenderLog.SelfCheck: hole {Hole()}");
        ok &= evaluated == 1;
        Flush();
        Enabled = saved;
        string mark = $"ExtenderLog.SelfCheck: on disk before return {Environment.TickCount64}";
        log.AlwaysNow(mark);
        bool now = TailHas(mark);
        log.Info(ok && now ? "ExtenderLog.SelfCheck: passed"
                 : !ok ? "ExtenderLog.SelfCheck: FAILED -- the disabled log evaluated its holes"
                 : "ExtenderLog.SelfCheck: FAILED -- an AlwaysNow line was not in the file when the call returned");
    }

    /// <summary>Does the log file's last few KiB hold <paramref name="text"/>? Off-device only.</summary>
    private static bool TailHas(string text)
    {
        try
        {
            using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(Math.Max(0, f.Length - 8192), SeekOrigin.Begin);
            return new StreamReader(f).ReadToEnd().Contains(text, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }
}
