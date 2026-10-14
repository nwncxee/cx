using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The <c>"cx"</c> members of a server's NUI window JSON (<c>docs/ui-policy-contract.md</c>). A hook on the window's constructor
/// reads the JSON the server sent and hands it to <see cref="CxTranslate"/>; a hook on the window's per-frame <c>Update</c> reads
/// the server's bind updates for the bound settings and sends the server its refusals. The window's keys are released when the
/// window has not ticked for <see cref="StaleMs"/>.
/// </summary>
internal static unsafe class CxWindows
{
    /// <summary>How long a window may go without a tick before it counts as closed.</summary>
    internal const long StaleMs = 3000;
    /// <summary>Bytes of an engine <c>DynamicBinding</c>, on both builds.</summary>
    private const int BindingBytes = 392;
    private const int MaxWindows = 64, MaxOutbox = 16, EventsPerTick = 4, MaxBinds = 64;
    /// <summary>Deque member offsets: the compiler's container ABI, the same on every client build.</summary>
    private const int GccStart = 16, GccFinish = 48, GccNode = 24, GccLast = 16;
    private const int MsvcMap = 8, MsvcMapSize = 16, MsvcOff = 24, MsvcSize = 32;

    private sealed class BoundSetting
    {
        public string Name = "", Key = "";
        public byte[] NameUtf8 = [];
        public bool Registered;
    }

    private sealed class CxWindow
    {
        public string Id = "";
        public long Seen;
        public readonly List<string> Keys = new();
        public readonly List<BoundSetting> Binds = new();
        public readonly List<PolicyRefusal> Outbox = new();
        public IntPtr Json;
    }

    /// <summary>Everything the hooks share.</summary>
    private sealed class CxState
    {
        public readonly Dictionary<IntPtr, CxWindow> Windows = new();
        public readonly Dictionary<string, IntPtr> Owner = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PolicyRefusal> Late = new();
        public int LateSeq;
        public ExtenderLog? Log;
        public long SweepAt;
        public IntPtr CtorOrig, UpdateOrig;
        public bool Armed;
    }

    private static CxState _s = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        var s = _s;
        s.Log = log;
        if (!EngineLayout.Require(log, "CxWindows", "Nui::JSON::JsonWindow", "Nui::JSON::BindUpdate")
            || !SymbolMap.Require(log, "CxWindows", SymbolMap.NuiJsonWindowCtor, SymbolMap.NuiJsonWindowUpdate, SymbolMap.NuiEventPush, SymbolMap.NuiJsonWindowBind, SymbolMap.NuiDynamicBindingDtor))
        {
            log.Always("CxWindows: the NUI window symbols or layout rows did not resolve -- \"cx\" members in a window's JSON are ignored");
            return;
        }
        s.CtorOrig = (IntPtr)hooks.InstallNuiJsonWindowCtor(&OnCtor);
        s.UpdateOrig = (IntPtr)hooks.InstallNuiJsonWindowUpdate(&OnUpdate);
        s.Armed = s.CtorOrig != IntPtr.Zero && s.UpdateOrig != IntPtr.Zero;
        if (!s.Armed) log.Always("CxWindows: the window constructor or Update did not hook -- \"cx\" members are ignored");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnCtor(IntPtr self, int id, IntPtr name, IntPtr json)
    {
        var s = _s;
        IntPtr r = ((delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, IntPtr>)s.CtorOrig)(self, id, name, json);
        try { if (NuiKnobs.Enabled.V && NuiKnobs.CxJson.V) Open(s, self); }
        catch (Exception e) { s.Log?.Always($"CxWindows: reading a window's cx members failed: {e.Message}"); }
        return r;
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(IntPtr self)
    {
        var s = _s;
        if (s.Windows.Count > 0)
        {
            try { Tick(s, self); }
            catch (Exception e) { s.Log?.Always($"CxWindows: a window tick failed: {e.Message}"); }
        }
        ((delegate* unmanaged<IntPtr, void>)s.UpdateOrig)(self);
    }

    /// <summary>Reads a just-built window's JSON and applies its cx members.</summary>
    private static void Open(CxState s, IntPtr self)
    {
        var win = new Nui__JSON__JsonWindow(self);
        string? json = NuiJsonRead.Dump((IntPtr)Unsafe.AsPointer(ref win.WindowJson));
        if (json == null || !json.Contains("\"cx\"", StringComparison.Ordinal)) return;
        string id = NuiJsonRead.ReadString((byte*)Unsafe.AsPointer(ref win.WindowName), OperatingSystem.IsWindows()) ?? "";
        if (id.Length == 0 || (s.Windows.Count >= MaxWindows && !s.Windows.ContainsKey(self))) return;
        Sweep(s, Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency, true);
        if (s.Windows.TryGetValue(self, out CxWindow? old)) Release(s, self, old);
        CxPlan plan = CxTranslate.Translate(id, json);
        var w = new CxWindow { Id = id, Seen = Environment.TickCount64, Json = (IntPtr)Unsafe.AsPointer(ref win.WindowJson) };
        s.Windows[self] = w;
        foreach (CxSet set in plan.Sets)
        {
            s.Owner[set.Key] = self;
            if (!w.Keys.Contains(set.Key, StringComparer.OrdinalIgnoreCase)) w.Keys.Add(set.Key);
            if (set.Bind != null) { if (w.Binds.Count < MaxBinds) w.Binds.Add(new BoundSetting { Name = set.Bind, NameUtf8 = Encoding.UTF8.GetBytes(set.Bind), Key = set.Key }); }
            else ServerPolicy.Set(set.Key, set.Value);
        }
        foreach (PolicyRefusal r in plan.Refused)
        {
            PolicyRefusals.Add(r.Kind, r.Key, r.Value, r.Reason);
            if (w.Outbox.Count < MaxOutbox) w.Outbox.Add(r);
        }
        s.LateSeq = PolicyRefusals.LastSeq;
        s.Log?.Info($"CxWindows: window '{id}' set {plan.Sets.Count} key(s), {plan.Refused.Count} refused, {w.Binds.Count} bound");
    }

    private static void Tick(CxState s, IntPtr self)
    {
        long now = Environment.TickCount64;
        Route(s);
        if (s.Windows.TryGetValue(self, out CxWindow? w))
        {
            w.Seen = now;
            if (w.Binds.Count > 0) { Register(self, w); Ingest(self, w); }
            for (int i = 0; i < EventsPerTick && w.Outbox.Count > 0; i++)
            {
                CxEvent.PushRefusal(self, w.Outbox[0]);
                w.Outbox.RemoveAt(0);
            }
        }
        Sweep(s, now, false);
    }

    /// <summary>Hands each refusal made since the last tick to the outbox of the window that owns its key, once per key and reason.</summary>
    private static void Route(CxState s)
    {
        int seq = PolicyRefusals.LastSeq;
        if (seq == s.LateSeq) return;
        if (seq < s.LateSeq) s.LateSeq = 0;
        s.Late.Clear();
        PolicyRefusals.Since(s.LateSeq, s.Late);
        s.LateSeq = seq;
        foreach (PolicyRefusal r in s.Late)
        {
            if (!s.Owner.TryGetValue(r.Key, out IntPtr owner) || !s.Windows.TryGetValue(owner, out CxWindow? ow) || ow.Outbox.Count >= MaxOutbox) continue;
            bool dup = false;
            foreach (PolicyRefusal o in ow.Outbox) dup |= o.Key == r.Key && o.Reason == r.Reason;
            if (!dup) ow.Outbox.Add(r);
        }
    }

    /// <summary>Tells the engine each bound setting's bind name, once, so an update for a name no element uses is kept and not
    /// discarded. <c>Bind</c> finds or creates the window's binding for a leaf, and the copy it returns is ended at once.</summary>
    private static void Register(IntPtr self, CxWindow w)
    {
        foreach (BoundSetting b in w.Binds)
        {
            if (b.Registered) continue;
            b.Registered = true;
            IntPtr leaf = NuiJsonRead.FindBind(w.Json, b.Name);
            if (leaf == IntPtr.Zero || EngineFn.NuiJsonWindowBind == null || EngineFn.NuiDynamicBindingDtor == null) continue;
            void* copy = NativeMemory.AllocZeroed(BindingBytes);
            try
            {
                EngineFn.NuiJsonWindowBind(copy, self, leaf);
                EngineFn.NuiDynamicBindingDtor((IntPtr)copy);
            }
            finally { NativeMemory.Free(copy); }
        }
    }

    /// <summary>Releases the keys of windows that stopped ticking. Every second, or at once when <paramref name="force"/>.</summary>
    private static void Sweep(CxState s, long now, bool force)
    {
        if (!force && now < s.SweepAt) return;
        s.SweepAt = now + 1000;
        List<IntPtr>? gone = null;
        foreach (var kv in s.Windows)
            if (now - kv.Value.Seen > StaleMs) (gone ??= new()).Add(kv.Key);
        if (gone == null) return;
        foreach (IntPtr p in gone) Release(s, p, s.Windows[p]);
    }

    /// <summary>Forgets a window and releases the keys it still owns: a key a newer window has taken stays.</summary>
    private static void Release(CxState s, IntPtr p, CxWindow w)
    {
        s.Windows.Remove(p);
        foreach (string key in w.Keys)
            if (s.Owner.TryGetValue(key, out IntPtr o) && o == p) { s.Owner.Remove(key); ServerPolicy.Set(key, ""); }
        s.Log?.Info($"CxWindows: window '{w.Id}' closed, {w.Keys.Count} key(s) released");
    }

    /// <summary>Reads the bind updates waiting in the window's queue before the engine drains it. The queue is a libstdc++ deque of
    /// 64-byte records on Linux, an MSVC deque of pointers to them on Windows; the window's own bind map is not touched.</summary>
    private static void Ingest(IntPtr self, CxWindow w)
    {
        byte* q = (byte*)Unsafe.AsPointer(ref new Nui__JSON__JsonWindow(self).BindIngest);
        if (OperatingSystem.IsWindows())
        {
            ulong size = *(ulong*)(q + MsvcSize), off = *(ulong*)(q + MsvcOff), mask = *(ulong*)(q + MsvcMapSize) - 1;
            byte** map = *(byte***)(q + MsvcMap);
            if (size == 0 || map == null || size > 4096) return;
            for (ulong i = 0; i < size; i++) Record(w, map[(off + i) & mask]);
            return;
        }
        byte* p = *(byte**)(q + GccStart), end = *(byte**)(q + GccFinish);
        if (p == end) return;
        byte* last = *(byte**)(q + GccStart + GccLast);
        byte** node = *(byte***)(q + GccStart + GccNode);
        for (int n = 0; p != end && n < 4096; n++)
        {
            Record(w, p);
            p += EngineLayout.NuiBindUpdateStride;
            if (p == last) { node++; p = *node; last = p + EngineLayout.NuiBindUpdateStride * 8; }
        }
    }

    /// <summary>Applies one queued update to the bound settings it names. A name is compared as bytes, so a record for no bound
    /// setting allocates nothing.</summary>
    private static void Record(CxWindow w, byte* rec)
    {
        if (rec == null) return;
        var update = new Nui__JSON__BindUpdate((IntPtr)rec);
        byte* name = (byte*)Unsafe.AsPointer(ref update.Name);
        foreach (BoundSetting b in w.Binds)
        {
            if (!NuiJsonRead.StringIs(name, OperatingSystem.IsWindows(), b.NameUtf8)) continue;
            string? text = NuiJsonRead.Dump((IntPtr)Unsafe.AsPointer(ref update.Json));
            if (text == null) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                ServerPolicy.Set(b.Key, CxTranslate.BoundText(doc.RootElement));
            }
            catch (JsonException) { }
        }
    }
}
