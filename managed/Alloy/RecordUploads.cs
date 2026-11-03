using System;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Why one record upload sent the whole table instead of the slots that moved. The reason a frame
/// crossed is the one number that separates a real table-wide edge from a ceiling that is simply too low.</summary>
internal enum RecordWhole : byte
{
    /// <summary>Nothing claimed this upload: it sends the moved slots.</summary>
    None,
    /// <summary>The first upload onto a device, whose table holds nothing yet.</summary>
    First,
    /// <summary>The GPU table was reallocated, so every slot in it is fresh memory.</summary>
    Grew,
    /// <summary>The ownership high-water mark moved, which reallocates the table.</summary>
    Owners,
    /// <summary>The world scene died: an area transition unstamped every record.</summary>
    AreaEdge,
    /// <summary>Cached texture handles were forgotten, which every record holding one outlives.</summary>
    Handles,
    /// <summary>The render device went, and with it everything the table described.</summary>
    Device,
    /// <summary>A retired texture name sat past the mark table, so no per-slot scan could find it.</summary>
    Names,
    /// <summary>A debug dial changed what a record means.</summary>
    Knob,
    /// <summary>More slots moved in one frame than the hand-over list seats.</summary>
    Handover,
    /// <summary>More slots moved in one frame than the mark list seats.</summary>
    Marks,
}

/// <summary>The record upload's own books: per reason, how many uploads went whole, and what the delta path
/// cost on the frames that stayed on it.</summary>
internal sealed class RecordUploadState
{
    /// <summary>The reason the pending upload is whole -- the FIRST claim since the last upload.</summary>
    public RecordWhole Why = RecordWhole.First;
    public readonly long[] Wholes = new long[RecordUploads.WhyCount];
    public long WholeBytes, Deltas, DeltaRuns, DeltaBytes, Marks;
    /// <summary>The most slots any one frame handed over, against the list's seats.</summary>
    public int MarksMax;
}

/// <summary>The record table's upload tally, read off the period line. A whole upload is the table's whole
/// capacity in bytes; a delta sends the merged runs of the slots that moved.</summary>
internal static class RecordUploads
{
    /// <summary>One slot per <see cref="RecordWhole"/> member.</summary>
    internal const int WhyCount = 11;

    private static RecordUploadState _recUp = new();

    /// <summary>Claim the pending upload for <paramref name="why"/>; the first claim holds, so an edge that
    /// also floods the mark lists is not reported as their overflow. Racy by design: workers mark in parallel
    /// and the loser of a tie is the same frame's other reason.</summary>
    internal static void Claim(RecordWhole why)
    {
        if (_recUp.Why == RecordWhole.None) _recUp.Why = why;
    }

    /// <summary>The slots one pass handed to the Vulkan frame, and the list's seats.</summary>
    internal static void NoteMarks(int n, int seats)
    {
        _recUp.Marks += n;
        _recUp.MarksMax = Math.Max(_recUp.MarksMax, Math.Min(n, seats));
    }

    /// <summary>This upload sent the whole table, <paramref name="bytes"/> of records and handles.</summary>
    internal static void NoteWhole(long bytes)
    {
        RecordUploadState s = _recUp;
        s.Wholes[(int)(s.Why == RecordWhole.None ? RecordWhole.Grew : s.Why)]++;
        s.WholeBytes += bytes;
        s.Why = RecordWhole.None;
    }

    /// <summary>This upload sent <paramref name="runs"/> merged runs, <paramref name="bytes"/> in all.</summary>
    internal static void NoteDelta(int runs, long bytes)
    {
        RecordUploadState s = _recUp;
        s.Deltas++;
        s.DeltaRuns += runs;
        s.DeltaBytes += bytes;
        s.Why = RecordWhole.None;
    }

    internal static void ResetPeriod()
    {
        RecordUploadState s = _recUp;
        Array.Clear(s.Wholes);
        s.WholeBytes = s.Deltas = s.DeltaRuns = s.DeltaBytes = s.Marks = 0;
        s.MarksMax = 0;
    }

    /// <summary>The period's record traffic: the whole uploads by reason with their bytes, then what the delta
    /// path moved on the frames that stayed on it, and how close the hand-over list came to its seats.</summary>
    internal static string Stats(long frames, int seats)
    {
        RecordUploadState s = _recUp;
        long whole = 0;
        for (int w = 0; w < WhyCount; w++) whole += s.Wholes[w];
        var sb = new StringBuilder($"record table {whole} whole upload(s) ({s.WholeBytes / 1048576.0:F0} MiB");
        for (int w = 1; w < WhyCount; w++)
            if (s.Wholes[w] > 0) sb.Append($", {(RecordWhole)w} {s.Wholes[w]}");
        float f = Math.Max(frames, 1);
        return sb.Append($") + {s.Deltas} delta(s) ({s.DeltaBytes / 1048576.0:F1} MiB, {s.DeltaRuns / f:F0} runs/frame), "
                       + $"{s.Marks / f:F0} slots/frame handed over, most {s.MarksMax} of {seats}").ToString();
    }

    /// <summary>The tally against a hand-made sequence: two claimed whole uploads, one unclaimed (which reads as
    /// the reallocation it can only be), and one delta. Oracle: the counts and byte totals as literals.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "RecordUploads");
        RecordUploadState was = _recUp;
        _recUp = new RecordUploadState { Why = RecordWhole.None };
        try
        {
            const int sevenRuns = 7, fourSlots = 4, eightSeats = 8;
            const long oneMiB = 1048576, halfMiB = 524288;
            Claim(RecordWhole.Owners);
            Claim(RecordWhole.Handover);
            NoteWhole(oneMiB);
            check.Expect(_recUp.Wholes[(int)RecordWhole.Owners] == 1 && _recUp.Wholes[(int)RecordWhole.Handover] == 0,
                         "the first claim holds and the second is not counted");
            NoteMarks(fourSlots, eightSeats);
            NoteDelta(sevenRuns, halfMiB);
            check.Expect(_recUp.Deltas == 1 && _recUp.DeltaRuns == sevenRuns && _recUp.MarksMax == fourSlots,
                         "a delta counts its runs and the slots handed over");
            NoteWhole(oneMiB);
            check.Expect(_recUp.Wholes[(int)RecordWhole.Grew] == 1,
                         "an upload no edge claimed is the reallocation that needs no claim");
            Claim(RecordWhole.AreaEdge);
            NoteWhole(halfMiB);
            check.Expect(_recUp.WholeBytes == oneMiB + oneMiB + halfMiB && _recUp.Wholes[(int)RecordWhole.AreaEdge] == 1,
                         $"three whole uploads carry 2.5 MiB between them (got {_recUp.WholeBytes} bytes)");
            NoteMarks(eightSeats + 1, eightSeats);
            check.Expect(_recUp.MarksMax == eightSeats, "a pass that outruns the seats is capped at them");
            ResetPeriod();
            check.Expect(_recUp.Wholes[(int)RecordWhole.AreaEdge] == 0 && _recUp.Deltas == 0 && _recUp.MarksMax == 0,
                         "the next period starts from zero");
        }
        finally
        {
            _recUp = was;
        }
        log.Info(check.Ok ? "RecordUploads.SelfCheck: passed" : "RecordUploads.SelfCheck: FAILED -- see above");
    }
}
