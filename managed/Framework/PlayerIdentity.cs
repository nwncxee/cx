using System;
using System.Text;
using CX.Engine;

namespace CX.Framework;

/// <summary>Who is playing where, for per-character persisted state such as the radial layout: the connected
/// server's <c>ip:port</c> and the local creature's first + last name off its stats block -- not the display name,
/// which a disguise (<c>SetName</c>) rewrites into the override field. Cold reads through the app object; the
/// address call heap-allocates a CExoString, so never per frame. Empty parts when not in a game.</summary>
public static unsafe class PlayerIdentity
{
    private const int MaxNameLen = 128;
    /// <summary>The most roster entries a walk trusts; a count past it is not a roster.</summary>
    internal const int MaxPlayers = 256;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    /// <summary>Present iff the client exports <see cref="SymbolMap.GetServerNetworkAddress"/>; the by-value call
    /// itself has no pointer to null-check, the generator hides the per-OS sret order behind it.</summary>
    private static readonly IntPtr _serverAddress = Native.Sym(SymbolMap.GetServerNetworkAddress);
    private const byte TypeCreature = 5;

    /// <summary>The live CClientExoApp, or zero before the app exists.</summary>
    public static IntPtr App()
    {
        IntPtr app = ClientApi.LiveExoApp;
        if (app != IntPtr.Zero) return app;
        if (_appMgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)_appMgr;
        return mgr != IntPtr.Zero ? *(IntPtr*)mgr : IntPtr.Zero;
    }

    /// <summary>The live app's internals, or a null view before the app exists.</summary>
    private static CClientExoAppInternal Internal()
    {
        IntPtr app = App();
        return app == IntPtr.Zero ? default : new CClientExoApp(app).Internal;
    }

    /// <summary>"a.b.c.d:port" of the connected server, "" when not connected (the main menu returns an empty
    /// string; through the master-server relay it is the relay's address).</summary>
    public static string ServerAddress()
    {
        CClientExoAppInternal intern = Internal();
        if (intern.IsNull || _serverAddress == IntPtr.Zero) return "";
        CNetLayer net = intern.NetLayer;
        if (net.IsNull) return "";
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;   // the dtor below frees whatever buffer lands here
        net.GetServerNetworkAddress(exo);
        string s = Text(*(byte**)exo, *(uint*)(exo + 8));
        Native.DestroyExoString((IntPtr)exo);
        return s;
    }

    /// <summary>The local player's roster entry (CNWCPlayer*): the one whose creature id is the local creature's.
    /// Zero when there is no local creature (not in a module). Chat's tell resolver and the radial key share it.</summary>
    public static IntPtr LocalPlayer()
    {
        CClientExoAppInternal intern = Internal();
        if (intern.IsNull) return IntPtr.Zero;
        uint localObj = intern.PlayerCreatureId;
        if (localObj == 0) return IntPtr.Zero;
        IntPtr listBase = intern.Players;
        int count = intern.PlayerCount;
        if (listBase == IntPtr.Zero || count <= 0 || count > MaxPlayers) return IntPtr.Zero;
        for (int i = 0; i < count; i++)
        {
            var player = new CNWCPlayer(*(IntPtr*)(listBase + i * 8));
            if (!player.IsNull && player.CreatureId == localObj) return player.Ptr;
        }
        return IntPtr.Zero;
    }

    /// <summary>The local <c>CNWCCreature*</c> when it exists and carries a stats block (what the engine's own
    /// menu ctors gate on), else zero.</summary>
    public static IntPtr Creature()
    {
        IntPtr app = App();
        if (app == IntPtr.Zero || EngineFn.GetGameObject == null) return IntPtr.Zero;
        var exoApp = new CClientExoApp(app);
        CClientExoAppInternal intern = exoApp.Internal;
        if (intern.IsNull) return IntPtr.Zero;
        uint localObj = intern.PlayerCreatureId;
        if (localObj == 0) return IntPtr.Zero;
        var obj = new CNWCObject(exoApp.GetGameObject(localObj).Ptr);
        if (obj.IsNull || obj.Type != TypeCreature) return IntPtr.Zero;
        return new CNWCCreature(obj.Ptr).Stats.IsNull ? IntPtr.Zero : obj.Ptr;
    }

    /// <summary>The local creature's "First Last" (either half alone when the other is empty), "" when there is
    /// no local creature. Survives a disguise: the override name lives in another field.</summary>
    public static string CharacterName()
    {
        IntPtr obj = Creature();
        if (obj == IntPtr.Zero) return "";
        CNWCCreatureStats stats = new CNWCCreature(obj).Stats;
        string first = Text((byte*)stats.FirstName, stats.FirstNameLen).Trim();
        string last = Text((byte*)stats.LastName, stats.LastNameLen).Trim();
        return first.Length == 0 ? last : last.Length == 0 ? first : first + " " + last;
    }

    /// <summary>Both halves as one key, <c>server|character</c>; the character half may be empty before the roster fills.</summary>
    public static string Key() => ServerAddress() + Sep + CharacterName();
    /// <summary>True when a <see cref="Key"/> carries a character (the roster had the local player).</summary>
    public static bool HasCharacter(string key) => !key.EndsWith(Sep, StringComparison.Ordinal);
    private const string Sep = "|";

    /// <summary>Engine bytes as chars one-to-one, as the roster and the plate read them.</summary>
    private static string Text(byte* buf, uint len)
        => buf == null || len == 0 || len > MaxNameLen ? "" : Encoding.Latin1.GetString(buf, (int)len);
}
