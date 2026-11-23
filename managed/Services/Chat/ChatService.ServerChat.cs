using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;

namespace CX.Services.Chat;

public sealed unsafe partial class ChatService
{
    /// <summary>The server chat minor of the line the engine is handling on this stack, and the chain slot below it.</summary>
    private sealed class ServerChatState
    {
        public HandleServerToPlayerChatFn* Next;
        public int Minor = InboundChannel.NoMinor;
    }

    private static ServerChatState _serverChat = new();

    /// <summary>Below the nameplate's latch; the two only read the byte, so the order is free.</summary>
    private const int ServerChatPriority = 10;

    /// <summary>Latches the chat minor so the inbound tap, which runs on the same stack, can file a DM-channel line.</summary>
    private static void InitServerChat(ExtenderLog log, HookService hooks)
    {
        _serverChat.Next = hooks.ChainHandleServerToPlayerChat(&OnServerChat, ServerChatPriority);
        log.Info(_serverChat.Next != null
            ? "ChatService: chat minor latched (HandleServerToPlayerChat)"
            : "ChatService: chat minor not latched (symbol unresolved): DM-channel lines file as Shout");
    }

    [UnmanagedCallersOnly]
    private static void OnServerChat(IntPtr self, byte minor)
    {
        var s = _serverChat;
        int prev = s.Minor;
        s.Minor = minor;
        if (s.Next != null) (*s.Next)(self, minor);
        s.Minor = prev;
    }
}
