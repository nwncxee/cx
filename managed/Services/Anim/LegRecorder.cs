using System;
using System.IO;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// Records each body's complete <see cref="LegFrame"/> while <c>anim.record</c> is on, for the offline replay
/// (<see cref="LegReplay"/>). A frame is copied into a buffer made when a recording starts and written in bulk when the
/// buffer is full or <see cref="FlushTicks"/> old, so the frame hook allocates nothing and touches no disk between
/// flushes; with the knob off the hook is one branch and <see cref="Tick"/>, which sees the knob off whether or not a body is corrected, closes the file. <see cref="Tick"/> runs once a frame, outside the per-body path.
/// </summary>
internal static class LegRecorder
{
    /// <summary>One recording: its buffer, the stream, the bodies seen and whether it was refused. A new recording is a new
    /// object, which carries the log and the frames of the earlier ones.</summary>
    internal sealed class RecorderState
    {
        internal LegRec[] Buffer = [];
        internal int Count;
        internal Stream? Out;
        internal long FlushedAt;
        internal int Written;
        internal readonly IntPtr[] Keys = new IntPtr[MaxBodies];
        internal int Bodies;
        internal string Path = "";
        /// <summary>The folder or the stream refused; no new recording starts until the knob reads off.</summary>
        internal bool Failed;
        internal readonly ExtenderLog? Log;
        /// <summary>Frames written by the recordings before this one.</summary>
        internal readonly int Before;

        internal RecorderState(ExtenderLog? log = null, int before = 0) { Log = log; Before = before; }

        /// <summary>Frames written this session, this recording and the ones before it.</summary>
        internal int Total => Before + Written;
    }

    private static RecorderState _st = new();

    /// <summary>Bodies one recording tells apart; a frame of a later one is dropped.</summary>
    internal const int MaxBodies = 64;
    /// <summary>Frames the buffer holds before a bulk write.</summary>
    internal const int BufferFrames = 1024;
    /// <summary>The longest a frame waits in the buffer: two seconds of the stopwatch's ticks.</summary>
    internal static long FlushTicks => System.Diagnostics.Stopwatch.Frequency * 2;
    /// <summary>The attempts at a file name within one second before a start gives up.</summary>
    private const int NameTries = 100;

    private static readonly Func<string> LogsFolder = static () => UserFolders.Logs;

    /// <summary>Gives the recorder the log its refusals and its file name go to.</summary>
    internal static void Attach(ExtenderLog log) => _st = new RecorderState(log, _st.Total);

    /// <summary>The frames written this session.</summary>
    internal static int FramesWritten => _st.Total;

    /// <summary>The frame hook: <paramref name="gob"/> names the body, <paramref name="f"/> is its frame complete.
    /// ponytail: a body's address is its identity, so a gob freed and reborn at the same address continues the old body's
    /// frames; a session that cares records between area changes.</summary>
    internal static void Frame(IntPtr gob, in LegFrame f)
    {
        if (!AnimKnobs.Record.V) return;
        _st = Frame(_st, true, LogsFolder, gob, f, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>The once-a-frame edge: with the knob off, writes what is buffered and closes the file and forgets a refusal; with it
    /// on, writes a buffer that has waited <see cref="FlushTicks"/>.</summary>
    internal static void Tick() => Tick(_st, AnimKnobs.Record.V, System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>The frame hook's core, taking the knob as <paramref name="on"/>: off or refused, nothing is made; on, a recording
    /// starts in <paramref name="folder"/>'s folder when there is none, and the frame is added. Returns the state to carry on with,
    /// a new object when a recording started.</summary>
    internal static RecorderState Frame(RecorderState st, bool on, Func<string> folder, IntPtr gob, in LegFrame f, long now)
    {
        if (!on || st.Failed) return st;
        if (st.Out == null) st = Start(st, folder, now);
        if (st.Out != null) Add(st, gob, f, now);
        return st;
    }

    /// <inheritdoc cref="Tick()"/>
    internal static void Tick(RecorderState st, bool on, long now)
    {
        if (!on)
        {
            if (st.Out != null) Stop(st);
            st.Failed = false;
        }
        else if (st.Out != null && st.Count > 0 && now - st.FlushedAt > FlushTicks) Flush(st, now);
    }

    /// <summary>Opens a recording in <paramref name="folder"/>'s folder and returns its state; the old state, marked
    /// <see cref="RecorderState.Failed"/> and logged once, when the folder cannot be written. The file is named by the UTC second, with a
    /// suffix when that name is taken.</summary>
    private static RecorderState Start(RecorderState old, Func<string> folder, long now)
    {
        var st = new RecorderState(old.Log, old.Total);
        try
        {
            string dir = folder();
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, $"cx-legs-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
            for (int n = 0; n < NameTries; n++)
            {
                string path = n == 0 ? stem + ".cxlegs" : $"{stem}-{n}.cxlegs";
                if (File.Exists(path)) continue;
                Begin(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), st, now);
                st.Path = path;
                old.Log?.Info($"LegRecorder: recording to {path}");
                return st;
            }
            throw new IOException($"{NameTries} recordings already carry this second's name");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            old.Log?.Info($"LegRecorder: no recording, {e.Message}");
            old.Failed = true;
            return old;
        }
    }

    /// <summary>Starts a recording into <paramref name="to"/>: the header, and the buffer.</summary>
    internal static void Begin(Stream to, RecorderState st, long now = 0)
    {
        st.Out = to;
        st.Buffer = new LegRec[BufferFrames];
        st.Count = 0;
        st.Bodies = 0;
        st.FlushedAt = now;
        LegRecording.WriteHeader(to);
    }

    /// <summary>Appends one frame of body <paramref name="key"/>, writing the buffer out when it is full.</summary>
    internal static void Add(RecorderState st, IntPtr key, in LegFrame f, long now)
    {
        if (st.Out == null) return;
        int body = -1;
        for (int i = 0; i < st.Bodies; i++)
            if (st.Keys[i] == key) { body = i; break; }
        if (body < 0)
        {
            if (st.Bodies == MaxBodies) return;
            st.Keys[body = st.Bodies++] = key;
        }
        ref LegRec r = ref st.Buffer[st.Count++];
        r.Body = body + 1;
        r.Frame = f;
        if (st.Count == st.Buffer.Length) Flush(st, now);
    }

    /// <summary>Writes the buffered frames and empties it; a write that fails stops the recording and logs it once.</summary>
    internal static void Flush(RecorderState st, long now)
    {
        st.FlushedAt = now;
        if (st.Out == null || st.Count == 0) return;
        try
        {
            st.Out.Write(LegRecording.Bytes<LegRec>(st.Buffer.AsSpan(0, st.Count)));
            st.Out.Flush();
            st.Written += st.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            st.Log?.Info($"LegRecorder: recording stopped, {e.Message}");
            st.Failed = true;
            Close(st);
        }
        st.Count = 0;
    }

    /// <summary>Writes what is left and closes the file, logging the count.</summary>
    internal static void Stop(RecorderState st)
    {
        Flush(st, 0);
        if (st.Out != null) st.Log?.Info($"LegRecorder: {st.Written} frames in {st.Path}");
        Close(st);
    }

    private static void Close(RecorderState st)
    {
        try { st.Out?.Dispose(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { st.Log?.Info($"LegRecorder: closing failed, {e.Message}"); }
        st.Out = null;
    }
}
