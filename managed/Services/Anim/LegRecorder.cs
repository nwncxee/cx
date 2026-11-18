using System;
using System.IO;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// Records each body's complete <see cref="LegFrame"/> while <c>anim.record</c> is on, for the offline replay
/// (<see cref="LegReplay"/>). A frame is copied into a buffer made when recording starts and written in bulk when the
/// buffer is full or <see cref="FlushTicks"/> have passed, so the frame hook allocates nothing and touches no disk
/// between flushes; with the knob off the hook is one branch. <b>Hot</b> while on.
/// </summary>
internal static class LegRecorder
{
    /// <summary>What one recording keeps: its buffer, the stream it goes to and the bodies seen. A recording owns it whole, so
    /// starting a new one replaces the object.</summary>
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
    }

    private static RecorderState _st = new();

    /// <summary>Bodies one recording tells apart; a frame of a later one is dropped.</summary>
    internal const int MaxBodies = 64;
    /// <summary>Frames the buffer holds before a bulk write.</summary>
    internal const int BufferFrames = 1024;
    /// <summary>The longest a frame waits in the buffer: two seconds of the stopwatch's ticks.</summary>
    private static long FlushTicks => System.Diagnostics.Stopwatch.Frequency * 2;

    /// <summary>The frames written to files this session; 0 while recording has never been on.</summary>
    internal static int FramesWritten => _st.Written;

    /// <summary>The frame hook: <paramref name="gob"/> names the body, <paramref name="f"/> is its frame complete. Switching
    /// the knob off closes the file. ponytail: a body's address is its identity, so a gob freed and reborn at the same address
    /// continues the old body's frames; a session that cares records between area changes.</summary>
    internal static void Frame(IntPtr gob, in LegFrame f)
    {
        if (!AnimKnobs.Record.V)
        {
            if (_st.Out != null) Stop(_st);
            return;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_st.Out == null && !Start(_st, UserFolders.Logs, now)) return;
        Add(_st, gob, f, now);
    }

    /// <summary>Opens a recording file in <paramref name="folder"/>, named by the UTC second; false (and the knob's
    /// session writes nothing) when the folder cannot be written.</summary>
    internal static bool Start(RecorderState st, string folder, long now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"cx-legs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.cxlegs");
            Begin(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), st, now);
            st.Path = path;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
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

    /// <summary>Appends one frame of body <paramref name="key"/>, writing the buffer out when it is full or old.</summary>
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
        if (st.Count == st.Buffer.Length || now - st.FlushedAt > FlushTicks) Flush(st, now);
    }

    /// <summary>Writes the buffered frames and empties it.</summary>
    internal static void Flush(RecorderState st, long now)
    {
        st.FlushedAt = now;
        if (st.Out == null || st.Count == 0) return;
        try
        {
            st.Out.Write(MemoryMarshal.AsBytes(st.Buffer.AsSpan(0, st.Count)));
            st.Out.Flush();
            st.Written += st.Count;
        }
        catch (IOException) { }
        st.Count = 0;
    }

    /// <summary>Writes what is left and closes the file.</summary>
    internal static void Stop(RecorderState st)
    {
        Flush(st, 0);
        st.Out?.Dispose();
        st.Out = null;
    }
}
