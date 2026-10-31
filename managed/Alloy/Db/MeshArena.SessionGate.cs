using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

public static partial class MeshArena
{
    /// <summary><see cref="ArenaSelfCheck"/> off a session only: its area hops build half a gigabyte of meshes, which a
    /// player's boot does not pay for.</summary>
    internal static void ArenaSelfCheckOffSession(ExtenderLog log)
    {
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
            new Checker(log, "MeshArena.ArenaSelfCheck").Skip("in a session: the arena's area hops run in run-demo, not on a player's boot");
        else ArenaSelfCheck(log);
    }
}
