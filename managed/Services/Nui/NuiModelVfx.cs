using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The visual effects a model view's doll wears: only the <c>visualeffects.2da</c> rows the server lists, each on the body part it
/// names, put on the doll through the engine's own add call. The client cannot tell a permanent effect from a temporary one, so the
/// server chooses. The add call takes its transform by value: Linux copies it, Win64 destroys the copy it is handed, so each call there gets a
/// fresh copy of the identity transform built by the engine's own tree constructor.
/// </summary>
internal static unsafe class NuiModelVfx
{
    /// <summary>The most effects one doll wears.</summary>
    internal const int MaxRows = 16;
    /// <summary>The bytes a Win64 <c>ObjectVisualTransformData</c> takes (an MSVC map: head pointer and size), and the same again for the
    /// copy constructor's unused allocator.</summary>
    private const int TransformBytes = 32;

    /// <summary>One effect to wear: its <c>visualeffects.2da</c> row and the body part it sits on. Part 0 is the hand node, 1 the
    /// impact node, 2-11 the creature's ground nodes <c>monster0</c>-<c>monster9</c>, and any other number the root.</summary>
    internal readonly record struct VfxRow(ushort Row, byte Part);

    private sealed class VfxState
    {
        public ExtenderLog? Log;
        public IntPtr Identity;
        public bool Ready, Said, SoundReady;
        /// <summary>The managed thread inside <see cref="Wear"/> with the sounds muted, or 0.</summary>
        public int MuteThread;
        public ExoSoundPlay3DOneShotFn OrigOneShot;
        public ExoSoundSourcePlayFn OrigPlay;
    }

    private static readonly VfxState _v = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _v.Log = log;
        _v.Identity = SymbolMap.Resolve(SymbolMap.IdentityVisualTransform, log);
        _v.Ready = _v.Identity != IntPtr.Zero && SymbolMap.Resolve(SymbolMap.ObjAddVisualEffect, log) != IntPtr.Zero
                   && SymbolMap.Resolve(SymbolMap.ObjKillAllVisualEffects, log) != IntPtr.Zero
                   && (!OperatingSystem.IsWindows() || SymbolMap.Resolve(SymbolMap.TransformCopy, log) != IntPtr.Zero);
        if (!_v.Ready) { log.Always("NuiModelVfx: the engine's effect calls did not resolve -- dolls wear no effects"); return; }
        _v.OrigOneShot = hooks.InstallExoSoundPlay3DOneShot(&OnOneShot);
        _v.OrigPlay = hooks.InstallExoSoundSourcePlay(&OnPlay);
        _v.SoundReady = _v.OrigOneShot != null && _v.OrigPlay != null;
        if (!_v.SoundReady) log.Always("NuiModelVfx: the engine's sound calls did not hook -- vfxmute is ignored");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnOneShot(IntPtr self, IntPtr resref, VectorArg at, float volume, byte group, uint a, byte b)
        => _v.MuteThread == Environment.CurrentManagedThreadId ? IntPtr.Zero : _v.OrigOneShot(self, resref, at, volume, group, a, b);

    [UnmanagedCallersOnly]
    private static IntPtr OnPlay(IntPtr self)
        => _v.MuteThread == Environment.CurrentManagedThreadId ? IntPtr.Zero : _v.OrigPlay(self);

    /// <summary>A <c>vfx</c> value, <c>row[:part],row[:part],...</c>, as its effects. Text that is not a row, a part past 255 and anything past
    /// <see cref="MaxRows"/> is skipped, and a row named twice on one part is worn once.</summary>
    internal static VfxRow[] Parse(string value) =>
        value.Split(',').Select(One).Where(e => e.Row >= 0).Distinct().Take(MaxRows).Select(e => new VfxRow((ushort)e.Row, (byte)e.Part)).ToArray();

    private static (int Row, int Part) One(string text)
    {
        string[] bits = text.Split(':');
        if (bits.Length > 2 || !ushort.TryParse(bits[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort row)) return (-1, 0);
        if (bits.Length == 1) return (row, 0);
        return byte.TryParse(bits[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte part) ? (row, part) : (-1, 0);
    }

    /// <summary>The doll wears exactly <paramref name="rows"/>: every effect it had is ended first. With <paramref name="mute"/> the engine's
    /// sounds for the new effects (their impact one-shot and looping source) are not started.</summary>
    internal static void Wear(IntPtr doll, VfxRow[] rows, bool mute = false)
    {
        if (doll == IntPtr.Zero) return;
        if (!_v.Ready)
        {
            if (rows.Length > 0 && !_v.Said) { _v.Said = true; _v.Log?.Always("NuiModelVfx: this client puts no effects on a doll -- vfx ignored"); }
            return;
        }
        EngineFn.ObjKillAllVisualEffects(doll);
        Vec3 zero = default;
        byte* scratch = stackalloc byte[TransformBytes];
        if (mute && _v.SoundReady) _v.MuteThread = Environment.CurrentManagedThreadId;
        try
        {
            foreach (VfxRow e in rows)
                if (EngineFn.ObjAddVisualEffect(doll, e.Row, 0, NuiModelDoll.InvalidId, e.Part, 0, VectorArg.Of(&zero), TransformFor(scratch)) == 0)
                    _v.Log?.Always($"NuiModelVfx: the engine would not load effect row {e.Row}");
        }
        finally { _v.MuteThread = 0; }
        if (rows.Length > 0) _v.Log?.Always($"NuiModelVfx: the doll wears effect rows {string.Join(',', rows.Select(e => $"{e.Row}:{e.Part}"))}{(mute ? ", sounds muted" : "")}");
    }

    /// <summary>The transform argument for one add call: the engine's identity as it is on Linux, and on Windows a fresh copy in <paramref name="scratch"/> that the call
    /// destroys itself.</summary>
    private static IntPtr TransformFor(byte* scratch)
    {
        if (!OperatingSystem.IsWindows()) return _v.Identity;
        EngineFn.TransformCopy((IntPtr)scratch, _v.Identity, (IntPtr)(scratch + TransformBytes / 2));
        return (IntPtr)scratch;
    }

    /// <summary>The doll is going: end its effects while the creature is still whole.</summary>
    internal static void Clear(IntPtr doll)
    {
        if (_v.Ready && doll != IntPtr.Zero) EngineFn.ObjKillAllVisualEffects(doll);
    }

    /// <summary>Oracle: hand-written <c>vfx</c> values and the effects they name.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Parse("").Length == 0, "an empty value names no effect");
        VfxRow[] rows = Parse("147, 408,147");
        check.Expect(rows.Length == 2 && rows[0] == new VfxRow(147, 0) && rows[1] == new VfxRow(408, 0), "rows keep their order, sit on the hand and a repeat is dropped");
        check.Expect(Parse("x,-1,70000,13").Length == 1 && Parse("x,-1,70000,13")[0].Row == 13, "text, a negative and a row past 65535 are skipped");
        check.Expect(Parse("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17").Length == 16, "seventeen rows keep the first sixteen");
        VfxRow[] parts = Parse("147:1, 147:5,147:1, 9:300, 9:x, 12:2:3, 7:255");
        check.Expect(parts.Length == 3 && parts[0] == new VfxRow(147, 1) && parts[1] == new VfxRow(147, 5) && parts[2] == new VfxRow(7, 255),
                     "a row names its part, the same row on two parts is worn twice, and a bad part skips the entry");
    }
}
