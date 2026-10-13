using System;
using System.Globalization;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The visual effects a model view's doll wears: only the <c>visualeffects.2da</c> rows the server lists, put on the doll through
/// the engine's own add call. The client cannot tell a permanent effect from a temporary one, so the server chooses.
/// Linux only: the add call takes its transform by value, which Win64 hands to the callee to destroy.
/// </summary>
internal static unsafe class NuiModelVfx
{
    /// <summary>The most effects one doll wears.</summary>
    internal const int MaxRows = 16;
    /// <summary>The beam target of an effect that is no beam.</summary>
    private const uint NoTarget = 0x7f000000;   // not-an-offset: INVALID_OBJECT_ID

    private sealed class VfxState
    {
        public ExtenderLog? Log;
        public IntPtr Identity;
        public bool Ready, Said;
    }

    private static readonly VfxState _v = new();

    internal static void Init(ExtenderLog log)
    {
        _v.Log = log;
        _v.Ready = SymbolMap.Resolve(SymbolMap.ObjAddVisualEffect, log) != IntPtr.Zero
                   && SymbolMap.Resolve(SymbolMap.ObjKillAllVisualEffects, log) != IntPtr.Zero;
        _v.Identity = _v.Ready ? SymbolMap.Resolve(SymbolMap.IdentityVisualTransform, log) : IntPtr.Zero;
        _v.Ready &= _v.Identity != IntPtr.Zero;
        if (!_v.Ready) log.Always("NuiModelVfx: the engine's effect calls did not resolve -- dolls wear no effects");
    }

    /// <summary>A <c>vfx</c> value, <c>row,row,...</c>, as its rows. Text that is not a row is skipped, and so is anything past <see cref="MaxRows"/>.</summary>
    internal static ushort[] Parse(string value)
    {
        if (value.Length == 0) return Array.Empty<ushort>();
        var rows = new System.Collections.Generic.List<ushort>();
        foreach (string part in value.Split(','))
        {
            if (rows.Count == MaxRows) break;
            if (ushort.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort row) && !rows.Contains(row)) rows.Add(row);
        }
        return rows.ToArray();
    }

    /// <summary>The doll wears exactly <paramref name="rows"/>: every effect it had is ended first.</summary>
    internal static void Wear(IntPtr doll, ushort[] rows)
    {
        if (doll == IntPtr.Zero) return;
        if (!_v.Ready)
        {
            if (rows.Length > 0 && !_v.Said) { _v.Said = true; _v.Log?.Always("NuiModelVfx: this client puts no effects on a doll -- vfx ignored"); }
            return;
        }
        EngineFn.ObjKillAllVisualEffects(doll);
        Vec3 zero = default;
        foreach (ushort row in rows)
            if (EngineFn.ObjAddVisualEffect(doll, row, 0, NoTarget, 0, 0, VectorArg.Of(&zero), _v.Identity) == 0)
                _v.Log?.Always($"NuiModelVfx: the engine would not load effect row {row}");
        if (rows.Length > 0) _v.Log?.Always($"NuiModelVfx: the doll wears effect rows {string.Join(',', rows)}");
    }

    /// <summary>The doll is going: end its effects while the creature is still whole.</summary>
    internal static void Clear(IntPtr doll)
    {
        if (_v.Ready && doll != IntPtr.Zero) EngineFn.ObjKillAllVisualEffects(doll);
    }

    /// <summary>Oracle: hand-written <c>vfx</c> values and the rows they name.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Parse("").Length == 0, "an empty value names no effect");
        ushort[] rows = Parse("147, 408,147");
        check.Expect(rows.Length == 2 && rows[0] == 147 && rows[1] == 408, "rows keep their order and a repeat is dropped");
        check.Expect(Parse("x,-1,70000,13").Length == 1 && Parse("x,-1,70000,13")[0] == 13, "text, a negative and a row past 65535 are skipped");
        check.Expect(Parse("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17").Length == 16, "seventeen rows keep the first sixteen");
    }
}
