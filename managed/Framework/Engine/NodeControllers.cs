using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>An MdlNode's <c>NewController</c> records lie end to end at
/// <see cref="EngineLayout.MdlNodeControllers"/>, <see cref="EngineLayout.NewControllerStride"/> apart. The
/// stride is a record SIZE rather than a field row, so the indexer is a concept here; the id the controller
/// writes is the record's first word and needs no offset at all.</summary>
public readonly unsafe partial struct NewController
{
    /// <summary>Record <paramref name="i"/> counted from this one -- the array's head is the node's
    /// <see cref="EngineLayout.MdlNodeControllers"/> pointer, its length
    /// <see cref="EngineLayout.MdlNodeControllerCount"/>.</summary>
    public NewController this[int i] => new(Ptr + i * EngineLayout.NewControllerStride);
    /// <summary>The target-field id: the raw <c>Part</c> byte offset this controller writes, so it compares
    /// against a layout row (<see cref="EngineLayout.LightRange"/> is the <c>radius</c> track).</summary>
    public ref int Id => ref *(int*)Ptr;
}
