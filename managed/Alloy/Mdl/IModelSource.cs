namespace CX.Alloy.Mdl;

/// <summary>Where a model's bytes come from, split as the threading demands, and the seam that keeps
/// <c>Alloy/Mdl/</c> engine-free. <see cref="Locate"/> and <see cref="StillValid"/> run on the GAME thread and are
/// the only halves allowed to touch what the game owns; <see cref="Locate"/>'s opaque token must be a VALUE record,
/// since the worker reads it. <see cref="Parse"/> runs on <c>cx-model</c> and does the read and the parse.</summary>
public interface IModelSource
{
    /// <summary>Game thread: where the model named <paramref name="resref"/> lives, or null with
    /// <paramref name="why"/> set. Never reads the bytes.</summary>
    object? Locate(string resref, out string? why);

    /// <summary>Worker thread: read and parse what <see cref="Locate"/> found. Null with
    /// <paramref name="why"/> set is a refusal the cache counts and logs.</summary>
    ModelDef? Parse(object token, out string? why);

    /// <summary>Game thread, before the parsed model is filed: does <paramref name="token"/> still name the
    /// resource it named when it was located? A source whose lookup cannot move under a read keeps this default;
    /// the engine feed re-runs the lookup and compares the <c>RESID</c>, so a resref aliased mid-read costs a
    /// counted miss rather than the wrong model filed under the right name.</summary>
    bool StillValid(object token, out string? why) { why = null; return true; }

    /// <summary>Game thread, after <see cref="Parse"/> refused: one more read of <paramref name="token"/> by a path
    /// only this thread may take, or null with <paramref name="why"/> set. The default has no second path.</summary>
    ModelDef? Recover(object token, out string? why) { why = null; return null; }
}
