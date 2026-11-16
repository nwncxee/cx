using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// The wire contract with a server that implements hotkeys. ALL opcodes are PLACEHOLDERS the server must match;
/// payloads are ASCII on purpose (no endianness, trivially debuggable). Client -> server: KeyPress (code),
/// OpenUi (none), Revoke (CSV), GrantToken (<c>hex;CSV</c>), AbuseContact (email, once on login). Server ->
/// client: ArmSet (CSV) and PresentToken (hex proof of prior consent -- a server holding none restores none).
/// </summary>
internal static class HotkeyProtocol
{
    /// <summary>The hotkey unit: every symbol the four Hotkey* services cannot run without. Each service
    /// checks it first, so one unresolved member stands the whole feature down. Optional members are NOT here:
    /// OnGainFocus/OnLostFocus (stuck-key clear), g_pNuklear (NUI typing gate), the chat-prompt handlers
    /// (the vanilla-prompt typing observer, chat master off).</summary>
    public static readonly string[] RequiredSymbols =
    {
        CX.Framework.SymbolMap.HandleServerToPlayerMessage, CX.Framework.SymbolMap.HandleServerToPlayerLogin,
        CX.Framework.SymbolMap.SendPlayerToServerMessage, CX.Framework.SymbolMap.SendDeviceAdvertiseProperty,
        CX.Framework.SymbolMap.OkCancelCtor, CX.Framework.SymbolMap.OkCancelActivate,
        CX.Framework.SymbolMap.GuiMan, CX.Framework.SymbolMap.OperatorNew,
        CX.Framework.SymbolMap.HandleWMKeyDown, CX.Framework.SymbolMap.WinMessageHandler,
        CX.Framework.SymbolMap.ExoInput, CX.Framework.SymbolMap.GetKeyboardMode,
        CX.Framework.SymbolMap.SdlGetModState, CX.Framework.SymbolMap.SdlGetKeyName,
    };

    public const byte KeyPressMajor = 0x36, KeyPressMinor = 0x40;  // not-an-offset: a message opcode
    public const byte OpenUiMajor   = 0x36, OpenUiMinor   = 0x41;  // not-an-offset: a message opcode
    public const byte ArmSetMajor   = 0x36, ArmSetMinor   = 0x42;  // not-an-offset: a message opcode
    public const byte RevokeMajor   = 0x36, RevokeMinor   = 0x43;  // not-an-offset: a message opcode
    public const byte PresentTokenMajor = 0x36, PresentTokenMinor = 0x44;  // not-an-offset: a message opcode
    public const byte GrantTokenMajor   = 0x36, GrantTokenMinor   = 0x45;  // not-an-offset: a message opcode
    public const byte AbuseContactMajor = 0x36, AbuseContactMinor = 0x46;  // not-an-offset: a message opcode

    /// <summary>Where a server operator reports abuse of the extender. Sent on every login after CX_ENABLED.
    /// <see cref="Product.Contact"/>: the same address <c>/about</c> prints.</summary>
    public const string AbuseContact = Product.Contact;

    /// <summary>
    /// Mouse button N rides the KeyPress channel as MouseBase + N. Bit 28 keeps these clear
    /// of every SDLK (ASCII &lt; 128, scancode mask bit 30). MUST match the server end byte for byte.
    /// </summary>
    public const int MouseBase = 1 << 28;

    // Modifier bits OR'd onto a base code to make a combo (Ctrl+F) a distinct opaque code.
    // Bits 25/26/27 are free of every base (ASCII 0-6, scancode +30, mouse +28). Matching is
    // EXACT -- Ctrl+F never fires F. MUST match the server end byte for byte.
    public const int ModCtrl  = 1 << 25;
    public const int ModShift = 1 << 26;
    public const int ModAlt   = 1 << 27;
    public const int ModMask  = ModCtrl | ModShift | ModAlt;
}
