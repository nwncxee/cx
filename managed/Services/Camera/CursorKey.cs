using CX.Engine;
using CX.Framework;

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
    internal readonly CursorCodes[] Codes = new CursorCodes[CameraModes.ByMode.Length];
    /// <summary>The bound keys down now, from their press to their key-up; 0 when none.</summary>
    internal CursorCodes Down;
    /// <summary>The toggle's latch, with the mode's <c>alttoggle</c> on: a tap freed the cursor until the next tap.</summary>
    internal bool Free;

    /// <summary>Reads each mode's <c>cursorkey</c> into <see cref="Codes"/>. Cold: a setting's edit.</summary>
    internal void KeysChanged()
    {
        for (int i = 0; i < Codes.Length; i++) Codes[i] = Parse(CameraModes.ByMode[i].CursorKey.V);
        Reset();
    }

    /// <summary>One event from WinMessageHandler, in camera mode <paramref name="mode"/>, with the action camera's gate
    /// open or not. True when the event is the cursor key's and the game never sees it.</summary>
    internal bool Feed(SdlEvent* ev, bool gateOpen, int mode)
    {
        if (ev->Type == MouseLookService.SdlKeyDown) return ev->Repeat == 0 ? Press(ev->Sym, gateOpen, mode) : IsDown(ev->Sym) && Swallowed(ev->Sym);
        if (ev->Type == MouseLookService.SdlKeyUp) return Lift(ev->Sym);
        if (ev->Button is not (4 or 5)) return false;
        if (ev->Type == MouseLookService.SdlMouseDown) return Press(-ev->Button, gateOpen, mode);
        if (ev->Type == MouseLookService.SdlMouseUp) return Lift(-ev->Button);
        return false;
    }

    /// <summary>"Alt" is either Alt key, as SDL names neither so; any other name is the one press <see cref="KeyNames"/> reads.</summary>
    private static CursorCodes Parse(string name)
        => name.Trim().Equals("Alt", System.StringComparison.OrdinalIgnoreCase)
            ? new CursorCodes { A = (int)MouseLookService.SdlkLAlt, B = (int)MouseLookService.SdlkRAlt }
            : new CursorCodes { A = KeyNames.Code(name) };

    /// <summary>A bound key goes down while the gate is open: it is held until its key-up, and with the toggle on it
    /// flips the latch. True when the game never sees it.</summary>
    private bool Press(int code, bool gateOpen, int mode)
    {
        if (!gateOpen || code == 0 || (uint)mode >= (uint)Codes.Length || (Codes[mode].A != code && Codes[mode].B != code)) return false;
        if (!IsDown(code))
        {
            if (Down.A == 0) Down.A = code;
            else Down.B = code;
        }
        if (CameraModes.ByMode[mode].AltToggle.V) Free = !Free;
        return Swallowed(code);
    }

    /// <summary>A key comes up: a bound key held since its press is let go, whatever the gate says now. True when the
    /// game never sees it.</summary>
    private bool Lift(int code)
    {
        if (!IsDown(code)) return false;
        if (Down.A == code) Down.A = 0;
        if (Down.B == code) Down.B = 0;
        return Swallowed(code);
    }

    private bool IsDown(int code) => code != 0 && (Down.A == code || Down.B == code);

    /// <summary>A modifier key reaches the game whatever it binds: the game reads Alt as it always has.</summary>
    private static bool Swallowed(int code) => !KeyNames.IsModifier(code);

    internal bool Freed(int mode)
        => (uint)mode < (uint)Codes.Length && CameraModes.ByMode[mode].AltToggle.V ? Free : Down.A != 0 || Down.B != 0;

    /// <summary>Forgets the keys down and the toggle's latch.</summary>
    internal void Reset() { Down = default; Free = false; }
}
