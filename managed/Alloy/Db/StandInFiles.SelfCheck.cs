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
    private readonly Func<string, ushort, byte[]?>? _read = TexRegistry.ResourceStandIn;
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

    private byte[]? Read(string name, ushort type) => _files.TryGetValue((name.ToLowerInvariant(), type), out byte[]? b) ? b : null;

    public void Dispose()
    {
        MtrFeed.ArmForCheck(_feed);
        MtrDef.Bytes = _bytes;
        MtrDef.Answered = _answered;
        TexRegistry.ResourceStandIn = _read;
        MtrDef.Forget();
    }
}
