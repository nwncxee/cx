namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for records the Alloy seam and the database animation path build by hand.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary><c>CallBack</c>: the event name, a NUL-terminated array inline at the start of the record.</summary>
    public static readonly int SeamCallBackName = Field<byte>("CallBack", "seamName", count: 0x20);
}
