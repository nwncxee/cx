using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Interact;

/// <summary><see cref="GameKeymap.ActionOf"/> over a CExoInput built in memory as SetEventDescriptions leaves the real one.
/// Oracle: the key table entries and event descriptions written here.</summary>
internal static unsafe class GameKeymapSelfCheck
{
    /// <summary>The scancodes of Q, I and W (SDL_SCANCODE_Q and so on), and the engine keys the constructor gives them.</summary>
    private const int ScanQ = 0x14, ScanI = 0x0c, ScanW = 0x1a, KeyQ = 0x5f, KeyI = 0x57, KeyW = 0x65;  // not-an-offset: scancodes and engine key ids
    private const int ShiftMod = 0x100, QuickChat = 64, Inventory = 63, F1Help = 5;  // not-an-offset: modifier and action ids
    private const int DescSize = 0x28, NodeSize = 0x18, InnerSize = 0x200;  // not-an-offset: sizes of the fake objects

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GameKeymap.SelfCheck");
        IntPtr input = Alloc(0x10), inner = Alloc(InnerSize), lists = Alloc(2 * sizeof(IntPtr)), keys = Alloc(0x10);
        var blocks = new System.Collections.Generic.List<IntPtr> { input, inner, lists, keys };
        try
        {
            new CExoInput(input).Internal = new CExoInputInternal(inner);
            var engine = new CExoInputInternal(inner);
            engine.DescLists = new CExoInputDescLists(lists);
            engine.DescLists.Keyboard = new CExoLinkedListInternal(keys);
            Span<int> table = engine.KeyScancodes;
            table[KeyQ] = ScanQ; table[KeyI] = ScanI; table[KeyW] = ScanW;
            Add(blocks, keys, F1Help, KeyQ);
            Add(blocks, keys, QuickChat + 1, KeyQ | ShiftMod);
            Add(blocks, keys, Inventory, KeyI);
            Add(blocks, keys, QuickChat, KeyQ);

            check.Expect(GameKeymap.ActionOf(input, ScanQ) == QuickChat,
                $"Q runs Quick Chat (64) with no modifier, past a fixed action and a Shift+Q one: {GameKeymap.ActionOf(input, ScanQ)}");
            check.Expect(GameKeymap.ActionOf(input, ScanI) == Inventory, $"I runs Inventory (63): {GameKeymap.ActionOf(input, ScanI)}");
            check.Expect(GameKeymap.ActionOf(input, ScanW) == 0, $"W, an engine key no description names, runs nothing: {GameKeymap.ActionOf(input, ScanW)}");
            check.Expect(GameKeymap.ActionOf(input, 0x04) == 0, $"A, in no engine key, runs nothing: {GameKeymap.ActionOf(input, 0x04)}");
            check.Expect(GameKeymap.ActionOf(IntPtr.Zero, ScanQ) == 0, "no CExoInput reads as nothing");
        }
        finally
        {
            foreach (IntPtr b in blocks) NativeMemory.Free((void*)b);
        }
        log.Info(check.Ok ? "GameKeymap.SelfCheck: passed" : "GameKeymap.SelfCheck: FAILED");
        return check.Ok;
    }

    private static IntPtr Alloc(int size) => (IntPtr)NativeMemory.AllocZeroed((nuint)size);

    /// <summary>Puts a description at the head of <paramref name="list"/>, as AddInputEventDesc's AddHead does.</summary>
    private static void Add(System.Collections.Generic.List<IntPtr> blocks, IntPtr list, int action, int key)
    {
        IntPtr desc = Alloc(DescSize), node = Alloc(NodeSize);
        blocks.Add(desc); blocks.Add(node);
        var d = new CExoInputEventDesc(desc);
        d.Action = (uint)action; d.Key = (uint)key;
        var n = new CExoLinkedListNode(node);
        n.Data = new CNWCVisualEffectOnObject(desc);
        var l = new CExoLinkedListInternal(list);
        n.Next = l.Head;
        l.Head = n;
    }
}
