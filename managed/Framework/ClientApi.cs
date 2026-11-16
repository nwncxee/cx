using CX.Engine;

namespace CX.Framework;

/// <summary>
/// Small shared bridge to the client's message object, so services don't each re-resolve
/// the send plumbing. Lives in the framework layer: cross-cutting plumbing every feature calls,
/// not a feature of its own.
/// </summary>
public static unsafe class ClientApi
{
    /// <summary>The live CNWCMessage* captured from a hook that is handed one (inbound `self`, login `this`):
    /// the engine's own object. Zero until the first server message, so nothing sends before a connection.</summary>
    public static IntPtr LiveMessage;

    /// <summary>A live CClientExoApp* captured from the focus hooks (their `this`). A process-lifetime
    /// singleton, so one capture holds; lets services walk to the live in-game GUI without the flaky
    /// g_pAppManager global.</summary>
    public static IntPtr LiveExoApp;

    /// <summary>The message object every send goes through, or zero before the first server message. Never walked from
    /// <c>g_pAppManager</c>: at boot on Windows that chain can name an app the main thread has not finished building.</summary>
    public static IntPtr Message() => LiveMessage;

    public static void Send(byte major, byte minor, byte* payload, uint len)
    {
        IntPtr msg = Message();
        if (msg != IntPtr.Zero && EngineFn.SendPlayerToServerMessage != null) EngineFn.SendPlayerToServerMessage(msg, major, minor, payload, len);
    }

    /// <summary>
    /// Send an ASCII payload. Footgun: SendPlayerToServerMessage reserves the first 3 bytes for its
    /// own ['p'][major][minor] header and transmits the whole len, so we reserve 3 bytes up front and
    /// put the ASCII at offset 3 -- handing it the raw payload sends a minor-less runt the server drops.
    /// </summary>
    public static void SendAscii(byte major, byte minor, string payload)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(payload);
        byte[] buf = new byte[3 + ascii.Length];
        System.Array.Copy(ascii, 0, buf, 3, ascii.Length);
        fixed (byte* p = buf) Send(major, minor, p, (uint)buf.Length);
    }
}
