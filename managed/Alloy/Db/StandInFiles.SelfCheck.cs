using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Alloy.Db;

/// <summary>A self-check's resource tables: files by name and restype, answering every read while it lives, with
/// the material feed reading through them as it reads the engine's. Dispose puts back the session's readers.</summary>
internal sealed class StandInFiles : IDisposable
{
    /// <summary>The <c>.mtr</c> and <c>.txi</c> restypes, as <see cref="MtrFeed"/> and <c>TexSeam</c> ask for them.</summary>
    internal const ushort Mtr = 2072, Txi = 2022;   // not-an-offset: resource type ids

    private readonly Dictionary<(string, ushort), byte[]> _files = new();
    private readonly Func<string, byte[]?>? _bytes = MtrDef.Bytes;
    private readonly Func<string, bool>? _answered = MtrDef.Answered;
    private readonly Action<string>? _reread = MtrDef.Reread;
    private readonly HashSet<(string, ushort)> _locked = new();
    private readonly TexRegistry.StandInRead? _read = TexRegistry.ResourceStandIn;
    private readonly bool _feed;

    internal StandInFiles()
    {
        MtrDef.Forget();
        TexRegistry.ResourceStandIn = Read;
        _feed = MtrFeed.ArmForCheck(true);
    }

    /// <summary>Put file <paramref name="name"/> of <paramref name="type"/> in the tables, replacing any.</summary>
    internal void Put(string name, ushort type, string text) => _files[(name.ToLowerInvariant(), type)] = Encoding.ASCII.GetBytes(text);

    /// <summary>Take file <paramref name="name"/> of <paramref name="type"/> out of the tables.</summary>
    internal void Remove(string name, ushort type) => _files.Remove((name.ToLowerInvariant(), type));

    /// <summary>Make file <paramref name="name"/> of <paramref name="type"/> exist but fail to read, or read again.</summary>
    internal void Lock(string name, ushort type, bool locked)
    {
        if (locked) _locked.Add((name.ToLowerInvariant(), type));
        else _locked.Remove((name.ToLowerInvariant(), type));
    }

    private byte[]? Read(string name, ushort type, out string? why)
    {
        var key = (name.ToLowerInvariant(), type);
        why = _locked.Contains(key) ? "the self-check holds it locked" : null;
        return why == null && _files.TryGetValue(key, out byte[]? b) ? b : null;
    }

    public void Dispose()
    {
        MtrFeed.ArmForCheck(_feed);
        MtrDef.Bytes = _bytes;
        MtrDef.Answered = _answered;
        MtrDef.Reread = _reread;
        TexRegistry.ResourceStandIn = _read;
        MtrDef.Forget();
    }
}
