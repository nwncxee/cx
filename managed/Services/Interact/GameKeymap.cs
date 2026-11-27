using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Interact;

/// <summary>The game's own Key Mapping as the engine holds it: the remappable action a key runs with no modifier, named as
/// the Key Mapping panel names it. Cold: a key row's bind.</summary>
internal static unsafe class GameKeymap
{
    /// <summary>The engine keys AddEvent looks a key-down's scancode up in, the keyboard's.</summary>
    private const int FirstKey = 10, LastKey = 0x69;  // not-an-offset: engine key ids
    /// <summary>The actions SetEventDescriptions reads from [Keymapping] and keymap.2da, the remappable ones.</summary>
    private const uint FirstAction = 37, LastAction = 108;  // not-an-offset: input action ids
    /// <summary>More descriptions than the engine ever adds to one list; a longer walk is a broken list.</summary>
    private const int MaxWalk = 512;

    /// <summary>The Key Mapping name of the action SDL key code <paramref name="code"/> runs in the game with no modifier;
    /// null when none does, or when the engine cannot be read.</summary>
    internal static string? Action(int code)
    {
        if (code <= 0 || EngineFn.SdlGetScancodeFromKey == null) return null;
        IntPtr slot = Native.Sym(SymbolMap.ExoInput);
        int action = ActionOf(slot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)slot, EngineFn.SdlGetScancodeFromKey(code));
        return action == 0 ? null : Name(action);
    }

    /// <summary>The remappable action engine key table <paramref name="exoInput"/> runs on <paramref name="scancode"/> with
    /// no modifier, 37 to 108; 0 when none does.</summary>
    internal static int ActionOf(IntPtr exoInput, int scancode)
    {
        if (exoInput == IntPtr.Zero || scancode <= 0) return 0;
        CExoInputInternal engine = new CExoInput(exoInput).Internal;
        if (engine.IsNull || engine.DescLists.IsNull || engine.DescLists.Keyboard.IsNull) return 0;
        Span<int> table = engine.KeyScancodes;
        int key = table.Slice(FirstKey, LastKey - FirstKey + 1).IndexOf(scancode);
        if (key < 0) return 0;
        key += FirstKey;
        int walked = 0;
        for (CExoLinkedListNode node = engine.DescLists.Keyboard.Head; !node.IsNull && walked++ < MaxWalk; node = node.Next)
        {
            var desc = new CExoInputEventDesc(node.Data.Ptr);
            if (!desc.IsNull && desc.Key == (uint)key && desc.Action is >= FirstAction and <= LastAction) return (int)desc.Action;
        }
        return 0;
    }

    /// <summary>Action <paramref name="action"/>'s name: keymap.2da's ActionStrRef on row "Action&lt;n&gt;", as the Key Mapping
    /// panel shows it. Null when the table or the cell cannot be read.</summary>
    private static string? Name(int action)
    {
        IntPtr slot = Native.Sym(SymbolMap.Rules);
        IntPtr rules = slot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)slot;
        if (rules == IntPtr.Zero || EngineFn.C2DAGetINTEntryByLabel == null || EngineFn.ExoStringInit == null || EngineFn.ExoStringDtor == null)
            return null;
        IntPtr arrays = new CNWRules(rules).TwoDimArrays;
        IntPtr keymap = arrays == IntPtr.Zero ? IntPtr.Zero : new CTwoDimArrays(arrays).Keymap;
        if (keymap == IntPtr.Zero) return null;
        ExoStringRec row = default, column = default;
        Init(&row, "Action" + action.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Init(&column, "ActionStrRef");
        int strref = -1;
        int found = EngineFn.C2DAGetINTEntryByLabel(keymap, (IntPtr)(&row), (IntPtr)(&column), &strref);
        EngineFn.ExoStringDtor((IntPtr)(&column));
        EngineFn.ExoStringDtor((IntPtr)(&row));
        return found != 0 && strref >= 0 ? Native.TlkText((uint)strref) : null;
    }

    private static void Init(ExoStringRec* into, string ascii)
    {
        byte* text = stackalloc byte[ascii.Length + 1];
        for (int i = 0; i < ascii.Length; i++) text[i] = (byte)ascii[i];
        text[ascii.Length] = 0;
        EngineFn.ExoStringInit((IntPtr)into, text, uint.MaxValue);
    }
}
