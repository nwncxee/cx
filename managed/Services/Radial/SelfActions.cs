using System;
using System.Collections.Generic;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Radial;

/// <summary>
/// Self pins: an action the self menu fires, shown on any root and fired exactly as the self menu would. Its leaves
/// are <c>CGuiCreatureMenu</c> / <c>CClientSpellMenu</c> member functions reading only the menu's target fields, so CX
/// builds a <b>stand-in</b> of each class (the engine's own ctor, the local creature as target, torn down with the held
/// root). <see cref="Table"/> is the whole safety story and the clonability rule: a <c>self:&lt;key&gt;</c> row names a table entry, resolved by <see cref="SymbolMap.Resolve"/>, and nothing else. ponytail: a leaf outside it is not offered.
/// </summary>
internal static unsafe class SelfActions
{
    public const string Prefix = "self:";

    /// <summary>One invokable handler: its persisted key, its symbol, and which stand-in class it is a member of.</summary>
    private readonly record struct Handler(string Key, string Symbol, bool Spell);

    private static readonly Handler[] Table =
    {
        new("feat", SymbolMap.CreatureMenuUseFeat, false), new("emote", SymbolMap.CreatureMenuEmote, false),
        new("mode", SymbolMap.CreatureMenuToggleMode, false), new("chat", SymbolMap.CreatureMenuQuickChat, false),
        new("skill", SymbolMap.CreatureMenuUseSkill, false), new("rest", SymbolMap.CreatureMenuRest, false),
        new("sheet", SymbolMap.CreatureMenuCharacterSheet, false),
        new("cast", SymbolMap.SpellMenuCast, true), new("sla", SymbolMap.SpellMenuCastSla, true),
        new("sfeat", SymbolMap.SpellMenuUseFeat, true),
    };

    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.CreatureMenuUseFeat, SymbolMap.CreatureMenuEmote, SymbolMap.CreatureMenuToggleMode, SymbolMap.CreatureMenuQuickChat,
        SymbolMap.CreatureMenuUseSkill, SymbolMap.CreatureMenuRest, SymbolMap.CreatureMenuCharacterSheet, SymbolMap.SpellMenuCast,
        SymbolMap.SpellMenuCastSla, SymbolMap.SpellMenuUseFeat, SymbolMap.SpellMenuTargetIsValid, SymbolMap.CreatureMenuCtor,
        SymbolMap.SpellMenuCtor, SymbolMap.OperatorNew,
    };

    /// <summary>Resolved handler addresses, <see cref="Table"/> order; zero = unresolved.</summary>
    private static IntPtr[] _addr = Array.Empty<IntPtr>();
    private static OperatorNewFn _new;
    private static SpellMenuTargetIsValidFn _targetIsValid;
    // Left raw: the ctors take their Vector BY VALUE and Itanium packs it into the FIRST TWO argument slots
    // (xmm0 = x|y, xmm1 = z) while Win64 passes a pointer after `this` -- the parameter LISTS differ, so no
    // single [Fn] signature describes either symbol.
#if WINDOWS
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr> _creatureCtor;
    private static delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, int, IntPtr> _spellCtor;
#else
    private static delegate* unmanaged<double, float, IntPtr, IntPtr, void> _creatureCtor;
    private static delegate* unmanaged<double, float, IntPtr, uint, IntPtr, int, void> _spellCtor;
#endif
    /// <summary>The stand-ins of the current open, built on first use, released with the root.</summary>
    private static IntPtr _creature, _spell;
    private static ExtenderLog? _log;
    private static bool _warned;

    /// <summary>Every symbol resolved: self pins resolve and fire, and "Show everywhere" is offered.</summary>
    public static bool Ready { get; private set; }

    public static bool Init(ExtenderLog log)
    {
        _log = log;
        if (!SymbolMap.Require(log, "SelfActions", RequiredSymbols)) return Ready = false;
        _addr = new IntPtr[Table.Length];
        for (int i = 0; i < Table.Length; i++) _addr[i] = SymbolMap.Resolve(Table[i].Symbol, log);
        _new = (OperatorNewFn)SymbolMap.Resolve(SymbolMap.OperatorNew, log);
        _targetIsValid = (SpellMenuTargetIsValidFn)SymbolMap.Resolve(SymbolMap.SpellMenuTargetIsValid, log);
#if WINDOWS
        _creatureCtor = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)SymbolMap.Resolve(SymbolMap.CreatureMenuCtor, log);
        _spellCtor = (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, int, IntPtr>)SymbolMap.Resolve(SymbolMap.SpellMenuCtor, log);
#else
        _creatureCtor = (delegate* unmanaged<double, float, IntPtr, IntPtr, void>)SymbolMap.Resolve(SymbolMap.CreatureMenuCtor, log);
        _spellCtor = (delegate* unmanaged<double, float, IntPtr, uint, IntPtr, int, void>)SymbolMap.Resolve(SymbolMap.SpellMenuCtor, log);
#endif
        Ready = Array.IndexOf(_addr, IntPtr.Zero) < 0 && _new != null && _targetIsValid != null && _creatureCtor != null && _spellCtor != null;
        return Ready;
    }

    public static bool IsSelf(RadialPin pin) => pin.Fn.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The <c>self:&lt;key&gt;</c> token of a leaf whose entry is a table handler on a stand-in class (a
    /// non-virtual call on the menu object itself), else null.</summary>
    public static string? KeyOf(in RadialSlot s)
    {
        if (s.Kind != SlotKind.Entry || s.IsSubMenu || s.Fn == IntPtr.Zero || ((long)s.Fn & 1) != 0) return null;
        int i = Array.IndexOf(_addr, s.Fn);
        if (i < 0) return null;
        RootKind cls = RadialTree.Classify(s.Obj + s.Adj);
        return cls == (Table[i].Spell ? RootKind.Spell : RootKind.Creature) ? Prefix + Table[i].Key : null;
    }

    private static int IndexOf(RadialPin pin)
    {
        if (!IsSelf(pin)) return -1;
        string key = pin.Fn.Substring(Prefix.Length);
        for (int i = 0; i < Table.Length; i++) if (Table[i].Key == key) return i;
        return -1;
    }

    /// <summary>A self pin's row: valid when its handler is known and its stand-in stands, greyed when a spell the
    /// self menu itself would grey (no valid target at self) or nothing can be built (no local creature).</summary>
    public static bool Resolve(RadialPin pin, out bool valid)
    {
        valid = false;
        int i = IndexOf(pin);
        if (i < 0 || !Ready) return false;
        IntPtr m = StandIn(Table[i].Spell);
        if (m == IntPtr.Zero) return true;
        valid = !Table[i].Spell || _targetIsValid(m, (uint)(pin.Param & 0xffff)) != 0;
        return true;
    }

    /// <summary>Invoke the pin's handler on its stand-in under the event-object bracket; false when nothing fired.</summary>
    public static bool Fire(RadialPin pin)
    {
        int i = IndexOf(pin);
        if (i < 0 || !Ready) return false;
        IntPtr m = StandIn(Table[i].Spell);
        if (m == IntPtr.Zero) return false;
        _log?.Info($"[radial] self-fire {pin.Fn} param=0x{pin.Param:x} on=0x{(long)m:x}");
        return RadialTree.InvokeOn(m, _addr[i], pin.Param);
    }

    /// <summary>The stand-in of one class, built on first use for this open: the engine's ctor on
    /// <c>operator new</c> memory, the local creature (with stats, as the ctors gate) as target and its position as
    /// the vector, then <c>m_bAutoDelete</c> so <see cref="Release"/> can hand it to RecursiveAutoDelete. Zero when
    /// there is no such creature (logged once).</summary>
    private static IntPtr StandIn(bool spell)
    {
        IntPtr have = spell ? _spell : _creature;
        if (have != IntPtr.Zero) return have;
        IntPtr creature = PlayerIdentity.Creature();
        if (creature == IntPtr.Zero)
        {
            if (!_warned) { _warned = true; _log?.Info("[radial] self pin: no local creature with stats, rows greyed (logged once)"); }
            return IntPtr.Zero;
        }
        // A local copy: the ctors take the Vector BY VALUE, an ABI that differs per OS.
        System.Numerics.Vector3 pos = new CNWCObject(creature).Position;
        IntPtr m = _new((nuint)(spell ? EngineLayout.SpellMenuSize : EngineLayout.CreatureMenuSize));
        if (m == IntPtr.Zero) return IntPtr.Zero;
#if WINDOWS
        if (spell) _spellCtor(m, (IntPtr)(&pos), new CNWCObject(creature).Id, IntPtr.Zero, 0);
        else _creatureCtor(m, creature, (IntPtr)(&pos));
#else
        double xy = BitConverter.Int64BitsToDouble(*(long*)&pos);   // SysV: a 3-float aggregate rides xmm0 (x|y) + xmm1 (z)
        if (spell) _spellCtor(xy, pos.Z, m, new CNWCObject(creature).Id, IntPtr.Zero, 0);
        else _creatureCtor(xy, pos.Z, m, creature);
#endif
        new CRadialMenu(m).AutoDelete = 1;
        _log?.Info($"[radial] self stand-in {(spell ? "spell" : "creature")}=0x{(long)m:x} target=0x{new CNWCObject(creature).Id:x}");
        if (spell) _spell = m; else _creature = m;
        return m;
    }

    /// <summary>Tear the open's stand-ins down (RecursiveAutoDelete deletes an auto-delete menu with no children).</summary>
    public static void Release()
    {
        if (_creature != IntPtr.Zero) { RadialTree.Teardown(_creature); _creature = IntPtr.Zero; }
        if (_spell != IntPtr.Zero) { RadialTree.Teardown(_spell); _spell = IntPtr.Zero; }
    }

    /// <summary>Off-device: the token round-trip against a planted address table (the real one needs the client),
    /// the class check, and that an unknown key resolves to nothing.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        IntPtr[] saved = _addr;
        bool wasReady = Ready;
        _addr = new IntPtr[Table.Length];
        for (int i = 0; i < Table.Length; i++) _addr[i] = (IntPtr)(0x1000 + i * 0x10);
        try
        {
            var leaf = new RadialSlot(0, SlotKind.Entry, "Rest", "ir_rest", "", false, 0, IntPtr.Zero, _addr[5], IntPtr.Zero, IntPtr.Zero);
            check.Expect(KeyOf(leaf) == null, "a handler on an object of no known class is not a self action");
            check.Expect(KeyOf(leaf with { Fn = (IntPtr)((long)_addr[5] + 1) }) == null && KeyOf(leaf with { Fn = (IntPtr)0x2000 }) == null && KeyOf(leaf with { Kind = SlotKind.Creator }) == null,
                "a virtual thunk, a foreign fn, a submenu: none");
            var pin = new RadialPin { Fn = Prefix + "rest", Param = 0 };
            check.Expect(IsSelf(pin) && IndexOf(pin) == 5 && IndexOf(new RadialPin { Fn = Prefix + "nope" }) < 0 && IndexOf(new RadialPin { Fn = "x" }) < 0,
                "self: keys index the table; unknown keys and hoist tokens do not");
            Ready = false;
            check.Expect(!Resolve(pin, out bool v) && !v && !Fire(pin), "not ready: nothing resolves or fires");
        }
        finally { _addr = saved; Ready = wasReady; }
        log.Info(check.Ok ? "SelfActions.SelfCheck: passed" : "SelfActions.SelfCheck: FAILED");
    }
}
