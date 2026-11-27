using CX.Engine;

namespace CX.Services.Camera;

/// <summary>The SDL codes one camera mode's cursor key presses: both Alt keys for "Alt", else the one key or thumb
/// button (-4 / -5) its name gives; 0 for none.</summary>
internal struct CursorCodes
{
    internal int A, B;
}

/// <summary>The action camera's cursor key: the codes each mode's <c>cursorkey</c> binds, read at its edit, the bound
/// keys down now and the toggle's latch. One lives in <see cref="MouseLookService"/>.</summary>
internal sealed unsafe class CursorKeyState
{
    /// <summary>The codes per camera mode, indexed like <see cref="CameraModes.ByMode"/>.</summary>
    internal readonly CursorCodes[] Codes = new CursorCodes[3];

    /// <summary>Reads each mode's <c>cursorkey</c> into <see cref="Codes"/>. Cold: a setting's edit.</summary>
    internal void KeysChanged() { }

    /// <summary>One event from WinMessageHandler, in camera mode <paramref name="mode"/>, with the action camera's gate
    /// open or not. True when the event is the cursor key's and the game never sees it.</summary>
    internal bool Feed(SdlEvent* ev, bool gateOpen, int mode) => false;

    /// <summary>Whether the cursor key frees the cursor in camera mode <paramref name="mode"/> now.</summary>
    internal bool Freed(int mode) => false;

    /// <summary>Forgets the keys down and the toggle's latch.</summary>
    internal void Reset() { }
}
