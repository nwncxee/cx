using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Chat;

/// <summary>
/// CX commands dispatched from the game's OWN chat prompt, so live only while the CX chatbox is stood down
/// (<c>chat.enabled=0</c>). A chain on <c>CGuiInGameChatPrompt::HandleEnter</c> empties a known command line
/// before the original runs, so vanilla closes the prompt exactly as on an empty Enter -- including the unfocus
/// GUI event the server needs to drop its "typing" state -- and the command runs after; others go untouched.
/// </summary>
public sealed unsafe class PromptCommandService : IExtenderService
{
    /// <summary>Below the hotkey observer's layer on the same symbol: the observer sees every Enter first and always
    /// calls through; this layer is the one that decides what the original sees.</summary>
    public const int EnterPriority = 0;

    /// <summary><c>GUIINGAME_MESSAGE_CONSOLE</c>: the channel <c>ParseChatString</c> echoes client-only command
    /// output on (decompiled @0x7780b0: <c>AppendToMsgBuffer(gui, text, 0x10000000, 0, 0, "")</c>).</summary>
    private const uint ConsoleChannel = 0x10000000;  // not-an-offset: a chat channel mask

    private static ExtenderLog? _log;
    private static ChatHandleEnterFn* _slot;
    private static IntPtr _appMgr;
    /// <summary>The <c>""</c> handed to <c>CAurString::SetText</c>: the engine passes a string literal there, so the
    /// callee may keep the pointer -- give it memory that never moves.</summary>
    private static readonly IntPtr _empty = Marshal.AllocHGlobal(1);

    /// <summary>A zeroed 24-byte aggregate: a 17-byte <c>CResRef</c> reading <c>""</c> (char[16] + NUL, all zero),
    /// which SysV passes in memory like the real thing. Win64 hands a pointer to a copy instead.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct CResRef24 { public ulong A, B, C; }
    // Left raw: AppendToMsgBuffer's last parameter has a DIFFERENT shape per ABI, so no single
    // [Fn] signature describes it.
#if WINDOWS
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void> _append;
#else
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef24, void> _append;
#endif

    /// <summary>The unit -- every symbol this cannot run without: the Enter chain, the text read, the echo
    /// path and the app walk to it.</summary>
    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.ChatHandleEnter, SymbolMap.GuiEditGetText, SymbolMap.AppendToMsgBuffer, SymbolMap.AppManager,
        SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor,
    };

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        *(byte*)_empty = 0;
        if (!SymbolMap.Require(log, "PromptCommandService", RequiredSymbols)) return;
        if (!EngineLayout.Require(log, "PromptCommandService", "CGuiInGameChatPrompt", "CGuiEdit", "CAurString",
                                  "CClientExoApp", "CClientExoAppInternal")) return;
        _appMgr = Native.Sym(SymbolMap.AppManager);
#if WINDOWS
        _append = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void>)Native.Sym(SymbolMap.AppendToMsgBuffer);
#else
        _append = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef24, void>)Native.Sym(SymbolMap.AppendToMsgBuffer);
#endif
        _slot = hooks.ChainChatHandleEnter(&OnEnter, EnterPriority);
        log.Info(_slot != null ? "PromptCommandService: vanilla prompt dispatch chained on HandleEnter"
                               : "PromptCommandService: HandleEnter chain refused (single-owner hook on the symbol)");
    }

    [UnmanagedCallersOnly]
    private static void OnEnter(IntPtr prompt)
    {
        CGuiEdit edit = new CGuiInGameChatPrompt(prompt).Edit;
        string line = ReadLine(edit);
        bool hit = CommandRegistry.Global.Knows(line) && Clear(edit);
        if (_slot != null) (*_slot)(prompt);
        if (!hit) return;
        if (CommandRegistry.Global.TryDispatch(line, out string? reply) && reply != null) Echo(reply);
    }

    /// <summary>What vanilla reads: <c>CGuiEdit::GetText</c> (sret, caller-owned buffer), cut at the first line break
    /// the way HandleEnter's two <c>Find</c>/<c>Left</c> passes do. Latin1, as the chatbox reads engine text.</summary>
    private static string ReadLine(CGuiEdit edit)
    {
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;   // the dtor below frees whatever buffer lands here
        edit.GuiEditGetText(exo);
        byte* buf = *(byte**)exo;
        uint len = ((ExoStringRec*)exo)->Len;
        var sb = new StringBuilder((int)Math.Min(len, 4096));
        for (uint k = 0; k < len && k < 4096; k++)
        {
            if (buf[k] == '\n' || buf[k] == '\r') break;
            sb.Append((char)buf[k]);
        }
        EngineFn.ExoStringDtor((IntPtr)exo);
        return sb.ToString();
    }

    /// <summary>One-shot VtblSlot probe on the first CAurString seen; a failed probe is rot and the clear is never called.</summary>
    private static bool? _probeOk;

    /// <summary>The clear HandleEnter itself does first: <c>m_pString->SetText("")</c> through the CAurString vtable.
    /// Returns false when the slot failed its probe, so the line is forwarded as chat rather than dispatched half-way.</summary>
    private static bool Clear(CGuiEdit edit)
    {
        CAurString str = edit.String;
        if (str.IsNull) return false;
        _probeOk ??= EngineLayout.Probe("CAurString", str.Ptr, _log);
        if (_probeOk == false) return false;
        str.SetText(_empty);
        return true;
    }

    /// <summary>One vanilla chat-window line per reply line, on the console channel. <c>*g_pAppManager -&gt;
    /// CClientExoApp -&gt; internal -&gt; CGuiInGame</c>, the walk <c>GetInGameGui</c> is.</summary>
    private static void Echo(string reply)
    {
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return;
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal; if (intern.IsNull) return;
        CGuiInGame gui = intern.InGameGui; if (gui.IsNull) return;
#if WINDOWS
        byte* resref = stackalloc byte[32];
        for (int i = 0; i < 32; i++) resref[i] = 0;
#endif
        foreach (string line in reply.Split('\n'))
        {
            IntPtr exo = Native.NewExoString(line);
#if WINDOWS
            _append(gui.Ptr, exo, ConsoleChannel, 0, 0, (IntPtr)resref);
            *(ExoStringRec*)exo = default;   // Win64: the callee destroyed its by-value CExoString copy
#else
            _append(gui.Ptr, exo, ConsoleChannel, 0, 0, default);
#endif
            Native.FreeExoString(exo);
        }
    }
}
