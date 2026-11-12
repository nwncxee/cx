namespace CX.Services.Chat.Core;

/// <summary>Resolves between player names and live connection-scoped PlayerIds. The client implementation
/// walks the engine's player roster; tests inject a fake.</summary>
public interface IPlayerResolver
{
    /// <summary>Name → live PlayerId, or null when the player is offline / unknown.</summary>
    uint? Resolve(string name);

    /// <summary>PlayerId → CHARACTER name (never the account name), or null when the id is stale / unknown.
    /// The one name space every Tell pin lives in; a line's speaker name and incoming-tell routing both use it.</summary>
    string? NameOf(uint playerId) => null;

    /// <summary>The local player's ingest-space playerId, or null off-module / pre-load. Used to
    /// distinguish outbound Tell echoes (which carry your own id) from genuine incoming Tells.</summary>
    uint? LocalIngestId() => null;

    /// <summary>Every CHARACTER name on the roster, in roster order; Tab completion's fallback after the area.</summary>
    IReadOnlyList<string> OnlineNames() => Array.Empty<string>();
}
