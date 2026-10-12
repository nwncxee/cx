using System;
using System.Globalization;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Services.Nui;

/// <summary>
/// <c>"@model:&lt;key&gt;"</c> as any NUI image shows a live model: a throwaway copy of a creature the server names, drawn every
/// frame into the texture the engine made for the image, so NUI draws it like any other. What the view shows is the
/// server's, by policy keys under <c>model.&lt;key&gt;.</c> (<c>docs/ui-policy-contract.md</c>); until the doll has been built
/// and drawn once, and again once it is gone, the image holds the server's stand-in, or nothing.
/// </summary>
internal static unsafe class NuiModelView
{
    private const string Prefix = "@model:";
    private const string KeyRoot = "model.";
    private const int DefaultSize = 256, MaxSize = 1024, MaxKey = 48;
    private const long ProbeAfterMs = 20_000;

    /// <summary>The one view the doll serves: the image's pointers (read, never owned) and what the server last declared.</summary>
    private sealed class ViewData
    {
        public string Key = "";
        public IntPtr Nk, Ctrl;
        public int W = DefaultSize, H = DefaultSize;
        public uint[]? Standin;
        public string StandinName = "";
        public int Epoch = -1;
        public uint Target;
        public float Angle = 30f, Dist = 4f, Height = 0.9f, Yaw;
        public bool Anim = true, Built, WasRendered;
        public byte[] Info = new byte[0];
    }

    private sealed class ViewState
    {
        public readonly long Born = Environment.TickCount64;
        public bool Ready;
        public string Applied = "";
        public long NextLog;
        public ExtenderLog? Log;
        public ViewData? View;
        public string PendingKey = "";
        public uint[]? PendingPixels;
        public int PendingW, PendingH;
        public string PendingStandin = "";
    }

    private static readonly ViewState _s = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        _s.Ready = NuiModelDoll.Init(log);
        if (!_s.Ready) { log.Always("NuiModelView: the engine pieces did not resolve -- no model views"); return; }
        Capabilities.Register("modelview", 1, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.ModelView.V);
        NuiKnobs.Enabled.Changed += Capabilities.Refresh;
        NuiKnobs.ModelView.Changed += Capabilities.Refresh;
        NuiModelEngine.Install(log, hooks);
        FrameRegistry.Global.Register(FramePhase.PrePresent, 1, Frame, "NuiModelView.frame", log);
    }

    /// <summary>Is this nk_loadimg name a model view's?</summary>
    internal static bool IsModelName(byte* s)
    {
        for (int i = 0; i < Prefix.Length; i++) if (s[i] != (byte)Prefix[i]) return false;
        return s[Prefix.Length] != 0;
    }

    private static string KeyOf(byte* s)
    {
        var sb = new StringBuilder();
        for (byte* p = s + Prefix.Length; *p != 0 && sb.Length < MaxKey; p++) sb.Append((char)*p);
        return sb.ToString();
    }

    private static string Policy(string key, string what, string def) => ServerPolicy.Str($"{KeyRoot}{key}.{what}", def);

    private static float PolicyF(string key, string what, float def)
        => float.TryParse(Policy(key, what, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

    private static (int, int) SizeOf(string key)
    {
        string v = Policy(key, "size", "");
        int x = v.IndexOf('x');
        if (x > 0 && int.TryParse(v.AsSpan(0, x), out int w) && int.TryParse(v.AsSpan(x + 1), out int h)
            && w is > 0 and <= MaxSize && h is > 0 and <= MaxSize) return (w, h);
        return (DefaultSize, DefaultSize);
    }

    /// <summary>nk_loadimg asks for the image: make the TGA it will load, the stand-in at its own size or a transparent
    /// one. A declaration that lands later is applied to the live texture by <see cref="Frame"/>.</summary>
    internal static bool Prepare(byte* resref)
    {
        if (!_s.Ready || !NuiKnobs.Enabled.V || !NuiKnobs.ModelView.V) return false;
        string key = KeyOf(resref);
        var (w, h) = SizeOf(key);
        string standin = Policy(key, "standin", "");
        Span<byte> name = stackalloc byte[17];
        name.Clear();
        for (int i = 0; i < standin.Length && i < 16; i++) name[i] = (byte)standin[i];
        NuiItemIcons.ModelImage(name, w, h, out uint[] px, out int pw, out int ph);
        _s.PendingKey = key; _s.PendingPixels = px; _s.PendingW = pw; _s.PendingH = ph; _s.PendingStandin = standin;
        return true;
    }

    /// <summary>nk_loadimg returned <paramref name="ret"/>, a <c>shared_ptr&lt;NkImage&gt;</c> (pointer, control block).</summary>
    internal static void Loaded(byte* resref, IntPtr ret)
    {
        if (KeyOf(resref) != _s.PendingKey) return;
        ViewData? old = _s.View;
        var v = new ViewData
        {
            Key = _s.PendingKey, Nk = *(IntPtr*)ret, Ctrl = *(IntPtr*)(ret + IntPtr.Size),
            W = _s.PendingW, H = _s.PendingH, Standin = _s.PendingPixels, StandinName = _s.PendingStandin,
        };
        if (old != null && old.Key == v.Key && old.Nk == v.Nk) { old.Ctrl = v.Ctrl; return; }
        if (old != null && NuiModelDoll.Doll.Creature != IntPtr.Zero) NuiModelDoll.Destroy();
        _s.View = v;
    }

    /// <summary>The image is alive while its shared_ptr control block counts an owner (use count at +8 on both ABIs); the block
    /// outlives the image, because nk_loadimg's name map keeps a weak reference to it.</summary>
    private static bool Alive(ViewData v) => v.Ctrl != IntPtr.Zero && *(int*)(v.Ctrl + 8) > 0;   // not-an-offset: shared_ptr control block use count

    private static void Frame()
    {
        ViewState s = _s;
        string probe = NuiKnobs.ModelProbe.V;
        if (probe.Length > 0 && probe != s.Applied && GameGate.InGame && Environment.TickCount64 - s.Born > ProbeAfterMs) Probe(s, probe);
        if (s.View != null) Serve(s, s.View);
        NuiModelDoll.KeepShown();
        if (NuiModelDoll.Doll.Enabled && Environment.TickCount64 > s.NextLog)
        {
            s.NextLog = Environment.TickCount64 + 5000;
            s.Log?.Always("NuiModelView: " + NuiModelDoll.CameraLine() + " | " + NuiModelDoll.FadeLine());
        }
    }

    private static void Serve(ViewState s, ViewData v)
    {
        if (!Alive(v))
        {
            if (v.Built) { NuiModelDoll.Destroy(); v.Built = false; s.Log?.Always($"NuiModelView: '{v.Key}' closed -- doll taken down"); }
            s.View = null;
            return;
        }
        uint tex = new NkImage(v.Nk).GlTexture;
        if (tex == 0) return;
        int epoch = ServerPolicy.Epoch;
        if (epoch != v.Epoch) { v.Epoch = epoch; Read(s, v); }
        IntPtr who = Resolve(v.Key);
        if (who == IntPtr.Zero)
        {
            if (v.Built) { Teardown(s, v, tex); }
            return;
        }
        if (!v.Built)
        {
            if (!NuiModelDoll.Build(who, 0, 0, v.W, v.H, v.Anim)) return;
            v.Built = true;
            v.Info = NuiModelDoll.InfoOf(who).ToArray();
            NuiModelDoll.SetTarget(tex, v.W, v.H);
            ApplyCamera(v);
            s.Log?.Always($"NuiModelView: '{v.Key}' -- doll drawing into texture {tex} ({v.W}x{v.H})");
        }
        else if (v.Target != tex) NuiModelDoll.SetTarget(tex, v.W, v.H);
        else
        {
            ReadOnlySpan<byte> now = NuiModelDoll.InfoOf(who);
            if (!now.IsEmpty && v.Info.Length == now.Length && !now.SequenceEqual(v.Info))
            {
                if (NuiModelDoll.Refresh(who)) s.Log?.Always($"NuiModelView: '{v.Key}' -- the creature's appearance changed, the doll follows");
                v.Info = now.ToArray();
            }
        }
        v.Target = tex;
        if (NuiModelDoll.Rendered) v.WasRendered = true;
    }

    /// <summary>The creature the server named, or zero while it names none or the client does not know it yet.</summary>
    private static IntPtr Resolve(string key)
    {
        string t = Policy(key, "target", "");
        if (t.Length == 0) return IntPtr.Zero;
        if (t == "player") return NuiModelDoll.Player();
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)
            ? NuiModelDoll.Creature(id | NuiItemIcons.ServerIdBit) : IntPtr.Zero;
    }

    private static void Read(ViewState s, ViewData v)
    {
        v.Angle = PolicyF(v.Key, "angle", 30f);
        v.Dist = PolicyF(v.Key, "dist", 4f);
        v.Height = PolicyF(v.Key, "height", 0.9f);
        v.Yaw = PolicyF(v.Key, "yaw", 0f);
        v.Anim = PolicyF(v.Key, "anim", 1f) != 0f;
        string[] bg = Policy(v.Key, "bg", "").Split(',');
        float C(int i) => i < bg.Length && float.TryParse(bg[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        NuiModelDoll.SetBackground(C(0), C(1), C(2), C(3));
        if (v.Built) ApplyCamera(v);
        string standin = Policy(v.Key, "standin", "");
        if (standin != v.StandinName)
        {
            v.StandinName = standin;
            if (!NuiModelDoll.Rendered && v.Target != 0) UploadStandin(s, v);
        }
    }

    private static void ApplyCamera(ViewData v)
    {
        IntPtr c = NuiModelDoll.Doll.Camera;
        if (c == IntPtr.Zero) return;
        var cam = new CAurCamera(c);
        cam.SetViewAngle(v.Angle);
        cam.SetClipDist(0.1f, 2000f);
        NuiModelDoll.Orbit(v.Dist, v.Height, v.Yaw);
    }

    /// <summary>The doll is gone (the server named no one, or the creature left): put the stand-in back in the texture.</summary>
    private static void Teardown(ViewState s, ViewData v, uint tex)
    {
        NuiModelDoll.Destroy();
        v.Built = false;
        v.Target = tex;
        s.Log?.Always($"NuiModelView: '{v.Key}' -- doll taken down, stand-in restored");
        UploadStandin(s, v);
    }

    /// <summary>The stand-in's pixels into the view's texture, in the engine's context with its texture binding put back as it was.</summary>
    private static void UploadStandin(ViewState s, ViewData v)
    {
        uint[]? px = v.Standin;
        if (v.StandinName.Length > 0)
        {
            Span<byte> name = stackalloc byte[17];
            name.Clear();
            for (int i = 0; i < v.StandinName.Length && i < 16; i++) name[i] = (byte)v.StandinName[i];
            NuiItemIcons.ModelImage(name, v.W, v.H, out uint[] fresh, out int fw, out int fh);
            if (fw != v.W || fh != v.H) { s.Log?.Always($"NuiModelView: stand-in '{v.StandinName}' is {fw}x{fh}, the view {v.W}x{v.H} -- not applied"); return; }
            px = v.Standin = fresh;
        }
        else
        {
            px = v.Standin = new uint[v.W * v.H];
        }
        if (px == null || v.Target == 0 || Gl.TexSubImage2D == null) return;
        int prev;
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &prev);
        Gl.BindTexture(Gl.TEXTURE_2D, v.Target);
        fixed (uint* p = px) Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, v.W, v.H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)prev);
    }

    /// <summary><c>nui.modelprobe=x,y,w,h[,angle,dist,height,yaw,ai]</c>: a doll of you straight on the screen, for trying
    /// placements with no window. Debug only.</summary>
    private static void Probe(ViewState s, string spec)
    {
        s.Applied = spec;
        string[] t = spec.Split(',');
        float F(int i, float d) => i < t.Length && float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : d;
        IntPtr me = NuiModelDoll.Player();
        if (me == IntPtr.Zero) { s.Log?.Always("NuiModelView: probe -- no player creature"); return; }
        if (NuiModelDoll.Doll.Creature == IntPtr.Zero
            && !NuiModelDoll.Build(me, (int)F(0, 700), (int)F(1, 100), (int)F(2, 300), (int)F(3, 400), F(8, 1) != 0)) return;
        if (t.Length > 4) { var cam = new CAurCamera(NuiModelDoll.Doll.Camera); cam.SetViewAngle(F(4, 30f)); cam.SetClipDist(0.1f, 2000f); }
        if (t.Length > 5) NuiModelDoll.Orbit(F(5, 4f), F(6, 0.9f), F(7, 0));
    }
}
