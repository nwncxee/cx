using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Chat;

/// <summary>Desktop notifications: an <c>org.freedesktop.Notifications</c> call on the session D-Bus on Linux, a
/// notification-area balloon on Windows (a toast since Windows 10). Every blocking call runs on one worker thread,
/// started by the first <see cref="Show"/>; the game's main thread only queues.</summary>
internal static unsafe class DesktopNotify
{
    private const string AppName = "Neverwinter Nights";

    /// <summary>The worker's queue and the OS handles it opens on first use.</summary>
    private sealed class NotifyState
    {
        public readonly ConcurrentQueue<(string Title, string Body)> Queue = new();
        public readonly AutoResetEvent Wake = new(false);
        public Thread? Worker;
        public ExtenderLog? Log;
        /// <summary>Why delivery stood down, set once by the worker; null while it works.</summary>
        public volatile string? Failed;
        public bool Reported;
        public IntPtr Bus;
        public IntPtr Window, Icon;
        public bool TrayUp;
    }
    private static readonly NotifyState _s = new();

    /// <summary>Is the game window in the background? False when SDL cannot say, so nothing notifies blind.</summary>
    internal static bool Background
    {
        get { SdlGetKeyboardFocusFn focus = EngineFn.SdlGetKeyboardFocus; return focus != null && focus() == IntPtr.Zero; }
    }

    /// <summary>Queue a notification; never blocks. Once the worker has stood down, the next call says why in chat
    /// and every call drops its notification.</summary>
    internal static void Show(string title, string body, ExtenderLog? log)
    {
        if (_s.Failed is string why)
        {
            if (!_s.Reported) { _s.Reported = true; Notice.Show(log, $"Desktop notifications are unavailable: {why}"); }
            return;
        }
        log?.Info($"[notify] queued \"{title}\"");
        _s.Queue.Enqueue((title.Length > 0 ? title : AppName, body));
        if (_s.Worker == null)
        {
            _s.Log = log;
            _s.Worker = new Thread(Run) { IsBackground = true, Name = "cx-notify" };
            _s.Worker.Start();
        }
        _s.Wake.Set();
    }

    private static void Run()
    {
        Affinity.ClearThis();
        try
        {
            while (_s.Failed == null)
            {
                _s.Wake.WaitOne(_s.TrayUp ? TrayPollMs : Timeout.Infinite);
                while (_s.Failed == null && _s.Queue.TryDequeue(out var n))
                    _s.Failed = OperatingSystem.IsWindows() ? Balloon(n.Title, n.Body) : DBusNotify(n.Title, n.Body);
                if (_s.TrayUp && !Background) RemoveBalloon();
            }
        }
        catch (Exception e) { _s.Failed = e.Message; }   // an escaped exception would abort the game
        _s.Log?.Always($"[notify] desktop notifications stood down: {_s.Failed}");
    }

    // ---- Linux: libdbus ----

    private const string DBusLib = "libdbus-1.so.3";
    private const int DBusSession = 0, TypeString = 's', TypeUint32 = 'u', TypeInt32 = 'i', TypeArray = 'a';

    /// <summary>libdbus's iterator: a record the caller allocates, laid out as <c>dbus-message.h</c> declares it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DBusMessageIter
    {
        public IntPtr Dummy1, Dummy2;
        public uint Dummy3;
        public int Dummy4, Dummy5, Dummy6, Dummy7, Dummy8, Dummy9, Dummy10, Dummy11, Pad1;
        public IntPtr Pad2, Pad3;
    }

    /// <summary>One <c>Notify</c> call on a private session-bus connection, opened on first use. No reply is asked
    /// for, so the send is a socket write. Null on success, else why it failed.</summary>
    private static string? DBusNotify(string title, string body)
    {
        if (_s.Bus == IntPtr.Zero)
        {
            dbus_threads_init_default();
            _s.Bus = dbus_bus_get_private(DBusSession, IntPtr.Zero);
            if (_s.Bus == IntPtr.Zero) return "no D-Bus session bus";
            dbus_connection_set_exit_on_disconnect(_s.Bus, 0);   // libdbus would otherwise _exit() the game when the bus goes
        }
        IntPtr msg;
        fixed (byte* dest = "org.freedesktop.Notifications"u8, path = "/org/freedesktop/Notifications"u8, method = "Notify"u8)
            msg = dbus_message_new_method_call(dest, path, dest, method);
        if (msg == IntPtr.Zero) return "D-Bus would not build the Notify call";
        bool ok;
        fixed (byte* app = Utf8(AppName), summary = Utf8(title), text = Utf8(Markup(body)),
                     none = "\0"u8, strings = "s"u8, hints = "{sv}"u8)
        {
            DBusMessageIter args;
            uint replaces = 0;
            int expire = -1;
            dbus_message_iter_init_append(msg, &args);
            ok = Str(&args, app) && dbus_message_iter_append_basic(&args, TypeUint32, &replaces) != 0
                 && Str(&args, none) && Str(&args, summary) && Str(&args, text)
                 && EmptyArray(&args, strings) && EmptyArray(&args, hints)
                 && dbus_message_iter_append_basic(&args, TypeInt32, &expire) != 0;
        }
        if (ok)
        {
            dbus_message_set_no_reply(msg, 1);
            ok = dbus_connection_send(_s.Bus, msg, IntPtr.Zero) != 0;
            dbus_connection_flush(_s.Bus);
        }
        dbus_message_unref(msg);
        return ok ? null : "D-Bus refused the Notify call";
    }

    /// <summary>Append a NUL-terminated UTF-8 string. libdbus ABORTS the process on a null or invalid one, so every
    /// argument comes from <see cref="Utf8"/> or a <c>u8</c> literal that is never empty (an empty one pins as null).</summary>
    private static bool Str(DBusMessageIter* it, byte* s) => dbus_message_iter_append_basic(it, TypeString, &s) != 0;

    private static bool EmptyArray(DBusMessageIter* it, byte* signature)
    {
        DBusMessageIter sub;
        return dbus_message_iter_open_container(it, TypeArray, signature, &sub) != 0
               && dbus_message_iter_close_container(it, &sub) != 0;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Notification bodies are markup on most servers, so the line's own &amp; &lt; &gt; are escaped.</summary>
    private static string Markup(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [DllImport(DBusLib)] private static extern uint dbus_threads_init_default();
    [DllImport(DBusLib)] private static extern IntPtr dbus_bus_get_private(int type, IntPtr error);
    [DllImport(DBusLib)] private static extern void dbus_connection_set_exit_on_disconnect(IntPtr connection, uint exit);
    [DllImport(DBusLib)] private static extern IntPtr dbus_message_new_method_call(byte* destination, byte* path, byte* iface, byte* method);
    [DllImport(DBusLib)] private static extern void dbus_message_set_no_reply(IntPtr message, uint noReply);
    [DllImport(DBusLib)] private static extern void dbus_message_iter_init_append(IntPtr message, DBusMessageIter* iter);
    [DllImport(DBusLib)] private static extern uint dbus_message_iter_append_basic(DBusMessageIter* iter, int type, void* value);
    [DllImport(DBusLib)] private static extern uint dbus_message_iter_open_container(DBusMessageIter* iter, int type, byte* signature, DBusMessageIter* sub);
    [DllImport(DBusLib)] private static extern uint dbus_message_iter_close_container(DBusMessageIter* iter, DBusMessageIter* sub);
    [DllImport(DBusLib)] private static extern uint dbus_connection_send(IntPtr connection, IntPtr message, IntPtr serial);
    [DllImport(DBusLib)] private static extern void dbus_connection_flush(IntPtr connection);
    [DllImport(DBusLib)] private static extern void dbus_message_unref(IntPtr message);

    // ---- Windows: a notification-area balloon ----

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NiifRespectQuietTime = 0x80;  // not-an-offset: shellapi flags
    private const nint HwndMessage = -3, IdiApplication = 32512;
    /// <summary>How often the worker looks for the game regaining focus while the icon is up.</summary>
    private const int TrayPollMs = 500;

    /// <summary><c>NOTIFYICONDATAW</c> as <c>shellapi.h</c> declares it for Vista and later.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        public TipText Tip;
        public uint State, StateMask;
        public InfoText Info;
        public uint TimeoutOrVersion;
        public InfoTitleText InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }
    [InlineArray(128)] private struct TipText { private char _c; }
    [InlineArray(256)] private struct InfoText { private char _c; }
    [InlineArray(64)] private struct InfoTitleText { private char _c; }

    /// <summary>Show the balloon on our notification-area icon, adding the icon when it is not up (first use, or
    /// Explorer restarted). The icon belongs to a message-only window. Null on success, else why it failed.</summary>
    private static string? Balloon(string title, string body)
    {
        if (_s.Window == IntPtr.Zero)
        {
            fixed (char* cls = "STATIC") _s.Window = CreateWindowExW(0, cls, null, 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
            if (_s.Window == IntPtr.Zero) return "no window to own the notification icon";
            fixed (char* exe = Environment.ProcessPath) _s.Icon = ExtractIconW(GetModuleHandleW(null), exe, 0);
            if (_s.Icon <= 1) _s.Icon = LoadIconW(0, IdiApplication);
        }
        NotifyIconData d = Tray();
        d.Flags = NifIcon | NifTip | NifInfo;
        d.Icon = _s.Icon;
        d.InfoFlags = NiifRespectQuietTime;
        Copy(AppName, d.Tip);
        Copy(title, d.InfoTitle);
        Copy(body, d.Info);
        if (Shell_NotifyIconW(NimModify, &d) == 0 && Shell_NotifyIconW(NimAdd, &d) == 0)
            return "Shell_NotifyIcon refused the notification";
        _s.TrayUp = true;
        return null;
    }

    /// <summary>Take the icon, and any balloon it still shows, down once the game has focus again.</summary>
    private static void RemoveBalloon()
    {
        NotifyIconData d = Tray();
        _ = Shell_NotifyIconW(NimDelete, &d);
        _s.TrayUp = false;
    }

    private static NotifyIconData Tray() => new() { Size = (uint)sizeof(NotifyIconData), Window = _s.Window, Id = 1 };

    private static void Copy(string s, Span<char> dst) => s.AsSpan(0, Math.Min(s.Length, dst.Length - 1)).CopyTo(dst);

    [DllImport("user32")] private static extern IntPtr CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("kernel32")] private static extern IntPtr GetModuleHandleW(char* name);
    [DllImport("shell32")] private static extern IntPtr ExtractIconW(IntPtr instance, char* file, uint index);
    [DllImport("shell32")] private static extern int Shell_NotifyIconW(uint message, NotifyIconData* data);
}
