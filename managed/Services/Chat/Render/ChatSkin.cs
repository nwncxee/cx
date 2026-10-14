using CX.Framework;
using CX.Ui;

namespace CX.Services.Chat.Render;

/// <summary>The chat windows' share of the native look: one <see cref="NativeChrome"/> skin for every chat
/// window, probed once a frame on the render thread. <see cref="Native"/> is what the cold build asks before it
/// lays game art instead of flat fills, so a refused probe draws the flat look rather than nothing.</summary>
internal static class ChatSkin
{
    private sealed class ChatSkinState
    {
        public readonly Skin Skin = NativeChrome.NewSkin();
        public bool Armed;
        public int Epoch = -1;
        public bool WasNative;
    }

    private static ChatSkinState _s = new();

    /// <summary>True while the native look is on and its art is whole: the chrome is the game's, not flat fills.</summary>
    internal static bool Native => _s.Armed;

    /// <summary>Re-probe the art for this frame; once per frame, before any build or draw. Cheap after the
    /// first (a field compare) unless the server policy moved.</summary>
    internal static void Probe(ExtenderLog? log) => _s.Armed = Theme.IsNative && _s.Skin.Arm(log, "");

    /// <summary>The armed skin with its batches cleared, ready for one window's commands; null in the flat look.</summary>
    internal static Skin? Begin()
    {
        if (!_s.Armed) return null;
        _s.Skin.Begin();
        return _s.Skin;
    }

    /// <summary>True once the look -- or the art's verdict on it -- changed since the last call; the owner
    /// re-lays every window's chrome.</summary>
    internal static bool LookChanged()
    {
        if (_s.Epoch == Theme.Epoch && _s.WasNative == _s.Armed) return false;
        _s.Epoch = Theme.Epoch;
        _s.WasNative = _s.Armed;
        return true;
    }

    /// <summary>Module edge: probe the art again, since the next session's haks may carry different parts.</summary>
    internal static void Rearm() => _s.Skin.Rearm();
}
