using System;
using CX.Framework;

namespace CX.Engine;

public readonly partial struct CAurBehavior
{
    /// <summary>The camera object that owns a camera behaviour: the Gob sub-object it holds sits
    /// <see cref="EngineLayout.SeamCameraGobSub"/> bytes into the camera.</summary>
    public IntPtr OwnerCamera => Owner - EngineLayout.SeamCameraGobSub;
}
