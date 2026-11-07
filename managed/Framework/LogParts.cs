using System.Text;

namespace CX.Framework;

/// <summary>cx.log and its older parts in one folder: cx.log, then cx.1.log to cx.4.log, oldest highest.</summary>
internal sealed class LogParts(string dir, long cap = LogParts.MaxPartBytes)
{
    /// <summary>The most one part holds: players send logs over Discord.</summary>
    internal const long MaxPartBytes = 10_000_000;
    /// <summary>cx.log plus the numbered parts; the oldest is deleted first.</summary>
    internal const int MaxParts = 5;

    internal long Cap => cap;
    private StreamWriter? _file;

    internal string PathOf(int part) => Path.Combine(dir, part == 0 ? "cx.log" : $"cx.{part}.log");

    internal void Write(string line)
    {
        _file ??= new StreamWriter(new FileStream(PathOf(0), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { NewLine = "\n" };
        _file.WriteLine(line);
    }

    internal void Flush() => _file?.Flush();

    internal void Close()
    {
        _file?.Dispose();
        _file = null;
    }
}
