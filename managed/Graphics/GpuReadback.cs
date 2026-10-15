using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Graphics;

/// <summary>
/// A GPU-to-CPU readback that never stalls: <see cref="Slots"/> regions of ONE persistently mapped,
/// client-readable buffer. The GPU copies into a slot and fences it; the CPU reads a slot straight off the
/// mapping once its fence has passed, with no GL call. Never <c>glGetBufferSubData</c>: on NVIDIA it queues
/// its copy behind the whole submitted frame and blocks (a fence on the SOURCE does not help).
/// </summary>
public unsafe struct GpuReadback
{
    /// <summary>Written this frame, read two frames later: the third slot is the one the GPU may still be copying.</summary>
    public const int Slots = 3;
    private uint _buf;
    private byte* _map;
    private long _slotBytes;
    private fixed long _fence[Slots];

    /// <summary>Capacity per slot, in bytes.</summary>
    public readonly long SlotBytes => _slotBytes;

    /// <summary>The entry points it needs, asked where a pass checks its own.</summary>
    public static bool EntryPoints => Gl.NamedBufferStorage != null && Gl.MapNamedBufferRange != null
                                      && Gl.CopyNamedBufferSubData != null && Gl.FenceSync != null
                                      && Gl.ClientWaitSync != null && Gl.DeleteSync != null;

    /// <summary>Hold at least <paramref name="slotBytes"/> per slot. A regrow drops every slot in flight (their
    /// fences, their contents). False = the driver refused the mapping; the caller's readback sits out.</summary>
    public bool Ensure(long slotBytes)
    {
        if (_map != null && slotBytes <= _slotBytes) return true;
        Release();
        const uint flags = Gl.MAP_READ_BIT | Gl.MAP_PERSISTENT_BIT | Gl.MAP_COHERENT_BIT;
        long bytes = Math.Max(slotBytes, 4) * Slots;
        uint b; Gl.CreateBuffers(1, &b); _buf = b;
        Gl.NamedBufferStorage(_buf, (IntPtr)bytes, null, flags);
        _map = (byte*)Gl.MapNamedBufferRange(_buf, IntPtr.Zero, (IntPtr)bytes, flags);
        if (_map == null) { Release(); return false; }
        _slotBytes = bytes / Slots;
        return true;
    }

    /// <summary>GPU-side copy of <paramref name="bytes"/> from <paramref name="src"/> into slot
    /// <paramref name="slot"/> at <paramref name="dstOff"/>. A shader-written source needs
    /// <c>BUFFER_UPDATE_BARRIER_BIT</c> ahead of it, which is the caller's.</summary>
    public readonly void Copy(uint src, long srcOff, int slot, long dstOff, long bytes)
        => Gl.CopyNamedBufferSubData(src, _buf, (IntPtr)srcOff, (IntPtr)(slot * _slotBytes + dstOff), (IntPtr)bytes);

    /// <summary>Fence slot <paramref name="slot"/> behind the copies just issued into it.</summary>
    public void Fence(int slot)
    {
        if (_fence[slot] != 0) Gl.DeleteSync((IntPtr)_fence[slot]);
        _fence[slot] = (long)Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
    }

    /// <summary>Slot <paramref name="slot"/>'s bytes if its copy has landed, else null. Never waits.</summary>
    public readonly byte* Ready(int slot)
    {
        if (_map == null || _fence[slot] == 0) return null;
        uint st = Gl.ClientWaitSync((IntPtr)_fence[slot], 0, 0);
        if (st != Gl.ALREADY_SIGNALED && st != Gl.CONDITION_SATISFIED) return null;
        var all = new Span<byte>(_map, checked((int)(_slotBytes * Slots)));
        return (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(all.Slice(checked((int)(slot * _slotBytes)))));
    }

    /// <summary>Delete the buffer and the fences (the context is current).</summary>
    public void Release()
    {
        for (int i = 0; i < Slots; i++) { if (_fence[i] != 0) Gl.DeleteSync((IntPtr)_fence[i]); _fence[i] = 0; }
        if (_buf != 0) { uint b = _buf; Gl.DeleteBuffers(1, &b); }
        _buf = 0; _map = null; _slotBytes = 0;
    }

    /// <summary>The names died with the context: forget them, delete nothing.</summary>
    public void ContextLost()
    {
        for (int i = 0; i < Slots; i++) _fence[i] = 0;
        _buf = 0; _map = null; _slotBytes = 0;
    }
}
