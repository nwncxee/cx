using System.Text;

namespace CX.Framework;

/// <summary>cx.log and its older parts in one folder: cx.log, then cx.1.log to cx.4.log, oldest highest. Each instance
/// writes to a file it created itself, so two clients sharing a folder never mix lines. Not thread-safe; the caller
/// holds the lock.</summary>
internal sealed class LogParts(string dir, long cap = LogParts.MaxPartBytes)
{
    /// <summary>The most one part holds: players send logs over Discord.</summary>
    internal const long MaxPartBytes = 10_000_000;
    /// <summary>cx.log plus the numbered parts; the oldest is deleted first.</summary>
    internal const int MaxParts = 5;

    internal long Cap => cap;
    private StreamWriter? _file;
    private long _written;
    private bool _writeFailed;
    private bool _saidOpenFailed;

    /// <summary>The folder the parts are in.</summary>
    internal string Dir => dir;

    /// <summary>Carry this launch's cx.log into <paramref name="to"/>, as a fresh part there behind that folder's own
    /// rotation, and write there from now on. A move that cannot open the new folder goes on in the old file.</summary>
    internal void MoveTo(string to)
    {
        if (_writeFailed || UserFolders.Same(to, dir)) return;
        string from = PathOf(0), was = dir;
        bool open = _file != null;
        Close();
        dir = to;
        if (!open) return;
        try
        {
            using var launch = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!Open())
            {
                dir = was;
                _file = Writer(from, FileMode.Append);
                _written = launch.Length;
                return;
            }
            _file!.Flush();
            launch.CopyTo(_file.BaseStream);
            _written = launch.Length;
            launch.Dispose();
            File.Delete(from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cx] {from}: could not move to {to}, the rest of the log goes on without it: {e.Message}");
        }
    }

    internal string PathOf(int part) => Path.Combine(dir, part == 0 ? "cx.log" : $"cx.{part}.log");

    /// <summary>Append a line, rolling first if it would take the part past the cap. The first line of a launch opens a
    /// fresh file. A move that fails is retried on the next line, and until then lines reach only stderr.</summary>
    internal void Write(string line)
    {
        if (_writeFailed) return;
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (bytes > cap)
        {
            var fit = new byte[cap - 1];
            Encoding.UTF8.GetEncoder().Convert(line, fit, flush: true, out int chars, out int used, out _);
            line = line[..chars];
            bytes = used + 1;
        }
        if (_written + bytes > cap) Close();
        if (_file == null && !Open()) return;
        try
        {
            _file!.WriteLine(line);
            _written += bytes;
        }
        catch (IOException e) { WriteFailed(e); }
    }

    /// <summary>A saved copy's full path, or why there is none.</summary>
    internal readonly record struct Saved(string? Path, string? Why);

    /// <summary>Copy cx.log as it stands to <see cref="LogReport.FileName"/> beside it, written under a temporary name and
    /// renamed over the last copy, so a browser never picks up half a file.</summary>
    internal Saved SaveReport()
    {
        if (_file == null) return new(null, "cx.log holds no lines this launch could write");
        string to = Path.GetFullPath(Path.Combine(dir, LogReport.FileName)), part = to + ".part";
        try
        {
            _file.Flush();
            using (var from = new FileStream(PathOf(0), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                from.CopyTo(copy);
            File.Move(part, to, overwrite: true);
            return new(to, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(part); }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return new(null, e.Message);
        }
    }

    internal void Flush()
    {
        try { _file?.Flush(); }
        catch (IOException e) { WriteFailed(e); }
    }

    internal void Close()
    {
        try { _file?.Dispose(); }
        catch (IOException e) { WriteFailed(e); }
        _file = null;
        _written = 0;
    }

    /// <summary>A write that failed ends the file for this session: rolling on it would push older parts out, one for each
    /// failed write, on a full disk.</summary>
    private void WriteFailed(IOException e)
    {
        _writeFailed = true;
        _file = null;
        Console.Error.WriteLine($"[cx] {PathOf(0)}: write failed, the log goes to stderr only from here: {e.Message}");
    }

    private bool Open()
    {
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(PathOf(0))) Shift();
            // CreateNew: a second client that got here first keeps its file to itself; this one retries on the next line.
            _file = Writer(PathOf(0), FileMode.CreateNew);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_saidOpenFailed) Console.Error.WriteLine($"[cx] {PathOf(0)}: cannot start a new part, retrying on the next line: {e.Message}");
            _saidOpenFailed = true;
            return false;
        }
    }

    /// <summary>A part opened to write: UTF-8 with no mark, one newline per line, shared so a reader and a rotation can work
    /// beside it.</summary>
    private static StreamWriter Writer(string path, FileMode mode) =>
        new(new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { NewLine = "\n" };

    /// <summary>Free the cx.log name. Only the parts below the first free number move, so a retry after a failed move
    /// pushes nothing further along and deletes nothing more.</summary>
    private void Shift()
    {
        int free = 1;
        while (free < MaxParts - 1 && File.Exists(PathOf(free))) free++;
        File.Delete(PathOf(free));
        for (int part = free; part >= 1; part--) File.Move(PathOf(part - 1), PathOf(part));
        KeepTail(PathOf(1));
    }

    /// <summary>Cut a part an older build grew past the cap down to its last whole lines within the cap: the end of a log
    /// is where a crash is.</summary>
    private void KeepTail(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length <= cap) return;
        // One byte before the window: a newline there means the window already starts on a whole line.
        var tail = new byte[cap + 1];
        f.Seek(-tail.Length, SeekOrigin.End);
        f.ReadExactly(tail);
        int start = Array.IndexOf(tail, (byte)'\n') + 1;
        if (start == 0) start = 1;
        f.SetLength(0);
        f.Write(tail, start, tail.Length - start);
    }
}
