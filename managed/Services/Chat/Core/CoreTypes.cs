namespace CX.Services.Chat.Core;

// The chat core's one-line enums and structs, collected. A type only belongs here while its whole
// declaration fits on one line; anything that grows a body goes back to its own file.

/// <summary>The six sendable chat channels. Each maps to a vanilla
/// <c>CNWCMessage::SendPlayerToServerChat_*</c> sender (major 0x09; see ChatService.Send).
/// <see cref="Tell"/> alone carries a target playerId; the rest are proximity/context routed.</summary>
public enum Channel { Talk, Shout, Whisper, Tell, Party, DM }

/// <summary>What happens when a Tell arrives from a player without an existing pinned tab.</summary>
public enum TellAutoOpen { Off, NewTab, NewWindow }

/// <summary>What Tell auto-open routing decided.</summary>
public enum TellRoute { None, Exists, NewTab, NewWindow }

/// <summary>Per-window text alignment for chat message body lines.</summary>
public enum TextAlign { Left, Right, Center, Justify }

/// <summary>Timestamp presentation preset. ponytail: a fixed set of presets; a free-form format string is
/// the ceiling (needs a text field in the settings window -- a whole input control for one value).</summary>
public enum TsFormat { HourMin, HourMinSec, Hour12 }

/// <summary>Where a line's timestamp is read from. <see cref="Local"/> = the real wall clock (always
/// available). <see cref="Game"/> = the NWN in-game world clock (captured at ingest; falls back to local
/// when the world timer isn't up, e.g. the main menu).</summary>
public enum TsSource { Local, Game }

/// <summary>Background fill of a chat window. Solid = the look's panel background alone; the gradient shapes
/// run it to the look's second background colour along the window's direction/stop settings: Linear along the
/// angle, Radial centre -> edge, Conic mirrored around the angle.</summary>
public enum BgMode { Solid, Linear, Radial, Conic }

/// <summary>A clickable portrait rect in screen pixels, carrying the line's PlayerId for tell targeting.</summary>
public readonly record struct PortraitHit(float X, float Y, float W, float H, uint PlayerId);
